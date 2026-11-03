using System.Collections.Generic;

namespace PioneerFramework.Celestial
{
    /// <summary>Numeric v2 values use the existing deterministic fixed/range/override sampler.</summary>
    public sealed class CelestialVisualAppearanceXml
    {
        public CelestialSurfaceStyle style;
        public double cloudCoverage = 1;
        public double bandFrequency = 12;
        public double bandContrast = 0.25;
        public List<CelestialEnvironmentParameterXml> parameters = new();

        internal CelestialVisualProfile Resolve(CelestialVisualProfileDef profile, CelestialGenerationContext? context, CelestialVisualKind? kind)
        {
            var sampler = new CelestialPlanetParameterSampler(parameters, context, "appearance.");
            if (kind == CelestialVisualKind.Stellar) throw new System.ArgumentException("Stellar visuals retain the v1 configuration.");
            double Read(string key, double fallback, double lower, double upper, bool active = true)
                => active || !kind.HasValue ? sampler.Get(key, fallback, lower, upper) : fallback;
            double red = sampler.Get("red", profile.color.r, 0, 1), green = sampler.Get("green", profile.color.g, 0, 1),
                blue = sampler.Get("blue", profile.color.b, 0, 1), alpha = Read("alpha", profile.color.a, 0, 1, kind != CelestialVisualKind.Surface);
            double height = Read("shellHeightMeters", profile.shellHeightMeters, 0, 1e9, kind != CelestialVisualKind.Surface),
                fade = Read("fadeRangeMeters", profile.fadeRangeMeters, 1e-6, 1e9, kind == CelestialVisualKind.Clouds),
                drift = Read("driftPeriodTicks", profile.driftPeriodTicks, 1, 1e15, kind == CelestialVisualKind.Clouds),
                distortion = Read("distortionPeriodTicks", profile.distortionPeriodTicks, 1, 1e15, kind == CelestialVisualKind.Clouds);
            var resolved = new CelestialVisualAppearance(style, Read("cloudCoverage", cloudCoverage, 0, 1, kind == CelestialVisualKind.Clouds),
                Read("bandFrequency", bandFrequency, 1, 128, style == CelestialSurfaceStyle.GasBands),
                Read("bandContrast", bandContrast, 0, 1, style == CelestialSurfaceStyle.GasBands));
            sampler.Complete();
            var result = new CelestialVisualProfile(profile.defName, red, green, blue, alpha, profile.texturePath, profile.backgroundBiome?.defName,
                height, fade, drift, distortion, resolved);
            if (kind.HasValue) result.ValidateVisualKind(kind.Value);
            return result;
        }
    }
}
