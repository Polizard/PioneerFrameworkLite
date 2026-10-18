using System;

namespace PioneerFramework.RingWorld
{
    public readonly record struct RingTileFrame(DVector3 PositionMeters, DVector3 Normal,
        DVector3 Tangent, DVector3 VelocityMetersPerSecond);

    public static class RingSurfaceFrame
    {
        /// <summary>Parent-centered inertial frame, using the existing SurfaceFrame rotation convention.</summary>
        public static RingTileFrame At(RingParameters parameters, int tile, double parentAngularRate, double physicsTick)
        {
            if(!double.IsFinite(parentAngularRate)||!double.IsFinite(physicsTick)) throw new ArgumentOutOfRangeException(nameof(physicsTick));
            var geometry=new RingGeometry(new RingTopology(parameters.Columns,parameters.Rows),parameters.RadiusMeters);
            var frame=Megastructures.SurfaceSpatialMath.EvaluateParentAxial(new(geometry.Center(tile),geometry.Normal(tile),geometry.Tangent(tile)),
                new Megastructures.SpatialState(parameters.ParentBodyId,DVector3.zero,DVector3.zero,Megastructures.RotationData.Identity,new(0,-parentAngularRate,0),physicsTick),RingRotation.AngularRate,physicsTick);
            return new RingTileFrame(frame.Pose.PositionSystem.v,frame.Normal,frame.Tangent,frame.Pose.VelocitySystem.v);
        }
    }
}
