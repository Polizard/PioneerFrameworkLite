using System;
using MechJebLib.Lambert;

namespace PioneerFramework.SpaceAI
{
    /// <summary>One serial Lambert candidate per Step, eight candidates total. No task or parallel dispatch.</summary>
    public sealed class AiOrbitPlanningJob
    {
        private readonly DVector3 ownPosition, ownVelocity, targetPosition, targetVelocity;
        private readonly double mu, safeRadius, budget;
        private int index;
        private double bestCost = double.PositiveInfinity;
        public bool Completed => index >= 8;
        public bool HasSolution => double.IsFinite(bestCost);
        public double TotalDeltaV => bestCost;
        public DVector3 DeltaV { get; private set; }
        public AiOrbitPlanningJob(DVector3 position, DVector3 velocity, DVector3 target, DVector3 targetSpeed,
            double gravity, double minimumRadius, double maxDv)
        { ownPosition = position; ownVelocity = velocity; targetPosition = target; targetVelocity = targetSpeed; mu = gravity; safeRadius = minimumRadius; budget = maxDv; }
        public void Step()
        {
            if (Completed) return;
            int candidate = index++;
            if (!(mu > 0) || !(budget > 0) || ownPosition.magnitude <= safeRadius) return;
            double period = 2 * Math.PI * Math.Sqrt(Math.Pow(ownPosition.magnitude, 3) / mu);
            double time = Math.Max(30, period * (.025 + candidate * .075));
            try
            {
                DVector3 destination = OrbitMath.GetPositionAtTimeDelta(targetPosition, targetVelocity, time, mu);
                DVector3 goalSpeed = OrbitMath.GetVelocityAtTimeDelta(targetPosition, targetVelocity, time, mu);
                var (departure, arrival) = Gooding.Solve(mu, ownPosition, destination, time, TransferGeometry.ShortWay);
                DVector3 dv = departure - ownVelocity;
                double cost = dv.magnitude + (goalSpeed - arrival).magnitude;
                if (!double.IsFinite(cost) || cost > budget ||
                    !OrbitMath.IsOrbitSafe(ownPosition, departure, safeRadius, mu) || cost >= bestCost) return;
                bestCost = cost;
                DeltaV = dv;
            }
            catch (Exception e) when (e is ArgumentException || e is ArithmeticException || e is InvalidOperationException)
            { /* A geometrically invalid candidate does not invalidate the remaining search. */ }
        }
    }
}
