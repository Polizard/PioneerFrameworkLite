using System;

namespace PioneerFramework.Megastructures
{
    public static class ArtificialCycle
    {
        public static double DayPercent(double displayTicks,double periodTicks)
        {
            if(!double.IsFinite(displayTicks)||!double.IsFinite(periodTicks)||periodTicks<=0)throw new ArgumentOutOfRangeException(nameof(periodTicks));
            return ((displayTicks%periodTicks)+periodTicks)%periodTicks/periodTicks;
        }
        public static double SunGlow(double displayTicks,double periodTicks)=>Math.Clamp(Math.Cos((DayPercent(displayTicks,periodTicks)-.5)*2*Math.PI)*1.5,0,1);
    }
    public sealed class ArtificialEnvironmentProfile
    {
        public double DayPeriodTicks { get; set; }=60000;
        public string BiomeDefName { get; set; }="TemperateForest";
        public float Temperature { get; set; }=21;
        public float Rainfall { get; set; }=1200;
        public float Elevation { get; set; }=100;
    }
}
