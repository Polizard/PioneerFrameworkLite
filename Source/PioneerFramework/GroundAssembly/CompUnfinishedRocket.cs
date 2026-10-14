using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 未完工火箭框架的 Comp。框架是一个大型 Building/Frame，
    /// 施工到 100% 后由本 Comp 标记为已完工，并触发口袋地图生成/关联。
    /// 对应 v1 设计中的 CompUnfinishedRocket。
    /// </summary>
    public class CompProperties_UnfinishedRocket : CompProperties
    {
        public CompProperties_UnfinishedRocket()
        {
            compClass = typeof(CompUnfinishedRocket);
        }
    }

    public class CompUnfinishedRocket : ThingComp
    {
        private RocketDesignSpec spec = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        private bool isCompleted;

        public RocketDesignSpec Spec
        {
            get
            {
                if (spec == null)
                    spec = new RocketDesignSpec();
                return spec;
            }
        }

        public bool IsCompleted => isCompleted;

        public void AssignSpec(RocketDesignSpec newSpec)
        {
            spec = newSpec ?? new RocketDesignSpec();
            isCompleted = false;
        }

        /// <summary>
        /// 框架施工完成时调用。调试标记会直接调用原版 Frame.CompleteConstruction，
        /// 把 Frame 真正替换成已完工火箭建筑，并通知所属发射台进入已完工状态。
        /// 口袋地图生成由后续完工流程/发射流程接入。
        /// </summary>
        public void NotifyConstructionCompleted()
        {
            if (isCompleted)
                return;

            if (parent is Frame frame)
            {
                IntVec3 pos = frame.Position;
                Map map = frame.Map;
                Pawn? worker = map?.mapPawns?.FreeColonistsSpawned?.FirstOrDefault();
                if (worker == null)
                {
                    Log.Warning("[PioneerFramework] 标记完工：找不到可用施工者，无法完成框架。");
                    return;
                }

                frame.CompleteConstruction(worker);
                isCompleted = true;
                Log.Message($"[PioneerFramework] Rocket frame completed: {Spec.rocketDefName}");

                // 通知总装发射台：框架已变为已完工火箭。
                ThingDef rocketDef = DefDatabase<ThingDef>.GetNamedSilentFail(Spec.rocketDefName);
                Thing? built = null;
                if (rocketDef != null && map != null)
                {
                    built = map.thingGrid.ThingsListAt(pos)
                        .FirstOrDefault(t => !t.Destroyed && t.def == rocketDef);
                }
                if (built != null)
                {
                    Building? pad = built.Map?.thingGrid.ThingsListAt(pos)
                        .OfType<Building>()
                        .FirstOrDefault(b => LaunchPadUtility.IsAssemblyPad(b.def));
                    pad?.TryGetComp<CompAssemblyPad>()?.NotifyRocketCompleted(built);
                }
                return;
            }

            isCompleted = true;
            Log.Message($"[PioneerFramework] Rocket frame completed: {Spec.rocketDefName}");
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Deep.Look(ref spec, "spec");
            Scribe_Values.Look(ref isCompleted, "isCompleted", false);
            if (spec == null)
                spec = new RocketDesignSpec();
        }

        public override string CompInspectStringExtra()
        {
            return "火箭框架: " + Spec.rocketDefName + " " + (isCompleted ? "已完工" : "未完工");
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (var g in base.CompGetGizmosExtra())
                yield return g;

            if (parent.Faction != Faction.OfPlayer)
                yield break;

            if (!isCompleted)
            {
                yield return new Command_Action
                {
                    defaultLabel = "标记完工 (调试)",
                    defaultDesc = "跳过施工，直接标记火箭框架为已完工，用于调试后续流程。",
                    icon = TexCommand.DesirePower,
                    action = delegate
                    {
                        NotifyConstructionCompleted();
                    }
                };
            }
        }
    }
}
