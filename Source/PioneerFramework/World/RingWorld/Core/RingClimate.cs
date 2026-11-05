using System;

namespace PioneerFramework.RingWorld
{
    public static class RingClimate
    {
        public static double DayPercent(double ticks) => Megastructures.ArtificialCycle.DayPercent(ticks,60000);
        public static double SunGlow(double ticks) => Megastructures.ArtificialCycle.SunGlow(ticks,60000);
    }
}
