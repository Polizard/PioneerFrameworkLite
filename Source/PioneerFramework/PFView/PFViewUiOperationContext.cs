namespace PioneerFramework
{
    /// <summary>武器面板的命令来源。查看状态完全由窗口管理，不进入命令来源计算。</summary>
    internal sealed class PFViewUiOperationContext
    {
        public bool WeaponsOpen { get; private set; }
        public int WeaponSourceGroupId { get; private set; } = -1;

        public bool TryOpenWeapons(int groupId, bool canOperate, int steeringGroupId)
        {
            if (groupId < 0 || !canOperate || (steeringGroupId >= 0 && steeringGroupId != groupId)) return false;
            WeaponsOpen = true;
            WeaponSourceGroupId = groupId;
            return true;
        }

        public int ResolveSource(int steeringGroupId)
        {
            if (steeringGroupId >= 0)
            {
                // 显式切换操舵对象后，武器来源跟随宿主权威；结束操舵不会回跳旧船。
                if (WeaponsOpen) WeaponSourceGroupId = steeringGroupId;
                return steeringGroupId;
            }
            return WeaponsOpen ? WeaponSourceGroupId : -1;
        }

        public void CloseWeapons()
        {
            WeaponsOpen = false;
            WeaponSourceGroupId = -1;
        }

        public void InvalidateSource(int groupId)
        {
            if (WeaponSourceGroupId == groupId) CloseWeapons();
        }

        public void Reset() => CloseWeapons();

        public static bool CanFire(int sourceId, int targetId, bool sourceOperable,
            bool targetKnownAndActive, bool weaponReady)
            => sourceId >= 0 && targetId >= 0 && sourceId != targetId
               && sourceOperable && targetKnownAndActive && weaponReady;
    }
}
