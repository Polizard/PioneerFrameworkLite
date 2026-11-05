using System;
using System.IO;
using System.Linq;
using System.Reflection;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests
{
    public class RuntimePlanetLayerServiceTests
    {
        private const BindingFlags StaticFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static string SourcePath =>
            Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Source", "PioneerFramework", "World", "Visuals", "RuntimePlanetLayerService.cs");

        [Fact]
        public void CreateSurface_BuildsStableSurfaceSpec()
        {
            var spec = RuntimePlanetLayerSpec.CreateSurface(
                "PF_TestLayer", "test layer", 42.5f, 4, "Desert");

            Assert.Equal("PF_TestLayer", spec.DefName);
            Assert.Equal("test layer", spec.Label);
            Assert.Equal(42.5f, spec.Radius, 3);
            Assert.Equal(4, spec.Subdivisions);
            Assert.Equal("Desert", spec.BackgroundBiomeDefName);
            Assert.Contains("Terrain", spec.WorldGenStepDefNames);
            Assert.DoesNotContain("Tiles", spec.WorldGenStepDefNames);
            Assert.NotEmpty(spec.WorldDrawLayerTypes);
            Assert.NotEmpty(spec.WorldTabTypes);
        }

        [Fact]
        public void ValidateSpec_AcceptsValidSurfaceSpec()
        {
            var spec = RuntimePlanetLayerSpec.CreateSurface(
                "PF_TestLayer", "test layer", 42.5f, 4, "Desert");

            bool valid = RuntimePlanetLayerService.ValidateSpec(spec, out string error);
            Assert.True(valid, error);
        }

        [Fact]
        public void ValidateSpec_RejectsBlankDefName()
        {
            var spec = RuntimePlanetLayerSpec.CreateSurface(
                "", "invalid layer", 42.5f, 4, "Desert");

            bool valid = RuntimePlanetLayerService.ValidateSpec(spec, out string error);
            Assert.False(valid);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Fact]
        public void Service_ExposesRequiredMethods()
        {
            foreach (var name in new[]
            {
                "EnsureDefinition",
                "FindByDefName",
                "Create",
                "CanRemove",
                "TryRemove"
            })
            {
                var method = typeof(RuntimePlanetLayerService).GetMethods(StaticFlags)
                    .Where(m => m.Name == name)
                    .FirstOrDefault();
                Assert.True(method != null, $"RuntimePlanetLayerService.{name} is missing.");
            }

            Assert.NotNull(typeof(RuntimePlanetLayerService).GetMethod(
                "RunWorldGenerationPreservingRandState", StaticFlags));
            Assert.NotNull(typeof(RuntimePlanetLayerService).GetMethod(
                "DestroyLayerWorldObjects", StaticFlags));
        }

        [Fact]
        public void Source_PreservesRandStateAndRemovalOrder()
        {
            string source = File.ReadAllText(SourcePath);

            Assert.Contains("Rand.PushState();", source);
            Assert.Contains("Rand.PopState();", source);
            Assert.Contains("RunWorldGenerationPreservingRandState(layer, seedPart);", source);
            Assert.Contains("AllWorldObjectsOnLayer", source);
            Assert.Contains("IsRootSurface", source);
            Assert.Contains("RemovePlanetLayer", source);
            Assert.Contains("catch", source);

            int tryRemoveStart = source.IndexOf("public static bool TryRemove(", StringComparison.Ordinal);
            Assert.True(tryRemoveStart >= 0, "TryRemove source was not found.");

            string tryRemoveSource = source.Substring(tryRemoveStart);
            int destroyIndex = tryRemoveSource.IndexOf("DestroyLayerWorldObjects(layer);", StringComparison.Ordinal);
            int removeIndex = tryRemoveSource.IndexOf("worldGrid.RemovePlanetLayer(layer);", StringComparison.Ordinal);

            Assert.True(destroyIndex >= 0, "DestroyLayerWorldObjects call not found in TryRemove.");
            Assert.True(removeIndex > destroyIndex,
                "World objects must be destroyed before WorldGrid unregisters their layer.");
        }
    }
}
