using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 储罐建筑 Comp 的配置。挂在储罐 ThingDef 的 comps 列表里。
    /// </summary>
    public class CompProperties_PropellantTank : CompProperties
    {
        /// <summary>本储罐储存的工质 Def 名（运行时解析为 PropellantDef）。</summary>
        public string propellantDefName;

        /// <summary>容量（kg）。</summary>
        public float capacity = 1000f;

        /// <summary>新建（非读档）时的初始存量（kg），默认 0 = 空罐。</summary>
        public float startStoredMass = 0f;

        public CompProperties_PropellantTank()
        {
            compClass = typeof(CompPropellantTank);
        }
    }

    /// <summary>
    /// 储罐建筑 Comp：地图（=船）内储罐的工质存储单元。
    /// 同步契约：加注 / 消耗 / 摧毁时，同步到所属地图 SpaceMapComp 的
    /// (工质 → 质量 kg) 聚合向量上；多条储罐同种工质自然累加，一条船可携带多种工质。
    /// 向量本身不落盘（由各储罐 Spawn/DeSpawn 重建），避免读档 / 取档重复记账。
    /// </summary>
    public class CompPropellantTank : ThingComp
    {
        public CompProperties_PropellantTank Props => (CompProperties_PropellantTank)props;

        /// <summary>当前存量（kg）。</summary>
        private float storedMass;

        public float StoredMass => storedMass;

        public float Capacity => Props.capacity;

        /// <summary>本储罐的工质。Def 缺失（配置错误）时为 null，同步自动跳过。</summary>
        public PropellantDef PropellantDef { get; private set; }

        public override void Initialize(CompProperties props)
        {
            base.Initialize(props);
            if (!string.IsNullOrEmpty(Props.propellantDefName))
                PropellantDef = DefDatabase<PropellantDef>.GetNamedSilentFail(Props.propellantDefName);
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            // 新建（非读档）时用配置的初始存量起步；读档时 storedMass 由 PostExposeData 恢复。
            if (!respawningAfterLoad && storedMass <= 0f && Props.startStoredMass > 0f)
                storedMass = Props.startStoredMass;
            RegisterToMap(+storedMass);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode)
        {
            base.PostDeSpawn(map, mode);
            // 拆除 / 摧毁 / 移走：把本罐存量从船级工质向量扣回。
            RegisterToMap(-storedMass, map);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref storedMass, "storedMass", 0f);
        }

        /// <summary>加注。返回实际加入的质量（受剩余容量截断，≥ 0）。</summary>
        public float TryFill(float mass)
        {
            if (mass <= 0f || PropellantDef == null) return 0f;
            float space = Mathf.Max(0f, Capacity - storedMass);
            float actual = Mathf.Min(mass, space);
            if (actual <= 0f) return 0f;

            storedMass += actual;
            RegisterToMap(+actual);
            return actual;
        }

        /// <summary>消耗 / 抽取。返回实际扣除的质量（受当前存量截断，≥ 0）。</summary>
        public float TryDrain(float demand)
        {
            if (demand <= 0f || storedMass <= 0f) return 0f;
            float actual = Mathf.Min(demand, storedMass);
            storedMass -= actual;
            RegisterToMap(-actual);
            return actual;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (var g in base.CompGetGizmosExtra())
                yield return g;

            if (parent.Faction != Faction.OfPlayer)
                yield break;

            // 调试用加注/排空（dV 系统验证阶段；正式补给链路后续接入）
            yield return new Command_Action
            {
                defaultLabel = "加注 100kg",
                defaultDesc = "向储罐加注 100 kg 工质（调试）",
                icon = TexCommand.DesirePower,
                action = delegate { TryFill(100f); }
            };
            yield return new Command_Action
            {
                defaultLabel = "排空储罐",
                defaultDesc = "抽干储罐内全部工质（调试）",
                icon = TexCommand.ClearPrioritizedWork,
                action = delegate { TryDrain(storedMass); }
            };
        }

        public override string CompInspectStringExtra()
        {
            string defName = PropellantDef != null ? PropellantDef.label : (Props.propellantDefName ?? "无工质");
            return "工质: " + defName + "  " + storedMass.ToString("0.#") + " / " + Capacity.ToString("0.#") + " kg";
        }

        /// <summary>把质量增减同步到所属地图的 SpaceMapComp 工质向量。</summary>
        private void RegisterToMap(float delta, Map map = null)
        {
            if (delta == 0f || PropellantDef == null) return;
            var m = map ?? parent.Map;
            if (m == null) return;
            var mapComp = m.GetComponent<SpaceMapComp>();
            if (mapComp == null) return;
            mapComp.AddPropellantMass(PropellantDef, delta);
        }
    }
}
