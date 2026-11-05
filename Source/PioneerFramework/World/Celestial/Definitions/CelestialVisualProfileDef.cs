using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialVisualProfileDef : Def
    {
        public Color color = Color.white;
        public string? texturePath;
        public BiomeDef? backgroundBiome;
        public double shellHeightMeters;
        public double fadeRangeMeters = 5000;
        public double driftPeriodTicks = 600000;
        public double distortionPeriodTicks = 6000;
        public CelestialResult<CelestialVisualProfile> ToProfile()
        {
            try { return CelestialResult.Ok(new CelestialVisualProfile(defName, color.r, color.g, color.b, color.a, texturePath, backgroundBiome?.defName,
                shellHeightMeters, fadeRangeMeters, driftPeriodTicks, distortionPeriodTicks)); }
            catch (ArgumentException exception) { return CelestialResult.Reject<CelestialVisualProfile>(new CelestialError("InvalidVisualProfile", Detail: exception.Message)); }
        }
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            foreach (var error in ToProfile().Errors) yield return error.Code + ": " + error.Detail;
        }
    }
    public abstract class CelestialVisualCompProperties : CelestialCompProperties
    {
        public CelestialVisualProfileDef profile = null!;
        protected abstract CelestialVisualKind Kind { get; }
        public override string HandlerId => VisualResolvedProperties.HandlerFor(Kind);
        protected override IEnumerable<string> Provides => new[] { VisualResolvedProperties.CapabilityFor(Kind) };
        protected override IEnumerable<string> IntrinsicRequires => new[] { "geometry" };
        protected override string ExclusiveSlot => VisualResolvedProperties.CapabilityFor(Kind);
        public override CelestialResult<ICelestialResolvedProperties> Resolve(CelestialGenerationContext context)
        {
            var resolved = profile?.ToProfile();
            return resolved == null ? CelestialResult.Reject<ICelestialResolvedProperties>(new CelestialError("VisualProfileMissing", CompKey: key)) :
                resolved.Success ? CelestialResult.Ok<ICelestialResolvedProperties>(new VisualResolvedProperties(key, Kind, resolved.Value)) : CelestialResult.Reject<ICelestialResolvedProperties>(resolved.Errors.ToArray());
        }
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (profile == null) yield return "VisualProfileMissing";
            else foreach (var error in profile.ToProfile().Errors) yield return error.Code;
        }
    }
}
