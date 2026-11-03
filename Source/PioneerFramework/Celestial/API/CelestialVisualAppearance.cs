using System;
using System.Text.Json.Serialization;

namespace PioneerFramework.Celestial
{
    public enum CelestialSurfaceStyle { Descriptive, GasBands }

    /// <summary>Explicit v2 visual data; no physical atmosphere, surface or map capability is implied.</summary>
    public sealed class CelestialVisualAppearance
    {
        [JsonPropertyName("Style"), JsonPropertyOrder(1)]
        public CelestialSurfaceStyle Style { get; }
        [JsonPropertyName("CloudCoverage"), JsonPropertyOrder(2)]
        public double CloudCoverage { get; }
        [JsonPropertyName("BandFrequency"), JsonPropertyOrder(3)]
        public double BandFrequency { get; }
        [JsonPropertyName("BandContrast"), JsonPropertyOrder(4)]
        public double BandContrast { get; }
        public CelestialVisualAppearance(CelestialSurfaceStyle style, double cloudCoverage, double bandFrequency, double bandContrast)
        {
            if (!Enum.IsDefined(style)) throw new ArgumentOutOfRangeException(nameof(style));
            Domain(cloudCoverage, 0, 1, nameof(cloudCoverage));
            Domain(bandFrequency, 1, 128, nameof(bandFrequency));
            Domain(bandContrast, 0, 1, nameof(bandContrast));
            Style = style; CloudCoverage = cloudCoverage; BandFrequency = bandFrequency; BandContrast = bandContrast;
        }
        internal static void Domain(double value, double lower, double upper, string name)
        {
            if (!double.IsFinite(value) || value < lower || value > upper) throw new ArgumentOutOfRangeException(name);
        }
    }
}
