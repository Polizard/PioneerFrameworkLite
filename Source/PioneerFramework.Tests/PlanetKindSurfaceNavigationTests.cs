using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PioneerFramework.Celestial;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class PlanetKindSurfaceNavigationTests
{
    private static readonly WindowStack fakeWindows = new();
    private static FloatMenu capturedMenu;
    private static PlanetLayer selectedLayer;

    [Fact]
    public void RetainedWorldViewCommandRefreshesChoicesWhenClickedAfterSurfaceCreation()
    {
        using var fixture = new Fixture();
        fixture.CaptureMenus();
        var getter = AccessTools.Method(typeof(CelestialBodyLayerManager), "GetOrBuildCelestialViewCommand");
        var command = (Command_Action)getter.Invoke(null, null);
        var body = fixture.Create("PF_BarrenPlanet");
        var context = fixture.Session.Context().Value;
        var surface = fixture.Harness.Runtime.RequestSurface(new(context, body.Id));
        Assert.Equal(CelestialOperationStatus.Applied, surface.Operation.Status);
        // Invoke the already retained command, exactly as a cached gizmo consumer can do.
        command.action();
        Assert.NotNull(capturedMenu);
        var options = (List<FloatMenuOption>)AccessTools.Field(typeof(FloatMenu), "options").GetValue(capturedMenu);
        Assert.Contains(options, option => option.Label.Contains("荒芜行星", StringComparison.Ordinal) &&
            option.Label.Contains(surface.Handle.LayerId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    [Fact]
    public void SurfaceFailureSupersedesOldAppliedStatusAndRetainsNativeDetails()
    {
        var window = (Window_CelestialFrameworkDebug)RuntimeHelpers.GetUninitializedObject(typeof(Window_CelestialFrameworkDebug));
        AccessTools.Field(typeof(Window_CelestialFrameworkDebug), "lastOperation").SetValue(window, "old-applied-create");
        var method = AccessTools.Method(typeof(Window_CelestialFrameworkDebug), "ReportFailure");
        Assert.NotNull(method);
        method.Invoke(window, new object[] { new[] { new CelestialError("SurfaceGenerationFailed", Detail: "native layer fixture failure") } });
        Assert.Null(AccessTools.Field(typeof(Window_CelestialFrameworkDebug), "lastOperation").GetValue(window));
        string status = (string)AccessTools.Field(typeof(Window_CelestialFrameworkDebug), "status").GetValue(window);
        Assert.Contains("SurfaceGenerationFailed", status);
        Assert.Contains("native layer fixture failure", status);
    }

    private static bool FindWindows(ref WindowStack __result) { __result = fakeWindows; return false; }
    private static bool FindSelected(ref PlanetLayer __result) { __result = selectedLayer; return false; }
    private static bool CaptureWindow(Window window) { capturedMenu = window as FloatMenu; return false; }
    private static bool SkipCall() => false;
    [Theory]
    [InlineData("PF_BarrenPlanet")]
    [InlineData("PF_FrozenPlanet")]
    [InlineData("PF_GreenhousePlanet")]
    public void ActualAlienPresetGeneratesNativeLayerAndResolvesBackToItsOwner(string definition)
    {
        using var fixture = new Fixture();
        var body = fixture.Create(definition);
        var context = fixture.Session.Context();
        Assert.True(context.Success);
        var created = fixture.Harness.Runtime.RequestSurface(new(context.Value, body.Id));
        fixture.Session.Track(created.Operation);
        fixture.Session.Refresh();
        Assert.Equal(CelestialOperationStatus.Applied, created.Operation.Status);
        Assert.NotNull(created.Handle);
        var layer = Assert.IsType<CelestialSurfaceLayer>(fixture.Native.Grid.PlanetLayers[created.Handle.LayerId]);
        Assert.Equal(CelestialSurfaceState.Ready, layer.Header.State);
        Assert.Equal(layer.TilesCount, layer.Tiles.Count);
        Assert.True(layer.TilesCount > 0);
        Assert.Equal(body.Id, layer.Header.OwnerId);
        Assert.Same(layer, Launch.GetSurfaceState.GetLayerForBody(fixture.Manager, body.Description.Handle));
        Assert.Equal(body.Description.Handle, Launch.GetSurfaceState.GetBodyForLayer(fixture.Manager, layer));
        Assert.True(fixture.Manager.CaptureTarget(body.Description.Handle).Success);
        Assert.Contains(layer, CelestialBodyLayerManager.GetAllCelestialBodyLayers());
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly Harmony assets = new("PFL.Tests.PlanetKindNavigationNoGpu");
        internal CelestialNativeWorldTestScope Native { get; } = new();
        internal PlanetKindDebugTests.Harness Harness { get; private set; }
        internal CelestialBodyManager Manager => Native.Game.GetComponent<CelestialBodyManager>();
        internal PlanetKindDebugSession Session => PlanetKindDebugSession.For(Harness.Runtime);
        internal Fixture()
        {
            try { Initialize(); }
            catch { Dispose(); throw; }
        }
        private void Initialize()
        {
            assets.Patch(AccessTools.Method(typeof(ContentFinder<Texture2D>), "Get"),
                prefix: new HarmonyMethod(typeof(Fixture), nameof(SkipTexture)));
            var rootDef = new PlanetLayerDef { defName = "PF_NavigationRoot", layerType = typeof(CelestialSurfaceLayer),
                tileType = typeof(SurfaceTile), worldDrawLayers = new() };
            Native.SetRoot(rootDef);
            RuntimePlanetLayerService.RegisterCelestialInstance(Native.Grid, rootDef,
                new(PlanetKindDebugTests.Harness.Root, -1, 5000000, 100, 0, 1, ReadOnlyMemory<byte>.Empty));
            var tiles = new WorldGenStepDef { defName = "PF_CelestialTiles", worldGenStep = new CelestialWorldGenStep() };
            var definition = new PlanetLayerDef { defName = "PF_CelestialSurface", layerType = typeof(CelestialSurfaceLayer),
                tileType = typeof(SurfaceTile), worldDrawLayers = new() };
            AccessTools.Field(typeof(PlanetLayerDef), "worldGenSteps").SetValue(definition, new List<WorldGenStepDef> { tiles });
            Native.AddDefs(definition);
            Native.AddDefs(tiles);
            Harness = new(runtime => { _ = new CelestialSurfaceService(runtime, definition); });
            Native.AddDefs(Harness.Content.Biomes.Values.ToArray());
            Assert.True(Manager.AttachCelestialRuntime(Harness.Runtime).Success);
        }
        internal CelestialBodySnapshot Create(string definition)
        {
            var operation = Session.Create(new[] { Harness.Content.Bodies[definition] });
            Harness.Advance();
            Session.Refresh();
            Assert.Equal(CelestialOperationStatus.Applied, Session.LastOperation.Status);
            return Harness.Runtime.Query(Assert.Single(operation.InstanceIds)).Value;
        }
        private static bool SkipTexture() => false;
        internal void CaptureMenus()
        {
            capturedMenu = null;
            selectedLayer = Native.Grid.PlanetLayers.Values.First();
            assets.Patch(AccessTools.PropertyGetter(typeof(PlanetLayer), "Selected"), prefix: new HarmonyMethod(typeof(PlanetKindSurfaceNavigationTests), nameof(FindSelected)));
            assets.Patch(AccessTools.PropertyGetter(typeof(Find), "WindowStack"), prefix: new HarmonyMethod(typeof(PlanetKindSurfaceNavigationTests), nameof(FindWindows)));
            assets.Patch(AccessTools.Method(typeof(WindowStack), "Add"), prefix: new HarmonyMethod(typeof(PlanetKindSurfaceNavigationTests), nameof(CaptureWindow)));
            assets.Patch(AccessTools.Method(typeof(FloatMenuOption), "SetSizeMode"), prefix: new HarmonyMethod(typeof(PlanetKindSurfaceNavigationTests), nameof(SkipCall)));
            foreach (var method in AccessTools.GetDeclaredMethods(AccessTools.TypeByName("Verse.Sound.SoundStarter")).Where(m => m.Name == "PlayOneShotOnCamera"))
                assets.Patch(method, prefix: new HarmonyMethod(typeof(PlanetKindSurfaceNavigationTests), nameof(SkipCall)));
        }
        public void Dispose() { Harness?.Dispose(); Native.Dispose(); assets.UnpatchAll(assets.Id); }
    }
}
