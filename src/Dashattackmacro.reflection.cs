using System;
using System.Collections.Generic;
using System.Reflection;

public partial class DashAttackMacro
{
    // ============================================================
    // 대쉬 상태 (잔여 횟수 / 가능 여부) 리플렉션
    // ============================================================

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
    // 현재 장착 무기 정보 리플렉션
    // ============================================================

    public enum WeaponKind
    {
        Unknown = 0,
        SwordAndShield,
        Katana,
        GreatSword,
        Dagger,
        Crossbow,
        StaffMagic,
        Staff,
        Golem,
    }

    // EWeaponType 열거형의 실제 필드명 -> WeaponKind. 새 무기 추가 시 여기 한 줄만 추가.
    // ("Random"은 실제 무기가 아니라 선택 옵션이라 제외)
    private static readonly (string EnumFieldName, WeaponKind Kind)[] WeaponKindDefinitions =
    {
        ("SwordAndShield", WeaponKind.SwordAndShield),
        ("Katana",         WeaponKind.Katana),
        ("GreatSword",     WeaponKind.GreatSword),
        ("Dagger",         WeaponKind.Dagger),
        ("Crossbow",       WeaponKind.Crossbow),
        ("StaffMagic",     WeaponKind.StaffMagic),
        ("Staff",          WeaponKind.Staff),
        ("Golem",          WeaponKind.Golem),
    };

    // private static Type _playerAvatarType;
    private static Type _weaponControllerSimpleType;
    private static Type _weaponSimpleType;
    private static FieldInfo _weaponControllerField; // PlayerAvatar.weaponController (private)
    private static FieldInfo _currentWeaponField;    // WeaponControllerSimple.currentWeapon (public)
    private static FieldInfo _weaponTypeField;       // WeaponSimple.weaponType (public, EWeaponType)
    private static readonly Dictionary<object, WeaponKind> _weaponKindByEnumValue = new Dictionary<object, WeaponKind>();
    private static bool _weaponInfoLookupFailed;

