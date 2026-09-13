using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BepInEx;
using BepInEx.Logging;

using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

[BepInPlugin(
    "com.sophistcraft.DashAttackMacro",
    "DashAttackMacro",
    "1.09.11"
)]
public partial class DashAttackMacro : BaseUnityPlugin
{
    private static ManualLogSource logger;
    private void Awake()
    {
        logger = Logger;
        logger.LogInfo("DashAttackMacro loaded.");

        InputSystem.onEvent += OnInputSystemEvent;
    }
    private void OnDestroy()
    {
        InputSystem.onEvent -= OnInputSystemEvent;
    }



    // ============================================================
    // 매크로 상태 변수
    // ============================================================

    public enum MacroMode
    {
        None,      // OFF
        OneHit,    // 'Y'키: 단발 모드 (공격 1회 후 즉시 Release)
        Hitting    // 'U'키: 유지 모드 (손을 뗄 때까지 공격 누름 유지)
    }
    private MacroMode currentMode = MacroMode.None;

    private Coroutine macroCoroutine;
    private bool isMacroRunning = false;
    private int numChattered = 0;

    // 매크로 중 가상 마우스 이벤트 FIFO 큐 (true: Down, false: Up)
    private readonly Queue<bool> injectedMouseQueue = new Queue<bool>();

    private double physicalReleaseStart = 0; // 마우스 Release 시 채터링 방지용



    // ============================================================
    // 타이밍 상수
    // ============================================================

    private const float HOLD_TIME = 0.022f; // 누름 유지 ms
    private const double RELEASE_NOISE_FILTER_SEC = 0.022d;  // 릴리즈 노이즈 필터링
    //      const double RELEASE_NOISE_FILTER_SEC = 0.044d;  // 릴리즈 노이즈 필터링






    // ============================================================
    // Input System 이벤트 처리
    // ============================================================

    private void OnInputSystemEvent(InputEventPtr eventPtr, InputDevice device)
    {
        if (currentMode == MacroMode.None) return; // 매크로 OFF 상태에서는 스킵!

        if (IsAnyUIOpen()) return; // 인벤토리, 아티펙트선택, 인챈트 등 아무 UI 열려있으면 스킵!



        // 디바이스, 이벤트 유효성 체크
        if (device != Mouse.current) return;
        var mouse = (Mouse)device;
        if (!eventPtr.IsA<StateEvent>()) return;
        // (!eventPtr.IsA<StateEvent>() && !eventPtr.IsA<DeltaStateEvent>()) return;



        // 해당 디바이스의 '매크로 기동을 담당하는 키 이외의 이벤트만 있는 경우' 100% 스킵!
        bool leftButtonChanged = false;
        foreach (InputControl c in eventPtr.EnumerateChangedControls(device))
        {
            if (c == mouse.leftButton)
            {
                leftButtonChanged = true;
                break;
            }
        }
        if (!leftButtonChanged) return;



        // 매크로가 발생시킨 가상 이벤트라면 100% 스킵! → 정상적으로 클릭 발생
        bool isDown = mouse.leftButton.ReadValueFromEvent(eventPtr) > 0.5f;
        if (injectedMouseQueue.Count > 0 &&
            injectedMouseQueue.Peek() == isDown
            )
        {
            injectedMouseQueue.Dequeue();
            return;
        }



        // 가상 이벤트 아닐 경우에만 물리 마우스 상태 갱신
        if (isDown)
        {
            physicalReleaseStart = 0;
        }
        else
        {
            physicalReleaseStart = physicalReleaseStart == 0 ? Time.realtimeSinceStartupAsDouble : physicalReleaseStart;
        }



        WeaponInfo weaponInfo = GetWeaponInfo();
        {
            // 검방 가드중 좌클릭 시 특수공격 영향 시 강제 종료
            if (weaponInfo.IsGuardButtonDown == true)
            {
                /*logger.LogInfo(
                    $"[DashMacro] Kind={weaponInfo.Kind} / " +
                    $"IsGuarding={weaponInfo.IsGuardButtonDown?.ToString() ?? "N/A"} / " +
                    $"IsBladeSheathed={weaponInfo.IsBladeSheathed?.ToString() ?? "N/A"}"
                );*/
                return;
            }
            else
            // 도 납도상태 좌클릭 시 특수공격 상황 시 강제 종료
            if (weaponInfo.IsBladeSheathed == true)
            {
                /*logger.LogInfo(
                    $"[DashMacro] Kind={weaponInfo.Kind} / " +
                    $"IsGuarding={weaponInfo.IsGuardButtonDown?.ToString() ?? "N/A"} / " +
                    $"IsBladeSheathed={weaponInfo.IsBladeSheathed?.ToString() ?? "N/A"}"
                );*/
                return;
            }
        }



        // 시퀀스 시작 (isMacroRunning 중엔 중복 실행 방지)
        if (!isMacroRunning &&
            CanCharacterDash() // 대쉬 불가(남은 횟수 0) → 매크로 진입 방지
            )
        {
            if (macroCoroutine != null)
                StopCoroutine(macroCoroutine);

            injectedMouseQueue.Clear();
            physicalReleaseStart = 0;

            macroCoroutine = StartCoroutine(DashAttackSequence());
        }



        // 물리적 좌클릭 신호는 isMacroRunning 중엔 무조건 무시
        mouse.leftButton.WriteValueIntoEvent(0f, eventPtr);
    }






