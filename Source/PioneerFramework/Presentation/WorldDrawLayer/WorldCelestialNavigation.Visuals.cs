using System;
using System.Collections.Generic;
using PioneerFramework.Celestial;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework;

internal sealed partial class WorldCelestialNavigation
{
    private bool stellarShadersChecked;
    private bool stellarFast;
    private Shader? stellarSurfaceShader;
    private Shader? stellarHaloShader;
    private Material? stellarSurface;
    private Material? stellarHalo;
    private Mesh? stellarMesh;
    private PFViewStellarHeatCache? stellarHeatCache;

    private sealed record BakedSurface(PlanetLayer Layer, int Revision, Mesh Mesh, Material Material);
    private readonly Dictionary<int, BakedSurface> bakedSurfaces = new();
    private readonly HashSet<(int Handle, PlanetLayer Layer)> attemptedBakes = new();
    private (CelestialInstanceId Body, PlanetLayer Layer)? pendingBake;
    private float nextBakeTime;

    private bool DrawStellarVisual(Vector3 center, float radius, Quaternion orientation)
    {
        if (!stellarShadersChecked)
        {
            stellarShadersChecked = true;
            MainTabWindow_PFView.SelectStellarShaders(out stellarSurfaceShader, out stellarHaloShader, out stellarFast, out _);
        }
        if (stellarSurfaceShader == null || stellarHaloShader == null) return false;

        stellarMesh ??= MainTabWindow_PFView.BuildSphereMesh(48, 36);
        stellarSurface ??= new Material(stellarSurfaceShader) { renderQueue = PFViewLayers.PlanetAndShips };
        stellarHalo ??= new Material(stellarHaloShader) { renderQueue = PFViewLayers.StellarHalo };
        float extent = MainTabWindow_PFView.ApplyStellarVisual(stellarSurface, stellarHalo, camera.transform.position,
            center, radius, orientation, snapshot.Stamp.PhysicalSeconds, stellarFast,
            PFViewStellarProbe.DefaultHeatCacheEnabled, ref stellarHeatCache);
        var matrix = Matrix4x4.TRS(center, orientation, Vector3.one * (2 * extent));
        Graphics.DrawMesh(stellarMesh, matrix, stellarSurface, WorldCameraManager.WorldLayer);
        Graphics.DrawMesh(stellarMesh, matrix, stellarHalo, WorldCameraManager.WorldLayer);
        return true;
    }

    private bool TryDrawBakedSurface(WorldCelestialBody body, PlanetLayer? layer, Matrix4x4 transform,
        Vector3 center, float radius, Vector4 light)
    {
        if (layer == null || layer.TilesCount == 0) return false;
        Vector3 viewport = camera.WorldToViewportPoint(center);
        if (viewport.z <= camera.nearClipPlane || viewport.z >= camera.farClipPlane ||
            viewport.x is < -.15f or > 1.15f || viewport.y is < -.15f or > 1.15f)
            return false;
        float projectedRadius = radius / viewport.z * UI.screenHeight /
            (2 * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad / 2));
        if (projectedRadius < 5) return false;

        int handle = body.Member.Description.Handle;
        int revision = PFViewSurfaceBake.GetRevision(handle);
        if (bakedSurfaces.TryGetValue(handle, out var baked) && (baked.Revision != revision || baked.Layer != layer))
        {
            DestroyBakedSurface(baked);
            bakedSurfaces.Remove(handle);
            baked = null;
        }
        if (baked == null)
        {
            if (!PFViewSurfaceBake.EnsureReady(handle, layer) ||
                !PFViewSurfaceBake.TryGetPatch(handle, out var texture, out var capCenter, out float capAngle) || texture == null)
            {
                if (projectedRadius >= 9 && viewport.x is >= 0 and <= 1 && viewport.y is >= 0 and <= 1 &&
                    !attemptedBakes.Contains((handle, layer)) && pendingBake == null)
                {
                    pendingBake = (body.Id, layer);
                    nextBakeTime = Time.realtimeSinceStartup + .65f;
                }
                return false;
            }
            var shader = Shader.Find("PFLit");
            if (shader == null) return false;
            var material = new Material(shader) { color = Color.white, mainTexture = texture };
            material.SetVector("_SunColor", Vector4.one);
            baked = new(layer, PFViewSurfaceBake.GetRevision(handle),
                MainTabWindow_PFView.BuildSurfacePatchMesh(capCenter, capAngle), material);
            bakedSurfaces.Add(handle, baked);
        }
        baked.Material.SetVector("_SunDir", light);
        Graphics.DrawMesh(baked.Mesh, transform, baked.Material, WorldCameraManager.WorldLayer);
        return true;
    }

    private void BakeVisibleSurface()
    {
        if (pendingBake is not { } request || Time.realtimeSinceStartup < nextBakeTime ||
            returnLayer != null || surfaceQueued.HasValue || Math.Abs(Math.Log(distance / desiredDistance)) > .05 ||
            aimOffset.magnitude > distance * .03 || Quaternion.Angle(rotation, desiredRotation) > 1f)
            return;

        pendingBake = null;
        if (!snapshot.Bodies.TryGetValue(request.Body, out var body) || visibility.GetValueOrDefault(body.Id) < .99f ||
            NativeLayer(body) != request.Layer || TryTerrainMatrix(request.Layer, out _))
            return;
        var projected = camera.WorldToViewportPoint((Vector3)frame.ToView(body.Position));
        if (projected.z <= camera.nearClipPlane || projected.x is < 0 or > 1 || projected.y is < 0 or > 1) return;
        int handle = body.Member.Description.Handle;
        if (manager.GetBodyById(handle) is not { } info || !attemptedBakes.Add((handle, request.Layer))) return;
        if (!PFViewSurfaceBake.EnsureReady(handle, request.Layer) &&
            !PFViewSurfaceBake.TryBake(info, request.Layer, Find.World.info.Seed, out string? error) &&
            error != null)
            Log.Warning("[PFL World Celestial] " + error);
    }

    private static void DestroyBakedSurface(BakedSurface baked)
    {
        UnityEngine.Object.Destroy(baked.Mesh);
        UnityEngine.Object.Destroy(baked.Material);
    }

    private void DisposeVisuals()
    {
        if (stellarSurface != null) UnityEngine.Object.Destroy(stellarSurface);
        if (stellarHalo != null) UnityEngine.Object.Destroy(stellarHalo);
        if (stellarMesh != null) UnityEngine.Object.Destroy(stellarMesh);
        stellarHeatCache?.Dispose();
        foreach (var baked in bakedSurfaces.Values) DestroyBakedSurface(baked);
        if (bakedSurfaces.Count > 0) PFViewSurfaceBake.UnloadReloadable();
    }
}
