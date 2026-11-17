using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using PioneerFramework.Launch;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework;

internal sealed partial class WorldCelestialNavigation
{
    private const double LocalEntryZoom = .16;
    private const double NativeReturnZoom = .2;
    private const double LocalFocusZoom = .04;
    private const float MaxViewDistance = 1400f;

    private readonly CelestialBodyManager manager;
    private readonly CelestialRuntime runtime;
    private readonly Camera camera;
    private readonly WorldCameraDriver driver;
    private readonly PlanetLayer entryLayer;
    private readonly WorldCelestialSampler sampler;
    private readonly CelestialInstanceId entryBody;
    private readonly float originalNear;
    private readonly float originalFar;
    private readonly float originalAltitude;
    private readonly Quaternion originalSphereRotation;
    private WorldCelestialSnapshot snapshot;
    private WorldCelestialFrame frame;
    private CelestialInstanceId target;
    private CelestialInstanceId localRoot;
    private CelestialInstanceId? selection;
    private DVector3 aimOffset;
    private DVector3 fixedAim;
    private double distance;
    private double desiredDistance;
    private Quaternion rotation;
    private Quaternion desiredRotation;
    private bool system;
    private bool following = true;
    private bool inhibitSystem;
    private double autoSystemZoom = .010;
    private PlanetLayer? returnLayer;
    private PlanetTile? returnTile;
    private double inertialBlend;
    private readonly Dictionary<CelestialInstanceId, float> visibility = new();

    private WorldCelestialNavigation(CelestialBodyManager manager, CelestialRuntime runtime, Camera camera,
        WorldCameraDriver driver, PlanetLayer layer, CelestialInstanceId id, WorldCelestialSampler sampler,
        WorldCelestialSnapshot snapshot)
    {
        this.manager = manager;
        this.runtime = runtime;
        this.camera = camera;
        this.driver = driver;
        this.sampler = sampler;
        entryLayer = layer;
        entryBody = target = id;
        this.snapshot = snapshot;
        originalNear = camera.nearClipPlane;
        originalFar = camera.farClipPlane;
        originalAltitude = driver.altitude;
        originalSphereRotation = driver.sphereRotation;
        rotation = desiredRotation = camera.transform.rotation;
        localRoot = SubsystemOf(snapshot.Bodies[id]);
        double nativeScale = layer.Radius / snapshot.Bodies[id].Radius;
        Vector3 relative = camera.transform.position - layer.Origin;
        distance = desiredDistance = Math.Max(layer.Radius * 1.01, -Vector3.Dot(relative, camera.transform.forward)) / nativeScale;
        aimOffset = DVector3.Rotate((DVector3)relative + (DVector3)camera.transform.forward * (distance * nativeScale),
            DQuaternion.Inverse(FrameRotation)) / nativeScale;
        foreach (var body in VisibleBodies()) visibility.Add(body.Id, 1);
        ApplyCamera();
    }

    internal static double NativeZoom(PlanetLayer layer, Camera camera) =>
        layer.Radius / Math.Max(layer.Radius, (camera.transform.position - layer.Origin).magnitude);

    internal static WorldCelestialNavigation? TryCreate(CelestialBodyManager manager, CelestialRuntime runtime,
        PlanetLayer layer, Camera camera, WorldCameraDriver driver)
    {
        int handle = GetSurfaceState.GetBodyForLayer(manager, layer);
        var description = handle < 0 ? null : runtime.CaptureCatalog().Bodies.FirstOrDefault(body => body.Handle == handle);
        if (description == null) return null;
        var id = description.Id;
        var sampler = new WorldCelestialSampler(runtime, id);
        var result = sampler.Capture(RenderSeconds);
        if (!result.Success || !result.Value.Bodies.ContainsKey(id) || result.Value.StarId == id) return null;
        return new(manager, runtime, camera, driver, layer, id, sampler, result.Value);
    }

