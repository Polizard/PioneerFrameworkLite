using System;
using MechJebLib.Primitives;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **节点窗口**（2026-09-21）：活差额执行器的**第三条收手判据 —— 关于时刻的**。
    ///
    /// ## 它修的是什么
    /// 游戏内实证（`Player.log`，同轨 30° 快例，2026-09-21 02:25 构建）：
    /// <code>
    /// [阶梯] PhasingOrbit｜建调相轨道｜排入 2 发（Δv 合计 431.7 m/s）
    /// [Burn]  实际时长=9080s appliedDv=4412.3 m/s        ← 计划 431.7 的 **10 倍**，烧了 2.5 小时
    /// [燃烧·诊断] 剩余=1032.3 **一直不掉**｜实交/指令=0.481｜模型方向 vs 执行方向=151.89°
    /// [阶梯] Circularize（PeR=11130 / ApR=−47663 km）… ×18 次｜[Maneuver] BLOCKED（maxDV=0.0）
    /// </code>
    /// ⇒ 轨道被毁、燃料烧光、交会死。
    ///
    /// ## 为什么活差额律自己停不下来
    /// `BurnStep.Deficit` 的两条收手判据是**地板**（差额 &lt; 1e-3）与**反向**（差额与上一步方向反号，
    /// = 上游 `AngleFromNode() ≥ π/2` 的同义物）。**两条都只看差额本身**。
    /// 差额 = `v_target(UT) − v_live(UT)`：节点作废之后它描述的已经不是"这个节点还差多少" ⇒
    /// 两条都可能永远不触发。⇒ 必须有第三条**关于时刻**的判据。
    ///
    /// ## 本文件钉三件事
    /// · ① 截止算式 = `UT + 名义燃时/2 + 宽限`（燃烧以节点为中心 ⇒ 合法区间 `[UT−T/2, UT+T/2]`），
    ///   并钉住"现场那一发"的超时量级；
    /// · ② 名义燃时未知（0）时的退化口径仍是**有限**截止；
    /// · ③ **控制组**：节点当前时，活差额回环交付量精确、准时收手（把它钉住，才谈得上"越窗才是病"）。
    ///
    /// ## ⚠ 已登记的**失败复现**（诚实记录，不是没查）
    /// 我尝试在纯核里把"无界燃烧"整体复现出来（控制组那个回环扫过 `lead ∈ [100, 3000] s` ×
    /// `Δv ∈ [400, 1500] m/s`），**没能复现**：
    /// · 节点当前 ⇒ 交付精确（±0.5%）、准时收手；
    /// · 节点远到 1.5 个轨道周期之外 ⇒ 要么差额立刻反向（根本不点火）、要么照常收手。
    /// ⇒ 现场那 `1032.3 一直不掉` 很可能是**游戏侧独有的另一条路径**（例如"名义燃时按主网算、
    /// 实际按 RCS 的小加速度烧"⇒ 就是 `实交/指令 = 0.481` 那个读数），纯核里没有对应物。
    /// **本闸对它是通用的兜底**：不管那 af 从哪里来，只要烧穿窗口就停。
    /// </summary>
    public class BurnWindowTerminationTests
    {
        // ── 默认口径：`PlanetRadiusMeters = 5e6`、`PrimaryGravitationalParameter = 2e14` ──
        private static readonly double Mu = SpaceConstants.PrimaryGravitationalParameter;
        private static readonly double Radius = SpaceConstants.PlanetRadiusMeters + 500_000.0;   // 5500 km
        private const double Dt = 2.0;                                            // UPDATE_INTERVAL_TICKS
        private static readonly double AlignGateRad = 2.0 * Math.PI / 180.0;      // IgnitionAlignGateDeg
        private const double DeficitFloor = 1e-3;                                 // BurnTargetVFloor

        // ── 游戏侧窗口宽限（`SpaceGameComp.BurnNodeWindowPostSeconds` 的取值）──
        private const double GameGrace = 420.0;

        // ── 调试放置船（过推档：F=160kN / ṁ=140 kg/s）──
        private const double Thrust = 160_000.0;
        private const double MassFlow = 140.0;
        private const double DryMass = 5_000.0;
        private const double Fuel0 = 70_000.0;
        private const double Mass0 = DryMass + Fuel0;

        private static (V3 r, V3 v) Propagate(V3 r0, V3 v0, double tau)
            => MechJebLib.TwoBody.Shepperd.Solve(Mu, tau, r0, v0);

        private static (V3 r, V3 v) Circular(double radius)
            => (new V3(radius, 0.0, 0.0), new V3(0.0, Math.Sqrt(Mu / radius), 0.0));

        private static double SafeAcos(double x) => Math.Acos(Math.Max(-1.0, Math.Min(1.0, x)));

        /// <summary>
        /// ① **截止算式**：燃烧以节点为中心 ⇒ 合法区间 `[UT − T/2, UT + T/2]`，截止 = 右端 + 宽限。
        /// 并钉住"现场那一发"的量级：名义燃时 ≈150 s、节点 UT=0 ⇒ 截止 ≈570 s，而它烧了 9329 s
        /// ⇒ 闸**必抓**（裕度 16 倍），且**不误杀**合法但晚点的燃烧。
        /// </summary>
        [Fact]
        public void NodeWindow_DeadlineIsNodeCenteredPlusGrace()
        {
            double deadline = BurnStep.NodeWindowDeadline(0.0, 150.0, GameGrace);
            Assert.Equal(75.0 + GameGrace, deadline, 6);

            Assert.False(BurnStep.NodeWindowExpired(deadline - 1.0, 0.0, 150.0, GameGrace),
                "截止之前一秒不该判过期");
            Assert.False(BurnStep.NodeWindowExpired(deadline, 0.0, 150.0, GameGrace),
                "恰在截止上不该判过期（判据是 >）");
            Assert.True(BurnStep.NodeWindowExpired(deadline + 1.0, 0.0, 150.0, GameGrace),
                "截止之后一秒必须判过期 ⇒ 停火");

            // 现场实测：节点在过去 9329 s 的位置还在烧 ⇒ 闸必抓，且裕度 16 倍
            Assert.True(BurnStep.NodeWindowExpired(9329.0, 0.0, 150.0, GameGrace),
                "现场那发（节点后 9329 s 仍在烧）必须被判过期");

            // 合法但晚点（等对准 + 烧满后半段）⇒ **不许误杀**
            Assert.False(BurnStep.NodeWindowExpired(300.0, 0.0, 150.0, GameGrace),
                "晚点但仍在窗口内的燃烧不许被误杀");
        }

        /// <summary>② 名义燃时未知（0）⇒ 只给宽限（退化口径仍是个有限截止，不是"永不过期"）。</summary>
        [Fact]
        public void NodeWindow_ZeroBurnSecondsStillHasGrace()
        {
            Assert.Equal(GameGrace, BurnStep.NodeWindowDeadline(0.0, 0.0, GameGrace), 6);
            Assert.Equal(GameGrace, BurnStep.NodeWindowDeadline(0.0, -5.0, GameGrace), 6);   // 负值按 0
            Assert.False(BurnStep.NodeWindowExpired(GameGrace - 1.0, 0.0, 0.0, GameGrace));
            Assert.True(BurnStep.NodeWindowExpired(GameGrace + 1.0, 0.0, 0.0, GameGrace));
        }

        /// <summary>
        /// ③ **控制组**：节点当前时，活差额回环（含"冲量下一步首才生效"的时序）交付量必须≈计划、
        /// 且**在窗口内收手**。这条钉住"执行器本来是对的" ⇒ 越窗才是病。
        /// </summary>
        [Fact]
        public void Control_TargetVLoopDeliversPlannedDvWhenNodeIsCurrent()
        {
            (V3 r0, V3 v0) = Circular(Radius);
            const double planDv = 400.0;
            const double nodeUt = 300.0;
            var o = RunTargetVLoop(r0, v0, nodeUt, planDv, horizon: 20_000.0);

            Assert.True(o.IgnitionTick >= 0.0, "对准且节点在未来 ⇒ 必须点火");
            Assert.True(o.EndTick > 0.0, $"燃烧未收手（已烧 {o.AppliedDv:F1} m/s）");
            Assert.True(Math.Abs(o.AppliedDv - planDv) < planDv * 0.02,
                $"交付量必须≈计划：计划 {planDv:F1}、交付 {o.AppliedDv:F1} m/s");
            Assert.False(BurnStep.NodeWindowExpired(o.EndTick, nodeUt, Mass0 * planDv / Thrust, GameGrace),
                $"正常燃烧的收手时刻 {o.EndTick:F0}s 不该落在节点窗口之外");
        }

        private readonly struct LoopOutcome
        {
            public readonly double IgnitionTick, EndTick, AppliedDv;
            public LoopOutcome(double ig, double end, double dv) { IgnitionTick = ig; EndTick = end; AppliedDv = dv; }
        }

        /// <summary>
        /// 活差额回环的离线复刻（口径与 `SpaceGameComp.ComputeGroupPhysics` 逐条对齐）：
        /// · 步长 2 s、质量随工质耗减；
        /// · **冲量在物理步首才并入速度**（`ApplyPendingImpulses()` 在 `ComputeGroupPhysics` **之前**）；
        /// · 方向每拍重取；收手用 `BurnStep.DeficitToTargetV` 的**同一份**判据。
        /// </summary>
        private static LoopOutcome RunTargetVLoop(V3 r0, V3 v0, double nodeUt, double planDv, double horizon)
        {
            (V3 rN, V3 vN) = Propagate(r0, v0, nodeUt);
            V3 targetV = vN + vN.normalized * planDv;
            _ = rN;
            double ignitionTick = nodeUt - (Mass0 * planDv / Thrust) / 2.0;

            V3 cmdDir = v0.normalized;
            double mass = Mass0, fuel = Fuel0;
            V3 r = r0, v = v0, pending = V3.zero;
            double applied = 0.0, igTick = -1.0, endTick = -1.0;

            for (double tick = 0.0; tick <= horizon; tick += Dt)
            {
                if (pending.sqrMagnitude > 0.0) { v += pending; pending = V3.zero; }
                (V3 rNext, V3 vNext) = Propagate(r, v, Dt);

                (V3 rLiveUt, V3 vLiveUt) = Propagate(r, v, nodeUt - tick);
                _ = rLiveUt;
                BurnStep.DeficitToTargetV(vLiveUt, targetV, cmdDir,
                    DeficitFloor, out DVector3 defDirD, out double defRem, out _);
                V3 defDir = defDirD;
                if (defRem > 0.0 && defDir.sqrMagnitude > 0.0) cmdDir = defDir;

                if (tick >= ignitionTick && defRem > 0.0 && defDir.sqrMagnitude > 0.0
                    && SafeAcos(V3.Dot(cmdDir.normalized, defDir.normalized)) <= AlignGateRad)
                {
                    if (igTick < 0.0) igTick = tick;
                    double a = Thrust / mass;
                    double dv = Math.Min(a * Dt, defRem);
                    pending += defDir.normalized * dv;
                    applied += dv;
                    fuel -= MassFlow * Dt * (dv / (a * Dt));
                    mass = DryMass + Math.Max(fuel, 0.0);
                }

                r = rNext; v = vNext;
                if (igTick >= 0.0 && (defRem <= 0.0 || fuel <= 0.0)) { endTick = tick + Dt; break; }
            }
            return new LoopOutcome(igTick, endTick, applied);
        }
    }
}
