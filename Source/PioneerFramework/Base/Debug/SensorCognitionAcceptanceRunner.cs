using System.Collections.Generic;
using System.Text;
using PioneerFramework.Components;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **[V3] 认知层的一键验收（S2，2026-10-02）**。
    ///
    /// 自跑一个"观测者 + 匿踪目标"的最小场景，逐条验：
    ///   ① **范围内 ⇒ 接触出现**（目标 id / 来源 = 自舰探测 / 相位 = Live）；
    ///   ② **移出范围 ⇒ 立刻变影子**（仍在视图里、σ 长了 ⇒ "影子可用"）；
    ///   ③ **超过记忆窗口 ⇒ 失联**（点查询 false、全量读里也没有）；
    ///   ④ **回到范围内 ⇒ 一步之内立刻回到 Live**（"快进"那一半）；
    ///   ⑤ **点查询不触发刷新**（连续 100 次 `TryGetContact` 后 `lastUpdatePhysics` 不动）；
    ///   ⑥ **公开组走另一条路**（`IsStealth=false` ⇒ 进全量读、来源 = 公开投影，与记忆无关）。
    ///
    /// ## 场景口径（为什么这么配）
    /// · 观测者 = `CreateNoMapShipGroup` 造的船（自带 1000 km 传感器）；
    /// · **记忆窗口由数据给**（写 `BuildingTechBonus.memorySeconds = 6` s = 3 个物理步）——不是改判据，
    ///   而是走裁定 ② 的"观测者具名分量"这条真实通道 ⇒ 验收不必等 30 s；
    /// · 进/出范围的距离**由判据自己算**（`EffectiveDetectRange` 的 0.5× / 5×）⇒ 与目标尺寸解耦，不会误配；
    /// · 存档往返是**手动**项（点「认知快照」→ 存档 → 读档 → 再点 → 两行应一致），登记为 UNCHECKED。
    /// </summary>
    public static class SensorCognitionAcceptanceRunner
    {
        private enum Stage { Idle, AwaitSettle, MoveIn, MoveOut, WaitExpire, MoveBack, CheckPointQuery, CheckPublic }

        private const float ScenarioMemorySeconds = 6f;     // 3 个物理步（2 s/步）
        private const double SensorSensitivityMeters = 1.0e6;

        private static Stage stage = Stage.Idle;
        private static int stageStep;
        private static int observerGroupId = -1, targetGroupId = -1;
        private static int playerFaction = -1, enemyFaction = -1;
        private static double inRangeMeters, outOfRangeMeters;
        private static double sigmaLive, sigmaShadow;
        private static double lastUpdateAtShadow;
        private static bool sawLive, sawShadow, sawLost, sawRecover;

        public static bool Running => stage != Stage.Idle;

        public static void Run()
        {
            // ⚠ **快照必须在铺场之前**（2026-10-02 修）：原位置在 `SpawnTarget` **之后** ⇒
            //   `CleanupSince` 永远清不掉本次铺的观察者与目标 ⇒ 那两条船一路漏进后续用例
            //   （电子战总控实测：③视野对账时"总 2 组"、④组 5、⑤组 3，全是它）。
            PflTestKit.SnapshotGroups();
            if (Running) { Log.Message("[认知验收] 已在跑 ⇒ 忽略本次"); return; }
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var sensor = Current.Game?.GetComponent<SensorManager>();
            if (sg == null || mgr == null || sensor == null)
            {
                Log.Warning("[认知验收] 缺 SpaceGameComp / 天体管理器 / SensorManager ⇒ 跑不了");
                return;
            }

            playerFaction = Faction.OfPlayer?.loadID ?? 0;
            enemyFaction = FactionResolver.HostileFactionIdForPlayer();
            if (enemyFaction < 0 || enemyFaction == playerFaction)
            {
                PflTestKit.Begin("[V3] 认知层：点亮 / 影子 / 失联（一键）", "00 验收");
                PflTestKit.Unchecked("前置", "本局没有与玩家敌对的阵营（`HostileFactionIdForPlayer()` = -1）⇒ 匿踪判定跑不起来（同阵营不进判据）");
                return;
            }

            var planets = mgr.GetPlanets();
            int bodyId = planets != null && planets.Count > 0 ? planets[0].bodyId : 1;
            double mu = sg.GetBodyGM(bodyId);
            if (!(mu > 0.0)) { Log.Warning("[认知验收] 根天体 GM 非正"); return; }

            // 观测者：玩家阵营的船（自带传感器）
            double r = SpaceConstants.PlanetRadiusMeters + 500000.0;
            double v = System.Math.Sqrt(mu / r);
            var posObs = new DVector3(r, 0, 0);
            var velObs = new DVector3(0, 0, v);
            var obs = DebugShipSpawner.CreateNoMapShipGroup(sg, bodyId, posObs, velObs, out int obsEntityId,
                overThrust: false, circularOrbit: true);
            if (obs == null) { Log.Warning("[认知验收] 观测者创建失败（工质 Def 缺失？）"); return; }
            observerGroupId = obs.GroupId;
            // 记忆窗口走"观测者具名分量"的通道（裁定 ②）。
            // ⚠ 灵敏度是**加法**（部件级 1000 km + 建筑级 1000 km = 灵 2000 km，实测读数即此）——
            //   不是"钉成常数"；判据边界由 `EffectiveDetectRange` **实算**，所以与这里的值无关。
            obs.BuildingTechBonus = new SensorTech { sensitivityMeters = (float)SensorSensitivityMeters, memorySeconds = ScenarioMemorySeconds };

            // 匿踪目标：敌方阵营、小尺寸、无推进
            if (!SpawnTarget(sg, bodyId, posObs + new DVector3(0, 0, 500000.0), velObs, out targetGroupId))
            {
                Log.Warning("[认知验收] 目标创建失败");
                return;
            }

            var tech = obs.DetectTech;
            double range = DetectionCore.EffectiveDetectRange(Target(targetGroupId, sg)?.SignalStrength ?? 0.0, in tech, 0.0);
            inRangeMeters = range * 0.5;
            outOfRangeMeters = range * 5.0;

            PflTestKit.Begin("[V3] 认知层：点亮 / 影子 / 失联（一键）", "00 验收");
            PflTestKit.Note($"观测者 G{observerGroupId}（灵 {tech.sensitivityMeters / 1000f:F0} km · 记忆窗口 {ContactMemory.EffectiveMemorySeconds(tech.memorySeconds):F1} s）｜目标 G{targetGroupId}（匿踪）｜判据边界 {range / 1000.0:F0} km ⇒ 内 {inRangeMeters / 1000.0:F0} km / 外 {outOfRangeMeters / 1000.0:F0} km");

            sawLive = sawShadow = sawLost = sawRecover = false;
            stage = Stage.AwaitSettle;
            stageStep = 0;
        }

        public static void Abort()
        {
            if (!Running) return;
            PflTestKit.CleanupSince();
            PflTestKit.Abort();
            stage = Stage.Idle;
        }

        /// <summary>物理步驱动（空闲时零开销）。⚠ 在 `SensorManager` **之后**调用（读它本步刚写的视图）。</summary>
        public static void Tick()
        {
            if (stage == Stage.Idle) return;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var sensor = Current.Game?.GetComponent<SensorManager>();
            if (sg == null || sensor == null) { stage = Stage.Idle; return; }
            stageStep++;

            switch (stage)
            {
                case Stage.AwaitSettle:
                    if (stageStep < 2) return;
                    MoveTarget(sg, inRangeMeters);
                    stage = Stage.MoveIn; stageStep = 0;
                    break;

                case Stage.MoveIn:
                    if (stageStep < 2) return;                       // 给一拍扫描 + 一拍稳定
                    {
                        bool has = sensor.TryGetContact(playerFaction, targetGroupId, out var c);
                        var phase = sensor.ContactPhaseOf(playerFaction, targetGroupId);
                        sawLive = has && phase == TrackPhase.Live;
                        PflTestKit.Check(PflTestKit.Kind.Value, has && c.targetGroupId == targetGroupId,
                            "①范围内 ⇒ 接触出现（点查询拿到，id 对得上）",
                            $"拿到 {has}｜id {c.targetGroupId} vs 期望 {targetGroupId}｜相位 {phase}");
                        PflTestKit.Check(PflTestKit.Kind.Invariant, phase == TrackPhase.Live && c.source == ContactSource.OwnSensor,
                            "①刚刷新 ⇒ 相位 Live、来源 = 自舰探测",
                            $"相位 {phase}｜来源 {c.source}");
                        sigmaLive = c.positionSigma;
                        sigmaShadow = sigmaLive;
                        MoveTarget(sg, outOfRangeMeters);
                        stage = Stage.MoveOut; stageStep = 0;
                    }
                    break;

                case Stage.MoveOut:
                    if (stageStep < 2) return;
                    {
                        var phase = sensor.ContactPhaseOf(playerFaction, targetGroupId);
                        bool stillVisible = sensor.TryGetContact(playerFaction, targetGroupId, out var c);
                        sawShadow = phase == TrackPhase.Shadow && stillVisible;
                        sigmaShadow = c.positionSigma;
                        lastUpdateAtShadow = c.lastUpdatePhysics;
                        PflTestKit.Check(PflTestKit.Kind.Invariant, phase == TrackPhase.Shadow && stillVisible,
                            "②移出范围 ⇒ 立刻变影子（仍在视图里 —— 影子可被打击/制导）",
                            $"相位 {phase}｜点查询 {stillVisible}（σ {sigmaShadow:F0} m）");
                        PflTestKit.Check(PflTestKit.Kind.Value, sigmaShadow > sigmaLive,
                            "②影子期 σ 必须比新鲜时大（记忆不免费）",
                            $"新鲜 {sigmaLive:F0} m → 影子 {sigmaShadow:F0} m");
                        stage = Stage.WaitExpire; stageStep = 0;
                    }
                    break;

                case Stage.WaitExpire:
                    {
                        var phase = sensor.ContactPhaseOf(playerFaction, targetGroupId);
                        if (phase == TrackPhase.Shadow)
                        {
                            if (stageStep > 600) { PflTestKit.Unchecked("③记忆窗口", "等了 600 步还是影子 ⇒ 异常"); stage = Stage.Idle; return; }
                            return;                                   // 窗口（6 s = 3 步）很快就到
                        }
                        sawLost = phase == TrackPhase.Lost;
                        bool has = sensor.TryGetContact(playerFaction, targetGroupId, out _);
                        bool inView = AppendVisibleContains(sensor, targetGroupId);
                        PflTestKit.Check(PflTestKit.Kind.Invariant, sawLost && !has && !inView,
                            "③超过记忆窗口 ⇒ 失联（点查询 false、全量读里也没有）",
                            $"相位 {phase}｜点查询 {has}｜全量读含它 {inView}");
                        MoveTarget(sg, inRangeMeters);
                        stage = Stage.MoveBack; stageStep = 0;
                    }
                    break;

                case Stage.MoveBack:
                    if (stageStep < 1) return;
                    {
                        var phase = sensor.ContactPhaseOf(playerFaction, targetGroupId);
                        sawRecover = phase == TrackPhase.Live;
                        PflTestKit.Check(PflTestKit.Kind.Value, sawRecover,
                            "④回到范围内 ⇒ 一步之内回到 Live（快进那一半）",
                            $"相位 {phase}");
                        stage = Stage.CheckPointQuery; stageStep = 0;
                    }
                    break;

                case Stage.CheckPointQuery:
                    {
                        sensor.TryGetContact(playerFaction, targetGroupId, out var before);
                        for (int i = 0; i < 100; i++) sensor.TryGetContact(playerFaction, targetGroupId, out _);
                        sensor.TryGetContact(playerFaction, targetGroupId, out var after);
                        PflTestKit.Check(PflTestKit.Kind.Invariant, before.lastUpdatePhysics == after.lastUpdatePhysics,
                            "⑤点查询绝不触发刷新（100 次调用后时间戳不动）",
                            $"t {before.lastUpdatePhysics:F1} → {after.lastUpdatePhysics:F1}");
                        // 公开投影走另一条路：把目标改成"自报坐标"
                        var tg = Target(targetGroupId, sg);
                        if (tg != null) tg.IsStealth = false;
                        stage = Stage.CheckPublic; stageStep = 0;
                    }
                    break;

                case Stage.CheckPublic:
                    if (stageStep < 2) return;
                    {
                        bool inView = AppendVisibleContains(sensor, targetGroupId, out var src);
                        PflTestKit.Check(PflTestKit.Kind.Value, inView && src == ContactSource.Public,
                            "⑥公开组走另一条路（全量读含它、来源 = 公开投影，与记忆无关）",
                            $"全量读含它 {inView}｜来源 {src}");
                        var tgBack = Target(targetGroupId, sg);
                        if (tgBack != null) tgBack.IsStealth = true;
                        PflTestKit.CheckUnchecked("⑦存档往返（手动）",
                            "点「认知快照」→ 存档 → 读档 → 再点一次 ⇒ 两行应逐字一致（记忆/接触必须落盘）");
                        PflTestKit.Check(PflTestKit.Kind.Coverage, sawLive && sawShadow && sawLost && sawRecover,
                            "总览：四态流转都真的走到过",
                            $"Live {sawLive}｜Shadow {sawShadow}｜Lost {sawLost}｜恢复 {sawRecover}");
                        PflTestKit.End();
                        PrintSnapshot();
                        PflTestKit.CleanupSince();
                        stage = Stage.Idle;
                    }
                    break;
            }
        }

        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// **[E5] 假目标（诱饵）的机器判据**（2026-10-02）：注入 → 被看到；观测者静默 ⇒ 立刻看不到（DRFM 语义）；
        /// 抗欺骗够高 ⇒ **识破（深层提示：仍显示，但打 isDecoy 标记）**；生命期到 ⇒ 消失。全部走生产通道
        ///（`InjectDecoy` + `AppendVisible`），不手搓接触。
        /// </summary>
        public static void CheckDecoys()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var sensor = Current.Game?.GetComponent<SensorManager>();
            if (sg == null || mgr == null || sensor == null) { Log.Warning("[诱饵验收] 缺组件"); return; }

            int player = Faction.OfPlayer?.loadID ?? 0;
            int enemy = FactionResolver.HostileFactionIdForPlayer();
            PflTestKit.Begin("[E5] 假目标：注入 / 依赖开机 / 识别破 / 生命期", "00 验收");
            if (enemy < 0 || enemy == player) { PflTestKit.Unchecked("前置", "本局没有敌对阵营 ⇒ 验不了"); return; }

            var planets = mgr.GetPlanets();
            int bodyId = planets != null && planets.Count > 0 ? planets[0].bodyId : 1;
            double mu = sg.GetBodyGM(bodyId);
            if (!(mu > 0.0)) { PflTestKit.Unchecked("前置", "根天体 GM 非正"); return; }
            PflTestKit.SnapshotGroups();

            double r = SpaceConstants.PlanetRadiusMeters + 500000.0;
            double v = System.Math.Sqrt(mu / r);
            var obsPos = new DVector3(r, 0, 0);
            var obsVel = new DVector3(0, 0, v);
            var obs = DebugShipSpawner.CreateNoMapShipGroup(sg, bodyId, obsPos, obsVel, out _, overThrust: false, circularOrbit: true);
            if (obs == null) { PflTestKit.Unchecked("前置", "观测者创建失败"); PflTestKit.CleanupSince(); return; }
            obs.BuildingTechBonus = new SensorTech { sensitivityMeters = 1.0e6f, memorySeconds = 6f, eccmResist = 0f };

            const float Strength = 2f;      // 欺骗强度：观测者 eccmResist > 它才算识破
            var decoyPos = obsPos + new DVector3(0, 0, 300000.0);

            // ① 观测者**静默**时：注入的假接触不该被"照到"（DRFM：对方雷达不开机就骗不了它）
            obs.ActivePingEnabled = false;
            int quietId = sensor.InjectDecoy(enemy, decoyPos, obsVel, Strength, 60.0);
            sensor.OnPhysicsStepCompleted();                 // 手动驱动一步扫描（本条是即时判定，不等物理步）
            bool seenQuiet = AppendVisibleContainsDecoy(sensor, quietId, out _, out _);
            PflTestKit.Check(PflTestKit.Kind.Invariant, !seenQuiet,
                "①观测者静默 ⇒ 诱饵看不见（欺骗依赖对方主动探测开机，DRFM 语义）",
                $"静默时视图里含诱饵 = {seenQuiet}（诱饵 id {quietId}）");

            // ② 开机 ⇒ 该假接触出现（与真接触同构：同一个列表、同样的读法）
            obs.ActivePingEnabled = true;
            sensor.OnPhysicsStepCompleted();
            bool seenOn = AppendVisibleContainsDecoy(sensor, quietId, out var srcOn, out var isMarked);
            PflTestKit.Check(PflTestKit.Kind.Value, seenOn && srcOn == ContactSource.Decoy && !isMarked,
                "②开机后被「照到」⇒ 假接触进视图（来源 = 诱饵；抗欺骗不够 ⇒ **不**带识破标记）",
                $"视图中 = {seenOn}｜来源 {srcOn}｜识破标记 {isMarked}（观测者抗欺骗 {obs.DetectTech.eccmResist:F1} vs 欺骗强度 {Strength:F1}）");

            // ③ 识破（深层提示）：把观测者的抗欺骗抬到欺骗强度之上 ⇒ **仍然显示**，但带标记
            obs.BuildingTechBonus = new SensorTech { sensitivityMeters = 1.0e6f, memorySeconds = 6f, eccmResist = 5f };
            sensor.OnPhysicsStepCompleted();
            bool seenHl = AppendVisibleContainsDecoy(sensor, quietId, out _, out var isMarkedHl);
            PflTestKit.Check(PflTestKit.Kind.Invariant, seenHl && isMarkedHl,
                "③识破 = **深层提示**（技术够高才看得到这是假的：接触仍在，但打上 isDecoy 标记）",
                $"视图中 = {seenHl}｜识破标记 {isMarkedHl}（抗欺骗 5.0 > 强度 {Strength:F1}）");

            // ③b **去伪存真**（用户 2026-10-02 裁定：同阵营直接共享 ⇒ 识破标记**单调**）：
            //   把观测者抗欺骗降回低值（它自己不再识破）再扫一次 ⇒ 标记**必须仍在**
            //   （否则会出现"没识破的成员把标记洗回去" = 后写者赢的假融合）。
            obs.BuildingTechBonus = new SensorTech { sensitivityMeters = 1.0e6f, memorySeconds = 6f, eccmResist = 0f };
            sensor.OnPhysicsStepCompleted();
            bool seenLow = AppendVisibleContainsDecoy(sensor, quietId, out _, out var stillMarked);
            PflTestKit.Check(PflTestKit.Kind.Invariant, seenLow && stillMarked,
                "③b 去伪存真：同阵营共享 ⇒ 识破标记**单调**（低抗欺骗成员不许把标记洗掉）",
                $"降回抗欺骗 0.0 后再扫：视图中 = {seenLow}｜标记仍在 = {stillMarked}");

            // ④ 生命期：注入一条瞬时诱饵 ⇒ 过期即不出现（读时判，不扫描）
            // ⚠ 用 **0** 而不是"很小的正数"：本用例是**一次性动作**，两次调用之间**时钟不前进**
            //   （上一版用 0.001 s ⇒ `now >= expire` 为假 ⇒ 判据假红；那是我的算术，不是机制错）。
            int goneId = sensor.InjectDecoy(enemy, decoyPos, obsVel, Strength, 0.0);
            sensor.OnPhysicsStepCompleted();
            bool seenGone = AppendVisibleContainsDecoy(sensor, goneId, out _, out _);
            PflTestKit.Check(PflTestKit.Kind.Invariant, !seenGone,
                "④生命期到 ⇒ 消失（假接触只受自己的生命期管，不受记忆窗口管）",
                $"过期诱饵仍在视图 = {seenGone}");

            sensor.RemoveDecoy(quietId);
            sensor.RemoveDecoy(goneId);
            PflTestKit.CheckUnchecked("⑤存档往返（手动）",
                "注入一条长生命期诱饵 → 存档 → 读档 → 它应还在（假目标**没有真值源 ⇒ 必须落盘**，设计稿 §5.2）");

            PrintSnapshot();
            PflTestKit.End();
            PflTestKit.CleanupSince();
        }

        /// <summary>
        /// **[E3]/[N6] EW 浅层的机器判据**（S4，2026-10-02）：干扰按住 → 目标看不见；目标拉近 → **烧穿**重现；
        /// 干扰机开机自己变响（W1）；开火揭示 +基准。
        /// 全部走**真判据/真通道**：干扰效力由**部件**（`JammerComponentData`）给，判据用 `DetectionCore`，
        /// 不手搓阈值、不留测试专用后门。
        /// </summary>
        public static void CheckJammingAndReveal()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var sensor = Current.Game?.GetComponent<SensorManager>();
            if (sg == null || mgr == null || sensor == null) { Log.Warning("[EW 验收] 缺组件"); return; }

            int player = Faction.OfPlayer?.loadID ?? 0;
            int enemy = FactionResolver.HostileFactionIdForPlayer();
            PflTestKit.Begin("[V3]/[E3] EW 浅层：干扰抬高阈值 + 烧穿 + 开火揭示", "00 验收");
            if (enemy < 0 || enemy == player)
            {
                PflTestKit.Unchecked("前置", "本局没有敌对阵营 ⇒ 验不了（同阵营不进判据）");
                return;
            }

            var planets = mgr.GetPlanets();
            int bodyId = planets != null && planets.Count > 0 ? planets[0].bodyId : 1;
            double mu = sg.GetBodyGM(bodyId);
            if (!(mu > 0.0)) { PflTestKit.Unchecked("前置", "根天体 GM 非正"); return; }
            PflTestKit.SnapshotGroups();

            double r = SpaceConstants.PlanetRadiusMeters + 500000.0;
            double v = System.Math.Sqrt(mu / r);
            var obsPos = new DVector3(r, 0, 0);
            var obsVel = new DVector3(0, 0, v);

            var obs = DebugShipSpawner.CreateNoMapShipGroup(sg, bodyId, obsPos, obsVel, out _, overThrust: false, circularOrbit: true);
            if (obs == null) { PflTestKit.Unchecked("前置", "观测者创建失败"); PflTestKit.CleanupSince(); return; }
            obs.BuildingTechBonus = new SensorTech { sensitivityMeters = 1.0e6f, memorySeconds = 6f };

            // 干扰机：敌方、**部件级**给效力（`JammerComponentData`）——与生产同一条通道
            const float JamStrength = 2f;
            const double JammerDist = 100000.0;
            var jammer = DebugShipSpawner.CreateNoMapShipGroup(sg, bodyId, obsPos + new DVector3(0, 0, JammerDist), obsVel,
                out int jamEid, overThrust: false, circularOrbit: true);
            if (jammer == null) { PflTestKit.Unchecked("前置", "干扰机创建失败"); PflTestKit.CleanupSince(); return; }
            jammer.FactionId = enemy;
            var je = sg.GetEntityById(jamEid);
            if (je != null)
            {
                je.FactionId = enemy;
                if (je.Parts.Count > 0) je.Parts[0].AddComponent(new JammerComponentData { jamStrength = JamStrength });
                je.InvalidateStats();       // ⚠ 部件级失效**不上行**（[C4] 登记的既有缺口）⇒ 实体级必须显式
                jammer.InvalidateSignal();  // ⇒ 再置群级脏
            }

            // 目标：敌方匿踪弹（SpawnTarget 用 enemyFaction）
            const double FarDist = 240000.0;
            if (!SpawnTarget(sg, bodyId, obsPos + new DVector3(0, 0, FarDist), obsVel, out int tgtId))
            { PflTestKit.Unchecked("前置", "目标创建失败"); PflTestKit.CleanupSince(); return; }

            var tech = obs.DetectTech;
            double sTgt = Target(tgtId, sg)?.SignalStrength ?? 0.0;
            double jam = DetectionCore.JamEffort(JamStrength, JammerDist);

            PflTestKit.Note($"观测者灵 {tech.sensitivityMeters / 1000f:F0} km｜干扰机：强度 {JamStrength:F1} @ {JammerDist / 1000.0:F0} km ⇒ J_eff={jam:F2}｜目标 S={sTgt:F4} @ {FarDist / 1000.0:F0} km");

            // ① 干扰关机：基线可见
            bool baseVisible = DetectionCore.IsVisible(sTgt, FarDist, in tech, 0.0);
            PflTestKit.Check(PflTestKit.Kind.Value, baseVisible,
                "①无干扰时看得见（基线；否则下面两条无从谈起）",
                $"有效距离 {DetectionCore.EffectiveDetectRange(sTgt, in tech, 0.0) / 1000.0:F0} km vs 距离 {FarDist / 1000.0:F0} km");

            // ② 干扰开机：同一目标被按住
            bool jammedVisible = DetectionCore.IsVisible(sTgt, FarDist, in tech, jam);
            PflTestKit.Check(PflTestKit.Kind.Invariant, !jammedVisible,
                "②干扰开机 ⇒ 抬高阈值、目标被按住（看不见）",
                $"J_eff={jam:F2}｜有效距离 {DetectionCore.EffectiveDetectRange(sTgt, in tech, jam) / 1000.0:F0} km < 距离 {FarDist / 1000.0:F0} km");

            // ③ 烧穿（免费推论）：干扰不动、目标拉近 ⇒ 必然重新可见。
            // ⚠ 距离**由判据自己算**（0.5× / 1.5× 被压制后的有效距离）——不拍脑袋写系数：
            //   上一版写死"远距 × 0.25"，而本例 J_eff=200 把有效距离压到 48 km ⇒ 60 km 仍在外面、判据假红，
            //   那是**场景算术**不是机制错。两侧都钉：里面必可见、外面必不可见（这就是"恰好穿过一次"）。
            double effJammed = DetectionCore.EffectiveDetectRange(sTgt, in tech, jam);
            double insideD = effJammed * 0.5;
            double outsideD = effJammed * 1.5;
            bool insideVisible = DetectionCore.IsVisible(sTgt, insideD, in tech, jam);
            bool outsideVisible = DetectionCore.IsVisible(sTgt, outsideD, in tech, jam);
            PflTestKit.Check(PflTestKit.Kind.Value, insideVisible && !outsideVisible,
                "③烧穿：干扰不动、目标拉近 ⇒ 在有效距离处恰好翻真一次（内可见 / 外不可见）",
                $"被压制后有效距离 {effJammed / 1000.0:F0} km ⇒ 内 {insideD / 1000.0:F0} km 可见={insideVisible}｜外 {outsideD / 1000.0:F0} km 可见={outsideVisible}");

            // ④ W1 第二开关：干扰开机自己变响（干扰机必须辐射）
            jammer.JammingEnabled = false;
            double jOff = jammer.SignalStrength;
            jammer.JammingEnabled = true;
            double jOn = jammer.SignalStrength;
            PflTestKit.Check(PflTestKit.Kind.Value,
                System.Math.Abs((jOn - jOff) - SignalModel.JamMultiplier) <= 1e-6,
                "④干扰开机自己变响（W1：干扰机必须辐射才能干扰）",
                $"升幅 {jOn - jOff:F3} vs 期望 {SignalModel.JamMultiplier:F1}（关机态 {jOff:F3}）");
            jammer.JammingEnabled = false;

            // ⑤ 开火揭示：一次开火 ⇒ +基准（窗口内）
            double sBefore = obs.SignalStrength;
            obs.NotifyWeaponFire(TimeScaleManager.GetPhysicsTickSafe());
            double sAfter = obs.SignalStrength;
            PflTestKit.Check(PflTestKit.Kind.Value,
                System.Math.Abs((sAfter - sBefore) - SignalModel.RevealMultiplier) <= 1e-6,
                "⑤开火揭示：开火者信号 +基准（窗口内）",
                $"升幅 {sAfter - sBefore:F3} vs 期望 {SignalModel.RevealMultiplier:F1}｜窗口 {SignalModel.RevealWindowSeconds:F0} s（到期靠**时间流逝**，读时检查）");
            PflTestKit.CheckUnchecked("⑥揭示窗口到期回落",
                $"等 {SignalModel.RevealWindowSeconds:F0} s（= {SignalModel.RevealWindowSeconds / 2.0:F0} 个物理步）后再读同一群的 S 应回落 —— 手动观察即可");

            PrintSnapshot();
            PflTestKit.End();
            PflTestKit.CleanupSince();
        }

        /// <summary>
        /// **视图对账**（[V3] 消费方的机器判据，2026-10-02 S3）：世界视图"画不画 / 点不点得到"用的
        /// 准入判据（`SensorManager.PlayerCanSee`）必须与认知层的可见集**逐条对上**：
        ///   应可见 = 公开组（不匿踪） + 我方单位（含匿踪） + 在册且未失联的接触
        ///   （三类互斥：公开组不是匿踪⇒没有接触；我方单位被同阵营剪枝⇒没有接触）
        /// ⇒ 这条判据分辨两种坏法：**漏画**（认知层算得出、视图看不见）与**多画**（画了看不见的棋子）。
        /// 注意它**不改** dev 真值开关的状态（开关开着时本条自然红——那是预期，别在开着的时候跑）。
        /// </summary>
        public static void CheckViewReconciliation()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var sensor = Current.Game?.GetComponent<SensorManager>();
            if (sg == null || sensor == null)
            {
                Log.Warning("[视图对账] 缺 SpaceGameComp / SensorManager");
                return;
            }
            var player = Faction.OfPlayer;
            if (player == null) { Log.Warning("[视图对账] 无玩家阵营"); return; }

            // ⚠ **自铺一个小场景**（2026-10-02 修）：空场景下"可见 0 == 应为 0"是**平凡成立**
            //   （本库明令禁止的假绿——"0 个样本下失败数 == 0"不是通过）。三组里应当只见两组：
            //   **公开船**（可见）+ **我方匿踪弹**（自有遥测 ⇒ 可见）+ **远处敌匿踪弹**（5,000 km ⇒ 看不见）。
            PflTestKit.SnapshotGroups();
            var mgr0 = Current.Game?.GetComponent<CelestialBodyManager>();
            var planets0 = mgr0?.GetPlanets();
            int bodyId0 = planets0 != null && planets0.Count > 0 ? planets0[0].bodyId : 1;
            if (sg.GetBodyGM(bodyId0) > 0.0)
            {
                double r0 = SpaceConstants.PlanetRadiusMeters + 500000.0;
                double v0 = System.Math.Sqrt(sg.GetBodyGM(bodyId0) / r0);
                var p0 = new DVector3(r0, 0, 0);
                var vv0 = new DVector3(0, 0, v0);
                DebugShipSpawner.CreateNoMapShipGroup(sg, bodyId0, p0, vv0, out _, overThrust: false, circularOrbit: true);   // 公开船
                SpawnTarget(sg, bodyId0, p0 + new DVector3(0, 0, 200000.0), vv0, out _, player.loadID);                        // 我方匿踪弹
                SpawnTarget(sg, bodyId0, p0 + new DVector3(0, 0, 5000000.0), vv0, out _);                                     // 远处敌匿踪弹
            }

            int total = 0, visible = 0, pub = 0, own = 0, tracked = 0;
            foreach (var g in sg.GetAllGroups())
            {
                if (g == null || !g.IsActive) continue;
                total++;
                if (sensor.PlayerCanSee(g)) visible++;
                if (!g.IsStealth) pub++;
                else if (g.FactionId == player.loadID) own++;
            }
            double now = TimeScaleManager.GetPhysicsTickSafe();
            // 在册接触（未失联）——按"该阵营里被真正记着的目标"计数（与画出来的那批同源）
            var buf = visibleScratch;
            buf.Clear();
            sensor.AppendVisible(player.loadID, buf);
            // AppendVisible 含公开组 + 我方单位 + 接触 ⇒ 与上面三类同口径；这里只取"匿踪且非我方"的那批
            for (int i = 0; i < buf.Count; i++)
            {
                var c = buf[i];
                if (c.source != ContactSource.Public) tracked++;
            }

            int expected = pub + own + tracked;
            PflTestKit.Begin("[V3] 视图对账（绘制/点击准入 = 认知层可见集）", "00 验收");
            PflTestKit.Check(PflTestKit.Kind.Invariant, visible == expected,
                "世界棋子的可见数 = 公开 + 我方 + 在册接触（不漏画、不多画）",
                $"总 {total} ⇒ 可见 {visible} vs 应为 {expected}（公开 {pub} + 我方 {own} + 接触 {tracked}）｜dev 真值开关 {SensorManager.DebugShowGroundTruth}｜t={now:F1}");
            PflTestKit.End();
            PflTestKit.CleanupSince();      // 清掉本对账自铺的场（快照取在铺场之前）
        }

        /// <summary>认知快照（存档往返用的手动读数；也只读+只打日志）。</summary>
        public static void PrintSnapshot()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var sensor = Current.Game?.GetComponent<SensorManager>();
            if (sg == null || sensor == null) { Log.Warning("[认知快照] 无 SpaceGameComp / SensorManager"); return; }
            double now = TimeScaleManager.GetPhysicsTickSafe();
            var sb = new StringBuilder();
            sb.AppendFormat("[认知快照] t={0:F1}｜组 {1}｜各阵营接触：", now, CountGroups(sg));
            foreach (int fid in new[] { playerFaction, enemyFaction })
            {
                if (fid < 0) continue;
                int n = sensor.TrackCount(fid);
                sb.AppendFormat("\n  阵营 {0}：{1} 条", fid, n);
            }
            sb.Append("\n  （判据口径：记忆窗口 = 观测者的 `SensorTech.memorySeconds`；σ 随影龄涨）");
            Log.Message(sb.ToString());
        }

        private static void MoveTarget(SpaceGameComp sg, double distanceMeters)
        {
            var obs = sg.GetGroupById(observerGroupId);
            var tgt = sg.GetGroupById(targetGroupId);
            if (obs == null || tgt == null) return;
            var dir = new DVector3(0, 1, 0);                          // 与观测者拉开一个任意方向
            tgt.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)(obs.CurrentOrbit.positionSystem.v + dir * distanceMeters),
                velocitySystem = obs.CurrentOrbit.velocitySystem,
                rotation = Quaternion.identity,
            };
        }

        private static bool AppendVisibleContains(SensorManager sensor, int targetId)
            => AppendVisibleContains(sensor, targetId, out _);

        private static bool AppendVisibleContains(SensorManager sensor, int targetId, out ContactSource src)
            => AppendVisibleContains(sensor, targetId, out src, out _);

        private static bool AppendVisibleContains(SensorManager sensor, int targetId, out ContactSource src, out bool isDecoy)
        {
            src = ContactSource.Public;
            isDecoy = false;
            var buf = visibleScratch;
            buf.Clear();
            sensor.AppendVisible(playerFaction, buf);
            for (int i = 0; i < buf.Count; i++)
                if (buf[i].targetGroupId == targetId) { src = buf[i].source; isDecoy = buf[i].isDecoy; return true; }
            return false;
        }

        /// <summary>假接触版（按 decoyId 找——它们没有 targetGroupId）。</summary>
        private static bool AppendVisibleContainsDecoy(SensorManager sensor, int decoyId, out ContactSource src, out bool isDecoy)
        {
            src = ContactSource.Public;
            isDecoy = false;
            var buf = visibleScratch;
            buf.Clear();
            sensor.AppendVisible(playerFaction, buf);
            for (int i = 0; i < buf.Count; i++)
                if (buf[i].decoyId == decoyId) { src = buf[i].source; isDecoy = buf[i].isDecoy; return true; }
            return false;
        }

        private static readonly List<Contact> visibleScratch = new();

        private static SpaceEntityGroup? Target(int groupId, SpaceGameComp sg) => sg.GetGroupById(groupId);

        private static int CountGroups(SpaceGameComp sg)
        {
            int n = 0;
            foreach (var _ in sg.GetAllGroups()) n++;
            return n;
        }

        /// <summary>匿踪目标：小尺寸单部件、无推进、不广播坐标（[N3] 的导弹口径）。</summary>
        private static bool SpawnTarget(SpaceGameComp sg, int bodyId, DVector3 pos, DVector3 vel, out int groupId, int faction = -1)
        {
            groupId = -1;
            int gid = sg.AllocGroupId();
            int eid = sg.AllocEntityId();
            var group = new SpaceEntityGroup(gid, faction < 0 ? enemyFaction : faction)
            {
                IsMovable = true,
                IsCollidable = true,
                IsStealth = true,
                CelestialBodyId = bodyId,
            };
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)pos,
                velocitySystem = (SystemVec3)vel,
                rotation = Quaternion.identity,
            };
            sg.RegisterGroup(group);

            var entity = new SpaceEntity(eid, enemyFaction)
            {
                HasMap = false,
                IsMapArchived = false,
                IsCollidable = true,
                IsMovable = true,
                CanInteract = false,
                IsAffectedByGravity = true,
            };
            entity.GroupId = gid;
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);
            entity.mainAxisShip = new DVector3(0, 0, -1);
            var part = new SpacePart
            {
                partId = 1,
                defName = "PF_ShipPart_Cube",
                positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(10.0, 10.0, 30.0),
                hp = 150f,
                isIndestructible = false,
            };
            part.AddComponent(new HullMassComponent { mass = 400f });
            entity.SetParts(new List<SpacePart> { part });
            sg.RegisterEntity(entity);
            group.AddEntity(eid);
            groupId = gid;
            return true;
        }
    }
}
