using System;

namespace PioneerFramework.SpaceAI
{
    /// <summary>Eight bounded local encounter candidates, using only contact motion and own telemetry.</summary>
    public static class AiPlanningCore
    {
        private static readonly double[] Horizons = { 10, 20, 40, 60, 90, 120, 180, 300 };
        public static DVector3 Plan(in AiShipState ship, in AiContact target, AiAction action, double now)
        {
            if (!target.Valid || !(ship.Acceleration > 0) || !(ship.DeltaV > 0)) return DVector3.zero;
            DVector3 relative = target.PositionAt(now) - ship.Position;
            if (!(relative.magnitude > 1)) return DVector3.zero;
            DVector3 away = -relative.normalized;
            double available = action == AiAction.Retreat ? ship.DeltaV : Math.Max(0, ship.DeltaV - ship.ReserveDeltaV);
            double limit = Math.Min(available, ship.Acceleration * 5);
            if (!(limit > .01)) return DVector3.zero;
            if (action == AiAction.Retreat) return away * Math.Min(limit, 50);
            double standoff = Math.Max(100, ship.WeaponRange * (ship.Role == AiRole.ElectronicWarfare ? .9 : .65));
            DVector3 best = DVector3.zero;
            double bestScore = double.PositiveInfinity;
            for (int i = 0; i < Horizons.Length; i++)
            {
                double t = Horizons[i];
                DVector3 desired = target.Velocity - ship.Velocity + (relative + away * standoff) / t;
                double magnitude = desired.magnitude;
                if (magnitude > limit) desired *= limit / magnitude;
                DVector3 future = relative + (target.Velocity - ship.Velocity - desired) * t;
                double error = Math.Abs(future.magnitude - standoff);
                double score = error + desired.magnitude * 10;
                if (score < bestScore) { best = desired; bestScore = score; }
            }
            return best;
        }
    }
}
