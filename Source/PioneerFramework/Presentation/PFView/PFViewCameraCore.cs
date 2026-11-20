using System;
using System.Collections.Generic;
using UnityEngine;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 的**焦点类型**（2026-10-18 视图系统 v2）：
    /// · Body   看行星/恒星（远景档，距离按天体半径与 FOV 取）；
    /// · Group  看编队（战术档，300 m–3 km，用户 2026-10-10 口径）；
    /// · Frozen 焦点对象丢失后的冻结位置（不移动相机，等玩家决定）。
    /// ⚠ 这是**相机焦点**，与 selectedGroupId（详情面板/射手）解耦——见 MainTabWindow_PFView.Camera.cs。
    /// </summary>
    internal enum PFViewFocusKind
    {
        Body = 0,
        Group = 1,
        Frozen = 2,
    }

    /// <summary>
    /// 纯核：焦点 + 相机取景数学。无 Unity 物体、无 Verse 状态 ⇒ headless 可测。
    /// 常量一律**引用** PFViewCameraLimits / PFViewFrameCore，不复制（防两套口径）。
    /// </summary>
    internal static class PFViewCameraCore
    {
        /// <summary>默认俯仰（旧行为 45°）。</summary>
        public const float DefaultPitch = 45f;

        /// <summary>远景档最大距离（view 单位）：必须 < FarClip=1e8，否则行星被远裁剔除成空屏（U25）。</summary>
        public const float MaxBodyDistanceView = 8e7f;

        /// <summary>冻结焦点距离下限/上限（view 单位）：防 NaN、防一拉就飞。</summary>
        public const float FrozenMinDistanceView = 10f;
        public const float FrozenMaxDistanceView = 1e7f;

        /// <summary>缩放每格比例（0.1 ⇒ 每格 ×0.9 或 ×1/0.9）。
        /// 方向（2026-10-18 用户再次切换）：**滚轮向上 = 拉远**，向下 = 拉近。</summary>
        public const float WheelSensitivity = 0.1f;

        /// <summary>滚轮单帧限幅：触控板/高分辨率滚轮 delta 可远大于 1（U8）。</summary>
        public static float ClampWheelDelta(float deltaY)
            => Mathf.Clamp(deltaY, -3f, 3f);

        /// <summary>滚轮 → 新距离（乘法缩放；delta 先限幅）。
        /// delta &gt; 0（滚轮向上）= 距离变大（拉远）；delta &lt; 0 = 拉近。</summary>
        public static float WheelDistance(float distanceView, float deltaY)
            => distanceView * Mathf.Pow(1f / (1f - WheelSensitivity), ClampWheelDelta(deltaY));

        /// <summary>同向滚动累加目标；反向输入从当前画面距离起算，立即改变缩放方向。</summary>
        public static float WheelTargetDistance(float targetDistance, float displayedDistance, float deltaY)
        {
            if (!float.IsFinite(deltaY)) return targetDistance;
            if (float.IsFinite(displayedDistance) && displayedDistance > 0f
                && ((deltaY > 0f && targetDistance < displayedDistance)
                    || (deltaY < 0f && targetDistance > displayedDistance)))
                targetDistance = displayedDistance;
            return WheelDistance(targetDistance, deltaY);
        }

        /// <summary>
        /// 用真实帧时间平滑视距；在对数距离上指数收敛，近景/行星档共享比例手感。
        /// 0.065 s 时间常数使约 95% 的缩放在 0.2 s 内完成，无速度积累和过冲。
        /// 目标已按当前镜头模式限幅；显示值仍可来自被用户打断的跨档过渡。
        /// </summary>
        public static float SmoothZoomDistance(float current, float target, float dt)
        {
            if (!float.IsFinite(target) || target <= 0f) return current;
            if (!float.IsFinite(current) || current <= 0f) return target;
            if (!float.IsFinite(dt) || dt <= 0f) return current;

            double blend = 1.0 - Math.Exp(-dt / 0.065);
            float distance = (float)(current * Math.Exp(Math.Log((double)target / current) * blend));
            if (Math.Abs(distance - target) <= target * 1e-6f) return target;
            return Mathf.Clamp(distance, Math.Min(current, target), Math.Max(current, target));
        }

        /// <summary>取裁剪范围内最靠前的球面。射线方向的相机空间 Z 分量为 1。</summary>
        public static int PickCelestialBody(Vector3 cameraPosition, Vector3 direction, float nearDepth, float farDepth,
            IReadOnlyList<(int BodyId, Vector3 Center, float Radius)> spheres)
        {
            DVector3 ray = (DVector3)direction;
            double inverseLengthSquared = 1.0 / ray.sqrMagnitude;
            double bestDepth = farDepth;
            int bestBody = -1;
            foreach (var sphere in spheres)
            {
                DVector3 offset = (DVector3)sphere.Center - (DVector3)cameraPosition;
                // 叉积直接求到射线的垂距，避免远处小球在两个大平方数相减时丢失精度。
                double perpendicularSquared = DVector3.Cross(offset, ray).sqrMagnitude * inverseLengthSquared;
                double halfChordSquared = (double)sphere.Radius * sphere.Radius - perpendicularSquared;
                if (halfChordSquared < 0.0) continue;

                double depth = DVector3.Dot(offset, ray) * inverseLengthSquared
                    - Math.Sqrt(halfChordSquared * inverseLengthSquared);
                if (!(depth >= nearDepth && depth < bestDepth)) continue;

                bestDepth = depth;
                bestBody = sphere.BodyId;
            }
            return bestBody;
        }

        /// <summary>
        /// 相机不许钻进天体：若相机到球心的距离 &lt; 半径×margin，沿径向推回球面外。
        /// 解决 2026-10-18 用户报「缩放时摄像机进入星体内部」。
        /// </summary>
        public static Vector3 ClampCameraOutsideSphere(Vector3 cameraPos, Vector3 center, float radiusView, float margin = 1.002f)
        {
            if (radiusView <= 0f) return cameraPos;
            float minDist = radiusView * margin;
            Vector3 d = cameraPos - center;
            float dist = d.magnitude;
            if (dist >= minDist) return cameraPos;
            Vector3 dir = dist > 1e-4f ? d / dist : Vector3.up;
            return center + dir * minDist;
        }

        /// <summary>
        /// 近裁随「离最近天体表面」收窄：否则看行星最近档时 near=0.35×distance 会把整颗星球近侧裁掉，
        /// 看起来像摄像机钻进了星体内部。表面余量越小，near 越小（保表面在近裁面之外）。
        /// </summary>
        public static float NearClipForCamera(float distanceView, float surfaceGapView, float nearMin, float nearMax)
        {
            float near = distanceView * 0.35f;
            if (surfaceGapView > 0f) near = Mathf.Min(near, surfaceGapView * 0.8f);
            return Mathf.Clamp(near, nearMin, nearMax);
        }

        internal static bool SphereIntersectsFrustum(Vector3 center, float radius, float nearClip, float farClip,
            float verticalFovDegrees, float aspect)
        {
            float r = Mathf.Max(radius, 0.01f);
            if (center.z + r < nearClip || center.z - r > farClip) return false;

            float halfFov = verticalFovDegrees * 0.5f * Mathf.Deg2Rad;
            float tanV = Mathf.Tan(halfFov);
            float tanH = tanV * aspect;
            // 侧面方程未归一化，需用各自的法线长度换算球半径。
            float horizontalMargin = r * Mathf.Sqrt(1f + tanH * tanH);
            float verticalMargin = r * Mathf.Sqrt(1f + tanV * tanV);
            return Mathf.Abs(center.x) <= center.z * tanH + horizontalMargin &&
                Mathf.Abs(center.y) <= center.z * tanV + verticalMargin;
        }

        /// <summary>
        /// 俯仰按整圈归一化：**所有焦点**（天体 / 编队 / 冻结）都可越过正上、正下方连续环绕。
        /// 2026-11-20 用户：旧的「天体 5°–85°」限位导致永远看不到行星与舰船的下半部分，取消；
        /// 上方向由 <see cref="OrbitUp" /> 连续给出，越过极点不会翻转。
        /// </summary>
        public static float ResolvePitch(float pitchDeg)
            => float.IsFinite(pitchDeg) ? WrapAngle(pitchDeg) : DefaultPitch;

        /// <summary>角度整圈归一化到 (-180, 180]。</summary>
        internal static float WrapAngle(float angleDeg)
        {
            float wrapped = angleDeg % 360f;
            return wrapped > 180f ? wrapped - 360f : wrapped < -180f ? wrapped + 360f : wrapped;
        }

        /// <summary>按焦点类型夹取距离。Group 档沿用 PFViewCameraLimits（用户口径，只许加强）。</summary>
        public static float ClampDistance(PFViewFocusKind kind, float distanceView, float metersToView, float bodyRadiusView)
        {
            switch (kind)
            {
                case PFViewFocusKind.Group:
                    return PFViewCameraLimits.ClampOrbitDistance(distanceView, metersToView, true);
                case PFViewFocusKind.Frozen:
                    return Mathf.Clamp(distanceView, FrozenMinDistanceView, FrozenMaxDistanceView);
                default:
                    float min = Mathf.Max(bodyRadiusView * 1.2f, FrozenMinDistanceView);
                    return Mathf.Clamp(distanceView, min, MaxBodyDistanceView);
            }
        }

        /// <summary>
        /// 换焦点时的**距离投影**：保留朝向，只把旧距离夹进新档合法区间。
        /// 这是「换焦点不重置、不瞬跳距离」的第一半（另一半是过渡，见 Camera.cs）。
        /// </summary>
        public static float ProjectDistance(PFViewFocusKind fromKind, PFViewFocusKind toKind,
            float distanceView, float metersToView, float bodyRadiusView)
        {
            // 从远景档进入编队档：若旧距离比 3 km 还远，直接落到 3 km（再由过渡滑过去），
            // 不采用「默认 1 km」——1 km 是首次聚焦的默认值，不是每次换焦点都重置。
            if (toKind == PFViewFocusKind.Group && fromKind != PFViewFocusKind.Group)
                distanceView = PFViewCameraLimits.GroupOrbitMaxMeters * metersToView;

            return ClampDistance(toKind, distanceView, metersToView, bodyRadiusView);
        }

        /// <summary>
        /// 天体档默认取景：按包围球求距离，保证整个天体落进 FOV（U11）。
        /// d = R / sin(min(半垂直FOV, 半水平FOV)) × margin。
        /// </summary>
        public static float DefaultBodyDistanceView(float radiusView, float fovDegVertical, float aspect, float margin = 1.05f)
        {
            float halfV = fovDegVertical * 0.5f * Mathf.Deg2Rad;
            float halfH = Mathf.Atan(Mathf.Tan(halfV) * Mathf.Max(aspect, 0.01f));
            float half = Mathf.Min(halfV, halfH);
            float sin = Mathf.Max(Mathf.Sin(half), 1e-4f);
            return Mathf.Max(radiusView / sin * margin, radiusView * 1.2f);
        }

        /// <summary>编队档默认距离（首次聚焦）：1 km（PFViewCameraLimits.GroupInitOrbitMeters）。</summary>
        public static float DefaultGroupDistanceView(float metersToView)
            => PFViewCameraLimits.GroupInitOrbitMeters * metersToView;

        /// <summary>轨道方向单位向量（相机相对焦点的球坐标）。</summary>
        public static Vector3 OrbitDir(float yawDeg, float pitchDeg)
        {
            float yR = yawDeg * Mathf.Deg2Rad;
            float pR = pitchDeg * Mathf.Deg2Rad;
            float cP = Mathf.Cos(pR);
            return new Vector3(cP * Mathf.Sin(yR), Mathf.Sin(pR), cP * Mathf.Cos(yR));
        }

        /// <summary>随轨道俯仰连续旋转的上方向，避免越过正上/正下方时固定 world-up 翻转。</summary>
        public static Vector3 OrbitUp(float yawDeg, float pitchDeg)
        {
            float yR = yawDeg * Mathf.Deg2Rad;
            float pR = pitchDeg * Mathf.Deg2Rad;
            float sP = Mathf.Sin(pR);
            return new Vector3(-sP * Mathf.Sin(yR), Mathf.Cos(pR), -sP * Mathf.Cos(yR));
        }

        /// <summary>船体局部 +Z 为船头；轨道坐标 +Z 转向机尾，+Y 保持船体上方。</summary>
        public static Quaternion GroupOrbitFrame(Quaternion shipRotationInView)
            => shipRotationInView * new Quaternion(0, 1, 0, 0);

        /// <summary>
        /// **速度正向**的编队第三人称帧（2026-10-18 用户口径）：相机前向 = 速度方向。
        /// 做法：取船体帧，绕**最小旋转**把船头(+Z)搬到速度方向，再套 <see cref="GroupOrbitFrame" />
        /// 翻到机尾 —— 滚转基准仍取船体上方向（最小旋转不会绕前向轴额外加滚转；速度与船头有
        /// 俯仰差时上方向随之过渡），只有前向被换成速度。
        /// 速度为零/非有限 ⇒ 原样退回船体帧（悬停/停靠不产生无意义朝向）。
        ///
        /// ⚠ 只用**受管**四元数算术：`Quaternion.FromToRotation/AngleAxis/LookRotation` 在 Unity 引用
        /// 程序集里是 ECall，headless 一调即抛（同 `ViewFrame.ToViewRotation` 的纪律）⇒ 这里自己算。
        /// </summary>
        public static Quaternion GroupVelocityOrbitFrame(Quaternion hullRotationInView, Vector3 velocityInView)
        {
            float max = Mathf.Max(Mathf.Abs(velocityInView.x), Mathf.Max(Mathf.Abs(velocityInView.y), Mathf.Abs(velocityInView.z)));
            if (!float.IsFinite(max) || max <= 1e-4f) return GroupOrbitFrame(hullRotationInView);

            // 先按最大分量缩放再单位化：速度量级 1e4 m/s 时平方不溢出。
            Vector3 scaled = velocityInView / max;
            float length = Mathf.Sqrt(scaled.x * scaled.x + scaled.y * scaled.y + scaled.z * scaled.z);
            if (!float.IsFinite(length) || length <= 1e-4f) return GroupOrbitFrame(hullRotationInView);
            Vector3 target = scaled / length;

            Vector3 nose = hullRotationInView * Vector3.forward;
            Quaternion align = MinimalRotation(nose, target, hullRotationInView * Vector3.up);
            return GroupOrbitFrame(align * hullRotationInView);
        }

        /// <summary>把单位向量 <paramref name="from" /> 转到 <paramref name="to" /> 的最小旋转四元数。
        /// 180° 反平行时绕 <paramref name="fallbackUp" /> 翻（保持滚转基准）；退化轴回落到世界 +Y。</summary>
        internal static Quaternion MinimalRotation(Vector3 from, Vector3 to, Vector3 fallbackUp)
        {
            float dot = Mathf.Clamp(Vector3.Dot(from, to), -1f, 1f);
            if (dot >= 1f - 1e-6f) return Quaternion.identity;
            if (dot <= -1f + 1e-6f)
            {
                Vector3 axis = fallbackUp;
                float axisLength = Mathf.Sqrt(axis.x * axis.x + axis.y * axis.y + axis.z * axis.z);
                if (!float.IsFinite(axisLength) || axisLength <= 1e-6f) axis = Vector3.up;
                else axis /= axisLength;
                return UnitRotation(new Quaternion(axis.x, axis.y, axis.z, 0f));
            }
            Vector3 cross = Vector3.Cross(from, to);
            return UnitRotation(new Quaternion(cross.x, cross.y, cross.z, 1f + dot));
        }

        /// <summary>轨道后方向和上方向的正交基；仅使用受管四元数算术。</summary>
        public static Quaternion OrbitRotation(float yawDeg, float pitchDeg, Quaternion frame)
        {
            float y = yawDeg * Mathf.Deg2Rad * .5f, p = -pitchDeg * Mathf.Deg2Rad * .5f;
            float sy = Mathf.Sin(y), cy = Mathf.Cos(y), sp = Mathf.Sin(p), cp = Mathf.Cos(p);
            return frame * new Quaternion(cy * sp, sy * cp, -sy * sp, cy * cp);
        }

        internal static Quaternion UnitRotation(Quaternion q)
        {
            float length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return length > 1e-6f && float.IsFinite(length)
                ? new(q.x / length, q.y / length, q.z / length, q.w / length) : Quaternion.identity;
        }

        /// <summary>视角模式过渡走最短四元数弧；q 与 -q 保持同一姿态。</summary>
        public static Quaternion InterpolateRotation(Quaternion from, Quaternion to, float progress)
        {
            float t = Mathf.Clamp01(progress);
            from = UnitRotation(from);
            to = UnitRotation(to);
            float dot = from.x * to.x + from.y * to.y + from.z * to.z + from.w * to.w;
            if (dot < 0)
            {
                to = new(-to.x, -to.y, -to.z, -to.w);
                dot = -dot;
            }
            float a = 1 - t, b = t;
            if (dot < .9995f)
            {
                float angle = Mathf.Acos(Mathf.Clamp(dot, 0, 1));
                float sine = Mathf.Sin(angle);
                a = Mathf.Sin((1 - t) * angle) / sine;
                b = Mathf.Sin(t * angle) / sine;
            }
            return UnitRotation(new(from.x * a + to.x * b, from.y * a + to.y * b,
                from.z * a + to.z * b, from.w * a + to.w * b));
        }

        /// <summary>跨视角或船体坐标系的过渡，位置与 up 使用同一旋转。</summary>
        public static (Vector3 Offset, Vector3 Up) InterpolateOrbit(Quaternion fromRotation, float fromDistance,
            Quaternion toRotation, float toDistance, float progress)
        {
            Quaternion rotation = InterpolateRotation(fromRotation, toRotation, progress);
            float distance = Mathf.Lerp(fromDistance, toDistance, Mathf.Clamp01(progress));
            return (rotation * Vector3.forward * distance, rotation * Vector3.up);
        }

        /// <summary>过渡按短角差插值朝向和视距，位置与上方向同源，倒置复位也不穿过焦点。</summary>
        public static (Vector3 Offset, Vector3 Up) InterpolateOrbit(float fromYaw, float fromPitch, float fromDistance,
            float toYaw, float toPitch, float toDistance, float progress)
        {
            float t = Mathf.Clamp01(progress);
            float yaw = fromYaw + WrapAngle(toYaw - fromYaw) * t;
            float pitch = fromPitch + WrapAngle(toPitch - fromPitch) * t;
            float distance = Mathf.Lerp(fromDistance, toDistance, t);
            return (OrbitDir(yaw, pitch) * distance, OrbitUp(yaw, pitch));
        }

        /// <summary>相机在 view space 中的位置（相对渲染原点）。</summary>
        public static Vector3 CameraViewPos(Vector3 panOffset, float yawDeg, float pitchDeg, float distanceView)
            => panOffset + OrbitDir(yawDeg, pitchDeg) * distanceView;

        /// <summary>平滑 0→1（smoothstep）。过渡用。</summary>
        public static float SmoothStep01(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// 临界阻尼步进（Unity SmoothDamp 同款公式；纯算术、无 ECall）。
        /// 用于 P1 的距离/焦点弹簧；P0 的换焦点过渡用显式插值，本函数留给后续统一。
        /// </summary>
        public static float CriticallyDampedStep(float current, float target, ref float velocity, float smoothTime, float dt)
        {
            smoothTime = Mathf.Max(0.0001f, smoothTime);
            float omega = 2f / smoothTime;
            float x = omega * dt;
            float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
            float change = current - target;
            float temp = (velocity + omega * change) * dt;
            velocity = (velocity - omega * temp) * exp;
            return target + (change + temp) * exp;
        }

        /// <summary>开火语义校验：必须有目标，且目标不能是射手自己（U0）。</summary>
        public static bool CanFire(int selectedGroupId, int targetGroupId)
            => selectedGroupId >= 0 && targetGroupId >= 0 && targetGroupId != selectedGroupId;
    }
}
