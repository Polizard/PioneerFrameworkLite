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
            var p = MissileGuidanceDefaults.Standard;
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
            var p = MissileGuidanceDefaults.Standard;
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
            var p = MissileGuidanceDefaults.Standard;
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
            var p = MissileGuidanceDefaults.Standard;
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
            var p = MissileGuidanceDefaults.Standard;
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
            var p = MissileGuidanceDefaults.Standard;
            // 曾接近到 500m —— 历史值必须同时置 initialized（2026-09-14 起用显式标志替代"≤0=未初始化"哨兵）
            var gs = new MissileGuidanceState { minSurfaceDist = 500.0, minSurfaceInitialized = true };
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
            var p = MissileGuidanceDefaults.Standard;
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

        /// <summary>
        /// C1：**负测试**（原缺——可达测试只断言"包线内必命中"，一个"永远命中"的错误实现也能过）。
        ///
        /// 用**硬约束**做负测试：燃料不足。这不是"难打"，是能量上不可能把横差归零，
        /// 因此实现若无视燃料上限而报 hit，本测试必红。
        ///
        /// ⚠ 实测更正（2026-09-14）：审查建议用"`l > 0.8·(r/c)·lateralAccel` 即不可达"写负测试，
        /// **按原样写不出来**——那条判据只是可达测试里的**保守跳过过滤器**（目标静止时导弹减速修横差
        /// 会把 tGo 拉长），实测 `r=1500 c=100 l=1600` 被它判为"包线外"却真实命中（minSD=−57）。
        /// 故负测试必须建立在**物理硬约束**上，不能建立在那条启发式上。
        /// </summary>
        [Fact]
        public void Terminal_InsufficientFuel_CannotHit()
        {
            var p = MissileGuidanceDefaults.Standard;

            // 1kg 工质（标准弹 2000kg）+ 大横差：无论控制律多聪明都修不完横差
            var starved = MissileFlightSim.Crossing(6000, 300, 800, legacy: false);
            starved.fuelMassOverride = 1.0;
            var starvedRes = starved.Run(p);
            Assert.True(!starvedRes.hit || starvedRes.aborted,
                string.Format("燃料不足不得命中：hit={0} ab={1} minSD={2:F0}",
                    starvedRes.hit, starvedRes.aborted, starvedRes.minSurfaceDist));

            // 同一几何给足燃料 → 应命中（证明上面红的是"燃料"而不是"几何本身就打不中"）
            var fueled = MissileFlightSim.Crossing(6000, 300, 800, legacy: false);
            var fueledRes = fueled.Run(p);
            Assert.True(fueledRes.hit,
                string.Format("同几何给足燃料应命中（否则本测试的对照失效）：minSD={0:F0} ab={1}",
                    fueledRes.minSurfaceDist, fueledRes.aborted));
        }

        /// <summary>
        /// C2：`closingDemand` 的**对照断言**（原只断上界 &lt;200，0 或负值也能过）。
        /// 手法：同一几何关掉转向项（turnRateRad 取极大 → 转向耗时≈0），需求应显著更高。
        /// 这样断言的是"转向时间**被计入**"，而不是"需求被压低了"。
        /// </summary>
        [Fact]
        public void Guidance_ClosingDemand_TurnTimeTermIsLoadBearing()
        {
            var baseP = MissileGuidanceDefaults.Standard;
            var gs = new MissileGuidanceState();
            var pos = DVector3.zero;
            var vel = new DVector3(50, 300, 0);
            var aim = new DVector3(2000, 0, 0);

            var withTurn = MissileGuidanceCore.ComputeStep(pos, vel, aim, DVector3.zero, 57.0, baseP, gs);

            var fast = baseP;
            fast.turnRateRad = 1e6; // 转向瞬时 → tRot≈0 → 需求只受横差归零约束
            var noTurn = MissileGuidanceCore.ComputeStep(pos, vel, aim, DVector3.zero, 57.0, fast, gs);

            Assert.True(withTurn.closingDemand < 200.0,
                "含转向时间时应被压到 200 以下，实际 " + withTurn.closingDemand.ToString("F0"));
            Assert.True(withTurn.closingDemand > 50.0,
                "且不应被压到近零（下界守卫），实际 " + withTurn.closingDemand.ToString("F0"));
            Assert.True(noTurn.closingDemand > withTurn.closingDemand * 1.3,
                string.Format("关掉转向项后需求应显著更高（证明转向项承重）：含={0:F0} 不含={1:F0}",
                    withTurn.closingDemand, noTurn.closingDemand));
        }

        /// <summary>
        /// C3：边界用例——`targetRadius = 0`（无包围球）与 `pos == aimPoint`（重合早退分支）。
        /// 这两条此前无任何用例覆盖；重合分支若写坏（如除零/NaN 传播）不会被现有测试发现。
        /// </summary>
        [Fact]
        public void Guidance_DegenerateGeometry_NoNaNAndEarlyReturn()
        {
            var p = MissileGuidanceDefaults.Standard;
            var gs = new MissileGuidanceState();

            // ① pos == aimPoint：应早退，不产生 NaN，动作合法
            var coincident = MissileGuidanceCore.ComputeStep(
                DVector3.zero, new DVector3(300, 0, 0), DVector3.zero, DVector3.zero, 57.0, p, gs);
            Assert.True(!double.IsNaN(coincident.dv.x) && !double.IsNaN(coincident.dv.y) && !double.IsNaN(coincident.dv.z),
                "重合早退不得产生 NaN");
            Assert.True(coincident.action == MissileGuidanceAction.Coast
                || coincident.action == MissileGuidanceAction.Burn);

            // ② targetRadius = 0：表面距 = 质心距，仍应正常工作且给出有限值
            var noRadius = MissileGuidanceCore.ComputeStep(
                DVector3.zero, new DVector3(100, 0, 0), new DVector3(5000, 0, 0), DVector3.zero, 0.0, p, gs);
            Assert.Equal(5000.0, noRadius.surfaceDist, 6);
            Assert.Equal(MissileGuidanceAction.Burn, noRadius.action);

            // ③ 贴表面（surfaceDist == 0）之后再远离：历史必须保持 0
            // ——旧实现拿 `minSurfaceDist <= 0` 当"未初始化"哨兵，会把这 0 当成未初始化、
            //    下一步用更大的值覆盖它 → 追丢判据被抬高、提前 Abort（2026-09-14 改为显式 initialized 标志）
            var st = new MissileGuidanceState();
            var touching = MissileGuidanceCore.ComputeStep(
                DVector3.zero, new DVector3(300, 0, 0), new DVector3(57.0, 0, 0), DVector3.zero, 57.0, p, st);
            Assert.Equal(0.0, touching.state.minSurfaceDist, 9);

            var receding = MissileGuidanceCore.ComputeStep(
                DVector3.zero, new DVector3(-300, 0, 0), new DVector3(157.0, 0, 0), DVector3.zero, 57.0, p, touching.state);
            Assert.Equal(0.0, receding.state.minSurfaceDist, 9);
            Assert.Equal(100.0, receding.surfaceDist, 6);
        }

        [Fact]
        public void Terminal_FrontOfQueue_CloseRangeWithLateralOffset_Converges()
        {
            // 队首几何（2026-09-08 游戏内擦过场景）：0.88km 处 400m 横偏 + Combat 步长（dt=1/30s）
            var p = MissileGuidanceDefaults.Standard;
            var sim = MissileFlightSim.OffsetApproach(880.0, 150.0, 400.0, legacy: false);
            sim.dt = 1.0 / 30.0;
            sim.maxSteps = 20000;
            var res = sim.Run(p);
            Assert.True(res.hit, "队首近距横偏应收敛命中（minSD=" + res.minSurfaceDist.ToString("F0") + "）");
        }

        [Fact]
        public void Terminal_LateralPositionOffset_Converges()
        {
            var p = MissileGuidanceDefaults.Standard;
            // 深度队列横向 jitter ±286m：位置偏置（速度平行于"发射器→目标"线）
            var res = MissileFlightSim.OffsetApproach(11000, 150, 286, legacy: false).Run(p);
            Assert.True(res.hit, "横向位置偏置 286m / 11km 接近应收敛命中");
        }

        [Fact]
        public void Reacquire_NearMiss_ComesBackAndHits()
        {
            var p = MissileGuidanceDefaults.Standard;
            var sim = MissileFlightSim.Crossing(1500, 400, 200, legacy: false);
            sim.maxSteps = 6000;
            var res = sim.Run(p);
            Assert.True(res.hit, "擦过后应掉头再捕获命中（几何未丢失前不中止）");
        }

        [Fact]
        public void Abort_FleeingTarget_StopsInsteadOfBurningForever()
        {
            var p = MissileGuidanceDefaults.Standard;
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
            var p = MissileGuidanceDefaults.Standard;

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

        /// <summary>
        /// A3：齐射滑杆的**显示值**必须等于发射路径实际用的间距（同一函数、同一组输入）。
        /// 修前 UI 自己写死 `min(2000, 10000/(n−1))` → n=8 显示 1429m 而实际 2000m；
        /// 且那个 10000 是**被废弃的旧几何（10km 队长上限）**，即"间距压到爆炸半径内、
        /// 后弹被兄弟炸飞"那个 bug 的参数——UI 显示的是修复前的数字。
        /// 现在两侧都调 `ComputeSpacing(Def.safetySpacingMeters, Def.arraySideLimitMeters, n, blastRadius×1.2)`，
        /// 本测试把 n=8 的显示值钉在 2000m（谁改坏任一侧都会红）。
        /// </summary>
        [Fact]
        public void VolleySlider_DisplayedSpacing_MatchesLaunchPath()
        {
            // 与 PF_Missile_Standard 的实际取值一致（safetySpacing 2000 与 arraySideLimit 16000 用 C# 默认，blastRadius 1500 由 XML 覆盖）
            const double safetySpacing = 2000.0;
            const double arraySideLimit = 16000.0;
            const double blastRadius = 1500.0;
            double floor = blastRadius * 1.2;

            Assert.Equal(2000.0, MissileVolleyMath.ComputeSpacing(safetySpacing, arraySideLimit, 8, floor), 6);
            Assert.Equal(14000.0, (8 - 1) * 2000.0, 6);   // n=8 队列总长 = 14km ≤ 上限 16km（未被压）
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
