using System;
using System.Collections.Generic;
using PioneerFramework.Celestial;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework;

internal sealed partial class WorldCelestialNavigation
{
    private sealed record SurfaceMaterials(Material Opaque, Material? Fade);
    private readonly Dictionary<int, SurfaceMaterials> surfaceMaterials = new();
    private readonly Dictionary<int, (PlanetLayer Layer, int Revision, bool Built, float RetryAt)> previewAttempts = new();
    private Mesh? surfacePreviewSphere;
    private (CelestialInstanceId Body, PlanetLayer Layer)? pendingPreview;
    private float preparePreviewAt;
    private bool previewShadersChecked;
    private Shader? previewShader;
    private Shader? previewFadeShader;

    private bool DrawSurfacePreview(Mesh sphere, WorldCelestialBody body, PlanetLayer layer, Vector3 center, float radius,
        Quaternion orientation, Vector3 sun, bool nativeTerrain)
    {
        if (!previewShadersChecked)
        {
            previewShadersChecked = true;
            previewShader = Shader.Find("PF_WorldSurfacePreview");
            previewFadeShader = Shader.Find("PF_WorldSurfacePreviewFade");
        }
        if (previewShader == null) return false;

        int handle = body.Member.Description.Handle;
        if (!surfaceMaterials.TryGetValue(handle, out var materials))
        {
            materials = new(new Material(previewShader), previewFadeShader != null ? new Material(previewFadeShader) : null);
            surfaceMaterials.Add(handle, materials);
        }

        PFViewSurfaceBake.TryGetWorldPreview(handle, layer, out var patch);
        if (patch == null)
            RequestSurfacePreview(body, layer, center, radius);
        else if (!previewAttempts.TryGetValue(handle, out var attempt) || !attempt.Built || !ReferenceEquals(attempt.Layer, layer))
            previewAttempts[handle] = (layer, PFViewSurfaceBake.GetRevision(handle), true, 0f);

        float ready = patch != null ? Mathf.Clamp01((Time.realtimeSinceStartup - patch.BornAt) / .25f) : 0f;
        Color background = layer.Def.worldDrawLayers.Contains(typeof(WorldDrawLayer_UngeneratedPlanetParts))
            ? PFViewSurfaceBake.WorldOceanColor : body.Member.Color;
        var transform = Matrix4x4.TRS(center, orientation, Vector3.one * (2 * radius));

        ApplySurfacePreview(materials.Opaque, nativeTerrain ? null : patch, background, sun, 1f, ready);
        Graphics.DrawMesh(sphere, nativeTerrain ? transform * Matrix4x4.Scale(Vector3.one * .995f) : transform,
            materials.Opaque, WorldCameraManager.WorldLayer);

        float opacity = SurfacePreviewOpacity(nativeSurface?.Zoom ?? 0, nativeTerrain);
        // A cold cache should reveal the real terrain sooner than an already useful preview.
        opacity = Mathf.Lerp(opacity * opacity, opacity, ready);
        if (nativeTerrain && opacity > 0f && materials.Fade != null)
        {
            surfacePreviewSphere ??= MainTabWindow_PFView.BuildSphereMesh(64, 48);
            ApplySurfacePreview(materials.Fade, patch, background, sun, opacity, ready);
            // Clear the sphere mesh's chord error while keeping native rivers, roads and hills under the fade.
            Graphics.DrawMesh(surfacePreviewSphere, transform * Matrix4x4.Scale(Vector3.one * 1.003f),
                materials.Fade, WorldCameraManager.WorldLayer);
        }
        return true;
    }

    private static void ApplySurfacePreview(Material material, PFViewSurfaceBake.WorldPreview? patch,
        Color background, Vector3 sun, float opacity, float ready)
    {
        material.color = new Color(background.r, background.g, background.b, opacity);
        material.SetVector("_SunDir", new Vector4(sun.x, sun.y, sun.z, 0));
        material.mainTexture = patch?.Texture ?? BaseContent.WhiteTex;
        if (patch == null)
        {
            material.SetVector("_Patch", Vector4.zero);
            return;
        }

        material.SetVector("_Patch", new Vector4(patch.Center.x, patch.Center.y, patch.Center.z, patch.Angle));
        material.SetVector("_PatchRight", new Vector4(patch.TangentX.x, patch.TangentX.y, patch.TangentX.z, ready));
        material.SetVector("_PatchUp", new Vector4(patch.TangentY.x, patch.TangentY.y, patch.TangentY.z, 0));
    }

    private void RequestSurfacePreview(WorldCelestialBody body, PlanetLayer layer, Vector3 center, float radius)
    {
        bool focused = !system && body.Id == target;
        if (pendingPreview is { } pending && (pending.Body == body.Id || !focused))
            return;

        int handle = body.Member.Description.Handle;
        if (PFViewSurfaceBake.IsWorldPreviewPending(handle, layer)) return;
        float now = Time.realtimeSinceStartup;
        if (previewAttempts.TryGetValue(handle, out var attempt) && ReferenceEquals(attempt.Layer, layer) &&
            attempt.Revision == PFViewSurfaceBake.GetRevision(handle) &&
            (attempt.Built ? !focused : now < attempt.RetryAt))
            return;

        var viewport = camera.WorldToViewportPoint(center);
        if (viewport.z <= camera.nearClipPlane || viewport.z >= camera.farClipPlane ||
            viewport.x is < 0 or > 1 || viewport.y is < 0 or > 1)
            return;
        float projectedRadius = radius / viewport.z * UI.screenHeight /
            (2 * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad / 2));
        if (projectedRadius < 5f && !focused) return;
        pendingPreview = (body.Id, layer);
        preparePreviewAt = now + (focused ? 0f : .20f);
    }

    private void PrepareVisibleSurfacePreview()
    {
        if (pendingPreview is not { } request || Time.realtimeSinceStartup < preparePreviewAt || surfaceQueued.HasValue)
            return;
        bool focused = !system && request.Body == target;
        if (!focused && (Math.Abs(Math.Log(distance / desiredDistance)) > .08 || Quaternion.Angle(rotation, desiredRotation) > 2f))
            return;

        pendingPreview = null;
        if (!snapshot.Bodies.TryGetValue(request.Body, out var body) || NativeLayer(body) != request.Layer ||
            (!focused && visibility.GetValueOrDefault(body.Id) < .9f))
            return;
        var viewport = camera.WorldToViewportPoint((Vector3)frame.ToView(body.Position));
        if (viewport.z <= camera.nearClipPlane || viewport.z >= camera.farClipPlane ||
            viewport.x is < 0 or > 1 || viewport.y is < 0 or > 1)
            return;

        int handle = body.Member.Description.Handle;
        PFViewSurfaceBake.PrepareWorldPreview(handle, request.Layer);
        previewAttempts[handle] = (request.Layer, PFViewSurfaceBake.GetRevision(handle), false,
            Time.realtimeSinceStartup + 5f);
    }

    private void DisposeSurfacePreviewMaterials()
    {
        foreach (var materials in surfaceMaterials.Values)
        {
            UnityEngine.Object.Destroy(materials.Opaque);
            if (materials.Fade != null) UnityEngine.Object.Destroy(materials.Fade);
        }
        if (surfacePreviewSphere != null) UnityEngine.Object.Destroy(surfacePreviewSphere);
    }
}
