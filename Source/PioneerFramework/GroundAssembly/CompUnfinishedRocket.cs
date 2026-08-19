using System.Collections.Generic;
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
        private RocketDesignSpec spec;

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
        /// 框架施工完成时调用。当前只改状态；口袋地图生成由发射流程/完工流程后续接入。
        /// </summary>
        public void NotifyConstructionCompleted()
        {
            if (isCompleted)
                return;
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
