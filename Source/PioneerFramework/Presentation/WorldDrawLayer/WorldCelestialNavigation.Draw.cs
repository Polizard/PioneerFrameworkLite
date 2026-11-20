using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework;

public sealed class PF_WorldDrawLayer_CelestialNavigation : WorldDrawLayerBase
{
    public PF_WorldDrawLayer_CelestialNavigation() => dirty = false;
    public override bool Visible => WorldCelestialHost.Active;
    public override void Render() => WorldCelestialHost.Navigation?.Draw();
}

internal sealed partial class WorldCelestialNavigation
{
    private readonly Dictionary<WorldCelestialMember, Material> sphereMaterials = new();
    private readonly Dictionary<ExistingEphemerisParameters, DVector3[]> orbitPoints = new();
    private readonly List<Vector3> vertices = new();
    private readonly List<Color> colors = new();
    private readonly List<int> triangles = new();
    private readonly List<Rect> drawnLabels = new();
    private Mesh? lines;
    private Material? lineMaterial;

    private double DisplayRadius(WorldCelestialBody body)
    {
        var center = frame.ToView(body.Position);
        double depth = DVector3.Dot(center - (DVector3)camera.transform.position, (DVector3)camera.transform.forward);
        double floor = depth * Math.Tan(camera.fieldOfView * Math.PI / 360) * 5 / UI.screenHeight;
        return Math.Max(body.Radius * frame.Scale, floor);
    }

    internal bool DrawNativeLayer(WorldDrawLayerBase layer) =>
        layer is WorldDrawLayer_Terrain terrain && TryTerrainMatrix(terrain.planetLayer, out _);

    internal bool ShowFocusedLayer(PlanetLayer layer) =>
        !system && target != entryBody && (Zoom >= .12 || returnLayer == layer) && NativeLayer(Target) == layer;

    internal bool TryTerrainMatrix(PlanetLayer layer, out Matrix4x4 transform)
    {
        transform = Matrix4x4.identity;
        if (layer == null) return false;
        var body = layer == entryLayer ? snapshot.Bodies[entryBody] : Target;
        if ((body.Id != entryBody && (system || Zoom < .12) && returnLayer != layer) ||
            NativeLayer(body) != layer || !layer.Visible || layer.TilesCount == 0 ||
            visibility.GetValueOrDefault(body.Id) < .26 ||
            (returnLayer != layer && body.Radius / (frame.ToView(body.Position) -
                (DVector3)camera.transform.position).magnitude * frame.Scale < .025))
            return false;
        float scale = (float)(body.Radius * frame.Scale / layer.Radius);
        transform = Matrix4x4.TRS((Vector3)frame.ToView(body.Position),
            (Quaternion)(frame.Rotation * body.Motion.Orientation), Vector3.one * scale) *
            Matrix4x4.Translate(-layer.Origin);
        return true;
    }

