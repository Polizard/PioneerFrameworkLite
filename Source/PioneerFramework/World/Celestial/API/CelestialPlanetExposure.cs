using System;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed record CelestialExposureConfiguration
    {
        public double MinimumOxygenPascals { get; }
        public double MinimumPressurePascals { get; }
        public double MaximumPressurePascals { get; }
        public double HypoxiaPerDay { get; }
        public double PressurePerDay { get; }
        public double ToxicityPerDay { get; }
        public double RadiationPerDay { get; }
        public double CarbonDioxideThresholdPascals { get; }
        public bool GravityStats { get; }
        public CelestialExposureConfiguration(double minimumOxygenPascals, double minimumPressurePascals,
            double maximumPressurePascals, double hypoxiaPerDay, double pressurePerDay, double toxicityPerDay,
            double radiationPerDay, double carbonDioxideThresholdPascals, bool gravityStats)
        {
            if (new[] { minimumOxygenPascals, minimumPressurePascals, maximumPressurePascals, hypoxiaPerDay,
                pressurePerDay, toxicityPerDay, radiationPerDay, carbonDioxideThresholdPascals }.Any(x => !double.IsFinite(x) || x < 0) ||
                maximumPressurePascals <= minimumPressurePascals || minimumOxygenPascals <= 0 || carbonDioxideThresholdPascals <= 0)
                throw new ArgumentException("Invalid exposure configuration.");
            MinimumOxygenPascals = minimumOxygenPascals; MinimumPressurePascals = minimumPressurePascals;
            MaximumPressurePascals = maximumPressurePascals; HypoxiaPerDay = hypoxiaPerDay; PressurePerDay = pressurePerDay;
            ToxicityPerDay = toxicityPerDay; RadiationPerDay = radiationPerDay;
            CarbonDioxideThresholdPascals = carbonDioxideThresholdPascals; GravityStats = gravityStats;
        }
    }
    /// <summary>Separate channels. Native StatWorker already aggregates genes and equipment once.</summary>
    public sealed record CelestialExposureProtection(bool Biological, double OxygenSupply = 0,
        double LowPressureResistance = 0, double HighPressureResistance = 0,
        double ToxicResistance = 0, double ToxicEnvironmentResistance = 0, double RadiationResistance = 0);
    public sealed record CelestialExposureDose(double Hypoxia, double Pressure, double Toxicity, double Radiation)
    { public bool Dangerous => Hypoxia > 0 || Pressure > 0 || Toxicity > 0 || Radiation > 0; }
    public static class CelestialExposureModel
    {
        public static CelestialExposureDose Dose(CelestialSurfaceEnvironmentSample sample, CelestialExposureProtection protection,
            int elapsedGameTicks, bool nativeVacuumOwnsDamage)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(elapsedGameTicks);
            var config = sample.Exposure;
            if (config == null || !protection.Biological || elapsedGameTicks == 0) return new(0, 0, 0, 0);
            double days = elapsedGameTicks / 60000d;
            double oxygen = sample.OxygenPartialPressurePascals.HasValue ?
                Math.Clamp(1 - sample.OxygenPartialPressurePascals.Value / config.MinimumOxygenPascals, 0, 1) : 0;
            double low = sample.PressurePascals.HasValue && sample.PressurePascals < config.MinimumPressurePascals ?
                Math.Clamp(1 - sample.PressurePascals.Value / Math.Max(1, config.MinimumPressurePascals), 0, 1) : 0;
            double high = sample.PressurePascals.HasValue ? Math.Clamp(sample.PressurePascals.Value / config.MaximumPressurePascals - 1, 0, 10) : 0;
            if (nativeVacuumOwnsDamage) { oxygen = 0; low = 0; }
            return new(oxygen * config.HypoxiaPerDay * days * Remaining(protection.OxygenSupply),
                (low * Remaining(protection.LowPressureResistance) + high * Remaining(protection.HighPressureResistance)) * config.PressurePerDay * days,
                sample.ToxicityPerDay * days * Remaining(protection.ToxicResistance) * Remaining(protection.ToxicEnvironmentResistance),
                sample.RadiationPerDay * days * Remaining(protection.RadiationResistance));
        }
        private static double Remaining(double value) => 1 - Math.Clamp(value, 0, 1);
        public static double GravityFactor(double gravity, bool carrying) => carrying ?
            Math.Clamp(9.80665 / Math.Max(.1, gravity), .25, 2) : Math.Clamp(Math.Sqrt(9.80665 / Math.Max(.1, gravity)), .5, 1.5);
    }
}
