using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 火箭建造蓝图的 Comp：在原版 Blueprint 阶段持有 RocketDesignSpec，
    /// 当 Blueprint 转成 Frame 时由 Harmony 补丁把 spec 转交给 CompUnfinishedRocket。
    /// </summary>
    public class CompProperties_RocketBlueprint : CompProperties
    {
        public CompProperties_RocketBlueprint()
        {
            compClass = typeof(CompRocketBlueprint);
        }
    }

    public class CompRocketBlueprint : ThingComp
    {
        private RocketDesignSpec spec;

        public RocketDesignSpec Spec
        {
            get
            {
                if (spec == null)
                    spec = new RocketDesignSpec();
                return spec;
            }
        }

        public void AssignSpec(RocketDesignSpec newSpec)
        {
            spec = newSpec ?? new RocketDesignSpec();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Deep.Look(ref spec, "spec");
            if (spec == null)
                spec = new RocketDesignSpec();
        }

        public override string CompInspectStringExtra()
        {
            return "火箭建造蓝图: " + Spec.rocketDefName;
        }
    }
}
