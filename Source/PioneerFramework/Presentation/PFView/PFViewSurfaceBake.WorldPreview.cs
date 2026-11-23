using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework;

internal static partial class PFViewSurfaceBake
{
    private const int WorldPreviewSize = 512;
    private const int WorldPreviewCacheSize = 8;
    private const int WorldPreviewCenterSlice = 4096;
    private const int WorldPreviewTileSlice = 256;
    private const int WorldPreviewBakeLayer = 29;

    // The daytime deep-ocean colour used by native WorldOcean and UngeneratedPlanetParts.
    internal static readonly Color WorldOceanColor = new(0.173f, 0.204f, 0.259f, 1f);

    internal sealed record WorldPreview(PlanetLayer Layer, Texture2D Texture, Vector3 Center,
        Vector3 TangentX, Vector3 TangentY, float Angle, float BornAt)
    {
        internal long LastUse = Stopwatch.GetTimestamp();
    }

    private static readonly Dictionary<int, WorldPreview> worldPreviews = new();
    private static readonly Dictionary<int, PlanetLayer> pendingWorldPreviews = new();
    private static WorldPreviewWork? worldPreviewWork;

    private sealed record WorldPreviewCap(Vector3 Center, Vector3 TangentX, Vector3 TangentY, float Angle);

    private sealed class WorldPreviewWork(int handle, PlanetLayer layer)
    {
        internal readonly int Handle = handle;
        internal readonly PlanetLayer Layer = layer;
        internal readonly Vector3[] Centers = new Vector3[layer.TilesCount];
        internal int NextCenter;
        internal int NextTile;
        internal Task<WorldPreviewCap?>? Coverage;
        internal RenderTexture? Target;
        internal GameObject? CameraObject;
        internal Camera? Camera;
    }

    internal static bool TryGetWorldPreview(int handle, PlanetLayer layer, out WorldPreview? preview)
    {
        EnsureContext();
        if (worldPreviews.TryGetValue(handle, out preview) && ReferenceEquals(preview.Layer, layer))
        {
            preview.LastUse = Stopwatch.GetTimestamp();
            return true;
        }
        preview = null;
        return false;
    }

    internal static void PrepareWorldPreview(int handle, PlanetLayer layer)
    {
        if (TryGetWorldPreview(handle, layer, out _) || layer.TilesCount == 0 || !MatchesCelestialOwner(handle, layer)) return;

        if (pendingWorldPreviews.TryGetValue(handle, out var previous) && !ReferenceEquals(previous, layer))
            CancelWorldPreviewWork(handle);
        pendingWorldPreviews[handle] = layer;
    }

    internal static bool IsWorldPreviewPending(int handle, PlanetLayer layer) =>
        pendingWorldPreviews.TryGetValue(handle, out var pending) && ReferenceEquals(pending, layer);

    private static void PumpWorldPreview()
    {
        if (worldPreviewWork == null)
        {
            if (pendingWorldPreviews.Count == 0) return;
            var request = pendingWorldPreviews.First();
            worldPreviewWork = new(request.Key, request.Value);
        }
        var work = worldPreviewWork;
        try
        {
            if (work.Layer.TilesCount == work.Centers.Length && MatchesCelestialOwner(work.Handle, work.Layer) &&
                !AdvanceWorldPreview(work)) return;
        }
        catch (Exception error)
        {
            Log.Warning("[PFL World Surface Preview] " + error.GetBaseException().Message);
        }
        CancelWorldPreviewWork(work.Handle);
    }

    private static bool AdvanceWorldPreview(WorldPreviewWork work)
    {
        if (work.Coverage == null)
        {
            int end = Math.Min(work.NextCenter + WorldPreviewCenterSlice, work.Centers.Length);
            for (int i = work.NextCenter; i < end; i++) work.Centers[i] = work.Layer.GetTileCenter(i);
            work.NextCenter = end;
            if (end == work.Centers.Length)
            {
                var centers = work.Centers;
                work.Coverage = Task.Run(() => GetWorldPreviewCap(centers));
            }
            return false;
        }
        if (!work.Coverage.IsCompleted) return false;
        var cap = work.Coverage.GetAwaiter().GetResult();
        if (cap == null) return true;

        if (work.Camera == null)
        {
            BeginWorldPreviewRender(work);
            return false;
        }
        if (work.NextTile < work.Centers.Length)
        {
            RenderWorldPreviewTiles(work, cap);
            return false;
        }
        PublishWorldPreview(work, cap);
        return true;
    }

