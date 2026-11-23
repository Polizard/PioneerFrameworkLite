using RimWorld.Planet;
using UnityEngine;

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

    private void DisposeVisuals()
    {
        if (stellarSurface != null) UnityEngine.Object.Destroy(stellarSurface);
        if (stellarHalo != null) UnityEngine.Object.Destroy(stellarHalo);
        if (stellarMesh != null) UnityEngine.Object.Destroy(stellarMesh);
        stellarHeatCache?.Dispose();
        DisposeSurfacePreviewMaterials();
    }
}