    private static double RenderSeconds => Current.Game?.GetComponent<TimeScaleManager>()?.GetRenderTick() ?? double.NaN;
    private WorldCelestialBody Target => snapshot.Bodies[target];
    private double Zoom => Target.Radius / distance;
    private double DesiredZoom => Target.Radius / desiredDistance;
    internal bool ShowsStar => system;
    internal bool ShowsEntrySubsystem => !system && localRoot == SubsystemOf(snapshot.Bodies[entryBody]);
    private DQuaternion FrameRotation
    {
        get
        {
            var fixedRotation = DQuaternion.Inverse(snapshot.Bodies[entryBody].Motion.Orientation);
            if (inertialBlend <= 0) return fixedRotation;
            if (inertialBlend >= 1) return DQuaternion.identity;
            Quaternion blended = Quaternion.Slerp((Quaternion)fixedRotation, Quaternion.identity, (float)inertialBlend);
            double length = Math.Sqrt((double)blended.x * blended.x + (double)blended.y * blended.y +
                (double)blended.z * blended.z + (double)blended.w * blended.w);
            return new(blended.x / length, blended.y / length, blended.z / length, blended.w / length);
        }
    }
    private DVector3 Aim => following ? Target.Position + aimOffset : fixedAim;
    private PlanetLayer? NativeLayer(WorldCelestialBody body) => GetSurfaceState.GetLayerForBody(manager, body.Member.Description.Handle);

    internal Vector3 EntryCenter => (Vector3)frame.ToView(snapshot.Bodies[entryBody].Position);
    internal float EntryRadius => (float)(snapshot.Bodies[entryBody].Radius * frame.Scale);
    internal Vector3 ProjectEntryPoint(Vector3 fixedPoint)
    {
        var body = snapshot.Bodies[entryBody];
        var delta = DVector3.Rotate((DVector3)fixedPoint * (body.Radius / entryLayer.Radius),
            frame.Rotation * body.Motion.Orientation) * frame.Scale;
        return EntryCenter + (Vector3)delta;
    }

    internal bool ValidFor(CelestialRuntime candidate, Camera candidateCamera, PlanetLayer layer) =>
        ReferenceEquals(runtime, candidate) && camera == candidateCamera && layer == entryLayer && !runtime.IsRestoring;

    internal bool Update()
    {
        var sample = sampler.Capture(RenderSeconds);
        if (!sample.Success || !sample.Value.Bodies.ContainsKey(entryBody)) return false;
        snapshot = sample.Value;
        if (!snapshot.Bodies.ContainsKey(target) || !snapshot.Bodies.ContainsKey(localRoot)) return false;

        float t = 1f - Mathf.Exp(-12f * Mathf.Min(.1f, Time.unscaledDeltaTime));
        distance = Math.Exp(Math.Log(distance) + (Math.Log(desiredDistance) - Math.Log(distance)) * t);
        aimOffset *= 1 - t;
        rotation = Quaternion.Slerp(rotation, desiredRotation, t);
        double blendTarget = system || localRoot != SubsystemOf(snapshot.Bodies[entryBody]) ? 1 : 0;
        inertialBlend += (blendTarget - inertialBlend) * t;
        if (Math.Abs(blendTarget - inertialBlend) < 1e-5) inertialBlend = blendTarget;

        if (returnLayer != null && Math.Abs(Math.Log(distance / desiredDistance)) < .012 &&
            aimOffset.magnitude < Target.Radius * .015 && Quaternion.Angle(rotation, desiredRotation) < .5f)
            return false;

        if (!system && returnLayer == null)
        {
            if (WorldCelestialFrame.CanRearm(Zoom, autoSystemZoom)) inhibitSystem = false;
            if (WorldCelestialFrame.ShouldEnterSystem(Zoom, inhibitSystem, autoSystemZoom) &&
                WorldCelestialFrame.ShouldEnterSystem(DesiredZoom, inhibitSystem, autoSystemZoom))
                ShowSystem();
            else if (following && Zoom >= NativeReturnZoom && DesiredZoom >= NativeReturnZoom)
            {
                if (NativeLayer(Target) is { } layer) ReturnToNative(layer);
                else MaybePrepareSurface();
            }
        }

        UpdateVisibility(t);
        ApplyCamera();
        BakeVisibleSurface();
        return true;
    }

    private void ApplyCamera()
    {
        double nativeScale = entryLayer.Radius / snapshot.Bodies[entryBody].Radius;
        double scale = Math.Min(nativeScale, MaxViewDistance / distance);
        frame = new(Aim, (DVector3)entryLayer.Origin, FrameRotation, scale);
        camera.transform.rotation = rotation;
        camera.transform.position = entryLayer.Origin - camera.transform.forward * (float)(distance * scale);
        double far = 0;
        foreach (var body in RenderedBodies())
            far = Math.Max(far, (frame.ToView(body.Position) - (DVector3)camera.transform.position).magnitude + body.Radius * scale);
        camera.nearClipPlane = Mathf.Clamp((float)(distance * scale * .001), .01f, originalNear);
        camera.farClipPlane = Mathf.Max(originalFar, (float)Math.Min(1e8, far * 1.15 + 50));
    }

