using System;

namespace PioneerFramework
{
    internal enum PFViewToolbarItem
    {
        Group, Navigate, Body, Previous, Display, Camera, Frame, Time, Weapons, Help, Debug, Navball, Radar
    }

    internal readonly record struct PFViewUiRect(float X, float Y, float Width, float Height)
    {
        public bool Contains(float x, float y) => Width > 0 && Height > 0
            && x >= X && x < X + Width && y >= Y && y < Y + Height;
        public bool Overlaps(PFViewUiRect other) => Width > 0 && Height > 0 && other.Width > 0 && other.Height > 0
            && X < other.X + other.Width && X + Width > other.X && Y < other.Y + other.Height && Y + Height > other.Y;
    }

    internal readonly record struct PFViewUiLayout(PFViewUiRect Toolbar, PFViewUiRect ObjectList,
        PFViewUiRect Details, PFViewUiRect Radar, PFViewUiRect Navball, PFViewUiRect Status,
        PFViewUiRect SteeringStrip, bool Compact, bool CompactNavballReadouts, bool RequiresInstrumentDrawer)
    {
        public PFViewUiRect PilotPanel { get; init; }
    }

    /// <summary>仅计算逻辑 UI 像素；不触碰相机、投影、宿主或 UI 全局状态。</summary>
    internal static class PFViewUiLayoutCore
    {
        private const float Pad = 8f;
        public const float BallSize = 222f;

        public static PFViewUiRect NextToolbarRect(float toolbarWidth, ref float x, ref float y, PFViewToolbarItem item)
        {
            float width = item switch
            {
                PFViewToolbarItem.Group or PFViewToolbarItem.Navigate or PFViewToolbarItem.Body => 92f,
                PFViewToolbarItem.Previous or PFViewToolbarItem.Navball or PFViewToolbarItem.Radar => 80f,
                PFViewToolbarItem.Display => 76f,
                PFViewToolbarItem.Camera => 144f,
                PFViewToolbarItem.Frame => 132f,
                PFViewToolbarItem.Time => 200f,
                PFViewToolbarItem.Weapons => 88f,
                PFViewToolbarItem.Help => 30f,
                _ => 60f
            };
            width = Math.Min(width, Math.Max(0, toolbarWidth - 12f));
            if (x + width > toolbarWidth - 6f) { x = 6f; y += 32f; }
            var rect = new PFViewUiRect(x, y, width, 24f);
            x += width + 4f;
            return rect;
        }

        private static float ToolbarHeight(float width, bool navDrawer, bool radarDrawer)
        {
            float x = 6f, y = 4f;
            // Reserve weapons even when no operation source is available, so focus changes keep the layout stable.
            for (var item = PFViewToolbarItem.Group; item <= PFViewToolbarItem.Radar; item++)
            {
                if (item == PFViewToolbarItem.Navball && !navDrawer || item == PFViewToolbarItem.Radar && !radarDrawer) continue;
                NextToolbarRect(width, ref x, ref y, item);
            }
            return y + 28f;
        }

        public static PFViewUiRect PlaceLabel(PFViewUiRect desired, float width, float height, PFViewUiLayout layout)
        {
            if (width <= 0 || height <= 0 || !float.IsFinite(desired.X) || !float.IsFinite(desired.Y)) return default;
            float w = Math.Min(desired.Width, width), h = Math.Min(desired.Height, height);
            PFViewUiRect Clamp(float x, float y) => new(Math.Clamp(x, 0, width - w), Math.Clamp(y, 0, height - h), w, h);
            PFViewUiRect Obstacle(int i) => i switch
            {
                0 => layout.Toolbar, 1 => layout.ObjectList, 2 => layout.Details, 3 => layout.Radar,
                4 => layout.Navball.Width > 0 ? new(layout.Navball.X, layout.Navball.Y - 24,
                    layout.Navball.Width, layout.Navball.Height + 24) : default,
                5 => layout.Status, 6 => layout.SteeringStrip, _ => layout.PilotPanel
            };
            bool Free(PFViewUiRect rect)
            {
                for (int i = 0; i < 8; i++) if (rect.Overlaps(Obstacle(i))) return false;
                return true;
            }
            var initial = Clamp(desired.X, desired.Y);
            if (Free(initial)) return initial;
            PFViewUiRect best = default;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < 8; i++)
            {
                var obstacle = Obstacle(i);
                if (obstacle.Width <= 0 || obstacle.Height <= 0) continue;
                for (int side = 0; side < 4; side++)
                {
                    var candidate = side switch
                    {
                        0 => Clamp(obstacle.X - w - 4, initial.Y),
                        1 => Clamp(obstacle.X + obstacle.Width + 4, initial.Y),
                        2 => Clamp(initial.X, obstacle.Y - h - 4),
                        _ => Clamp(initial.X, obstacle.Y + obstacle.Height + 4)
                    };
                    float dx = candidate.X - initial.X, dy = candidate.Y - initial.Y;
                    float distance = dx * dx + dy * dy;
                    if (!Free(candidate) || distance >= bestDistance) continue;
                    best = candidate;
                    bestDistance = distance;
                }
            }
            return best;
        }

