using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **[A2-2] 风洞台架**（计划稿 §2.2 的五条判据 + 一条抓"逐轴相加"的陷阱用例）。
    ///
    /// ⚠ 大气参数是**合成值**（[补2]：ρ₀/H 数值延后裁定）——本台架测的是**算式**：
    /// 退化一致性 / 共转零阻力 / 各向异性比 = 面积比 / 终速次序 / 真空零 / 陷阱。
    ///
    /// 空气相对速度用**生产同一口** `SurfaceFrame`（不自己手写 ω×p）——台架与生产共用一个换系口。
    /// </summary>
    public class AeroDragTests
    {
        private const double Rho = 1.2;          // 合成 ρ (kg/m³)
        private const double Mass = 1000.0;      // 合成质量 (kg)
        private const double DayTicks = 60000.0; // 与 Earth Def 的 dayLengthTicks 同口径

        private static readonly DVector3 Fwd = DVector3.forward;
        private static readonly DVector3 Left = DVector3.left;
        private static readonly DVector3 Up = DVector3.up;

        /// <summary>"各向同性公式"的独立参照（判据 1 的对照组；与被测路径同算式 ⇒ 可比逐位）。</summary>
        private static DVector3 IsotropicReference(double rho, DVector3 vAir, double cdA, double mass)
        {
            double speed = vAir.magnitude;
            DVector3 vHat = vAir / speed;
            double q = 0.5 * rho * speed * speed;
            return vHat * (-(q * cdA / mass));
        }

        // ── 判据 1：退化一致性（三轴相等 ⇒ 任意姿态与各向同性公式逐位一致）──

        [Theory]
        [InlineData(0.0, 0.0, 0.0)]        // 姿态 = 单位
        [InlineData(0.7, 0.3, 0.5)]
        [InlineData(2.5, -1.2, 0.4)]
        [InlineData(0.955, 0.955, 0.955)]  // 斜向姿态（"对角飞行"陷阱的姿态面）
        public void IsotropicShape_MatchesIsotropicFormula_Bitwise_ForAnyAttitude(double ax, double ay, double az)
        {
            const double c = 72.5;
            var shape = AeroShape.Isotropic(c);
            var vAir = new DVector3(123.0, -45.0, 67.0);

            // 任意姿态：把世界基向量转一组机体三轴（正交单位）
            var q = DQuaternion.AngleAxis(1.0, new DVector3(ax, ay, az).sqrMagnitude > 0
                ? new DVector3(ax, ay, az).normalized : DVector3.up);
            DVector3 f = DVector3.Rotate(Fwd, q), l = DVector3.Rotate(Left, q), u = DVector3.Rotate(Up, q);

            // A_eff 必须**恰好**是系数（标量路径；不是 c·√(Σp²) 的近似值）
            DVector3 vHat = vAir / vAir.magnitude;
            Assert.Equal(c, AeroDrag.EffectiveArea(shape, vHat, f, l, u));

            var got = AeroDrag.Accel(Rho, vAir, shape, f, l, u, Mass);
            var expected = IsotropicReference(Rho, vAir, c, Mass);
            Assert.Equal(expected.x, got.x);
            Assert.Equal(expected.y, got.y);
            Assert.Equal(expected.z, got.z);
        }

        // ── 判据 2：与大气共转的物体（v_air ≡ 0）阻力恒 0（无论姿态、无论惯性速度多大）──

        [Fact]
        public void CoRotatingObject_HasZeroDrag_RegardlessOfAttitude()
        {
            const double R = 5_000_000.0;                       // Earth Def 半径
            double omega = 2.0 * Math.PI / DayTicks;            // 唯一口径：GetSurfaceState.RotationOmega
            var p = new DVector3(R, 0.0, 0.0);

            DVector3 vCo = SurfaceFrame.SurfaceVelocity(p, omega);
            Assert.True(vCo.magnitude > 500.0);                 // 前提：赤道共转 ≈523.6 m/s（"惯性速度很大"）

            // 空气相对速度 = 惯性速度 − 地表共转速度：同函数同参数 ⇒ **逐位**为零
            DVector3 vAir = SurfaceFrame.GroundRelativeVelocity(p, vCo, omega);
            Assert.Equal(0.0, vAir.sqrMagnitude);

            var rod = AeroShape.FromBox(new DVector3(1, 1, 30), dragCoefficient: 1.0);
            var q = DQuaternion.AngleAxis(1.1, DVector3.up);
            var a = AeroDrag.Accel(Rho, vAir, rod,
                DVector3.Rotate(Fwd, q), DVector3.Rotate(Left, q), DVector3.Rotate(Up, q), Mass);
            Assert.Equal(0.0, a.sqrMagnitude);

            // 对照组：同样"很大"的惯性速度，但相对空气不静止 ⇒ 阻力非零
            DVector3 vOff = vCo + new DVector3(0.0, 0.0, 1.0);
            var a2 = AeroDrag.Accel(Rho, SurfaceFrame.GroundRelativeVelocity(p, vOff, omega), rod,
                Fwd, Left, Up, Mass);
            Assert.True(a2.sqrMagnitude > 0.0);
        }

        // ── 判据 3：正飞 vs 侧飞的阻力比 = 对应迎风面积比（解析对照）──

        [Fact]
        public void NoseFirstVsSideways_Ratio_EqualsAreaRatio()
        {
            var rod = AeroShape.FromBox(new DVector3(1, 1, 30), dragCoefficient: 1.0);   // 头锥 1 m² / 侧向 30 m²
            var vNose = Fwd * 200.0;
            var vSide = Left * 200.0;

            double aNose = AeroDrag.Accel(Rho, vNose, rod, Fwd, Left, Up, Mass).magnitude;
            double aSide = AeroDrag.Accel(Rho, vSide, rod, Fwd, Left, Up, Mass).magnitude;

            Assert.Equal(1.0, AeroDrag.EffectiveArea(rod, vNose.normalized, Fwd, Left, Up));
            Assert.Equal(30.0, AeroDrag.EffectiveArea(rod, vSide.normalized, Fwd, Left, Up));
            Assert.True(aSide > aNose);
            Assert.True(Math.Abs(aNose / aSide - rod.forwardCdA / rod.leftCdA) < 1e-12,
                $"阻力比 {aNose / aSide:E6} ≠ 面积比 {rod.forwardCdA / rod.leftCdA:E6}");
        }

        // ── 判据 4：同推力下终速——侧飞 < 正飞（能量意义）──

        [Fact]
        public void TerminalSpeed_SidewaysSlowerThanNoseFirst()
        {
            var rod = AeroShape.FromBox(new DVector3(1, 1, 30));
            const double thrustAccel = 5.0;   // 合成推力加速度 (m/s²)

            double vNose = TerminalSpeed(rod, Fwd, thrustAccel);
            double vSide = TerminalSpeed(rod, Left, thrustAccel);

            Assert.True(vSide < vNose, $"侧飞终速 {vSide:F2} 应 < 正飞终速 {vNose:F2}");
            // 次序的来源是面积：v_t ∝ 1/√A ⇒ v_side/v_nose = √(A_nose/A_side)
            Assert.True(Math.Abs(vSide / vNose - Math.Sqrt(rod.forwardCdA / rod.leftCdA)) < 1e-6);
        }

        private static double TerminalSpeed(in AeroShape shape, DVector3 dir, double thrustAccel)
        {
            double lo = 0.0, hi = 100_000.0;   // 二分：阻力加速度 = 推力加速度
            for (int i = 0; i < 100; i++)
            {
                double mid = 0.5 * (lo + hi);
                double a = AeroDrag.Accel(Rho, dir * mid, shape, Fwd, Left, Up, Mass).magnitude;
                if (a < thrustAccel) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        // ── 判据 5：真空物体不产生任何气动作用（ρ=0 ⇒ 逐位零）──

        [Fact]
        public void Vacuum_ProducesExactZero()
        {
            var shape = AeroShape.FromBox(new DVector3(6, 6, 12));
            var a = AeroDrag.Accel(0.0, new DVector3(7000.0, -1200.0, 300.0), shape, Fwd, Left, Up, Mass);
            Assert.Equal(0.0, a.x);
            Assert.Equal(0.0, a.y);
            Assert.Equal(0.0, a.z);
        }

        // ── 斜向气流：**盒子解析投影**（2026-10-28 改口径）──

        [Fact]
        public void ObliqueFlow_UsesBoxProjection_NotQuadrature()
        {
            // 前/左都 30（x·y = 30、y·z = 30 ⇒ 方形柱体），45° 斜吹时
            //   盒子真投影 = |n·ẑ|·30 + |n·x̂|·30 = 42.43  ← 现在用这个（= 真实阴影面积）
            //   旧口径（二次范数）= 30 —— 对盒模型是**低估**（立方体沿体对角会低估 42%）
            // ⚠ D13 禁的是"**力**逐轴相加"；**面积**的逐轴投影加权是盒模型正解。
            var shape = new AeroShape(forwardCdA: 30.0, leftCdA: 30.0, upCdA: 5.0);
            double s = 1.0 / Math.Sqrt(2.0);
            var vHat = new DVector3(s, 0.0, s);

            double aEff = AeroDrag.EffectiveArea(shape, vHat, Fwd, Left, Up);

            Assert.Equal(30.0 * Math.Sqrt(2.0), aEff, 12);   // 42.4264
            Assert.True(aEff > 35.0, $"A_eff={aEff:R} 落在'二次范数'值 30 附近 ⇒ 用了旧算式");
        }

        // ── 方向自洽：阻力与空气相对速度反向 ──

        [Fact]
        public void Accel_OpposesAirRelativeVelocity()
        {
            var shape = AeroShape.FromBox(new DVector3(6, 6, 12));
            var vAir = new DVector3(120.0, -30.0, 80.0);
            var a = AeroDrag.Accel(Rho, vAir, shape, Fwd, Left, Up, Mass);

            Assert.True(DVector3.Dot(a, vAir) < 0.0);
            Assert.True(DVector3.Cross(a, vAir).magnitude < 1e-9 * a.magnitude * vAir.magnitude);
        }
    }
}
