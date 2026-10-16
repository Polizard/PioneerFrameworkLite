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
        internal float LandingStoredMass { get => storedMass; set => storedMass = value; }

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
        // P51 分组用的复用容器（**每 tick 调用** ⇒ 不许每 tick 分配）：D 与 T 都是玩家可建的量。
        private static readonly Dictionary<PropellantDef, List<int>> _groupScratch =
            new Dictionary<PropellantDef, List<int>>();
        private static readonly List<PropellantDef> _leaderOrder = new List<PropellantDef>();

        public static void TickDifferenceSync(Map map)
        {
            if (map == null || !tanksByMap.TryGetValue(map, out var list) || list.Count == 0)
                return;

            var mapComp = map.GetComponent<SpaceMapComp>();
            if (mapComp == null) return;

            // ── P51（2026-09-27）：**一趟分组 O(T)**，替掉原实现"每个工质组都要扫全表两遍"的 O(T²) ──
            // 本函数**每 tick** 被 `SpaceMapComp` 驱动，而储罐数 T 是**玩家可建**的 ⇒ T 大时这一项是
            // 帧内可见的二次项。改法 = 先一趟按工质分组（**保列表顺序**），再逐组处理。
            // ⚠ **保序即保结果**：分组与求和都按列表顺序 ⇒ float 累加顺序不变 ⇒ 逐位相同（零行为变化）。
            // ⚠ **刻意保留原实现的一处怪癖**：首领（该工质在列表里**第一个**出现的那个）若无效
            //   （null / 已毁 / 无工质 Def），则**整组跳过** —— 原实现用"前面有没有同工质的"来定首领，
            //   而无效项也会被它读到并占住首领位 ⇒ 组被跳掉。这里按同样语义实现（不顺手"修好"它：
            //   那是独立决定，见下方登记）。
            _groupScratch.Clear();
            _leaderOrder.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i];
                if (t == null) continue;
                var def = t.PropellantDef;
                if (def == null) continue;
                if (!_groupScratch.TryGetValue(def, out var bucket))
                {
                    bucket = new List<int>();
                    _groupScratch[def] = bucket;
                    _leaderOrder.Add(def);
                }
                bucket.Add(i);   // 存**下标**（不存对象，避免持有已毁零件的引用）
            }

            for (int gi = 0; gi < _leaderOrder.Count; gi++)
            {
                var def = _leaderOrder[gi];
                var bucket = _groupScratch[def];

                // 首领 = 该工质在列表里第一个出现者；它无效 ⇒ 整组跳过（原语义）
                var leader = list[bucket[0]];
                if (leader.parent == null || leader.parent.Destroyed) continue;

                float localTotal = 0f;
                for (int k = 0; k < bucket.Count; k++)
                {
                    var t = list[bucket[k]];
                    if (t.parent == null || t.parent.Destroyed) continue;
                    localTotal += t.storedMass;
                }
                // ⚠ P51 同批（2026-09-27）：池侧已是 double ⇒ 这里的镜像比对也用 double，
                //   免得"池追罐"的误差被 float 截断吃回去（本地 `storedMass` 仍是 float：它是显示镜像）。
                double vectorMass = mapComp.GetPropellantMass(def);
                double diff = localTotal - vectorMass;
                if (diff <= 0.01) continue;
                if (localTotal <= 0f) continue;   // 与原实现里那句 `break` 等价：本组不做分摊

                for (int k = 0; k < bucket.Count; k++)
                {
                    var t = list[bucket[k]];
                    if (t.parent == null || t.parent.Destroyed) continue;
                    // ⚠ 本地 `storedMass` 仍是 float（**显示镜像**：真值是 `SpaceMapComp` 池里的 double）
                    //    ⇒ 这里一次边界 cast，镜像的算术与改前逐位相同（只跟随 double 的 diff，不改它的行为）。
                    float share = (float)(diff * ((double)t.storedMass / localTotal));
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
