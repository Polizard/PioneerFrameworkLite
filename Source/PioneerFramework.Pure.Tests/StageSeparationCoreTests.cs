using System;
using System.Collections.Generic;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **抛级分离刚体解算的判据矩阵**（2026-10-30，用户方案："两组原点都取质心，速度按分离时的
    /// 角速度/速度与两半质心位置严格算"）。
    ///
    /// 口径（⚠ 两条容易写错的）：
    /// 1. 本核约定 `velocityOrigin` = **原点那一点**的速度（不是质心速度）。整船动量因此是
    ///    `m·(V + ω×r_com)`，只有当原点恰好是质心时才等于 `m·V`；
    /// 2. 大坐标（1e6 m 量级）下"世界位置不变"只能按 **1 µm 级容差**判 —— 浮点的
    ///    `Rotate(p−c)+Rotate(c) ≠ Rotate(p)` 是数学相等、比特不等。
    /// </summary>
    public class StageSeparationCoreTests
    {
        private const double PosTol = 1e-6;        // m（大坐标加减的舍入量级 ~2e-10，1 µm 足够严）
        private const double RelTol = 1e-11;       // 动量/角动量的相对容差

        // 两段"零件"：A 在机头侧（z 大）、B 在尾部（z 小 = 被抛级），质量有意做成不对称。
        private static readonly DVector3[] PartPos =
        {
            new(0, 0, 4), new(1.5, 0, 2), new(-1.5, 0, 2), new(0, 0, -3), new(0.8, 0.6, -6),
        };
        private static readonly double[] PartMass = { 900, 150, 150, 1200, 400 };
        private static readonly bool[] IsB = { false, false, false, true, true };

        private static readonly DVector3 Origin = new(1_000_000, 2_000_000, 3_000_000);
        private static readonly DVector3 Velocity = new(0, 7000, 100);

        private static (SeparationMass a, SeparationMass b) Halves()
        {
            var a = new SeparationMass();
            var b = new SeparationMass();
            for (int i = 0; i < PartPos.Length; i++)
            {
                if (IsB[i]) StageSeparationCore.Add(ref b, PartMass[i], PartPos[i]);
                else StageSeparationCore.Add(ref a, PartMass[i], PartPos[i]);
            }
            return (a, b);
        }

        private static double TotalMass()
        {
            double m = 0;
            for (int i = 0; i < PartMass.Length; i++) m += PartMass[i];
            return m;
        }

        private static DVector3 WholeCenter()
        {
            DVector3 sum = DVector3.zero;
            for (int i = 0; i < PartPos.Length; i++) sum += PartPos[i] * PartMass[i];
            return sum / TotalMass();
        }

        /// <summary>整船刚体（原点速度 V + 世界 ω + 姿态 R）的**总动量**。</summary>
        private static DVector3 WholeMomentum(in DVector3 v, in DVector3 omega, in DQuaternion rot)
            => (v + DVector3.Cross(omega, DVector3.Rotate(WholeCenter(), rot))) * TotalMass();

        private static void Solve(out SeparationSolution s, double relativeSpeed = 1.0,
            DVector3? omega = null, DQuaternion? rotation = null)
        {
            var (a, b) = Halves();
            DVector3 w = omega ?? DVector3.zero;
            DQuaternion r = rotation ?? DQuaternion.identity;
            bool ok = StageSeparationCore.Solve(a, b, Origin, Velocity, w, r, relativeSpeed, out s);
            Assert.True(ok, s.reason);
            Assert.True(s.ok);
            Assert.Equal("", s.reason);
        }

        // ── 1. 世界几何逐件不变（重参数化恒等式；大坐标 ⇒ 1 µm 容差）──
        [Fact]
        public void Reorigin_KeepsEveryPartWorldPosition()
        {
            foreach (var rot in new[]
                     {
                         DQuaternion.identity,
                         DQuaternion.AngleAxis(0.7, new DVector3(1, 2, -0.5).normalized),
                     })
            {
                Solve(out var s, relativeSpeed: 0.0, omega: new DVector3(0.01, -0.02, 0.03), rotation: rot);
                for (int i = 0; i < PartPos.Length; i++)
                {
                    DVector3 c = IsB[i] ? s.centerB : s.centerA;
                    DVector3 origin = IsB[i] ? s.originB : s.originA;
                    DVector3 worldBefore = Origin + DVector3.Rotate(PartPos[i], rot);
                    DVector3 worldAfter = origin + DVector3.Rotate(PartPos[i] - c, rot);
                    Assert.True((worldAfter - worldBefore).magnitude <= PosTol,
                        $"i={i} Δ={(worldAfter - worldBefore).magnitude:E3}");
                }
            }
        }

        // ── 2. 质心守恒（相对旧原点）────────────────────────────
        [Fact]
        public void MassCenter_IsPreserved()
        {
            Solve(out var s, relativeSpeed: 1.0);
            DVector3 combined = (s.centerA * s.massA + s.centerB * s.massB) / (s.massA + s.massB);
            Assert.True((combined - WholeCenter()).magnitude <= 1e-12);
        }

        // ── 3. 动量守恒（含冲量与不含冲量；口径 = 整船刚体动量）──
        [Fact]
        public void Momentum_IsPreserved_WithAndWithoutImpulse()
        {
            DVector3 omega = new(0.02, 0.01, -0.03);
            foreach (double vRel in new[] { 0.0, 0.3, 1.0, 2.0 })
            {
                Solve(out var s, vRel, omega: omega);
                DVector3 before = WholeMomentum(Velocity, omega, DQuaternion.identity);
                DVector3 after = s.velocityA * s.massA + s.velocityB * s.massB;
                Assert.True((after - before).magnitude <= RelTol * before.magnitude,
                    $"vRel={vRel} |Δp|={(after - before).magnitude:G3} |p|={before.magnitude:G3}");
            }
        }

        // ── 4. 角动量守恒（粒子级，绕整体质心；口径 = 原点速度 + 世界 ω）──
        [Fact]
        public void AngularMomentum_IsPreserved_WithAndWithoutImpulse()
        {
            DVector3 omega = new(0.02, 0.01, -0.03);
            DVector3 com = WholeCenter();

            foreach (double vRel in new[] { 0.0, 1.0 })
            {
                Solve(out var s, vRel, omega: omega);

                DVector3 lBefore = DVector3.zero;
                for (int i = 0; i < PartPos.Length; i++)
                {
                    DVector3 r = PartPos[i] - com;
                    DVector3 v = Velocity + DVector3.Cross(omega, PartPos[i]);   // 原点速度 + ω×p
                    lBefore += DVector3.Cross(r, v * PartMass[i]);
                }

                DVector3 lAfter = DVector3.zero;
                for (int i = 0; i < PartPos.Length; i++)
                {
                    DVector3 r = PartPos[i] - com;
                    DVector3 c = IsB[i] ? s.centerB : s.centerA;
                    DVector3 vg = IsB[i] ? s.velocityB : s.velocityA;
                    DVector3 v = vg + DVector3.Cross(omega, PartPos[i] - c);     // 该半绕自己质心
                    lAfter += DVector3.Cross(r, v * PartMass[i]);
                }

                Assert.True((lAfter - lBefore).magnitude <= RelTol * lBefore.magnitude,
                    $"vRel={vRel} |ΔL|={(lAfter - lBefore).magnitude:G3} |L|={lBefore.magnitude:G3}");
            }
        }

        // ── 5. 新原点 = 各自质心 ────────────────────────────────
        [Fact]
        public void NewOrigins_AreTheHalvesCentersOfMass()
        {
            Solve(out var s, relativeSpeed: 0.0);
            Assert.True(((s.originA - Origin) - s.centerA).magnitude <= PosTol);
            Assert.True(((s.originB - Origin) - s.centerB).magnitude <= PosTol);
        }

        // ── 6. 方向与级号标签无关（交换 A/B 只翻转方向）──────────
        [Fact]
        public void Direction_FollowsGeometry_NotLabels()
        {
            Solve(out var s);
            var (a, b) = Halves();
            Assert.True(StageSeparationCore.Solve(b, a, Origin, Velocity, DVector3.zero,
                DQuaternion.identity, 1.0, out var swapped));
            Assert.True((swapped.directionBody + s.directionBody).magnitude <= 1e-12);
            Assert.Equal(s.massA, swapped.massB, 9);
        }

        // ── 7. 中心冲量不产生自旋（方向 ∥ 两半质心连线）──────────
        [Fact]
        public void Impulse_IsCentral_SoNoSpin()
        {
            Solve(out var s, relativeSpeed: 1.0, omega: new DVector3(0.01, 0.02, 0.03));
            DVector3 delta = s.centerB - s.centerA;
            Assert.True(DVector3.Cross(s.directionBody, delta).magnitude <= 1e-12);
            // 没有 Δω 输出 = 角速度不变；第 4 条已证明 L 仍守恒。
        }

        // ── 8. 相对分离速度（ω=0 时 = 请求值）───────────────────
        [Fact]
        public void RelativeSpeed_MatchesRequest_WhenNotRotating()
        {
            Solve(out var s, relativeSpeed: 1.5);
            double rel = DVector3.Dot(s.velocityB - s.velocityA, s.directionBody);
            Assert.Equal(1.5, rel, 9);
            Assert.Equal(1.5, s.relativeSpeed, 9);
            Assert.True(s.impulseJ > 0.0);
        }

        // ── 9. **无冲量 + ω≈0 ⇒ 两半相对速度为 0**（"必须给机构冲量"的证据）
        [Fact]
        public void WithoutImpulse_AndNoRotation_HalvesStayTogether()
        {
            Solve(out var s, relativeSpeed: 0.0, omega: DVector3.zero);
            Assert.True((s.velocityB - s.velocityA).magnitude <= 1e-15);
            Assert.Equal(0.0, s.impulseJ, 15);
        }

        // ── 10. 退化输入必须拒绝 ────────────────────────────────
        [Fact]
        public void DegenerateInputs_AreRejected()
        {
            var good = new SeparationMass();
            StageSeparationCore.Add(ref good, 100.0, new DVector3(0, 0, 3));

            Assert.False(StageSeparationCore.Solve(good, new SeparationMass(), DVector3.zero, DVector3.zero,
                DVector3.zero, DQuaternion.identity, 1.0, out var s1));
            Assert.Equal("zero-mass", s1.reason);

            var same = new SeparationMass();
            StageSeparationCore.Add(ref same, 100.0, new DVector3(0, 0, 3));
            Assert.False(StageSeparationCore.Solve(good, same, DVector3.zero, DVector3.zero,
                DVector3.zero, DQuaternion.identity, 1.0, out var s2));
            Assert.Equal("coincident-centers", s2.reason);

            var bad = new SeparationMass();
            StageSeparationCore.Add(ref bad, 100.0, new DVector3(double.NaN, 0, 0));
            Assert.Equal(0.0, bad.mass, 12);          // 非有限位置不计入
        }
    }
}
