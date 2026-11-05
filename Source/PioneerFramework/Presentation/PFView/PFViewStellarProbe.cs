namespace PioneerFramework
{
    /// <summary>
    /// P0 恒星性能探针的**开关状态**（2026-10-28；与 `MainTabWindow_PFView` 的静态构造器解耦）。
    ///
    /// 为什么单独一个类：`MainTabWindow_PFView` 有显式静态构造器（`InitStaticResources` 会建 Mesh，
    /// headless 测试里需要 GPU/Vulkan 依赖）⇒ 测试/工具**只读开关**时不该触发那个 cctor。
    /// 探针状态放这里后，headless 单测可直接读写默认值/Reset，不碰窗口资源。
    ///
    /// 所有默认值 = 现状（surface/halo 开、步数 0=64、RT 1.0、探针不记录）。
    /// </summary>
    internal static class PFViewStellarProbe
    {
        /// <summary>是否绘制恒星 surface pass（不透明光球/日冕体积）。默认开 = 现状。</summary>
        internal static bool SurfacePass = true;

        /// <summary>是否提交恒星 halo pass（透明日冕）。默认开 = 现状。</summary>
        internal static bool HaloPass = true;

        /// <summary>步数预算覆盖；0 = 用 `PFViewStellarVisualCore.SurfaceSteps`（=64，现状）。</summary>
        internal static int StepBudget;

        /// <summary>渲染纹理缩放；1.0 = 现状。0.5 时像素数约 1/4（判填充率用）。</summary>
        internal static float RenderScale = 1f;

        /// <summary>探针是否在记录（由 PFView 调试窗 PostOpen/PreClose 置位）。</summary>
        internal static bool Active;

        /// <summary>恢复全部默认（= 现状）；测量结束必须调用。</summary>
        internal static void ResetDefaults()
        {
            SurfacePass = true;
            HaloPass = true;
            StepBudget = 0;
            RenderScale = 1f;
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
