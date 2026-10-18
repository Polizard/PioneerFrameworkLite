using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 的**视图内反馈层**（2026-10-18 视图系统 v2）：
    /// · 选中括号 / 目标线 / 离屏箭头（U15）；焦点不在视图中心画记号（2026-10-18 用户口径）。
    /// · 视图内 toast（U12）——**不能依赖 Messages.Message**：宿主把它画在 PFView 底下。
    ///
    /// 纪律：全部屏幕空间 IMGUI + PFViewPalette 灰度；不加 3D 材质、不进 uiBlockRects（否则会挡相机输入）。
    /// </summary>
    public partial class MainTabWindow_PFView
    {
        private sealed class PfvToast
        {
            public string Text = "";
            public float Until;
        }

        private readonly List<PfvToast> toasts = new();
        private static readonly List<PfvToast> toastPool = new();

        /// <summary>视图内提示（3.5 s）。玩家在 PFView 里能看见；不写日志、不弹 Messages。</summary>
        internal void QueueToast(string text, float seconds = 3.5f)
        {
            if (string.IsNullOrEmpty(text)) return;
            PfvToast t;
            if (toastPool.Count > 0)
            {
                t = toastPool[toastPool.Count - 1];
                toastPool.RemoveAt(toastPool.Count - 1);
            }
            else
            {
                t = new PfvToast();
            }
            t.Text = text;
            t.Until = Time.realtimeSinceStartup + Mathf.Max(0.5f, seconds);
            toasts.Add(t);
            while (toasts.Count > 4)
            {
                toastPool.Add(toasts[0]);
                toasts.RemoveAt(0);
            }
        }

        private void DrawToasts(Rect inRect)
        {
            float now = Time.realtimeSinceStartup;
            for (int i = toasts.Count - 1; i >= 0; i--)
            {
                if (now >= toasts[i].Until)
                {
                    toastPool.Add(toasts[i]);
                    toasts.RemoveAt(i);
                }
            }
            if (toasts.Count == 0) return;

            float w = Mathf.Min(560f, inRect.width - 40f);
            float h = 24f;
            float y = rToolbar.yMax + 8f + PanelPad;
            for (int i = 0; i < toasts.Count; i++)
            {
                var r = new Rect(inRect.center.x - w * 0.5f, y + i * (h + 4f), w, h);
                Widgets.DrawBoxSolid(r, PFViewPalette.PanelBg);
                UiKit.SpacecraftUIDecor.DrawColoredLabel(r, toasts[i].Text, PFViewPalette.TextPrimary,
                    GameFont.Tiny, TextAnchor.MiddleCenter);
            }
        }

        /// <summary>选中/焦点/目标的世界标记。画在面板之前 ⇒ 被面板自然遮挡。</summary>
        private void DrawWorldMarkers(Rect inRect)
        {
            if (renderCamera == null || inRect.width < 10f) return;
            double tick = RenderTick();
            DVector3 origin = GetRenderOrigin(tick);
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();

            // ── 选中括号 ──
            if (selectedGroupId >= 0 && spaceGame != null)
            {
                var g = spaceGame.GetGroupById(selectedGroupId);
                if (CanInspectEntity(g))
                {
                    DVector3 abs = GetGroupPosAbs(g, tick);
                    DrawBracketFor(abs, inRect, PFViewPalette.Accent, $"G{selectedGroupId}", true);
                }
            }

            // ── 目标线与距离 ──
            if (targetGroupId >= 0 && spaceGame != null)
            {
                var target = spaceGame.GetGroupById(targetGroupId);
                if (CanInspectEntity(target))
                {
                    DVector3 tAbs = GetGroupPosAbs(target, tick);
                    DVector3 sAbs = origin;
                    if (ResolveOperationGroup() is { } operation)
                    {
                        sAbs = GetGroupPosAbs(operation, tick);
                    }
                    DrawTargetLink(sAbs, tAbs, inRect);
                }
            }
        }

        private void DrawBracketFor(DVector3 abs, Rect inRect, Color col, string label, bool showDistance)
        {
            if (!TryProjectWorld(abs, inRect, out Vector2 p, out bool behind, out _))
            {
                DrawOffscreenMarker(abs, inRect, col, label);
                return;
            }

            if (!inRect.Contains(p))
            {
                DrawEdgeMarker(p, inRect, col, label);
                return;
            }

            const float arm = 9f;
            Widgets.DrawLine(new Vector2(p.x - arm, p.y - arm), new Vector2(p.x - arm, p.y - arm + 6f), col, 1f);
            Widgets.DrawLine(new Vector2(p.x - arm, p.y - arm), new Vector2(p.x - arm + 6f, p.y - arm), col, 1f);
            Widgets.DrawLine(new Vector2(p.x + arm, p.y - arm), new Vector2(p.x + arm, p.y - arm + 6f), col, 1f);
            Widgets.DrawLine(new Vector2(p.x + arm, p.y - arm), new Vector2(p.x + arm - 6f, p.y - arm), col, 1f);
            Widgets.DrawLine(new Vector2(p.x - arm, p.y + arm), new Vector2(p.x - arm, p.y + arm - 6f), col, 1f);
            Widgets.DrawLine(new Vector2(p.x - arm, p.y + arm), new Vector2(p.x - arm + 6f, p.y + arm), col, 1f);
            Widgets.DrawLine(new Vector2(p.x + arm, p.y + arm), new Vector2(p.x + arm, p.y + arm - 6f), col, 1f);
            Widgets.DrawLine(new Vector2(p.x + arm, p.y + arm), new Vector2(p.x + arm - 6f, p.y + arm), col, 1f);

            if (!showDistance) return;
            float m = Vector3.Distance(AbsToView(abs, GetRenderOrigin()), renderCamera.transform.position) * ViewToMeters;
            string text = m >= 1000f ? $"{label} · {m / 1000f:F0} km" : $"{label} · {m:F0} m";
            DrawTag(new Vector2(p.x + arm + 3f, p.y - arm - 2f), text, col);
        }

        private void DrawTargetLink(DVector3 fromAbs, DVector3 toAbs, Rect inRect)
        {
            bool a = TryProjectWorld(fromAbs, inRect, out Vector2 pa, out _, out _);
            bool b = TryProjectWorld(toAbs, inRect, out Vector2 pb, out _, out _);
            float distM = (float)(toAbs - fromAbs).magnitude;

            if (a && b)
            {
                // 虚线：8 px 实 / 6 px 空（灰度，不靠色相）
                DrawDashedLine(pa, pb, PFViewPalette.TextSecondary);
                Vector2 mid = (pa + pb) * 0.5f;
                DrawTag(mid, $"目标 · {distM / 1000f:F0} km", PFViewPalette.TextPrimary);
            }
            else
            {
                DrawOffscreenMarker(toAbs, inRect, PFViewPalette.StateWarn, $"目标 · {distM / 1000f:F0} km");
            }
        }

        private static void DrawDashedLine(Vector2 a, Vector2 b, Color col)
        {
            float len = Vector2.Distance(a, b);
            if (len < 1f) return;
            Vector2 dir = (b - a) / len;
            for (float d = 0f; d < len; d += 14f)
            {
                float e = Mathf.Min(d + 8f, len);
                Widgets.DrawLine(a + dir * d, a + dir * e, col, 1f);
            }
        }

        private void DrawOffscreenMarker(DVector3 abs, Rect inRect, Color col, string label)
        {
            Vector3 vp = AbsToView(abs, GetRenderOrigin());
            Vector3 sp = renderCamera.WorldToScreenPoint(vp);
            Vector2 center = inRect.center;
            Vector2 dir;
            if (sp.z > 0f)
            {
                Vector2 p = new(inRect.x + sp.x / renderCamera.pixelWidth * inRect.width,
                                inRect.y + (1f - sp.y / renderCamera.pixelHeight) * inRect.height);
                dir = p - center;
            }
            else
            {
                // 相机后方：用反方向（至少让玩家知道往哪转）
                dir = new Vector2(-vp.x, vp.y);
            }
            if (dir.sqrMagnitude < 1e-4f) dir = Vector2.up;
            DrawEdgeMarker(center + dir.normalized * 10f, inRect, col, label);
        }

        private void DrawEdgeMarker(Vector2 from, Rect inRect, Color col, string label)
        {
            Vector2 center = inRect.center;
            Vector2 dir = from - center;
            if (dir.sqrMagnitude < 1e-4f) dir = Vector2.up;
            float halfW = inRect.width * 0.5f - 22f;
            float halfH = inRect.height * 0.5f - 22f;
            float dx = Mathf.Abs(dir.x) < 1e-4f ? 9999f : halfW / Mathf.Abs(dir.x);
            float dy = Mathf.Abs(dir.y) < 1e-4f ? 9999f : halfH / Mathf.Abs(dir.y);
            float s = Mathf.Min(dx, dy);
            Vector2 edge = center + dir.normalized * s;
            Widgets.DrawBoxSolid(new Rect(edge.x - 3f, edge.y - 3f, 6f, 6f), col);
            DrawTag(edge + new Vector2(5f, -7f), label, col);
        }

        private Rect ConstrainMarkerLabel(Rect desired, Rect viewRect)
        {
            var label = PFViewUiLayoutCore.PlaceLabel(new(desired.x - viewRect.x, desired.y - viewRect.y,
                desired.width, desired.height), viewRect.width, viewRect.height, uiLayout);
            return FromUiRect(label, viewRect);
        }

        private void DrawTag(Vector2 pos, string text, Color col)
        {
            var rect = ConstrainMarkerLabel(new(pos.x, pos.y, Mathf.Max(90f, text.Length * 7f + 8f), 18f), viewportRect);
            if (rect.width <= 0) return;
            Widgets.DrawBoxSolid(rect, PFViewPalette.PanelBg);
            TooltipHandler.TipRegion(rect, text);
            UiKit.SpacecraftUIDecor.DrawColoredLabel(rect, text, col, GameFont.Tiny, TextAnchor.MiddleLeft);
        }

        /// <summary>世界→视口坐标（与拾取同一套 RT→窗口映射）。</summary>
        private bool TryProjectWorld(DVector3 abs, Rect inRect, out Vector2 p, out bool behind, out float depth)
        {
            p = default;
            behind = false;
            depth = 0f;
            if (renderCamera == null) return false;

            Vector3 vp = AbsToView(abs, GetRenderOrigin());
            Vector3 sp = renderCamera.WorldToScreenPoint(vp);
            depth = sp.z;
            if (sp.z <= 0f)
            {
                behind = true;
                return false;
            }

            p = new Vector2(inRect.x + sp.x / renderCamera.pixelWidth * inRect.width,
                            inRect.y + (1f - sp.y / renderCamera.pixelHeight) * inRect.height);
            return true;
        }
    }
}
