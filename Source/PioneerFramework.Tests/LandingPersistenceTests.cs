using System;
using System.IO;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PioneerFramework.Landing;
using Verse;
using Xunit;
namespace PioneerFramework.Tests;
public class LandingPersistenceTests
{
    private static bool ThrowScribeError(string text) => throw new InvalidOperationException(text);
    private static bool ResolveModType(string typeName, ref Type __result)
    {
        Type found = typeof(LandingTaskData).Assembly.GetType(typeName);
        if (found == null) return true;
        __result = found; return false;
    }
    [Theory]
    [InlineData(LandingTransitStatus.InFlight)] [InlineData(LandingTransitStatus.AwaitingPlacement)]
    [InlineData(LandingTransitStatus.Completing)] [InlineData(LandingTransitStatus.Blocked)]
    [InlineData(LandingTransitStatus.PreparingDestination)] [InlineData(LandingTransitStatus.Transferring)]
    [InlineData(LandingTransitStatus.FailedRecoverable)]
    public void TaskScribeRoundTripRetainsStableOperationAndFuel(LandingTransitStatus status)
    {
        var data = new LandingTaskData { TaskId = "saved", GroupId = 42, Status = status, DurationTicks = 180 };
        data.SourceMaps.Add(new LandingSourceMapData { SourceKey = "map-key", MapId = 84, Status = LandingMapStatus.Transferred });
        data.FuelReceipt.Committed = true; data.FuelReceipt.ConsumedKg = 12.5;
        string path = Path.Combine(Path.GetTempPath(), "pfl-landing-" + Guid.NewGuid() + ".xml");
        var prefs = AccessTools.Field(typeof(Prefs), "data"); object previous = prefs.GetValue(null);
        prefs.SetValue(null, RuntimeHelpers.GetUninitializedObject(prefs.FieldType));
        var diagnostic = new Harmony("PFL.Landing.ScribeDiagnostic");
        diagnostic.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(LandingPersistenceTests), nameof(ThrowScribeError)));
        // The game registers active mod assemblies. Bootstrap that lookup in the standalone host.
        diagnostic.Patch(AccessTools.Method(typeof(GenTypes), nameof(GenTypes.GetTypeInAnyAssembly), new[] { typeof(string), typeof(string) }), prefix: new HarmonyMethod(typeof(LandingPersistenceTests), nameof(ResolveModType)));
        try
        {
            Scribe.saver.InitSaving(path, "landing-test"); Scribe_Deep.Look(ref data, "task"); Scribe.saver.FinalizeSaving();
            LandingTaskData restored = null; Scribe.loader.InitLoading(path);
            Scribe_Deep.Look(ref restored, "task"); Scribe.loader.FinalizeLoading();
            Assert.Equal(status, restored.Status); Assert.True(restored.FuelReceipt.Committed);
            Assert.Equal(12.5, restored.FuelReceipt.ConsumedKg); Assert.Equal("landing:saved:map-key", restored.SourceMaps[0].OperationId(restored.TaskId));
            Assert.Equal(LandingMapStatus.Transferred, restored.SourceMaps[0].Status);
        }
        finally { diagnostic.UnpatchAll(diagnostic.Id); Scribe.ForceStop(); prefs.SetValue(null, previous); if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public void RecoveryPayloadScribePreservesEntityAndDeliveryState()
    {
        var original = new LandingRecoveryEntry
        {
            EntryId = "entry-11", EntityId = 11, TransferredFromSpace = true, State = LandingHandoffState.HandingOff, ReceiverCaravanId = 17,
            Payload = new LandingEntityPayload { EntryId = "entry-11", EntityId = 11, Entity = new SpaceEntity(11), DryMass = 100, FuelMass = 20 }
        };
        var restored = RoundTrip(original);
        Assert.Equal(17, restored.ReceiverCaravanId); Assert.Equal(LandingHandoffState.HandingOff, restored.State);
        Assert.Equal(11, restored.Payload.Entity.EntityId); Assert.Equal(120, restored.Payload.Mass);
        restored.HandOff(_ => true, _ => throw new InvalidOperationException("Duplicate delivery"));
        var delivered = RoundTrip(restored);
        Assert.Equal(LandingHandoffState.Delivered, delivered.State); Assert.Null(delivered.Payload);
    }
    private static T RoundTrip<T>(T data) where T : IExposable
    {
        string path = Path.Combine(Path.GetTempPath(), "pfl-payload-" + Guid.NewGuid() + ".xml");
        var prefs = AccessTools.Field(typeof(Prefs), "data"); object previous = prefs.GetValue(null);
        prefs.SetValue(null, RuntimeHelpers.GetUninitializedObject(prefs.FieldType));
        var bootstrap = new Harmony("PFL.Landing.PayloadScribe");
        bootstrap.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(LandingPersistenceTests), nameof(ThrowScribeError)));
        bootstrap.Patch(AccessTools.Method(typeof(GenTypes), nameof(GenTypes.GetTypeInAnyAssembly), new[] { typeof(string), typeof(string) }), prefix: new HarmonyMethod(typeof(LandingPersistenceTests), nameof(ResolveModType)));
        try
        {
            Scribe.saver.InitSaving(path, "payload-test"); Scribe_Deep.Look(ref data, "data"); Scribe.saver.FinalizeSaving();
            T restored = default; Scribe.loader.InitLoading(path); Scribe_Deep.Look(ref restored, "data"); Scribe.loader.FinalizeLoading(); return restored;
        }
        finally { bootstrap.UnpatchAll(bootstrap.Id); Scribe.ForceStop(); prefs.SetValue(null, previous); if (File.Exists(path)) File.Delete(path); }
    }
}
