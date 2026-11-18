using System.Collections.Generic;
using System.Reflection;
using PioneerFramework.Celestial;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 行星际粗转移的**捕获决策**测试（2026-11-18，授权件 `后端变更授权-20261118-行星际粗转移`）。
    ///
    /// 不走真实星历：用 headless 世界壳 + 反射注入一个"目标天体"（GM/半径），
    /// 直接驱动 `TryArmInterplanetaryCapture`，钉住三条终态：
    ///   ① 双曲线进入 + 有燃料 ⇒ 捕获点火（phase=Capture, HasActiveBurn）；
    ///   ② 有界进入 ⇒ 不点火，直接 Captured；
    ///   ③ 双曲线进入 + 没燃料 ⇒ 不点火，Flyby（且必须留 Error）。
    /// </summary>
    public class InterplanetaryCaptureTests
    {
        private const double TargetMu = 4.9028e12;
        private const double TargetRadius = 1.737e6;
        private const double SoiRadius = 6.6e7;      // 入口半径（测试用，不依赖真实 SOI）
        private const double HyperbolicSpeed = 800.0; // 远超逃逸（≈385 m/s）
        private const double BoundSpeed = 100.0;

        private static CelestialBodyManager Manager(SpaceAiRuntimeTestHost host)
            => (CelestialBodyManager)host.Game.GetComponent<CelestialBodyManager>();

        private static void InjectTargetBody(CelestialBodyManager manager)
        {
            var field = typeof(CelestialBodyManager).GetField("bodiesById",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new System.InvalidOperationException("bodiesById field missing");
            var bodies = (Dictionary<int, CelestialBodyInfo>)field.GetValue(manager)!;
            bodies[0] = new CelestialBodyInfo
            {
                bodyId = 0, defName = "PF_TestTarget", label = "测试目标",
                bodyType = CelestialBodyType.Planet, radius = TargetRadius,
                gravitationalParameter = TargetMu, hasSurface = true,
            };
        }

        private static SpaceEntityGroup ArmGroup(SpaceAiRuntimeTestHost host, double speed, bool withFuel)
        {
            var group = host.AddShip();
            group.CelestialBodyId = 0;
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)new DVector3(SoiRadius, 0, 0),
                velocitySystem = (SystemVec3)new DVector3(0, 0, speed),
                rotation = DQuaternion.identity,
            };
            group.interplanetaryTargetBodyId = 0;
            group.interplanetaryPhase = SpaceGameComp.InterplanetaryPhaseOutbound;

            var entity = new SpaceEntity(1000, 2) { GroupId = group.GroupId };
            if (withFuel)
            {
                entity.AddPropulsion(new PropulsionData
                {
                    propellant = new PropellantDef { defName = "PF_TestTransferFuel" },
                    ve = 3000, massFlow = 1, mass = 1000, capacity = 1000, isVectoredRcs = false,
                });
            }
            host.Space.RegisterEntity(entity);
            group.GetEntityIds().Add(entity.EntityId);
            return group;
        }

        [Fact]
        public void HyperbolicArrivalWithFuel_BurnsToCapture()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var manager = Manager(host);
            InjectTargetBody(manager);
            var group = ArmGroup(host, HyperbolicSpeed, withFuel: true);

            host.Space.TryArmInterplanetaryCapture(group, 0, manager, 100);

            Assert.Equal(SpaceGameComp.InterplanetaryPhaseCapture, group.interplanetaryPhase);
            Assert.True(group.HasActiveBurn, "捕获应当点火");
        }

        [Fact]
        public void BoundArrival_CapturesWithoutBurning()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var manager = Manager(host);
            InjectTargetBody(manager);
            var group = ArmGroup(host, BoundSpeed, withFuel: true);

            host.Space.TryArmInterplanetaryCapture(group, 0, manager, 100);

            Assert.Equal(SpaceGameComp.InterplanetaryPhaseCaptured, group.interplanetaryPhase);
            Assert.False(group.HasActiveBurn, "已界进入不应点火");
        }

        [Fact]
        public void HyperbolicArrivalWithoutFuel_ReportsFlybyAndDoesNotBurn()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var manager = Manager(host);
            InjectTargetBody(manager);
            var group = ArmGroup(host, HyperbolicSpeed, withFuel: false);

            host.Space.TryArmInterplanetaryCapture(group, 0, manager, 100);

            Assert.Equal(SpaceGameComp.InterplanetaryPhaseFlyby, group.interplanetaryPhase);
            Assert.False(group.HasActiveBurn, "燃料不足时不许点火");
        }

        [Fact]
        public void IgnoresGroupsThatAreNotOutboundToThisBody()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var manager = Manager(host);
            InjectTargetBody(manager);
            var group = ArmGroup(host, HyperbolicSpeed, withFuel: true);
            group.interplanetaryPhase = SpaceGameComp.InterplanetaryPhaseNone;

            host.Space.TryArmInterplanetaryCapture(group, 0, manager, 100);

            Assert.Equal(SpaceGameComp.InterplanetaryPhaseNone, group.interplanetaryPhase);
            Assert.False(group.HasActiveBurn);
        }

        [Fact]
        public void VerifierCaptainCapturedWhenTheBurnLeftTheOrbitBound()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var manager = Manager(host);
            InjectTargetBody(manager);
            var group = ArmGroup(host, BoundSpeed, withFuel: true);
            group.interplanetaryPhase = SpaceGameComp.InterplanetaryPhaseCapture;

            host.Space.VerifyInterplanetaryTransfers(100);

            Assert.Equal(SpaceGameComp.InterplanetaryPhaseCaptured, group.interplanetaryPhase);
            Assert.False(group.HasActiveBurn);
        }

        [Fact]
        public void VerifierMarksFlybyWhenTheSingleRetryIsAlreadySpent()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var manager = Manager(host);
            InjectTargetBody(manager);
            var group = ArmGroup(host, HyperbolicSpeed, withFuel: true);
            group.interplanetaryPhase = SpaceGameComp.InterplanetaryPhaseCapture;
            group.interplanetaryCaptureRetried = true;

            host.Space.VerifyInterplanetaryTransfers(100);

            Assert.Equal(SpaceGameComp.InterplanetaryPhaseFlyby, group.interplanetaryPhase);
            Assert.False(group.HasActiveBurn);
        }
    }
}
