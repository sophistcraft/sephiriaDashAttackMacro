using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
public class DashAttackMacro : BaseUnityPlugin
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



        // 평타 후딜레이 때문에 씹히는 상황, 딜레이 풀릴 때까지 1프레임씩 대기
        if (IsAttackingAnimationRequest())
        {
            double ifIsAttackingStart = Time.realtimeSinceStartupAsDouble;
            const double MAX_ATTACK_RECOVERY_WAIT = 0.333d; // 세이프티

            while (IsAttackingAnimationRequest())
            {
                if (currentMode == MacroMode.None) // 대기 중 매크로 꺼짐 → 그대로 종료
                {
                    isMacroRunning = false;
                    macroCoroutine = null;
                    yield break;
                }

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



        isMacroRunning = false;
        macroCoroutine = null;
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


















    // ============================================================
    // 대쉬 상태 (잔여 횟수 / 가능 여부) 리플렉션
    // ============================================================

    /*
    private static Type _picType;
    private static PropertyInfo _picInstanceProp;
    private static FieldInfo _picAvatarField;      // PlayerInputController.avatar (private)
    private static FieldInfo _currentDashModuleField; // UnitAvatar.CurrentDashModule
    private static PropertyInfo _canDashProp;      // CharacterDash.CanDash
    private static bool _dashLookupFailed;

    private static Type _weaponSimpleType;
    private static Type _weaponControllerSimpleType;
    private static Type _playerAvatarType;
    private static FieldInfo _attackingAnimationField;    // PlayerAvatar.attackingAnimationRequest

    private static bool EnsureCanDashReflection()
    {
        if (_dashLookupFailed) return false;
        if (_canDashProp != null) return true;

        try
        {
            _picType = FindTypeInLoadedAssemblies("PlayerInputController");
            Type unitAvatarType = FindTypeInLoadedAssemblies("UnitAvatar");
            Type characterDashType = FindTypeInLoadedAssemblies("CharacterDash");

            if (_picType == null || unitAvatarType == null || characterDashType == null)
            {
                logger.LogWarning("[DashMacro] 대쉬 리플렉션 대상 타입을 찾지 못함");
                _dashLookupFailed = true;
                return false;
            }

            _picInstanceProp = _picType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            _picAvatarField = _picType.GetField("avatar", BindingFlags.NonPublic | BindingFlags.Instance);
            _currentDashModuleField = unitAvatarType.GetField("CurrentDashModule", BindingFlags.Public | BindingFlags.Instance);
            _canDashProp = characterDashType.GetProperty("CanDash", BindingFlags.Public | BindingFlags.Instance);

            _weaponControllerSimpleType = FindTypeInLoadedAssemblies("WeaponControllerSimple");
            _weaponSimpleType = FindTypeInLoadedAssemblies("WeaponSimple");
            _playerAvatarType = FindTypeInLoadedAssemblies("PlayerAvatar"); // avatar 필드의 실제 런타임 타입

            if (_picInstanceProp == null || _picAvatarField == null || _currentDashModuleField == null || _canDashProp == null)
            {
                logger.LogWarning("[DashMacro] 대쉬 리플렉션 멤버 조회 실패 (게임 버전 변경 가능성)");
                _dashLookupFailed = true;
                return false;
            }

            if (_weaponControllerSimpleType == null || _weaponSimpleType == null || _playerAvatarType == null)
            {
                logger.LogWarning("[DashMacro] 무기 리플렉션 대상 타입을 찾지 못함");
                _dashLookupFailed = true;
                return false;
            }

            _attackingAnimationField = _playerAvatarType.GetField("attackingAnimationRequest", BindingFlags.NonPublic | BindingFlags.Instance);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("[DashMacro] 대쉬 리플렉션 초기화 실패: " + ex);
            _dashLookupFailed = true;
            return false;
        }
    }

    private static bool IsAttackingAnimationRequest()
    {
        if (!EnsureCanDashReflection()) return false; // null(조회 실패) 시 false(=대쉬평타 가능 상태) 반환 → fail-open

        try
        {
            object pic = _picInstanceProp.GetValue(null);
            object avatar = pic != null ? _picAvatarField.GetValue(pic) : null;
            if (avatar == null) return false; // null(조회 실패) 시 false(=대쉬평타 가능 상태) 반환 → fail-open

            return (bool)_attackingAnimationField.GetValue(avatar);
        }
        catch (Exception ex)
        {
            logger.LogWarning("[DashMacro] attackingAnimationRequest 조회 실패: " + ex);
            return false; // null(조회 실패) 시 false(=대쉬평타 가능 상태) 반환 → fail-open
        }
    }
    */

    private static Type _picType;
    private static PropertyInfo _picInstanceProp;
    private static FieldInfo _picAvatarField;
    private static FieldInfo _currentDashModuleField;
    private static PropertyInfo _canDashProp;
    private static bool _dashLookupFailed;

    private static Type _playerAvatarType;
    private static FieldInfo _attackingAnimationField;
    private static bool _attackAnimLookupFailed;

    private static bool EnsureCanDashReflection()
    {
        if (_dashLookupFailed) return false;
        if (_canDashProp != null) return true;

        try
        {
            _picType = FindTypeInLoadedAssemblies("PlayerInputController");
            Type unitAvatarType = FindTypeInLoadedAssemblies("UnitAvatar");
            Type characterDashType = FindTypeInLoadedAssemblies("CharacterDash");

            if (_picType == null || unitAvatarType == null || characterDashType == null)
            {
                logger.LogWarning("[DashMacro] 대쉬 리플렉션 대상 타입을 찾지 못함");
                _dashLookupFailed = true;
                return false;
            }

            _picInstanceProp = _picType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            _picAvatarField = _picType.GetField("avatar", BindingFlags.NonPublic | BindingFlags.Instance);
            _currentDashModuleField = unitAvatarType.GetField("CurrentDashModule", BindingFlags.Public | BindingFlags.Instance);
            _canDashProp = characterDashType.GetProperty("CanDash", BindingFlags.Public | BindingFlags.Instance);

            if (_picInstanceProp == null || _picAvatarField == null || _currentDashModuleField == null || _canDashProp == null)
            {
                logger.LogWarning("[DashMacro] 대쉬 리플렉션 멤버 조회 실패 (게임 버전 변경 가능성)");
                _dashLookupFailed = true;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("[DashMacro] 대쉬 리플렉션 초기화 실패: " + ex);
            _dashLookupFailed = true;
            return false;
        }
    }

    private static object GetCurrentDashModule()
    {
        if (!EnsureCanDashReflection()) return null;
        try
        {
            object pic = _picInstanceProp.GetValue(null);
            object avatar = pic != null ? _picAvatarField.GetValue(pic) : null;
            return avatar != null ? _currentDashModuleField.GetValue(avatar) : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning("[DashMacro] CurrentDashModule 조회 실패: " + ex);
            return null;
        }
    }

    private static bool CanCharacterDash()
    {
        object dash = GetCurrentDashModule();
        if (dash == null) return true; // null(조회 실패) 시 true 반환 → 리플렉션이 깨져도 매크로 자체가 죽지 않게 fail-open

        return (bool)_canDashProp.GetValue(dash);
    }

    // PlayerInputController.avatar 접근은 대쉬 쪽에서 이미 확보한 것을 그대로 재사용 (같은 접근 경로라 굳이 별도로 다시 찾을 필요 없음)
    private static bool EnsureIsAttackingAnimationReflection()
    {
        if (_attackAnimLookupFailed) return false;
        if (_attackingAnimationField != null) return true; // ★ 캐시 조기 반환 추가

        try
        {
            _playerAvatarType = FindTypeInLoadedAssemblies("PlayerAvatar");

            if (_playerAvatarType == null)
            {
                logger.LogWarning("[DashMacro] PlayerAvatar 타입을 찾지 못함");
                _attackAnimLookupFailed = true;
                return false;
            }

            _attackingAnimationField = _playerAvatarType.GetField("attackingAnimationRequest", BindingFlags.NonPublic | BindingFlags.Instance);

            if (_attackingAnimationField == null) // ★ 누락됐던 null 체크 추가
            {
                logger.LogWarning("[DashMacro] attackingAnimationRequest 필드를 찾지 못함 (게임 버전 변경 가능성)");
                _attackAnimLookupFailed = true;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("[DashMacro] 공격딜레이 리플렉션 초기화 실패: " + ex);
            _attackAnimLookupFailed = true;
            return false;
        }
    }

    private static bool IsAttackingAnimationRequest()
    {
        // ★ avatar 접근은 대쉬 쪽 Ensure를 통해 확보 (실패해도 fail-open이라 대쉬 체크 자체엔 지장 없음)
        if (!EnsureCanDashReflection() || !EnsureIsAttackingAnimationReflection())
            return false; // null(조회 실패) 시 false(=대쉬평타 가능 상태) 반환 → fail-open

        try
        {
            object pic = _picInstanceProp.GetValue(null);
            object avatar = pic != null ? _picAvatarField.GetValue(pic) : null;
            if (avatar == null)
                return false; // null(조회 실패) 시 false(=대쉬평타 가능 상태) 반환 → fail-open

            return (bool)_attackingAnimationField.GetValue(avatar);
        }
        catch (Exception ex)
        {
            logger.LogWarning("[DashMacro] attackingAnimationRequest 조회 실패: " + ex);
            return false; // null(조회 실패) 시 false(=대쉬평타 가능 상태) 반환 → fail-open
        }
    }






    // ============================================================
    // UI 상태 (인벤토리 / 아티팩트 선택 등 활성 여부) 리플렉션
    // ============================================================

    private static Type _uiManagerType;
    private static PropertyInfo _uiManagerInstanceProp;
    private static PropertyInfo _currentControlStackProp;
    private static PropertyInfo _uiBaseIsOpenedProp;
    private static bool _uiLookupFailed;

    private static bool EnsureUIReflection()
    {
        if (_uiLookupFailed)
            return false;

        if (_currentControlStackProp != null && _uiBaseIsOpenedProp != null)
            return true;

        try
        {
            _uiManagerType = FindTypeInLoadedAssemblies("UIManager");
            Type uiBaseType = FindTypeInLoadedAssemblies("UIBase");

            if (_uiManagerType == null || uiBaseType == null)
            {
                logger.LogWarning("[DashMacro] UIManager/UIBase 타입을 찾지 못함");
                _uiLookupFailed = true;
                return false;
            }

            _uiManagerInstanceProp = _uiManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            _currentControlStackProp = _uiManagerType.GetProperty("CurrentControlStack", BindingFlags.Public | BindingFlags.Instance);
            _uiBaseIsOpenedProp = uiBaseType.GetProperty("IsOpened", BindingFlags.Public | BindingFlags.Instance);

            if (_uiManagerInstanceProp == null || _currentControlStackProp == null || _uiBaseIsOpenedProp == null)
            {
                logger.LogWarning("[DashMacro] UIManager 멤버 리플렉션 실패 (게임 버전 변경 가능성)");
                _uiLookupFailed = true;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("[DashMacro] UI 리플렉션 초기화 실패: " + ex);
            _uiLookupFailed = true;
            return false;
        }
    }

    // 현재 열려있는 UI 패널 목록을 IList로 가져옴 (없으면 null)
    private static System.Collections.IList GetCurrentUIStack()
    {
        if (!EnsureUIReflection())
            return null;

        try
        {
            object uiManagerInstance = _uiManagerInstanceProp.GetValue(null);
            if (uiManagerInstance == null)
                return null;

            object stackObj = _currentControlStackProp.GetValue(uiManagerInstance);
            return stackObj as System.Collections.IList;
        }
        catch (Exception ex)
        {
            logger.LogWarning("[DashMacro] UI 스택 조회 실패: " + ex);
            return null;
        }
    }

    private static int _lastUICount = -1; // -1: 아직 한 번도 체크 안 함

    // 현재 열려있는 UI 개수를 반환하면서, 변동 있을 때만 로그 출력
    private static int GetCurrentUICountAndLogIfChanged()
    {
        System.Collections.IList stack = GetCurrentUIStack();
        int currentCount = stack?.Count ?? 0;

        if (currentCount != _lastUICount)
        {
            logger.LogInfo($"[UI스택] cnt: {_lastUICount} -> cnt: {currentCount}");
            _lastUICount = currentCount;
        }

        return currentCount;
    }

    // 뭐라도 UI가 열려서 게임플레이 입력을 막아야 하는 상황인지
    private static bool IsAnyUIOpen()
    {
        return GetCurrentUICountAndLogIfChanged() > 0;
    }

    // 특정 UI 클래스가 열려있는지 (예: "UI_InventoryViewer", "UI_SephiriteRewardPanel")
    private static bool IsSpecificUIOpen(string uiClassName)
    {
        System.Collections.IList stack = GetCurrentUIStack();
        if (stack == null)
            return false;

        try
        {
            foreach (object uiElement in stack)
            {
                if (uiElement == null)
                    continue;

                if (uiElement.GetType().Name == uiClassName)
                {
                    return (bool)_uiBaseIsOpenedProp.GetValue(uiElement);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("[DashMacro] 특정 UI 상태 확인 실패: " + ex);
        }

        return false;
    }






    // ============================================================
    // 인게임 채팅 로그 관련
    // ============================================================

    private static MethodInfo _rpcChatMethod;
    private static bool _rpcChatLookupFailed;

    private static void SendGameLog(string text)
    {
        try
        {
            Type dungeonManagerType = FindTypeInLoadedAssemblies("DungeonManager");
            if (dungeonManagerType == null)
            {
                logger.LogWarning("[테스트] DungeonManager 타입을 찾지 못함");
                return;
            }

            PropertyInfo instanceProp = dungeonManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            object dungeonManagerInstance = instanceProp?.GetValue(null);
            if (dungeonManagerInstance == null)
            {
                logger.LogWarning("[테스트] DungeonManager.Instance == null");
                return;
            }

            // RpcChat 검색
            if (_rpcChatMethod == null && !_rpcChatLookupFailed)
            {
                _rpcChatMethod = dungeonManagerType.GetMethod("RpcChat", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_rpcChatMethod == null)
                {
                    _rpcChatLookupFailed = true;
                    logger.LogWarning("[테스트] RpcChat 메소드를 찾지 못함");
                }
            }

            if (_rpcChatMethod == null)
                return;

            // RpcChat 호출
            _rpcChatMethod.Invoke(dungeonManagerInstance, new object[] { null, "[DashMacro]", text });
        }
        catch (Exception ex)
        {
            logger.LogWarning("[테스트] 인게임 로그 전송 실패: " + ex);
        }
    }

    private static Type FindTypeInLoadedAssemblies(string typeName)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = asm.GetType(typeName);
            if (type != null)
                return type;
        }

        return null;
    }
}