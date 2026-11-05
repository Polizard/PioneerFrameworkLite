using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed class CompMapGeneration(CelestialCompDescriptor descriptor) : CelestialBodyComp(descriptor), ICelestialMapGenerationCapability
    {
        public CelestialMapProfile GetProfile() => ((MapGenerationResolvedProperties)Context.Properties).Profile;
    }
    public sealed class CompProperties_MapGeneration : CelestialCompProperties
    {
        public CelestialMapProfileDef profile = null!;
        public override string HandlerId => MapGenerationResolvedProperties.Handler;
        protected override IEnumerable<string> Provides => new[] { "map.generation" };
        protected override IEnumerable<string> IntrinsicRequires => new[] { "surface" };
        protected override string ExclusiveSlot => "map.generation";
        public override CelestialBodyComp CreateComp() => new CompMapGeneration(Descriptor);
        public override CelestialResult<ICelestialResolvedProperties> Resolve(CelestialGenerationContext context)
        {
            var resolved = profile?.ToProfile();
            return resolved == null ? CelestialResult.Reject<ICelestialResolvedProperties>(new CelestialError("MapProfileMissing", CompKey: key)) :
                resolved.Success ? CelestialResult.Ok<ICelestialResolvedProperties>(new MapGenerationResolvedProperties(key, resolved.Value)) :
                CelestialResult.Reject<ICelestialResolvedProperties>(resolved.Errors.ToArray());
        }
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (profile == null) yield return "MapProfileMissing";
            else foreach (var error in profile.ToProfile().Errors) yield return error.Code;
        }
    }
}
