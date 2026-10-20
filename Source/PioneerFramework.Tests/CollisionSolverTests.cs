using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>碰撞求解第一批核心：角速度接触、顺序冲量、摩擦的纯台架。</summary>
    public class CollisionSolverTests
    {
        private static InertiaTensor.InertiaResult UnitInertia()
            => new()
            {
                HasData = true,
                mass = 1.0,
                p1 = 1.0, p2 = 1.0, p3 = 1.0,
                r00 = 1.0, r11 = 1.0, r22 = 1.0,
            };

        private static CollisionBodyState Body(double x, double vx)
            => new()
            {
                Mass = 1.0,
                Inertia = UnitInertia(),
                Rotation = DQuaternion.identity,
                Position = new DVector3(x, 0, 0),
                Velocity = new DVector3(vx, 0, 0),
            };

        private static CollisionContactPoint Contact(DVector3 point)
            => new()
            {
                Point = point,
                Normal = DVector3.right,
                Penetration = 0.1,
            };

        [Fact]
        public void HeadOn_CenterContact_ReversesNormalRelativeVelocityByRestitution()
        {
            var a = Body(0, 1);
            var b = Body(2, -1);
            Span<CollisionContactPoint> contacts = stackalloc CollisionContactPoint[1];
            contacts[0] = Contact(new DVector3(1, 0, 0));

            CollisionSolver.Resolve(ref a, ref b, contacts, restitution: 0.5, friction: 0.0, iterations: 8);

            double vRel = b.Velocity.x - a.Velocity.x;
            Assert.Equal(1.0, vRel, 9);          // -e * (-2)
            Assert.Equal(0.0, a.AngularVelocity.magnitude, 9);
            Assert.Equal(0.0, b.AngularVelocity.magnitude, 9);
        }

        [Fact]
        public void OffCenterContact_ProducesOppositeAngularVelocity()
        {
            var a = Body(0, 1);
            var b = Body(2, -1);
            Span<CollisionContactPoint> contacts = stackalloc CollisionContactPoint[1];
            contacts[0] = Contact(new DVector3(1, 1, 0));

            CollisionSolver.Resolve(ref a, ref b, contacts, restitution: 0.2, friction: 0.0, iterations: 8);

            Assert.True(a.AngularVelocity.magnitude > 1e-6);
            Assert.True(b.AngularVelocity.magnitude > 1e-6);
            Assert.True(DVector3.Dot(a.AngularVelocity, b.AngularVelocity) < 0.0);
        }

        [Fact]
        public void Friction_ReducesTangentialRelativeVelocity()
        {
            var a = new CollisionBodyState
            {
                Mass = 1.0,
                Inertia = UnitInertia(),
                Rotation = DQuaternion.identity,
                Position = DVector3.zero,
                Velocity = new DVector3(1, 1, 0),
            };
            var b = Body(2, 0);
            Span<CollisionContactPoint> contacts = stackalloc CollisionContactPoint[1];
            contacts[0] = Contact(new DVector3(1, 0, 0));

            CollisionSolver.Resolve(ref a, ref b, contacts, restitution: 0.0, friction: 0.5, iterations: 8);

            // 法向已消；切向被摩擦削减，但不应反向越过 0。
            Assert.True(Math.Abs(a.Velocity.y) < 1.0);
            Assert.True(a.Velocity.y >= -1e-9);
        }

        [Fact]
        public void NoInertiaData_StillSolvesLinearContact()
        {
            var a = new CollisionBodyState
            {
                Mass = 2.0,
                Inertia = default,
                Rotation = DQuaternion.identity,
                Position = DVector3.zero,
                Velocity = new DVector3(2, 0, 0),
            };
            var b = new CollisionBodyState
            {
                Mass = 2.0,
                Inertia = default,
                Rotation = DQuaternion.identity,
                Position = new DVector3(2, 0, 0),
                Velocity = new DVector3(-2, 0, 0),
            };
            Span<CollisionContactPoint> contacts = stackalloc CollisionContactPoint[1];
            contacts[0] = Contact(new DVector3(1, 0, 0));

            CollisionSolver.Resolve(ref a, ref b, contacts, restitution: 0.0, friction: 0.0, iterations: 4);

            Assert.Equal(0.0, b.Velocity.x - a.Velocity.x, 9);
            Assert.Equal(0.0, a.AngularVelocity.magnitude, 9);
        }
    }
}
