using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 空间站地图的控制器。
    /// 管理地图的冻结/解冻状态。
    /// 冻结 = 无pawn时暂停所有自定义 tick，但不销毁地图。
    /// </summary>
    public class SpaceStationController : MapComponent
    {
        private bool frozen;

        // 保存被冻结前处于通电状态的建筑，解冻时恢复
        private List<Building> poweredBuildings = new List<Building>();

        public bool Frozen => frozen;

        public SpaceStationController(Map map) : base(map) { }

        /// <summary>
        /// 尝试冻结地图。仅当地图上没有 pawn 时才成功。
        /// </summary>
        public bool Freeze()
        {
            // 检查是否有任何 pawn（包含敌对、动物、机械族）
            if (map.mapPawns.AllPawns.Any())
            {
                Messages.Message("Cannot freeze: pawns still present on the station.",
                    MessageTypeDefOf.RejectInput, false);
                return false;
            }

            frozen = true;

            // 切断所有用电器的电力，记录当前通电的建筑
            poweredBuildings.Clear();
            foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
            {
                var powerComp = thing.TryGetComp<CompPowerTrader>();
                if (powerComp != null && powerComp.PowerOn)
                {
                    poweredBuildings.Add((Building)thing);
                    powerComp.PowerOn = false;
                }
            }

            Messages.Message("Station frozen.", MessageTypeDefOf.SilentInput, false);
            return true;
        }

        /// <summary>
        /// 解冻地图，恢复之前通电的建筑。
        /// </summary>
        public void Unfreeze()
        {
            frozen = false;

            // 恢复之前通电的建筑
            foreach (var building in poweredBuildings)
            {
                if (building.Destroyed) continue;
                var powerComp = building.TryGetComp<CompPowerTrader>();
                if (powerComp != null)
                    powerComp.PowerOn = true;
            }
            poweredBuildings.Clear();

            Messages.Message("Station unfrozen.", MessageTypeDefOf.SilentInput, false);
        }

        /// <summary>
        /// 冻结时跳过所有自定义 tick。
        /// </summary>
        public override void MapComponentTick()
        {
            if (frozen)
                return;

            // 后续的空间站自定义 tick 逻辑放在这里
            // 例如：推进器合力计算、空间站移动等
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref frozen, "frozen");
            Scribe_Collections.Look(ref poweredBuildings, "poweredBuildings", LookMode.Reference);
        }
    }
}