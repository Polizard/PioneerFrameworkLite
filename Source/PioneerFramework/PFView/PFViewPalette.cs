using UnityEngine;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 专用**灰度**调色板。
    ///
    /// 用户裁定③（2026-10-02）：灰度只用于 PFView——地面「航天器管理」窗继续用彩色
    /// <see cref="PioneerFramework.UiKit.SpacecraftUIPalette"/>，两窗**不**统一配色。
    ///
    /// 黑白灰纪律（本阶段施工与后续 V-1/V-3 均按此执行）：
    ///   · 层级靠**明度**（三级文字 / 两级面板 / 线宽）与**反白**，不靠色相；
    ///   · 状态靠**符号**（✓ ✕ ◐ ◌）＋明度差，不用红绿；
    ///   · 所有颜色必须 R=G=B —— 本文件只用 <see cref="G"/> / <see cref="Gray"/> 构造，从根上写不出彩色。
    /// </summary>
    internal static class PFViewPalette
    {
        // ── 底（由深到浅） ──

        /// <summary>全屏屏底（深于所有面板）。</summary>
        internal static readonly Color WindowBg = G(0x0A);

        /// <summary>面板底（列表栏 / 读数栏 / 雷达）。</summary>
        internal static readonly Color PanelBg = G(0x14);

        /// <summary>面板高亮（选中项 / 展开区）。</summary>
        internal static readonly Color PanelBgRaised = G(0x1C);

        /// <summary>面板悬停。</summary>
        internal static readonly Color PanelBgHover = G(0x22);

        /// <summary>条带底（工具条 / 状态条）。</summary>
        internal static readonly Color BarBg = G(0x10);

        // ── 线 ──

        /// <summary>细边框（半透明白，无彩色）。</summary>
        internal static readonly Color Border = new(1f, 1f, 1f, 0.10f);

        /// <summary>网格线（坐标环 / 背景格）。</summary>
        internal static readonly Color Grid = G(0x24);

        /// <summary>分割线。</summary>
        internal static readonly Color Divider = G(0x3A);

        // ── 文字（三级，靠明度分层级） ──

        /// <summary>一级文字（标题 / 主读数）。</summary>
        internal static readonly Color TextPrimary = G(0xE6);

        /// <summary>二级文字（正文 / 字段值）。</summary>
        internal static readonly Color TextSecondary = G(0x9A);

        /// <summary>三级文字（标签 / 注释 / 占位）。</summary>
        internal static readonly Color TextMuted = G(0x5C);

        // ── 强调（无彩色：亮 + 反白） ──

        /// <summary>强调（选中 / 主操作）：纯白。</summary>
        internal static readonly Color Accent = G(0xFF);

        /// <summary>强调降档（次要强调）。</summary>
        internal static readonly Color AccentDim = G(0xB8);

        // ── 雷达盘面扫描（V-2，2026-10-18） ──
        // 扫描线是盘面上**最亮**的东西（白），尾迹靠 alpha 渐变，待机线只比网格亮一档：
        // 全部 R=G=B，层级仍只靠明度/透明度表达，不引入色相。

        /// <summary>扫描前导线 / 接触点被扫到时的峰值明度。</summary>
        internal static readonly Color SweepLine = G(0xFF);

        /// <summary>待机扫描（没有主动雷达 / 未开机）：压暗的装饰扫描线，明度介于网格与二级文字之间。</summary>
        internal static readonly Color SweepStandby = Gray(0x9A, 0.55f);

        // ── 状态（符号 + 明度，不用色相） ──

        /// <summary>正常 / 在线 / Live。</summary>
        internal static readonly Color StateOk = G(0xD8);

        /// <summary>注意 / 影子 / Shadow（低置信）。</summary>
        internal static readonly Color StateWarn = G(0x8A);

        /// <summary>失效 / 丢失 / Lost / 假接触。</summary>
        internal static readonly Color StateDead = G(0x50);

        /// <summary>灰度构造：唯一入口（v = 0..255），R=G=B 由构造保证。</summary>
        private static Color G(int v) => Gray(v, 1f);

        /// <summary>带透明度的灰度（扫描尾迹/余辉这类亮暗过渡要 alpha，但**仍不许有色相**）。</summary>
        internal static Color Gray(int v, float alpha)
        {
            float c = v / 255f;
            return new Color(c, c, c, alpha);
        }
    }
}