    internal void Restore(bool complete)
    {
        DisposeDrawing();
        if (camera == null || driver == null) return;
        camera.nearClipPlane = originalNear;
        camera.farClipPlane = originalFar;
        if (Find.WorldSelector?.SelectedLayer != entryLayer) return;

        if (complete && returnLayer is { } layer && snapshot.Bodies.TryGetValue(target, out var body) &&
            ReferenceEquals(NativeLayer(body), layer))
        {
            Quaternion fixedRotation = Quaternion.Inverse((Quaternion)(FrameRotation * body.Motion.Orientation)) * rotation;
            float altitude = (float)(distance * layer.Radius / body.Radius) - layer.ExtraCameraAltitude;
            WorldCelestialHost.PlaceNative(driver, layer, fixedRotation, altitude);
            if (returnTile is { Valid: true } tile && tile.Layer == layer)
                Find.WorldSelector.SelectedTile = tile;
        }
        else
            WorldCelestialHost.PlaceNative(driver, entryLayer, Quaternion.Inverse(originalSphereRotation), originalAltitude);
    }

    private CelestialInstanceId SubsystemOf(WorldCelestialBody body)
    {
        if (body.ParentId is not { } id || id == snapshot.StarId) return body.Id;
        while (snapshot.Bodies.TryGetValue(id, out var parent) && parent.ParentId is { } next && next != snapshot.StarId)
            id = next;
        return id;
    }

    private IEnumerable<WorldCelestialBody> VisibleBodies() => system
        ? snapshot.Bodies.Values.Where(body => body.Id == snapshot.StarId || body.ParentId == snapshot.StarId)
        : snapshot.Bodies.Values.Where(body => body.Id == localRoot ||
            (body.Id != snapshot.StarId && SubsystemOf(body) == localRoot));

    private IEnumerable<WorldCelestialBody> RenderedBodies() =>
        snapshot.Bodies.Values.Where(body => visibility.GetValueOrDefault(body.Id) > 0);

    private void UpdateVisibility(float amount)
    {
        var wanted = VisibleBodies().Select(body => body.Id).ToHashSet();
        foreach (var body in snapshot.Bodies.Values)
        {
            float targetVisibility = wanted.Contains(body.Id) ? 1 : 0;
            float value = Mathf.Lerp(visibility.GetValueOrDefault(body.Id), targetVisibility, amount);
            if (value < .015f && targetVisibility == 0) visibility.Remove(body.Id);
            else visibility[body.Id] = value > .999f ? 1 : value;
        }
    }

    private void Focus(WorldCelestialBody body, double zoom)
    {
        DVector3 previousAim = Aim;
        target = body.Id;
        selection = body.Id;
        following = true;
        aimOffset = previousAim - body.Position;
        desiredDistance = body.Radius / Math.Clamp(zoom, .000001, .88);
        returnLayer = null;
        returnTile = null;
        ResetRegion();
    }

    internal void ShowSystem()
    {
        system = true;
        var star = snapshot.Bodies[snapshot.StarId];
        Focus(star, LocalFocusZoom);
        double extent = star.Radius;
        foreach (var body in VisibleBodies())
            extent = Math.Max(extent, Math.Max((body.Position - star.Position).magnitude,
                body.Member.Orbit?.ApoapsisRadius ?? 0) + body.Radius);
        desiredDistance = FitDistance(extent);
        DVector3 normal = VisibleBodies().FirstOrDefault(body => body.Member.Orbit != null)?.Member.Orbit?.OrbitNormal ?? DVector3.up;
        Vector3 axis = (Vector3)normal.normalized;
        if (axis.sqrMagnitude < .001f) axis = Vector3.up;
        Vector3 up = Vector3.ProjectOnPlane(rotation * Vector3.up, axis);
        if (up.sqrMagnitude < .001f) up = Vector3.ProjectOnPlane(rotation * Vector3.right, axis);
        desiredRotation = Quaternion.LookRotation(axis, up.normalized);
    }

