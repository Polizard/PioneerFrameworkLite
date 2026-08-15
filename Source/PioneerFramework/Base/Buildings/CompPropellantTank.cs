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
        /// <summary>
        /// 地图 → 该地图内全部储罐 Comp（按注册顺序）。
        /// 用于「差值同步」：burn 等外部消耗只扣 mapComp 聚合向量，储罐建筑本地
        /// 存量需要逐 tick 追上向量，才能让地图上的储罐液面真实下降。
        /// </summary>
        private static readonly Dictionary<Map, List<CompPropellantTank>> tanksByMap = new Dictionary<Map, List<CompPropellantTank>>();

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
            RegisterCapacity(+1, parent.Map);

            // 登记到差值同步注册表（同工质组内第一个储罐作为同步执行者）
            if (!tanksByMap.TryGetValue(parent.Map, out var list))
            {
                list = new List<CompPropellantTank>();
                tanksByMap[parent.Map] = list;
            }
            list.Add(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode)
        {
            base.PostDeSpawn(map, mode);
            // 拆除 / 摧毁 / 移走：把本罐存量从船级工质向量扣回。
            RegisterToMap(-storedMass, map);
            RegisterCapacity(-1, map);

            if (tanksByMap.TryGetValue(map, out var list))
            {
                list.Remove(this);
                if (list.Count == 0)
                    tanksByMap.Remove(map);
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref storedMass, "storedMass", 0f);
        }

        public override void CompTick()
        {
            base.CompTick();
            // 空间站地图为 pocket map，隐藏状态下 Thing 的 CompTick 不可靠；
            // 差值同步统一由 SpaceGameComp.GameComponentTick 驱动（见 TickDifferenceSync）。
        }

        /// <summary>
        /// 差值同步（每 tick 由 SpaceGameComp 对空间站地图调用）：
        /// burn / 轨道保持 / 外部扣减只动 mapComp 工质向量，储罐建筑本地存量逐 tick 追平，
        /// 地图上储罐液面真实下降。1x 下物理步 2s 扣一次、1/60x 下每 2/60s 扣一次均适用。
        /// 只改本地存量不回写向量（向量是目标值），避免与 RegisterToMap 双向循环。
        /// </summary>
        public static void TickDifferenceSync(Map map)
        {
            if (map == null || !tanksByMap.TryGetValue(map, out var list) || list.Count == 0)
                return;

            var mapComp = map.GetComponent<SpaceMapComp>();
            if (mapComp == null) return;

            // 按工质分组处理（每种工质组内第一个储罐作为执行者）
            for (int g = 0; g < list.Count; g++)
            {
                var leader = list[g];
                if (leader == null || leader.parent == null || leader.parent.Destroyed) continue;
                var def = leader.PropellantDef;
                if (def == null) continue;

                // 找到同工质组内第一个注册者，仅它执行（其余跳过）
                bool isLeader = true;
                for (int i = 0; i < g; i++)
                {
                    if (list[i].PropellantDef == def) { isLeader = false; break; }
                }
                if (!isLeader) continue;

                float localTotal = 0f;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].PropellantDef != def || list[i].parent == null || list[i].parent.Destroyed) continue;
                    localTotal += list[i].storedMass;
                }
                float vectorMass = mapComp.GetPropellantMass(def);
                float diff = localTotal - vectorMass;
                if (diff <= 0.01f) continue;

                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    if (t.PropellantDef != def || t.parent == null || t.parent.Destroyed) continue;
                    if (localTotal <= 0f) break;
                    float share = diff * (t.storedMass / localTotal);
                    if (share > 0f)
                        t.storedMass = Mathf.Max(0f, t.storedMass - share);
                }
            }
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

        /// <summary>同步本罐容量到 mapComp（MaxDeltaV 的满箱口径）。</summary>
        private void RegisterCapacity(int sign, Map map)
        {
            var m = map ?? parent.Map;
            if (m == null) return;
            var mapComp = m.GetComponent<SpaceMapComp>();
            if (mapComp == null) return;
            mapComp.AddPropellantCapacity(sign * Capacity);
        }
    }
}
