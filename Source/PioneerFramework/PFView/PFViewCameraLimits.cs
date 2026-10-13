using UnityEngine;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 的**取景限制**（2026-10-10 用户口径：
    /// 「摄像机看向 Group 时要有距离限制，平移不能超过 2km，拉远不能超过 3km，靠近不能近于 300m」）。
    ///
    /// 三条口径落成三条夹取（都只在"看向编队"时生效）：
    /// · **平移**：焦点离编队的偏移 ≤ `GroupPanMaxMeters`（2 km）—— 超了按方向缩回上限，不丢方向；
    /// · **拉远**：轨道距离 ≤ `GroupOrbitMaxMeters`（3 km）；
    /// · **靠近**：轨道距离 ≥ `GroupOrbitMinMeters`（300 m）。
    /// ⚠ 看**行星/星系**那套远尺度取景（几十万~上亿单位）**不受影响** —— 那是另一档相机距离，别一起夹。
    ///
    /// 纯算术（`Mathf`/`Vector3` 值类型）⇒ headless 可测；调用方负责给 `metersToView`（本文件不碰任何 Unity 物体）。
    /// </summary>
    internal static class PFViewCameraLimits
    {
        /// <summary>焦点平移上限（米）：看向编队时，焦点不许离编队超过这么远。</summary>
        public const float GroupPanMaxMeters = 2000f;

        /// <summary>轨道拉远上限（米）。</summary>
        public const float GroupOrbitMaxMeters = 3000f;

        /// <summary>轨道靠近下限（米）。</summary>
        public const float GroupOrbitMinMeters = 300f;

        /// <summary>选中编队时的**默认**轨道距离（米）。必须落在 [靠近下限, 拉远上限] 内（有一条用例钉这个自洽性）。</summary>
        public const float GroupInitOrbitMeters = 1000f;

        /// <summary>
        /// 轨道距离夹取（view 单位）。`focusedOnGroup = false`（看行星）⇒ **原样返回**（远尺度取景不受影响）。
        /// </summary>
        public static float ClampOrbitDistance(float distanceView, float metersToView, bool focusedOnGroup)
        {
            if (!focusedOnGroup) return distanceView;
            return Mathf.Clamp(distanceView, GroupOrbitMinMeters * metersToView, GroupOrbitMaxMeters * metersToView);
        }

        /// <summary>
        /// 焦点平移夹取（view 单位）：超限就**按方向缩到上限**（保留方向；`magnitude ≈ 0` 的退化输入原样返回，不产生 NaN）。
        /// </summary>
        public static Vector3 ClampPanOffset(Vector3 offsetView, float metersToView, bool focusedOnGroup)
        {
            if (!focusedOnGroup) return offsetView;
            float max = GroupPanMaxMeters * metersToView;
            float m = offsetView.magnitude;
            return m > max && m > 1e-6f ? offsetView * (max / m) : offsetView;
        }
    }
}
