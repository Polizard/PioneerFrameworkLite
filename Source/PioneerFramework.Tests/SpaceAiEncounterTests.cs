using System;
using System.Collections.Generic;
using PioneerFramework.SpaceAI;
using Xunit;

namespace PioneerFramework.Tests
{
    public class SpaceAiEncounterTests
    {
        [Fact] public void InvalidShipTemplate_IsRejectedBeforeMapGeneration()
        {
            var template=new SpaceShipTemplateDef {width=2,height=40,dryMass=1000};
            Assert.False(template.TryValidate(out _));
        }
        [Fact] public void EncounterCount_StaysWithinConfiguredRange()
        {
            var def=new SpaceEncounterDef {minShips=3,maxShips=6};
            Assert.Equal(3,def.ClampCount(1));Assert.Equal(6,def.ClampCount(99));
        }
        [Fact] public void FailedGeneration_RollsBackEveryCreatedShipInReverseOrder()
        {
            var removed=new List<int>();var transaction=new EncounterBuildTransaction(removed.Add);
            transaction.Add(10);transaction.Add(11);transaction.Dispose();
            Assert.Equal(new[]{11,10},removed);
        }
        [Fact] public void CommittedGeneration_IsPreserved()
        {
            var transaction=new EncounterBuildTransaction(_=>throw new Exception("Unexpected cleanup"));
            transaction.Add(1);transaction.Commit();transaction.Dispose();
        }
        [Fact] public void FailureBeforeEntityRegistration_RemovesBothActualSpatialIndexes()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var group = host.AddShip();
            Assert.Contains(group, host.Space.GetGroupsInRange(DVector3.zero, 100000));
            var fine = (System.Collections.IDictionary)HarmonyLib.AccessTools.Field(typeof(PioneerFramework.Combat.CombatGrid),
                "shipCoveredCells").GetValue(host.Grid);
            Assert.True(fine.Contains(group));
            EncounterShipFactory.RollbackCreation(host.Space, group, null, false);
            Assert.Null(host.Space.GetGroupById(group.GroupId));
            Assert.DoesNotContain(group, host.Space.GetGroupsInRange(DVector3.zero, 100000));
            Assert.False(fine.Contains(group));
        }
        [Fact] public void MissingContact_DoesNotEndEncounterOrDeleteShip()
        {
            Assert.False(SpaceEncounterService.CanFinishWithdrawal(100,90,10000,20000));
            Assert.False(SpaceEncounterService.CanFinishWithdrawal(100,0,30000,20000));
            Assert.True(SpaceEncounterService.CanFinishWithdrawal(200,0,30000,20000));
        }
        [Fact] public void HealthyLivingObjective_LongLossAbandonsSearchAndAllowsSafeCompletion()
        {
            var entry = new SpaceEncounterRecord { status = SpaceEncounterStatus.Active };
            Assert.False(entry.UpdateSearch(100, false, false, 180));
            Assert.False(entry.UpdateSearch(279, false, false, 180));
            Assert.True(entry.UpdateSearch(280, false, false, 180));
            Assert.True(entry.searchAbandoned);
            Assert.False(SpaceEncounterService.CanFinishWithdrawal(399,280,0,150000,true));
            Assert.True(SpaceEncounterService.CanFinishWithdrawal(400,280,0,150000,true));
            Assert.Equal(SpaceEncounterStatus.Active, entry.status);
        }
        [Fact] public void ReacquisitionOrOtherEngagement_RestartsLostContactTimer()
        {
            var entry = new SpaceEncounterRecord();
            Assert.False(entry.UpdateSearch(0, false, false, 180));
            Assert.False(entry.UpdateSearch(170, true, false, 180));
            Assert.False(entry.UpdateSearch(200, false, false, 180));
            Assert.False(entry.UpdateSearch(370, false, true, 180));
            Assert.False(entry.UpdateSearch(400, false, false, 180));
            Assert.False(entry.UpdateSearch(579, false, false, 180));
            Assert.True(entry.UpdateSearch(580, false, false, 180));
        }
    }
}
