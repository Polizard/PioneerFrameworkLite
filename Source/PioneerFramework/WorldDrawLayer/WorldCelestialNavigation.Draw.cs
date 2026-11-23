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

    private const double NativeTerrainStart = .07;
    private const double NativeTerrainFull = .16;

    private (CelestialInstanceId Body, PlanetLayer Layer, double Zoom)? SelectNativeSurface(DVector3 cameraPosition)
    {
        if (returnLayer is { } returning)
            return (target, returning, Target.Radius / (Target.Position - cameraPosition).magnitude);

        // Hold the outgoing planet or moon while its apparent size finishes the reverse fade.
        if (!system && NativeSurfaceAt(target, cameraPosition) is { } incoming) return incoming;
        return nativeSurface is { } previous && (system || previous.Body != target)
            ? NativeSurfaceAt(previous.Body, cameraPosition) : null;
    }

    private (CelestialInstanceId Body, PlanetLayer Layer, double Zoom)? NativeSurfaceAt(CelestialInstanceId id,
        DVector3 cameraPosition)
    {
        if (!snapshot.Bodies.TryGetValue(id, out var body) || visibility.GetValueOrDefault(id) < .99f)
            return null;
        double zoom = body.Radius / (body.Position - cameraPosition).magnitude;
        if (zoom < NativeTerrainStart || NativeLayer(body) is not { } layer || layer.TilesCount == 0 ||
            !layer.Def.worldDrawLayers.Any(typeof(WorldDrawLayer_Terrain).IsAssignableFrom))
            return null;
        return (id, layer, zoom);
    }

    internal static float SurfacePreviewOpacity(double zoom, bool nativeTerrain)
    {
        if (!nativeTerrain) return 1f;
        double blend = Math.Clamp((zoom - NativeTerrainStart) / (NativeTerrainFull - NativeTerrainStart), 0, 1);
        return (float)(1 - blend * blend * (3 - 2 * blend));
    }

    internal bool DrawNativeLayer(WorldDrawLayerBase layer) => layer is WorldDrawLayer worldLayer &&
        layer is (WorldDrawLayer_Terrain or WorldDrawLayer_UngeneratedPlanetParts or WorldDrawLayer_MoonUngeneratedPlanetParts
            or WorldDrawLayer_Hills or WorldDrawLayer_Rivers or WorldDrawLayer_Roads) &&
        UsesNativeTerrain(worldLayer.planetLayer);

    internal bool ShowFocusedLayer(PlanetLayer layer) => ReferenceEquals(nativeSurface?.Layer, layer);

    private bool UsesNativeTerrain(PlanetLayer layer) => ShowFocusedLayer(layer) && layer.Visible && layer.TilesCount > 0;

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
            bool nativeTerrain = body.Id == nativeSurface?.Body && layer != null && UsesNativeTerrain(layer);
            bool dayNight = layer == null || !layer.IsRootSurface || Find.PlaySettings.usePlanetDayNightSystem;
            var toSun = (star.Position - body.Position).normalized;
            var fixedSun = dayNight ? DVector3.Rotate(toSun, DQuaternion.Inverse(body.Motion.Orientation)) : DVector3.zero;
            if (nativeTerrain)
            {
                // The original terrain samples the global propagation direction in the native render frame.
                Shader.SetGlobalVector(ShaderPropertyIDs.PlanetSunLightDirection, (Vector3)(-fixedSun));
                Shader.SetGlobalFloat(ShaderPropertyIDs.PlanetSunLightEnabled, dayNight ? 1f : 0f);
                radius = (float)(body.Radius * frame.Scale);
            }
            if (layer != null && layer.TilesCount > 0 &&
                DrawSurfacePreview(sphere, body, layer, center, radius, orientation, (Vector3)fixedSun, nativeTerrain)) continue;

            var sun = body.Member.Profile?.Appearance?.Style == CelestialSurfaceStyle.GasBands
                ? (Vector3)DVector3.Rotate(toSun, frame.Rotation) : (Vector3)fixedSun;
            var light = new Vector4(sun.x, sun.y, sun.z, 0);
            var material = SphereMaterial(body.Member);
            material.SetVector("_SunDir", light);
            var transform = Matrix4x4.TRS(center, orientation, Vector3.one * (2 * radius));
            float sink = nativeTerrain ? .005f : 0f;
            Graphics.DrawMesh(sphere, transform * Matrix4x4.Scale(Vector3.one * (1 - sink)),
                material, WorldCameraManager.WorldLayer);
        }
        if (selectedRegion.Valid && UsesNativeTerrain(selectedRegion.Layer)) DrawRegion();
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

    private void DrawRegion()
    {
        var polygon = new List<Vector3>(6);
        var layer = selectedRegion.Layer;
        layer.GetTileVertices(selectedRegion, polygon);
        if (polygon.Count == 0) return;
        Vector3 Point(Vector3 vertex) => layer.Origin + vertex * 1.008f;
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
