using System;
using System.Collections.Generic;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **惯量张量解析样本**（批 0 判据，2026-10-17）：形状预设 / 合成（含平行轴）/ 主轴分解 / 分类器。
    /// 口径见 `Base/Core/InertiaTensor.cs` 头注；这里只钉"手算可得的数"。
    /// </summary>
    public class InertiaTensorTests
    {
        [Fact]
        public void Box_Inertia_MatchesAnalytic()
        {
            InertiaTensor.BoxInertia(12.0, 2.0, 4.0, 6.0, out var ixx, out var iyy, out var izz);
            Assert.Equal(12.0 * (16.0 + 36.0) / 12.0, ixx, 12);  // 52
            Assert.Equal(12.0 * (4.0 + 36.0) / 12.0, iyy, 12);   // 40
            Assert.Equal(12.0 * (4.0 + 16.0) / 12.0, izz, 12);   // 20
        }

        [Fact]
        public void Cylinder_And_Sphere_MatchAnalytic()
        {
            // 2026-10-29：圆柱轴向 = 体 **Z**（旧口径是 Y，见 PartSelfInertia 注释）
            InertiaTensor.CylinderInertia(10.0, 2.0, 3.0, out var ixx, out var iyy, out var izz);
            Assert.Equal(0.5 * 10.0, izz, 12);                 // 轴向 = 5
            Assert.Equal(10.0 * (3.0 + 9.0) / 12.0, ixx, 12);  // 径向 = 10
            Assert.Equal(ixx, iyy, 12);
            InertiaTensor.SphereInertia(10.0, 2.0, out var sx, out var sy, out var sz);
            Assert.Equal(0.4 * 10.0, sx, 12);                  // 4
            Assert.Equal(sx, sy, 12);
            Assert.Equal(sx, sz, 12);
        }

        [Fact]
        public void Cylinder_DiameterTakesX_HeightTakesZ()
        {
            // 圆柱 = 直径(X) + 高(Z)：把 Z 拉长 ⇒ 径向惯量变大、绕 Z 的轴向惯量不变。
            InertiaTensor.PartSelfInertia(InertiaTensor.Shape.Cylinder, 200.0,
                3.0, 3.0, 6.0, false, 0, 0, 0, 0, 0, 0,
                out double ixx, out double iyy, out double izz, out _, out _, out _);
            double r = 1.5;
            Assert.Equal(200.0 * (3.0 * r * r + 36.0) / 12.0, ixx, 9);   // 712.5
            Assert.Equal(ixx, iyy, 12);
            Assert.Equal(0.5 * 200.0 * r * r, izz, 9);                   // 225（绕 Z = 轴向）

            InertiaTensor.PartSelfInertia(InertiaTensor.Shape.Cylinder, 200.0,
                3.0, 3.0, 20.0, false, 0, 0, 0, 0, 0, 0,
                out double ixx2, out _, out double izz2, out _, out _, out _);
            Assert.True(ixx2 > ixx);
            Assert.Equal(izz, izz2, 12);
        }

        [Fact]
        public void Compose_RotatedLocalTensor_SwapsIxxIzz()
        {
            // 长方体 4×2×2（m=12）本体系 I=(8,20,20)；绕 Y 转 90° ⇒ 世界 I=(20,20,8)
            var r = InertiaTensor.Compose(new List<InertiaTensor.PartContribution>
            {
                new()
                {
                    mass = 12.0,
                    cxx = 8.0, cyy = 20.0, czz = 20.0,
                    rotation = DQuaternion.AngleAxis(Math.PI / 2.0, DVector3.up),
                },
            });

            Assert.True(r.HasData);
            Assert.Equal(20.0, r.p1, 9);
            Assert.Equal(20.0, r.p2, 9);
            Assert.Equal(8.0, r.p3, 9);
            Assert.Equal(48.0, r.Trace, 9);
        }

        [Fact]
        public void Compose_TwoPointMasses_MatchesHandCalc()
        {
            // 两个 2 kg 点质量在 z=0 / z=2 ⇒ 质心 z=1；绕质心 Ix=Iy=2·1²·2=4、Iz=0
            var parts = new List<InertiaTensor.PartContribution>
            {
                new() { mass = 2.0, pz = 0.0 },
                new() { mass = 2.0, pz = 2.0 },
            };
            var r = InertiaTensor.Compose(parts);
            Assert.True(r.HasData);
            Assert.Equal(4.0, r.mass, 12);
            Assert.Equal(1.0, r.comZ, 12);
            Assert.Equal(4.0, r.p1, 12);
            Assert.Equal(4.0, r.p2, 12);
            Assert.Equal(0.0, r.p3, 12);
            Assert.Equal(8.0, r.oIxx, 12);   // 绕原点：2·2²
        }

        [Fact]
        public void Compose_TwoBoxes_SelfInertiaPlusParallelAxis()
        {
            // 两个 1×1×1、6 kg 的盒在 z=±1：自惯量各 1（对角），平行轴 +6·1² ⇒ 每件 7
            double self = 6.0 * (1.0 + 1.0) / 12.0;   // 1
            var parts = new List<InertiaTensor.PartContribution>
            {
                new() { mass = 6.0, pz = 1.0, cxx = self, cyy = self, czz = self },
                new() { mass = 6.0, pz = -1.0, cxx = self, cyy = self, czz = self },
            };
            var r = InertiaTensor.Compose(parts);
            Assert.Equal(12.0, r.mass, 12);
            Assert.Equal(0.0, r.comZ, 12);
            Assert.Equal(14.0, r.p1, 9);   // (1 + 6) × 2
            Assert.Equal(14.0, r.p2, 9);
            Assert.Equal(2.0, r.p3, 9);    // 1 × 2
            // 对角张量、两轴严格相等 ⇒ 轴对称（分类器只认严格相等）
            Assert.Equal(InertiaTensor.InertiaType.Axisymmetric, r.type);
        }

        [Fact]
        public void Compose_OffDiagonal_MatchesHandCalc()
        {
            // 两个 2 kg 点质量在 (±1, ±1, 0)：Ixx=Iyy=4、Izz=8、Ixy=−4（质心在原点 ⇒ 两套张量相同）
            var parts = new List<InertiaTensor.PartContribution>
            {
                new() { mass = 2.0, px = 1.0, py = 1.0 },
                new() { mass = 2.0, px = -1.0, py = -1.0 },
            };
            var r = InertiaTensor.Compose(parts);
            Assert.Equal(0.0, r.comX, 12);
            Assert.Equal(4.0, r.oIxx, 12);
            Assert.Equal(4.0, r.oIyy, 12);
            Assert.Equal(8.0, r.oIzz, 12);
            Assert.Equal(-4.0, r.oIxy, 12);
            // 特征值 = {8, 8, 0}（平面内 8/0，z 向 8）
            Assert.Equal(8.0, r.p1, 9);
            Assert.Equal(8.0, r.p2, 9);
            Assert.Equal(0.0, r.p3, 9);
            // 解析上两轴相等；Jacobi 数值路径可能只在 1e-15 内相等 ⇒ **不钉死分类**（档位由严格相等判定）
            Assert.NotEqual(InertiaTensor.InertiaType.Isotropic, r.type);
        }

        [Fact]
        public void PrincipalAxes_Orthonormal_AndEigenEquation()
        {
            double ixx = 10.0, iyy = 7.0, izz = 4.0, ixy = 1.5, ixz = -0.7, iyz = 0.9;
            InertiaTensor.PrincipalAxes(ixx, iyy, izz, ixy, ixz, iyz,
                out var p1, out var p2, out var p3,
                out var r00, out var r01, out var r02,
                out var r10, out var r11, out var r12,
                out var r20, out var r21, out var r22);
            Assert.True(p1 >= p2 && p2 >= p3);
            Assert.Equal(ixx + iyy + izz, p1 + p2 + p3, 9);      // 迹不变
            // 列向量单位正交
            Assert.Equal(1.0, r00 * r00 + r10 * r10 + r20 * r20, 9);
            Assert.Equal(1.0, r01 * r01 + r11 * r11 + r21 * r21, 9);
            Assert.Equal(1.0, r02 * r02 + r12 * r12 + r22 * r22, 9);
            Assert.Equal(0.0, r00 * r01 + r10 * r11 + r20 * r21, 9);
            Assert.Equal(0.0, r00 * r02 + r10 * r12 + r20 * r22, 9);
            Assert.Equal(0.0, r01 * r02 + r11 * r12 + r21 * r22, 9);
            // I·x = p·x（第一条主轴）
            Assert.Equal(p1 * r00, ixx * r00 + ixy * r10 + ixz * r20, 8);
            Assert.Equal(p1 * r10, ixy * r00 + iyy * r10 + iyz * r20, 8);
            Assert.Equal(p1 * r20, ixz * r00 + iyz * r10 + izz * r20, 8);
        }

        [Fact]
        public void Classifier_ThreeTiers_ExactEqualityOnly()
        {
            Assert.Equal(InertiaTensor.InertiaType.Isotropic, InertiaTensor.Classify(5, 5, 5));
            Assert.Equal(InertiaTensor.InertiaType.Axisymmetric, InertiaTensor.Classify(9, 5, 5));
            Assert.Equal(InertiaTensor.InertiaType.Asymmetric, InertiaTensor.Classify(9, 5, 4));
            // 近简并带（相对差 1e-12、非严格相等）⇒ **不**判轴对称（KSA 的三条悬崖之一，我们走数值路径）
            double p2 = 5.0 * (1.0 - 1e-12);   // 注意：必须 ≤ p1（降序前提），否则相对间隙为负
            Assert.Equal(InertiaTensor.InertiaType.Asymmetric, InertiaTensor.Classify(5.0, p2, 2.0));
            Assert.True(InertiaTensor.IsNearDegenerate(5.0, p2, 2.0, out var gap12, out _));
            Assert.True(gap12 > 0.0 && gap12 <= InertiaTensor.DegenerateTolerance);
        }

        [Fact]
        public void SinglePartSquareBox_IsAxisymmetric_AndMatchesBoxAnalytic()
        {
            // 单 part 船的自证刻度：2×3×2 的盒（方形截面 ⇒ 两轴严格相等 ⇒ 轴对称）
            double mass = 100.0;
            InertiaTensor.BoxInertia(mass, 2.0, 3.0, 2.0, out var ixx, out var iyy, out var izz);
            var parts = new List<InertiaTensor.PartContribution>
            {
                new() { mass = mass, cxx = ixx, cyy = iyy, czz = izz },
            };
            var r = InertiaTensor.Compose(parts);
            Assert.True(r.HasData);
            Assert.Equal(0.0, r.comX, 12);
            Assert.Equal(ixx, r.p1, 9);
            Assert.Equal(ixx, r.p2, 9);
            Assert.Equal(iyy, r.p3, 9);
            Assert.Equal(InertiaTensor.InertiaType.Axisymmetric, r.type);
        }

        [Fact]
        public void Compose_DebugShip_ThreeParts_MatchesHandAnchor()
        {
            // 与 `DebugShipSpawner.SpawnMultiPartDebugShip` **同一构型**（游戏内验收的锚点读数）：
            // hull 3000 kg 10×6×40 @原点 · left 800 kg 4×4×8 @(8,0,−10) · right 200 kg 圆柱 Ø3×6 @(−6,2,12)。
            // 期望值由独立实现（numpy 特征值求解，2026-10-29 重算）算出——不是从被测代码抄的。
            // ⚠ 2026-10-29 圆柱轴向 Y→Z：该件的自惯量从 (712.5, 225, 712.5) 变成 (712.5, 712.5, 225)
            //   ⇒ Iyy +487.5、Izz −487.5；主轴随之微调（p1/p2/p3 都换了）。
            var r = InertiaTensor.Compose(DebugShipParts());
            Assert.True(r.HasData);
            Assert.Equal(4000.0, r.mass, 9);
            Assert.Equal(1.30, r.comX, 9);
            Assert.Equal(0.10, r.comY, 9);
            Assert.Equal(-1.40, r.comZ, 9);
            Assert.Equal(583785.24444996, r.p1, 6);
            Assert.Equal(528200.12158937, r.p2, 6);
            Assert.Equal(77184.63396067, r.p3, 6);
            Assert.Equal(InertiaTensor.InertiaType.Asymmetric, r.type);
            Assert.Equal(524645.83333333, r.oIxx, 6);
            Assert.Equal(598245.83333333, r.oIyy, 6);
            Assert.Equal(95558.33333333, r.oIzz, 6);
        }

        [Fact]
        public void MomentAboutBodyAxis_DebugShip_MatchesBodyTensorDiagonal()
        {
            // 本体系三根轴（滚 z / 偏 y / 俯 x）的转动惯量 = 绕质心张量在体轴下的对角元（独立实现值）。
            var r = InertiaTensor.Compose(DebugShipParts());
            Assert.Equal(516765.83333333, r.MomentAboutBodyAxis(1.0, 0.0, 0.0), 6);
            Assert.Equal(583645.83333333, r.MomentAboutBodyAxis(0.0, 1.0, 0.0), 6);
            Assert.Equal(88758.33333333, r.MomentAboutBodyAxis(0.0, 0.0, 1.0), 6);
            // 主轴方向的投影必须还原主轴惯量（方向矩阵与 p 同一份数据）
            Assert.Equal(r.p1, r.MomentAboutBodyAxis(r.r00, r.r10, r.r20), 6);
            Assert.Equal(r.p2, r.MomentAboutBodyAxis(r.r01, r.r11, r.r21), 6);
            Assert.Equal(r.p3, r.MomentAboutBodyAxis(r.r02, r.r12, r.r22), 6);
            // 任意方向的界限：min(p) ≤ nᵀIn ≤ max(p)
            double any = r.MomentAboutBodyAxis(0.6, 0.8, 0.0);
            Assert.InRange(any, r.p3, r.p1);
        }

        [Fact]
        public void RateLimit_AnchorShip_IsFiveDegreesPerSecond_WithFloorAndCeiling()
        {
            // 锚点船 = 默认无Map船（5000 kg / 40×20×80）；最慢主轴（Iyy = (40²+80²)/12·m）保持 5°/s。
            double anchorMoment = InertiaTensor.AnchorSlowestAxisMoment();
            Assert.Equal(5000.0 * (40.0 * 40.0 + 80.0 * 80.0) / 12.0, anchorMoment, 6);
            Assert.Equal(InertiaTensor.AnchorRateDegPerSec,
                InertiaTensor.RateForMoment(anchorMoment) * 180.0 / Math.PI, 9);
            // 16,200 t 地图站量级（I≈1e10）⇒ 压到地板 0.2°/s；小零件量级 ⇒ 封顶 30°/s
            Assert.Equal(InertiaTensor.MinRateDegPerSec,
                InertiaTensor.RateForMoment(1e10) * 180.0 / Math.PI, 9);
            Assert.Equal(InertiaTensor.MaxRateDegPerSec,
                InertiaTensor.RateForMoment(1e3) * 180.0 / Math.PI, 9);
            // KSA 语义助手：authority = τ/I；RateBit = Δh/I
            Assert.Equal(0.4, InertiaTensor.TorqueAuthority(200.0, 500.0), 12);
            Assert.Equal(0.004, InertiaTensor.RateBit(2.0, 500.0), 12);
        }

        [Fact]
        public void InverseInertiaMultiply_Isotropic_IsSimpleDivision()
        {
            // 等惯（cxx=cyy=czz=5）⇒ Δω = h / 5，方向不变
            var r = InertiaTensor.Compose(new List<InertiaTensor.PartContribution>
            {
                new() { mass = 1.0, cxx = 5.0, cyy = 5.0, czz = 5.0 },
            });
            var dw = InertiaTensor.InverseInertiaMultiply(r, 1.0, 2.0, 3.0);
            Assert.Equal(0.2, dw.x, 12);
            Assert.Equal(0.4, dw.y, 12);
            Assert.Equal(0.6, dw.z, 12);
        }

        private static List<InertiaTensor.PartContribution> DebugShipParts()
        {
            var parts = new List<InertiaTensor.PartContribution>();
            AddPart(parts, InertiaTensor.Shape.Box, 3000.0, 10.0, 6.0, 40.0, 0.0, 0.0, 0.0);
            AddPart(parts, InertiaTensor.Shape.Box, 800.0, 4.0, 4.0, 8.0, 8.0, 0.0, -10.0);
            AddPart(parts, InertiaTensor.Shape.Cylinder, 200.0, 3.0, 3.0, 6.0, -6.0, 2.0, 12.0);
            return parts;
        }

        private static void AddPart(List<InertiaTensor.PartContribution> parts, InertiaTensor.Shape shape,
            double mass, double sx, double sy, double sz, double px, double py, double pz)
        {
            InertiaTensor.PartSelfInertia(shape, mass, sx, sy, sz, false, 0, 0, 0, 0, 0, 0,
                out double ixx, out double iyy, out double izz,
                out double ixy, out double ixz, out double iyz);
            parts.Add(new InertiaTensor.PartContribution
            {
                mass = mass, px = px, py = py, pz = pz,
                cxx = ixx, cyy = iyy, czz = izz, cxy = ixy, cxz = ixz, cyz = iyz,
            });
        }
    }
}