    internal void ShowEntryLocal()
    {
        system = false;
        var body = snapshot.Bodies[entryBody];
        localRoot = SubsystemOf(body);
        Focus(body, LocalFocusZoom);
        FitLocalOrbitExtent(body);
        inhibitSystem = true;
        desiredRotation = Quaternion.Inverse(originalSphereRotation);
    }

    private bool OpenBody(WorldCelestialBody body, double zoom)
    {
        if (!ReferenceEquals(WorldCelestialHost.Navigation, this) || !WorldCelestialHost.Active ||
            !snapshot.Bodies.TryGetValue(body.Id, out var current)) return false;
        body = current;
        if (body.Id == snapshot.StarId)
            system = true;
        else
        {
            system = false;
            localRoot = SubsystemOf(body);
            inhibitSystem = true;
        }
        Focus(body, zoom);
        if (!system)
        {
            autoSystemZoom = .010;
            if (zoom == LocalFocusZoom) FitLocalOrbitExtent(body);
            if (NativeLayer(body) is { } layer) AimAtAvailableTerrain(body, layer);
        }
        return true;
    }

    private double FitDistance(double extent)
    {
        float fov = Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f) * Mathf.Min(1f, camera.aspect);
        return extent * (1.15 + 1.10 / Mathf.Max(.01f, fov));
    }

    private void FitLocalOrbitExtent(WorldCelestialBody body)
    {
        double extent = 0;
        foreach (var satellite in snapshot.Bodies.Values.Where(satellite => satellite.ParentId == body.Id))
            extent = Math.Max(extent, Math.Max((satellite.Position - body.Position).magnitude,
                satellite.Member.Orbit?.ApoapsisRadius ?? 0) + satellite.Radius);
        desiredDistance = Math.Max(desiredDistance, FitDistance(extent));
        autoSystemZoom = Math.Min(.010, DesiredZoom / 1.45);
    }

    private void ReturnToNative(PlanetLayer layer, PlanetTile? tile = null)
    {
        if (returnLayer != null) return;
        double nearest = WorldCameraDriver.MinAltitude + layer.ExtraCameraAltitude;
        double zoom = Math.Min(Math.Clamp(Math.Max(Zoom, DesiredZoom), NativeReturnZoom, .8),
            layer.Radius / Math.Max(layer.Radius, nearest));
        Focus(Target, zoom);
        if (tile is { Valid: true } region && region.Layer == layer)
            AimAtSurfaceDirection(Target, layer.GetTileCenter(region).normalized);
        else
            AimAtAvailableTerrain(Target, layer);
        returnLayer = layer;
        returnTile = tile;
    }

    private void AimAtAvailableTerrain(WorldCelestialBody body, PlanetLayer layer)
    {
        if (layer.ViewAngle >= 180f || layer.ViewCenter.sqrMagnitude < .001f) return;
        var center = layer.ViewCenter.normalized;
        DQuaternion targetFrame = ShowsEntrySubsystem
            ? DQuaternion.Inverse(snapshot.Bodies[entryBody].Motion.Orientation)
            : DQuaternion.identity;
        var orientation = targetFrame * body.Motion.Orientation;
        var direction = (Vector3)DVector3.Rotate((DVector3)center, orientation);
        if (Vector3.Angle(direction, desiredRotation * Vector3.back) > layer.ViewAngle)
            AimAlong(-direction);
    }

    private void AimAtSurfaceDirection(WorldCelestialBody body, Vector3 direction) =>
        AimAlong((Vector3)DVector3.Rotate((DVector3)(-direction), FrameRotation * body.Motion.Orientation));

    private void AimAlong(Vector3 forward)
    {
        var up = Vector3.ProjectOnPlane(rotation * Vector3.up, forward);
        if (up.sqrMagnitude < .001f) up = Vector3.ProjectOnPlane(rotation * Vector3.right, forward);
        desiredRotation = Quaternion.LookRotation(forward, up);
    }

    private void OpenNativeSurface(WorldCelestialBody body, PlanetLayer layer)
    {
        var tile = selectedRegion;
        if (OpenBody(body, NativeReturnZoom)) ReturnToNative(layer, tile);
    }

    internal static bool CanEnterWithWheel(PlanetLayer layer, Camera camera) => NativeZoom(layer, camera) <= LocalEntryZoom;
}
