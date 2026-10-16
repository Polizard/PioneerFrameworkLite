using System;
using PioneerFramework;
using PioneerFramework.Landing;
using Xunit;
using System.Collections.Generic;
using HarmonyLib;
using PioneerFramework.Components;
using Verse;
namespace PioneerFramework.Tests;
public class LandingFuelTransactionTests : IDisposable
{
    private readonly Harmony logging = new Harmony("PFL.Tests.LandingFuelLogging");
    public LandingFuelTransactionTests()
    {
        // The standalone host has no native game logger. Keep the fuel logic and balances real.
        logging.Patch(AccessTools.Method(typeof(Log), nameof(Log.Message), new[] { typeof(string) }),
            prefix: new HarmonyMethod(typeof(LandingFuelTransactionTests), nameof(SkipGameLog)));
    }
    private static bool SkipGameLog() => false;
    public void Dispose() => logging.UnpatchAll(logging.Id);
    [Fact]
    public void MutationThenThrowRunsRollback()
    {
        double fuel = 100;
        Assert.False(LandingFuelTransaction.TryRun(() => { fuel -= 20; throw new InvalidOperationException("debit failed"); }, () => fuel = 100, out var error));
        Assert.Equal(100, fuel); Assert.Contains("debit failed", error);
    }
    [Fact]
    public void FailedRollbackIsReported()
    {
        Assert.False(LandingFuelTransaction.TryRun(() => throw new Exception("first"), () => throw new Exception("restore"), out var error));
        Assert.Contains("first", error); Assert.Contains("restore", error);
    }
    [Fact]
    public void SuspendRetainsOriginalFlagsAndRejectsSecondTask()
    {
        var group = new SpaceEntityGroup(1) { IsActive = true, IsMovable = true, IsCollidable = false };
        var saved = LandingGroupLifecycle.SuspendState(group, "first");
        Assert.True(saved.IsActive); Assert.True(saved.IsMovable); Assert.False(saved.IsCollidable);
        Assert.False(group.IsActive); Assert.False(group.IsMovable);
        Assert.Throws<InvalidOperationException>(() => LandingGroupLifecycle.SuspendState(group, "second"));
    }
    [Fact]
    public void CapturedAggregateRestoresOriginalUnequalEntries()
    {
        var sg = new SpaceGameComp(null); var group = new SpaceEntityGroup(7);
        var def = new PropellantDef { defName = "test-fuel" };
        var entity = new SpaceEntity(11);
        entity.propulsion.Add(new PropulsionData { propellant = def, mass = 80, capacity = 100 });
        entity.propulsion.Add(new PropulsionData { propellant = def, mass = 25, capacity = 100 });
        entity.InvalidatePropulsion(); group.GetEntityIds().Add(11);
        ((Dictionary<int, SpaceEntity>)AccessTools.Field(typeof(SpaceGameComp), "entities").GetValue(sg)).Add(11, entity);
        var receipt = LandingFuelTransaction.Capture(sg, group);
        entity.TryConsumePropellant(def, 90);
        Assert.Equal(15, entity.TotalPropellantMass);
        Assert.True(LandingFuelTransaction.Rollback(receipt, sg).Success);
        Assert.Equal(80, entity.propulsion[0].mass); Assert.Equal(25, entity.propulsion[1].mass);
        Assert.Equal(105, entity.TotalPropellantMass);
    }

