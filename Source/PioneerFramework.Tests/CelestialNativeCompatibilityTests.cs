using System;
using System.Linq;
using HarmonyLib;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialNativeCompatibilityTests
{
    [Fact]
    public void LayerMetadataUsesObjectIdentityAcrossWorldsSharingTheSameLayerNumber()
    {
        var assets = new Harmony("PFL.Celestial.MetadataNoGpuProbe");
        assets.Patch(AccessTools.Method(typeof(Verse.ContentFinder<UnityEngine.Texture2D>), "Get"),
            prefix: new HarmonyMethod(typeof(CelestialNativeCompatibilityTests), nameof(SkipTextureLookup)));
        try
        {
        using var fixture = new CelestialWorldBootstrapTests.Fixture();
        CelestialSurfaceLayer Layer(RimWorld.Planet.WorldGrid grid, double day)
        {
            var properties = new ExistingEnvironmentResolvedProperties("environment", new("profile", day, "light", Array.Empty<string>()));
            var configuration = CelestialGenerationConfiguration.Encode(new[] { new CelestialCompSaveRecord("environment", properties.HandlerId, 1, 1,
                properties.Serialize().Span, ReadOnlySpan<byte>.Empty) });
            return RuntimePlanetLayerService.RegisterCelestialInstance(grid, fixture.RootDefinition, new(CelestialInstanceId.Parse("same-owner"), -1, 1000, 1, 0, 1, configuration));
        }
        var first = Layer(fixture.Native.Grid, 48000);
        Assert.Equal(48000, CelestialBodyLayerManager.GetDayLength(first));
        using (var secondWorld = new CelestialNativeWorldTestScope())
        {
            var second = Layer(secondWorld.Grid, 72000);
            Assert.Equal(first.LayerID, second.LayerID);
            Assert.Equal(72000, CelestialBodyLayerManager.GetDayLength(second));
            Assert.Equal(48000, CelestialBodyLayerManager.GetDayLength(first));
        }
        }
        finally { assets.UnpatchAll(assets.Id); }
    }
    private static bool SkipTextureLookup() => false;
    [Fact]
    public void NativeSignaturesAndEveryInstalledHandlerCanBeVerifiedWithoutGeneratingAWorld()
    {
        // Initialize the permanent owner before the temporary probe installs individual handlers.
        // Otherwise the transpiler can initialize its declaring type during a temporary patch.
        PioneerFramework.MapGeneration.NativeLocalMapHooks.Install();
        Assert.True(CelestialNativeCompatibility.ValidateNativeContracts().Success, string.Join(",", CelestialNativeCompatibility.Report.Where(entry => !entry.Available)));
        Assert.Equal("RootLayerAdapterMissing", Assert.Single(CelestialNativeCompatibility.ResolveRootDefinition(null).Errors).Code);
        Assert.Equal("NativePatchMissing", Assert.Single(CelestialNativeCompatibility.ValidateInstalledHooks("missing-probe-hook").Errors).Code);
        var patch = new Harmony("PFL.Celestial.CompatibilityInstallationProbe");
        try
        {
            foreach (var binding in CelestialNativeCompatibility.ExpectedPatches())
            {
                if (Harmony.GetPatchInfo(binding.Target) is { } existing && existing.Prefixes.Concat(existing.Postfixes).Concat(existing.Finalizers).Concat(existing.Transpilers).Any(p => p.PatchMethod == binding.Handler)) continue;
                var handler = new HarmonyMethod(binding.Handler);
                patch.Patch(binding.Target, prefix: binding.Kind == "prefix" ? handler : null, postfix: binding.Kind == "postfix" ? handler : null,
                    finalizer: binding.Kind == "finalizer" ? handler : null, transpiler: binding.Kind == "transpiler" ? handler : null);
            }
            Assert.True(CelestialNativeCompatibility.ValidateInstalledHooks().Success);
            Assert.All(CelestialNativeCompatibility.CaptureInstalledHooks(), entry => Assert.True(entry.Available));
        }
        finally { patch.UnpatchAll(patch.Id); }
    }
}
