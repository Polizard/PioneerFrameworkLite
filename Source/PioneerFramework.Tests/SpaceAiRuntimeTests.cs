using PioneerFramework.SpaceAI;
using Xunit;

namespace PioneerFramework.Tests
{
    public class SpaceAiRuntimeTests
    {
        [Fact] public void ReleasingOldAiCommand_CannotCancelNewExternalBurn()
        {
            var group=new SpaceEntityGroup(1,2);group.StartBurn(new DVector3(1,0,0));
            var record=new AiShipRecord {ownsBurn=true,ownedBurnVersion=group.BurnCommandVersion};
            group.StartBurn(new DVector3(2,0,0));
            Assert.False(SpaceAiManager.TryReleaseBurn(group,record));Assert.True(group.HasActiveBurn);
        }
        [Fact] public void Registration_IsAtomicAndRejectsDuplicateOwnership()
        {
            var manager=new SpaceAiManager(null);
            Assert.True(manager.TryRegisterFleet(2,new[]{10,11},out int fleet));
            Assert.True(fleet>0);Assert.Equal(2,manager.Ships.Count);
            Assert.False(manager.TryRegisterFleet(2,new[]{12,10},out _));Assert.Equal(2,manager.Ships.Count);
        }
        [Fact] public void FleetTelemetry_CountsOnlyItsActiveAttackMissilesAndRebuildsAfterLoss()
        {
            using var host = new SpaceAiRuntimeTestHost(); var shooter = host.AddShip();
            var target = new SpaceEntityGroup(20,3) { CelestialBodyId = 0 };
            var alternate = new SpaceEntityGroup(21,3) { CelestialBodyId = 0 };
            var missile = new SpaceEntityGroup(30,2) { CelestialBodyId = 0 };
            host.Space.RegisterGroup(target); host.Space.RegisterGroup(alternate); host.Space.RegisterGroup(missile);
            var sensor = new SensorManager(host.Game); var avm = new AutonomousVehicleManager(host.Game);
            host.Game.components.Add(avm);
            var vehicles = (System.Collections.Generic.List<AutonomousVehicleManager.VehicleRecord>)
                HarmonyLib.AccessTools.Field(typeof(AutonomousVehicleManager),"vehicles").GetValue(avm);
            vehicles.Add(new() { groupId = 30, launcherGroupId = 10, targetGroupId = 20,
                logicalCount = 4, mission = AutonomousVehicleManager.MissionType.Attack });
            var tracks = (AiTrackRegistry)HarmonyLib.AccessTools.Field(typeof(SpaceAiManager),"tracks").GetValue(host.Ai);
            sensor.TryGetKnownState(2,target,out var first); sensor.TryGetKnownState(2,alternate,out var second);
            int one = tracks.Remember(2,first), two = tracks.Remember(2,second);
            var contacts = new[] { ContactTargetService.Project(first,one,true,false),
                ContactTargetService.Project(second,two,true,false) };
            var state = new AiShipState(10,DVector3.zero,DVector3.zero,10000,1,1000,100,1,1,AiRole.Missile,true,true,false);
            var fleet = new AiFleetCoordinator();
            host.Ai.RefreshInFlight(fleet,host.Ai.Ships[0].fleetId,2,host.Space,sensor,0);
            Assert.Equal(two,fleet.SelectTarget(state,contacts,0));
            vehicles[0].launcherGroupId = 999;
            host.Ai.RefreshInFlight(fleet,host.Ai.Ships[0].fleetId,2,host.Space,sensor,1);
            Assert.Equal(one,fleet.SelectTarget(state,contacts,1));
            vehicles[0].launcherGroupId = 10;
            host.Space.UnregisterGroup(missile);
            host.Ai.RefreshInFlight(fleet,host.Ai.Ships[0].fleetId,2,host.Space,sensor,2);
            Assert.Equal(one,fleet.SelectTarget(state,contacts,2));
        }
        [Theory] [InlineData(false)] [InlineData(true)]
        public void Unregister_StopsOwnedBurnAndPreservesExternalReplacement(bool replaced)
        {
            using var host = new SpaceAiRuntimeTestHost();
            var group = host.AddShip(); var record = host.Ai.Ships[0];
            group.StartBurn(new DVector3(1,0,0));
            record.ownsBurn = true; record.ownedBurnVersion = group.BurnCommandVersion;
            if (replaced) group.StartBurn(new DVector3(2,0,0));
            host.Ai.UnregisterGroup(group.GroupId);
            Assert.Empty(host.Ai.Ships);
            Assert.Equal(replaced, group.HasActiveBurn);
            Assert.False(record.ownsBurn);
        }
        [Theory] [InlineData(true)] [InlineData(false)]
        public void PendingApproach_IsDiscardedBeforeWorkAfterRetreatOrMapLoss(bool retreat)
        {
            using var host = new SpaceAiRuntimeTestHost();
            var group = host.AddShip(); var record = host.Ai.Ships[0];
            record.planning = new AiOrbitPlanningJob(new DVector3(1000,0,0), DVector3.zero,
                new DVector3(2000,0,0), DVector3.zero, 0, 1, 1000);
            record.planning.Step();
            var target = new SpaceEntityGroup(20,3) { CelestialBodyId = 0 };
            host.Space.RegisterGroup(target);
            record.targetGroupId = 20; record.planningTrack = 1; record.planningBody = 0;
            record.decision = new AiDecision(AiAction.Approach,1,false,true,false,false);
            record.action = retreat ? AiAction.Retreat : AiAction.Approach;
            record.withdrawing = retreat; record.adapter.Ready = retreat;
            group.StartBurn(new DVector3(-10,0,0)); long version = group.BurnCommandVersion;
            record.ownsBurn = true; record.ownedBurnVersion = version;
            HarmonyLib.AccessTools.Method(typeof(SpaceAiManager), "AdvancePlanning").Invoke(host.Ai,
                new object[] { host.Space, new SensorManager(host.Game), 1d });
            Assert.Null(record.planning);
            Assert.Equal(version, group.BurnCommandVersion);
        }
        [Fact] public void Unregistration_ReleasesControlWithoutDeletingOtherRecords()
        {
            var manager=new SpaceAiManager(null);
            manager.TryRegisterFleet(2,new[]{10,11},out _);
            manager.UnregisterGroup(10);Assert.Single(manager.Ships);Assert.Equal(11,manager.Ships[0].groupId);
        }
        [Theory] [InlineData(false,true,2,2,false)] [InlineData(true,false,2,2,false)]
        [InlineData(true,true,2,3,false)] [InlineData(true,true,2,2,true)]
        public void ControlRequiresActiveMovableOriginalFaction(bool active,bool movable,int owner,int faction,bool expected)
        {Assert.Equal(expected,SpaceAiManager.CanControl(active,movable,owner,faction,false,false));}
        [Fact] public void LandingOrManualControl_PreemptsAi()
        {
            Assert.False(SpaceAiManager.CanControl(true,true,2,2,true,false));
            Assert.False(SpaceAiManager.CanControl(true,true,2,2,false,true));
        }
        [Fact] public void Metrics_ReportIncludesHeavyPlanningSamples()
        {
            var metrics=new AiMetrics();metrics.Record(1,20);metrics.Record(2,30);metrics.Record(9,40);
            var report=metrics.Report();Assert.Equal(3,report.Samples);Assert.Equal(4,report.MeanMilliseconds);Assert.Equal(9,report.P95Milliseconds);Assert.Equal(90,report.AllocatedBytes);
        }
    }
}
