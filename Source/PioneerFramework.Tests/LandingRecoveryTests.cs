using System;
using System.Collections.Generic;
using PioneerFramework.Landing;
using Xunit;
namespace PioneerFramework.Tests;
public class LandingRecoveryTests
{
    [Fact]
    public void ReceiverAcceptsThenThrowsReconcileDeliversOnce()
    {
        var entry = new LandingRecoveryEntry { EntryId = "entry", Payload = new LandingEntityPayload { DryMass = 100, FuelMass = 20 } };
        bool received = false; int attempts = 0;
        Assert.Throws<InvalidOperationException>(() => entry.HandOff(_ => received, _ => { attempts++; received = true; throw new InvalidOperationException("after insert"); }));
        Assert.True(entry.HandOff(_ => received, _ => attempts++));
        Assert.Equal(1, attempts); Assert.Equal(LandingHandoffState.Delivered, entry.State); Assert.Null(entry.Payload);
    }
    [Fact]
    public void CargoMassCountsDryMassAndFuelOnce()
    { Assert.Equal(120, new LandingEntityPayload { DryMass = 100, FuelMass = 20 }.Mass); }

    [Fact]
    public void PendingHandoffChecksOriginalReceiverBeforeAnotherCaravanCanClaim()
    {
        var entry = new LandingRecoveryEntry { EntryId = "entry", ReceiverCaravanId = 17,
            State = LandingHandoffState.HandingOff, Payload = new LandingEntityPayload { DryMass = 100 } };
        Assert.True(LandingRecoveryService.Reconcile(new[] { entry }, (caravanId, id) => caravanId == 17 && id == "entry").Success);
        int copies = 0;
        Assert.True(entry.HandOff(_ => false, _ => copies++));
        Assert.Equal(0, copies); Assert.Null(entry.Payload);
        Assert.Equal(LandingHandoffState.Delivered, entry.State);
    }

    [Fact]
    public void MissingOriginalReceiverKeepsHandoffPayloadBlocked()
    {
        var payload = new LandingEntityPayload { DryMass = 100 };
        var entry = new LandingRecoveryEntry { EntryId = "entry", ReceiverCaravanId = 17,
            State = LandingHandoffState.HandingOff, Payload = payload };
        Assert.False(LandingRecoveryService.Reconcile(new[] { entry }, (_, _) => null).Success);
        Assert.Same(payload, entry.Payload); Assert.Equal(LandingHandoffState.HandingOff, entry.State);
    }

    [Fact]
    public void ConfirmedFailedInsertAllowsRemainingPayloadToBeRecovered()
    {
        var entry = new LandingRecoveryEntry { EntryId = "entry", ReceiverCaravanId = 17,
            State = LandingHandoffState.HandingOff, Payload = new LandingEntityPayload { DryMass = 100 } };
        Assert.True(LandingRecoveryService.Reconcile(new[] { entry }, (_, _) => false).Success);
        Assert.Equal(LandingHandoffState.Available, entry.State);
        Assert.NotNull(entry.Payload); Assert.Equal(-1, entry.ReceiverCaravanId);
    }
    [Fact]
    public void RecoveryConfigurationUsesMostSpecificThenStableNameAndGenericFallback()
    {
        var payload = new LandingEntityPayload { Entity = new SpaceEntity(7) };
        payload.Entity.SetParts(new List<SpacePart> { new SpacePart { defName = "engine" }, new SpacePart { defName = "hull" } }, false);
        var generic = new LandingRecoveryDef { defName = "PF_GenericLandingRecovery" };
        var engine = new LandingRecoveryDef { defName = "engine", partDefNames = new List<string> { "engine" } };
        var first = new LandingRecoveryDef { defName = "a", partDefNames = new List<string> { "engine", "hull" } };
        var last = new LandingRecoveryDef { defName = "z", partDefNames = new List<string> { "engine", "hull" } };
        Assert.Same(first, LandingRecoveryService.SelectConfiguration(payload, new[] { generic, engine, last, first }));
        payload.Entity.SetParts(new List<SpacePart>(), false);
        Assert.Same(generic, LandingRecoveryService.SelectConfiguration(payload, new[] { generic, engine, last, first }));
    }
}