    private static WorldPreviewCap? GetWorldPreviewCap(Vector3[] centers)
    {
        if (!ComputeCap(centers, out var center, out float halfAngle)) return null;
        float angle = (Mathf.Min(halfAngle, MaxHalfAngleDeg) + PatchMarginDeg) * Mathf.Deg2Rad;
        TileTangentFrame(center, out var tangentX, out var tangentY);
        return new(center, tangentX, tangentY, angle);
    }

    private static void BeginWorldPreviewRender(WorldPreviewWork work)
    {
        work.Target = RenderTexture.GetTemporary(WorldPreviewSize, WorldPreviewSize, 0,
            RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var cameraObject = work.CameraObject = new GameObject("PF World Surface Preview Camera");
        var camera = work.Camera = cameraObject.AddComponent<Camera>();
        camera.enabled = false;
        camera.orthographic = true;
        camera.orthographicSize = 1f;
        camera.transform.position = new Vector3(0, 0, -10);
        camera.transform.rotation = Quaternion.identity;
        camera.nearClipPlane = .1f;
        camera.farClipPlane = 100f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.clear;
        camera.cullingMask = 1 << WorldPreviewBakeLayer;
        camera.targetTexture = work.Target;
    }

    private static void RenderWorldPreviewTiles(WorldPreviewWork work, WorldPreviewCap cap)
    {
        int end = Math.Min(work.NextTile + WorldPreviewTileSlice, work.Centers.Length);
        var mesh = BuildBakeMesh(work.Layer, work.Centers, cap.Center, cap.TangentX, cap.TangentY, cap.Angle,
            out var materials, worldPreview: true, firstTile: work.NextTile, endTile: end);
        try
        {
            var camera = work.Camera!;
            for (int i = 0; i < materials.Length; i++)
                Graphics.DrawMesh(mesh, Matrix4x4.identity, materials[i], WorldPreviewBakeLayer, camera, i);
            camera.Render();
            // The engine loads the previous colour attachment on subsequent tile batches.
            camera.clearFlags = CameraClearFlags.Nothing;
            work.NextTile = end;
        }
        finally { UnityEngine.Object.Destroy(mesh); }
    }

    private static void ClearWorldPreviewWork()
    {
        pendingWorldPreviews.Clear();
        if (worldPreviewWork is { } work) CancelWorldPreviewWork(work.Handle);
    }

    private static void CancelWorldPreviewWork(int handle)
    {
        pendingWorldPreviews.Remove(handle);
        if (worldPreviewWork is not { } work || work.Handle != handle) return;
        worldPreviewWork = null;
        if (work.Coverage is { } coverage)
            _ = coverage.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        if (work.Camera != null) work.Camera.targetTexture = null;
        if (work.CameraObject != null) UnityEngine.Object.Destroy(work.CameraObject);
        if (work.Target != null) RenderTexture.ReleaseTemporary(work.Target);
    }

    private static void PublishWorldPreview(WorldPreviewWork work, WorldPreviewCap cap)
    {
        int handle = work.Handle;

        var previousTarget = RenderTexture.active;
        Texture2D? texture = null;
        try
        {
            RenderTexture.active = work.Target;
            texture = new Texture2D(WorldPreviewSize, WorldPreviewSize, TextureFormat.RGBA32, true)
            {
                name = "PF World Surface Preview",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear
            };
            texture.ReadPixels(new Rect(0, 0, WorldPreviewSize, WorldPreviewSize), 0, 0, false);
            if (!texture.GetPixels32().Any(pixel => pixel.a > 0))
            {
                Log.WarningOnce("[PFL World Surface Preview] Empty terrain readback; will retry.", 0x5F501);
                return;
            }
            texture.Apply(true, false);

            if (worldPreviews.Remove(handle, out var previous)) UnityEngine.Object.Destroy(previous.Texture);
            if (worldPreviews.Count >= WorldPreviewCacheSize)
            {
                int oldest = worldPreviews.MinBy(entry => entry.Value.LastUse).Key;
                UnityEngine.Object.Destroy(worldPreviews[oldest].Texture);
                worldPreviews.Remove(oldest);
            }
            worldPreviews.Add(handle, new(work.Layer, texture, cap.Center, cap.TangentX, cap.TangentY,
                cap.Angle, Time.realtimeSinceStartup));
            texture = null;
        }
        finally
        {
            RenderTexture.active = previousTarget;
            if (texture != null) UnityEngine.Object.Destroy(texture);
        }
    }
}
