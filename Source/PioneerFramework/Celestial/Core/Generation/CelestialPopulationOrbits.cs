using System;

namespace PioneerFramework.Celestial
{
    /// <summary>Population orbit constraints use meters, seconds, and cubic meters per second squared.</summary>
    internal static class CelestialPopulationOrbits
    {
        private const double GravitationalConstant = 6.67430e-11;

        internal static double GravitationalParameter(double radius, double density)
            => GravitationalConstant * (4.0 / 3.0) * Math.PI * radius * radius * radius * density;

        internal static double PeriodSeconds(double semiMajor, double combinedGm)
            => 2 * Math.PI * Math.Sqrt(Math.Pow(semiMajor, 3) / combinedGm);

        internal static double HillRadius(double periapsis, double orbitingGm, double parentGm)
            => periapsis * Math.Cbrt(orbitingGm / (3 * parentGm));

        internal static double RocheRadius(double parentRadius, double parentDensity, double satelliteDensity)
            => 2.44 * parentRadius * Math.Cbrt(parentDensity / satelliteDensity);

        internal static double MutualHillClearance(double firstSemiMajor, double secondSemiMajor, double combinedGm, double parentGm)
            => 3.5 * (firstSemiMajor + secondSemiMajor) / 2 * Math.Cbrt(combinedGm / (3 * parentGm));
    }

    internal readonly record struct CelestialRadialEnvelope(double SemiMajor, double Inner, double Outer)
    {
        internal static CelestialRadialEnvelope Around(double semiMajor, double eccentricity, double reach)
            => new(semiMajor, semiMajor * (1 - eccentricity) - reach, semiMajor * (1 + eccentricity) + reach);

        internal bool Overlaps(CelestialRadialEnvelope other, double margin)
            => Inner - margin <= other.Outer && Outer + margin >= other.Inner;
    }
}
