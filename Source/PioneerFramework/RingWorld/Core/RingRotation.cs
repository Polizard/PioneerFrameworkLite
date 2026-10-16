using System;

namespace PioneerFramework.RingWorld
{
    public static class RingRotation
    {
        public const double PeriodTicks = 60000;
        public const double AngularRate = Math.Tau / PeriodTicks;

        public static double Phase(double physicsTick)
        {
            if (!double.IsFinite(physicsTick)) throw new ArgumentOutOfRangeException(nameof(physicsTick));
            double tick = physicsTick % PeriodTicks;
            if (tick < 0) tick += PeriodTicks;
            return tick * AngularRate;
        }

        public static DVector3 ToView(DVector3 local, double physicsTick) =>
            SurfaceFrame.FixedToEci(local, 1, Phase(physicsTick));

        public static DVector3 ToLocal(DVector3 view, double physicsTick) =>
            SurfaceFrame.EciToFixed(view, 1, Phase(physicsTick));
    }
}
