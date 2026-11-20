using System;

namespace PioneerFramework
{
    internal enum PFViewToolbarItem
    {
        Navigate, Body, Previous, Display, Camera, Frame, Time, Help, Debug, Navball, Cockpit, Objects
    }

    internal enum PFViewDrawerTab { None, Status, Weapons, Radar, Details, Objects }

    internal readonly record struct PFViewUiRect(float X, float Y, float Width, float Height)
    {
        public bool Contains(float x, float y) => Width > 0 && Height > 0
            && x >= X && x < X + Width && y >= Y && y < Y + Height;
        public bool Overlaps(PFViewUiRect other) => Width > 0 && Height > 0 && other.Width > 0 && other.Height > 0
            && X < other.X + other.Width && X + Width > other.X && Y < other.Y + other.Height && Y + Height > other.Y;
    }

    internal readonly record struct PFViewUiLayout(PFViewUiRect Toolbar, PFViewUiRect ObjectList,
        PFViewUiRect Details, PFViewUiRect Radar, PFViewUiRect Navball, PFViewUiRect Status,
        PFViewUiRect SteeringStrip, bool Compact, bool RequiresInstrumentDrawer)
    {
        public PFViewUiRect PilotPanel { get; init; }
        public PFViewUiRect ObjectActions { get; init; }
        public PFViewUiRect ShipStatus { get; init; }
        public PFViewUiRect Weapons { get; init; }
        public PFViewUiRect FlightControls { get; init; }
        public PFViewUiRect Alert { get; init; }
        public PFViewUiRect ToastArea { get; init; }
        public PFViewUiRect Drawer { get; init; }
        public PFViewDrawerTab DrawerTab { get; init; }
        public bool ObjectsInDrawer { get; init; }

        internal const int OccupiedCount = 14;

        public PFViewUiRect Occupied(int index) => index switch
        {
            0 => Toolbar, 1 => ObjectList, 2 => Details, 3 => Radar,
            4 => Navball,
            5 => Status, 6 => SteeringStrip, 7 => PilotPanel, 8 => ObjectActions,
            9 => ShipStatus, 10 => Weapons, 11 => FlightControls, 12 => Alert, _ => Drawer
        };

        public bool ShowsToolbarItem(PFViewToolbarItem item) => PFViewUiLayoutCore.ShowsToolbarItem(
            item, RequiresInstrumentDrawer, Compact, ObjectsInDrawer);
    }

    /// <summary>仅计算逻辑 UI 像素；不触碰相机、投影、宿主或 UI 全局状态。</summary>
    internal static class PFViewUiLayoutCore
    {
        private const float Pad = 8f;
        public const float BallSize = 222f;
        public const float DockNavballHeight = 88f;
        public const float DockNavballReadoutsWidth = 176f;
        private const float DockNavballMinWidth = 96f;
        private const float DockControlMinWidth = 112f;
        private const float PilotPanelMinWidth = 220f;
        private const float PilotPanelHeight = 54f;
        private const float PilotPanelMaxWidth = 680f;

        public static PFViewUiRect NextToolbarRect(float toolbarWidth, ref float x, ref float y, PFViewToolbarItem item)
        {
            float width = item switch
            {
                PFViewToolbarItem.Navigate or PFViewToolbarItem.Body => 92f,
                PFViewToolbarItem.Previous or PFViewToolbarItem.Navball => 80f,
                PFViewToolbarItem.Display => 76f,
                PFViewToolbarItem.Camera => 144f,
                PFViewToolbarItem.Frame => 132f,
                PFViewToolbarItem.Time => 200f,
                PFViewToolbarItem.Cockpit or PFViewToolbarItem.Objects => 94f,
                PFViewToolbarItem.Help => 30f,
                _ => 60f
            };
            width = Math.Min(width, Math.Max(0, toolbarWidth - 12f));
            if (x + width > toolbarWidth - 6f) { x = 6f; y += 32f; }
            var rect = new PFViewUiRect(x, y, width, 24f);
            x += width + 4f;
            return rect;
        }

        internal static bool ShowsToolbarItem(PFViewToolbarItem item, bool navDrawer, bool compact, bool objectsDrawer)
            => item switch
            {
                PFViewToolbarItem.Navball => navDrawer,
                PFViewToolbarItem.Cockpit => compact,
                PFViewToolbarItem.Objects => objectsDrawer,
                _ => true
            };

        private static float ToolbarHeight(float width, bool navDrawer, bool compact, bool objectsDrawer)
        {
            float x = 6f, y = 4f;
            for (var item = PFViewToolbarItem.Navigate; item <= PFViewToolbarItem.Objects; item++)
                if (ShowsToolbarItem(item, navDrawer, compact, objectsDrawer))
                    NextToolbarRect(width, ref x, ref y, item);
            return y + 28f;
        }

