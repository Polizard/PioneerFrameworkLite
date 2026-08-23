using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 航天器管理界面装饰元素绘制工具。
    /// 全部使用 IMGUI 程序化绘制，不依赖外部 Texture2D 资源。
    /// 风格：磁带盒未来主义（L 形角标、SpecBar、网格线）+ 复古未来主义（网点纹理）。
    /// </summary>
    internal static class SpacecraftUIDecor
    {
        // ── 6.1 L 形角标（磁带卡座铬色感） ──

        /// <summary>
        /// 在矩形四角绘制 L 形边框线，磁带卡座铬色装饰。
        /// </summary>
        /// <param name="area">目标矩形区域。</param>
        /// <param name="lineColor">线条颜色。</param>
        /// <param name="inset">角标相对矩形边缘的内缩距离。</param>
        /// <param name="arm">L 形线的臂长。</param>
        internal static void DrawCornerLBrackets(Rect area, Color lineColor, float inset = 6f, float arm = 14f)
        {
            GUI.color = lineColor;
            float x0 = area.x + inset;
            float y0 = area.y + inset;
            float x1 = area.xMax - inset;
            float y1 = area.yMax - inset;

            // 左上
            Widgets.DrawLineHorizontal(x0, y0, arm);
            Widgets.DrawLineVertical(x0, y0, arm);
            // 右上
            Widgets.DrawLineHorizontal(x1 - arm, y0, arm);
            Widgets.DrawLineVertical(x1, y0, arm);
            // 左下
            Widgets.DrawLineHorizontal(x0, y1, arm);
            Widgets.DrawLineVertical(x0, y1 - arm, arm);
            // 右下
            Widgets.DrawLineHorizontal(x1 - arm, y1, arm);
            Widgets.DrawLineVertical(x1, y1 - arm, arm);

            GUI.color = Color.white;
        }

        // ── 6.2 横向状态条带（SpecBar 风格） ──

        /// <summary>
        /// 绘制横向状态条带：3px 实色左边条 + 深蓝灰底 + 标签文字。
        /// 用于状态机当前态、发射台状态行。
        /// </summary>
        internal static void DrawSpecBar(Rect r, string text, Color accentColor)
        {
            const float strip = 3f;
            Widgets.DrawBoxSolid(r, SpacecraftUIPalette.SpecBarBg);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, strip, r.height), accentColor);

            Rect textR = new Rect(r.x + strip + 8f, r.y + 4f, r.width - strip - 12f, r.height - 8f);
            DrawColoredLabel(textR, text, SpacecraftUIPalette.SpecTextTitle, GameFont.Small, TextAnchor.MiddleLeft);
        }

        /// <summary>
        /// 绘制 SpecBar 风格的多行卡片：3px 实色左边条 + 深暖灰面板底 + 四角 L 形角标。
        /// 用于总览页发射台状态行、施工页火箭关联状态等多行信息卡片（§8.1/§8.3）。
        /// 调用方负责在返回的 innerRect 中绘制具体内容行。
        /// </summary>
        /// <param name="r">卡片外框矩形。</param>
        /// <param name="accentColor">左边条语义色（暖铜/军绿/冷青/锈红）。</param>
        /// <param name="cardPad">内边距，默认 12px。返回的内内容区已扣除该内边距。</param>
        /// <returns>可绘制内容的内部矩形区域。</returns>
        internal static Rect DrawSpecCard(Rect r, Color accentColor, float cardPad = 12f)
        {
            const float strip = 3f;
            Widgets.DrawBoxSolid(r, SpacecraftUIPalette.SpecPanelBg);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, strip, r.height), accentColor);
            DrawCornerLBrackets(r, SpacecraftUIPalette.ColBorder);
            return new Rect(r.x + strip + cardPad, r.y + cardPad,
                r.width - strip - cardPad * 2f, r.height - cardPad * 2f);
        }

        // ── 6.3 网格线背景（磁带未来主义线框网格） ──

        /// <summary>
        /// 绘制浅色网格线背景，磁带未来主义线框网格感。
        /// </summary>
        internal static void DrawLightGrid(Rect area, Color lineColor, float step = 40f)
        {
            GUI.color = lineColor;
            for (float x = area.x; x < area.xMax; x += step)
                Widgets.DrawLineVertical(x, area.y, area.height);
            for (float y = area.y; y < area.yMax; y += step)
                Widgets.DrawLineHorizontal(area.x, y, area.width);
            GUI.color = Color.white;
        }

        // ── 6.6 网点纹理（复古印刷感） ──

        /// <summary>
        /// 绘制 1px 点阵网点纹理，复古印刷感。用于卡片背景装饰。
        /// </summary>
        internal static void DrawDotGrid(Rect area, Color dotColor, float step = 8f)
        {
            GUI.color = dotColor;
            Texture2D tex = BaseContent.WhiteTex;
            for (float x = area.x; x < area.xMax; x += step)
                for (float y = area.y; y < area.yMax; y += step)
                    GUI.DrawTexture(new Rect(x, y, 1f, 1f), tex);
            GUI.color = Color.white;
        }

        // ── 通用工具方法 ──

        /// <summary>
        /// 绘制带颜色的文本标签，支持字体与对齐方式。绘制后恢复默认颜色与对齐。
        /// </summary>
        internal static void DrawColoredLabel(Rect r, string text, Color color, GameFont font, TextAnchor anchor)
        {
            Text.Font = font;
            Text.Anchor = anchor;
            GUI.color = color;
            Widgets.Label(r, text);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        /// <summary>
        /// 绘制等宽数字读数（§4.2）：GameFont.Tiny + 右对齐，让数字按位数对齐。
        /// 用于 PocketMapId #12345、坐标 (12,0,5)、目标高度 400.0 km 等数字字段。
        /// 垂直居中（MiddleRight）避免文字溢出到相邻行。
        /// </summary>
        /// <param name="rect">数字显示区域（建议固定宽度 80–120px）。</param>
        /// <param name="number">数字字符串。</param>
        /// <param name="color">文字色（默认 ColTextBody）。</param>
        internal static void DrawMonospaceNumber(Rect rect, string number, Color color)
        {
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = color;
            Widgets.Label(rect, number);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        /// <summary>
        /// 绘制卡片背景：填充底色 + 上下边框线。
        /// </summary>
        internal static void DrawCardBackground(Rect r, Color bg)
        {
            Widgets.DrawBoxSolid(r, bg);
            GUI.color = SpacecraftUIPalette.ColBorder;
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 1f), SpacecraftUIPalette.ColBorder);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), SpacecraftUIPalette.ColBorder);
            GUI.color = Color.white;
        }

        /// <summary>
        /// 绘制分割线（1px 实色横线）。
        /// </summary>
        internal static void DrawDivider(Rect r, float y, float width)
        {
            Color oldColor = GUI.color;
            GUI.color = SpacecraftUIPalette.SpecDivider;
            GUI.DrawTexture(new Rect(r.x, y, width, 1f), BaseContent.WhiteTex);
            GUI.color = oldColor;
        }

        /// <summary>
        /// 绘制窗口主背景：暖调近黑 CRT 屏底。
        /// </summary>
        internal static void DrawWindowBackground(Rect r)
        {
            Widgets.DrawBoxSolid(r, SpacecraftUIPalette.ColWindowBg);
        }

        // ── 7.1 按钮（主要 / 次要 / 警告 / 调试） ──

        /// <summary>
        /// 绘制主要按钮：实心语义色背景 + 暖白文字。用于主操作（新建设计/应用参数/进入舱内等）。
        /// 禁用时背景半透明（alpha 0.35）；hover 时使用 bgHover 色。
        /// </summary>
        /// <param name="rect">按钮矩形。</param>
        /// <param name="label">按钮文字。</param>
        /// <param name="bg">背景语义色（暖铜/军绿/冷青）。</param>
        /// <param name="bgHover">hover 时的背景色（提亮版）。</param>
        /// <param name="textColor">文字色（通常为 SpecWarmWhite）。</param>
        /// <param name="enabled">是否启用。禁用时按钮不可点击且半透明。</param>
        /// <returns>点击时返回 true。</returns>
        internal static bool DrawPrimaryButton(Rect rect, string label,
            Color bg, Color bgHover, Color textColor, bool enabled = true)
        {
            bool hover = enabled && Mouse.IsOver(rect);
            Color drawBg = hover ? bgHover : bg;
            if (!enabled)
                drawBg = new Color(drawBg.r, drawBg.g, drawBg.b, 0.35f);

            Widgets.DrawBoxSolid(rect, drawBg);

            Color drawText = enabled
                ? textColor
                : new Color(textColor.r, textColor.g, textColor.b, 0.5f);
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = drawText;
            Widgets.Label(rect, label);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            if (!enabled)
                return false;

            return Widgets.ButtonInvisible(rect, true);
        }

        /// <summary>
        /// 绘制带边框按钮：深暖灰底 + 1px 语义色边框 + 语义色文字。
        /// 用于次要/警告/调试操作（取消设计/直接发射调试/强制发射 PFL）。
        /// </summary>
        /// <param name="rect">按钮矩形。</param>
        /// <param name="label">按钮文字。</param>
        /// <param name="borderColor">边框语义色（锈红/琥珀/半透明白）。</param>
        /// <param name="textColor">文字色（与边框同色）。</param>
        /// <param name="enabled">是否启用。</param>
        /// <returns>点击时返回 true。</returns>
        internal static bool DrawOutlinedButton(Rect rect, string label,
            Color borderColor, Color textColor, bool enabled = true)
        {
            float alpha = enabled ? 1f : 0.35f;

            // 深暖灰底
            Widgets.DrawBoxSolid(rect, SpacecraftUIPalette.SpecPanelBg);

            // 1px 语义色边框
            Color border = new Color(borderColor.r, borderColor.g, borderColor.b, alpha);
            GUI.color = border;
            Widgets.DrawLineHorizontal(rect.x, rect.y, rect.width);
            Widgets.DrawLineHorizontal(rect.x, rect.yMax - 1f, rect.width);
            Widgets.DrawLineVertical(rect.x, rect.y, rect.height);
            Widgets.DrawLineVertical(rect.xMax - 1f, rect.y, rect.height);
            GUI.color = Color.white;

            // 文字
            Color drawText = enabled
                ? textColor
                : new Color(textColor.r, textColor.g, textColor.b, 0.5f);
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = drawText;
            Widgets.Label(rect, label);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            if (!enabled)
                return false;

            return Widgets.ButtonInvisible(rect, true);
        }
    }
}