    // ============================================================
    // 매크로 시퀀스
    // ============================================================

    private IEnumerator DashAttackSequence()
    {
        isMacroRunning = true;
        numChattered = 0;

        try
        {
            // 평타 후딜레이 때문에 씹히는 상황, 딜레이 풀릴 때까지 1프레임씩 대기
            if (IsAttackingAnimationRequest())
            {
                double ifIsAttackingStart = Time.realtimeSinceStartupAsDouble;
                const double MAX_ATTACK_RECOVERY_WAIT = 0.333d; // 세이프티

                while (IsAttackingAnimationRequest())
                {
                    if (currentMode == MacroMode.None) // 대기 중 매크로 꺼짐 → 그대로 종료
                        yield break;

                    if (Time.realtimeSinceStartupAsDouble - ifIsAttackingStart > MAX_ATTACK_RECOVERY_WAIT)
                    {
                        /**/logger.LogInfo($"[DashMacro] IsAttackingAnimationRequest() 타임아웃");
                        break;
                    }

                    yield return null; // while: 프레임 마다 검사
                }

                /**/logger.LogInfo($"[DashMacro] 평타딜레이 대기 이후 발동됨 !!!!");
            }



            // 스페이스바(대쉬) press
            SetButtonState(Keyboard.current.spaceKey, true);
            yield return new WaitForSecondsRealtime(HOLD_TIME);

            // 스페이스바(대쉬) release
            SetButtonState(Keyboard.current.spaceKey, false); yield return null; // 씹힘 방지용 (최소 딜레이)

            // 좌클릭(공격) press
            SendMouseLeftClick(true);
            yield return new WaitForSecondsRealtime(HOLD_TIME);

            // 좌클릭(공격) release, 모드따라 갈림
            switch (currentMode)
            {
                // OneHit 모드: 공격 후 즉시 release 후 마우스를 뗄 때까지 정지
                case MacroMode.OneHit:
                {
                    SendMouseLeftClick(false); yield return null; // 씹힘 방지용 (최소 딜레이)

                    double caseOneHitStart = Time.realtimeSinceStartupAsDouble;
                    const double MAX_ONEHIT_SEQUENCE_DURATION = 1.333d; // 세이프티

                    double releaseConfirmStart = 0;
                    while (true)
                    {
                        if (currentMode != MacroMode.OneHit) break;

                        if (Time.realtimeSinceStartupAsDouble - caseOneHitStart > MAX_ONEHIT_SEQUENCE_DURATION)
                        {
                            /**/logger.LogInfo($"[DashMacro] case OneHit: 타임아웃");
                            break;
                        }

                        if (IsPhysicalLeftButtonDown())
                        {
                            releaseConfirmStart = 0;
                            numChattered++;
                        }
                        else
                        {
                            if (releaseConfirmStart == 0)
                                releaseConfirmStart = Time.realtimeSinceStartupAsDouble;
                            else
                            if (Time.realtimeSinceStartupAsDouble - releaseConfirmStart > RELEASE_NOISE_FILTER_SEC)
                                break; // RELEASE_NOISE_FILTER_SEC 동안 채터링을 포함한 press 발생하지 않음 → release 확정
                        }

                        yield return null; // while: 프레임 마다 검사
                    }

                    break;
                }

                // Hitting 모드: 손가락으로 마우스를 뗄 때까지 공격 누름 상태 유지
                case MacroMode.Hitting:
                {
                    if (physicalReleaseStart == 0 ||
                        Time.realtimeSinceStartupAsDouble - physicalReleaseStart < RELEASE_NOISE_FILTER_SEC
                        )
                    {
                        while (true)
                        {
                            if (currentMode != MacroMode.Hitting) break;

                            if (IsPhysicalLeftButtonDown())
                            {
                                physicalReleaseStart = 0;
                                numChattered++;
                            }
                            else
                            {
                                if (physicalReleaseStart == 0)
                                    physicalReleaseStart = Time.realtimeSinceStartupAsDouble;
                                else
                                if ((physicalReleaseStart > 0 ? (Time.realtimeSinceStartupAsDouble - physicalReleaseStart) : 0) > RELEASE_NOISE_FILTER_SEC)
                                    break; // RELEASE_NOISE_FILTER_SEC 동안 채터링을 포함한 press 발생하지 않음 → release 확정
                            }

                            yield return null; // while: 프레임 마다 검사
                        }
                    }

                    SendMouseLeftClick(false); yield return null; // 씹힘 방지용 (최소 딜레이)
                    break;
                }
            }
            /**/logger.LogInfo($"[DashMacro] MACRO ENDED mode={currentMode} numChattered={numChattered}, releaseSequencedTime={(physicalReleaseStart > 0 ? (Time.realtimeSinceStartupAsDouble - physicalReleaseStart) : 0) * 1000:F0}ms");
        }
        finally
        {
            numChattered = 0;
            isMacroRunning = false;
            macroCoroutine = null;
        }
    }

