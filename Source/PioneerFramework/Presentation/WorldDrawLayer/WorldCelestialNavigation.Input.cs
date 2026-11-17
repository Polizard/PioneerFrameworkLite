using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework;

internal sealed partial class WorldCelestialNavigation
{
    private int dragButton = -1;
    private int clickCount;
    private CelestialInstanceId? lastClicked;
    private float lastClickAt;
    private Vector2 lastClickPosition;
    private Vector2 down;
    private bool dragged;
    private PlanetTile selectedRegion = PlanetTile.Invalid;
    private CelestialInstanceId? surfaceQueued;
    private readonly Dictionary<CelestialInstanceId, string> surfaceErrors = new();
    private readonly HashSet<CelestialInstanceId> surfaceAttempted = new();

    internal void HandleInput(Event e)
    {
        if (e.rawType == EventType.MouseUp && e.button == dragButton && e.type == EventType.Used)
        {
            dragButton = -1;
            return;
        }
        if (Find.WindowStack.WindowsPreventCameraMotion || Find.WindowStack.IsOpen<FloatMenu>() ||
            (e.isMouse && (WorldCelestialHost.OverMainButtonBar() ||
                Find.WindowStack.GetWindowAt(UI.MousePositionOnUIInverted) != null)))
        {
            if (e.rawType == EventType.MouseUp && e.button == dragButton) dragButton = -1;
            return;
        }

        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            if (selectedRegion.Valid) ResetRegion();
            else if (system || localRoot != SubsystemOf(snapshot.Bodies[entryBody])) ShowEntryLocal();
            else
            {
                OpenBody(snapshot.Bodies[entryBody], NativeReturnZoom);
                ReturnToNative(entryLayer);
            }
            e.Use();
            return;
        }
        if (e.type == EventType.ScrollWheel)
        {
            returnLayer = null;
            double maximum = SystemExtent() * 18;
            desiredDistance = Math.Clamp(desiredDistance * Math.Exp(Math.Clamp(e.delta.y * .18, -1.5, 1.5)),
                Target.Radius / .88, maximum);
            e.Use();
            return;
        }
        if (e.type == EventType.MouseDown && e.button is >= 0 and <= 2)
        {
            if (dragButton == -1)
            {
                dragButton = e.button;
                down = e.mousePosition;
                dragged = false;
                clickCount = e.clickCount;
            }
            e.Use();
            return;
        }
        if (e.type == EventType.MouseDrag && dragButton is 1 or 2 && e.button == dragButton)
        {
            dragged |= (e.mousePosition - down).sqrMagnitude >= 16;
            if (dragged)
            {
                returnLayer = null;
                if (dragButton == 1)
                {
                    desiredRotation = Quaternion.AngleAxis(e.delta.x * .27f, desiredRotation * Vector3.up) *
                        Quaternion.AngleAxis(e.delta.y * .27f, desiredRotation * Vector3.right) * desiredRotation;
                }
                else
                {
                    fixedAim = Aim;
                    following = false;
                    double metersPerPixel = distance * 2 * Math.Tan(camera.fieldOfView * Math.PI / 360) / UI.screenHeight;
                    fixedAim += DVector3.Rotate((DVector3)(rotation * new Vector3(-e.delta.x, e.delta.y, 0)),
                        DQuaternion.Inverse(FrameRotation)) * metersPerPixel;
                }
            }
            e.Use();
            return;
        }
        if (e.type != EventType.MouseUp || e.button != dragButton) return;
        int button = dragButton;
        dragButton = -1;
        if (!dragged && button is 0 or 1)
        {
            var ray = MouseRay();
            var body = Pick(ray, out double hit);
            if (button == 1) OpenMenu(body);
            else
            {
                float now = Time.realtimeSinceStartup;
                bool doubleClick = body != null && body.Id == lastClicked &&
                    (Math.Max(clickCount, e.clickCount) > 1 ||
                     (now - lastClickAt < .32f && (e.mousePosition - lastClickPosition).sqrMagnitude < 49));
                lastClicked = doubleClick ? null : body?.Id;
                lastClickAt = now;
                lastClickPosition = e.mousePosition;
                selection = body?.Id;
                ResetRegion();
                if (doubleClick) OpenBody(body!, LocalFocusZoom);
                else if (body != null) PickRegion(body, ray.GetPoint((float)hit));
            }
        }
        e.Use();
    }

    private double SystemExtent()
    {
        var star = snapshot.Bodies[snapshot.StarId];
        return Math.Max(Target.Radius * 200, snapshot.Bodies.Values.Max(body =>
            (body.Position - star.Position).magnitude + body.Radius));
    }

    private Ray MouseRay()
    {
        Vector2 point = UI.MousePositionOnUI;
        return camera.ViewportPointToRay(new Vector3(point.x / UI.screenWidth, point.y / UI.screenHeight, 0));
    }

    private WorldCelestialBody? Pick(Ray ray, out double depth)
    {
        depth = double.PositiveInfinity;
        WorldCelestialBody? picked = null;
        foreach (var body in VisibleBodies())
        {
            DVector3 center = frame.ToView(body.Position);
            double exact = WorldCelestialFrame.RaySphere((DVector3)ray.origin, (DVector3)ray.direction,
                center, DisplayRadius(body));
            double hit = exact;
            if (!double.IsFinite(exact))
            {
                Vector3 projected = camera.WorldToViewportPoint((Vector3)center);
                Vector2 mouse = UI.MousePositionOnUI;
                Vector2 pixels = new(projected.x * UI.screenWidth, projected.y * UI.screenHeight);
                if (projected.z > camera.nearClipPlane && (pixels - mouse).sqrMagnitude <= 9 * 9)
                    hit = projected.z;
            }
            if (hit >= depth || hit > camera.farClipPlane) continue;
            depth = hit;
            picked = body;
        }
        return picked;
    }

    private void OpenMenu(WorldCelestialBody? body)
    {
        var options = new List<FloatMenuOption>();
        if (body != null)
        {
            options.Add(new("PF_WorldCelestial_FocusClose".Translate(body.Member.Label), () => OpenBody(body, .88)));
            if (body.Id != snapshot.StarId)
                options.Add(new("PF_WorldCelestial_FocusSystem".Translate(body.Member.Label), () => OpenBody(body, LocalFocusZoom)));
            if (NativeLayer(body) is { } layer)
                options.Add(new("PF_WorldCelestial_OpenSurface".Translate(), () => OpenNativeSurface(body, layer)));
        }
        options.Add(new("PF_WorldCelestial_ChooseBody".Translate(), OpenBodyList));
        options.Add(new("PF_WorldCelestial_FullSystem".Translate(), ShowSystem));
        options.Add(new("PF_WorldCelestial_Back".Translate(), ShowEntryLocal));
        Find.WindowStack.Add(new FloatMenu(options));
    }

    private void OpenBodyList()
    {
        Find.WindowStack.Add(new FloatMenu(VisibleBodies().OrderBy(body => body.Member.Label, StringComparer.Ordinal)
            .Select(body => new FloatMenuOption(body.Member.Label, () => OpenBody(body, LocalFocusZoom))).ToList()));
    }

    private void MaybePrepareSurface()
    {
        if (system || !following || DesiredZoom < NativeReturnZoom || Zoom < NativeReturnZoom || surfaceQueued.HasValue ||
            surfaceAttempted.Contains(target) ||
            runtime.GetCapabilities<ICelestialSurfaceCapability>(target).Count != 1) return;
        var requested = target;
        surfaceQueued = requested;
        LongEventHandler.QueueLongEvent(() =>
        {
            surfaceQueued = null;
            if (!ReferenceEquals(WorldCelestialHost.Navigation, this) || !WorldCelestialHost.Active ||
                target != requested || system || !following || DesiredZoom < NativeReturnZoom) return;
            surfaceAttempted.Add(requested);
            var context = new CelestialOperationContext("world-view/surface/" + Guid.NewGuid().ToString("N"),
                runtime.WorldSessionId, runtime.CaptureCatalog().Revision);
            var result = runtime.RequestSurface(new(context, requested));
            if (result.Operation.Status == CelestialOperationStatus.Rejected)
                surfaceErrors[requested] = string.Join(", ", result.Operation.Errors.Select(error => error.Code));
        }, "GeneratingPlanet", false, exception =>
        {
            surfaceQueued = null;
            surfaceErrors[requested] = exception.Message;
            Log.Error("[PFL World Celestial] " + exception);
        });
    }

    private void PickRegion(WorldCelestialBody body, Vector3 point)
    {
        var layer = NativeLayer(body);
        if (body.Id != target || Zoom < NativeReturnZoom || layer == null || !TryTerrainMatrix(layer, out var transform))
            return;
        Vector3 direction = (transform.inverse.MultiplyPoint3x4(point) - layer.Origin).normalized;
        float best = float.NegativeInfinity;
        for (int tile = 0; tile < layer.TilesCount; tile++)
        {
            float dot = Vector3.Dot(direction, layer.GetTileCenter(tile).normalized);
            if (dot <= best) continue;
            best = dot;
            selectedRegion = new PlanetTile(tile, layer);
        }
    }

    private void ResetRegion() => selectedRegion = PlanetTile.Invalid;
}
