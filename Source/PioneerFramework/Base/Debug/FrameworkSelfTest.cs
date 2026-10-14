using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **统一判定框架的首两个用例**（S1a，2026-09-23）—— 把此前"只打印数字、没有判据"的两个探针
    /// （`FactionResolver.LogProbe` / `VisibilityProbe`）变成**有判据、且遵守 UNCHECKED 纪律**的用例。
    ///
    /// 它们同时是**框架自己的自证**：框架能不能跑、结论块长什么样，按一次这个按钮就知道
    /// （调试页签「00 验收」→「测试框架自检」）。
    ///
    /// ## 为什么从这两个开始
    /// 体检（2026-09-23）发现全库"有代码无判据"的两类最典型形态：
    /// · **只打印不判定**（阵营解析读数、可见性快照）⇒ 沉默会被读成"没问题"；
    /// · **判据今天还立不起来**（`DetectTech` 无写入者）⇒ 这种要**显式登记**（<see cref="PflTestKit.CheckUnchecked"/>），
    ///   既不冒充验证过，也不污染整个用例的结论。
    /// </summary>
    public static class FrameworkSelfTest
    {
        /// <summary>单独按按钮：重置 + 跑 + 打结论块。</summary>
        public static void Run()
        {
            PflTestKit.Reset();
            RunCases();
            PflTestKit.PrintVerdict("测试框架自检（首两用例）");
        }

        /// <summary>**只跑用例本体**（重置与打结论由调用方负责）—— 供「一键测试（全覆盖）」复用，判据不复制。</summary>
        public static void RunCases()
        {
            RunFactionCase();
            RunVisibilityCase();
        }

        /// <summary>
        /// ① **[V12] 阵营解析**：真阵营语义必须能把"敌方"判成敌对；且本用例自己不许制造解析失败。
        /// ⚠ 世界里没有敌对阵营时（`-1`）⇒ **整个用例 UNCHECKED**（0 样本，判不了）。
        /// </summary>
        private static void RunFactionCase()
        {
            PflTestKit.Begin("阵营解析（[V12]）", "00 验收");
            int before = FactionResolver.UnresolvedCount;

            int hostileId = FactionResolver.HostileFactionIdForPlayer();
            var player = Faction.OfPlayer;

            if (hostileId < 0 || player == null)
            {
                PflTestKit.Unchecked("敌对阵营语义",
                    $"没有可判的样本（hostileId={hostileId}、玩家阵营{(player == null ? "缺失" : "在")}）⇒ 敌对语义无从断言");
                return;
            }

            var hostile = FactionResolver.Get(hostileId);
            PflTestKit.Check(PflTestKit.Kind.Invariant, hostile != null, "敌对阵营 id 可解析",
                hostile != null ? hostile.Name : "解析返回 null（假 id？）");
            if (hostile == null)
            {
                PflTestKit.End();
                return;
            }

            // ★ 核心断言（V12 的语义接回）：真阵营语义下"敌方"必须被判成敌对
            PflTestKit.Check(PflTestKit.Kind.Value, FactionResolver.IsHostile(hostileId, player.loadID),
                "敌方 → 判为敌对（Faction.HostileTo 语义）");
            // 自反：自己与自己不敌对（真阵营语义的必要条件）
            PflTestKit.Check(PflTestKit.Kind.Value, !FactionResolver.IsHostile(player.loadID, player.loadID),
                "玩家 → 玩家 不敌对");
            // 不变式：本用例自己不许制造解析失败（真 id 都该解析得出来）
            PflTestKit.Check(PflTestKit.Kind.Invariant, FactionResolver.UnresolvedCount == before,
                "解析失败计数不增长",
                $"{before} → {FactionResolver.UnresolvedCount}");
            PflTestKit.End();
        }

        /// <summary>
        /// ② **[P1] 可见性四字段**：能断言的（信号随主动开关单调抬升）断言；
        /// 今天**立不起来的**（`DetectTech` 无写入者）**显式登记为 UNCHECKED 子项**，不冒充已验。
        /// </summary>
        private static void RunVisibilityCase()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null)
            {
                PflTestKit.Begin("可见性四字段（[P1]）", "00 验收");
                PflTestKit.Unchecked("样本编队", "无 SpaceGameComp（不在太空场景？）");
                return;
            }

            SpaceEntityGroup? sample = null;
            int activeCount = 0;
            foreach (var g in sg.GetAllGroups())
            {
                if (!g.IsActive) continue;
                activeCount++;
                if (sample == null) sample = g;
            }
            if (sample == null)
            {
                PflTestKit.Begin("可见性四字段（[P1]）", "00 验收");
                PflTestKit.Unchecked("样本编队", "场景里 0 个活动编队 ⇒ 四个字段无从断言");
                return;
            }

            PflTestKit.Begin("可见性四字段（[P1]）", "00 验收");
            PflTestKit.Note($"活动编队 {activeCount} 个；样本 G{sample.GroupId}");

            // 主动开关的单调性（[V8] 的"开机即暴露"）——自造状态必须还原
            bool pingSaved = sample.ActivePingEnabled;
            sample.ActivePingEnabled = false;
            double sigOff = sample.SignalStrength;
            sample.ActivePingEnabled = true;
            double sigOn = sample.SignalStrength;
            sample.ActivePingEnabled = pingSaved;

            PflTestKit.Check(PflTestKit.Kind.Invariant, sigOff >= 0.0 && sigOn >= 0.0, "信号强度非负",
                $"关 {sigOff:F4} / 开 {sigOn:F4}");
            PflTestKit.Check(PflTestKit.Kind.Value, sigOn >= sigOff - 1e-9, "主动开关单调抬升信号",
                $"开 {sigOn:F4} ≥ 关 {sigOff:F4}");
            PflTestKit.Check(PflTestKit.Kind.Invariant, sample.ActivePingEnabled == pingSaved,
                "主动开关已还原（自造状态必须还回去）");

            // [V7] 侦测技术三分量（2026-09-24 起**可判**：它有写入者了）。
            // 判据 = **派生量与"两段来源之和"一致**：**部件级**（各实体 `AggregatedStats` 求和）
            //      + **建筑级**（`BuildingTechBonus`，[N7] 每物理步投影写入；2026-10-01 加）——
            //      这两段合起来才是 `RecomputeDerived` 的全部输入；
            //      + 若该编队**确实装了**传感器部件，则派生灵敏度必须 > 0（"给量纲"的正向验收）。
            // ⚠ 期望值从**部件事实**推出，不从"样本应该有什么"推 —— 样本是"场上第一个活动编队"，
            //   可能是导弹（按设计**不装**传感器），写死"必须有传感器"会假红。
            {
                var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
                float sumRange = 0f, sumEcm = 0f, sumEccm = 0f;
                int entities = 0;
                bool sawSensorPart = false;
                if (spaceGame != null)
                {
                    foreach (int eid in sample.GetEntityIds())
                    {
                        var e = spaceGame.GetEntityById(eid);
                        if (e == null) continue;
                        var st = e.AggregatedStats;
                        sumRange += st.sensorRange;
                        sumEcm += st.ecmResist;
                        sumEccm += st.eccmResist;
                        entities++;
                        foreach (var part in e.Parts)
                        {
                            if (part.isDestroyed) continue;
                            for (int i = 0; i < part.components.Count; i++)
                                if (part.components[i] is Components.SensorArrayComponentData) sawSensorPart = true;
                        }
                    }
                }
                var dt = sample.DetectTech;
                var bonus = sample.BuildingTechBonus;   // [V7]/[N7] 建筑级投影；没有建筑的编队恒为 0
                bool consistent =
                    System.Math.Abs(dt.sensitivityMeters - (sumRange + bonus.sensitivityMeters)) <= System.Math.Max(1f, sumRange * 1e-4f)
                    && System.Math.Abs(dt.ecmResist - (sumEcm + bonus.ecmResist)) <= 1e-3f
                    && System.Math.Abs(dt.eccmResist - (sumEccm + bonus.eccmResist)) <= 1e-3f;
                PflTestKit.Check(PflTestKit.Kind.Invariant, consistent,
                    "DetectTech = 各实体 AggregatedStats 之和 + 建筑级投影（部件级 + 建筑级两段，别只看实体）",
                    $"编队 灵{dt.sensitivityMeters:F0}m/抗压{dt.ecmResist:F2}/抗骗{dt.eccmResist:F2}｜实体求和 灵{sumRange:F0}m/抗压{sumEcm:F2}/抗骗{sumEccm:F2}｜建筑投影 灵{bonus.sensitivityMeters:F0}m/抗压{bonus.ecmResist:F2}/抗骗{bonus.eccmResist:F2}（{entities} 个实体）");
                if (sawSensorPart)
                {
                    PflTestKit.Check(PflTestKit.Kind.Value, dt.sensitivityMeters > 0f,
                        "装了传感器部件的编队 ⇒ 派生灵敏度必须 > 0（[V7] 给量纲：为 0 即链断了）",
                        $"灵 {dt.sensitivityMeters:F0} m（{entities} 个实体）");
                }
                else
                {
                    PflTestKit.Note("样本编队没有传感器部件（导弹按设计不装）⇒ 只验了『派生量 = 实体求和』那一半；" +
                        "正向那条要摆空间站或验收船（它们装了传感器阵列）");
                }
            }

            // ⚠ 这一条今天仍**立不起来**——显式登记，别让"用例通过"被读成"四字段都验过了"
            PflTestKit.CheckUnchecked("匿踪闸门 IsStealth 的感知后果",
                "消费方（传感器/接触层）未接线（待 P2）⇒ 今天只能读字段本身，读不出后果");
            PflTestKit.End();
        }
    }
}
