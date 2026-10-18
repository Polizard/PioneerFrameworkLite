using PioneerFramework.Combat;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **角冲量符号**（2026-10-17 用户报「碰撞后双方同向转」）：
    /// 同一接触点、等大反向的两个冲量，两侧 Δω 的符号由杠杆臂决定——
    /// 头对头（杠杆与冲量共线）= 0；偏置接触 = 反向。
    /// </summary>
    public class CollisionAngularImpulseTests
    {
        private static InertiaTensor.InertiaResult Isotropic(double p)
        {
            return new InertiaTensor.InertiaResult
            {
                HasData = true,
                mass = 1000.0,
                p1 = p, p2 = p, p3 = p,
                r00 = 1.0, r11 = 1.0, r22 = 1.0,
                type = InertiaTensor.InertiaType.Isotropic
            };
        }

        [Fact]
        public void HeadOnContact_OnCenterLine_GivesZeroDeltaOmega()
        {
            var r = Isotropic(1000.0);

            // A 在原点、B 在 +36m；接触点 (3,0,0) 在两者中心连线上，冲量沿 X ⇒ r×J = 0
            DVector3 dwA = CombatManager.ComputeCollisionDeltaOmega(
                r, DQuaternion.identity, DVector3.zero, new DVector3(3, 0, 0), new DVector3(-100, 0, 0));
            DVector3 dwB = CombatManager.ComputeCollisionDeltaOmega(
                r, DQuaternion.identity, new DVector3(36, 0, 0), new DVector3(3, 0, 0), new DVector3(100, 0, 0));

            Assert.Equal(0.0, dwA.magnitude, 12);
            Assert.Equal(0.0, dwB.magnitude, 12);
        }

        [Fact]
        public void OffCenterContact_MirroredLevers_GiveOppositeDeltaOmega()
        {
            var r = Isotropic(1000.0);

            // 接触点偏 +Y 1m：A 杠杆 (3,1,0)、B 杠杆 (-33,1,0) ⇒ 两侧 Δω 的 Z 分量应反向
            DVector3 dwA = CombatManager.ComputeCollisionDeltaOmega(
                r, DQuaternion.identity, DVector3.zero, new DVector3(3, 1, 0), new DVector3(-100, 0, 0));
            DVector3 dwB = CombatManager.ComputeCollisionDeltaOmega(
                r, DQuaternion.identity, new DVector3(36, 0, 0), new DVector3(3, 1, 0), new DVector3(100, 0, 0));

            Assert.True(dwA.z > 0.0, $"A 应 +Z 转，实际 {dwA.z}");
            Assert.True(dwB.z < 0.0, $"B 应 -Z 转，实际 {dwB.z}");
            Assert.True(DVector3.Dot(dwA, dwB) < 0.0, $"两侧角速度应反向，实际点积={DVector3.Dot(dwA, dwB)}");
        }
    }
}
