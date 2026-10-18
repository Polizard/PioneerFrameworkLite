using System;

namespace PioneerFramework.RingWorld
{
    public static class RingRotation
    {
        public const double PeriodTicks = 60000;
        public const double AngularRate = Math.Tau / PeriodTicks;

        private static readonly Megastructures.PeriodicMotion Motion=new(new DVector3(0,-1,0),PeriodTicks);
        public static double Phase(double physicsTick)=>Motion.Phase(physicsTick);

        public static DVector3 ToView(DVector3 local, double physicsTick) =>
            Motion.Rotate(local,physicsTick);

        public static DVector3 ToLocal(DVector3 view, double physicsTick) =>
            Motion.InverseRotate(view,physicsTick);
    }
}
