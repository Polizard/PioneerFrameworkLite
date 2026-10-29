using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 恒星渲染质量档位（2026-10-28 P2；用户口径：默认不削弱视觉，降级必须是玩家自选）。
    /// `VisualFirst` = 现状观感（P2 全质量路径）；后两档只降低日冕采样密度，不改相机/轨道/光球细节。
    /// </summary>
    public enum PFViewStellarQuality
    {
        /// <summary>视觉优先（默认）：P2 全质量，禁止降采样/降分辨率。</summary>
        VisualFirst = 0,

        /// <summary>均衡：日冕采样预算约 75%。</summary>
        Balanced = 1,

        /// <summary>性能优先：日冕采样预算约 50%（P3 的 RT 缩放也只允许在此档出现）。</summary>
        PerformanceFirst = 2,
    }

    /// <summary>
    /// 质量档位 → 步数预算的纯核（无 Unity/Verse；headless 可测）。
    /// shader 把 `_StellarParams.x / 64` 当质量倍率；因此 64/48/32 对应 1.0/0.75/0.5。
    /// </summary>
    internal static class PFViewStellarQualityCore
    {
        public const int VisualFirstSteps = 64;
        public const int BalancedSteps = 48;
        public const int PerformanceFirstSteps = 32;

        public static int StepsFor(PFViewStellarQuality quality)
        {
            switch (quality)
            {
                case PFViewStellarQuality.Balanced: return BalancedSteps;
                case PFViewStellarQuality.PerformanceFirst: return PerformanceFirstSteps;
                default: return VisualFirstSteps;
            }
        }

        /// <summary>存档/配置里的旧值或损坏值一律回退到视觉优先。</summary>
        public static PFViewStellarQuality Sanitize(int stored)
            => stored is (int)PFViewStellarQuality.Balanced or (int)PFViewStellarQuality.PerformanceFirst
                ? (PFViewStellarQuality)stored
                : PFViewStellarQuality.VisualFirst;
    }

    public partial class PioneerFrameworkLiteSettings
    {
        /// <summary>
        /// 是否启用 P2 的新版恒星渲染（解析光球 + 薄日冕壳）。
        /// 默认 **false**：默认渲染 = 用户认可的旧视觉（legacy shader）；新版只作为玩家自选项保留。
        /// </summary>
        public bool useFastStellarRenderer;

        /// <summary>新版渲染的日冕采样档位；只在 `useFastStellarRenderer=true` 时生效。</summary>
        public PFViewStellarQuality stellarQuality = PFViewStellarQuality.VisualFirst;

        partial void ExposeStellarSettings();

        partial void ExposeStellarSettings()
        {
            Scribe_Values.Look(ref useFastStellarRenderer, "useFastStellarRenderer", false);
            int stored = (int)stellarQuality;
            Scribe_Values.Look(ref stored, "stellarQuality", (int)PFViewStellarQuality.VisualFirst);
            stellarQuality = PFViewStellarQualityCore.Sanitize(stored);
        }
    }
}