    internal void Draw()
    {
        var sphere = PartShapeMeshes.For(InertiaTensor.Shape.Sphere, null);
        if (sphere == null) return;
        vertices.Clear();
        colors.Clear();
        triangles.Clear();
        var star = snapshot.Bodies[snapshot.StarId];
        foreach (var body in RenderedBodies())
        {
            Vector3 center = (Vector3)frame.ToView(body.Position);
            float radius = (float)DisplayRadius(body) * Mathf.Sqrt(visibility[body.Id]);
            var orientation = (Quaternion)(frame.Rotation * body.Motion.Orientation);
            DrawOrbit(body);
            if (body.Id == star.Id && DrawStellarVisual(center, radius, orientation)) continue;

            var layer = NativeLayer(body);
            bool nativeTerrain = layer != null && TryTerrainMatrix(layer, out _);
            var sun = (Vector3)DVector3.Rotate((star.Position - body.Position).normalized, frame.Rotation);
            var light = new Vector4(sun.x, sun.y, sun.z, 0);
            var material = SphereMaterial(body.Member);
            material.SetVector("_SunDir", light);
            var transform = Matrix4x4.TRS(center, orientation, Vector3.one * (2 * radius));
            bool bakedTerrain = !nativeTerrain && TryDrawBakedSurface(body, layer, transform, center, radius, light);
            float sink = bakedTerrain ? PFViewSurfaceBake.BaseSinkFactor : nativeTerrain ? .005f : 0f;
            Graphics.DrawMesh(sphere, transform * Matrix4x4.Scale(Vector3.one * (1 - sink)),
                material, WorldCameraManager.WorldLayer);
        }
        if (selectedRegion.Valid && TryTerrainMatrix(selectedRegion.Layer, out var matrix))
            DrawRegion(matrix);
        if (triangles.Count == 0) return;
        lines ??= new Mesh { name = "PF_WorldCelestialTrails" };
        lines.Clear();
        lines.SetVertices(vertices);
        lines.SetColors(colors);
        lines.SetTriangles(triangles, 0);
        lines.RecalculateBounds();
        lineMaterial ??= new Material(Shader.Find("PF_WorldCelestialTrail") ??
            Shader.Find("WorldVertexColorTransparent") ?? ShaderDatabase.WorldOverlayTransparent);
        Graphics.DrawMesh(lines, Matrix4x4.identity, lineMaterial, WorldCameraManager.WorldLayer);
    }

    private Material SphereMaterial(WorldCelestialMember member)
    {
        if (sphereMaterials.TryGetValue(member, out var cached)) return cached;
        var profile = member.Profile;
        bool gas = profile?.Appearance?.Style == CelestialSurfaceStyle.GasBands;
        var shader = gas ? Shader.Find("PF_PlanetBands") : Shader.Find("PFLit");
        var material = new Material(shader ?? ShaderDatabase.WorldOverlayCutout) { color = member.Color };
        material.mainTexture = (profile?.TexturePath is { } path ? ContentFinder<Texture2D>.Get(path, false) : null)
            ?? BaseContent.WhiteTex;
        material.SetVector("_SunColor", Vector4.one);
        if (gas)
        {
            var bands = PFViewVisualBindings.Bands(profile!);
            material.SetVector("_BandParams", bands.Parameters);
            material.SetVector("_BandColor", bands.Color);
        }
        sphereMaterials.Add(member, material);
        return material;
    }

    private void DrawOrbit(WorldCelestialBody body)
    {
        if (body.Member.Orbit is not { HasOrbit: true } orbit || body.ParentId is not { } parentId ||
            !snapshot.Bodies.TryGetValue(parentId, out var parent)) return;
        float pixelRadius = (float)(orbit.OrbitRadius / distance * UI.screenHeight /
            (2 * Math.Tan(camera.fieldOfView * Math.PI / 360)));
        float near = (float)(Zoom / .22);
        float alpha = .84f * visibility[body.Id] * visibility.GetValueOrDefault(parentId) *
            (pixelRadius / (pixelRadius + 9f)) / (1 + near * near);
        if (alpha <= 0f) return;
        if (!orbitPoints.TryGetValue(orbit, out var offsets))
        {
            offsets = new DVector3[129];
            for (int i = 0; i < offsets.Length - 1; i++)
                offsets[i] = orbit.OffsetAtEccentricAnomaly(i * (2 * Math.PI / (offsets.Length - 1)));
            offsets[^1] = offsets[0];
            orbitPoints.Add(orbit, offsets);
        }
        DVector3 bodyOffset = body.Position - parent.Position;
        Color ColorAt(DVector3 point)
        {
            Color color = body.Member.Color;
            double separation = (point - bodyOffset).magnitude / orbit.OrbitRadius;
            color.a = alpha * (float)(.04 + .96 * Math.Exp(-2.0 * separation));
            return color;
        }
        var previous = (Vector3)frame.ToView(parent.Position + offsets[0]);
        Color previousColor = ColorAt(offsets[0]);
        for (int i = 1; i < offsets.Length; i++)
        {
            var point = (Vector3)frame.ToView(parent.Position + offsets[i]);
            Color color = ColorAt(offsets[i]);
            AddLine(previous, point, previousColor, color, 1.8f);
            previous = point;
            previousColor = color;
        }
    }

