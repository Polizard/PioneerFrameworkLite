using System;

namespace PioneerFramework
{
    /// <summary>大世界起飞过场的纯数学路径；输入、输出均为系统系米制坐标。</summary>
    public static class LaunchTransitPath
    {
        public static double Progress(int startTick, int durationTicks, int nowTick)
        {
            if (durationTicks <= 0) return 1.0;
            return Math.Max(0.0, Math.Min(1.0, ((double)nowTick - startTick) / durationTicks));
        }

        /// <summary>
        /// 起点方向随当前天体中心移动；系统系目标保持不变。
        /// 目标因天体移动进入球体时，仅把过场图标停在表面，结算仍需拒绝该目标。
        /// </summary>
        public static DVector3 PositionFromMovingBody(DVector3 launchBodyCenter,
            DVector3 currentBodyCenter, DVector3 start, DVector3 target, double progress)
        {
            DVector3 startRelative = start - launchBodyCenter;
            DVector3 targetRelative = target - currentBodyCenter;
            double surfaceRadius = startRelative.magnitude;
            if (targetRelative.magnitude < surfaceRadius)
            {
                DVector3 direction = targetRelative.sqrMagnitude > 1e-18
                    ? targetRelative.normalized : startRelative.normalized;
                target = currentBodyCenter + direction * surfaceRadius;
            }
            return Position(currentBodyCenter, currentBodyCenter + startRelative, target, progress);
        }

        public static DVector3 Position(DVector3 bodyCenter, DVector3 start, DVector3 target, double progress)
        {
            if (progress <= 0.0) return start;
            if (progress >= 1.0) return target;

            DVector3 from = start - bodyCenter;
            DVector3 to = target - bodyCenter;
            double fromRadius = from.magnitude;
            double toRadius = to.magnitude;
            if (fromRadius < 1e-9 || toRadius < 1e-9)
                return start + (target - start) * progress;

            double eased = progress * progress * (3.0 - 2.0 * progress);
            DVector3 fromDir = from / fromRadius;
            DVector3 toDir = to / toRadius;
            double dot = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(fromDir, toDir)));
            DVector3 direction;

            if (dot > 0.999999)
            {
                direction = (fromDir * (1.0 - eased) + toDir * eased).normalized;
            }
            else if (dot < -0.999999)
            {
                DVector3 axis = Math.Abs(fromDir.x) < 0.9 ? new DVector3(1, 0, 0) : new DVector3(0, 1, 0);
                DVector3 tangent = DVector3.Cross(fromDir, axis).normalized;
                direction = fromDir * Math.Cos(Math.PI * eased) + tangent * Math.Sin(Math.PI * eased);
            }
            else
            {
                double angle = Math.Acos(dot);
                double divisor = Math.Sin(angle);
                direction = fromDir * (Math.Sin((1.0 - eased) * angle) / divisor)
                    + toDir * (Math.Sin(eased * angle) / divisor);
            }

            double radius = fromRadius + (toRadius - fromRadius) * eased;
            return bodyCenter + direction.normalized * radius;
        }
    }
}
