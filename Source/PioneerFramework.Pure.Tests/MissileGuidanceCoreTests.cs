using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 导弹终末制导纯核心单测（MissileGuidanceCore / MissileVolleyMath，2026-09-08 抽纯）。
    /// 仿真模型见 <see cref="MissileFlightSim"/> / <see cref="VolleyFlightSim"/>（D15 主推方向型镜像 +
    /// 扫掠命中判定）。这些测试是控制律的快速迭代回路：纯测试秒级出结果，游戏内只做最终验收。
    /// </summary>
    public class MissileGuidanceCoreTests
    {
        // ── 单元：控制律分量 ──────────────────────────────────
        [Fact]
        public void Guidance_BoundedClosingDemand_WhenLateralCorrectionNeedsTime()
        {
            var p = MissileGuidanceParams.CreateDefault();
            var gs = new MissileGuidanceState();
            // 1.5km 外、接近 150、横向 800：修横差需 800/83≈9.6s，tGo 仅 10s → 接近率需求被压到
            // surfaceDist·a/(margin·lateral) = 1500·83/(2·800) ≈ 78 → 低于当前接近率 150 → 不补（只加不减）
            var step = MissileGuidanceCore.ComputeStep(
                DVector3.zero, new DVector3(150, 800, 0),
                new DVector3(1500, 0, 0), DVector3.zero, 57.0, p, gs);

            Assert.Equal(MissileGuidanceAction.Burn, step.action);
            Assert.Equal(0.0, step.boostDemand, 9);
            Assert.True(step.closingDemand < 150.0, "接近率需求应被横差修正时间压到当前接近率之下");
            // dv 方向应几乎垂直于视线（纯横向修正 → 主推全力修横差）
            Assert.True(Math.Abs(DVector3.Dot(step.dv.normalized, new DVector3(1, 0, 0))) < 0.1);
        }

        [Fact]
        public void Guidance_BoostsClosing_WhenLateralSmallAndTimeAmple()
        {
            var p = MissileGuidanceParams.CreateDefault();
            var gs = new MissileGuidanceState();
            var step = MissileGuidanceCore.ComputeStep(
                DVector3.zero, new DVector3(150, 10, 0),
                new DVector3(30000, 0, 0), DVector3.zero, 57.0, p, gs);

            Assert.Equal(MissileGuidanceAction.Burn, step.action);
            Assert.Equal(p.cruiseClosing - 150.0, step.boostDemand, 6);
            Assert.True(step.closingDemand <= p.cruiseClosing + 1e-9);
        }

        [Fact]
        public void Guidance_ClosingDemand_AccountsForTurnTime()
        {
            var p = MissileGuidanceParams.CreateDefault();
            var gs = new MissileGuidanceState();
            // 2km、接近 50、横向 300：速度与视线夹角 80.5°（转向 4.0s）+ 横差归零 3.6s → 需求压到 ~130
            // （旧式只算横差归零 → 277；转向时间是主推未对准期的真实开销，2026-09-08 队首擦过实证）
            var step = MissileGuidanceCore.ComputeStep(
                DVector3.zero, new DVector3(50, 300, 0),
                new DVector3(2000, 0, 0), DVector3.zero, 57.0, p, gs);
            Assert.True(step.closingDemand < 200.0, "转向时间应计入接近率需求上限，实际 " + step.closingDemand.ToString("F0"));
        }

        [Fact]
        public void Guidance_HoldFire_SuppressesBoostOnly()
        {
            var p = MissileGuidanceParams.CreateDefault();
            var gs = new MissileGuidanceState();
            // 错峰时隙未到（allowBoost=false）：横差小、时间充裕也不补接近率，dv = 纯横向修正
            var step = MissileGuidanceCore.ComputeStep(
                DVector3.zero, new DVector3(150, 10, 0),
                new DVector3(30000, 0, 0), DVector3.zero, 57.0, p, gs, allowBoost: false);

            Assert.Equal(0.0, step.boostDemand, 9);
            Assert.Equal(MissileGuidanceAction.Burn, step.action); // 仍有横向修正
            Assert.True(Math.Abs(step.dv.normalized.x) < 0.3, "dv 应以横向为主（不沿视线冲刺）");
        }

        [Fact]
        public void Guidance_Receding_CommandsReversal()
        {
            var p = MissileGuidanceParams.CreateDefault();
            var gs = new MissileGuidanceState();
            // 擦过之后：目标在后方（视线 +x）、弹仍沿 −x 远离 → 接近率 −300 → 掉头再捕获
            var step = MissileGuidanceCore.ComputeStep(
                DVector3.zero, new DVector3(-300, 0, 0),
                new DVector3(5000, 0, 0), DVector3.zero, 57.0, p, gs);

            Assert.Equal(MissileGuidanceAction.Burn, step.action);
            Assert.True(step.reacquiring);
            Assert.True(step.boostDemand > 0.0, "远离时允许反向补量（掉头）");
            Assert.True(step.dv.normalized.x > 0.9, "dv 应指向目标（掉头）");
        }

        [Fact]
        public void Abort_GeometryLost_WhenClosestApproachPassed()
        {
            var p = MissileGuidanceParams.CreateDefault();
            var gs = new MissileGuidanceState { minSurfaceDist = 500.0 }; // 曾接近到 500m
            // 已远离到最近点 +lostMargin 之外 → 判定几何丢失
            var step = MissileGuidanceCore.ComputeStep(
                DVector3.zero, new DVector3(-300, 0, 0),
                new DVector3(p.lostMarginMeters + 10000.0, 0, 0), DVector3.zero, 57.0, p, gs);

            Assert.Equal(MissileGuidanceAction.Abort, step.action);
        }

        // ── 端到端：可达包线内收敛（回归网格）────────────────
        [Fact]
        public void Terminal_ReachableEnvelope_AllConverge()
        {
            var p = MissileGuidanceParams.CreateDefault();
            double[] ranges = { 1500, 3000, 6000, 11000, 20000, 60000 };
            double[] closings = { 50, 100, 250, 450 };
            double[] laterals = { 50, 200, 400, 800 };

            int total = 0;
            foreach (double r in ranges)
            foreach (double c in closings)
            foreach (double l in laterals)
            {
                // 物理可达包线：修横差时间 ≤ 0.8×初始 tGo（包线外任何控制律都打不中，属物理约束）
                if (l > 0.8 * (r / c) * p.lateralAccel) continue;
                total++;
                var res = MissileFlightSim.Crossing(r, c, l, legacy: false).Run(p);
                Assert.True(res.hit,
                    $"可达包线内应命中：r={r:F0} closing={c:F0} lateral={l:F0} minSD={res.minSurfaceDist:F0} ab={res.aborted}");
            }
            Assert.True(total >= 60, "回归网格应覆盖足够多的可达组合，实际 " + total);
        }

        [Fact]
        public void Terminal_FrontOfQueue_CloseRangeWithLateralOffset_Converges()
        {
            // 队首几何（2026-09-08 游戏内擦过场景）：0.88km 处 400m 横偏 + Combat 步长（dt=1/30s）
            var p = MissileGuidanceParams.CreateDefault();
            var sim = MissileFlightSim.OffsetApproach(880.0, 150.0, 400.0, legacy: false);
            sim.dt = 1.0 / 30.0;
            sim.maxSteps = 20000;
            var res = sim.Run(p);
            Assert.True(res.hit, "队首近距横偏应收敛命中（minSD=" + res.minSurfaceDist.ToString("F0") + "）");
        }

        [Fact]
        public void Terminal_LateralPositionOffset_Converges()
        {
            var p = MissileGuidanceParams.CreateDefault();
            // 深度队列横向 jitter ±286m：位置偏置（速度平行于"发射器→目标"线）
            var res = MissileFlightSim.OffsetApproach(11000, 150, 286, legacy: false).Run(p);
            Assert.True(res.hit, "横向位置偏置 286m / 11km 接近应收敛命中");
        }

        [Fact]
        public void Reacquire_NearMiss_ComesBackAndHits()
        {
            var p = MissileGuidanceParams.CreateDefault();
            var sim = MissileFlightSim.Crossing(1500, 400, 200, legacy: false);
            sim.maxSteps = 6000;
            var res = sim.Run(p);
            Assert.True(res.hit, "擦过后应掉头再捕获命中（几何未丢失前不中止）");
        }

        [Fact]
        public void Abort_FleeingTarget_StopsInsteadOfBurningForever()
        {
            var p = MissileGuidanceParams.CreateDefault();
            var sim = new MissileFlightSim
            {
                mPos = DVector3.zero,
                mVel = DVector3.zero,
                tPos = new DVector3(5000, 0, 0),
                tVel = new DVector3(6000, 0, 0), // 6km/s 逃逸：导弹 dV 上限 ~5.6km/s，追不上
                maxSteps = 6000
            };
            var res = sim.Run(p);
            Assert.True(res.aborted, "持续远离超出手臂范围应中止（K6 回 Standby，不空烧）");
            Assert.False(res.hit);
        }

        // ── ③ 弹群间距：必须超出弹头爆炸冲量作用半径 ────────────
        [Fact]
        public void Volley_SpacingBeyondBlastRadius_NoKicks_AllHit()
        {
            var p = MissileGuidanceParams.CreateDefault();

            // 旧几何：间距被 10km 队长上限压到 1.43km < 爆炸半径 1.5km → 前弹爆炸冲量侧推后弹
            double oldSpacing = MissileVolleyMath.ComputeSpacing(2000.0, 10000.0, 8);
            Assert.True(oldSpacing < 1500.0, "旧几何间距应落在爆炸半径内（根因复现前提）");
            var oldResult = new VolleyFlightSim { spacing = oldSpacing }.Run(p);
            Assert.True(oldResult.kicked > 0, "间距 < 爆炸半径时后弹必然吃到爆炸冲量");
            Assert.True(oldResult.maxKick > 200.0, "冲量量级应为数百 m/s（足以打乱队形）");

            // 新几何：队长上限 16km → 间距 2km > 爆炸半径 1.5km → 零冲量、全命中
            double newSpacing = MissileVolleyMath.ComputeSpacing(2000.0, 16000.0, 8);
            Assert.True(newSpacing > 1500.0, "新几何间距必须超出爆炸冲量作用半径");
            var newResult = new VolleyFlightSim { spacing = newSpacing }.Run(p);
            Assert.Equal(0, newResult.kicked);
            Assert.Equal(8, newResult.hits);
        }

        [Fact]
        public void VolleySpacing_ExceedsBlastImpulseReach()
        {
            const double blastRadius = 1500.0;   // PF_Missile_Standard.blastRadius
            const double blastDamage = 600.0;    // blastCenterDamage
            const double partHp = 200.0;         // 导弹弹体 HP

            double lethal = MissileVolleyMath.BlastLethalRadius(blastRadius, blastDamage, partHp);
            Assert.Equal(1000.0, lethal, 6); // D0·(1−d/R) ≥ HP → d ≤ 1km（致死带）

            double spacing8 = MissileVolleyMath.ComputeSpacing(2000.0, 16000.0, 8);
            Assert.True(spacing8 > blastRadius, "队列间距必须超出弹头爆炸半径（冲量可达范围，非仅致死带）");
            Assert.True(spacing8 > lethal, "且必须超出致死带");
        }

        [Fact]
        public void VolleySpacing_SingleMissile_ReturnsSafetySpacing()
        {
            Assert.Equal(2000.0, MissileVolleyMath.ComputeSpacing(2000.0, 16000.0, 1), 9);
        }

        [Fact]
        public void VolleySpacing_FloorRespected_WhenSideLimitTooSmall()
        {
            // 总长上限过小（8km/7 ≈ 1.14km）时间距被爆炸半径下限（1.8km）抬回——宁超长不冒冲量
            double spacing = MissileVolleyMath.ComputeSpacing(2000.0, 8000.0, 8, 1800.0);
            Assert.Equal(1800.0, spacing, 6);
        }

        [Fact]
        public void BlastLethalRadius_ZeroWhenDamageCannotKill()
        {
            Assert.Equal(0.0, MissileVolleyMath.BlastLethalRadius(1500.0, 150.0, 200.0), 9);
        }
    }
}
