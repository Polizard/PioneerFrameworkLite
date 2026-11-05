namespace PioneerFramework
{
    /// <summary>
    /// **PFView 参考系档位**（2026-10-12「PFView 参考系按场景选」）。
    /// · **惯性系**（默认，= 历史行为）：飞船/天体的轨道运动看得见，地面随自转"掠过去"
    ///   —— 地球贴地时地面 **523 m/物理秒（≈每实时帧 523 m）**，近景根本没法看；
    /// · **固连系**：整套场景乘"绝对系 → 固连系"的旋转 ⇒ **地面静止**、飞船相对地面动
    ///   —— 贴地 / 低空 / 悬浮 / 起降落位这些场景要的就是这个。
    /// </summary>
    public enum PFViewFrameMode
    {
        /// <summary>自动（默认）：按相机高度判 —— 贴地/低空用固连系，其余用惯性系。</summary>
        Auto = 0,
        /// <summary>强制惯性系（历史行为；对比用）。</summary>
        Inertial = 1,
        /// <summary>强制固连系（贴地近景用；对比用）。</summary>
        Fixed = 2,
    }

    /// <summary>
    /// **参考系档位的纯核心**：自动档的判据 + 阈值（旋钮集中一处，可 headless 钉）。
    ///
    /// 阈值口径：**大气厚度的一半**（与大气壳/天空同一条线 —— 壳正好落在"天空刚黑"处）
    /// 与 **100 km 低空兜底**取大者：有大气的地球档 ⇒ `max(50 km, 100 km) = 100 km`；
    /// 无大气天体（月球）⇒ 100 km 兜底（月球上 100 km 已经算"低空"）。
    /// ⚠ 判据用**相机**高度（不是飞船高度）：PFView 的镜头本来就在船附近（≤3 km），
    /// 用相机高度才与"看到什么"一致。
    /// </summary>
    public static class PFViewFrameCore
    {
        /// <summary>无大气天体的"低空"兜底阈值（米）。</summary>
        public const double LowAltitudeFallbackMeters = 100_000.0;

        /// <summary>大气口径的系数（= 厚度的一半，与大气壳同一处口径）。</summary>
        public const double AtmosphereHalfFactor = 0.5;

        /// <summary>自动档的切换阈值（米）：`max(大气厚度 × 0.5, 100 km)`。</summary>
        public static double FixedThresholdMeters(double atmosphereThicknessMeters)
        {
            if (atmosphereThicknessMeters <= 0.0) return LowAltitudeFallbackMeters;
            double half = atmosphereThicknessMeters * AtmosphereHalfFactor;
            return half > LowAltitudeFallbackMeters ? half : LowAltitudeFallbackMeters;
        }

        /// <summary>该用固连系吗。`Auto` 档按相机高度与阈值判；显式档直接生效（对比/调试用）。</summary>
        public static bool UseFixedFrame(PFViewFrameMode mode, double cameraAltitudeMeters, double atmosphereThicknessMeters)
            => mode switch
            {
            PFViewFrameMode.Fixed => true,
            PFViewFrameMode.Inertial => false,
            _ => cameraAltitudeMeters < FixedThresholdMeters(atmosphereThicknessMeters),
            };

        /// <summary>档位名（UI 与读数共用一份词）。</summary>
        public static string ModeName(PFViewFrameMode mode)
            => mode switch
            {
            PFViewFrameMode.Inertial => "惯性",
            PFViewFrameMode.Fixed => "固连",
            _ => "自动",
            };
    }
}
