using RimWorld;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 发射台脐带塔：地面侧桥。
    /// 继承原版 MapPortal，复用 Dialog_EnterPortal / LordJob_LoadAndEnterPortal 转移逻辑。
    /// 地图本体仍是 SpacecraftMapParent，不是口袋地图。
    /// </summary>
    public class Building_RocketUmbilicalTower : MapPortal
    {
        public override string EnterString => "登上火箭";

        public override Map? GetOtherMap()
        {
            CompUmbilicalTower tower = GetComp<CompUmbilicalTower>();
            SpacecraftMapParent? spacecraft = tower?.EnsureSpacecraftMap();
            return spacecraft?.Map;
        }

        public override IntVec3 GetDestinationLocation()
        {
            CompUmbilicalTower tower = GetComp<CompUmbilicalTower>();
            return tower?.Bridge?.spacecraftCell ?? IntVec3.Invalid;
        }

        public override bool IsEnterable(out string reason)
        {
            if (!base.IsEnterable(out reason))
                return false;

            CompUmbilicalTower tower = GetComp<CompUmbilicalTower>();
            ThingWithComps? pad = tower?.AssemblyPad;
            CompAssemblyPad? padComp = pad?.TryGetComp<CompAssemblyPad>();
            if (padComp == null || padComp.State != LaunchPadState.Completed)
            {
                reason = "当前发射台没有已完工火箭。";
                return false;
            }

            if (padComp.Design == null || padComp.Design.CabinType != RocketCabinType.Spacecraft)
            {
                reason = "当前火箭不是航天舱室，无法登船。";
                return false;
            }

            return true;
        }
    }
}
