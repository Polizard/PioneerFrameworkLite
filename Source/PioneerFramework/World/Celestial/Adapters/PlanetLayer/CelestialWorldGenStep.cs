using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialWorldGenStep : WorldGenStep
    {
        public override int SeedPart => 58474782;
        public override void GenerateFresh(string seed, PlanetLayer layer)
        {
            if (layer is not CelestialSurfaceLayer surface) throw new InvalidOperationException("Celestial surface adapter required.");
            var configuration = CelestialLayerConfiguration.For(surface.Header);
            if (!configuration.Success) { surface.Header.State = CelestialSurfaceState.Failed; throw new InvalidOperationException(configuration.Errors[0].Code); }
            var profiles = configuration.Value.Where(record => record.HandlerId == BiomeGenerationResolvedProperties.Handler).ToArray();
            if (profiles.Length != 1) { surface.Header.State = CelestialSurfaceState.Failed; throw new InvalidOperationException("BiomeProfileMissingOrAmbiguous"); }
            var properties = BiomeGenerationResolvedProperties.Deserialize(profiles[0].Key, profiles[0].ConfigurationVersion, profiles[0].ConfigurationPayload);
            if (!properties.Success) { surface.Header.State = CelestialSurfaceState.Failed; throw new InvalidOperationException(properties.Errors[0].Code); }
            var generated = CelestialWorldGeneration.Generate(surface, properties.Value.Profile, seed);
            if (!generated.Success) throw new InvalidOperationException(string.Join(", ", generated.Errors.Select(error => error.Code + ": " + error.Detail)));
        }
    }

    public static partial class CelestialWorldGeneration
    {
        public static CelestialResult<bool> Generate(PlanetLayer layer, CelestialGenerationProfile profile, string? seedString = null)
        {
            if (layer is not CelestialSurfaceLayer surface) return CelestialResult.Reject<bool>(new CelestialError("SurfaceAdapterMissing"));
            if (surface.Header.State == CelestialSurfaceState.Ready) return CelestialResult.Ok(true);
            if (surface.Header.State == CelestialSurfaceState.Failed) return CelestialResult.Reject<bool>(new CelestialError("SurfaceGenerationFailed", surface.Header.OwnerId));
            Rand.PushState();
            try
            {
                if (profile.Mode == CelestialBiomeGenerationMode.Parameterized)
                {
                    // 单通道：参数化先写 tile；原生步骤只从 profile.WorldGenSteps 执行一次（构造器禁止 Terrain）。
                    GenerateParameterized(surface, profile, seedString ?? Find.World.info.seedString);
                    GenerateNativeSteps(surface, profile, seedString ?? Find.World.info.seedString);
                }
                else GenerateNativeSteps(surface, profile, seedString ?? Find.World.info.seedString);
                if (surface.Tiles.Count != surface.TilesCount || surface.Tiles.Any(tile => tile.PrimaryBiome == null))
                    throw new InvalidOperationException("Generated tiles are incomplete.");
                surface.Header.State = CelestialSurfaceState.Ready;
                return CelestialResult.Ok(true);
            }
            catch (Exception exception)
            {
                surface.Header.State = CelestialSurfaceState.Failed;
                return CelestialResult.Reject<bool>(new CelestialError("SurfaceGenerationFailed", surface.Header.OwnerId, Detail: exception.Message));
            }
            finally { Rand.PopState(); }
        }

        private static void GenerateNativeSteps(CelestialSurfaceLayer layer, CelestialGenerationProfile profile, string seedString)
        {
            var names = profile.WorldGenSteps.Concat(layer.IsRootSurface ? profile.RootOnlySteps : Array.Empty<string>());
            var steps = names.Select(name => DefDatabase<WorldGenStepDef>.GetNamedSilentFail(name) ?? throw new InvalidOperationException("WorldGenStepMissing: " + name))
                .OrderBy(step => step.order).ToArray();
            if (steps.Any(step => step.worldGenStep == null || step.worldGenStep is CelestialWorldGenStep)) throw new InvalidOperationException("InvalidWorldGenStep");
            var previousSeeds = new Dictionary<int, int>();
            foreach (var step in steps)
            {
                // RimWorld 1.7 WorldGenerator.GetSeedPart：同 SeedPart 的前项数量作为偏移；不改变原步骤的执行顺序。
                int part = step.worldGenStep.SeedPart;
                int repeated = previousSeeds.GetValueOrDefault(part);
                previousSeeds[part] = repeated + 1;
                Rand.Seed = Gen.HashCombineInt(layer.Header.GenerationSeed, unchecked(part + repeated));
                step.worldGenStep.GenerateFresh(seedString, layer);
            }
        }
    }
}