        public static PFViewUiLayout Compute(float width, float height, bool detailsVisible, PFViewUiVisibility visibility, bool piloting = false)
        {
            if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0) return default;
            float sideW = Math.Min(width < 900f ? 300f : 330f, Math.Max(0, width - 2 * Pad));
            float toolbarH = Math.Min(height, ToolbarHeight(width, false, false));
            bool crowded = visibility.ShowRadar && (visibility.ShowNavball || piloting)
                && width - sideW - 260f - 4f * Pad < (piloting ? 250f : BallSize);
            bool drawer = width < 760f || crowded || height - toolbarH - 24f - 2f * Pad < 268f;
            if (drawer)
                toolbarH = Math.Min(height, ToolbarHeight(width, visibility.ShowNavball, visibility.ShowRadar));
            float statusH = Math.Min(24f, Math.Max(0, height - toolbarH));
            float top = toolbarH + Pad;
            float bottom = Math.Max(top, height - statusH - Pad);
            float bodyH = Math.Max(0, bottom - top);
            bool compact = width < 1100f || height < 600f;
            var side = new PFViewUiRect(Pad, top, sideW, bodyH);
            PFViewUiRect objects = side;
            PFViewUiRect details = default;
            if (compact && detailsVisible)
            {
                // Leave room for the fixed list controls and a complete fleet card when space permits.
                float listH = Math.Min(Math.Max(0, bodyH - 96f - Pad), Math.Max(252f, (bodyH - Pad) * .65f));
                objects = new(side.X, top, sideW, listH);
                details = new(side.X, top + listH + Pad, sideW, Math.Max(0, bodyH - listH - Pad));
            }
            PFViewUiRect radar = default, nav = default, strip = default;
            float centerLeft = side.X + side.Width + Pad;
            float centerRight = width - Pad;

            if (!compact)
            {
                float rightX = width - 288f - Pad;
                centerRight = rightX - Pad;
                if (visibility.ShowRadar && !drawer)
                    radar = new(width - Pad - 260f, bottom - 260f, 260f, 260f);
                if (detailsVisible)
                    details = new(rightX, top, 288f, Math.Max(0, (radar.Width > 0 ? radar.Y - Pad : bottom) - top));
            }
            else if (visibility.ShowRadar && !drawer)
            {
                radar = new(width - Pad - 260f, bottom - 260f, 260f, 260f);
                centerRight = radar.X - Pad;
            }

            float centerW = Math.Max(0, centerRight - centerLeft);
            bool compactReadouts = centerW < 522f;
            if (visibility.ShowNavball && !drawer)
            {
                float navW = compactReadouts ? BallSize : 522f;
                nav = new(centerLeft + (centerW - navW) * .5f, bottom - BallSize, navW, BallSize);
            }
            if (visibility.ShowSteeringStrip && !visibility.ShowNavball && centerW > 0)
                strip = new(centerLeft, Math.Max(top, bottom - 32f), centerW, Math.Min(32f, bodyH));

            PFViewUiRect pilot = default;
            if (piloting && centerW > 0)
            {
                float pilotBottom = nav.Width > 0 ? nav.Y - 24f - Pad : bottom;
                if (strip.Width > 0) pilotBottom = Math.Min(pilotBottom, strip.Y - Pad);
                float pilotH = Math.Min(112f, Math.Max(0, pilotBottom - top));
                float pilotW = Math.Min(522f, centerW);
                if (pilotH >= 72f) pilot = new(centerLeft + (centerW - pilotW) * .5f, top, pilotW, pilotH);
            }
            return new(new(0, 0, width, toolbarH), objects, details, radar, nav,
                new(0, height - statusH, width, statusH), strip, compact, compactReadouts, drawer) { PilotPanel = pilot };
        }
    }
}
