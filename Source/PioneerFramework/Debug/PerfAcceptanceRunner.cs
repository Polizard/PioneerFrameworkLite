using System;
using System.Collections.Generic;
using PioneerFramework.Combat;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 游戏内「一键验收」（2026-09-10 建立；2026-09-14 扩展 A0/A1/A2 引力场与弹头）。
    ///
    /// 三段式结论：
    ///   · **即时电池**（`GravityFieldSelfTest`）：开工即跑，20 项数据路径/场数学/A1 参数一致性断言，无需等模拟。
    ///   · **硬指标**（不变式）：任何 `[性能探针][不一致]` 计为非 0 即 FAIL——含兜底汇总 `TotalMismatches`。
    ///   · **值判据**：新引入。旧版只有"覆盖度"（跑没跑到）与"硬指标"（不变式破没破），
    ///     但"跑到了、不变式没破、值却不对"（如月球域差分=0、弹头引力 100× 偏离）也需要能判 FAIL。
    ///   · **覆盖度**：每阶段必须有证据增量，否则判"未覆盖"（没有证据的通过等于没测）。
    ///
    /// 原覆盖《性能优化待办2026-09-09》第 1/2/3/4/5/14/15/16/19 条。
    /// 由 Debug 菜单「PFL 测试」页签（DebugTabMenu_PFL）一个按钮启动，
    /// 之后每 tick 由 SpaceGameComp.GameComponentTick 推进。
    ///
    /// 与 BurnArchitectureTest 同一套路：相位状态机 + 每阶段超时 + PASS/FAIL 累积 + 结论打印。
    /// 与探针（PerfProbe）的关系：探针负责在**每次**走缓存/复用路径时影子校验并计数；
    /// 本验收器负责**自动按顺序部署场景**，并在每个阶段用"计数增量"证明该阶段真的走到了目标代码路径
    /// （否则该阶段判"未覆盖"，不算通过）——没有证据的通过等于没测。
    ///
    /// 阶段与判据（增量 > 0 = 该条被覆盖）：
    ///   1/7 弹群齐射→解离 N=8   ⑮爆炸网格查询（弹头 AoE 必然走网格）      （SpawnVolleyImpactTest）
    ///   2/7 合并态弹群燃烧      ①聚合器缓存命中共            （SpawnVolleyImpactTest + 排机动）
    ///   3/7 炮塔对射            ⑤组件缓存命中 + ⑯逆四元数 命中           （SpawnTurretTest）
    ///   4/7 无Map碰撞           接触解算（碰撞链真的解算到接触）          （SpawnCollisionTest）
    ///   5/7 主推轴向+RCS 燃烧   ③组级燃烧切片 scratch 复用检查            （SpawnAxialThrustTest）
    ///   6/7 月球系引力场(A0)    ㉑子天体域差分采样 + 比值落潮汐带 0.1%–0.6% （SpawnMoonGravityTest，**非圆轨**）
    ///   7/7 弹头引力(A2)        ㉒弹头建段 + |a| ≈ 主导项 μ/r²（0.2–5×）   （SpawnMissileImpactTest）
    /// 硬指标（任一非 0 即整体 FAIL）：①无变更重建/影子不一致 ②容器身份变化 ③切片共用/别名错
    ///   ④重复销毁/未消费 ⑤组件缓存不一致 ⑮网格漏伤 ⑯逆四元数陈旧 ⑲Gooding 触顶。
    /// ⑲（Gooding）不在游戏内触发退化几何，由纯测试强制覆盖（GoodingTests：180 组几何 + NaN 必抛不挂死）。
    ///
    /// 注意：本验收器会部署调试船/建筑（与面板上手动点各场景一致），会解暂停并切 1x；
    /// 跨场景的残留船可能互相干扰，因此各阶段判据只用"计数增量"这类不依赖单场景纯净度的事实。
    /// </summary>
    public static class PerfAcceptanceRunner
    {
        private enum Stage { Idle, Volley, MergedBurn, Turret, Collision, Thrust, GravityLunar, WarheadGravity, MissedWarhead, Verdict }

        /// <summary>每阶段上限（真实 tick；1x 下约 90 秒）。超时判"未覆盖"，不判失败。</summary>
        private const int PHASE_TIMEOUT_TICKS = 5400;
        private const int PHASE_COUNT = 8;

        private static Stage stage = Stage.Idle;
        private static int stageStartTick;
        private static int nextProgressLogTick;

        // 2026-11-01（用户要求"专门做一个测试按钮"）：单阶段自检的自动收尾与残留清理。
        // 只给【09】用；不动正常 8 阶段流程。
        private static int autoStopAtTick = -1;
        private static bool autoRestoreStages;
        private static HashSet<int>? sessionBaselineGroups;


        private static readonly List<string> passes = new();
        private static readonly List<string> uncovered = new();
        /// <summary>值判据失败（"跑到了但数不对"）——与"未覆盖"不同，这类直接判 FAIL。</summary>
        private static readonly List<string> failures = new();

        // 阶段起点计数（增量 = 该阶段是否真走到目标代码路径）
        private static long bAggReused, bRotInv, bCompRefHit, bExplQuery, bSliceChecks, bContacts;
        private static long bGravitySubBody, bGravityRootChecked, bProjSteps, bProjSegments, bFreeFlightBends;
        private static long bWarheadSegments;
        private static string? batterySummary = "未运行";

        // ── 进度窗口用的状态（窗口直接读这些，不从日志字符串里解析）──
        /// <summary>各阶段结果：Pending 未开始 / Running 进行中 / Passed 通过 / Uncovered 未覆盖 / Failed 值判据失败。</summary>
        public enum StageOutcome { Pending, Running, Passed, Uncovered, Failed, Skipped }

        // ── 测试项勾选（进度窗口里可改；静态保存，一次会话内有效）──
        // 存在的理由：阶段 4「无 Map 碰撞」要等船漂到接触（最慢），只想验引力相关时没必要等。
        private static readonly bool[] stageEnabled = CreateAllEnabled();
        private static bool batteryEnabled = true;

        private static bool[] CreateAllEnabled()
        {
            var a = new bool[PHASE_COUNT];
            for (int i = 0; i < a.Length; i++) a[i] = true;
            return a;
        }

        public static bool IsStageEnabled(int index)
            => index >= 0 && index < PHASE_COUNT && stageEnabled[index];
        public static void SetStageEnabled(int index, bool on)
        {
            if (index >= 0 && index < PHASE_COUNT) stageEnabled[index] = on;
        }
        public static bool IsBatteryEnabled => batteryEnabled;
        public static void SetBatteryEnabled(bool on) => batteryEnabled = on;
        public static void SetAllStages(bool on)
        {
            for (int i = 0; i < PHASE_COUNT; i++) stageEnabled[i] = on;
        }
        /// <summary>预设：只跑引力相关（电池 + 6/7 月球域 + 7/7 弹头）。</summary>
        public static void SelectGravityOnly()
        {
            batteryEnabled = true;
            for (int i = 0; i < PHASE_COUNT; i++) stageEnabled[i] = i >= PHASE_COUNT - 3;
        }
        public static int SelectedStageCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < PHASE_COUNT; i++) if (stageEnabled[i]) n++;
                return n;
            }
        }

        private static readonly StageOutcome[] outcomes = new StageOutcome[PHASE_COUNT];
        private static readonly string?[] outcomeNotes = new string[PHASE_COUNT];

        public static int StageTotal => PHASE_COUNT;
        /// <summary>当前阶段序号 1..7；0 = 未运行或已出结论。</summary>
        public static int CurrentStageNumber
            => stage >= Stage.Volley && stage <= Stage.WarheadGravity ? (int)stage : 0;
        public static StageOutcome OutcomeAt(int index)
            => index >= 0 && index < PHASE_COUNT ? outcomes[index] : StageOutcome.Pending;
        public static string? NoteAt(int index)
            => index >= 0 && index < PHASE_COUNT ? outcomeNotes[index] : null;
        public static string StageLabelAt(int index)
        {
            var s = (Stage)(index + 1);
            return StageName(s);
        }
        public static int CurrentStageElapsed
            => CurrentStageNumber > 0 ? CurTick - stageStartTick : 0;
        public static int StageTimeout => PHASE_TIMEOUT_TICKS;
        public static float CurrentStageProgress01
            => CurrentStageNumber > 0
                ? Math.Min(1f, (float)(CurTick - stageStartTick) / PHASE_TIMEOUT_TICKS)
                : 0f;
        /// <summary>总进度：电池算第 1 步、7 个阶段各算 1 步 → 分母 8。</summary>
        public static float OverallProgress01
        {
            get
            {
                int done = batterySummary == "未运行" ? 0 : 1;
                for (int i = 0; i < PHASE_COUNT; i++)
                    if (outcomes[i] == StageOutcome.Passed || outcomes[i] == StageOutcome.Uncovered
                        || outcomes[i] == StageOutcome.Failed) done++;
                if (done >= PHASE_COUNT + 1) return 1f;
                return Math.Min(1f, (done + CurrentStageProgress01) / (PHASE_COUNT + 1f));
            }
        }
        /// <summary>结论文案；null = 本次还没出结论。</summary>
        public static string? Verdict { get; private set; }
        public static bool VerdictIsFail { get; private set; }
        public static string? BatterySummary => batterySummary;
        public static int BatteryPassedCount => batteryPassed;
        public static int BatteryFailedCount => batteryFailed;
        public static IReadOnlyList<string> Failures => failures;
        public static IReadOnlyList<string> Passes => passes;
        public static IReadOnlyList<string> Uncovered => uncovered;

        /// <summary>上一阶段进入时的组 id 快照：用于阶段间清场（见 CleanupSinceLastStageEntry）。</summary>
        private static readonly HashSet<int> groupIdsAtLastEntry = new();

        /// <summary>场景方法会自行打开这些"逐 tick 详细日志"开关且**从不关闭**（首轮验收实测
        /// [弹群接触] 刷了 2.5 万行、把结论埋掉）→ 阶段间强制关掉，结论时恢复玩家原值。</summary>
        private static bool savedDetailedCombat, savedDetailedCollision, savedDetailedGuidance;
        private static readonly List<int> doomedGroupIds = new();
        private static int batteryPassed, batteryFailed;

        public static bool Running => stage != Stage.Idle;

        /// <summary>面板状态行文案（面板打开时每帧一次，仅运行时才拼串）。</summary>
        public static string StatusText
        {
            get
            {
                if (stage == Stage.Idle) return "未运行";
                int el = CurTick - stageStartTick;
                return string.Format("{0}（t+{1}/{2}｜勾选 {12}/{13}）证据增量：①{3} ⑯{4} ⑤{5} ⑮{6} ③{7} 接触{8}｜月球域{9} 根域{10} 弹头段{11}",
                    StageName(stage), el, PHASE_TIMEOUT_TICKS,
                    PerfProbe.AggReused - bAggReused, PerfProbe.RotInvHits - bRotInv,
                    PerfProbe.CompRefHits - bCompRefHit, PerfProbe.ExplGridQueries - bExplQuery,
                    PerfProbe.SliceBufferChecks - bSliceChecks, PerfProbe.ContactsResolved - bContacts,
                    PerfProbe.GravitySubBodyProbes - bGravitySubBody,
                    PerfProbe.GravityRootChecked - bGravityRootChecked,
                    PerfProbe.WarheadSegments - bWarheadSegments,
                    SelectedStageCount, PHASE_COUNT);
            }
        }

        private static int CurTick => Find.TickManager?.TicksGame ?? 0;

        private static string StageName(Stage s)
        
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            => s switch
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            {
            Stage.Volley => "1/8 弹群齐射→解离→弹头AoE（⑮）",
            Stage.MergedBurn => "2/8 合并态弹群燃烧（①聚合器）",
            Stage.Turret => "3/8 炮塔对射（⑤⑯）",
            Stage.Collision => "4/8 无Map碰撞（接触解算）",
            Stage.Thrust => "5/8 主推轴向+RCS 燃烧（②③）",
            Stage.GravityLunar => "6/8 月球系引力场（A0：子天体域差分）",
            Stage.WarheadGravity => "7/8 弹头引力（A2：建段）",
            Stage.MissedWarhead => "8/8 漏靶弹头（A2：段内插值 + 引力弯曲）",
            Stage.Verdict => "出结论",
            _ => s.ToString(),
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            };

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
            failures.Clear();
            for (int i = 0; i < PHASE_COUNT; i++)
            {
                outcomes[i] = stageEnabled[i] ? StageOutcome.Pending : StageOutcome.Skipped;
                outcomeNotes[i] = stageEnabled[i] ? null : "已取消勾选（本次跳过）";
            }
            Verdict = null;
            VerdictIsFail = false;

            // 探针：开启 + 清零，保证结论里的每个数字都属于本次验收
            PerfProbe.Enabled = true;
            PerfProbe.ShadowChecks = true;
            PerfProbe.Reset();
            PerfProbe.SummaryIntervalTicks = 100000000; // 运行期间用面板状态行看进度，避免日志刷屏；结论时恢复

            savedDetailedCombat = CombatManager.DebugDetailedCombat;
            savedDetailedCollision = CombatManager.DebugDetailedCollision;
            savedDetailedGuidance = AutonomousVehicleManager.DebugDetailedGuidance;

            // 1x：物理时间轴与真实 tick 一致，阶段超时才有意义（与 ArchTest 同款处理）
            Current.Game?.GetComponent<TimeScaleManager>()?.SetNormalMode();

            // 即时电池（不需等模拟）：引力场数据路径 + 场数学 + A1 参数一致性。
            // 先跑它，因为它的结论与后面的场景无关，且失败原因最明确。
            if (batteryEnabled)
            {
                GravityFieldSelfTest.Run();
                // 2026-10-16：机制在册自检并进即时电池（反射清理/行为迁移批的可机判部分）
                RefactorMechanismsSelfTest.Run();
                batteryPassed = GravityFieldSelfTest.LastPassed + RefactorMechanismsSelfTest.LastPassed;
                batteryFailed = GravityFieldSelfTest.LastFailed + RefactorMechanismsSelfTest.LastFailed;
                batterySummary = $"即时电池：引力 {GravityFieldSelfTest.LastPassed}/{GravityFieldSelfTest.LastFailed}"
                    + $" + 机制在册 {RefactorMechanismsSelfTest.LastPassed}/{RefactorMechanismsSelfTest.LastFailed}";
                foreach (var f in GravityFieldSelfTest.LastFailures) failures.Add("[电池·引力] " + f);
                foreach (var f in RefactorMechanismsSelfTest.LastFailures) failures.Add("[电池·机制] " + f);
            }
            else
            {
                batteryPassed = batteryFailed = 0;
                batterySummary = "已取消勾选";
            }

            Window_PFLSelfTest.OpenIfClosed(); // 进度窗口（长流程没有它只能翻日志猜跑到哪了）

            Log.Message("[验收] ═══ 性能优化 + 引力场一键验收开始（探针已开启，影子校验全开）═══");
            Log.Message("[验收] 说明：每阶段自动部署场景；『未覆盖』= 超时内没观测到证据（通常场景还没打完），不代表失败；"
                + "任何 [性能探针][不一致] 红字 = 真实缺陷。");
            Log.Message("[验收] 提示：不想等满 8 阶段（尤其阶段 4 无Map碰撞要等船漂到接触）可随时点「■ 中止验收」——"
                + "它会**立即打印结论与全部计数**；每 600 tick 也会打一行进度含累计计数，所以中途关游戏也能看到数据。");
            Log.Message($"[验收] 起始组数={GroupCount()}（结论里对比，用于确认场景确实部署了）｜勾选：电池{(batteryEnabled ? "开" : "关")}，阶段 {SelectedStageCount}/{PHASE_COUNT}");
            foreach (var skipped in SkippedStageLabels()) Log.Message("[验收] 已跳过：" + skipped);

            if (!EnterNextEnabledStage(0)) PrintVerdict();   // 一个阶段都没勾 → 直接出结论
        }

        /// <summary>中止：立即出结论（保留已跑到的证据）。</summary>
        public static void Abort()
        {
            if (!Running) return;
            Log.Warning("[验收] 已被玩家中止，直接出结论");
            PrintVerdict();
        }

        /// <summary>
        /// **单阶段专项自检**（2026-11-01 用户要求：不要再"开始→等→掐秒中止"）：
        /// 只跑指定阶段（默认 `1` = 合并态弹群燃烧/聚合器缓存），`maxTicks` 后**自动**出结论并清场。
        /// 进入前先清掉"本会话基线之外"的测试残留组；阶段选择/电池开关跑完自动恢复。
        /// ⚠ 基线在**第一次**调用时快照 ⇒ 首次使用前请先重读档/重启，清掉历史上遗留的验收组。
        /// </summary>
        public static void RunFocusedSelfTest(int phaseIndex = 1, int maxTicks = 180)
        {
            if (Running) Abort();
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null)
            {
                Log.Warning("[自检] 不在太空场景（缺 SpaceGameComp），无法布置专项自检");
                return;
            }

            if (sessionBaselineGroups == null) sessionBaselineGroups = SnapshotGroupIds(sg);
            CleanupGroupsNotIn(sessionBaselineGroups, sg);

            SetAllStages(false);
            SetStageEnabled(phaseIndex, true);
            SetBatteryEnabled(false);
            SnapshotGroups();          // 进入阶段前的基线：EnterStage 的清理只清本次场景，不动玩家资产
            autoRestoreStages = true;
            autoStopAtTick = -1;

            Run();
            if (Running) autoStopAtTick = CurTick + maxTicks;
        }

        private static HashSet<int> SnapshotGroupIds(SpaceGameComp sg)
        {
            var set = new HashSet<int>();
            foreach (var g in sg.GetAllGroups()) set.Add(g.GroupId);
            return set;
        }

        private static void CleanupGroupsNotIn(HashSet<int> keep, SpaceGameComp sg)
        {
            doomedGroupIds.Clear();
            foreach (var g in sg.GetAllGroups())
                if (!keep.Contains(g.GroupId)) doomedGroupIds.Add(g.GroupId);
            for (int i = 0; i < doomedGroupIds.Count; i++) sg.DestroyGroup(doomedGroupIds[i]);
            if (doomedGroupIds.Count > 0)
                Log.Message($"[自检] 已清理上次测试残留组 {doomedGroupIds.Count} 个（不动会话基线内的玩家资产）");
        }

        /// <summary>自动收尾：出结论 → 清本次场景 → 恢复阶段勾选/电池开关。</summary>
        private static void FinishFocusedSelfTest()
        {
            autoStopAtTick = -1;
            Log.Message("[自检] 到时自动收尾（单阶段专项自检）");
            if (Running) Abort();                    // 出结论（未覆盖也会给出原因）
            CleanupSinceLastStageEntry();
            if (autoRestoreStages)
            {
                SetAllStages(true);
                SetBatteryEnabled(true);
                autoRestoreStages = false;
            }
        }

        /// <summary>从 fromIndex 起找第一个勾选的阶段并进入；返回 false = 后面没有勾选的阶段。</summary>
        private static bool EnterNextEnabledStage(int fromIndex)
        {
            for (int i = fromIndex; i < PHASE_COUNT; i++)
            {
                if (!stageEnabled[i]) continue;
                EnterStage((Stage)(i + 1));
                return true;
            }
            return false;
        }

        /// <summary>本阶段结束后推进到下一个勾选阶段；没有了就出结论。</summary>
        private static void AdvanceTo(Stage current)
        {
            if (!EnterNextEnabledStage((int)current)) PrintVerdict();
        }

        private static List<string> SkippedStageLabels()
        {
            var list = new List<string>();
            for (int i = 0; i < PHASE_COUNT; i++)
                if (!stageEnabled[i]) list.Add(StageName((Stage)(i + 1)));
            return list;
        }

        private static void EnterStage(Stage s)
        {
            // 阶段间清场：上一阶段（及其弹群）会留下大量组在轨道上乱撞——首轮验收实测残留 90+ 组、
            // 弹群互相叠盒刷了 2.5 万行日志，并很可能导致后续阶段的目标被打掉/炮塔被挡而不出证据。
            // 只销毁"上次进入阶段之后新建的组"（即本次验收自己造的场景物），不碰玩家原有资产。
            if (s != Stage.Volley) CleanupSinceLastStageEntry();

            stage = s;
            if (s >= Stage.Volley && s <= Stage.WarheadGravity)
            {
                int idx = (int)s - 1;
                outcomes[idx] = StageOutcome.Running;
                outcomeNotes[idx] = null;
            }
            stageStartTick = CurTick;
            nextProgressLogTick = CurTick + 600;
            bAggReused = PerfProbe.AggReused;
            bRotInv = PerfProbe.RotInvHits;
            bCompRefHit = PerfProbe.CompRefHits;
            bExplQuery = PerfProbe.ExplGridQueries;
            bSliceChecks = PerfProbe.SliceBufferChecks;
            bContacts = PerfProbe.ContactsResolved;
            bGravitySubBody = PerfProbe.GravitySubBodyProbes;
            bGravityRootChecked = PerfProbe.GravityRootChecked;
            bProjSteps = PerfProbe.ProjectileSteps;
            bProjSegments = PerfProbe.ProjectileSegments;
            bFreeFlightBends = PerfProbe.FreeFlightBends;
            bWarheadSegments = PerfProbe.WarheadSegments;

            switch (s)
            {
                case Stage.Volley:
                    Log.Message("[验收] 【1/8】弹群齐射→解离 N=8（覆盖第 15 条：弹头 AoE 走细网格；顺带跑通制导/解离/每步 scratch）");
                    DebugShipSpawner.SpawnVolleyImpactTest();
                    break;
                case Stage.MergedBurn:
                    Log.Message("[验收] 【2/8】合并态弹群燃烧（覆盖第 1 条：多实体组聚合器缓存）");
                    DebugShipSpawner.SpawnVolleyImpactTest();
                    QueueMergedVolleyBurn(); // 同 tick 内保住合并态并排机动（见方法注释）
                    break;
                case Stage.Turret:
                    Log.Message("[验收] 【3/8】炮塔对射（覆盖第 5/14/16 条：组件缓存 + 命中几何）");
                    DebugShipSpawner.SpawnTurretTest();
                    break;
                case Stage.Collision:
                    Log.Message("[验收] 【4/8】无 Map 碰撞（覆盖第 2/4 条：每步容器复用 + 接触解算链）");
                    DebugShipSpawner.SpawnCollisionTest();
                    break;
                case Stage.Thrust:
                    Log.Message("[验收] 【5/8】主推轴向 + RCS 燃烧（覆盖第 1/2/3 条燃烧路径）");
                    DebugShipSpawner.SpawnAxialThrustTest();
                    break;
                case Stage.GravityLunar:
                    Log.Message("[验收] 【6/8】月球系引力场（A0）：月球附近非圆轨群组 → 子天体域差分比值应为潮汐 0.1%–0.6%");
                    DebugShipSpawner.SpawnMoonGravityTest();
                    break;
                case Stage.WarheadGravity:
                    Log.Message("[验收] 【7/8】弹头引力（A2）：导弹命中生成弹头 → 建段 |a| 应 ≈主导项 μ/r²（旧实现 ~100×）");
                    DebugShipSpawner.SpawnMissileImpactTest();
                    break;
                case Stage.MissedWarhead:
                    Log.Message("[验收] 【8/8】漏靶弹头（A2）：让弹头活满寿命 —— 验段内插值飞行（㉒节奏）"
                        + "与引力弯曲量（㉔ 实际偏移/½|a|t² ≈ 1×）");
                    DebugShipSpawner.SpawnMissedWarheadTest();
                    break;
                case Stage.Verdict:
                    PrintVerdict();
                    break;
            }

            SnapshotGroups();
        }

        private static void SnapshotGroups()
        {
            groupIdsAtLastEntry.Clear();
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) return;
            foreach (var g in sg.GetAllGroups()) groupIdsAtLastEntry.Add(g.GroupId);
        }

        /// <summary>销毁"上次进入阶段之后新建的组"（= 本次验收的场景物与其残留）。</summary>
        private static void CleanupSinceLastStageEntry()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) return;

            doomedGroupIds.Clear();
            foreach (var g in sg.GetAllGroups())
                if (!groupIdsAtLastEntry.Contains(g.GroupId)) doomedGroupIds.Add(g.GroupId);

            for (int i = 0; i < doomedGroupIds.Count; i++)
                sg.DestroyGroup(doomedGroupIds[i]);

            // 场景方法自开的逐 tick 详细日志在此关掉（否则整轮验收都被 [弹群接触] 之类刷爆）
            CombatManager.DebugDetailedCombat = false;
            CombatManager.DebugDetailedCollision = false;
            AutonomousVehicleManager.DebugDetailedGuidance = false;

            if (doomedGroupIds.Count > 0)
                Log.Message($"[验收] 阶段间清场：销毁上一阶段残留组 {doomedGroupIds.Count} 个（验收自造，不动玩家资产）；详细场景日志已关");
        }

        /// <summary>由 SpaceGameComp.GameComponentTick 每 tick 驱动。</summary>
        public static void Tick()
        {
            if (!Running || stage == Stage.Verdict)
            {
                if (autoStopAtTick > 0) FinishFocusedSelfTest();   // 自然跑完（提前 PASS）也要清场/复位
                return;
            }
            if (autoStopAtTick > 0 && CurTick >= autoStopAtTick)
            {
                FinishFocusedSelfTest();
                return;
            }

            bool timedOut = CurTick - stageStartTick >= PHASE_TIMEOUT_TICKS;

            // 进度日志（每 600 tick）：即使玩家没开面板也能从日志看出"在等什么"，避免误以为卡死
            if (CurTick >= nextProgressLogTick)
            {
                nextProgressLogTick = CurTick + 600;
                // 累计量也一并打印：这样即使半途关游戏（没跑到结论），也能看出各条是否真被走到
                Log.Message($"[验收] 进行中：{StageName(stage)} t+{CurTick - stageStartTick}/{PHASE_TIMEOUT_TICKS} 本阶段增量：①{PerfProbe.AggReused - bAggReused} ⑯{PerfProbe.RotInvHits - bRotInv} ⑤{PerfProbe.CompRefHits - bCompRefHit} ⑮{PerfProbe.ExplGridQueries - bExplQuery} ③{PerfProbe.SliceBufferChecks - bSliceChecks} 接触{PerfProbe.ContactsResolved - bContacts}｜累计：①reused={PerfProbe.AggReused} ⑯命中={PerfProbe.RotInvHits} ⑤命中={PerfProbe.CompRefHits} ⑮网格={PerfProbe.ExplGridQueries} ⑦空武器表={PerfProbe.EmptyWeaponListHits} ⑩省查找={PerfProbe.RecordLookupsAvoided} ⑧天体位={PerfProbe.BodyPosHits}/{PerfProbe.BodyPosMisses}｜㉔局部位={PerfProbe.BodyParentCacheHits}/{PerfProbe.BodyParentCacheMisses} ㉖{PerfProbe.StepClockReport()}");
            }

            switch (stage)
            {
                case Stage.Volley:
                    // 判据只用该场景**能**走到的路径：⑮弹头 AoE（导弹命中必然触发）。
                    // 曾把 ⑯（逆四元数，只在射弹/激光命中几何里调用）写进本阶段，导致阶段永不满足——教训记于此。
                    if (PerfProbe.ExplGridQueries > bExplQuery)
                    {
                        Pass($"⑮ 弹群命中 → 弹头 AoE 走细网格 +{PerfProbe.ExplGridQueries - bExplQuery} 次（漏伤判据见硬指标；附本阶段 ①聚合器 reused+{PerfProbe.AggReused - bAggReused}）");
                        AdvanceTo(Stage.Volley);
                    }
                    else if (timedOut) TimeoutStage("未观测到『弹头 AoE 走网格』（导弹可能未命中）");
                    break;

                case Stage.MergedBurn:
                    if (PerfProbe.AggReused > bAggReused)
                    {
                        Pass($"① 合并态弹群（多实体组 N=8）群级燃烧走聚合器缓存 reused+{PerfProbe.AggReused - bAggReused}");
                        AdvanceTo(Stage.MergedBurn);
                    }
                    else if (timedOut) TimeoutStage("未观测到『聚合器缓存命中』（合并态弹群可能未燃起来或已解离）");
                    break;

                case Stage.Turret:
                    if (PerfProbe.CompRefHits > bCompRefHit && PerfProbe.RotInvHits > bRotInv)
                    {
                        Pass($"⑤⑯ 对射：组件缓存命中+{PerfProbe.CompRefHits - bCompRefHit}，命中几何走逆四元数缓存 +{PerfProbe.RotInvHits - bRotInv}");
                        AdvanceTo(Stage.Turret);
                    }
                    else if (timedOut) TimeoutStage("未观测到『组件缓存命中 + 命中几何缓存命中』（炮塔可能未开火）");
                    break;

                case Stage.Collision:
                    // 该场景是低速靠近（25~30 m/s，低于爆炸阈值）→ 判据用"接触解算"，不要求爆炸
                    if (PerfProbe.ContactsResolved > bContacts)
                    {
                        Pass($"②④ 接触解算 +{PerfProbe.ContactsResolved - bContacts} 次（碰撞链跑到 SeparatePositions；爆炸走网格由上一阶段已覆盖）");
                        AdvanceTo(Stage.Collision);
                    }
                    else if (timedOut) TimeoutStage("未观测到『接触解算』（三条船可能未接触）");
                    break;

                case Stage.Thrust:
                    if (PerfProbe.SliceBufferChecks > bSliceChecks)
                    {
                        Pass($"②③ 燃烧路径走到组级 scratch 复用检查 +{PerfProbe.SliceBufferChecks - bSliceChecks} 次（物理步 scratch 身份/别名见硬指标）");
                        AdvanceTo(Stage.Thrust);
                    }
                    else if (timedOut) TimeoutStage("未观测到『组级燃烧切片复用』（可能未点火）");
                    break;

                case Stage.GravityLunar:
                    // 覆盖 = 子天体域真的采样到了；值判据（灾难性偏离）由探针直接记 Mismatch → 硬指标。
                    // 这里额外把实测比值打进结论，便于人不看日志也能判断。
                    if (PerfProbe.GravitySubBodyProbes > bGravitySubBody)
                    {
                        Pass($"A0 月球系：子天体域差分采样 +{PerfProbe.GravitySubBodyProbes - bGravitySubBody} 次，差分/局部引力 = {PerfProbe.GravitySubBodyLastRatio:P4}（范围 {PerfProbe.GravitySubBodyMinRatio:P4}–{PerfProbe.GravitySubBodyMaxRatio:P4}）｜判读：0=旧行为(错) 0.1%–0.6%=潮汐(对) ~7%=全量和(错)");

                        if (PerfProbe.GravitySubBodyLastRatio < 1e-4 || PerfProbe.GravitySubBodyLastRatio > 0.03)
                            MarkStageFailed("差分比值不在潮汐带");
                        if (PerfProbe.GravitySubBodyLastRatio < 1e-4 || PerfProbe.GravitySubBodyLastRatio > 0.03)
                            failures.Add($"A0 月球域差分比值 {PerfProbe.GravitySubBodyLastRatio:P4} 不在潮汐带 0.01%–3%（0=旧行为 / ~7%=全量和）");

                        AdvanceTo(Stage.GravityLunar);
                    }
                    else if (timedOut) TimeoutStage("未观测到『子天体域差分采样』（月球群组可能未建立/未在积分——注意圆轨走解析路径不进引力场）");
                    break;

                case Stage.WarheadGravity:
                    if (PerfProbe.WarheadSegments > bWarheadSegments)
                    {
                        double ratio = PerfProbe.WarheadAccelRatio;
                        Pass($"A2 弹头引力：建段 +{PerfProbe.WarheadSegments - bWarheadSegments} 次，|a|={PerfProbe.WarheadAccel:F3} m/s²（主导项 μ/r² 的 {ratio:F2}×，应 0.2–5×） 指向行星中心余弦={PerfProbe.WarheadTowardCenterCos:F3}｜射弹段：重建 {PerfProbe.ProjectileSegments} / 步 {PerfProbe.ProjectileSteps}");

                        if (ratio > 0.0 && (ratio < 0.2 || ratio > 5.0))
                            MarkStageFailed("弹头引力偏离主导项");
                        if (ratio > 0.0 && (ratio < 0.2 || ratio > 5.0))
                            failures.Add($"A2 弹头引力偏离主导项 {ratio:F1}×（应 0.2–5×；~100× = 参考系混用的旧伪值）");
                        // 「段重建频率」不在这里判：**唯一判据在探针里**（PerfProbe.NoteProjectileStep，
                        // 按弹、按物理时间、且要求期望段数 ≥4 才下结论）。
                        // 2026-09-14 教训：此处曾留一条"全局 段/步 > 1/60 即过频"的粗糙判据，
                        // 在"1 段 / 1 步"（第一步必然如此）时误报 FAIL——同一件事两个实现，naive 的那个害人。

                        AdvanceTo(Stage.WarheadGravity);
                    }
                    else if (timedOut) TimeoutStage("未观测到『弹头建段』（导弹可能未命中/未生成弹头；A2 路径只在弹头射弹上）");
                    break;

                case Stage.MissedWarhead:
                    // 判据以「拿到弯曲样本」为准（探针要求飞行 ≥15 s 才采样），而不是只看"段重建 > 0"：
                    // 后者约 2 秒就满足，弯曲断言那次根本没机会跑（2026-09-14 首版如此，PASS 文案里
                    // 「㉔弯曲采样=0 次」即为证据）。拿到样本 ⇒ 段内插值飞行已跨多段、且能做定量判读。
                    if (PerfProbe.FreeFlightBends > bFreeFlightBends)
                    {
                        Pass($"A2 漏靶弹头：段重建 +{PerfProbe.ProjectileSegments - bProjSegments} 次｜㉔弯曲采样 +{PerfProbe.FreeFlightBends - bFreeFlightBends} 次 最近 实际/½|a|t²={PerfProbe.FreeFlightLastRatio:F2}×（实际 {PerfProbe.FreeFlightLastLinear:F1} m / 理论 {PerfProbe.FreeFlightLastExpected:F1} m；<1 = 引力没真加上 / >>1 = 参考系错或每段丢一步）");

                        // 修好"段起点时刻"后该比值应精确到 <1%（圆轨上 ½|a|t² 是严格解）；留 0.8–1.3 的余量
                        if (PerfProbe.FreeFlightBends > 0
                            && (PerfProbe.FreeFlightLastRatio < 0.8 || PerfProbe.FreeFlightLastRatio > 1.3))
                            failures.Add($"A2 引力弯曲量 {PerfProbe.FreeFlightLastRatio:F2}× 不在 0.8–1.3×（<1 = 引力没真加上 / >>1 = 参考系错 / 每段丢一步运动也会偏）");

                        AdvanceTo(Stage.MissedWarhead);
                    }
                    else if (timedOut) TimeoutStage("未观测到『自由飞行弯曲样本』（漏靶弹头可能未生成、或未活过 15 物理秒）");
                    break;
            }
        }

        private static void MarkStageFailed(string why)
        {
            int idx = CurrentStageNumber - 1;
            if (idx >= 0 && idx < PHASE_COUNT && outcomes[idx] != StageOutcome.Failed)
            {
                outcomes[idx] = StageOutcome.Failed;
                outcomeNotes[idx] = why;
            }
        }

        private static void TimeoutStage(string why)
        {
            int toIdx = CurrentStageNumber - 1;
            if (toIdx >= 0 && toIdx < PHASE_COUNT)
            {
                outcomes[toIdx] = StageOutcome.Uncovered;
                outcomeNotes[toIdx] = why;
            }
            uncovered.Add(StageName(stage) + "：" + why + $"（超时 {PHASE_TIMEOUT_TICKS} tick）");
            Log.Warning("[验收] " + StageName(stage) + " 未覆盖——" + why + "。继续下一阶段。");
            switch (stage)
            {
                case Stage.Volley: AdvanceTo(Stage.Volley); break;
                case Stage.MergedBurn: AdvanceTo(Stage.MergedBurn); break;
                case Stage.Turret: AdvanceTo(Stage.Turret); break;
                case Stage.Collision: AdvanceTo(Stage.Collision); break;
                case Stage.Thrust: AdvanceTo(Stage.Thrust); break;
                case Stage.GravityLunar: AdvanceTo(Stage.GravityLunar); break;
                case Stage.WarheadGravity: AdvanceTo(Stage.WarheadGravity); break;
                case Stage.MissedWarhead: AdvanceTo(Stage.MissedWarhead); break;
            }
        }

        private static void Pass(string msg)
        {
            passes.Add(msg);
            int idx = CurrentStageNumber - 1;
            if (idx >= 0 && idx < PHASE_COUNT)
            {
                outcomes[idx] = StageOutcome.Passed;
                outcomeNotes[idx] = msg;
            }
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
            Check(hard, PerfProbe.GravityRootMismatch, "㉑引力场根域影子不一致（天体枚举/位置累加错）");
            Check(hard, PerfProbe.EntityRegistryLeaks, "㉕实体注册表泄漏（离群未注销 → 僵尸实体）");
            // 兜底：任何 Mismatch（含引力场域 ㉑ 与弹头引力 ㉒，它们没有各自的专属硬指标计数）
            Check(hard, PerfProbe.TotalMismatches, "任何 [性能探针][不一致]（兜底汇总，含 ㉑引力场域 / ㉒弹头引力）");

            bool volleyCovered = Covered("⑮");      // 弹群命中 → 弹头 AoE 走网格
            bool mergedCovered = Covered("①");      // 合并态弹群燃烧 → 聚合器缓存
            bool turretCovered = Covered("⑤");      // 组件缓存 + 命中几何
            bool collisionCovered = Covered("②④"); // 接触解算
            bool thrustCovered = Covered("②③");    // 组级燃烧切片复用
            bool lunarCovered = Covered("A0 月球系");   // 子天体域差分采样
            bool warheadCovered = Covered("A2 弹头引力"); // 弹头建段
            bool missedCovered = Covered("A2 漏靶弹头");  // 段内插值飞行
            int covered = (volleyCovered ? 1 : 0) + (mergedCovered ? 1 : 0) + (turretCovered ? 1 : 0)
                + (collisionCovered ? 1 : 0) + (thrustCovered ? 1 : 0)
                + (lunarCovered ? 1 : 0) + (warheadCovered ? 1 : 0) + (missedCovered ? 1 : 0);
            int selectedTotal = SelectedStageCount;
            var skippedLabels = SkippedStageLabels();

            Log.Message("[验收] ═══════════ 性能优化验收结论 ═══════════");
            Log.Message($"[验收] 硬指标（不变式）：{(hard.Count == 0 ? "全部为 0 ✓" : hard.Count + " 项非 0 ✗")}");
            foreach (var h in hard) Log.Error("[验收] 硬指标失败：" + h);

            // 被跳过的阶段要显示"跳过"，不能和"跑了但没拿到证据"混为一谈
            Log.Message($"[验收] 覆盖度 {covered}/{selectedTotal}（勾选阶段）：弹群AoE⑮{CoverLabel(0, volleyCovered)} 合并态聚合器①{CoverLabel(1, mergedCovered)} 对射⑤⑯{CoverLabel(2, turretCovered)} 碰撞{CoverLabel(3, collisionCovered)} 燃烧②③{CoverLabel(4, thrustCovered)} 月球域A0{CoverLabel(5, lunarCovered)} 弹头A2{CoverLabel(6, warheadCovered)} 漏靶A2{CoverLabel(7, missedCovered)}");
            if (skippedLabels.Count > 0)
                Log.Message($"[验收] 已跳过 {skippedLabels.Count} 个阶段（取消勾选）：{string.Join("；", skippedLabels.ToArray())}");
            Log.Message("[验收] ⑲Gooding：不在游戏内触发退化几何，由纯测试强制覆盖"
                + "（GoodingTests：180 组几何扫描确认上限不误杀 + NaN 必抛不挂死）");
            foreach (var u in uncovered) Log.Warning("[验收] 未覆盖：" + u);

            // ── 优化是否真的生效（而不是退化成每次都重建 / 完全没走缓存）──
            long rebuilt = PerfProbe.AggRebuiltFirst + PerfProbe.AggRebuiltEntitySet
                + PerfProbe.AggRebuiltPropulsion + PerfProbe.AggRebuiltNoChange;
            Log.Message($"[验收] 生效证据：①聚合器 reused={PerfProbe.AggReused} / rebuilt={rebuilt}（reused 应远大于 rebuilt；正本发布后重建 {PerfProbe.AggRebuiltPublished} 次 = 契约预期）；⑤组件缓存 命中={PerfProbe.CompRefHits} / 实查={PerfProbe.CompRefMissResolved}；⑯逆四元数 命中={PerfProbe.RotInvHits} / 未命中={PerfProbe.RotInvMisses}；⑮爆炸网格查询={PerfProbe.ExplGridQueries}");
            Log.Message($"[验收] 中低优先批（7/8/10 条）生效证据：⑦共享空武器表命中={PerfProbe.EmptyWeaponListHits}（= 省下的空 List 分配）；⑩扫掠早退省下 GetRecord={PerfProbe.RecordLookupsAvoided} 次；⑧天体位置记忆化 命中={PerfProbe.BodyPosHits} / 未命中={PerfProbe.BodyPosMisses}（命中 = 省下的递归父链 + 每级 Cos/Sin）");
            Log.Message($"[验收] ㉔天体**局部**缓存（第 24 条，服务 A0 引力场）：命中={PerfProbe.BodyParentCacheHits} / 未命中={PerfProbe.BodyParentCacheMisses}（未命中应 ≈ 天体数×步数，命中/未命中应 ≈ 组数−1）｜影子比对={PerfProbe.BodyParentCacheShadowChecked} 不一致={PerfProbe.BodyParentCacheShadowMismatch}（不一致恒 0 = 缓存没给陈旧值）");
            Log.Message($"[验收] ㉖每物理步墙钟（第 25 条；比较前后请用同一固定场景，绝对值随机器/场景变）：{PerfProbe.StepClockReport()}");
            Log.Message($"[验收] ② 物理步 scratch 校验={PerfProbe.StepScratchChecked} 次、接触解算={PerfProbe.ContactsResolved} 次（前者 >0 = 每物理步都在校验容器复用；后者 >0 = 碰撞链真的解算到接触）");
            Log.Message($"[验收] ⑮网格 vs 旧扫候选：网格={PerfProbe.ExplGridCandidates} 旧扫={PerfProbe.ExplOldScanCandidates} 仅旧扫有={PerfProbe.ExplOldOnlyNoPartInRange}（旧球筛 100km 余量差异，非漏伤）");
            Log.Message($"[验收] ⑲Gooding 调用={Gooding.DebugTlambCalls} 触顶={Gooding.DebugSeriesCapHits} 实测最大迭代={Gooding.DebugMaxSeriesIter}/{Gooding.MAX_SERIES_ITER}（另有纯测试护栏）");
            Log.Message($"[验收] ㉕实体注册表：检查={PerfProbe.EntityRegistryChecks} 次 泄漏={PerfProbe.EntityRegistryLeaks}（应恒 0）");
Log.Message($"[验收] 组数：结束={GroupCount()}｜id 复用观察={PerfProbe.GroupIdReuseObserved} 次（信息量：现在应为 0——A5a 已改为持久单调计数器）");

            // 完整探针汇总（所有计数，便于逐项核对）；恢复常规限频
            PerfProbe.SummaryIntervalTicks = 300;
            PerfProbe.LogSummaryNow();

            string verdict = hard.Count > 0 || failures.Count > 0
                ? $"FAIL（不变式破坏 {hard.Count} 项 / 值判据失败 {failures.Count} 项，看上面红字）"
                : selectedTotal == 0
                    ? "PASS（只跑了电池，未勾选任何阶段）"
                    : covered == selectedTotal
                        ? $"PASS（硬指标全 0，{selectedTotal}/{selectedTotal} 勾选阶段覆盖）"
                        : $"PARTIAL（硬指标与值判据全过，覆盖 {covered}/{selectedTotal} 勾选阶段——未覆盖项请重跑对应场景或延长观察）";

            // ── 即时电池 + 引力场读数（值判据，与覆盖度分开报）──
            Log.Message("[验收] ═══════════ 引力场 / 制导参数（A0/A1/A2）═══════════");
            Log.Message($"[验收] {(batterySummary ?? "引力自检电池未运行")}（明细见上方 [引力自检] / [机制自检] 段落）");
            Log.Message($"[验收] ㉑引力场：根域影子 检查={PerfProbe.GravityRootChecked} 不一致={PerfProbe.GravityRootMismatch}｜子天体域差分={PerfProbe.GravitySubBodyProbes} 次，最近比值={PerfProbe.GravitySubBodyLastRatio:P4}（范围 {PerfProbe.GravitySubBodyMinRatio:P4}–{PerfProbe.GravitySubBodyMaxRatio:P4}）｜判读 0=旧行为(错) 0.1%–0.6%=潮汐(对) ~7%=全量和(错)");
            Log.Message($"[验收] ㉒弹头引力(A2)：建段 {PerfProbe.WarheadSegments} 次 最近 |a|={PerfProbe.WarheadAccel:F3} m/s²（主导项 {PerfProbe.WarheadAccelRatio:F2}×，应 0.2–5×；~100× = 参考系混用旧伪值） 指向行星中心余弦={PerfProbe.WarheadTowardCenterCos:F3}｜射弹段：重建={PerfProbe.ProjectileSegments} / 步={PerfProbe.ProjectileSteps}（预期 ≈1/120）");
            if (failures.Count > 0)
            {
                Log.Error($"[验收] 值判据失败 {failures.Count} 项：");
                foreach (var f in failures) Log.Error("[验收] 值判据失败：" + f);
            }
            else
            {
                Log.Message("[验收] 值判据：全部通过 ✓（根域影子 0 不一致、差分落在潮汐带、弹头引力 ≈主导项）");
            }

            Verdict = verdict;
            VerdictIsFail = hard.Count > 0 || failures.Count > 0;

            Log.Message("[验收] ═══ 总判定：" + verdict + " ═══");
            // 恢复玩家原来的详细日志开关（场景方法把它们开着不放）
            CombatManager.DebugDetailedCombat = savedDetailedCombat;
            CombatManager.DebugDetailedCollision = savedDetailedCollision;
            AutonomousVehicleManager.DebugDetailedGuidance = savedDetailedGuidance;

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
            Log.Message($"[验收] 合并态弹群 G{gid}：Loiter={ok} + 已排 ΔV=10 切向机动（physicsTick+30）→ 等群级燃烧查聚合器");
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

        /// <summary>覆盖度行的单格文案：✓ / 跳过（未勾选）/ 未覆盖（跑了但没证据）。</summary>
        private static string CoverLabel(int index, bool covered)
        {
            if (covered) return "✓";
            return IsStageEnabled(index) ? "未覆盖" : "跳过";
        }

        private static bool Covered(string prefix)
        {
            for (int i = 0; i < passes.Count; i++)
                if (passes[i].StartsWith(prefix, System.StringComparison.Ordinal)) return true;
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
