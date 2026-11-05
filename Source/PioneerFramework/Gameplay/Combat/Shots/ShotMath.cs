using System;

namespace PioneerFramework.Combat
{
    /// <summary>Shot numerical rules. No game/Unity types; shared by simulation and tests.</summary>
    public static class ShotMath
    {
        public static bool Positive(double value) => double.IsFinite(value) && value > 0;
        public static bool Nonnegative(double value) => double.IsFinite(value) && value >= 0;

        public static double MuzzleSpeed(double reference, double weaponFactor, double mountFactor)
        {
            if (!Positive(reference) || !Positive(weaponFactor) || !Positive(mountFactor)) return 0;
            double result = reference * weaponFactor * mountFactor;
            return Positive(result) ? result : 0;
        }

        public static float BlastFalloff(double distance, double radius, double exponent)
        {
            if (!Nonnegative(distance) || !Positive(radius) || !Positive(exponent) || distance >= radius) return 0;
            return (float)Math.Pow(1 - distance / radius, exponent);
        }
    }
}
