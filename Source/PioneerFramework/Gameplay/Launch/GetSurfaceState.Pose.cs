using System;
using PioneerFramework.Celestial;

namespace PioneerFramework.Launch
{
    public static partial class GetSurfaceState
    {
        // 2026-11-11锛堢敤鎴锋巿鏉冿級锛氭棦鏈夋槦鍘嗘ˉ涔熻蛋鎻愪緵鏂瑰Э鎬併€?
        // 鍓嶆彁鍙樹簡锛氭ˉ鍦?existing.circular.v2 涓嬩骇鍑?*鏉冨▉鑷浆**锛堣酱/鐩镐綅/閫嗚/娼睈閿佸畾锛夛紝
        // 涓嶅啀鍙湁鏍囬噺 dayLengthTicks 鈬?瀹屾暣鐨?fixed鈫抜nertial 濮挎€佷笌鎯€ц閫熷害瀵规ˉ鍚屾牱鍙敤銆?
        // 瀵硅酱=+Y銆佺浉浣?0 鐨勬棦鏈夊唴瀹癸紝鍥涘厓鏁拌矾寰勪笌鏃?Ry(鈭捪塼) 閫愬紡绛変环锛圕ross(蠅,p) == 蠅路(鈭抪_z,0,p_x)锛夈€?
        // 鏃?Y 杞寸畻寮忓彧鍦?*娌℃湁杩愯鏃?*鐨勫厹搴曢噷淇濈暀锛圧otationOmega锛夈€?
        private static bool UsesProviderPose(CelestialBodyManager? manager)
            => manager?.TryGetCelestialRuntime(out _) == true;

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