    private void SetButtonState(ButtonControl control, bool pressed)
    {
        if (control == null) return;

        float value = pressed ? 1f : 0f;

        using (StateEvent.From(control.device, out var eventPtr))
        {
            control.WriteValueIntoEvent(value, eventPtr);

            InputSystem.QueueEvent(eventPtr);
        }
    }

    private void SendMouseLeftClick(bool pressed)
    {
        if (Mouse.current == null) return;

        // 미리 큐에 등록, onEvent 내부에서 가상 이벤트는 스킵 가능하도록 → 정상적으로 클릭 발생
        injectedMouseQueue.Enqueue(pressed);

        using (StateEvent.From(Mouse.current, out var eventPtr))
        {
            // 좌클릭 값 강제 세팅
            Mouse.current.leftButton.WriteValueIntoEvent(pressed ? 1f : 0f, eventPtr);

            // 좌클릭 이외의 상태는 현재 물리상태 그대로 반영
            if (Mouse.current.rightButton.isPressed)
                Mouse.current.rightButton.WriteValueIntoEvent(1f, eventPtr);
            if (Mouse.current.middleButton.isPressed)
                Mouse.current.middleButton.WriteValueIntoEvent(1f, eventPtr);
            if (Mouse.current.backButton.isPressed)
                Mouse.current.backButton.WriteValueIntoEvent(1f, eventPtr);
            if (Mouse.current.forwardButton.isPressed)
                Mouse.current.forwardButton.WriteValueIntoEvent(1f, eventPtr);
            Mouse.current.position.WriteValueIntoEvent(Mouse.current.position.ReadValue(), eventPtr);

            InputSystem.QueueEvent(eventPtr);
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private const int VK_LBUTTON = 0x01;
    private static bool IsPhysicalLeftButtonDown()
    {
        // 최상위 비트가 1이면 현재 실제로 눌려있는 상태
        return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
    }






    // ============================================================
    // Update (모드 ON / OFF 제어)
    // ============================================================

    private static MacroMode MacroMode_BeforePause = MacroMode.None;
    private bool wasPausePanelOpenLastFrame = false;

    private Coroutine pausePanelCheckCoroutine;
    private IEnumerator CheckPausePanelOpenAfterDelay()
    {
        yield return new WaitForSecondsRealtime(0.333f);

        if (IsSpecificUIOpen("UI_PausePanel") && currentMode != MacroMode.None)
        {
            MacroMode_BeforePause = currentMode;
            TurnOffMacro();
        }

        pausePanelCheckCoroutine = null;
    }

    private void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return;

        // 'ESC'키 : PausePanel 열 때, 매크로 모드 켜져있었다면 해당 모드 OFF
        if (currentMode != MacroMode.None &&
            keyboard.escapeKey.wasPressedThisFrame
            )
        {
            if (pausePanelCheckCoroutine != null)
                StopCoroutine(pausePanelCheckCoroutine);

            pausePanelCheckCoroutine = StartCoroutine(CheckPausePanelOpenAfterDelay());
        }
        else

        // 'Y'키 / '-'키 : [OneHit 모드] ON/OFF 토글
        if (keyboard.yKey.wasPressedThisFrame ||
            keyboard.numpadMinusKey.wasPressedThisFrame
            )
        {
            if (currentMode == MacroMode.OneHit)
            {
                TurnOffMacro();
            }
            else
            {
                TurnOnMacro(MacroMode.OneHit);
            }
        }
        else

        // 'U'키 / '+'키 : [Hitting 모드] ON/OFF 토글
        if (keyboard.uKey.wasPressedThisFrame ||
            keyboard.numpadPlusKey.wasPressedThisFrame
            )
        {
            if (currentMode == MacroMode.Hitting)
            {
                TurnOffMacro();
            }
            else
            {
                TurnOnMacro(MacroMode.Hitting);
            }
        }



        // PausePanel이 닫히는 순간 Falling Edge 감지 → UI 전체가 0개면 이전 매크로 복원 시도
        bool isPausePanelOpenNow = IsSpecificUIOpen("UI_PausePanel");
        if (wasPausePanelOpenLastFrame &&
            !isPausePanelOpenNow
            )
        {
            if (!IsAnyUIOpen() &&
                MacroMode_BeforePause != MacroMode.None
                )
            {
                TurnOnMacro(MacroMode_BeforePause);
                MacroMode_BeforePause = MacroMode.None;
            }
        }
        wasPausePanelOpenLastFrame = isPausePanelOpenNow;
    }

    private void TurnOnMacro(MacroMode macroMode)
    {
        currentMode = macroMode;

        SendGameLog(
            "<color=#00FF00FF>좌클대쉬공격 ON [모드 : " +
            (   macroMode == MacroMode.OneHit ?  "단발" :
                macroMode == MacroMode.Hitting ? "유지" :
                "") +
            "]</color>"
        );
    }

    private void TurnOffMacro()
    {
        currentMode = MacroMode.None;
        if (isMacroRunning && macroCoroutine != null)
        {
            StopCoroutine(macroCoroutine);
            isMacroRunning = false;

            ReleaseAllInputs();
        }

        SendGameLog("<color=#FF0000FF>좌클대쉬공격 OFF" + (IsSpecificUIOpen("UI_PausePanel") ? " (일시정지)" : "") + "</color>");
    }

    private void ReleaseAllInputs()
    {
        injectedMouseQueue.Clear();

        if (Keyboard.current != null)
            SetButtonState(Keyboard.current.spaceKey, false);

        if (Mouse.current != null)
            SendMouseLeftClick(false);
    }
}