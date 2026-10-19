using System;

namespace PioneerFramework.SpaceAI
{
    /// <summary>Simulation seconds; missed decisions are coalesced, never replayed.</summary>
    public static class AiSchedule
    {
        public static bool IsDue(double now, ref double next, double interval)
        {
            if (!double.IsFinite(now) || now < next) return false;
            next = now + Math.Max(.01, interval);
            return true;
        }
    }
}
