using PioneerFramework;
using RimWorld;
using UnityEngine;

namespace PioneerFramework.ShipSkin
{
    /// <summary>
    /// 舰船蒙皮的阵营涂装与工业装甲配色（2026-10-20）。
    ///
    /// 口径：
    ///   · 中性钢灰底色独立于阵营色，阵营色只用于贴图中少量涂装区域；
    ///   · 阵营解析走既有唯一入口 `FactionResolver.Get`（解析失败 ⇒ 用钢灰，不伪造敌对色）；
    ///   · 亮度保底：阵营色过暗时往白抬一点，保证船在夜面仍能读出形体（本库纪律：不许"看不见=没问题"）。
    /// </summary>
    public static class ShipSkinPalette
    {
        /// <summary>中性装甲钢灰（与 `ShipSkinPlates.png` 的灰底相乘后的目标观感）。</summary>
        private static readonly Color Steel = new(0.76f, 0.80f, 0.86f);

        /// <summary>局部涂装保留可辨认的阵营色，面积由贴图蓝色遮罩限定。</summary>
        private const float FactionMix = 0.80f;

        /// <summary>现有三类浮雕分别采用结构深灰、舱室浅灰、装甲中灰。</summary>
        internal static Color HullColor(int reliefClass) => reliefClass switch
        {
            1 => new Color(0.40f, 0.44f, 0.48f),
            2 => new Color(0.76f, 0.79f, 0.82f),
            3 => new Color(0.57f, 0.61f, 0.66f),
            _ => new Color(0.74f, 0.77f, 0.80f),
        };

        /// <summary>同一共享材质的表面参数；保持三档各一次提交。</summary>
        internal static float Roughness(int reliefClass) => reliefClass switch
        {
            1 => 0.68f,
            2 => 0.82f,
            3 => 0.62f,
            _ => 0.76f,
        };

        /// <summary>保底亮度：低于它就把 tint 往白抬（最多抬 50%）。</summary>
        private const float MinLuminance = 0.55f;

        /// <summary>无 Map 编队/导弹/有 Map 船体共用的局部涂装颜色。</summary>
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
