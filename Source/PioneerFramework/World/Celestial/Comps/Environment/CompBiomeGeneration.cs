using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed class CompBiomeGeneration : CelestialBodyComp, ICelestialBiomeGenerationCapability
    {
        public CompBiomeGeneration(CelestialCompDescriptor descriptor) : base(descriptor) { }
        public CelestialGenerationProfile GetProfile() => ((BiomeGenerationResolvedProperties)Context.Properties).Profile;
    }
    public sealed class CompProperties_BiomeGeneration : CelestialCompProperties
    {
        public CelestialBiomeProfileDef profile = null!;
        // Explicit dependency orders the SAME resolved climate before terrain; omitted climate remains omitted.
        public bool useEnvironmentClimate;
        public override string HandlerId => BiomeGenerationResolvedProperties.Handler;
        public override int ConfigurationVersion => profile?.terrain == null ? 1 : 2;
        protected override IEnumerable<string> Provides => new[] { "biomes" };
        protected override IEnumerable<string> IntrinsicRequires
        {
            get
            {
                yield return "geometry";
                if (profile?.mode == CelestialBiomeGenerationMode.Parameterized) yield return "surface";
                if (useEnvironmentClimate) yield return "environment.existing";
            }
        }
        protected override string ExclusiveSlot => "biomes";
        public override CelestialBodyComp CreateComp() => new CompBiomeGeneration(Descriptor);
        public override CelestialResult<ICelestialResolvedProperties> Resolve(CelestialGenerationContext context)
        {
            if (profile == null) return CelestialResult.Reject<ICelestialResolvedProperties>(new CelestialError("BiomeProfileMissing", CompKey: key));
            var resolved = profile.ToProfile(context, useEnvironmentClimate);
            if (useEnvironmentClimate && resolved.Success && resolved.Value.Terrain?.Climate == null)
                return CelestialResult.Reject<ICelestialResolvedProperties>(new CelestialError("TerrainClimateMissing", CompKey: key));
            return resolved.Success ? CelestialResult.Ok<ICelestialResolvedProperties>(new BiomeGenerationResolvedProperties(key, resolved.Value)) :
                CelestialResult.Reject<ICelestialResolvedProperties>(resolved.Errors.ToArray());
        }
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (profile == null) yield return "BiomeProfileMissing";
            else foreach (var error in profile.ToProfile().Errors) yield return error.Code;
        }
    }
}