    [Theory]
    [InlineData(0, 1997.552656061878)]
    [InlineData(25, 2000.490233497263)]
    [InlineData(500, 2056.304204769580)]
    public void FeasibleMainEngineLandingDoesNotRequireReserveRcsFuel(double rcsFuel, double required)
    {
        var (sg, group, entity, main, rcs) = LandingShip(12000, rcsFuel);
        var source = sg.GetGroupPropulsionSource(group);
        var quote = BurnCalculator.Estimate(500, 5000, source, source.Networks);
        Assert.True(quote.Feasible); Assert.True(quote.Networks[1].IsReserve);
        double requiredBefore = sg.LandingRequiredFuel(group, 500);
        Assert.True(LandingFuelTransaction.TryDebit(sg, group, 500, out var receipt, out var report), report.ReasonKey + ": " + string.Join("; ", report.Arguments));
        Assert.Equal(required, requiredBefore, 2);
        Assert.Equal(quote.RequiredPropellant, receipt.ConsumedKg, 2);
        Assert.Equal(required, receipt.ConsumedKg, 2);
        Assert.Equal(12000 - required, entity.GetPropellantMass(main), 2);
        Assert.Equal(rcsFuel, entity.GetPropellantMass(rcs));
    }

    [Fact]
    public void LandingUsesQuotedRcsRelayAfterMainFuelIsSpent()
    {
        var (sg, group, entity, main, rcs) = LandingShip(5373, 500);
        var source = sg.GetGroupPropulsionSource(group);
        var quote = BurnCalculator.Estimate(2733.7f, 5000, source, source.Networks);
        Assert.True(quote.Feasible); Assert.False(quote.Networks[1].IsReserve);
        Assert.Equal(5400.6567, sg.LandingRequiredFuel(group, 2733.7), 2);
        Assert.True(LandingFuelTransaction.TryDebit(sg, group, 2733.7, out var receipt, out var report), report.ReasonKey + ": " + string.Join("; ", report.Arguments));
        Assert.Equal(0, entity.GetPropellantMass(main));
        Assert.Equal(5373 + quote.Networks[1].RequiredPropellant, receipt.ConsumedKg, 2);
        Assert.Equal(500 - quote.Networks[1].RequiredPropellant, entity.GetPropellantMass(rcs), 2);
    }

    [Fact]
    public void EmptyMainTankCanLandUsingQuotedRcsFallback()
    {
        var (sg, group, entity, main, rcs) = LandingShip(0, 500);
        var source = sg.GetGroupPropulsionSource(group);
        Assert.True(BurnCalculator.Estimate(20, 5000, source, source.Networks).Feasible);
        Assert.Equal(72.846610, sg.LandingRequiredFuel(group, 20), 2);
        Assert.True(LandingFuelTransaction.TryDebit(sg, group, 20, out var receipt, out var report), report.ReasonKey + ": " + string.Join("; ", report.Arguments));
        Assert.Equal(72.846636, receipt.ConsumedKg, 2);
        Assert.Equal(0, entity.GetPropellantMass(main));
        Assert.Equal(500 - receipt.ConsumedKg, entity.GetPropellantMass(rcs), 2);
    }

    private static (SpaceGameComp sg, SpaceEntityGroup group, SpaceEntity entity, PropellantDef main, PropellantDef rcs) LandingShip(double mainFuel, double rcsFuel)
    {
        var sg = new SpaceGameComp(null); var group = new SpaceEntityGroup(7);
        var main = new PropellantDef { defName = "landing-test-main" };
        var rcs = new PropellantDef { defName = "landing-test-rcs" };
        var part = new SpacePart(); part.components.Add(new RoomComponentData { dryMass = 5000 });
        var entity = new SpaceEntity(11); entity.SetParts(new List<SpacePart> { part }, false);
        entity.AddPropulsion(new PropulsionData { propellant = main, ve = 4000, massFlow = 4, mass = mainFuel, capacity = 12000 });
        entity.AddPropulsion(new PropulsionData { propellant = rcs, ve = 1500, massFlow = 1, mass = rcsFuel, capacity = 500, isVectoredRcs = true });
        group.GetEntityIds().Add(11);
        ((Dictionary<int, SpaceEntity>)AccessTools.Field(typeof(SpaceGameComp), "entities").GetValue(sg)).Add(11, entity);
        return (sg, group, entity, main, rcs);
    }
}
