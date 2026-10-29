using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialAuditRestoreTests
{
    private static int nativeSaveCalls;
    [Fact]
    public void FailedRestoreIsRejectedBeforeNativeSaveCanTouchAnyDestination()
    {
        using var fixture = new CelestialMapIdentityTests.Fixture();
        string path = TempPath();
        var patch = new Harmony("PFL.Tests.AuditNativeSaveGuard");
        try
        {
            Scribe.saver.InitSaving(path, "manager"); fixture.Native.Game.GetComponent<CelestialBodyManager>().ExposeData(); Scribe.saver.FinalizeSaving();
            var loaded = new CelestialBodyManager(fixture.Native.Game);
            Scribe.loader.InitLoading(path); loaded.ExposeData(); Scribe.loader.FinalizeLoading();
            fixture.Native.Game.components.RemoveAll(component => component is CelestialBodyManager);
            fixture.Native.Game.components.Add(loaded);
            var hook = typeof(CelestialRuntime).Assembly.GetType("PioneerFramework.Celestial.CelestialSaveGuard");
            if (hook != null)
                patch.Patch(AccessTools.Method(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame)),
                    prefix: new HarmonyMethod(AccessTools.Method(hook, "BeforeSave")));
            patch.Patch(AccessTools.Method(typeof(SafeSaver), nameof(SafeSaver.Save)),
                prefix: new HarmonyMethod(typeof(CelestialAuditRestoreTests), nameof(StopNativeSave)));
            patch.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.GameInfo)),
                prefix: new HarmonyMethod(typeof(CelestialAuditRestoreTests), nameof(ProvideSaveMetadata)));
            nativeSaveCalls = 0;
            try { GameDataSaveLoader.SaveGame("PFL_AUDIT_GUARD_NO_OUTPUT"); }
            catch (InvalidOperationException exception) when (exception.Message.Contains("SaveGuardSentinel", StringComparison.Ordinal)) { }
            Assert.Equal(0, nativeSaveCalls);
        }
        finally { patch.UnpatchAll(patch.Id); Scribe.ForceStop(); if (File.Exists(path)) File.Delete(path); }
    }
    private static bool StopNativeSave()
    { nativeSaveCalls++; throw new InvalidOperationException("SaveGuardSentinel: native save must not run during failed restoration."); }
    private static bool ProvideSaveMetadata(ref GameInfo __result)
    { __result = (GameInfo)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GameInfo)); return false; }
    [Fact]
    public void LoadedAdapterCannotWriteItsOldDocumentAsANewSave()
    {
        using var fixture = new CelestialMapIdentityTests.Fixture();
        string path = TempPath();
        var loaded = new CelestialSaveAdapter();
        try
        {
            Scribe.saver.InitSaving(path, "state");
            new CelestialSaveAdapter(fixture.Scope.Runtime).ExposeData();
            Scribe.saver.FinalizeSaving();
            Scribe.loader.InitLoading(path); loaded.ExposeData(); Scribe.loader.FinalizeLoading();
            Assert.NotNull(loaded.Document);
            Scribe.saver.InitSaving(path, "state");
            Assert.Contains("RuntimeUnavailable", Assert.Throws<InvalidOperationException>(loaded.ExposeData).Message);
        }
        finally { Scribe.ForceStop(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void PendingRestoreCannotBeSavedButOriginalDiagnosticPayloadRemainsAvailable()
    {
        using var fixture = new CelestialMapIdentityTests.Fixture();
        var document = new CelestialSaveAdapter(fixture.Scope.Runtime).CaptureCheckpoint().Value;
        var restore = new CelestialRestoreCoordinator(Guid.NewGuid(), null, CelestialBuiltinCompRegistry.Create(), _ => throw new Exception("No XML"));
        Assert.True(restore.RestoreIdentities(document).Success);
        string path = TempPath();
        try
        {
            Scribe.saver.InitSaving(path, "state");
            Assert.Contains("RestorePending", Assert.Throws<InvalidOperationException>(() => new CelestialSaveAdapter(restore.Runtime).ExposeData()).Message);
            Assert.Equal(document.PhysicsCheckpoint.Payload.ToArray(), restore.Runtime.RestoreDocument.PhysicsCheckpoint.Payload.ToArray());
        }
        finally { Scribe.ForceStop(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ManagerReportsFailedRestoreThenExplicitRetryCanReconnectWithoutChangingBodyIdentity()
    {
        using var diagnostics = new CelestialExpectedRestoreErrorScope();
        using var fixture = new CelestialMapIdentityTests.Fixture();
        var manager = new CelestialBodyManager(fixture.Native.Game);
        var original = fixture.Scope.Runtime.CaptureCatalog().Bodies.Select(body => body.Id).ToArray();
        string path = TempPath();
        var patch = new Harmony("PFL.Tests.AuditRestoreReconnect");
        try
        {
            Scribe.saver.InitSaving(path, "manager"); fixture.Native.Game.GetComponent<CelestialBodyManager>().ExposeData(); Scribe.saver.FinalizeSaving();
            Scribe.loader.InitLoading(path); manager.ExposeData(); Scribe.loader.FinalizeLoading();
            manager.LoadedGame();
            manager.LoadedGame();
            Assert.Single(diagnostics.Messages);
            Assert.Contains(manager.CelestialRestoreErrors, error => error.Code == "ProviderUnavailable");
            var retry = typeof(CelestialBodyManager).GetMethod("RetryCelestialRestore");
            Assert.NotNull(retry);
            patch.Patch(AccessTools.Method(typeof(CelestialPhysicsProviderRegistry), nameof(CelestialPhysicsProviderRegistry.Connect)),
                prefix: new HarmonyMethod(typeof(CelestialAuditRestoreTests), nameof(ConnectReplay)));
            var recovered = Assert.IsType<CelestialResult<bool>>(retry.Invoke(manager, null));
            Assert.True(recovered.Success, string.Join(";", recovered.Errors.Select(error => error.Code + ":" + error.Detail)));
            Assert.Empty(manager.CelestialRestoreErrors);
            Assert.True(manager.TryGetCelestialRuntime(out var runtime));
            Assert.False(runtime.IsRestoring);
            Assert.Equal(original, runtime.CaptureCatalog().Bodies.Select(body => body.Id));
            Assert.True(Assert.IsType<CelestialResult<bool>>(retry.Invoke(manager, null)).Success);
            Assert.Single(fixture.Native.Grid.PlanetLayers);
        }
        finally { patch.UnpatchAll(patch.Id); Scribe.ForceStop(); if (File.Exists(path)) File.Delete(path); }
    }
    private static bool ConnectReplay(string providerId, CelestialPhysicsWorldRequest request, ref CelestialResult<CelestialPhysicsConnection> __result)
    {
        if (providerId != DeterministicTestPhysicsBridge.Id) return true;
        var bridge = new DeterministicTestPhysicsBridge(request.WorldSessionId, request.InitialSeconds);
        __result = CelestialResult.Ok(new CelestialPhysicsConnection(bridge, bridge.CommittedStamp));
        return false;
    }
    private static string TempPath() => Path.Combine(Path.GetTempPath(), "PFL-audit-restore-" + Guid.NewGuid().ToString("N") + ".xml");
}
