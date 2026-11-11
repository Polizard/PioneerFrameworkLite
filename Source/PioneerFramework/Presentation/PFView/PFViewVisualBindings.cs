using PioneerFramework.Celestial;
using UnityEngine;

namespace PioneerFramework
{
    /// <summary>Production material inputs, computed without creating GPU resources or requesting world layers.</summary>
    internal static class PFViewVisualBindings
    {
        internal readonly record struct CloudPlan(double AltitudeMeters, Vector4 Parameters, Vector4 Tint);
        internal readonly record struct BandPlan(Vector4 Parameters, Vector4 Color);
        internal static CloudPlan Clouds(CelestialVisualProfile? profile, double tick, double cameraAltitudeMeters)
        {
            double height = profile?.ShellHeightMeters ?? PFViewClouds.AltitudeMeters;
            var appearance = profile?.Appearance;
            return new(height, new Vector4(
                (float)PFViewClouds.Opacity(cameraAltitudeMeters, height, profile?.FadeRangeMeters ?? PFViewClouds.FadeRangeMeters),
                (float)PFViewClouds.AnimationPhase(tick, profile?.DriftPeriodTicks ?? PFViewClouds.DriftPeriodTicks),
                (float)PFViewClouds.AnimationPhase(tick, profile?.DistortionPeriodTicks ?? PFViewClouds.DistortionPeriodTicks),
                (float)(appearance?.CloudCoverage ?? 1)),
                appearance == null ? new Vector4(1, 1, 1, 1) : new Vector4((float)profile!.Red, (float)profile.Green, (float)profile.Blue, (float)profile.Alpha));
        }
        internal static BandPlan Bands(CelestialVisualProfile profile)
        {
            var appearance = profile.Appearance;
            if (appearance?.Style != CelestialSurfaceStyle.GasBands) throw new System.ArgumentException("Gas bands must be explicitly selected.");
            return new(new Vector4((float)appearance.BandFrequency, (float)appearance.BandContrast, 0, 0),
                new Vector4((float)profile.Red, (float)profile.Green, (float)profile.Blue, (float)profile.Alpha));
        }
        internal static Color Atmosphere(Color legacyColor, CelestialVisualProfile? profile, bool night)
        {
            float opacity = night ? PFViewAtmosphere.NightShellAlpha : PFViewAtmosphere.ShellAlpha;
            if (profile?.Appearance != null)
                return new Color((float)profile.Red, (float)profile.Green, (float)profile.Blue, opacity * (float)profile.Alpha);
            legacyColor.a = opacity;
            return legacyColor;
        }
    }
}
