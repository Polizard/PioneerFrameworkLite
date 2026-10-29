using System.Collections.Generic;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 火箭爬升段运行态（可序列化）。由 RocketLaunchController 每物理步驱动，
    /// 存 groupId/bodyId + 冻结制导参数（冗余存储，载入存档不依赖 Def 推导）。
    /// </summary>
    public class RocketLaunchState : IExposable
    {
        public int groupId = -1;
        public int bodyId = -1;
        public double rTarget;
        public double tau = 600.0;
        public double vRadMax = 300.0;
        public double deadband = 0.5;
        public double tolR = 10000.0;
        public double tolV = 10.0;
        public double planetRadius;
        public double vAscent = 500.0;
        public double altPitchStart = 20000.0;
        public double altPitchEnd = 450000.0;
        public double progradeSign = -1.0;
        /// <summary>通道增益（2026-10-06 抗延迟；见 `LaunchGuidanceParams.radialGain`）。0 = 旧行为。</summary>
        public double radialGain;
        public double tanGain;

        public LaunchGuidanceParams ToParams() => new() {
            rTarget = rTarget,
            tau = tau,
            vRadMax = vRadMax,
            deadband = deadband,
            tolR = tolR,
            tolV = tolV,
            planetRadius = planetRadius,
            vAscent = vAscent,
            altPitchStart = altPitchStart,
            altPitchEnd = altPitchEnd,
            progradeSign = progradeSign
        };

        public void FromParams(in LaunchGuidanceParams p)
        {
            rTarget = p.rTarget;
            tau = p.tau;
            vRadMax = p.vRadMax;
            deadband = p.deadband;
            tolR = p.tolR;
            tolV = p.tolV;
            planetRadius = p.planetRadius;
            vAscent = p.vAscent;
            altPitchStart = p.altPitchStart;
            altPitchEnd = p.altPitchEnd;
            progradeSign = p.progradeSign;
            radialGain = p.radialGain;
            tanGain = p.tanGain;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref groupId, "groupId", -1);
            Scribe_Values.Look(ref bodyId, "bodyId", -1);
            Scribe_Values.Look(ref rTarget, "rTarget", 0.0);
            Scribe_Values.Look(ref tau, "tau", 600.0);
            Scribe_Values.Look(ref vRadMax, "vRadMax", 300.0);
            Scribe_Values.Look(ref deadband, "deadband", 0.5);
            Scribe_Values.Look(ref tolR, "tolR", 10000.0);
            Scribe_Values.Look(ref tolV, "tolV", 10.0);
            Scribe_Values.Look(ref planetRadius, "planetRadius", 0.0);
            Scribe_Values.Look(ref vAscent, "vAscent", 500.0);
            Scribe_Values.Look(ref altPitchStart, "altPitchStart", 20000.0);
            Scribe_Values.Look(ref altPitchEnd, "altPitchEnd", 450000.0);
            Scribe_Values.Look(ref progradeSign, "progradeSign", -1.0);
            Scribe_Values.Look(ref radialGain, "radialGain", 0.0);
            Scribe_Values.Look(ref tanGain, "tanGain", 0.0);
        }
    }

    /// <summary>
    /// 火箭爬升段控制器（设计 §4/§8）：发射链注册后，每物理步对火箭 group 做
    /// 持续引导（EndBurn → LaunchGuidanceCore.ComputeStep → StartBurn(dv)），
    /// 到达目标窗口后清 burn、解除行星碰撞豁免、切给 BeginCircularize 入轨。
    /// 与 BoardingManager 同构（GameComponent，RimWorld 自动实例化）。
    /// 职责边界：不做实体/group 创建（LaunchService）、不做物理积分（SpaceGameComp）、
    /// 不做坐标映射（GetSurfaceState）、不做入轨结算（BeginCircularize）。
    /// </summary>
    public class RocketLaunchController : GameComponent
    {
        /// <summary>详细日志开关（每物理步引导状态）。默认关，避免长时间爬升刷屏。</summary>
        public static bool DebugDetailedLaunch ;
        /// <summary>状态探针间隔（真实 tick）。0 = 关闭探针日志；&gt;0 时每间隔打印全部活动爬升状态。</summary>
        public static int ProbeInterval ;
        private int probeNextTick ;

        /// <summary>爬升状态探针（测试面板/探针日志共用，不序列化）。</summary>
        public struct LaunchProbe
        {
            public int groupId;
            public double r;      // 当前地心半径 (m)
            public double deltaR; // 距目标半径 (m)
            public double vMag;   // 当前速度 (m/s)
            public double vRad;   // 径向速度 (m/s)（>0 爬升 / <0 坠落，观察用）
            public double dvMag;  // 引导指令 |dv| (m/s)
            public double fuelKg; // 剩余工质 (kg)
            public bool burning;  // 是否正在燃烧
            public bool arrived;  // 是否到达窗口
        }

        /// <summary>爬升结算条目（测试面板历史用，可序列化）：入轨成功 / 失联销毁。</summary>
        public struct LaunchLogEntry : IExposable
        {
            public int tick;
            public int groupId;
            public string outcome; // Orbited / Destroyed / CircularizeFailed
            public string detail;

            public LaunchLogEntry(int tick, int groupId, string outcome, string detail)
            {
                this.tick = tick;
                this.groupId = groupId;
                this.outcome = outcome;
                this.detail = detail;
            }

            public void ExposeData()
            {
                Scribe_Values.Look(ref tick, "tick", 0);
                Scribe_Values.Look(ref groupId, "groupId", -1);
                Scribe_Values.Look(ref outcome, "outcome", null);
                Scribe_Values.Look(ref detail, "detail", null);
            }
        }

        private const int MaxLogEntries = 20;

        private List<RocketLaunchState> activeLaunches = new();
        private List<LaunchLogEntry> recentLog = new();

        public RocketLaunchController(Game game) { }

        private SpaceGameComp SpaceGame => Current.Game?.GetComponent<SpaceGameComp>();

        public IReadOnlyList<RocketLaunchState> ActiveLaunches => activeLaunches;

        /// <summary>结算历史（最新在前，最多 MaxLogEntries 条）。测试面板展示用。</summary>
        public IReadOnlyList<LaunchLogEntry> RecentLog => recentLog;

        /// <summary>追加结算历史条目（自动去重 + 上限裁剪）。</summary>
        private void AddLog(int tick, int groupId, string outcome, string detail)
        {
            recentLog.Insert(0, new LaunchLogEntry(tick, groupId, outcome, detail));
            if (recentLog.Count > MaxLogEntries)
                recentLog.RemoveRange(MaxLogEntries, recentLog.Count - MaxLogEntries);
        }

        /// <summary>计算单个爬升的运行态探针（无记忆：直接按当前轨道算，不修改任何状态）。</summary>
        public LaunchProbe ProbeState(RocketLaunchState st)
        {
            var sg = SpaceGame;
            var g = sg?.GetGroupById(st.groupId);
            if (g == null)
                return new LaunchProbe { groupId = st.groupId, r = double.NaN, deltaR = double.NaN, vMag = double.NaN, dvMag = double.NaN, fuelKg = double.NaN };

            var prms = st.ToParams();
            double r = g.CurrentOrbit.positionSystem.magnitude;
            var step = LaunchGuidanceCore.ComputeStep(g.CurrentOrbit.positionSystem.v, GuidanceVelocity(g), sg.GetBodyGM(st.bodyId), prms);
            var src = sg.GetGroupPropulsionSource(g);
            return new LaunchProbe
            {
                groupId = st.groupId,
                r = r,
                deltaR = prms.rTarget - r,
                vMag = g.CurrentOrbit.velocitySystem.magnitude,
                vRad = DVector3.Dot(g.CurrentOrbit.velocitySystem.v, g.CurrentOrbit.positionSystem.normalized.v),
                dvMag = step.dv.magnitude,
                fuelKg = src != null ? src.TotalPropellantMass : 0.0,
                burning = g.HasActiveBurn,
                arrived = step.arrived
            };
        }

        /// <summary>
        /// 注册一次火箭爬升段：置行星碰撞豁免（出生在大气层内），之后每物理步闭环制导。
        /// [A6]（2026-10-06）：**三步交接之①** —— 进大气档 = 设"只算中心天体"的引力掩码
        /// （只在气动不可忽略时设 ⇒ ρ 数值未定/无大气 ⇒ 与旧行为逐位相同）。
        /// </summary>
        public bool RegisterLaunch(SpaceEntityGroup group, int bodyId, in LaunchGuidanceParams prms)
        {
            if (group == null) return false;

            group.exemptPlanetCollision = true;
            var st = new RocketLaunchState { groupId = group.GroupId, bodyId = bodyId };
            st.FromParams(prms);
            activeLaunches.Add(st);

            EnterAtmosphereGravityTier(group, bodyId);

            double gm = SpaceGame?.GetBodyGM(bodyId) ?? 0.0;
            double vC = prms.rTarget > 0.0 && gm > 0.0 ? System.Math.Sqrt(gm / prms.rTarget) : 0.0;
            Log.Message($"[RocketLaunch] 爬升段开始: group={group.GroupId} body={bodyId} rTarget={prms.rTarget / 1000.0:F0}km vC={vC:F1}m/s");
            return true;
        }

        /// <summary>
        /// **[A6] 三步交接之①：进大气档** —— 设"只算中心天体"的引力掩码（掩码 = 本系成员剔除中心天体）。
        /// 只在**气动不可忽略**（ρ/ρ₀ &gt; e^−k）时设；惰性剖面/数值未定 ⇒ 不设（零行为变化）。
        /// 闩锁置位后 `SetGravityMask` 拒绝 ⇒ 不可能二次翻转。
        /// </summary>
        private void EnterAtmosphereGravityTier(SpaceEntityGroup group, int bodyId)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var body = mgr?.GetBodyById(bodyId);
            if (mgr == null || body == null) return;

            double altitude = group.CurrentOrbit.positionSystem.magnitude - body.radius;
            if (!body.Atmosphere.IsAerodynamicallySignificant(altitude, body.atmosphereRestoreScaleHeights))
                return;

            var members = mgr.GetSystemMembers(bodyId);
            var mask = new List<int>(members.Count);
            for (int i = 0; i < members.Count; i++)
                if (members[i].bodyId != bodyId) mask.Add(members[i].bodyId);
            if (!group.SetGravityMask(mask)) return;   // 闩锁拒绝（或空掩码）

            Log.Message($"[RocketLaunch][A6] 进大气档：group={group.GroupId} 引力源 {members.Count} → {members.Count - mask.Count}（掩码 {mask.Count} 项：{string.Join(",", mask)}）· 高度 {altitude / 1000.0:F0} km");
        }

        /// <summary>
        /// **[A6] 三步交接之②：恢复全场** —— 气动可忽略（ρ/ρ₀ ≤ e^−k，或出大气顶）时自动恢复。
        /// **单向、幂等**：恢复过就置闩锁（`RestoreFullGravityField`），永不自动回忽略。
        /// 返回"本次是否真的发生了恢复"（供日志/验收读数）。
        /// </summary>
        private bool TryRestoreFullGravityField(SpaceEntityGroup group, RocketLaunchState st)
        {
            if (group.ignoredGravityBodyIds == null) return false;   // 没在档里（数值未定期间恒走这里）
            if (IsInSignificantAir(group, st)) return false;

            if (!group.RestoreFullGravityField()) return false;
            Log.Message($"[RocketLaunch][A6] 出气动区：group={group.GroupId} 恢复全场引力（单向闩锁置位）· 高度 {AltitudeKm(group, st):F0} km");
            return true;
        }

        /// <summary>
        /// **制导输入速度**：`当前速度 + 在飞冲量`（`pendingImpulse`，本步首落地）。
        ///
        /// **为什么必须加**（2026-10-06，游戏日志 + 台架双向定案）：
        /// 本控制器是 `GameComponent`，**在 `SpaceGameComp` 之前 tick**（实测：同一 tick 的日志里
        /// `[RocketLaunch探针]` 行先于 `[燃烧·诊断]` 行）。⇒ 算制导时看到的
        /// `CurrentOrbit.velocitySystem` **不含**上一拍命令、本步首才落地的冲量 ⇒ 上一拍的修正会被
        /// **再索要一遍**（同一份 Δv 交付两次、来回抵消）= 可见的"抽抽" + 双倍油耗。
        /// 时间加速把这一步放大：Fast = 8×（`TimeScaleManager.PhysicsSpeedMultiplier`，dt=16 s）。
        /// 台架（游戏同款三阶泰勒积分器 + 一拍延迟）实测：dt=2 余 857 kg → **2166 kg**；
        /// **dt=8 无预测直接烧干（复现游戏）→ 含在飞冲量后 2222 kg**；dt=16 同理 1814 kg。
        /// ⚠ **只补速度、不外推位置**：位置外推（`pos + vel·dt`）在大 dt 下会冲过头（台架实测：掉回地面）。
        /// </summary>
        private static DVector3 GuidanceVelocity(SpaceEntityGroup group)
            => group.CurrentOrbit.velocitySystem.v + group.pendingImpulse;

        /// <summary>
        /// 本步"气动不可忽略"判定（[A6+]，2026-10-06）：**同一处**给闩锁恢复与顺气流指向用
        /// （判据 = `CelestialBodyInfo.atmosphereRestoreScaleHeights`，默认 9.5 个标尺高度 ⇒ 地球 76 km）。
        /// </summary>
        private static bool IsInSignificantAir(SpaceEntityGroup group, RocketLaunchState st)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var body = mgr?.GetBodyById(st.bodyId);
            if (body == null) return false;
            double altitude = group.CurrentOrbit.positionSystem.magnitude - body.radius;
            return body.Atmosphere.IsAerodynamicallySignificant(altitude, body.atmosphereRestoreScaleHeights);
        }

        private static double AltitudeKm(SpaceEntityGroup group, RocketLaunchState st)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var body = mgr?.GetBodyById(st.bodyId);
            return (group.CurrentOrbit.positionSystem.magnitude - (body?.radius ?? 0.0)) / 1000.0;
        }

        /// <summary>
        /// **[A6+] 爬升失败退出**（2026-10-06，游戏内实测补）：带阻力后爬升**可能失败**
        /// （本次实测：长棍侧飞 → 烧干 50 t → 落回再入 → **钻进地表以下继续"飞"**）。
        /// ⚠ 若不清理：`exemptPlanetCollision` 永不解除 ⇒ 火箭穿地后仍是"活动爬升"（僵尸态，实测存在）。
        /// 判据 = 半径 &lt; 天体半径（地表以下）⇒ 撤豁免 + 撤指向 + 记一条 + 移出活动表，
        /// 随后由**既有撞击判据**正常收尾（销毁 + 着陆/坠毁消息）。
        /// </summary>
        private bool TryHandleFailedAscent(SpaceEntityGroup group, RocketLaunchState st)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var body = mgr?.GetBodyById(st.bodyId);
            if (body == null) return false;
            double r = group.CurrentOrbit.positionSystem.magnitude;
            if (!(r < body.radius)) return false;

            group.exemptPlanetCollision = false;
            group.aeroAlignToAirflow = false;
            AddLog(Find.TickManager.TicksGame, st.groupId, "Destroyed",
                $"爬升失败：r={r / 1000.0:F0} km 落到地表以下（撤豁免，交回撞击判据）");
            Log.Message($"[RocketLaunch] group={group.GroupId} 爬升失败退出：r={r / 1000.0:F0} km < 天体半径 {body.radius / 1000.0:F0} km ⇒ 撤豁免/撤指向，交回撞击判据");
            return true;
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            if (Find.TickManager.TicksGame % 2 != 0) return; // 物理步节奏

            var spaceGame = SpaceGame;
            if (spaceGame == null)
            {
                activeLaunches.Clear();
                return;
            }

            // 探针日志：按 ProbeInterval 间隔打印全部活动爬升状态
            int tick = Find.TickManager.TicksGame;
            if (ProbeInterval > 0 && tick >= probeNextTick)
            {
                probeNextTick = tick + ProbeInterval;
                for (int i = 0; i < activeLaunches.Count; i++)
                {
                    var p = ProbeState(activeLaunches[i]);
                    Log.Message($"[RocketLaunch探针] t={tick} group={p.groupId} r={p.r / 1000.0:F0}km ΔR={p.deltaR:F0}m v={p.vMag:F1}m/s vRad={p.vRad:F1} |dv|={p.dvMag:F1}m/s 燃={p.fuelKg:F0}kg 烧={(p.burning ? "on" : "off")} 到达={p.arrived}");
                }
            }

            for (int i = activeLaunches.Count - 1; i >= 0; i--)
            {
                var st = activeLaunches[i];
                var group = spaceGame.GetGroupById(st.groupId);
                if (group == null || !group.IsActive)
                {
                    // group 已销毁（坠毁/手动删）→ 结算 + 移除
                    if (group == null)
                        AddLog(Find.TickManager.TicksGame, st.groupId, "Destroyed", "群组不存在（已销毁/未注册）");
                    else
                        AddLog(Find.TickManager.TicksGame, st.groupId, "Destroyed", "群组失联（IsActive=false）");
                    activeLaunches.RemoveAt(i);
                    continue;
                }

                // [A6]/[A6+] 大气段两件：① 出气动区 ⇒ 恢复全场（单向闩锁、幂等）；
                //   ② 顺气流指向开关（D13 的指向纪律：气动不可忽略时把头锥对准空气相对速度）
                bool inAir = IsInSignificantAir(group, st);
                if (!inAir) TryRestoreFullGravityField(group, st);
                group.aeroAlignToAirflow = inAir;

                // [A6+] 失败退出：钻进地表以下 = 爬升失败（撤豁免/撤指向，交回撞击判据）；否则是"僵尸态"
                if (TryHandleFailedAscent(group, st))
                {
                    activeLaunches.RemoveAt(i);
                    continue;
                }

                // 火箭全程非圆轨：直接读 CurrentOrbit（物理步开始前 pendingImpulse 已施加）
                LaunchGuidanceParams prms = st.ToParams();
                var step = LaunchGuidanceCore.ComputeStep(
                    group.CurrentOrbit.positionSystem.v, GuidanceVelocity(group),
                    spaceGame.GetBodyGM(st.bodyId), prms);

                if (DebugDetailedLaunch)
                {
                    var p = group.CurrentOrbit.positionSystem.v;
                    double alt = p.magnitude - prms.planetRadius;
                    double vRad = DVector3.Dot(group.CurrentOrbit.velocitySystem.v, p.normalized);
                    Log.Message($"[RocketLaunch] group={group.GroupId} t={Find.TickManager.TicksGame} alt={alt / 1000.0:F0}km r={p.magnitude / 1000.0:F0}km v={group.CurrentOrbit.velocitySystem.magnitude:F1}m/s vRad={vRad:F1} |dv|={step.dv.magnitude:F1} arrived={step.arrived}");
                }

                if (step.arrived)
                {
                    // 到达：清 burn（防 BeginCircularize 判"正在机动"）→ 解除碰撞豁免/撤指向 → 切给入轨
                    group.EndBurn();
                    group.exemptPlanetCollision = false;
                    group.aeroAlignToAirflow = false;
                    if (spaceGame.BeginCircularize(group, st.bodyId))
                    {
                        AddLog(Find.TickManager.TicksGame, st.groupId, "Orbited",
                            $"爬升完成 r={group.CurrentOrbit.positionSystem.magnitude / 1000.0:F0}km → 进入圆轨化");
                        Log.Message($"[RocketLaunch] group={group.GroupId} 爬升完成，进入圆轨化机动");
                    }
                    else
                    {
                        AddLog(Find.TickManager.TicksGame, st.groupId, "CircularizeFailed",
                            "到达窗口但圆轨化失败，保持正常轨道");
                        Log.Message($"[RocketLaunch] group={group.GroupId} 到达窗口但圆轨化失败，保持正常轨道");
                    }
                    activeLaunches.RemoveAt(i);
                    continue;
                }

                // 未到达：EndBurn 清上一物理步残余 → 全矢量 RCS → StartBurn(dv)
                if (group.HasActiveBurn)
                    group.EndBurn();
                if (step.dv.sqrMagnitude > 0.0)
                {
                    group.burnUseRcs = true;
                    group.StartBurn(step.dv);
                }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref activeLaunches, "activeLaunches", LookMode.Deep);
            Scribe_Collections.Look(ref recentLog, "recentLog", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (activeLaunches == null)
                    activeLaunches = new List<RocketLaunchState>();
                if (recentLog == null)
                    recentLog = new List<LaunchLogEntry>();
            }
        }
    }
}
