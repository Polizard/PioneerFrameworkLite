using System;

namespace PioneerFramework.Landing
{
    public static class LandingTransitPath
    {
        public static double Progress(int startTick, int durationTicks, int nowTick) =>
            durationTicks <= 0 ? 1 : Math.Clamp(((double)nowTick - startTick) / durationTicks, 0, 1);

        public static DVector3 Position(DVector3 departureCenter, DVector3 currentCenter,
            DVector3 startSystem, DVector3 targetSystem, double surfaceRadius, double progress)
        {
            if (progress <= 0) return startSystem;
            if (progress >= 1) return targetSystem;
            DVector3 from = startSystem - departureCenter;
            DVector3 to = targetSystem - currentCenter;
            double eased = progress * progress * (3 - 2 * progress);
            var a = from.normalized;
            var b = to.normalized;
            double dot = Math.Clamp(DVector3.Dot(a, b), -1, 1);
            DVector3 direction;
            if (dot > .999999) direction = (a * (1 - eased) + b * eased).normalized;
            else if (dot < -.999999)
            {
                var axis = Math.Abs(a.x) < .9 ? new DVector3(1, 0, 0) : new DVector3(0, 1, 0);
                var tangent = DVector3.Cross(a, axis).normalized;
                direction = a * Math.Cos(Math.PI * eased) + tangent * Math.Sin(Math.PI * eased);
            }
            else
            {
                double angle = Math.Acos(dot);
                direction = (a * Math.Sin((1 - eased) * angle) + b * Math.Sin(eased * angle)) / Math.Sin(angle);
            }
            double radius = Math.Max(surfaceRadius, from.magnitude * (1 - eased) + to.magnitude * eased);
            return currentCenter + direction.normalized * radius;
        }
    }
}
