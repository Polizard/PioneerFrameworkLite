using PioneerFramework;
using RimWorld;
using UnityEngine;

namespace PioneerFramework.ShipSkin
{
    /// <summary>
    /// 舰船蒙皮的阵营配色（2026-10-19，用户裁定"极简硬表面：只板缝/倒角/阵营色"）。
    ///
    /// 口径：
    ///   · 底子是**中性钢灰**（贴图本身是灰的），阵营色只做**低饱和浸染**（默认 32%）——
    ///     直接乘满会把表面压成泥色，也会背离"极简"；
    ///   · 阵营解析走既有唯一入口 `FactionResolver.Get`（解析失败 ⇒ 用钢灰，不伪造敌对色）；
    ///   · 亮度保底：阵营色过暗时往白抬一点，保证船在夜面仍能读出形体（本库纪律：不许"看不见=没问题"）。
    /// </summary>
    public static class ShipSkinPalette
    {
        /// <summary>中性装甲钢灰（与 `ShipSkinPlates.png` 的灰底相乘后的目标观感）。</summary>
        private static readonly Color Steel = new(0.76f, 0.80f, 0.86f);

        /// <summary>阵营色浸染比例（0.32 = 能看出阵营，但仍是一块装甲）。</summary>
        private const float FactionMix = 0.32f;

        /// <summary>保底亮度：低于它就把 tint 往白抬（最多抬 50%）。</summary>
        private const float MinLuminance = 0.55f;

        /// <summary>无 Map 编队/导弹/有 Map 船体：按阵营算一个整体 tint。</summary>
        public static Color PartTint(int factionId)
        {
            Color tint = Steel;
            Faction? faction = FactionResolver.Get(factionId);
            if (faction != null)
                tint = Color.Lerp(Steel, faction.Color, FactionMix);
            return EnsureReadable(tint);
        }

        /// <summary>
        /// 亮度保底（纯函数、可测）：阵营色过暗时把 tint 往白抬（最多抬 50%），
        /// 保证"夜面还能读出船体轮廓"——这是本库"不许用沉默冒充没问题"的同款判据。
        /// </summary>
        public static Color EnsureReadable(Color tint)
        {
            float luminance = Luminance(tint);
            if (luminance >= MinLuminance) return tint;
            float lift = (MinLuminance - luminance) / MinLuminance * 0.5f;
            return Color.Lerp(tint, Color.white, lift);
        }

        /// <summary>感知亮度（Rec.601，与 `EnsureReadable` 同一口径）。</summary>
        public static float Luminance(Color c) => c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;

        /// <summary>
        /// 地图浮雕三档（结构梁/舱室/装甲）保持自己的明度层次，再向阵营 tint 靠 28%。
        /// 这样"有 Map 船"仍是三层可读结构，但整体带阵营色。
        /// </summary>
        public static Color ReliefTint(Color classTint, int factionId)
            => Color.Lerp(classTint, PartTint(factionId), 0.28f);
    }
}
