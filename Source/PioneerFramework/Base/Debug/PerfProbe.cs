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
    ///
    /// **为什么本类（以及 Base/Debug/ 全体）没有 #if DEBUG 隔离**（D5 决策，2026-09-14）：
    ///  · 本仓库的 #if DEBUG 是**有效**隔离（Release 不定义 DEBUG；已提交 DLL 里 DEBUG 独占字面量命中数 0）
    ///    ——但**刻意不**把它用在 Base/Debug/ 上；
    ///  · 理由：验收工具（探针 + 一键验收 + 自检电池 + 进度窗口）必须在**发布 DLL** 里可用——
    ///    游戏内回归正是跑发布 DLL 的 dev-mode Debug 菜单；
    ///  · 它们全部是**运行时门控**：探针挂在 Enabled（默认 false）之后、场景/自检挂在 dev-mode 菜单之后，
    ///    关闭时开销只是一次静态 bool 读。单机 mod 里"发布体积/攻击面"不构成理由（dev mode 本就是调试入口）。
    ///  · 因此"Base/Debug/ 不进 Release"是一条**被否掉的候选方案**，不是待办。
    ///  · 但边界要守住：**玩家可达的正式 UI 不放此目录**（Window_MissileControl / Window_Steering
    ///    已迁至 Base/Buildings/，见 D1）——Debug 目录只放开发者工具。
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
        private static readonly Dictionary<int, (object buffer, SpaceEntityGroup group)> sliceBufferByGroup
            = new Dictionary<int, (object buffer, SpaceEntityGroup group)>();

        /// <summary>观察到的 groupId 复用次数（id 分配"扫 max+1"的后果）——信息量，非缺陷。</summary>
        public static long GroupIdReuseObserved;
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
            /// <summary>组对象引用：id 复用（扫 max+1 的后果）时"同 id 不同组"不得跨组比较。</summary>
            public SpaceEntityGroup Group;
        }

        /// <summary>控制台/日志即时信息（非异常）。</summary>
        public static void Note(string tag, string detail) => Log.Message($"[性能探针][{tag}] {detail}");

        /// <summary>不变式被破坏：红字 + 按 tag 计数（前 10 次全打，之后每 100 次打一条，防刷屏）。</summary>
        /// <summary>全部 Mismatch 的总次数（兜底硬指标：任何不变式破坏都会计入，
        /// 包括尚无专属计数的域——如引力场子天体域与弹头引力）。</summary>
        public static long TotalMismatches;

        public static void Mismatch(string tag, string detail)
        {
            TotalMismatches++;
            long n = mismatchByTag.TryGetValue(tag, out var c) ? c + 1 : 1;
            mismatchByTag[tag] = n;
            if (n <= 10 || n % 100 == 0)
                Log.Error($"[性能探针][不一致][{tag}] 第{n}次 {detail}");
        }

        // ════════ 第 1 条：聚合器缓存 ════════

        public static void AggReuse(int groupId) => AggReused++;

        /// <summary>记录一次聚合器重建，并按上次状态归类原因（区分"该重建"与"不该重建"）。</summary>
        public static void AggRebuild(SpaceEntityGroup group, int entitySetVersion, int stamp, int entityCount)
        {
            int groupId = group.GroupId;
            bool known = aggLast.TryGetValue(groupId, out var prev) && ReferenceEquals(prev.Group, group);
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
                Group = group,
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
        /// <summary>
        /// 组级切片 scratch 复用检查。
        /// **按"组对象身份"而非仅 groupId 记忆**：id 分配是"扫 max+1"，销毁最大 id 的组后
        /// 新组会复用该 id（2026-09-14 验收阶段间清场正是这样制造了大量复用），
        /// 此时"同 id 不同组"会被误判成"scratch 身份变化"——那是**假警报**。
        /// 复用本身是真问题（计划 A5a：会影响弹头 sourceGroupId 免疫与 AVM targetGroupId 追踪），
        /// 故单独计数 IsReused 供观察，但不计入 mismatch。
        /// </summary>
        public static void SliceBuffer(SpaceEntityGroup group, object buffer)
        {
            int groupId = group.GroupId;
            SliceBufferChecks++;
            if (sliceBufferByGroup.TryGetValue(groupId, out var prev))
            {
                if (!ReferenceEquals(prev.group, group))
                {
                    GroupIdReuseObserved++;
                    sliceBufferByGroup[groupId] = (buffer, group);
                }
                else if (!ReferenceEquals(prev.buffer, buffer))
                {
                    Mismatch("切片缓冲", $"G{groupId} scratch 身份变化（{prev.buffer.GetHashCode()}→{buffer.GetHashCode()}）");
                }
            }
            else
            {
                sliceBufferByGroup[groupId] = (buffer, group);
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

        // ── 第 24 条：天体**局部**位置/速度 (bodyId,tick) 记忆化（系统系引力场的热路径）──
        // 与第 8 条分开计数：世界系缓存服务渲染/展示（低频），局部系缓存服务 A0 后的群组引力场
        // （每群组每步 × 父链每颗天体）。判据：未命中 ≈ 子天体数 × 步数（缓存前是 ×组数），
        // 即命中/未命中 ≈ 同一系统内**同时要场的组数 − 1**。
        public static long BodyLocalHits;
        public static long BodyLocalMisses;
        public static long BodyLocalShadowChecked, BodyLocalShadowMismatch;
        public static void NoteBodyLocalPosHit() => BodyLocalHits++;
        public static void NoteBodyLocalPosMiss() => BodyLocalMisses++;

        /// <summary>局部位置/速度缓存影子：命中时与无缓存重算值逐位比对（容差 0——同一份 Double 算式，
        /// 重算必然逐位相同；不等只可能是缓存陈旧）。</summary>
        public static void BodyLocalShadow(int bodyId, DVector3 cached, DVector3 fresh, double tick, string what)
        {
            BodyLocalShadowChecked++;
            double d = (cached - fresh).magnitude;
            if (d <= 0.0) return;
            BodyLocalShadowMismatch++;
            Mismatch("天体局部缓存", string.Format(
                "天体{0} {1} 命中值≠重算值 Δ={2:E3} m（t={3:F3}）——缓存陈旧？", bodyId, what, d, tick));
        }

        public static void DestroyedConsume(int groupId, int tick)
        {
            // 注：这里只有 id（调用点拿不到组对象），故 id 复用理论上仍可能假报"同一步销毁两次"。
            // 触发条件是"同一步内旧组销毁 + 新组复用同 id 再销毁"，实际极罕见；真出现时看日志里
            // 是否有 GroupIdReuseObserved 计数即可判定是复用而非重复销毁。
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
        // ── ㉑引力场（A0/A2，2026-09-14）──
        /// <summary>根域一致性采样次数：场(全部后代) vs 场(根 + 直接子天体)。</summary>
        public static long GravityRootChecked;
        /// <summary>✗ 根域两者不一致（天体层级收集/累加出错；当前数据无孙天体时应恒为 0）。</summary>
        public static long GravityRootMismatch;
        /// <summary>子天体域（如月球）差分采样次数。</summary>
        public static long GravitySubBodyProbes;
        /// <summary>子天体域最近一次「差分/局部引力」比值（预告：~0.1%–2% 是潮汐；~7% 说明走了全量和）。</summary>
        public static double GravitySubBodyLastRatio;
        public static double GravitySubBodyMinRatio = double.MaxValue;
        public static double GravitySubBodyMaxRatio = double.MinValue;
        /// <summary>子天体域最近一次差分的模长（m/s²）与局部引力模长（m/s²）。</summary>
        public static double GravitySubBodyLastDiff;
        public static double GravitySubBodyLastLocal;
        /// <summary>引力射弹的段重建次数 / 步数（比值应 ≈ 步长/2s：Combat 档 1/120）。</summary>
        public static long ProjectileSegments;
        public static long ProjectileSteps;
        /// <summary>弹头最近一次建段时的引力模长（m/s²）、"指向行星中心"余弦、以及 |a|/主导项（μ/r² 最大值）。</summary>
        public static double WarheadAccel;
        public static double WarheadTowardCenterCos;
        public static double WarheadAccelRatio;
        /// <summary>弹头建段采样次数（>0 = 确实有弹头飞过、A2 路径被走到）。</summary>
        public static long WarheadSegments;
        /// <summary>自由飞行弯曲量采样次数（>0 = 有射弹活过了 15 s，A2 的段内插值飞行被真正走到）。</summary>
        public static long FreeFlightBends;
        /// <summary>最近一次「实际偏移 / ½|a|t²」及其分量（应 ≈1×）。</summary>
        public static double FreeFlightLastRatio;
        public static double FreeFlightLastLinear;
        public static double FreeFlightLastExpected;

        private static long gravityProbeCounter;
        private static long warheadLogCounter;
        private static long freeFlightLogCounter;

        /// <summary>限频采样（每 64 次一次）——引力场是热路径，逐次采样自己会变成噪声源。</summary>
        public static bool ShouldProbeGravity() => (++gravityProbeCounter & 63) == 0;

        /// <summary>根域影子结果：match=false 即异常（字段级累加/层级枚举出错）。</summary>
        public static void GravityRootDomain(bool match, string detail)
        {
            GravityRootChecked++;
            if (!match) Mismatch("引力场根域", detail);
        }

        /// <summary>子天体域差分采样：报告「差分模长 / 局部引力模长」。
        /// **值判据**：≈0 = 旧"只算中心天体"行为（A0 未生效）；~7% = 走了"全量求和"错路；
        /// 潮汐正确值约 0.1%–0.6%。两种灾难性偏离直接记 Mismatch（→ 进硬指标）。</summary>
        public static void GravitySubBodyDomain(double diffMagnitude, double localGravity)
        {
            GravitySubBodyProbes++;
            GravitySubBodyLastDiff = diffMagnitude;
            GravitySubBodyLastLocal = localGravity;
            double ratio = localGravity > 1e-9 ? diffMagnitude / localGravity : 0.0;
            GravitySubBodyLastRatio = ratio;
            if (ratio < GravitySubBodyMinRatio) GravitySubBodyMinRatio = ratio;
            if (ratio > GravitySubBodyMaxRatio) GravitySubBodyMaxRatio = ratio;

            if (localGravity <= 1e-9) return;
            if (ratio < 1e-4)
                Mismatch("引力场子天体域", string.Format(
                    "差分近零（{0:P4}）：子天体周围未带上根天体贡献 —— A0 未生效？差分={1:E3} 局部={2:F3} m/s²",
                    ratio, diffMagnitude, localGravity));
            else if (ratio > 0.03)
                Mismatch("引力场子天体域", string.Format(
                    "差分为全量级（{0:P4}，潮汐应 0.1%–0.6%）：走了『全量和』错路 差分={1:E3} 局部={2:F3} m/s²",
                    ratio, diffMagnitude, localGravity));
        }

        /// <summary>
        /// 引力射弹的一步。**断言按弹、按物理时间**（不是按步数——步长随档位变：
        /// Combat 档 1/60 s、Normal 档 1 s，按步数算的比值会在 1x 档下凭空翻 60 倍）。
        /// 期望重建次数 = 飞行物理秒 / 段长；过频 = 段长没生效（"算得太勤"）。
        /// 注：首轮验收曾用**全局**段数/步数（481/26500 ≈ 1/55）判"过频"，实为 ~2.2 枚弹头并发所致。
        /// </summary>
        public static void NoteProjectileStep(int projId, int projRebuilds, double elapsedSeconds,
            double segmentLength, bool rebuiltThisStep)
        {
            // 全局聚合（信息量：看整体规模）——**注意别删**：验收阶段 8/8 的判据读的就是 ProjectileSegments。
            // （2026-09-14 教训：改写成按弹断言时把这两行删了，导致 8/8 判据永远拿到 0、阶段恒判"未覆盖"。）
            ProjectileSteps++;
            if (rebuiltThisStep) ProjectileSegments++;

            if (projRebuilds <= 0) return;

            // 按弹、按物理时间判频率（按步数会随档位翻 60 倍，见方法上方说明）
            double expected = segmentLength > 0.0 ? elapsedSeconds / segmentLength : 0.0;
            if (expected < 4.0) return;   // 段数太少，统计无意义

            if (projRebuilds > expected * 1.5 + 4.0)
                Mismatch("射弹引力段", string.Format(
                    "P{0} 段重建过频：{1} 段 / 应 ≈{2:F0} 段（飞行 {3:F1}s / 段长 {4:F1}s）—— 段长没生效？",
                    projId, projRebuilds, expected, elapsedSeconds, segmentLength));
        }

        /// <summary>
        /// 引力射弹的**自由飞行弯曲量**（定量值判据）：与"生成时的直线"相比偏了多少，
        /// 对照 ½·|a|·t²（圆轨上 a 大小不变、方向在几十秒内几乎不转，故该式精确到 &lt;0.1%）。
        /// 这条把"我要受真实应力的弹头"变成可判定的数：弯曲量若远小于该式 ⇒ 引力没真加上；
        /// 远大于 ⇒ 又是参考系错（旧实现 ~100× 主导项，会大出 4 个数量级）。
        /// **实战战绩（2026-09-14）**：Normal 档实测 3.99–5.16×，据此查出"段重建时 baseTick 取了
        /// 当前时刻而非状态时刻 → 每段丢一步运动"的真 bug（Combat 档 0.83%、Normal 档 33%）。
        /// </summary>
        public static void NoteFreeFlightBend(int projId, bool isWarhead, double elapsedSeconds,
            DVector3 spawnPos, DVector3 spawnVel, DVector3 pos, double accelMagnitude)
        {
            if (elapsedSeconds < 15.0 || accelMagnitude <= 1e-6) return;

            double linear = (pos - (spawnPos + spawnVel * elapsedSeconds)).magnitude;
            double expected = 0.5 * accelMagnitude * elapsedSeconds * elapsedSeconds;
            if (expected <= 1e-9) return;

            double ratio = linear / expected;
            FreeFlightBends++;
            FreeFlightLastRatio = ratio;
            FreeFlightLastLinear = linear;
            FreeFlightLastExpected = expected;

            if ((++freeFlightLogCounter % 8) != 1) return;
            Log.Message($"[性能探针][引力弯曲] {(isWarhead ? "弹头" : "射弹")} P{projId} t={elapsedSeconds:F1}s " +
                        $"实际偏移={linear:F1} m / ½|a|t²={expected:F1} m = {ratio:F2}×（应 0.8–1.3×）");
        }

        /// <summary>弹头建段时的引力快照 + **值判据**（限频打印：这是"真实应力"是否生效的直接读数）。
        /// accelRatio = |a| / 最近天体的主导项 μ/r² —— 参考系混用的旧实现是 ~100×（用恒星的 GM 除以行星系半径），
        /// 故 [0.2, 5] 的带宽足以致命地抓住它，又能容忍两体贡献相近/矢量相消的几何。</summary>
        public static void NoteWarheadSegment(double accelMagnitude, double towardCenterCos, double accelRatio)
        {
            WarheadAccel = accelMagnitude;
            WarheadTowardCenterCos = towardCenterCos;
            WarheadAccelRatio = accelRatio;
            WarheadSegments++;

            if (accelRatio > 0.0 && (accelRatio < 0.2 || accelRatio > 5.0))
                Mismatch("弹头引力", string.Format(
                    "引力偏离主导项 {0:F1}×（应在 0.2–5×）：|a|={1:F3} m/s² 指向行星中心余弦={2:F3}",
                    accelRatio, accelMagnitude, towardCenterCos));

            if ((++warheadLogCounter % 4) != 1) return;
            Log.Message($"[性能探针][弹头引力] |a|={accelMagnitude:F3} m/s²（主导项 {accelRatio:F2}×）" +
                        $" 指向行星中心余弦={towardCenterCos:F3}（旧实现此处 ≈100× 主导项——那是参考系混用的伪值）");
        }

        // ── ㉕实体注册表一致性（A5b）──
        /// <summary>一致性检查次数（每 600 tick 一次）。</summary>
        public static long EntityRegistryChecks;
        /// <summary>✗ 注册表里存在"不属于任何存活组"的实体（= 泄漏：离群时忘了注销）。</summary>
        public static long EntityRegistryLeaks;
        /// <summary>最近一次泄漏的样本 id（诊断用）。</summary>
        public static string? EntityRegistrySample;

        /// <summary>
        /// 注册表一致性：`entities` 必须恰好等于"各存活组 entityIds 的并集"。
        /// 不等 = 有实体离群却没注销 → 永久滞留（随档序列化）且 `GetEntityById(陈旧 id)` 会取回僵尸
        /// （质量/燃料/推进全错）。**钩子每 600 tick 跑一次**，与日志汇总频率无关，故验收期间静默期也能查。
        /// </summary>
        public static void EntityRegistry(int leakCount, string? sample)
        {
            EntityRegistryChecks++;
            if (leakCount <= 0) return;
            EntityRegistryLeaks += leakCount;
            EntityRegistrySample = sample;
            Mismatch("实体注册表", string.Format(
                "{0} 个实体不属于任何存活组（离群未注销 → 泄漏）：{1}", leakCount, sample));
        }

        // ════════ 第 25 条：每物理步墙钟 ════════
        // 为什么需要（2026-09-14 的教训）：核验"优化之后是否变差"时发现日志里**全是计数**
        // （reused/命中/不一致…），"帧时间没变"只能靠代码结构推断，给不出前后对比。
        // 口径：①整步 = SpaceGameComp 里一次 UpdateAllMovements（含操舵/冲量/群组物理/耗散）；
        //      ②引力场段 = CollectSystemSources + FieldAccelJerk（不含轨道保持修正，那部分很贵且
        //        只有白名单群组付；要单独看就再加一个累加器，别改动现有累加器含义）。
        // 读法：计数全部按 **Stopwatch tick**（原始时间戳差）累加，报告时才换算成 µs——避免每次
        //      换算的整除误差累积（Windows 上 Frequency=1e7，即 100ns 分辨率；单组引力段可能只有
        //      数 µs，所以分辨率这一项要在读数时心里有数）。
        // 注意：群组物理在 Parallel.For 里跑（组数 ≥ 阈值时），此时"引力场段"是**各组分段之和**，
        //      可以超过整步墙钟——那不是 bug，是并行。故读数里同时给出"引力段采样数/步"（= 平均组数），
        //      比较前后时用同一个固定场景，看的是趋势不是绝对值。
        public static long StepClocks;            // 计时的物理步数
        public static long StepTicksTotal;        // 全部步的墙钟总和（Stopwatch tick）
        public static long StepTicksMax;          // 最慢一步（尖峰的唯一读数来源）
        public static long StepTicksMaxAtStep;    // 最慢一步是第几步（1 起；用来定位尖峰发生在何时）
        public static long StepTicksMaxAtTick;    // 那一步对应的真实 tick（同上）
        public static long GravityTicksTotal;     // 引力场段总和（Stopwatch tick）
        public static long GravitySegments;       // 引力场段采样次数（= Σ 每组每子步一次）

        /// <summary>整步计时开始。**只在 PerfProbe.Enabled 时调用**（关闭时调用点直接跳过）。</summary>
        public static long StepClockStart() => System.Diagnostics.Stopwatch.GetTimestamp();

        /// <summary>整步计时结束（start = StepClockStart 的返回值）。记录最慢步**及其步序号/真实 tick**
        /// ——光知道"最慢 10 ms"没用，得知道它是场景部署那一步还是稳态里的某一步。</summary>
        public static void StepClockEnd(long start)
        {
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            StepClocks++;
            StepTicksTotal += dt;
            if (dt > StepTicksMax)
            {
                StepTicksMax = dt;
                StepTicksMaxAtStep = StepClocks;
                StepTicksMaxAtTick = Find.TickManager?.TicksGame ?? 0;
            }
        }

        /// <summary>引力场段计时开始（**只在 Enabled 时调用**）。</summary>
        public static long GravityClockStart() => System.Diagnostics.Stopwatch.GetTimestamp();

        /// <summary>引力场段计时结束（+= 原子累加：群组物理走在 Parallel.For 里，普通 ++ 会丢计数）。</summary>
        public static void GravityClockEnd(long start)
        {
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            System.Threading.Interlocked.Add(ref GravityTicksTotal, dt);
            System.Threading.Interlocked.Increment(ref GravitySegments);
        }

        /// <summary>Stopwatch tick → µs（报告侧换算）。</summary>
        private static double ToMicros(long stopwatchTicks)
            => stopwatchTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>第 25 条的读数行（µs/步 + 引力场段占比）。stepClocks 为 0 时返回"未采样"。</summary>
        public static string StepClockReport()
        {
            if (StepClocks <= 0) return "未采样（探针未开启或没跑到物理步）";
            double avg = ToMicros(StepTicksTotal) / StepClocks;
            double max = ToMicros(StepTicksMax);
            double gravPerStep = ToMicros(GravityTicksTotal) / StepClocks;
            double share = avg > 0.0 ? gravPerStep / avg : 0.0;
            double segPerStep = (double)GravitySegments / StepClocks;
            return string.Format(
                "步数={0} 平均={1:F1}µs/步 最慢={2:F1}µs（第{3}步 / t={4}）｜引力场段={5:F1}µs/步 占整步={6:P1}（采样 {7:F1} 段/步）",
                StepClocks, avg, max, StepTicksMaxAtStep, StepTicksMaxAtTick, gravPerStep, share, segPerStep);
        }

        public static void LogSummaryNow()        {
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

            string subBody = GravitySubBodyProbes > 0
                ? $"{GravitySubBodyLastRatio:P3}（范围 {GravitySubBodyMinRatio:P3}–{GravitySubBodyMaxRatio:P3}，差分={GravitySubBodyLastDiff:F4} 局部={GravitySubBodyLastLocal:F3} m/s²）"
                : "未采样";
            sb.Append($"\n㉑引力场(A0) 根域影子={GravityRootChecked} 不一致={GravityRootMismatch}｜子天体域差分={GravitySubBodyProbes} 次，差分/局部引力={subBody}");
            sb.Append($"\n㉓id 复用观察={GroupIdReuseObserved} 次（id 分配「扫 max+1」的后果；计划 A5a 的根因，非缺陷）");
            sb.Append($"\n㉒射弹引力段(A2) 段重建={ProjectileSegments} / 步={ProjectileSteps}（预期 ≈步数/120）｜弹头建段={WarheadSegments} 次 最近 |a|={WarheadAccel:F3} m/s²（主导项 {WarheadAccelRatio:F2}×，应 0.2–5×）指向行星中心余弦={WarheadTowardCenterCos:F3}");

            sb.Append($"\n㉔天体局部缓存(第24条) 命中={BodyLocalHits} 未命中={BodyLocalMisses}（未命中应 ≈ 子天体数×步数，不是 ×组数；命中/未命中 ≈ 组数−1）影子={BodyLocalShadowChecked} 不一致={BodyLocalShadowMismatch}");
            sb.Append($"\n㉖每物理步墙钟(第25条) {StepClockReport()}");

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
            BodyLocalHits = BodyLocalMisses = BodyLocalShadowChecked = BodyLocalShadowMismatch = 0;
            StepClocks = StepTicksTotal = StepTicksMax = GravityTicksTotal = GravitySegments = 0;
            StepTicksMaxAtStep = StepTicksMaxAtTick = 0;
            CompRefHits = CompRefMissResolved = CompRefMissNull = CompRefGameSwitched = 0;
            CompRefShadowChecked = CompRefShadowMismatch = 0;
            ExplGridQueries = ExplGridCandidates = ExplOldScanCandidates = ExplOldOnlyNoPartInRange = ExplMissedWithPartInRange = ExplShadowChecked = 0;
            RotInvHits = RotInvMisses = RotInvShadowChecked = RotInvShadowMismatch = 0;
            GravityRootChecked = GravityRootMismatch = 0;
            GravitySubBodyProbes = 0;
            GravitySubBodyLastRatio = GravitySubBodyLastDiff = GravitySubBodyLastLocal = 0.0;
            GravitySubBodyMinRatio = double.MaxValue;
            GravitySubBodyMaxRatio = double.MinValue;
            ProjectileSegments = ProjectileSteps = 0;
            WarheadAccel = WarheadTowardCenterCos = WarheadAccelRatio = 0.0;
            WarheadSegments = 0;
            FreeFlightBends = 0;
            FreeFlightLastRatio = FreeFlightLastLinear = FreeFlightLastExpected = 0.0;
            gravityProbeCounter = warheadLogCounter = freeFlightLogCounter = 0;
            TotalMismatches = 0;
            GroupIdReuseObserved = 0;
            EntityRegistryChecks = EntityRegistryLeaks = 0;
            EntityRegistrySample = null;
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
