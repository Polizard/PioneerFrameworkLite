using PioneerFramework.SpaceAI;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class SpaceAiCoreTests
{
    private static AiShipState Ship(double dv=1000, double ammo=1, double health=1) =>
        new(1, DVector3.zero, DVector3.zero, 10000, 10, dv, 100, health, ammo, AiRole.Gunship, true, true, true);
    private static AiContact Track(int key=1, bool hostile=true, bool usable=true, bool decoy=false,
        double x=5000, double sigma=1) => new(key, new DVector3(x,0,0), new DVector3(10,0,0),
            0, sigma, 10, usable, hostile, false, decoy, false);

    [Fact] public void NoContact_SearchesWithoutFiring()
    { var d=AiDecisionCore.Decide(Ship(), Array.Empty<AiContact>(), 0); Assert.Equal(AiAction.Search,d.Action); Assert.False(d.Fire); Assert.True(d.ActiveRadar); }
    [Fact] public void UnknownContact_IsInvestigatedBeforeAttack()
    { var d=AiDecisionCore.Decide(Ship(),new[]{Track(hostile:false)},0); Assert.Equal(AiAction.Investigate,d.Action);Assert.False(d.Fire); }
    [Fact] public void ExpiredContact_CannotBeTrackedOrFiredAt()
    { var d=AiDecisionCore.Decide(Ship(),new[]{Track(usable:false)},0);Assert.Equal(0,d.TrackId);Assert.False(d.Fire); }
    [Fact] public void RecognizedDecoy_IsRejected()
    { var d=AiDecisionCore.Decide(Ship(),new[]{Track(decoy:true)},0);Assert.False(d.Fire);Assert.Equal(0,d.TrackId); }
    [Fact] public void HostileInRange_IsEngaged()
    { var d=AiDecisionCore.Decide(Ship(),new[]{Track()},0);Assert.Equal(AiAction.Engage,d.Action);Assert.True(d.Fire); }
    [Fact] public void OutOfRange_ApproachesWithoutFiring()
    { var d=AiDecisionCore.Decide(Ship(),new[]{Track(x:20000)},0);Assert.Equal(AiAction.Approach,d.Action);Assert.False(d.Fire); }
    [Theory] [InlineData(99,1,1)] [InlineData(1000,0,1)] [InlineData(1000,1,.2)]
    public void ResourceOrDamageEmergency_Retreats(double dv,double ammo,double health)
    {var d=AiDecisionCore.Decide(Ship(dv,ammo,health),new[]{Track()},0);Assert.Equal(AiAction.Retreat,d.Action);Assert.False(d.Fire);}
    [Fact] public void Ties_SelectStableTrackKey()
    { var a=AiDecisionCore.Decide(Ship(),new[]{Track(3),Track(2)},0);var b=AiDecisionCore.Decide(Ship(),new[]{Track(2),Track(3)},0);Assert.Equal(2,a.TrackId);Assert.Equal(a,b); }
    [Fact] public void ShadowContact_UsesCoastedPosition()
    { var c=Track();Assert.Equal(new DVector3(5100,0,0),c.PositionAt(10)); }
    [Fact] public void Uncertainty_CanPreventWastefulFire()
    { var d=AiDecisionCore.Decide(Ship(),new[]{Track(sigma:100000)},0);Assert.False(d.Fire);Assert.True(d.ActiveRadar); }
    [Fact] public void InvalidNumbers_CannotGenerateCommands()
    {var d=AiDecisionCore.Decide(Ship(),new[]{Track(x:double.NaN)},0);Assert.False(d.Fire);Assert.Equal(0,d.TrackId);}
    [Fact] public void Schedule_DoesNotReplayMissedPeriods()
    { double due=1;Assert.True(AiSchedule.IsDue(100,ref due,5));Assert.Equal(105,due);Assert.False(AiSchedule.IsDue(100,ref due,5)); }
    [Fact] public void Schedule_SameInstantIsNotRepeated()
    {double due=0;Assert.True(AiSchedule.IsDue(0,ref due,1));Assert.False(AiSchedule.IsDue(0,ref due,1));}
    [Fact] public void ApproachPlan_ReservesFuelAndIsFinite()
    {var dv=AiPlanningCore.Plan(Ship(),Track(x:20000),AiAction.Approach,0);Assert.True(dv.magnitude<=900);Assert.True(dv.x>0);}
    [Fact] public void RetreatPlan_MovesAwayFromKnownThreat()
    {var dv=AiPlanningCore.Plan(Ship(),Track(),AiAction.Retreat,0);Assert.True(dv.x<0);}
    [Fact] public void NoThrust_ProducesNoManeuver()
    {var s=new AiShipState(1,DVector3.zero,DVector3.zero,10000,0,1000,100,1,1,AiRole.Gunship,true,false,false);Assert.Equal(DVector3.zero,AiPlanningCore.Plan(s,Track(),AiAction.Approach,0));}
    [Fact] public void FleetAllocation_AvoidsOvercommittingToSameContact()
    {
        var fleet=new AiFleetCoordinator();
        var tracks=new[]{Track(1),Track(2)};
        int first=fleet.SelectTarget(Ship(),tracks,0);
        fleet.Reserve(first,1,10);
        int second=fleet.SelectTarget(Ship(),tracks,0);
        Assert.NotEqual(first,second);
        fleet.Expire(11);Assert.Equal(first,fleet.SelectTarget(Ship(),tracks,11));
    }
    [Fact] public void OrbitalSearch_IsIncrementalAndBounded()
    {
        var job=new AiOrbitPlanningJob(new DVector3(6000000,0,0),new DVector3(0,0,7000),
            new DVector3(6100000,0,100000),new DVector3(0,0,6950),3.986e14,5500000,2000);
        Assert.False(job.Completed);int steps=0;
        while(!job.Completed){job.Step();Assert.True(++steps<=8);}
        Assert.Equal(8,steps);
    }
    [Fact] public void FleetSalvo_SharedWindowCoalescesMissedPeriods()
    {
        var fleet = new AiFleetCoordinator();
        Assert.True(fleet.MissileWindowOpen(1));
        Assert.True(fleet.MissileWindowOpen(1.5));
        Assert.False(fleet.MissileWindowOpen(4));
        Assert.True(fleet.MissileWindowOpen(100));
        Assert.False(fleet.MissileWindowOpen(103));
    }
    [Fact] public void ActualInFlightCommitment_IsRebuiltAndStopsReservingAfterMissileLoss()
    {
        var fleet = new AiFleetCoordinator(); var contacts = new[] { Track(1), Track(2) };
        fleet.SetInFlight(1, 4);
        Assert.Equal(2, fleet.SelectTarget(Ship(), contacts, 50));
        fleet.ClearInFlight();
        Assert.Equal(1, fleet.SelectTarget(Ship(), contacts, 51));
    }
    [Fact] public void FleetWithdrawal_HealthySupportCoversLastForBoundedTime()
    {
        var fleet = new AiFleetCoordinator();
        var members = new[] {
            new AiFleetMember(1, AiRole.Gunship, true, false),
            new AiFleetMember(2, AiRole.Missile, true, false),
            new AiFleetMember(3, AiRole.ElectronicWarfare, false, true) };
        Assert.True(fleet.WithdrawalOrder(1, members, 10).Withdraw);
        var cover = fleet.WithdrawalOrder(3, members, 10);
        Assert.True(cover.Cover); Assert.False(cover.Withdraw);
        Assert.True(fleet.WithdrawalOrder(3, members, 25).Withdraw);
    }
    [Fact] public void DecoyBudget_ConsumesOneChargeAndRequiresCooldown()
    {
        var budget=new DecoyBudget(2);
        Assert.True(budget.TrySpend(0,10));Assert.Equal(1,budget.Charges);
        Assert.False(budget.TrySpend(9,10));Assert.Equal(1,budget.Charges);
        Assert.True(budget.TrySpend(10,10));Assert.Equal(0,budget.Charges);
        Assert.False(budget.TrySpend(20,10));
    }
    [Fact] public void DisarmedCombatShip_Withdraws()
    {
        var ship=Ship() with {HasWeapons=false};
        Assert.Equal(AiAction.Retreat,AiDecisionCore.Decide(ship,new[]{Track()},0).Action);
    }
    [Fact] public void Shortlist_IsBoundedAndRetainsImmediateThreat()
    {
        var input=new List<AiContact>();for(int i=1;i<=20;i++) input.Add(Track(i,x:5000+i));
        input.Add(Track(30) with {Threatening=true});
        var output=new List<AiContact>();AiCandidateShortlist.Fill(Ship(),input,0,output);
        Assert.Equal(4,output.Count);Assert.Equal(30,output[0].TrackId);
    }
    [Fact] public void OrbitalPlanning_SafeCircularContactHasFeasibleSolution()
    {
        var target=new DVector3(6100000,0,100000);
        var speed=new DVector3(-100000,0,6100000).normalized*Math.Sqrt(3.986e14/target.magnitude);
        var job=new AiOrbitPlanningJob(new DVector3(6000000,0,0),new DVector3(0,0,Math.Sqrt(3.986e14/6000000)),target,speed,3.986e14,5500000,2000);
        while(!job.Completed)job.Step();Assert.True(job.HasSolution);Assert.True(job.DeltaV.magnitude<2000);
    }
    [Fact] public void SpawnBesideHoveringTarget_MustReceiveSafeFreeFlightOrbit()
    {
        double mu = 2e14, r = 5500000, safety = 5100000;
        var position = new DVector3(r,0,0); var velocity = new DVector3(0,0,576);
        Assert.True(AiSpawnOrbitCore.TryCreate(position,velocity,30000,0,mu,safety,out var spawned,out var speed));
        Assert.True(OrbitMath.IsOrbitSafe(spawned,speed,safety,mu));
        Assert.True(speed.magnitude > 5000);
    }
    [Fact] public void SpawnInsideAtmosphere_IsRejectedBeforeCreatingShipOrMap()
    {
        Assert.False(AiSpawnOrbitCore.TryCreate(new DVector3(5000000,0,0),DVector3.zero,30000,0,
            2e14,5100000,out _,out _));
    }
    [Fact] public void IncomingEscapeSpeed_IsNotTreatedAsSafeWhenItIntersectsPlanet()
    {
        Assert.True(AiSpawnOrbitCore.TryCreate(new DVector3(5500000,0,0),new DVector3(-10000,0,0),
            30000,0,2e14,5100000,out var position,out var velocity));
        Assert.True(DVector3.Cross(position,velocity).magnitude > 1);
        Assert.True(System.Math.Abs(DVector3.Dot(position.normalized,velocity)) < 1);
    }
}
