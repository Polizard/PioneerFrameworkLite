using System;

namespace PioneerFramework.RingWorld
{
    public static class RingClimate
    {
        public static double DayPercent(double ticks) => ((ticks % 60000) + 60000) % 60000 / 60000;
        public static double SunGlow(double ticks) => Math.Clamp(Math.Cos((DayPercent(ticks) - .5) * 2 * Math.PI) * 1.5, 0, 1);
    }
}
