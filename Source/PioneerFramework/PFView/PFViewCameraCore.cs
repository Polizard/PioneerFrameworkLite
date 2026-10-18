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
        /// <summary>俯仰夹取（与旧实现一致：5°–85°，避免万向锁/穿地）。</summary>
        public const float MinPitch = 5f;
        public const float MaxPitch = 85f;

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

        /// <summary>俯仰夹取。</summary>
        public static float ClampPitch(float pitchDeg)
            => Mathf.Clamp(pitchDeg, MinPitch, MaxPitch);

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
