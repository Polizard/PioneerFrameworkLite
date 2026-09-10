using System;
using System.Collections.Generic;
using System.Text;
using PioneerFramework.Combat;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 性能优化改动验收探针（2026-09-10；对应《性能优化待办2026-09-09》第 1/2/3/4/5/14/15/16/19 条）。
    ///
    /// 设计原则——不靠"看着像对"：
    ///  · **影子校验**：走缓存/复用路径的同时，用**旧的未缓存算法**再算一遍，逐字段比对；
    ///    任何不一致立即 Log.Error（红字）并计数。汇总里"不一致"必须全 0 才算验收通过。
    ///  · **计数可读**：命中/未命中/重建/候选数全部累计，限频打印一行汇总——用于确认优化真的生效
    ///    （reused 应远大于 rebuilt；hits 应远大于 misses），而不是退化成"每次都重建"。
    ///  · **零开销**：默认关闭（Enabled=false）；关闭时每个探针点只是一次静态 bool 读，无字符串拼接。
    ///
    /// 验收判据（汇总行内字段，全 0 才通过）：
    ///  ①agg.why-nochange、①agg.shadow-mismatch、②identity-change、③slice-shared/alias-mismatch、
    ///  ④double-destroy、⑤comp.shadow-mismatch、⑯rotinv.shadow-mismatch、⑮grid-miss(真实漏伤)、
    ///  ⑲gooding.cap-hits；另 ⑮grid-miss-info（旧球筛余量差异）>0 属正常，不代表漏伤。
    /// </summary>
    public static class PerfProbe
    {
        /// <summary>总开关（Gizmo 切换）。关闭时全部探针零开销。</summary>
        public static bool Enabled = false;

        /// <summary>影子校验开关：关掉后只统计计数与"无变更重建"等结构性异常，不做重算比对。</summary>
        public static bool ShadowChecks = true;

        /// <summary>汇总打印间隔（真实 tick）。</summary>
        public static int SummaryIntervalTicks = 300;

        // ── 第 1 条：群级推进聚合器缓存 ──
        public static long AggReused;                  // 命中缓存
        public static long AggRebuiltNoChange;         // ✗ 输入未变却重建（指纹不稳 / 缓存被清）
        public static long AggRebuiltEntitySet;        // 实体增删触发（预期）
        public static long AggRebuiltPropulsion;       // 推进数据变更触发（预期，如火箭注入）
        public static long AggRebuiltFirst;            // 首次构建（预期）
        public static long AggShadowChecked, AggShadowMismatch;

        // ── 第 2/3/4 条：每物理步 scratch 复用 ──
        public static long StepScratchStaleTail;       // 复用数组尾部被跳过的陈旧槽（正常 >0）
        public static long StepScratchRegrown;         // 数组正常扩容次数（组数增长，合法，信息量）
        public static long StepScratchIdentityChange;  // ✗ 容量够却仍重新分配（复用失效）
        public static long StepScratchChecked;
        public static long SliceBufferShared;          // ✗ 两个 group 共用同一 scratch（并行写会串）
        public static long SliceAliasMismatch;         // ✗ result.ActiveSlices 不是该组自己的 scratch
        public static long SliceBufferChecks;          // 组级切片 scratch 复用检查次数（燃烧路径走过的证据）
        public static long DestroyedLeftover;          // 复用字段在上一步遗留的项数（正常，信息量）
        public static long DestroyedDoubleDestroy;     // ✗ 同一步同一组被销毁两次
        public static long GroupsToCheckLeftoverTotal; // groupsToCheck 上一步遗留项累计（正常稳态，信息量）
        public static long ContactsResolved;           // 实际解算的船-船接触次数（碰撞链真跑到的证据；非异常）

        // ── 第 5/14 条：组件引用缓存 ──
        public static long CompRefHits, CompRefMissResolved, CompRefMissNull, CompRefGameSwitched;
        public static long CompRefShadowChecked, CompRefShadowMismatch;

        // ── 第 15 条：爆炸 AoE 走细网格 ──
        public static long ExplGridQueries;            // 走网格的爆炸查询次数
        public static long ExplGridCandidates;         // 网格给出的候选组总数
        public static long ExplOldScanCandidates;      // 旧算法（全场扫描 + 100km 球筛）会纳入的组数
        public static long ExplOldOnlyNoPartInRange;   // 仅旧算法纳入、实际无部件在半径内（正常，余量差异）
        public static long ExplMissedWithPartInRange;  // ✗ 真有部件在半径内却被网格漏掉（真实漏伤）
        public static long ExplShadowChecked;

        // ── 第 16 条：姿态逆四元数 per-tick 缓存 ──
        public static long RotInvHits, RotInvMisses, RotInvShadowChecked, RotInvShadowMismatch;

        // ── 第 19 条：Gooding 级数迭代上限（读 Gooding 纯静态计数）──
        public static long GoodingCalls, GoodingCapHits;
        public static int GoodingMaxIter;

        private static int nextSummaryTick;
        private static readonly Dictionary<string, long> mismatchByTag = new Dictionary<string, long>();
        private static readonly Dictionary<int, AggState> aggLast = new Dictionary<int, AggState>();
        private static readonly Dictionary<int, object> sliceBufferByGroup = new Dictionary<int, object>();
        private static readonly Dictionary<object, int> sliceGroupByBuffer = new Dictionary<object, int>();
        private static readonly Dictionary<int, long> destroyedSeen = new Dictionary<int, long>();
        private static readonly Dictionary<string, int> compShadowCounter = new Dictionary<string, int>();
        private static int rotInvShadowCounter;

        private struct AggState
        {
            public int EntitySetVersion;
            public int Stamp;
            public int Entities;
            public bool Built;   // 是否已建过（区分首次构建）
        }

        /// <summary>控制台/日志即时信息（非异常）。</summary>
        public static void Note(string tag, string detail) => Log.Message($"[性能探针][{tag}] {detail}");

        /// <summary>不变式被破坏：红字 + 按 tag 计数（前 10 次全打，之后每 100 次打一条，防刷屏）。</summary>
        public static void Mismatch(string tag, string detail)
        {
            long n = mismatchByTag.TryGetValue(tag, out var c) ? c + 1 : 1;
            mismatchByTag[tag] = n;
            if (n <= 10 || n % 100 == 0)
                Log.Error($"[性能探针][不一致][{tag}] 第{n}次 {detail}");
        }

        // ════════ 第 1 条：聚合器缓存 ════════

        public static void AggReuse(int groupId) => AggReused++;

        /// <summary>记录一次聚合器重建，并按上次状态归类原因（区分"该重建"与"不该重建"）。</summary>
        public static void AggRebuild(int groupId, int entitySetVersion, int stamp, int entityCount)
        {
            bool known = aggLast.TryGetValue(groupId, out var prev);
            if (!known)
            {
                AggRebuiltFirst++;
            }
            else if (prev.EntitySetVersion != entitySetVersion)
            {
                AggRebuiltEntitySet++;
            }
            else if (prev.Stamp != stamp)
            {
                AggRebuiltPropulsion++;
            }
            else
            {
                // 输入（实体集 + 各实体推进版本）完全未变却重建 → 指纹不稳定或缓存被外部清空
                AggRebuiltNoChange++;
                Mismatch("聚合器缓存", $"G{groupId} 实体集/推进版本均未变却重建：entities={prev.Entities}→{entityCount} ver={entitySetVersion} stamp={prev.Stamp}→{stamp}");
            }
            aggLast[groupId] = new AggState
            {
                EntitySetVersion = entitySetVersion,
                Stamp = stamp,
                Entities = entityCount,
                Built = true
            };
        }

        public static void AggShadowResult(bool ok, string? detail)
        {
            AggShadowChecked++;
            if (!ok)
            {
                AggShadowMismatch++;
                Mismatch("聚合器缓存", "影子比对不一致：" + detail);
            }
        }

        /// <summary>聚合器影子校验是否该跑（每 group 每 64 次命中一次，避免探针本身成为热点）。</summary>
        public static bool ShouldShadowAgg() => (AggReused & 63) == 0;

        // ════════ 第 2/3/4 条：每步 scratch ════════

        private static object? lastMovable, lastResults;
        private static int lastResultsLength;

        /// <summary>步首校验：容器身份稳定（说明确实在复用）+ 统计复用数组尾部的陈旧槽数。
        /// results 用 Array 接收：GroupPhysicsResult 是 SpaceGameComp 的私有嵌套结构，不为探针放宽可见性。
        /// 注意：组数增长时数组**扩容**是合法的（记 StepScratchRegrown），只有"容量够却仍被重新分配"才算复用失效
        /// （2026-09-10 首轮实测教训：把合法扩容当不变式破坏会刷红字）。</summary>
        public static void StepScratch(List<SpaceEntityGroup> movable, Array results, int liveCount)
        {
            StepScratchChecked++;
            if (lastMovable == null)
            {
                lastMovable = movable;
                lastResults = results;
                lastResultsLength = results.Length;
            }
            else
            {
                if (!ReferenceEquals(lastMovable, movable))
                {
                    StepScratchIdentityChange++;
                    Mismatch("每步scratch", "movableGroups 列表被重新分配（复用失效）");
                    lastMovable = movable;
                }
                if (!ReferenceEquals(lastResults, results))
                {
                    if (liveCount <= lastResultsLength)
                    {
                        StepScratchIdentityChange++;
                        Mismatch("每步scratch", string.Format(
                            "physicsResults 数组在容量足够（{0} ≥ 本步 {1}）时仍被重新分配", lastResultsLength, liveCount));
                    }
                    else
                    {
                        StepScratchRegrown++; // 正常扩容（信息量）
                    }
                    lastResults = results;
                    lastResultsLength = results.Length;
                }
            }

            if (results.Length > liveCount) StepScratchStaleTail++;
        }

        /// <summary>groupsToCheck 步首遗留项（信息量：>0 = 上一步确实填过且字段被复用）。
        /// 注意：遗留**不是**异常——每步顺序是「查遗留 → 清空 → 填充 → 消费」，检查点上看到的是上一步的项，
        /// 而那一步已在本步开始前消费完毕（首轮实测教训：曾把非零遗留误判为"填而未消费"而刷 FAIL）。</summary>
        public static void GroupsToCheckLeftover(int leftover) => GroupsToCheckLeftoverTotal += leftover;

        /// <summary>消费前校验：本步消费的 id 数不得超过本步处理的结果数
        /// （能真正抓到"清空失效 / 残留上一步陈旧 id 被重复消费"）。</summary>
        public static void GroupsToCheckConsume(int count, int liveCount)
        {
            if (count > liveCount)
                Mismatch("待检查集合", string.Format("本步消费 {0} 个 id > 本步结果数 {1}（清空失效或残留陈旧 id）", count, liveCount));
        }

        /// <summary>组级燃烧切片缓冲：校验"每组独占且身份恒定"，并统计复用次数。</summary>
        public static void SliceBuffer(int groupId, object buffer)
        {
            SliceBufferChecks++;
            if (sliceBufferByGroup.TryGetValue(groupId, out var prev))
            {
                if (!ReferenceEquals(prev, buffer))
                    Mismatch("切片缓冲", $"G{groupId} scratch 身份变化（{prev.GetHashCode()}→{buffer.GetHashCode()}）");
            }
            else
            {
                sliceBufferByGroup[groupId] = buffer;
            }

            if (sliceGroupByBuffer.TryGetValue(buffer, out int owner))
            {
                if (owner != groupId)
                {
                    SliceBufferShared++;
                    Mismatch("切片缓冲", $"G{owner} 与 G{groupId} 共用同一 scratch 对象（并行区会互相覆盖）");
                }
            }
            else
            {
                sliceGroupByBuffer[buffer] = groupId;
            }
        }

        public static void SliceAlias(bool ok, string detail)
        {
            if (!ok)
            {
                SliceAliasMismatch++;
                Mismatch("切片缓冲", "ActiveSlices 非本组 scratch：" + detail);
            }
        }

        /// <summary>待销毁导弹组缓冲：步首遗留项数（字段复用的正常现象，>0 即证明复用生效）。</summary>
        public static void DestroyedLeftoverReport(int leftover) => DestroyedLeftover += leftover;

        /// <summary>船-船接触解算（SeparatePositions 收口调用）：碰撞检测链真的解算到接触的证据。</summary>
        public static void NoteContact() => ContactsResolved++;

        // ── 第 7/10 条（中低优先级批）：省下的分配/查找次数（仅观测量，非判据）──
        public static long EmptyWeaponListHits;   // 第 7 条：走共享空实例的次数 = 省下的空 List 分配
        public static long RecordLookupsAvoided;  // 第 10 条：扫掠未命中而对早退省下的 GetRecord 次数

        public static void NoteEmptyWeaponListHit() => EmptyWeaponListHits++;
        public static void NoteRecordLookupsAvoided(int count) => RecordLookupsAvoided += count;

        // ── 第 8 条：天体世界位置/速度 (bodyId,tick) 记忆化 ──
        public static long BodyPosHits;   // 命中 = 省下的一次递归父链 + 每级 Cos/Sin
        public static long BodyPosMisses;
        public static void NoteBodyPosHit() => BodyPosHits++;
        public static void NoteBodyPosMiss() => BodyPosMisses++;

        public static void DestroyedConsume(int groupId, int tick)
        {
            if (destroyedSeen.TryGetValue(groupId, out long lastTick) && lastTick == tick)
            {
                DestroyedDoubleDestroy++;
                Mismatch("待销毁集合", $"G{groupId} 同一步(t={tick})被销毁两次");
            }
            destroyedSeen[groupId] = tick;
        }

        // ════════ 第 5/14 条：组件引用缓存 ════════

        public static void CompRefHit(string typeName) => CompRefHits++;

        public static void CompRefMiss(string typeName, bool found)
        {
            if (found) CompRefMissResolved++;
            else CompRefMissNull++;
        }

        public static void CompRefGameSwitch(string typeName) => CompRefGameSwitched++;

        /// <summary>是否做组件缓存影子校验（前 2 次必查 + 之后每 512 次）。
        /// ⚠️ 调用方必须先问本方法、再执行实时查找——否则被优化的线性扫描每次都跑，探针自身成为热点。</summary>
        public static bool ShouldShadowCompRef(string typeName)
        {
            int n = compShadowCounter.TryGetValue(typeName, out var c) ? c + 1 : 1;
            compShadowCounter[typeName] = n;
            return n <= 2 || (n & 511) == 0;
        }

        /// <summary>影子校验：缓存引用必须等于实时 GetComponent。</summary>
        public static void CompRefShadow(string typeName, GameComponent cached, GameComponent fresh)
        {
            CompRefShadowChecked++;
            if (!ReferenceEquals(cached, fresh))
            {
                CompRefShadowMismatch++;
                Mismatch("组件缓存", $"{typeName} 缓存={cached} 实时={fresh}（跨局未失效或组件被替换）");
            }
        }

        // ════════ 第 15 条：爆炸 AoE 走网格 ════════

        public static void ExplosionQuery(string tag, int frameId, double R, int gridCandidates)
        {
            ExplGridQueries++;
            ExplGridCandidates += gridCandidates;
            if (ExplGridQueries <= 8 || (ExplGridQueries & 15) == 0)
            {
                int perAxis = (int)(2.0 * R / CombatGrid.FINE_CELL_SIZE) + 2; // 覆盖格数上限（R 内格数，非全场）
                Note("爆炸网格", $"{tag} frame={frameId} R={R:F0}m 立方体覆盖格数≤{perAxis * perAxis * perAxis} 网格候选={gridCandidates}");
            }
        }

        /// <summary>是否对本次爆炸做影子全场扫描（前 8 次 + 之后每 16 次）。</summary>
        public static bool ShouldShadowExplosion() => ExplGridQueries <= 8 || (ExplGridQueries & 15) == 0;

        public static void ExplosionShadowResult(string tag, int oldScanCount, int oldOnlyCount, int missedWithPartInRange, string detail)
        {
            ExplShadowChecked++;
            ExplOldScanCandidates += oldScanCount;
            ExplOldOnlyNoPartInRange += oldOnlyCount;
            if (missedWithPartInRange > 0)
            {
                ExplMissedWithPartInRange += missedWithPartInRange;
                Mismatch("爆炸网格", $"{tag} 有 {missedWithPartInRange} 个组的部件在半径内却被网格漏掉（真实漏伤）：{detail}");
            }
            else
            {
                Note("爆炸影子", $"{tag} 旧扫候选={oldScanCount} 仅旧扫有={oldOnlyCount} 漏伤={missedWithPartInRange} ✓（仅旧扫有 = 旧球筛 100km 余量的保守差异，实际无部件在半径内）");
            }
        }

        // ════════ 第 16 条：逆四元数缓存 ════════

        public static void RotInvHit() => RotInvHits++;
        public static void RotInvMiss() => RotInvMisses++;

        /// <summary>影子校验节流：每 256 次调用比对一次（实算是纯数学，很便宜）。</summary>
        public static bool ShouldShadowRotInv() => (++rotInvShadowCounter & 255) == 0;

        public static void RotInvShadow(bool identical, string detail)
        {
            RotInvShadowChecked++;
            if (!identical)
            {
                RotInvShadowMismatch++;
                Mismatch("逆四元数缓存", "缓存值与实时重算不一致（缓存陈旧）：" + detail);
            }
        }

        // ════════ 汇总 ════════

        /// <summary>验收器出结论时强制打印一次汇总（不受限频约束）。</summary>
        public static void LogSummaryNow()
        {
            if (!Enabled) return;
            nextSummaryTick = 0;
            MaybeLogSummary();
        }

        public static void MaybeLogSummary()
        {
            if (!Enabled) return;
            int tick = Find.TickManager?.TicksGame ?? 0;
            if (tick < nextSummaryTick) return;
            nextSummaryTick = tick + Math.Max(60, SummaryIntervalTicks);

            GoodingCalls = Gooding.DebugTlambCalls;
            GoodingCapHits = Gooding.DebugSeriesCapHits;
            GoodingMaxIter = Gooding.DebugMaxSeriesIter;

            var sb = new StringBuilder();
            sb.Append($"[性能探针] t={tick} 判据=下列 mismatch/miss 全 0");

            sb.Append($"\n①聚合器 reused={AggReused} rebuilt(首次={AggRebuiltFirst} 实体集={AggRebuiltEntitySet} 推进={AggRebuiltPropulsion} 无变更={AggRebuiltNoChange}) 影子={AggShadowChecked} 不一致={AggShadowMismatch}");

            sb.Append($"\n②③④scratch 步校验={StepScratchChecked} 身份变化={StepScratchIdentityChange} 扩容={StepScratchRegrown} 陈旧尾跳过={StepScratchStaleTail} 切片检查={SliceBufferChecks} 切片共用={SliceBufferShared} 别名错={SliceAliasMismatch} 待销毁遗留={DestroyedLeftover} 重复销毁={DestroyedDoubleDestroy} 待检查遗留={GroupsToCheckLeftoverTotal}（正常稳态） 接触解算={ContactsResolved}");

            sb.Append($"\n⑤⑭组件缓存 命中={CompRefHits} 实查={CompRefMissResolved} 未注册={CompRefMissNull} 换局={CompRefGameSwitched} 影子={CompRefShadowChecked} 不一致={CompRefShadowMismatch}");

            sb.Append($"\n⑮爆炸网格 查询={ExplGridQueries} 网格候选={ExplGridCandidates} 旧扫候选={ExplOldScanCandidates} 仅旧扫有={ExplOldOnlyNoPartInRange} 漏伤={ExplMissedWithPartInRange} 影子={ExplShadowChecked}");

            sb.Append($"\n⑯逆四元数 命中={RotInvHits} 未命中={RotInvMisses} 影子={RotInvShadowChecked} 不一致={RotInvShadowMismatch}");

            sb.Append($"\n⑲Gooding 调用={GoodingCalls} 迭代上限命中={GoodingCapHits} 实测最大迭代={GoodingMaxIter}(上限 {Gooding.MAX_SERIES_ITER})");

            Log.Message(sb.ToString());
        }

        public static void Reset()
        {
            AggReused = AggRebuiltNoChange = AggRebuiltEntitySet = AggRebuiltPropulsion = AggRebuiltFirst = 0;
            AggShadowChecked = AggShadowMismatch = 0;
            StepScratchStaleTail = StepScratchIdentityChange = StepScratchChecked = StepScratchRegrown = 0;
            SliceBufferShared = SliceAliasMismatch = DestroyedLeftover = DestroyedDoubleDestroy = 0;
            SliceBufferChecks = 0;
            GroupsToCheckLeftoverTotal = 0;
            ContactsResolved = 0;
            EmptyWeaponListHits = 0;
            RecordLookupsAvoided = 0;
            BodyPosHits = BodyPosMisses = 0;
            CompRefHits = CompRefMissResolved = CompRefMissNull = CompRefGameSwitched = 0;
            CompRefShadowChecked = CompRefShadowMismatch = 0;
            ExplGridQueries = ExplGridCandidates = ExplOldScanCandidates = ExplOldOnlyNoPartInRange = ExplMissedWithPartInRange = ExplShadowChecked = 0;
            RotInvHits = RotInvMisses = RotInvShadowChecked = RotInvShadowMismatch = 0;
            mismatchByTag.Clear();
            aggLast.Clear();
            sliceBufferByGroup.Clear();
            sliceGroupByBuffer.Clear();
            destroyedSeen.Clear();
            compShadowCounter.Clear();
            lastMovable = lastResults = null;
            nextSummaryTick = 0;
        }
    }
}
