using UnityEngine;

namespace PioneerFramework.UiKit
{
    /// <summary>
    /// 航天器管理界面调色板。
    /// PFL 航天主题，与 RimWorld 原版暗色窗口基调和谐。
    /// 三风格混合：瑞士国际主义基底 + 磁带盒未来主义暖调近黑 + 复古未来主义饱和强调色。
    /// ⚠ 2026-10-02（V-0）：迁入 UiKit 供共用；**本色板保持彩色**——PFView 走独立的灰度
    ///   `PFViewPalette`（用户裁定③"灰度只 PFView 用"）。命名空间不用 `UI`：那会让
    ///   `PioneerFramework` 树里的 `UI.screenWidth`（Verse.UI）被同名的子命名空间截胡。
    /// </summary>
    internal static class SpacecraftUIPalette
    {
        // ── 中性色（瑞士国际主义基底） ──

        /// <summary>窗底背景 — 暖调近黑（CRT 屏底）。</summary>
        internal static readonly Color ColWindowBg = HexRgb("#0D1018");

        /// <summary>头部背景 — 深蓝灰（标题栏、Tab 栏底色）。</summary>
        internal static readonly Color ColHeaderBg = HexRgb("#1C222C");

        /// <summary>侧栏背景 — 半透明深蓝灰。</summary>
        internal static readonly Color ColSidebarBg = new(0.07f, 0.09f, 0.12f, 0.92f);

        /// <summary>面板背景 — 深暖灰（卡片、列表项底色）。</summary>
        internal static readonly Color SpecPanelBg = HexRgb("#151A22");

        /// <summary>面板高亮 — 提亮暖灰（选中卡片、展开区）。</summary>
        internal static readonly Color SpecPanelBgHighlight = HexRgb("#1A1F28");

        /// <summary>面板悬停 — 暖灰。</summary>
        internal static readonly Color SpecPanelBgHover = HexRgb("#1A2029");

        /// <summary>边框 — 半透明白（卡片边、分割线）。</summary>
        internal static readonly Color ColBorder = new(1f, 1f, 1f, 0.08f);

        /// <summary>分割线 — 暖灰。</summary>
        internal static readonly Color SpecDivider = HexRgb("#2E343D");

        /// <summary>SpecBar 背景 — 深蓝灰。</summary>
        internal static readonly Color SpecBarBg = HexRgb("#1C222C");

        // ── 文字色 ──

        /// <summary>主文字 — 银白（标题、正文）。</summary>
        internal static readonly Color ColTextBody = HexRgb("#E6ECF2");

        /// <summary>次要文字 — 蓝灰（元数据、说明、占位提示）。</summary>
        internal static readonly Color ColTextMuted = HexRgb("#889196");

        /// <summary>标题色 — 银白。</summary>
        internal static readonly Color SpecTextTitle = HexRgb("#E6ECF2");

        /// <summary>暖白 — 米白（主按钮文字、高亮读数）。</summary>
        internal static readonly Color SpecWarmWhite = HexRgb("#FAF7F2");

        // ── 强调色（复古未来主义饱和色） ──

        /// <summary>暖铜 — 主操作、原版 TransportPod 模式。</summary>
        internal static readonly Color SpecWarmCopper = HexRgb("#D48C3C");

        /// <summary>暖铜悬停。</summary>
        internal static readonly Color SpecWarmCopperHover = HexRgb("#E4A060");

        /// <summary>冷青 — PFL 入轨模式、轨迹参数。</summary>
        internal static readonly Color SpecColdCyan = HexRgb("#3A8A7A");

        /// <summary>冷青悬停。</summary>
        internal static readonly Color SpecColdCyanHover = HexRgb("#4A9A8A");

        /// <summary>冷青禁用。</summary>
        internal static readonly Color SpecColdCyanDisabled = new(58f / 255f, 138f / 255f, 122f / 255f, 0.35f);

        /// <summary>军绿 — 已完工状态、施工进度。</summary>
        internal static readonly Color SpecAccentMilitary = HexRgb("#6B8E5A");

        /// <summary>军绿按钮。</summary>
        internal static readonly Color SpecMilitaryButton = HexRgb("#5A7A4A");

        /// <summary>军绿按钮悬停。</summary>
        internal static readonly Color SpecMilitaryButtonHover = HexRgb("#6B8E5A");

        /// <summary>警告 — 锈红（取消设计、条件不满足）。</summary>
        internal static readonly Color SpecDanger = HexRgb("#E76F51");

        /// <summary>琥珀 — 调试按钮、dev 区标识。</summary>
        internal static readonly Color SpecAmber = HexRgb("#F4A261");

        // ── 辅助方法 ──

        /// <summary>将 #RRGGBB 格式的十六进制字符串转换为 Color。</summary>
        private static Color HexRgb(string hex)
        {
            if (hex.Length >= 7 && hex[0] == '#')
            {
                int r = HexNibble(hex[1]) * 16 + HexNibble(hex[2]);
                int g = HexNibble(hex[3]) * 16 + HexNibble(hex[4]);
                int b = HexNibble(hex[5]) * 16 + HexNibble(hex[6]);
                return new Color(r / 255f, g / 255f, b / 255f, 1f);
            }
            return Color.white;
        }

        private static int HexNibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return 0;
        }
    }
}
