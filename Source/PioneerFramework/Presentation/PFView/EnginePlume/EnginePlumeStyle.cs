// ⛔【NOSTEPANYMORE】表现层文件（PFView 引擎喷焰配色/常量）。不写任何后端字段。
//    若下一步要改 Base/** 等后端文件，按 BACKEND-LOCK.md 停机请示，不得顺手改。
// ─────────────────────────────────────────────────────────────────────────────
using UnityEngine;

namespace PioneerFramework
{
    /// <summary>
    /// 一种工质对应的四层颜色（头/尾/外辉光/喷口点），alpha 直接当亮度权重用。
    /// </summary>
    internal readonly struct EnginePlumeColors
    {
        public readonly Color head;
        public readonly Color tail;
        public readonly Color glow;
        public readonly Color dot;

        public EnginePlumeColors(Color head, Color tail, Color glow, Color dot)
        {
            this.head = head;
            this.tail = tail;
            this.glow = glow;
            this.dot = dot;
        }
    }

    /// <summary>
    /// **引擎喷焰的表现层常量与配色表**（2026-11-08，PFView 引擎视觉特效 P1）。
    ///
    /// 结构：
    /// · 工质 → 颜色桶的判定在纯核 EnginePlumeCore（可 headless 测）；
    /// · 本类只负责"颜色桶 → Unity Color / 片宽比例 / alpha"这些 GFX 口径。
    ///
    /// 纪律：
    /// · 不改 PropellantDef（后端锁 + 避免第三方工质无颜色）；未知工质回退化学色，只计数不报错。
    /// · 走加性混合（见 PF_ProjectileGlow 的 ShaderDef），因此 alpha 是亮度而不是"透明度"。
    /// </summary>
    internal static class EnginePlumeStyle
    {
        // 片形比例（相对喷口半径；喷口半径来自纯核的 AABB 近似）。
        public const float CoreHeadWidthFraction = 1.10f;
        public const float CoreTailWidthFraction = 2.00f;
        public const float GlowHeadWidthFraction = 2.40f;
        public const float GlowTailWidthFraction = 3.40f;
        public const float DotRadiusFraction = 1.40f;

        // 外辉光比焰心短，避免尾部一大团。
        public const float GlowLengthFraction = 0.55f;

        // 各层 alpha（= 加性亮度权重）。
        public const float CoreHeadAlpha = 0.85f;
        public const float CoreTailAlpha = 0.25f;
        public const float GlowHeadAlpha = 0.22f;
        public const float GlowTailAlpha = 0.06f;
        public const float DotAlpha = 0.80f;

        // 闪烁幅度（长度/亮度乘子围绕 1 摆动）。
        public const float MainFlickerAmplitude = 0.08f;
        public const float RcsFlickerAmplitude = 0.25f;

        // 大气内轻微暖化（只对化学/未知生效，看起来像"氧气助燃"）。
        private static readonly Color AtmosphereTint = new(1.00f, 0.62f, 0.28f);
        private const float AtmosphereTintAmount = 0.12f;

        private static readonly EnginePlumeColors Chemical = new(
            new Color(1.000f, 0.953f, 0.839f, 1f),
            new Color(1.000f, 0.478f, 0.122f, 1f),
            new Color(1.000f, 0.700f, 0.300f, 1f),
            new Color(1.000f, 0.930f, 0.800f, 1f));

        private static readonly EnginePlumeColors Hydrogen = new(
            new Color(0.918f, 0.965f, 1.000f, 1f),
            new Color(0.435f, 0.714f, 1.000f, 1f),
            new Color(0.650f, 0.850f, 1.000f, 1f),
            new Color(0.900f, 0.960f, 1.000f, 1f));

        private static readonly EnginePlumeColors Xenon = new(
            new Color(0.910f, 0.925f, 1.000f, 1f),
            new Color(0.400f, 0.470f, 1.000f, 1f),
            new Color(0.620f, 0.700f, 1.000f, 1f),
            new Color(0.880f, 0.900f, 1.000f, 1f));

        private static readonly EnginePlumeColors Hydrazine = new(
            new Color(0.965f, 0.949f, 1.000f, 1f),
            new Color(0.557f, 0.525f, 0.910f, 1f),
            new Color(0.780f, 0.720f, 1.000f, 1f),
            new Color(0.940f, 0.920f, 1.000f, 1f));

        private static readonly EnginePlumeColors Fallback = new(
            new Color(1.000f, 0.940f, 0.820f, 1f),
            new Color(1.000f, 0.540f, 0.190f, 1f),
            new Color(1.000f, 0.690f, 0.330f, 1f),
            new Color(1.000f, 0.900f, 0.760f, 1f));

        public static EnginePlumeColors ColorsFor(int bucket, bool inAtmosphere)
        {
            EnginePlumeColors c = bucket switch
            {
                EnginePlumeCore.BucketChemical => Chemical,
                EnginePlumeCore.BucketHydrogen => Hydrogen,
                EnginePlumeCore.BucketXenon => Xenon,
                EnginePlumeCore.BucketHydrazine => Hydrazine,
                _ => Fallback,
            };
            if (!inAtmosphere) return c;
            bool warm = bucket == EnginePlumeCore.BucketChemical || bucket == EnginePlumeCore.BucketUnknown;
            if (!warm) return c;
            return new EnginePlumeColors(
                Color.Lerp(c.head, AtmosphereTint, AtmosphereTintAmount),
                Color.Lerp(c.tail, AtmosphereTint, AtmosphereTintAmount),
                Color.Lerp(c.glow, AtmosphereTint, AtmosphereTintAmount),
                c.dot);
        }

        public static float FlickerAmplitude(EnginePlumeRole role)
            => role == EnginePlumeRole.Rcs ? RcsFlickerAmplitude : MainFlickerAmplitude;

        /// <summary>把纯核给的"亮度乘子"缩放到对应角色的闪烁幅度：flicker ∈ [0.92,1.08] → 幅度可调。</summary>
        public static float ApplyFlicker(double flicker, EnginePlumeRole role)
        {
            double centered = flicker - 1.0;          // [-0.08, 0.08]
            double scaled = centered / 0.08 * FlickerAmplitude(role);
            return (float)(1.0 + scaled);
        }

        public static Color WithAlpha(Color c, float alpha)
        {
            c.a = alpha;
            return c;
        }
    }
}