using System;
using PioneerFramework.Celestial;

namespace PioneerFramework.Launch
{
    public static partial class GetSurfaceState
    {
        // Retain the existing ephemeris arithmetic in its original Y-axis convention.
        // Other providers supply the complete fixed-to-inertial pose and inertial angular velocity.
        private static bool UsesProviderPose(CelestialBodyManager? manager)
            => manager?.TryGetCelestialRuntime(out var runtime) == true && runtime.Physics is not ExistingCelestialEphemerisBridge;

        internal static DVector3 CoRotationVelocity(CelestialBodyManager? manager, int bodyId, DVector3 bodyPosition, double seconds)
            => UsesProviderPose(manager) ? DVector3.Cross(manager!.GetBodyMotion(bodyId, seconds).AngularVelocity, bodyPosition)
                : SurfaceFrame.SurfaceVelocity(bodyPosition, RotationOmega(manager, bodyId));

        internal static (double Latitude, double Longitude) BodyLongLat(CelestialBodyManager? manager, int bodyId,
            DVector3 bodyPosition, double seconds)
        {
            if (!UsesProviderPose(manager)) return SurfaceFrame.EciToLongLat(bodyPosition, RotationOmega(manager, bodyId), seconds);
            var motion = manager!.GetBodyMotion(bodyId, seconds);
            var fixedPoint = DVector3.Rotate(bodyPosition, DQuaternion.Inverse(motion.Orientation));
            return SurfaceFrame.EciToLongLat(fixedPoint, 0, 0);
        }

        internal static (DVector3 Position, DVector3 Velocity) HoverState(CelestialBodyManager manager, int bodyId,
            double radius, double latitude, double longitude, double altitude, double seconds)
        {
            (DVector3 pos, DVector3 vel) local;
            if (UsesProviderPose(manager))
            {
                var surface = SurfaceFrame.SurfaceState(radius, latitude, longitude, 0, 0).pos;
                double r = surface.magnitude;
                var fixedPoint = r > 1e-3 ? surface + surface * (altitude / r) : surface;
                local = FixedSurfaceState(manager, bodyId, fixedPoint, seconds);
            }
            else local = HoverCore.TargetState(radius, latitude, longitude, altitude, RotationOmega(manager, bodyId), seconds);
            if (!manager.TryGetCelestialRuntime(out _)) return local;
            var offset = manager.BodyFrameOffset(bodyId, seconds);
            return (offset.Position + local.pos, offset.Velocity + local.vel);
        }

        internal static DVector3 HoverAcceleration(CelestialBodyManager manager, int bodyId, DVector3 framePosition, double seconds)
        {
            var local = framePosition;
            if (manager.TryGetCelestialRuntime(out _)) local -= manager.BodyFrameOffset(bodyId, seconds).Position;
            if (!UsesProviderPose(manager)) return HoverCore.TargetAccel(local, RotationOmega(manager, bodyId));
            var angularVelocity = manager.GetBodyMotion(bodyId, seconds).AngularVelocity;
            return DVector3.Cross(angularVelocity, DVector3.Cross(angularVelocity, local));
        }
    }
}
