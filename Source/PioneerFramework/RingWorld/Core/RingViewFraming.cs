using System;

namespace PioneerFramework.RingWorld
{
    /// <summary>
    /// 环世界全景取景（2026-10-17 电影感视觉升级，"完整环纳入本轮"的纯核部分）。
    ///
    /// 背景：世界相机垂直 FOV 只有 20°（`WorldCameraManager.CreateWorldCamera`），
    /// `WorldCameraDriver` 的 altitude 范围 125–1100。旧版进入环视图固定 altitude=600
    /// 且方向是 `Vector3.forward`（相机位于环平面内=边缘视角），环左右会被画面裁掉。
    ///
    /// 这里按"环半径 + 屏幕宽高比 + 俯仰"反算能装下整环的 altitude：
    /// - 横向约束：环的角半径 atan(R/d) ≤ 水平半 FOV × safety
    /// - 纵向约束：环投影椭圆的半短轴 ≈ 角半径 × sin(俯仰) ≤ 垂直半 FOV × safety
    /// 取两者更严者反解 d，再换算 altitude = d − sqrt(R² + halfWidth²)，最后 clamp 到
    /// 世界相机实际范围。face-on（俯仰 90°）在 20° FOV 下永远装不下（需要 altitude≈1495），
    /// 所以默认用 35° 俯仰的"英雄机位"；具体观感由人工游戏内验收。
    ///
    /// 纯核：不依赖 Verse/Unity，可直接被 Pure.Tests 链接。
    /// </summary>
    public static class RingViewFraming
    {
        /// <summary>世界相机垂直 FOV（与反编译的 WorldCameraManager 一致；那边是常量，无法引用）。</summary>
        public const float VerticalFovDegrees = 20f;

        /// <summary>默认全景俯仰角。"倾斜看环 + 行星"是本轮完整环构图的核心。</summary>
        public const float PresetElevationDegrees = 35f;

        public const float MinAltitude = 300f;
        public const float MaxAltitude = 1100f;

        /// <summary>取景安全系数：环边缘留一点余量，避免贴边。</summary>
        public const float Safety = .92f;

        /// <summary>
        /// 反算距离后的额外余量（1 = 理论刚好装下；&gt;1 留出安全边）。
        /// </summary>
        private const double DistanceMargin = 1.01;

        /// <summary>按几何与屏幕宽高比给出默认全景 altitude（已 clamp 到相机范围）。</summary>
        public static float PresetAltitude(RingGeometry geometry, float aspect)
        {
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));
            float required = RequiredAltitude(geometry.Radius, geometry.HalfWidth, aspect);
            return Math.Clamp(required, MinAltitude, MaxAltitude);
        }

        /// <summary>
        /// 反算能装下整环的 altitude（未 clamp 前的实数结果，便于测试断言）。
        /// aspect = 屏幕宽/高；aspect &lt;= 0 时按 16:9 处理。
        /// </summary>
        public static float RequiredAltitude(double ringRadius, double ringHalfWidth, float aspect,
            double elevationDegrees = PresetElevationDegrees, double safety = Safety)
        {
            if (!double.IsFinite(ringRadius) || ringRadius <= 0)
                throw new ArgumentOutOfRangeException(nameof(ringRadius));
            if (!double.IsFinite(ringHalfWidth) || ringHalfWidth < 0)
                throw new ArgumentOutOfRangeException(nameof(ringHalfWidth));
            if (!double.IsFinite(safety) || safety <= 0 || safety > 1)
                throw new ArgumentOutOfRangeException(nameof(safety));
            double aspectSafe = double.IsFinite(aspect) && aspect > 0 ? aspect : 16.0 / 9.0;

            double halfVertical = VerticalFovDegrees * .5 * Math.PI / 180.0;
            double halfHorizontal = Math.Atan(aspectSafe * Math.Tan(halfVertical));
            double sinElevation = Math.Sin(elevationDegrees * Math.PI / 180.0);

            double horizontalLimit = halfHorizontal * safety;
            double verticalLimit = sinElevation > 1e-6 ? halfVertical * safety / sinElevation : Math.PI;
            double angularLimit = Math.Min(horizontalLimit, verticalLimit);
            double distance = ringRadius / Math.Tan(angularLimit) * DistanceMargin;
            double bound = Math.Sqrt(ringRadius * ringRadius + ringHalfWidth * ringHalfWidth);
            return (float)(distance - bound);
        }

        /// <summary>
        /// 判断给定 altitude 下整环是否落在屏幕上（横向 + 纵向）。用于测试与验收自检。
        /// </summary>
        public static bool RingFits(double ringRadius, double ringHalfWidth, float altitude, float aspect,
            double elevationDegrees = PresetElevationDegrees, double safety = Safety)
        {
            double aspectSafe = double.IsFinite(aspect) && aspect > 0 ? aspect : 16.0 / 9.0;
            double halfVertical = VerticalFovDegrees * .5 * Math.PI / 180.0;
            double halfHorizontal = Math.Atan(aspectSafe * Math.Tan(halfVertical));
            double bound = Math.Sqrt(ringRadius * ringRadius + ringHalfWidth * ringHalfWidth);
            double distance = bound + altitude;
            if (distance <= ringRadius) return false;
            double angularRadius = Math.Atan(ringRadius / distance);
            double projectedVertical = Math.Abs(Math.Sin(elevationDegrees * Math.PI / 180.0)) * angularRadius;
            return angularRadius <= halfHorizontal * safety && projectedVertical <= halfVertical * safety;
        }

        /// <summary>
        /// 默认全景视线方向（世界系单位向量，环位于 XZ 赤道平面、+Y 为极轴）。
        /// 返回"从环心指向相机"的方向；相机看向环心。
        /// </summary>
        public static DVector3 PresetViewDirection(double elevationDegrees = PresetElevationDegrees)
        {
            double elevation = elevationDegrees * Math.PI / 180.0;
            return new DVector3(0, Math.Sin(elevation), Math.Cos(elevation));
        }
    }
}
