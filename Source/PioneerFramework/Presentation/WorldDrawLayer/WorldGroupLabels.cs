using System;
using System.Collections.Generic;
using System.Globalization;
using PioneerFramework.Launch;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Observation = PioneerFramework.WorldGroupLabelCore.Observation;

namespace PioneerFramework;

/// <summary>世界地图的会话内标注。失联后只使用最近一次已知观测。</summary>
internal static class WorldGroupLabels
{
    private static readonly WorldGroupLabelPins pins = new();
    private static readonly List<WorldGroupLabelCore.Hit> hits = new();
    private static readonly Dictionary<int, Observation> observations = new();
    private static readonly List<int> active = new();
    private static readonly List<int> expired = new();
    private static readonly List<Rect> occupied = new();
    private static readonly Color Fluorescent = new(.93f, 1f, .12f, 1f);
    private static Game? game;
    private static World? world;
    private static float nextRefresh;

    private static void EnsureContext()
    {
        if (ReferenceEquals(game, Current.Game) && ReferenceEquals(world, Find.World)) return;
        game = Current.Game;
        world = Find.World;
        pins.Clear();
        observations.Clear();
        hits.Clear();
        nextRefresh = 0;
    }

    internal static void TogglePinned(int id)
    {
        EnsureContext();
        pins.Toggle(id);
        nextRefresh = 0;
    }

    internal static bool ToggleAt(Vector2 point)
    {
        EnsureContext();
        int id = WorldGroupLabelCore.Pick(point, hits);
        if (id < 0) return false;
        TogglePinned(id);
        return true;
    }

    internal static void Draw(SpaceGameComp space, int selectedId, WorldCelestialNavigation? navigation, Rect? panel)
    {
        EnsureContext();
        if (Event.current.type != EventType.Repaint || Find.WorldCamera is not { } camera) return;
        var manager = Current.Game?.GetComponent<CelestialBodyManager>();
        if (manager == null) return;
        int bodyId = ViewFrame.ViewedBodyId;
        int frameId = manager.GetReferenceFrameId(bodyId);
        if (frameId < 0) return;
        var sensor = Current.Game?.GetComponent<SensorManager>();
        double tick = SpaceGroupVisualManager.PhysicsTickNow();
        active.Clear();
        if (selectedId >= 0) active.Add(selectedId);
        foreach (int id in pins.Ids)
            if (id != selectedId) active.Add(id);
        expired.Clear();
        foreach (int id in observations.Keys)
            if (!active.Contains(id)) expired.Add(id);
        foreach (int id in expired) observations.Remove(id);

        bool refresh = Time.realtimeSinceStartup >= nextRefresh;
        if (refresh) nextRefresh = Time.realtimeSinceStartup + .1f;
        hits.Clear();
        occupied.Clear();
        occupied.Add(new Rect(8, 105, navigation == null ? 330 : 460, 68));
        if (panel is { } panelRect) occupied.Add(panelRect.ExpandedBy(6));
        if (navigation != null)
            occupied.Add(new Rect(UI.screenWidth - 330, 145, 322, Math.Min(410, UI.screenHeight - 190)));
        var viewport = new Rect(8, 82, UI.screenWidth - 16, UI.screenHeight - 130);

        var oldFont = Text.Font;
        var oldAnchor = Text.Anchor;
        bool oldWrap = Text.WordWrap;
        Color oldColor = GUI.color;
        try
        {
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = false;
            foreach (int id in active)
            {
                var group = space.GetGroupById(id);
                bool visible = group is { IsActive: true, IsSurfaceAnchor: false } &&
                    manager.GetReferenceFrameId(group.CelestialBodyId) == frameId &&
                    (sensor == null || SensorManager.DebugShowGroundTruth || sensor.PlayerCanSee(group));
                bool hasPrevious = observations.TryGetValue(id, out var observation);
                if (visible && (refresh || !hasPrevious || observation!.BodyId != bodyId))
                {
                    observation = WorldGroupLabelCore.PreserveKnown(observation,
                        Capture(space, manager, sensor, group!, bodyId, frameId, tick));
                    observations[id] = observation;
                }
                if (observation == null || observation.BodyId != bodyId) continue;
                Vector3 point = visible && !observation.Stale
                    ? SpaceGroupVisualManager.GetDisplayedGroupPosition(group!, sensor)
                    : SpaceGroupVisualManager.ToOverlayView(observation.Position, tick);
                Vector3 projected = camera.WorldToViewportPoint(point);
                if (projected.z <= camera.nearClipPlane || projected.z >= camera.farClipPlane ||
                    projected.x is < 0 or > 1 || projected.y is < 0 or > 1 ||
                    !SpaceGroupVisualManager.IsViewPosVisible(point, 2f)) continue;

                bool stale = !visible || observation.Stale;
                Vector2 icon = GenWorldUI.WorldToUIPosition(point);
                Color color = Fluorescent;
                color.a = stale ? .65f : 1;
                DrawBrackets(icon, color);
                int hitIndex = hits.Count;
                hits.Add(new(id, icon, default));
                string title = observation.Title + (stale ? " · " + "PF_WorldGroup_Stale".Translate() : "");
                string height = "PF_WorldGroup_Height".Translate(observation.Height);
                string speed = "PF_WorldGroup_Speed".Translate(observation.Speed);
                var size = new Vector2(Math.Max(130, Math.Max(Text.CalcSize(title).x,
                    Math.Max(Text.CalcSize(height).x, Text.CalcSize(speed).x))) + 8, 63);
                if (!WorldGroupLabelCore.TryPlace(icon, size, viewport, occupied, out var label)) continue;
                hits[hitIndex] = new(id, icon, label);
                occupied.Add(label.ExpandedBy(3));
                Vector2 end = new(Math.Clamp(icon.x, label.xMin, label.xMax),
                    Math.Clamp(icon.y, label.yMin, label.yMax));
                GUI.color = new Color(color.r, color.g, color.b, color.a * .45f);
                Widgets.DrawLine(icon, end, GUI.color, 1);
                DrawText(new Rect(label.x + 4, label.y, label.width - 8, 21), title, color);
                DrawText(new Rect(label.x + 4, label.y + 21, label.width - 8, 21), height, color);
                DrawText(new Rect(label.x + 4, label.y + 42, label.width - 8, 21), speed, color);
            }
        }
        finally
        {
            Text.Font = oldFont;
            Text.Anchor = oldAnchor;
            Text.WordWrap = oldWrap;
            GUI.color = oldColor;
        }
    }

