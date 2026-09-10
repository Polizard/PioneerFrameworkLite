using System;
using System.Collections.Generic;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 游戏内「一键验收」（2026-09-10；覆盖《性能优化待办2026-09-09》第 1/2/3/4/5/14/15/16/19 条）。
    /// 由 Debug 菜单「PFL 测试」页签（DebugTabMenu_PFL）一个按钮启动，
    /// 之后每 tick 由 SpaceGameComp.GameComponentTick 推进。
    ///
    /// 与 BurnArchitectureTest 同一套路：相位状态机 + 每阶段超时 + PASS/FAIL 累积 + 结论打印。
    /// 与探针（PerfProbe）的关系：探针负责在**每次**走缓存/复用路径时影子校验并计数；
    /// 本验收器负责**自动按顺序部署场景**，并在每个阶段用"计数增量"证明该阶段真的走到了目标代码路径
    /// （否则该阶段判"未覆盖"，不算通过）——没有证据的通过等于没测。
    ///
    /// 阶段与判据（增量 > 0 = 该条被覆盖）：
    ///   1/5 弹群齐射→解离 N=8   ①聚合器 reused + ⑯逆四元数 命中        （SpawnVolleyImpactTest）
    ///   2/5 撞击测试 1.5km      ⑮爆炸网格查询（弹头 AoE 必然走网格）      （SpawnMissileImpactTest）
    ///   3/5 炮塔对射            ⑤组件缓存命中 + ⑯逆四元数 命中           （SpawnTurretTest）
    ///   4/5 无Map碰撞           接触解算（碰撞链真的解算到接触）          （SpawnCollisionTest）
    ///   5/5 主推轴向+RCS 燃烧   ③组级燃烧切片 scratch 复用检查            （SpawnAxialThrustTest）
    /// 硬指标（任一非 0 即整体 FAIL）：①无变更重建/影子不一致 ②容器身份变化 ③切片共用/别名错
    ///   ④重复销毁/未消费 ⑤组件缓存不一致 ⑮网格漏伤 ⑯逆四元数陈旧 ⑲Gooding 触顶。
    /// ⑲（Gooding）不在游戏内触发退化几何，由纯测试强制覆盖（GoodingTests：180 组几何 + NaN 必抛不挂死）。
    ///
    /// 注意：本验收器会部署调试船/建筑（与面板上手动点各场景一致），会解暂停并切 1x；
    /// 跨场景的残留船可能互相干扰，因此各阶段判据只用"计数增量"这类不依赖单场景纯净度的事实。
    /// </summary>
    public static class PerfAcceptanceRunner
    {
        private enum Stage { Idle, Volley, MergedBurn, Turret, Collision, Thrust, Verdict }

        /// <summary>每阶段上限（真实 tick；1x 下约 90 秒）。超时判"未覆盖"，不判失败。</summary>
        private const int PHASE_TIMEOUT_TICKS = 5400;
        private const int PHASE_COUNT = 5;

        private static Stage stage = Stage.Idle;
        private static int stageStartTick;
        private static int nextProgressLogTick;


        private static readonly List<string> passes = new List<string>();
        private static readonly List<string> uncovered = new List<string>();

        // 阶段起点计数（增量 = 该阶段是否真走到目标代码路径）
        private static long bAggReused, bRotInv, bCompRefHit, bExplQuery, bSliceChecks, bContacts;

        public static bool Running => stage != Stage.Idle;

        /// <summary>面板状态行文案（面板打开时每帧一次，仅运行时才拼串）。</summary>
        public static string StatusText
        {
            get
            {
                if (stage == Stage.Idle) return "未运行";
                int el = CurTick - stageStartTick;
                return string.Format("{0}（t+{1}/{2}）证据增量：①{3} ⑯{4} ⑤{5} ⑮{6} ③{7} 接触{8}",
                    StageName(stage), el, PHASE_TIMEOUT_TICKS,
                    PerfProbe.AggReused - bAggReused, PerfProbe.RotInvHits - bRotInv,
                    PerfProbe.CompRefHits - bCompRefHit, PerfProbe.ExplGridQueries - bExplQuery,
                    PerfProbe.SliceBufferChecks - bSliceChecks, PerfProbe.ContactsResolved - bContacts);
            }
        }

        private static int CurTick => Find.TickManager?.TicksGame ?? 0;

        private static string StageName(Stage s)
        {
            switch (s)
            {
                case Stage.Volley: return "1/5 弹群齐射→解离→弹头AoE（⑮）";
                case Stage.MergedBurn: return "2/5 合并态弹群燃烧（①聚合器）";
                case Stage.Turret: return "3/5 炮塔对射（⑤⑯）";
                case Stage.Collision: return "4/5 无Map碰撞（接触解算）";
                case Stage.Thrust: return "5/5 主推轴向+RCS 燃烧（②③）";
                case Stage.Verdict: return "出结论";
                default: return s.ToString();
            }
        }

        /// <summary>一键启动：开探针 → 依次跑 5 个已验证场景 → 自动出结论。
        /// 入口：Debug 菜单 →「PFL 测试」页签 / 旧调试建筑的兼容 Gizmo。</summary>
        public static void Run()
        {
            if (Running)
            {
                Log.Warning("[验收] 已在运行中（先点「中止验收」）");
                return;
            }


            passes.Clear();
            uncovered.Clear();

            // 探针：开启 + 清零，保证结论里的每个数字都属于本次验收
            PerfProbe.Enabled = true;
            PerfProbe.ShadowChecks = true;
            PerfProbe.Reset();
            PerfProbe.SummaryIntervalTicks = 100000000; // 运行期间用面板状态行看进度，避免日志刷屏；结论时恢复

            // 1x：物理时间轴与真实 tick 一致，阶段超时才有意义（与 ArchTest 同款处理）
            Current.Game?.GetComponent<TimeScaleManager>()?.SetNormalMode();

            Log.Message("[验收] ═══ 性能优化一键验收开始（探针已开启，影子校验全开）═══");
            Log.Message("[验收] 说明：每阶段自动部署场景；『未覆盖』= 超时内没观测到证据（通常场景还没打完），不代表失败；"
                + "任何 [性能探针][不一致] 红字 = 真实缺陷。");
            Log.Message("[验收] 提示：不想等满 5 阶段（尤其阶段 4 无Map碰撞要等船漂到接触）可随时点「■ 中止验收」——"
                + "它会**立即打印结论与全部计数**；每 600 tick 也会打一行进度含累计计数，所以中途关游戏也能看到数据。");
            Log.Message(string.Format("[验收] 起始组数={0}（结论里对比，用于确认场景确实部署了）", GroupCount()));
            EnterStage(Stage.Volley);
        }

        /// <summary>中止：立即出结论（保留已跑到的证据）。</summary>
        public static void Abort()
        {
            if (!Running) return;
            Log.Warning("[验收] 已被玩家中止，直接出结论");
            PrintVerdict();
        }

        private static void EnterStage(Stage s)
        {
            stage = s;
            stageStartTick = CurTick;
            nextProgressLogTick = CurTick + 600;
            bAggReused = PerfProbe.AggReused;
            bRotInv = PerfProbe.RotInvHits;
            bCompRefHit = PerfProbe.CompRefHits;
            bExplQuery = PerfProbe.ExplGridQueries;
            bSliceChecks = PerfProbe.SliceBufferChecks;
            bContacts = PerfProbe.ContactsResolved;

            switch (s)
            {
                case Stage.Volley:
                    Log.Message("[验收] 【1/5】弹群齐射→解离 N=8（覆盖第 15 条：弹头 AoE 走细网格；顺带跑通制导/解离/每步 scratch）");
                    DebugShipSpawner.SpawnVolleyImpactTest();
                    break;
                case Stage.MergedBurn:
                    Log.Message("[验收] 【2/5】合并态弹群燃烧（覆盖第 1 条：多实体组聚合器缓存）");
                    DebugShipSpawner.SpawnVolleyImpactTest();
                    QueueMergedVolleyBurn(); // 同 tick 内保住合并态并排机动（见方法注释）
                    break;
                case Stage.Turret:
                    Log.Message("[验收] 【3/5】炮塔对射（覆盖第 5/14/16 条：组件缓存 + 命中几何）");
                    DebugShipSpawner.SpawnTurretTest();
                    break;
                case Stage.Collision:
                    Log.Message("[验收] 【4/5】无 Map 碰撞（覆盖第 2/4 条：每步容器复用 + 接触解算链）");
                    DebugShipSpawner.SpawnCollisionTest();
                    break;
                case Stage.Thrust:
                    Log.Message("[验收] 【5/5】主推轴向 + RCS 燃烧（覆盖第 1/2/3 条燃烧路径）");
                    DebugShipSpawner.SpawnAxialThrustTest();
                    break;
                case Stage.Verdict:
                    PrintVerdict();
                    break;
            }
        }

        /// <summary>由 SpaceGameComp.GameComponentTick 每 tick 驱动。</summary>
        public static void Tick()
        {
            if (!Running || stage == Stage.Verdict) return;

            bool timedOut = CurTick - stageStartTick >= PHASE_TIMEOUT_TICKS;

            // 进度日志（每 600 tick）：即使玩家没开面板也能从日志看出"在等什么"，避免误以为卡死
            if (CurTick >= nextProgressLogTick)
            {
                nextProgressLogTick = CurTick + 600;
                // 累计量也一并打印：这样即使半途关游戏（没跑到结论），也能看出各条是否真被走到
                Log.Message(string.Format("[验收] 进行中：{0} t+{1}/{2} 本阶段增量：①{3} ⑯{4} ⑤{5} ⑮{6} ③{7} 接触{8}"
                    + "｜累计：①reused={9} ⑯命中={10} ⑤命中={11} ⑮网格={12} ⑦空武器表={13} ⑩省查找={14} ⑧天体位={15}/{16}",
                    StageName(stage), CurTick - stageStartTick, PHASE_TIMEOUT_TICKS,
                    PerfProbe.AggReused - bAggReused, PerfProbe.RotInvHits - bRotInv,
                    PerfProbe.CompRefHits - bCompRefHit, PerfProbe.ExplGridQueries - bExplQuery,
                    PerfProbe.SliceBufferChecks - bSliceChecks, PerfProbe.ContactsResolved - bContacts,
                    PerfProbe.AggReused, PerfProbe.RotInvHits, PerfProbe.CompRefHits, PerfProbe.ExplGridQueries,
                    PerfProbe.EmptyWeaponListHits, PerfProbe.RecordLookupsAvoided,
                    PerfProbe.BodyPosHits, PerfProbe.BodyPosMisses));
            }

            switch (stage)
            {
                case Stage.Volley:
                    // 判据只用该场景**能**走到的路径：⑮弹头 AoE（导弹命中必然触发）。
                    // 曾把 ⑯（逆四元数，只在射弹/激光命中几何里调用）写进本阶段，导致阶段永不满足——教训记于此。
                    if (PerfProbe.ExplGridQueries > bExplQuery)
                    {
                        Pass(string.Format("⑮ 弹群命中 → 弹头 AoE 走细网格 +{0} 次（漏伤判据见硬指标；附本阶段 ①聚合器 reused+{1}）",
                            PerfProbe.ExplGridQueries - bExplQuery, PerfProbe.AggReused - bAggReused));
                        EnterStage(Stage.MergedBurn);
                    }
                    else if (timedOut) TimeoutStage("未观测到『弹头 AoE 走网格』（导弹可能未命中）");
                    break;

                case Stage.MergedBurn:
                    if (PerfProbe.AggReused > bAggReused)
                    {
                        Pass(string.Format("① 合并态弹群（多实体组 N=8）群级燃烧走聚合器缓存 reused+{0}",
                            PerfProbe.AggReused - bAggReused));
                        EnterStage(Stage.Turret);
                    }
                    else if (timedOut) TimeoutStage("未观测到『聚合器缓存命中』（合并态弹群可能未燃起来或已解离）");
                    break;

                case Stage.Turret:
                    if (PerfProbe.CompRefHits > bCompRefHit && PerfProbe.RotInvHits > bRotInv)
                    {
                        Pass(string.Format("⑤⑯ 对射：组件缓存命中+{0}，命中几何走逆四元数缓存 +{1}",
                            PerfProbe.CompRefHits - bCompRefHit, PerfProbe.RotInvHits - bRotInv));
                        EnterStage(Stage.Collision);
                    }
                    else if (timedOut) TimeoutStage("未观测到『组件缓存命中 + 命中几何缓存命中』（炮塔可能未开火）");
                    break;

                case Stage.Collision:
                    // 该场景是低速靠近（25~30 m/s，低于爆炸阈值）→ 判据用"接触解算"，不要求爆炸
                    if (PerfProbe.ContactsResolved > bContacts)
                    {
                        Pass(string.Format("②④ 接触解算 +{0} 次（碰撞链跑到 SeparatePositions；"
                            + "爆炸走网格由上一阶段已覆盖）", PerfProbe.ContactsResolved - bContacts));
                        EnterStage(Stage.Thrust);
                    }
                    else if (timedOut) TimeoutStage("未观测到『接触解算』（三条船可能未接触）");
                    break;

                case Stage.Thrust:
                    if (PerfProbe.SliceBufferChecks > bSliceChecks)
                    {
                        Pass(string.Format("②③ 燃烧路径走到组级 scratch 复用检查 +{0} 次（物理步 scratch 身份/别名见硬指标）",
                            PerfProbe.SliceBufferChecks - bSliceChecks));
                        EnterStage(Stage.Verdict);
                    }
                    else if (timedOut) TimeoutStage("未观测到『组级燃烧切片复用』（可能未点火）");
                    break;
            }
        }

        private static void TimeoutStage(string why)
        {
            uncovered.Add(StageName(stage) + "：" + why + string.Format("（超时 {0} tick）", PHASE_TIMEOUT_TICKS));
            Log.Warning("[验收] " + StageName(stage) + " 未覆盖——" + why + "。继续下一阶段。");
            switch (stage)
            {
                case Stage.Volley: EnterStage(Stage.MergedBurn); break;
                case Stage.MergedBurn: EnterStage(Stage.Turret); break;
                case Stage.Turret: EnterStage(Stage.Collision); break;
                case Stage.Collision: EnterStage(Stage.Thrust); break;
                case Stage.Thrust: EnterStage(Stage.Verdict); break;
            }
        }

        private static void Pass(string msg)
        {
            passes.Add(msg);
            Log.Message("[验收] PASS: " + msg);
        }

        private static void PrintVerdict()
        {
            stage = Stage.Verdict;

            // ── 硬指标：探针的任何"不变式被破坏"计数都必须为 0 ──
            var hard = new List<string>();
            Check(hard, PerfProbe.AggRebuiltNoChange, "①无变更却重建聚合器");
            Check(hard, PerfProbe.AggShadowMismatch, "①聚合器影子不一致");
            Check(hard, PerfProbe.StepScratchIdentityChange, "②每步 scratch 容器身份变化");
            Check(hard, PerfProbe.SliceBufferShared, "③两组共用切片 scratch");
            Check(hard, PerfProbe.SliceAliasMismatch, "③ActiveSlices 非本组 scratch");
            Check(hard, PerfProbe.DestroyedDoubleDestroy, "④同一步重复销毁");
            // 注：groupsToCheck 的"步首遗留"是正常稳态（本步填/本步消费/下一步才清），不是硬指标；
            // 真正的越界（消费数 > 本步结果数）在探针内直接记 Mismatch。
            Check(hard, PerfProbe.CompRefShadowMismatch, "⑤组件缓存与实时查找不一致");
            Check(hard, PerfProbe.ExplMissedWithPartInRange, "⑮爆炸走网格漏伤（有部件在半径内却未命中）");
            Check(hard, PerfProbe.RotInvShadowMismatch, "⑯逆四元数缓存陈旧");
            Check(hard, Gooding.DebugSeriesCapHits, "⑲Gooding 级数迭代触顶");

            bool volleyCovered = Covered("⑮");      // 弹群命中 → 弹头 AoE 走网格
            bool mergedCovered = Covered("①");      // 合并态弹群燃烧 → 聚合器缓存
            bool turretCovered = Covered("⑤");      // 组件缓存 + 命中几何
            bool collisionCovered = Covered("②④"); // 接触解算
            bool thrustCovered = Covered("②③");    // 组级燃烧切片复用
            int covered = (volleyCovered ? 1 : 0) + (mergedCovered ? 1 : 0) + (turretCovered ? 1 : 0)
                + (collisionCovered ? 1 : 0) + (thrustCovered ? 1 : 0);

            Log.Message("[验收] ═══════════ 性能优化验收结论 ═══════════");
            Log.Message(string.Format("[验收] 硬指标（不变式）：{0}",
                hard.Count == 0 ? "全部为 0 ✓" : hard.Count + " 项非 0 ✗"));
            foreach (var h in hard) Log.Error("[验收] 硬指标失败：" + h);

            Log.Message(string.Format("[验收] 覆盖度 {0}/{1}：弹群AoE⑮{2} 合并态聚合器①{3} 对射⑤⑯{4} 碰撞{5} 燃烧②③{6}",
                covered, PHASE_COUNT,
                volleyCovered ? "✓" : "未覆盖", mergedCovered ? "✓" : "未覆盖",
                turretCovered ? "✓" : "未覆盖", collisionCovered ? "✓" : "未覆盖",
                thrustCovered ? "✓" : "未覆盖"));
            Log.Message("[验收] ⑲Gooding：不在游戏内触发退化几何，由纯测试强制覆盖"
                + "（GoodingTests：180 组几何扫描确认上限不误杀 + NaN 必抛不挂死）");
            foreach (var u in uncovered) Log.Warning("[验收] 未覆盖：" + u);

            // ── 优化是否真的生效（而不是退化成每次都重建 / 完全没走缓存）──
            long rebuilt = PerfProbe.AggRebuiltFirst + PerfProbe.AggRebuiltEntitySet
                + PerfProbe.AggRebuiltPropulsion + PerfProbe.AggRebuiltNoChange;
            Log.Message(string.Format("[验收] 生效证据：①聚合器 reused={0} / rebuilt={1}（reused 应远大于 rebuilt）；"
                + "⑤组件缓存 命中={2} / 实查={3}；⑯逆四元数 命中={4} / 未命中={5}；⑮爆炸网格查询={6}",
                PerfProbe.AggReused, rebuilt, PerfProbe.CompRefHits, PerfProbe.CompRefMissResolved,
                PerfProbe.RotInvHits, PerfProbe.RotInvMisses, PerfProbe.ExplGridQueries));
            Log.Message(string.Format("[验收] 中低优先批（7/8/10 条）生效证据：⑦共享空武器表命中={0}（= 省下的空 List 分配）；"
                + "⑩扫掠早退省下 GetRecord={1} 次；⑧天体位置记忆化 命中={2} / 未命中={3}（命中 = 省下的递归父链 + 每级 Cos/Sin）",
                PerfProbe.EmptyWeaponListHits, PerfProbe.RecordLookupsAvoided, PerfProbe.BodyPosHits, PerfProbe.BodyPosMisses));
            Log.Message(string.Format("[验收] ② 物理步 scratch 校验={0} 次、接触解算={1} 次（前者 >0 = 每物理步都在校验容器复用；"
                + "后者 >0 = 碰撞链真的解算到接触）", PerfProbe.StepScratchChecked, PerfProbe.ContactsResolved));
            Log.Message(string.Format("[验收] ⑮网格 vs 旧扫候选：网格={0} 旧扫={1} 仅旧扫有={2}（旧球筛 100km 余量差异，非漏伤）",
                PerfProbe.ExplGridCandidates, PerfProbe.ExplOldScanCandidates, PerfProbe.ExplOldOnlyNoPartInRange));
            Log.Message(string.Format("[验收] ⑲Gooding 调用={0} 触顶={1} 实测最大迭代={2}/{3}（另有纯测试护栏）",
                Gooding.DebugTlambCalls, Gooding.DebugSeriesCapHits, Gooding.DebugMaxSeriesIter, Gooding.MAX_SERIES_ITER));
            Log.Message(string.Format("[验收] 组数：结束={0}", GroupCount()));

            // 完整探针汇总（所有计数，便于逐项核对）；恢复常规限频
            PerfProbe.SummaryIntervalTicks = 300;
            PerfProbe.LogSummaryNow();

            string verdict = hard.Count > 0
                ? "FAIL（存在不变式破坏，看上面红字）"
                : covered == PHASE_COUNT
                    ? "PASS（硬指标全 0，5/5 阶段覆盖）"
                    : string.Format("PARTIAL（硬指标全 0，覆盖 {0}/{1} 阶段——未覆盖项请重跑对应场景或延长观察）", covered, PHASE_COUNT);

            Log.Message("[验收] ═══ 总判定：" + verdict + " ═══");
            Log.Message("[验收] （探针保持开启便于继续观察；可在 Debug 面板关掉）");

            stage = Stage.Idle;
        }

        /// <summary>
        /// 第 1 条（聚合器缓存）专用造景：弹群由场景生成时是 N=8 同组，但场景自带 Attack，
        /// 而"已入 homing 100km"会在**下一个物理步**立刻解离——合并期只剩几 tick，手动/自动都赶不上干预。
        /// 故在**同一 tick 内**（下一个物理步之前）用 Loiter 覆盖 Attack（熄火静默待机 → 不解离、保持合并），
        /// 再给这个 8 实体组排一次小机动 → 群级燃烧必然走 GetGroupPropulsionSource(n=8) → 命中聚合器缓存。
        /// 找不到合并态登记就什么都不做，由阶段超时判"未覆盖"（不会误报通过）。
        /// </summary>
        private static void QueueMergedVolleyBurn()
        {
            var avm = Current.Game?.GetComponent<AutonomousVehicleManager>();
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (avm == null || spaceGame == null) return;

            int gid = FindMergedVolleyGroup(avm);
            if (gid < 0)
            {
                Log.Warning("[验收] 未找到合并态弹群登记（第 1 条本阶段将判未覆盖）");
                return;
            }

            bool ok = avm.IssueCommand(gid, AutonomousVehicleManager.MissionType.Loiter); // 覆盖场景自带的 Attack，保住合并态
            int execTick = (int)Math.Round(TimeScaleManager.GetPhysicsTickSafe()) + 30;
            spaceGame.QueueGroupManeuver(gid, new SpaceEntityGroup.PlannedManeuver
            {
                radialDV = 0f,
                tangentialDV = 10f,   // 小 ΔV：只为触发群级燃烧，不改变场景几何
                normalDV = 0f,
                executeTick = execTick,
                active = true
            });
            Log.Message(string.Format(
                "[验收] 合并态弹群 G{0}：Loiter={1} + 已排 ΔV=10 切向机动（physicsTick+30）→ 等群级燃烧查聚合器",
                gid, ok));
        }

        /// <summary>取最新登记的"未解离弹群"（logicalCount&gt;1 且未 volleySplit）。</summary>
        private static int FindMergedVolleyGroup(AutonomousVehicleManager avm)
        {
            var list = avm.Vehicles;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var r = list[i];
                if (r != null && !r.volleySplit && r.logicalCount > 1) return r.groupId;
            }
            return -1;
        }

        private static bool Covered(string prefix)
        {
            for (int i = 0; i < passes.Count; i++)
                if (passes[i].StartsWith(prefix)) return true;
            return false;
        }

        private static void Check(List<string> hard, long value, string what)
        {
            if (value != 0) hard.Add(what + "：" + value + " 次");
        }

        private static int GroupCount()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) return 0;
            int n = 0;
            foreach (var _ in sg.GetAllGroups()) n++;
            return n;
        }
    }
}
