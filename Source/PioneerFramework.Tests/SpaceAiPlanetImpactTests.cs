using System.Collections.Generic;
using HarmonyLib;
using PioneerFramework.Combat;
using RimWorld;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    public class SpaceAiPlanetImpactTests
    {
        private static readonly List<string> messages = new();
        private static bool CaptureMessage(string text) { messages.Add(text); return false; }
        private static bool SkipDefBootstrapWarning() => false;

        [Theory]
        [InlineData(true, 0, -1)]
        [InlineData(true, 1000, -1)]
        [InlineData(true, 0, 1)]
        [InlineData(false, 0, -1)]
        [InlineData(false, 1000, -1)]
        public void PlanetImpact_UsesMissileExplosionOnlyForRegisteredMissiles(bool isMissile, double speed, double altitude)
        {
            using var host = new SpaceAiRuntimeTestHost();
            var avm = new AutonomousVehicleManager(host.Game);
            host.Game.components.Add(avm);
            const string missileName = "PF_PlanetImpactTestMissile";
            if (DefDatabase<MissileDef>.GetNamedSilentFail(missileName) == null)
                DefDatabase<MissileDef>.Add(new MissileDef { defName = missileName, blastRadius = 1500 });
            var group = new SpaceEntityGroup(20, 2) { CelestialBodyId = 0 };
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)new DVector3(0, host.Space.GetBodySurfaceRadius(0) + altitude, 0),
                velocitySystem = (SystemVec3)new DVector3(0, -speed, 0)
            };
            host.Space.RegisterGroup(group);
            if (isMissile)
                avm.RegisterMissileLeader(20, 30, 1, 10, 1, missileName, "test", AutonomousVehicleManager.MissionType.Standby, -1);
            var patch = new Harmony("PFL.PlanetImpact.MessageCapture");
            patch.Patch(AccessTools.Method(typeof(DefOfHelper), nameof(DefOfHelper.EnsureInitializedInCtor)),
                prefix: new HarmonyMethod(typeof(SpaceAiPlanetImpactTests), nameof(SkipDefBootstrapWarning)));
            patch.Patch(AccessTools.Method(typeof(Messages), nameof(Messages.Message),
                new[] { typeof(string), typeof(MessageTypeDef), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(SpaceAiPlanetImpactTests), nameof(CaptureMessage)));
            messages.Clear();
            try
            {
                AccessTools.Method(typeof(SpaceGameComp), "CheckPlanetCollision").Invoke(host.Space, new object[] { group, 0d });
                var explosions = host.Game.GetComponent<CombatManager>().ActiveExplosions;
                if (altitude > 0)
                {
                    Assert.Empty(messages);
                    Assert.Empty(explosions);
                    Assert.Same(group, host.Space.GetGroupById(20));
                    Assert.NotNull(avm.GetRecord(20));
                    return;
                }
                Assert.Null(host.Space.GetGroupById(20));
                Assert.Null(avm.GetRecord(20));
                if (isMissile)
                {
                    Assert.Empty(messages);
                    Assert.Equal(1500, Assert.Single(explosions).maxRadius);
                }
                else
                {
                    Assert.Contains(speed == 0 ? "着陆" : "坠毁", Assert.Single(messages));
                    Assert.Empty(explosions);
                }
            }
            finally { patch.UnpatchAll(patch.Id); messages.Clear(); }
        }
    }
}