    private static Observation Capture(SpaceGameComp space, CelestialBodyManager manager, SensorManager? sensor,
        SpaceEntityGroup group, int bodyId, int frameId, double tick)
    {
        var player = Faction.OfPlayer;
        bool identified = SensorManager.DebugShowGroundTruth || player == null ||
            group.FactionId == player.loadID || !group.IsStealth;
        if (!identified)
        {
            var known = default(Contact);
            bool contact = sensor != null && sensor.TryGetContact(player!.loadID, group.GroupId, out known);
            return new(bodyId, contact ? known.position : group.CurrentOrbit.positionSystem.v,
                "G??? · ???", "???", "???",
                contact && sensor!.ContactPhaseOf(player!.loadID, group.GroupId) != TrackPhase.Live, false);
        }

        DVector3 position = group.IsCircularOrbit ? group.GetCircularPositionAtTick(tick) : group.CurrentOrbit.positionSystem.v;
        DVector3 velocity = group.IsCircularOrbit ? group.GetCircularVelocityAtTick(tick) : group.CurrentOrbit.velocitySystem.v;
        var body = manager.GetBodyMotion(bodyId, tick);
        var root = manager.GetBodyMotion(frameId, tick);
        var metrics = WorldGroupLabelCore.Measure(position, velocity, body.Position - root.Position,
            body.LinearVelocity - root.LinearVelocity, GetSurfaceState.BodyRadius(manager, bodyId));
        string id = "G" + group.GroupId.ToString(CultureInfo.InvariantCulture);
        string relation = Relation(group.FactionId);
        return new(bodyId, position, id + " · " + relation + " " + TypeName(space, group),
            FormatHeight(metrics.Height), PFViewUiObjectListCore.FormatSpeed(metrics.Speed), false, true);
    }

    private static string FormatHeight(double height) =>
        height < 0 ? "−" + PFViewUiObjectListCore.FormatDistance(-height) : PFViewUiObjectListCore.FormatDistance(height);

    private static string Relation(int factionId)
    {
        var player = Faction.OfPlayer;
        var faction = FactionResolver.Get(factionId);
        string key = factionId == player?.loadID ? "PF_WorldGroup_Friendly" :
            faction != null && player != null && faction.HostileTo(player) ? "PF_WorldGroup_Hostile" : "PF_WorldGroup_Neutral";
        return key.Translate();
    }

    private static string TypeName(SpaceGameComp space, SpaceEntityGroup group)
    {
        var vehicle = Current.Game?.GetComponent<AutonomousVehicleManager>()?.GetRecord(group.GroupId);
        if (vehicle != null) return "PF_WorldGroup_Missile".Translate();
        bool parts = false;
        bool alive = false;
        foreach (int id in group.GetEntityIds())
        {
            var entity = space.GetEntityById(id);
            if (entity == null || entity.Parts.Count == 0) continue;
            parts = true;
            alive |= entity.HasAlivePart;
        }
        string key = parts && !alive ? "PF_WorldGroup_Wreck" :
            !parts ? "PF_WorldGroup_Group" :
            group.IsMovable ? "PF_WorldGroup_Ship" : "PF_WorldGroup_Station";
        return key.Translate();
    }

    private static void DrawBrackets(Vector2 point, Color color)
    {
        float x = Mathf.Round(point.x);
        float y = Mathf.Round(point.y);
        for (int side = -1; side <= 1; side += 2)
        {
            float edge = x + side * 13;
            DrawPixel(new Rect(edge - 1, y - 12, 2, 24), color);
            float start = side < 0 ? edge : edge - 5;
            DrawPixel(new Rect(start, y - 12, 6, 2), color);
            DrawPixel(new Rect(start, y + 10, 6, 2), color);
        }
    }

    private static void DrawPixel(Rect rect, Color color)
    {
        Widgets.DrawBoxSolid(rect.ExpandedBy(2), new Color(color.r, color.g, color.b, color.a * .10f));
        Widgets.DrawBoxSolid(rect, color);
    }

    private static void DrawText(Rect rect, string text, Color color)
    {
        GUI.color = new Color(0, 0, 0, .8f);
        Widgets.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text);
        GUI.color = new Color(color.r, color.g, color.b, color.a * .16f);
        Widgets.Label(new Rect(rect.x - 1, rect.y, rect.width, rect.height), text);
        GUI.color = color;
        Widgets.Label(rect, text);
    }
}