        public static PFViewUiRect PlaceLabel(PFViewUiRect desired, float width, float height, PFViewUiLayout layout)
        {
            if (width <= 0 || height <= 0 || !float.IsFinite(desired.X) || !float.IsFinite(desired.Y)) return default;
            float w = Math.Min(desired.Width, width), h = Math.Min(desired.Height, height);
            PFViewUiRect Clamp(float x, float y) => new(Math.Clamp(x, 0, width - w), Math.Clamp(y, 0, height - h), w, h);
            bool Free(PFViewUiRect rect)
            {
                for (int i = 0; i < PFViewUiLayout.OccupiedCount; i++) if (rect.Overlaps(layout.Occupied(i))) return false;
                return true;
            }
            var initial = Clamp(desired.X, desired.Y);
            if (Free(initial)) return initial;
            PFViewUiRect best = default;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < PFViewUiLayout.OccupiedCount; i++)
            {
                var obstacle = layout.Occupied(i);
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

        /// <param name="shipSelected">是否选中了一艘可操作的舰船（含操舵/火控来源）。false ⇒ 右侧
        /// 舰况/火控/雷达整体收起并把那列宽度还给中央区；详细状态（detailsVisible）不看这个开关。</param>
        public static PFViewUiLayout Compute(float width, float height, bool detailsVisible,
            PFViewUiVisibility visibility, bool piloting = false, PFViewDrawerTab drawerTab = PFViewDrawerTab.None,
            bool alertVisible = false, bool shipSelected = true)
        {
            if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0) return default;

            const float leftWidth = 252f, rightWidth = 288f, actionsHeight = 124f, controlsHeight = 36f;
            bool compact = width < 1120f || height < 640f;
            bool objectsDrawer = width < 760f || height < 320f;
            if (!compact || (drawerTab == PFViewDrawerTab.Objects && !objectsDrawer))
                drawerTab = PFViewDrawerTab.None;
            float centerLeft = objectsDrawer ? Pad : leftWidth + 2 * Pad;
            float maxDrawerWidth = Math.Max(0, width - centerLeft - 2 * Pad - DockControlMinWidth);
            float drawerWidth = drawerTab == PFViewDrawerTab.None ? 0 : Math.Min(rightWidth, maxDrawerWidth);
            // 右列只在真有东西要画时才占宽：详细状态始终要占；舰况/火控/雷达要选中舰船才展开。
            bool rightColumn = !compact && (detailsVisible || shipSelected);
            float centerRight = width - (compact ? drawerWidth : rightColumn ? rightWidth : 0f) - Pad;
            if (!compact || drawerWidth > 0) centerRight -= Pad;
            float centerW = Math.Max(0, centerRight - centerLeft);

            float toolbarH = Math.Min(height, ToolbarHeight(width, false, compact, objectsDrawer));
            float minNavDockW = DockControlMinWidth + Pad + DockNavballMinWidth
                + (piloting ? PilotPanelMinWidth + Pad : 0f);
            float alertReserve = alertVisible ? 36f : 0f;
            bool navDrawer = visibility.ShowNavball && (centerW < minNavDockW
                || height - toolbarH - 24f - 2 * Pad < DockNavballHeight + alertReserve);
            if (navDrawer)
                toolbarH = Math.Min(height, ToolbarHeight(width, true, compact, objectsDrawer));

            float statusH = Math.Min(24f, Math.Max(0, height - toolbarH));
            float top = Math.Min(height - statusH, toolbarH + Pad);
            float bottom = Math.Max(top, height - statusH - Pad);
            float bodyH = bottom - top;
            PFViewUiRect objects = default, actions = default, details = default, radar = default;
            PFViewUiRect ship = default, weapons = default, drawer = default;
            if (!objectsDrawer)
            {
                float actionH = Math.Min(actionsHeight, Math.Max(0, bodyH - 138f));
                objects = new(Pad, top, leftWidth, bodyH - (actionH > 0 ? actionH + Pad : 0));
                actions = new(Pad, bottom - actionH, leftWidth, actionH);
            }

            if (rightColumn)
            {
                float x = width - Pad - rightWidth;
                float radarSide = Math.Clamp(bodyH * .32f, 168f, 248f);
                // 雷达盘面以本舰为原点 ⇒ 没选中舰船时它没有意义，跟舰况/火控一起收起。
                if (visibility.ShowRadar && shipSelected)
                    radar = new(x + (rightWidth - radarSide) * .5f, bottom - radarSide, radarSide, radarSide);
                float upperBottom = radar.Height > 0 ? radar.Y - Pad : bottom;
                if (detailsVisible)
                    details = new(x, top, rightWidth, upperBottom - top);
                else
                {
                    ship = new(x, top, rightWidth, 132f);
                    weapons = new(x, top + 132f + Pad, rightWidth, upperBottom - top - 132f - Pad);
                }
            }
            else if (drawerWidth > 0 && bodyH > 36f)
            {
                drawer = new(width - Pad - drawerWidth, top, drawerWidth, bodyH);
                var content = new PFViewUiRect(drawer.X, top + 36f, drawerWidth, bodyH - 36f);
                switch (drawerTab)
                {
                    case PFViewDrawerTab.Status: ship = content; break;
                    case PFViewDrawerTab.Weapons: weapons = content; break;
                    case PFViewDrawerTab.Details: details = content; break;
                    case PFViewDrawerTab.Radar:
                        float side = Math.Min(content.Width, content.Height);
                        radar = new(content.X + (content.Width - side) * .5f, content.Y, side, side);
                        break;
                    case PFViewDrawerTab.Objects:
                        float actionH = Math.Min(actionsHeight, Math.Max(0, content.Height - 138f));
                        objects = content with { Height = content.Height - actionH };
                        actions = new(content.X, bottom - actionH, content.Width, actionH);
                        break;
                }
            }

            bool navInline = visibility.ShowNavball && !navDrawer;
            bool pilotInline = piloting && centerW >= PilotPanelMinWidth + DockControlMinWidth + Pad
                && bodyH >= PilotPanelHeight + alertReserve;
            const float steeringWidth = 148f;
            bool stripInline = visibility.ShowSteeringStrip && !piloting && !visibility.ShowNavball
                && centerW >= steeringWidth + DockControlMinWidth + Pad && bodyH >= 32f + alertReserve;

            float reservedW = (navInline ? DockNavballMinWidth + Pad : 0f)
                + (pilotInline ? PilotPanelMinWidth + Pad : 0f) + (stripInline ? steeringWidth + Pad : 0f);
            float preferredFlightW = reservedW > 0 ? Math.Clamp(centerW * .36f, DockControlMinWidth, 300f) : 522f;
            float flightW = Math.Min(centerW - reservedW, preferredFlightW);
            float spareW = centerW - reservedW - flightW;
            float navW = navInline ? Math.Min(DockNavballMinWidth + spareW, 192f) : 0f;
            if (navW < DockNavballReadoutsWidth && navInline) navW = DockNavballMinWidth;
            float pilotW = pilotInline ? Math.Min(PilotPanelMaxWidth,
                PilotPanelMinWidth + spareW - (navInline ? navW - DockNavballMinWidth : 0f)) : 0f;
            float stripW = stripInline ? steeringWidth : 0f;
            float dockW = flightW + (navInline ? navW + Pad : 0f)
                + (pilotInline ? pilotW + Pad : 0f) + (stripInline ? stripW + Pad : 0f);
            float flightH = Math.Min(bodyH, controlsHeight);
            float dockH = navInline ? DockNavballHeight : pilotInline ? PilotPanelHeight : flightH;
            float dockY = bottom - dockH;
            float dockX = centerLeft + (centerW - dockW) * .5f;
            PFViewUiRect Place(float panelWidth, float panelHeight)
            {
                var panel = new PFViewUiRect(dockX, dockY + (dockH - panelHeight) * .5f, panelWidth, panelHeight);
                dockX += panelWidth + Pad;
                return panel;
            }

            var nav = navInline ? Place(navW, DockNavballHeight) : default;
            var pilot = pilotInline ? Place(pilotW, PilotPanelHeight) : default;
            var strip = stripInline ? Place(stripW, 32f) : default;
            var flight = Place(flightW, flightH);

            float cx = centerLeft + centerW * .5f;
            float alertW = Math.Min(centerW, 560f);
            var alert = alertVisible && dockY - top >= alertReserve
                ? new PFViewUiRect(cx - alertW * .5f, top, alertW, 28f) : default;
            float toastTop = alert.Height > 0 ? alert.Y + alert.Height + Pad : top;
            var toastArea = new PFViewUiRect(cx - alertW * .5f, toastTop, alertW, Math.Max(0, dockY - Pad - toastTop));
            return new(new(0, 0, width, toolbarH), objects, details, radar, nav,
                new(0, height - statusH, width, statusH), strip, compact, navDrawer)
            {
                PilotPanel = pilot, ObjectActions = actions, ShipStatus = ship, Weapons = weapons,
                FlightControls = flight, Alert = alert, ToastArea = toastArea, Drawer = drawer, DrawerTab = drawerTab,
                ObjectsInDrawer = objectsDrawer
            };
        }
    }
}
