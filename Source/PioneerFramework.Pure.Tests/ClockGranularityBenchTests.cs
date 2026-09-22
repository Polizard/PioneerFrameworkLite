using System;
using MechJebLib.Primitives;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **物理步就是执行器的分辨率**（B5-4，2026-09-23）。
    ///
    /// ## 为什么这条必须量出来（交接 §B5 第 ② 项"三个 dt"）
    /// 台架此前只有一个硬编码的 `5 s`（`RendezvousGuidanceCoreTests` 一族）—— **哪一档都不是**；
    /// 而游戏里物理步是三档（Normal 2 s / Fast 16 s / Combat 1÷30 s）。这属于"量纲错配"，
    /// 但它到底让读数差多少，此前**没人量过**：本文件用同一个律、同一发计划，只改物理步，量终端误差。
    ///
    /// ## 机制（两条，都在 `BurnStep` / `BurnExecutorPolicy` 里）
    /// · **分辨率**：一步能给 `a·dt`。目标速度差得比它小的时候，一次修正就是"一步的粒度"；
    /// · **一步延迟**：本步算出的冲量在**下一物理步首**才并入速度（`pendingImpulse`）⇒ 误差再乘一个 `dt`。
    ///
    /// ⚠ 本台架**故意**不建对准门（假设已对准）——它测的是**步长**这一件事，别混入姿态政策
    /// （"一步=一个机制"的施工纪律）。
    /// </summary>
    public class ClockGranularityBenchTests
    {
        private static readonly double Mu = SpaceConstants.PrimaryGravitationalParameter;
        private static readonly double Radius = SpaceConstants.PlanetRadiusMeters + 500_000.0;
        private const double Thrust = 160_000.0;
        private const double MassFlow = 140.0;
        private const double DryMass = 5_000.0;
        private const double Fuel0 = 70_000.0;
        private const double Mass0 = DryMass + Fuel0;
        private const double DeficitFloor = 1e-3;   // `BurnTargetVFloor`
        private const double NodeUt = 300.0;
        private const double PlanDv = 400.0;

        private static (V3 r, V3 v) Propagate(V3 r0, V3 v0, double tau)
            => MechJebLib.TwoBody.Shepperd.Solve(Mu, tau, r0, v0);

        private static (V3 r, V3 v) Circular(double radius)
            => (new V3(radius, 0.0, 0.0), new V3(0.0, Math.Sqrt(Mu / radius), 0.0));

        /// <summary>回环读数。<paramref name="finalDeficit"/> = 收手时的**律残差**（律控制的就是它）。</summary>
        private readonly struct LoopReading
        {
            public readonly double finalDeficit;   // m/s：`DeficitToTargetV` 给出的剩余量
            public readonly double appliedDv;      // m/s：实际并入速度的累计 Δv
            public readonly int steps;
            public LoopReading(double finalDeficit, double appliedDv, int steps)
            { this.finalDeficit = finalDeficit; this.appliedDv = appliedDv; this.steps = steps; }
        }

        /// <summary>
        /// 活差额律回环（口径与游戏逐条对齐：一步延迟 / 每拍重取方向 / `BurnStep` 判据）。
        ///
        /// ⚠ **度量口径（本轮踩过，记下来）**：活差额律控制的量是"把**当前状态推进到节点 UT** 时的速度"
        /// （`DeficitToTargetV` 吃的就是它），而**瞬时速度本来就 ≠ 靶**（节点在中点两侧、重力还在转方向）
        /// ⇒ 拿 `|v_瞬时 − v_靶|` 当误差会读出一个**恒等**于计划量的大数（我第一版就这么错：400 m/s 的计划
        /// 读出 462 m/s，看起来像"根本没烧"）。**律的残差才是它控制的量** ⇒ 读数取收手时的 `finalDeficit`。
        /// </summary>
        private static LoopReading RunTargetVLoop(double dt, double planDv, double horizon = 20_000.0)
        {
            (V3 r0, V3 v0) = Circular(Radius);
            (V3 _, V3 vN) = Propagate(r0, v0, NodeUt);
            V3 targetV = vN + vN.normalized * planDv;                       // 「在那个位置的、那个速度」
            double ignitionTick = BurnExecutorPolicy.CenteredIgnitionTick(
                NodeUt, Mass0 * planDv / Thrust, phaseShiftSeconds: 0.0, executionDelaySeconds: dt);

            V3 cmdDir = v0.normalized;
            double mass = Mass0, fuel = Fuel0;
            V3 r = r0, v = v0, pending = V3.zero;
            double applied = 0.0, lastDeficit = double.NaN;
            int steps = 0;
            bool burning = false;

            for (double tick = 0.0; tick <= horizon; tick += dt)
            {
                // ③ 一步延迟：上一步算出的冲量在本步首并入
                if (pending.sqrMagnitude > 0.0) { v += pending; pending = V3.zero; }

                (V3 rNext, V3 vNext) = Propagate(r, v, dt);
                (V3 rLiveUt, V3 vLiveUt) = Propagate(r, v, NodeUt - tick);
                _ = rLiveUt;

                BurnStep.DeficitToTargetV(OrbitView.ToD(vLiveUt), OrbitView.ToD(targetV), OrbitView.ToD(cmdDir),
                    DeficitFloor, out DVector3 defDirD, out double defRem, out bool reversed);
                V3 defDir = OrbitView.ToV3(defDirD);
                lastDeficit = defRem;
                if (defRem > 0.0 && defDir.sqrMagnitude > 0.0) cmdDir = defDir;
                steps++;

                if (tick >= ignitionTick && !reversed && defRem > 0.0 && defDir.sqrMagnitude > 0.0)
                {
                    burning = true;
                    double a = Thrust / mass;
                    double dv = Math.Min(a * dt, defRem);
                    pending += defDir.normalized * dv;
                    applied += dv;
                    fuel -= MassFlow * dt * (dv / (a * dt));
                    mass = DryMass + Math.Max(fuel, 0.0);
                }

                r = rNext; v = vNext;
                if (burning && (defRem <= 0.0 || reversed || fuel <= 0.0)) break;
            }
            return new LoopReading(lastDeficit, applied, steps);
        }

        /// <summary>
        /// ⭐ **被实测否掉的假设**（登记）：计划稿把"三个 dt"记成"量纲错配 ⇒ 精度损失"，但实测是——
        /// **单发燃烧的收手残差对物理步不敏感**：活差额律每拍重新测量差额、并按比例油门**恰好**交到它，
        /// 四档（1÷30 / 2 / 5 / 16 s）的收手残差都落在**地板**（1e-3）上，交付量也都 ≈ 计划（±5%）。
        /// ⇒ dt 的影响**不在**"一发烧得准不准"，而在**外环**（下一段的比值）：延迟/周期、以及"一步的粒度"。
        /// 本用例把这个事实钉住，免得下次又有人（包括我）拿 dt 去解释单发精度。
        /// </summary>
        [Fact]
        public void SingleBurnClosure_IsDtInsensitive_ResidualEndsAtTheFloor()
        {
            foreach (double dt in new[] { 1.0 / 30.0, 2.0, 5.0, 16.0 })
            {
                LoopReading rd = RunTargetVLoop(dt, PlanDv);
                Assert.True(rd.finalDeficit <= DeficitFloor + 1e-9,
                    $"dt={dt:F3} 档的收手残差应落在地板上：实得 {rd.finalDeficit:E3}");
                Assert.True(Math.Abs(rd.appliedDv - PlanDv) < PlanDv * 0.05,
                    $"dt={dt:F3} 档的交付量应≈计划：{rd.appliedDv:F1} vs {PlanDv:F0}");
            }
        }

        /// <summary>
        /// ★ **dt 真正影响的地方：执行延迟占控制周期的比例**。
        /// 冲量在**下一物理步首**才并入 ⇒ 每次修正都是"对一拍之前的状态"下的手；
        /// 而控制周期（Normal 10 s）才是"律多久看一次"的拍子 ⇒ 比值 `dt / 控制周期` 就是
        /// **每次修正的陈旧度**：Combat 0.33% · Normal **20%** · Fast **160%**（超过 100% ⇒ 控制周期内
        /// 塞不进一次完整修正，外环永远在追一拍前的状态）。
        /// 这就是"1× 档下手太粗、要降到 1/60×"的那条账的**量纲形式**。
        /// </summary>
        [Theory]
        [InlineData(1.0 / 30.0, 0.00333)]   // Combat：0.33%
        [InlineData(2.0, 0.20)]             // Normal：20%
        [InlineData(16.0, 1.60)]            // Fast：160%
        public void DelayOverControlPeriod_IsTheOodaStaleness(double dt, double expectRatio)
        {
            const double controlSeconds = 10.0;   // `NewLawControlDt`
            double ratio = dt / controlSeconds;

            Assert.Equal(expectRatio, ratio, 3);
            if (ratio >= 1.0)
                Assert.True(BurnExecutorPolicy.PhysicsStepsPerControlPeriod(dt, controlSeconds) <= 1,
                    "延迟超过一整个控制周期时，控制周期已经不再限制任何东西（物理步比它还慢）");
        }

        /// <summary>
        /// **分辨率的一条解析钉**：一步能给的 Δv = `a·dt`。当"目标还差得比它小"时，
        /// 这条通路一次修正就是"一步的粒度"——游戏侧实测那句"1× 档下一步可给 ≈88 m/s，
        /// 而收尾修正量只有 15–100 m/s ⇒ 一次修正≈一步的粒度"就是这个数。
        /// </summary>
        [Fact]
        public void OneStepDv_IsTheResolutionFloor_AndScalesWithDt()
        {
            double aMain = Thrust / Mass0;                 // 全油门可用加速度（点火那一步）

            // 目标还差 100 m/s，而一步只能给 a·dt ⇒ 单步交付被**步长**封顶（这就是"分辨率"）
            BurnStep.Result step = BurnStep.Compute(0.0, aMain, MassFlow, 2.0, remaining: 100.0);
            Assert.Equal(aMain * 2.0, step.Dv, 9);

            double oneStepNormal = aMain * 2.0;
            double oneStepFast = aMain * 16.0;
            Assert.Equal(8.0, oneStepFast / oneStepNormal, 9);   // Fast 是 Normal 的 8 倍粒度

            // 收尾修正量量级（15–100 m/s）在 Fast 档下**小于一步**⇒ 一次修正的粒度已经超过修正本身
            Assert.True(oneStepFast > 15.0, $"Fast 档一步 {oneStepFast:F1} m/s 应当盖过典型收尾修正量");
        }

        /// <summary>
        /// **三个 dt 是两个不同的拍子**：物理步是执行/积分的粒度，控制周期是"律多久看一次"的粒度。
        /// 例：Normal 2 s + 控制 10 s ⇒ 一个控制周期 **5** 个物理步；Fast 16 s ⇒ **1**（控制比物理还快，
        /// 此时控制周期不再限制任何东西）；Combat 1÷30 s ⇒ 300。
        /// </summary>
        [Fact]
        public void ControlPeriod_IsASamplingOfPhysicsSteps()
        {
            Assert.Equal(5, BurnExecutorPolicy.PhysicsStepsPerControlPeriod(2.0, 10.0));
            Assert.Equal(1, BurnExecutorPolicy.PhysicsStepsPerControlPeriod(16.0, 10.0));
            Assert.Equal(300, BurnExecutorPolicy.PhysicsStepsPerControlPeriod(1.0 / 30.0, 10.0));
            Assert.Equal(0, BurnExecutorPolicy.PhysicsStepsPerControlPeriod(0.0, 10.0));   // 退化输入
        }

        /// <summary>
        /// 周期到点用的是**"距上次多久"**（不是取模、不是平均）——三个时钟各传自己的周期。
        /// </summary>
        [Fact]
        public void PeriodDue_IsElapsedSinceLast_NotModulo()
        {
            Assert.False(BurnExecutorPolicy.ElapsedDue(now: 9.0, lastAtSeconds: 0.0, periodSeconds: 10.0));
            Assert.True(BurnExecutorPolicy.ElapsedDue(now: 10.0, lastAtSeconds: 0.0, periodSeconds: 10.0));
            Assert.True(BurnExecutorPolicy.ElapsedDue(now: 10.5, lastAtSeconds: 0.0, periodSeconds: 10.0));   // 物理步 2 s ⇒ 落在 10.5 这种非整数倍上也要触发
        }
    }
}
