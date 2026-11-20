namespace PioneerFramework
{
    /// <summary>
    /// P0 恒星性能探针的**开关状态**（2026-10-28；与 `MainTabWindow_PFView` 的静态构造器解耦）。
    ///
    /// 为什么单独一个类：`MainTabWindow_PFView` 有显式静态构造器（`InitStaticResources` 会建 Mesh，
    /// headless 测试里需要 GPU/Vulkan 依赖）⇒ 测试/工具**只读开关**时不该触发那个 cctor。
    /// 探针状态放这里后，headless 单测可直接读写默认值/Reset，不碰窗口资源。
    ///
    /// 默认绘制光球和日冕，采样预算 64 步，PFView RT 缩放 1.0（原分辨率；0.75/0.5 仅调试降采样）。
    /// </summary>
    internal static class PFViewStellarProbe
    {
        internal const float DefaultRenderScale = 1f;
        internal const bool DefaultHeatCacheEnabled = true;

        /// <summary>
        /// S3-a 区域跳过积分（2026-11-07）：默认使用 legacy 观感的加速版 shader。
        /// 关闭时回退到 `PF_StellarVolume.frag.glsl`（逐字旧路径），供游戏内 A/B。
        /// </summary>
        internal const bool DefaultS3AEnabled = true;

        /// <summary>
        /// S3-b 星体专用半分辨率离屏（2026-11-20）：默认关闭的实验档。
        /// 开启后星体 surface/halo 渲染到半分辨率 RT，再以 composite 代理球合成回主 RT；
        /// 观感/遮挡未被游戏内验收，只作 A/B 用。
        /// </summary>
        internal const bool DefaultS3BEnabled = false;

        /// <summary>是否绘制恒星 surface pass（不透明光球/日冕体积）。默认开 = 现状。</summary>
        internal static bool SurfacePass = true;

        /// <summary>是否提交恒星 halo pass（透明日冕）。默认开 = 现状。</summary>
        internal static bool HaloPass = true;

        /// <summary>步数预算覆盖；0 = 用 `PFViewStellarVisualCore.SurfaceSteps`（=64，现状）。</summary>
        internal static int StepBudget;

        /// <summary>PFView 渲染纹理缩放。1.0 = 原分辨率（默认）；0.75/0.5 仅调试窗降采样 A/B（整窗会变软）。</summary>
        internal static float RenderScale = DefaultRenderScale;

        /// <summary>是否使用 S3-a 区域跳过积分 shader（默认 true；关闭 = 逐字 legacy shader）。</summary>
        internal static bool S3AEnabled = DefaultS3AEnabled;

        /// <summary>是否使用 S3-b 星体半分辨率离屏（默认 false；实验档，重开 PFView 生效）。</summary>
        internal static bool S3BEnabled = DefaultS3BEnabled;

        /// <summary>探针是否在记录（由 PFView 调试窗 PostOpen/PreClose 置位）。</summary>
        internal static bool Active;

        /// <summary>恢复默认绘制、步数、缩放和 S3-a 开关。</summary>
        internal static void ResetDefaults()
        {
            SurfacePass = true;
            HaloPass = true;
            StepBudget = 0;
            RenderScale = DefaultRenderScale;
            S3AEnabled = DefaultS3AEnabled;
            S3BEnabled = DefaultS3BEnabled;
        }

        /// <summary>玩家是否启用了 P2 新版渲染（默认 false = 旧视觉/legacy shader）。</summary>
        internal static bool FastRendererEnabled
            => PioneerFrameworkLiteMod.Settings?.useFastStellarRenderer == true;

        /// <summary>
        /// 本帧步数预算：默认走旧 shader 的 64（= 用户认可的旧视觉）；只有玩家开新版渲染时才按
        /// 新版档位降采样。探针覆盖优先。
        /// </summary>
        internal static int ResolveStepBudget()
        {
            if (StepBudget > 0) return System.Math.Clamp(StepBudget, 4, 64);
            if (!FastRendererEnabled) return PFViewStellarVisualCore.SurfaceSteps;   // legacy: 64
            var tier = PioneerFrameworkLiteMod.Settings?.stellarQuality ?? PFViewStellarQuality.VisualFirst;
            return PFViewStellarQualityCore.StepsFor(tier);
        }
    }
}
