using System;
using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>B8：无 Map 实体的主推轴由 part 引擎聚合（旧 mainAxisShip 兜底）。</summary>
    public class MainThrustAxisAggregationTests
    {
        [Fact]
        public void NoEngineParts_FallsBackToLegacyMainAxisShip()
        {
            var e = new SpaceEntity(1, 0)
            {
                mainAxisShip = new DVector3(2, 0, 0),
            };
            e.SetParts(new List<SpacePart> { Part(1) });

            AssertAxis(new DVector3(1, 0, 0), e.GetMainThrustAxisShip());
        }

        [Fact]
        public void PartEngines_AggregateByThrustWeight()
        {
            var e = new SpaceEntity(2, 0);
            var back = Part(1);
            back.AddComponent(Engine(ThrustAxis.Back, ve: 3000f, flow: 10f));   // 30 kN
            var forward = Part(2);
            forward.AddComponent(Engine(ThrustAxis.Forward, ve: 3000f, flow: 5f)); // 15 kN
            e.SetParts(new List<SpacePart> { back, forward });

            // 2026-10-27 口径：轴 = 喷口方向，主推轴 = 推力 = −Σ(轴·ve·ṁ)
            // Back 30 kN（喷口 −Z ⇒ 推力 +Z）压过 Forward 15 kN（喷口 +Z ⇒ 推力 −Z）⇒ +Z
            AssertAxis(new DVector3(0, 0, 1), e.GetMainThrustAxisShip());
        }

        [Fact]
        public void VectoredRcs_DoesNotDefineMainAxis()
        {
            var e = new SpaceEntity(3, 0)
            {
                mainAxisShip = new DVector3(-1, 0, 0),
            };
            var rcs = Part(1);
            rcs.AddComponent(Engine(ThrustAxis.Back, ve: 9000f, flow: 100f, isVectoredRcs: true));
            e.SetParts(new List<SpacePart> { rcs });

            AssertAxis(new DVector3(-1, 0, 0), e.GetMainThrustAxisShip());
        }

        [Fact]
        public void DestroyedEngine_IsExcluded()
        {
            var e = new SpaceEntity(4, 0);
            var back = Part(1);
            back.AddComponent(Engine(ThrustAxis.Back, ve: 3000f, flow: 10f));
            var forward = Part(2);
            forward.AddComponent(Engine(ThrustAxis.Forward, ve: 3000f, flow: 100f));
            forward.isDestroyed = true;
            e.SetParts(new List<SpacePart> { back, forward });

            // 只剩 Back（喷口 −Z ⇒ 推力 +Z）
            AssertAxis(new DVector3(0, 0, 1), e.GetMainThrustAxisShip());
        }

        [Fact]
        public void EnginePartRotation_RotatesThrustAxis()
        {
            var e = new SpaceEntity(5, 0);
            var rotated = Part(1);
            rotated.rotationShip = DQuaternion.AngleAxis(Math.PI, DVector3.up);
            rotated.AddComponent(Engine(ThrustAxis.Back, ve: 3000f, flow: 10f));
            e.SetParts(new List<SpacePart> { rotated });

            // Back（喷口 −Z）绕 +Y 转 180° ⇒ 喷口 +Z ⇒ 推力 −Z
            AssertAxis(new DVector3(0, 0, -1), e.GetMainThrustAxisShip());
        }

        [Fact]
        public void SymmetricOpposingEngines_FallBackToLegacyAxis()
        {
            var e = new SpaceEntity(6, 0)
            {
                mainAxisShip = new DVector3(0, 1, 0),
            };
            var back = Part(1);
            back.AddComponent(Engine(ThrustAxis.Back, ve: 3000f, flow: 10f));
            var forward = Part(2);
            forward.AddComponent(Engine(ThrustAxis.Forward, ve: 3000f, flow: 10f));
            e.SetParts(new List<SpacePart> { back, forward });

            AssertAxis(new DVector3(0, 1, 0), e.GetMainThrustAxisShip());
        }

        private static SpacePart Part(int id)
            => new()
            {
                partId = id,
                boundsSizeShip = new DVector3(1, 1, 1),
                hp = 100f,
            };

        private static EngineComponentData Engine(ThrustAxis axis, float ve, float flow, bool isVectoredRcs = false)
            => new()
            {
                thrustAxis = axis,
                exhaustVelocity = ve,
                massFlowRate = flow,
                isVectoredRcs = isVectoredRcs,
                propellantDefName = "P",
            };

        private static void AssertAxis(DVector3 expected, DVector3 actual)
        {
            Assert.Equal(1.0, actual.magnitude, 9);
            Assert.Equal(expected.x, actual.x, 9);
            Assert.Equal(expected.y, actual.y, 9);
            Assert.Equal(expected.z, actual.z, 9);
        }
    }
}