    private void DrawRegion(Matrix4x4 matrix)
    {
        var polygon = new List<Vector3>(6);
        var layer = selectedRegion.Layer;
        layer.GetTileVertices(selectedRegion, polygon);
        if (polygon.Count == 0) return;
        Vector3 Point(Vector3 vertex) => matrix.MultiplyPoint3x4(layer.Origin + vertex * 1.008f);
        Vector3 previous = Point(polygon[^1]);
        foreach (var vertex in polygon)
        {
            Vector3 next = Point(vertex);
            AddLine(previous, next, Color.yellow, Color.yellow, 2);
            previous = next;
        }
    }

    private void AddLine(Vector3 a, Vector3 b, Color startColor, Color endColor, float pixels)
    {
        Vector3 fromCameraA = a - camera.transform.position;
        Vector3 fromCameraB = b - camera.transform.position;
        float depthA = Vector3.Dot(fromCameraA, camera.transform.forward);
        float depthB = Vector3.Dot(fromCameraB, camera.transform.forward);
        if (Mathf.Max(depthA, depthB) < camera.nearClipPlane || Mathf.Min(depthA, depthB) > camera.farClipPlane) return;
        Vector3 toLine = (fromCameraA + fromCameraB) * .5f;
        float depth = Mathf.Max((depthA + depthB) * .5f, camera.nearClipPlane);
        var side = Vector3.Cross(b - a, toLine).normalized *
            (pixels * depth * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad / 2) / UI.screenHeight);
        int start = vertices.Count;
        vertices.Add(a - side);
        vertices.Add(a + side);
        vertices.Add(b - side);
        vertices.Add(b + side);
        colors.Add(startColor);
        colors.Add(startColor);
        colors.Add(endColor);
        colors.Add(endColor);
        triangles.Add(start);
        triangles.Add(start + 2);
        triangles.Add(start + 1);
        triangles.Add(start + 1);
        triangles.Add(start + 2);
        triangles.Add(start + 3);
    }

    private void DisposeDrawing()
    {
        DisposeVisuals();
        foreach (var material in sphereMaterials.Values) UnityEngine.Object.Destroy(material);
        if (lines != null) UnityEngine.Object.Destroy(lines);
        if (lineMaterial != null) UnityEngine.Object.Destroy(lineMaterial);
    }

    internal void DrawGui()
    {
        if (Widgets.ButtonText(new Rect(12, 112, 126, 28),
                (system ? "PF_WorldCelestial_Back" : "PF_WorldCelestial_System").Translate()))
        {
            if (system) ShowEntryLocal();
            else ShowSystem();
        }
        if (Widgets.ButtonText(new Rect(146, 112, 126, 28), "PF_WorldCelestial_FullSystem".Translate()))
            ShowSystem();
        WorldCelestialHost.DrawPlanetViewLock(new Rect(280, 112, 180, 28));
        if (!ReferenceEquals(WorldCelestialHost.Navigation, this)) return;
        Widgets.Label(new Rect(12, 145, 260, 26), "PF_WorldCelestial_Zoom".Translate(Zoom.ToString("0.0000")));

        if (Event.current.type == EventType.Repaint)
        {
            var anchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleCenter;
            drawnLabels.Clear();
            foreach (var body in RenderedBodies().OrderByDescending(body => body.Id == selection)
                .ThenByDescending(DisplayRadius))
            {
                Vector3 center = (Vector3)frame.ToView(body.Position);
                Vector3 viewport = camera.WorldToViewportPoint(center);
                if (viewport.z < camera.nearClipPlane || viewport.z > camera.farClipPlane ||
                    viewport.x is < 0 or > 1 || viewport.y is < 0 or > 1) continue;
                float projectedRadius = (float)(DisplayRadius(body) / viewport.z * UI.screenHeight /
                    (2 * Math.Tan(camera.fieldOfView * Math.PI / 360)));
                Vector2 point = GenWorldUI.WorldToUIPosition(center);
                var label = new Rect(point.x - 78, point.y + projectedRadius + 4, 156, 24);
                if (drawnLabels.Any(other => other.Overlaps(label))) continue;
                drawnLabels.Add(label);
                var oldColor = GUI.color;
                Color labelColor = body.Id == selection ? Color.yellow : Color.white;
                labelColor.a = visibility[body.Id];
                GUI.color = labelColor;
                Widgets.Label(label, body.Member.Label);
                GUI.color = oldColor;
            }
            Text.Anchor = anchor;
        }

        if (selection is { } id && snapshot.Bodies.TryGetValue(id, out var selected))
            DrawInfo(selected);
    }

    private void DrawInfo(WorldCelestialBody body)
    {
        float width = Mathf.Min(310, UI.screenWidth - 36);
        var rect = new Rect(UI.screenWidth - width - 14, 155, width, Mathf.Min(397, UI.screenHeight - 200));
        if (Event.current.type == EventType.Repaint) Widgets.DrawWindowBackground(rect);
        var list = new Listing_Standard();
        list.Begin(rect.ContractedBy(12));
        list.Label(body.Member.Label + " · " + body.Member.Description.Category);
        list.Label("PF_WorldCelestial_Radius".Translate((body.Radius / 1000).ToString("N1")));
        if (body.Member.Description.GravitationalParameter is { } mu)
            list.Label("PF_WorldCelestial_Mass".Translate((mu / 6.67430e-11).ToString("0.###E+0")));
        if (body.ParentId is { } id && snapshot.Bodies.TryGetValue(id, out var parent))
            list.Label("PF_WorldCelestial_Distance".Translate(parent.Member.Label,
                ((body.Position - parent.Position).magnitude / 1000).ToString("N0")));
        if (body.Member.Orbit is { } orbit)
        {
            if (orbit.HasOrbit)
            {
                list.Label("PF_WorldCelestial_Orbit".Translate((orbit.OrbitRadius / 1000).ToString("N0"), orbit.Eccentricity.ToString("F3")));
                list.Label("PF_WorldCelestial_OrbitPeriod".Translate(orbit.OrbitPeriod.ToString("N0")));
            }
            if (orbit.DayLengthTicks > 0)
                list.Label("PF_WorldCelestial_Rotation".Translate(orbit.DayLengthTicks.ToString("N0")));
        }
        list.Label("PF_WorldCelestial_Satellites".Translate(snapshot.Bodies.Values.Count(child => child.ParentId == body.Id)));
        if (body.Member.Atmosphere.topAltitude > 0)
            list.Label("PF_WorldCelestial_Atmosphere".Translate((body.Member.Atmosphere.topAltitude / 1000).ToString("N1")));
        if (selectedRegion.Valid && body.Id == target)
        {
            var tile = selectedRegion.Layer[selectedRegion];
            list.Label("PF_WorldCelestial_Region".Translate(selectedRegion.tileId, tile.PrimaryBiome.LabelCap));
            if (tile is SurfaceTile surface)
            {
                list.Label("PF_WorldCelestial_Climate".Translate(surface.temperature.ToString("N1"), surface.rainfall.ToString("N0")));
                list.Label("PF_WorldCelestial_Elevation".Translate(surface.elevation.ToString("N0"), surface.hilliness.GetLabel()));
            }
        }
        if (surfaceErrors.TryGetValue(body.Id, out string? error))
        {
            list.Label(error);
            if (list.ButtonText("PF_WorldCelestial_RetrySurface".Translate()))
            {
                surfaceErrors.Remove(body.Id);
                surfaceAttempted.Remove(body.Id);
            }
        }
        else if (NativeLayer(body) is { } layer && list.ButtonText("PF_WorldCelestial_OpenSurface".Translate()))
        {
            OpenNativeSurface(body, layer);
        }
        list.End();
        if (rect.Contains(Event.current.mousePosition) && Event.current.type is
            EventType.MouseDown or EventType.MouseUp or EventType.MouseDrag or EventType.ScrollWheel)
            Event.current.Use();
    }
}