    private static bool EnsureWeaponInfoReflection()
    {
        if (_weaponInfoLookupFailed) return false;
        if (_weaponTypeField != null) return true; // 캐시 조기 반환

        try
        {
            _playerAvatarType = FindTypeInLoadedAssemblies("PlayerAvatar");
            _weaponControllerSimpleType = FindTypeInLoadedAssemblies("WeaponControllerSimple");
            _weaponSimpleType = FindTypeInLoadedAssemblies("WeaponSimple");
            Type eWeaponTypeEnum = FindTypeInLoadedAssemblies("EWeaponType");

            if (_playerAvatarType == null || _weaponControllerSimpleType == null || _weaponSimpleType == null || eWeaponTypeEnum == null)
            {
                logger.LogWarning("[DashMacro] 무기 정보 리플렉션 대상 타입을 찾지 못함");
                _weaponInfoLookupFailed = true;
                return false;
            }

            _weaponControllerField = _playerAvatarType.GetField("weaponController", BindingFlags.NonPublic | BindingFlags.Instance);
            _currentWeaponField = _weaponControllerSimpleType.GetField("currentWeapon", BindingFlags.Public | BindingFlags.Instance);
            _weaponTypeField = _weaponSimpleType.GetField("weaponType", BindingFlags.Public | BindingFlags.Instance);

            if (_weaponControllerField == null || _currentWeaponField == null || _weaponTypeField == null)
            {
                logger.LogWarning("[DashMacro] 무기 정보 리플렉션 멤버 조회 실패 (게임 버전 변경 가능성)");
                _weaponInfoLookupFailed = true;
                return false;
            }

            // EWeaponType의 각 static literal 값을 미리 boxing해서 캐시 (최초 1회만)
            foreach (var (fieldName, kind) in WeaponKindDefinitions)
            {
                FieldInfo enumField = eWeaponTypeEnum.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
                if (enumField != null)
                    _weaponKindByEnumValue[enumField.GetValue(null)] = kind;
                else
                    logger.LogWarning($"[DashMacro] EWeaponType.{fieldName}을 찾지 못함 → 해당 종류는 Unknown 처리됨");
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("[DashMacro] 무기 정보 리플렉션 초기화 실패: " + ex);
            _weaponInfoLookupFailed = true;
            return false;
        }
    }

    public readonly struct WeaponInfo
    {
        public readonly WeaponKind Kind;
        public readonly bool? IsGuardButtonDown; // SwordAndShield 전용
        // public readonly bool? IsGuarding;
        public readonly bool? IsBladeSheathed;   // 카타나 전용

        public WeaponInfo(WeaponKind kind, bool? isGuardButtonDown, bool? isBladeSheathed)
        {
            Kind = kind;
            IsGuardButtonDown = isGuardButtonDown;
            // IsGuarding = isGuarding;
            IsBladeSheathed = isBladeSheathed;
        }

        public bool IsSwordAndShield => Kind == WeaponKind.SwordAndShield;
        public bool IsKatana => Kind == WeaponKind.Katana;

        public static readonly WeaponInfo Unknown = new WeaponInfo(WeaponKind.Unknown, null, null);
    }

    // 검과방패로 판정된 경우, 가드 상태 불러오기
    private static FieldInfo _isGuardButtonDownField;
    private static Type _isGuardButtonDownFieldOwnerType;
    private static bool _guardButtonDownFieldLookupFailed;

    private static FieldInfo GetOrResolveGuardButtonDownField(Type swordAndShieldInstanceType)
    {
        if (_guardButtonDownFieldLookupFailed) return null;
        if (_isGuardButtonDownField != null && _isGuardButtonDownFieldOwnerType == swordAndShieldInstanceType)
            return _isGuardButtonDownField;

        // private 필드라 NonPublic 필요
        FieldInfo field = swordAndShieldInstanceType.GetField("isGuardButtonDown", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
        {
            logger.LogWarning($"[DashMacro] {swordAndShieldInstanceType.Name}에서 isGuardButtonDown 필드를 찾지 못함");
            _guardButtonDownFieldLookupFailed = true;
            return null;
        }

        _isGuardButtonDownField = field;
        _isGuardButtonDownFieldOwnerType = swordAndShieldInstanceType;
        return field;
    }

    // private static FieldInfo _isGuardAnimationTurnedOnField;
    // private static Type _isGuardAnimationTurnedOnFieldOwnerType;
    // private static bool _swordAndShieldFieldLookupFailed;

    // private static FieldInfo GetOrResolveGuardField(Type swordAndShieldInstanceType)
    // {
    //     if (_swordAndShieldFieldLookupFailed) return null;
    //     if (_isGuardAnimationTurnedOnField != null && _isGuardAnimationTurnedOnFieldOwnerType == swordAndShieldInstanceType)
    //         return _isGuardAnimationTurnedOnField;

    //     FieldInfo field = swordAndShieldInstanceType.GetField("isGuardAnimationTurnedOn", BindingFlags.Public | BindingFlags.Instance);
    //     if (field == null)
    //     {
    //         logger.LogWarning($"[DashMacro] {swordAndShieldInstanceType.Name}에서 isGuardAnimationTurnedOn 필드를 찾지 못함");
    //         _swordAndShieldFieldLookupFailed = true;
    //         return null;
    //     }

    //     _isGuardAnimationTurnedOnField = field;
    //     _isGuardAnimationTurnedOnFieldOwnerType = swordAndShieldInstanceType;
    //     return field;
    // }

    // 카타나로 판정된 경우, 납도 상태 불러오기
    private static FieldInfo _isBladeSheathedField;
    private static Type _isBladeSheathedFieldOwnerType;
    private static bool _katanaFieldLookupFailed;

    private static FieldInfo GetOrResolveBladeSheathedField(Type katanaInstanceType)
    {
        if (_katanaFieldLookupFailed) return null;
        if (_isBladeSheathedField != null && _isBladeSheathedFieldOwnerType == katanaInstanceType)
            return _isBladeSheathedField;

        FieldInfo field = katanaInstanceType.GetField("isBladeSheathed", BindingFlags.Public | BindingFlags.Instance);
        if (field == null)
        {
            logger.LogWarning($"[DashMacro] {katanaInstanceType.Name}에서 isBladeSheathed 필드를 찾지 못함");
            _katanaFieldLookupFailed = true;
            return null;
        }

        _isBladeSheathedField = field;
        _isBladeSheathedFieldOwnerType = katanaInstanceType;
        return field;
    }

    private static WeaponInfo GetWeaponInfo()
    {
        if (!EnsureCanDashReflection() || !EnsureWeaponInfoReflection())
            return WeaponInfo.Unknown;

        try
        {
            object pic = _picInstanceProp.GetValue(null);
            object avatar = pic != null ? _picAvatarField.GetValue(pic) : null;
            if (avatar == null) return WeaponInfo.Unknown;

            object weaponController = _weaponControllerField.GetValue(avatar);
            object weapon = weaponController != null ? _currentWeaponField.GetValue(weaponController) : null;
            if (weapon == null) return WeaponInfo.Unknown;

            object weaponTypeValue = _weaponTypeField.GetValue(weapon);
            WeaponKind kind = _weaponKindByEnumValue.TryGetValue(weaponTypeValue, out var k) ? k : WeaponKind.Unknown;

            bool? guarding = null;
            bool? sheathed = null;

            if (kind == WeaponKind.SwordAndShield)
            {
                FieldInfo field = GetOrResolveGuardButtonDownField(weapon.GetType());
                // FieldInfo field = GetOrResolveGuardField(weapon.GetType());
                if (field != null)
                    guarding = (bool)field.GetValue(weapon);
            }
            else if (kind == WeaponKind.Katana)
            {
                FieldInfo field = GetOrResolveBladeSheathedField(weapon.GetType());
                if (field != null)
                    sheathed = (bool)field.GetValue(weapon);
            }

            return new WeaponInfo(kind, guarding, sheathed);
        }
        catch (Exception ex)
        {
            logger.LogWarning("[DashMacro] 무기 정보 조회 실패: " + ex);
            return WeaponInfo.Unknown;
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