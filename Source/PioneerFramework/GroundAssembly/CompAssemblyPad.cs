using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using PioneerFramework.Launch;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 发射台状态机。
    /// 空闲 -> 设计中 -> 建造中 -> 已完工 -> 发射后回空闲。
    /// 对应 v1 设计中的发射台状态机。
    /// </summary>
    public enum LaunchPadState
    {
        Idle,
        Designing,
        Building,
        Completed
    }

    public class CompProperties_AssemblyPad : CompProperties
    {
        public CompProperties_AssemblyPad()
        {
            compClass = typeof(CompAssemblyPad);
        }
    }

    public class CompAssemblyPad : ThingComp
    {
        private LaunchPadState state;

        private RocketDesignSpec design;

        private Thing builtRocket;

        private int pocketMapId = -1;

        private RocketLaunchMode launchMode = RocketLaunchMode.PflOrbit;

        public LaunchPadState State => state;

        public RocketDesignSpec Design => design;

        public int PocketMapId => pocketMapId;

        /// <summary>当前记录的发射方式，默认 PFL 入轨。</summary>
        public RocketLaunchMode LaunchMode => launchMode;

        /// <summary>当前已完工/关联的火箭 Building；未完工或空闲时为 null。</summary>
        public Thing BuiltRocket => builtRocket;

        public void SetLaunchMode(RocketLaunchMode mode)
        {
            launchMode = mode;
        }

        /// <summary>发射完成（原版或 PFL）后复位发射台状态。</summary>
        public void NotifyLaunched()
        {
            var spacecraftMap = SpacecraftMapRegistry.GetBySpacecraftId(parent?.ThingID);
            spacecraftMap?.MarkLaunched();

            pocketMapId = -1;
            builtRocket = null;
            design = null;
            state = LaunchPadState.Idle;
        }

        /// <summary>把由脐带塔 MapPortal 生成/复用的舱内地图登记到发射台。</summary>
        public void RegisterExternalPocketMap(Map map)
        {
            if (map == null)
                return;

            pocketMapId = map.uniqueID;
        }

        /// <summary>发射台当前关联的火箭舱内口袋地图（可能尚未生成）。</summary>
        public Map GetPocketMap()
        {
            if (pocketMapId < 0)
                return null;
            return PioneerFramework.SpaceMapGenerator.FindPocketMap(pocketMapId);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref state, "state", LaunchPadState.Idle);
            Scribe_Deep.Look(ref design, "design");
            Scribe_References.Look(ref builtRocket, "builtRocket");
            Scribe_Values.Look(ref pocketMapId, "pocketMapId", -1);
            Scribe_Values.Look(ref launchMode, "launchMode", RocketLaunchMode.PflOrbit);
            if (design == null)
                design = new RocketDesignSpec();
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            EnsureUmbilicalTower();
        }

        /// <summary>确保发射台周边存在一个已绑定的脐带塔登船口。</summary>
        private void EnsureUmbilicalTower()
        {
            if (parent == null || parent.Map == null)
                return;

            ThingDef towerDef = DefDatabase<ThingDef>.GetNamedSilentFail("PF_Building_Rocket_UmbilicalTower");
            if (towerDef == null)
                return;

            // 已有绑定本发射台的脐带塔：直接复用。
            foreach (Thing thing in parent.Map.listerThings.ThingsOfDef(towerDef))
            {
                if (thing.TryGetComp<CompUmbilicalTower>()?.AssemblyPad == parent)
                    return;
            }

            IntVec3 cell = FindUmbilicalTowerCell();
            if (!cell.IsValid || !cell.InBounds(parent.Map))
                return;

            Building tower = (Building)ThingMaker.MakeThing(towerDef);
            tower.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(tower, cell, parent.Map, Rot4.North);
            CompUmbilicalTower towerComp = tower.TryGetComp<CompUmbilicalTower>();
            towerComp?.Bind(parent);
            // 只重连已有地图，不在建台阶段提前生成航天器地图。
            towerComp?.RebindExistingSpacecraftMap();
        }

        private IntVec3 FindUmbilicalTowerCell()
        {
            Map map = parent?.Map;
            if (map == null)
                return IntVec3.Invalid;

            CellRect padRect = parent.OccupiedRect();
            foreach (IntVec3 cell in padRect.ExpandedBy(1).Cells)
            {
                if (!cell.InBounds(map) || !cell.Standable(map))
                    continue;

                bool occupied = map.thingGrid.ThingsListAt(cell).Any(t => t is Building);
                if (!occupied)
                    return cell;
            }

            return IntVec3.Invalid;
        }

        /// <summary>进入设计状态并保存当前设计。</summary>
        public void SetDesign(RocketDesignSpec newDesign)
        {
            design = newDesign ?? new RocketDesignSpec();
            builtRocket = null;
            state = LaunchPadState.Designing;
            pocketMapId = -1;
        }

        /// <summary>直接登记一座已完工火箭（调试重建用），让发射台可直接发射它。</summary>
        public void SetCompletedRocket(RocketDesignSpec newDesign, Thing rocket)
        {
            design = newDesign ?? new RocketDesignSpec();
            builtRocket = rocket;
            state = LaunchPadState.Completed;
        }

        /// <summary>框架施工完成/调试标记完工后，通知发射台登记已完工火箭。</summary>
        public void NotifyRocketCompleted(Thing rocket)
        {
            if (rocket == null || rocket.Destroyed)
                return;
            builtRocket = rocket;
            state = LaunchPadState.Completed;
        }

        /// <summary>确认设计，生成未完工火箭框架。</summary>
        public bool TryStartBuild()
        {
            string failReason = "";
            if (design == null || !design.IsValid(out failReason))
            {
                if (!failReason.NullOrEmpty())
                    Messages.Message(failReason, MessageTypeDefOf.RejectInput, false);
                return false;
            }

            if (parent == null || parent.Map == null)
                return false;

            IntVec3 center = parent.OccupiedRect().CenterCell;
            if (!LaunchService.SpawnDebugRocketFrame(parent.Map, center, design))
                return false;

            state = LaunchPadState.Building;
            builtRocket = parent.Map.thingGrid.ThingsListAt(center)
                .FirstOrDefault(t => LaunchPadUtility.IsRocket(t.def));
            return true;
        }

        /// <summary>当前关联火箭被摧毁/消失时，回到设计阶段（保留设计数据）。</summary>
        private void CheckRocketState()
        {
            if (state != LaunchPadState.Building && state != LaunchPadState.Completed)
                return;

            bool rocketGone = builtRocket == null || builtRocket.Destroyed || !builtRocket.Spawned;
            if (!rocketGone)
                return;

            var spacecraftMap = SpacecraftMapRegistry.GetBySpacecraftId(parent?.ThingID);
            if (spacecraftMap != null)
                SpacecraftMapRegistry.Destroy(spacecraftMap);

            builtRocket = null;
            pocketMapId = -1;
            state = design != null ? LaunchPadState.Designing : LaunchPadState.Idle;
        }

        public override void CompTick()
        {
            base.CompTick();
            CheckRocketState();
        }

        /// <summary>取消当前设计/建造并回到空闲。</summary>
        public void CancelDesign()
        {
            // 已完工后的取消不是合法流程；防止绕过 UI 造成孤儿航天器地图。
            if (state == LaunchPadState.Completed)
                return;

            design = null;
            builtRocket = null;
            pocketMapId = -1;
            state = LaunchPadState.Idle;
        }

        /// <summary>发射当前火箭。成功后回到空闲。</summary>
        /// <param name="debug">true 时跳过「有人控制台 / 舱室封闭」人员校验。</param>
        public bool TryLaunch(bool debug = false)
        {
            if (design == null || parent == null || parent.Map == null)
                return false;

            IntVec3 rocketPos = builtRocket != null && builtRocket.Spawned
                ? builtRocket.OccupiedRect().CenterCell
                : parent.OccupiedRect().CenterCell;
            var entity = LaunchService.LaunchFromGround(parent.Map, rocketPos, design, debug);
            if (entity == null)
                return false;

            NotifyLaunched();
            return true;
        }

        public override string CompInspectStringExtra()
        {
            string stateText = state switch
            {
                LaunchPadState.Idle => "空闲",
                LaunchPadState.Designing => "设计中",
                LaunchPadState.Building => "建造中",
                LaunchPadState.Completed => "已完工",
                _ => "未知"
            };
            string designText = design != null
                ? (design.rocketDefName == "PF_Building_Rocket_Recoverable_Basic" ? "可回收火箭"
                    : design.rocketDefName == "PF_Building_Rocket_Basic" ? "不可回收火箭"
                    : design.rocketDefName)
                : "无设计";
            string modeText = launchMode == RocketLaunchMode.VanillaTransportPod ? "原版 TransportPod" : "PFL 入轨";
            return "发射台状态: " + stateText + "\n设计: " + designText + "\n发射方式: " + modeText;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (var g in base.CompGetGizmosExtra())
                yield return g;

            if (parent.Faction != Faction.OfPlayer)
                yield break;

            // 航天器管理界面是发射台唯一管理入口；设计/施工/舱内/发射/取消等操作全部由窗口承接。
            yield return new Command_Action
            {
                defaultLabel = "航天器管理",
                defaultDesc = "打开统一航天器管理界面。",
                icon = TexCommand.DesirePower,
                action = delegate
                {
                    Find.WindowStack.Add(new Window_SpacecraftManagement((Building)parent));
                }
            };
        }
    }
}
