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
            double angularRate=parentAngularRate+RingRotation.AngularRate;
            DVector3 position=SurfaceFrame.FixedToEci(geometry.Center(tile),angularRate,physicsTick);
            return new RingTileFrame(position,
                SurfaceFrame.FixedToEci(geometry.Normal(tile),angularRate,physicsTick),
                SurfaceFrame.FixedToEci(geometry.Tangent(tile),angularRate,physicsTick),
                SurfaceFrame.SurfaceVelocity(position,angularRate));
        }
    }
}
