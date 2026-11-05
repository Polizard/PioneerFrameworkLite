using System;

namespace PioneerFramework.SpaceAI
{
    /// <summary>Body-frame encounter initial states, validated before creating any runtime objects.</summary>
    public static class AiSpawnOrbitCore
    {
        public static bool TryCreate(DVector3 targetPosition,DVector3 targetVelocity,double distance,double separation,
            double mu,double safeRadius,out DVector3 position,out DVector3 velocity)
        {
            position = velocity = DVector3.zero;
            if (!(mu > 0) || !(safeRadius > 0) || !double.IsFinite(distance) || !double.IsFinite(separation) ||
                !Finite(targetPosition) || !Finite(targetVelocity) || targetPosition.sqrMagnitude < 1) return false;
            var outward = targetPosition.normalized;
            var side = DVector3.Cross(outward,DVector3.up).normalized;
            if (side.sqrMagnitude < .1) side = DVector3.Cross(outward,DVector3.right).normalized;
            position = targetPosition+outward*distance+side*separation;
            if (!Finite(position) || position.magnitude <= safeRadius) return false;
            velocity = targetVelocity-outward*30;
            if (SafePeriapsis(position,velocity,safeRadius,mu)) return true;
            var radial = position.normalized;
            var tangent = targetVelocity-radial*DVector3.Dot(targetVelocity,radial);
            if (tangent.sqrMagnitude < 1) tangent = side;
            velocity = tangent.normalized*Math.Sqrt(mu/position.magnitude);
            return SafePeriapsis(position,velocity,safeRadius,mu);
        }
        // 出生检查同时覆盖椭圆、抛物和双曲状态；不改变原轨道引导的既有判据。
        private static bool SafePeriapsis(DVector3 position,DVector3 velocity,double safeRadius,double mu)
        {
            var momentum = DVector3.Cross(position,velocity);
            double eccentricity = (DVector3.Cross(velocity,momentum)/mu-position.normalized).magnitude;
            double periapsis = momentum.sqrMagnitude/(mu*(1+eccentricity));
            return double.IsFinite(periapsis) && periapsis >= safeRadius;
        }
        private static bool Finite(DVector3 v) => double.IsFinite(v.x) && double.IsFinite(v.y) && double.IsFinite(v.z);
    }
}
