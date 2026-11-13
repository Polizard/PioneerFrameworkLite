using System.Collections.Generic;
using PioneerFramework.PFView.ShipBuildingVisuals;
using UnityEngine;
using Verse;

namespace PioneerFramework;

public partial class MainTabWindow_PFView
{
    private ShipBuildingVisualRenderer? buildingVisuals;
    private Game? buildingVisualGame;
    private readonly List<MapVisualContext> sceneMapContexts = new();
    private readonly List<MapVisualContext> cameraMapContexts = new();
    private readonly Dictionary<int, VisualSurfaceBox[]> drawnReliefSurfaces = new();
    private readonly HashSet<int> reliefDrawnPartIds = new();
    private readonly HashSet<int> cameraReliefPartIds = new();
    private readonly VisualRenderClock buildingVisualClock = new();
    private bool reliefWholeEntity;

    private void EnsureBuildingVisualSession()
    {
        if (!ReferenceEquals(buildingVisualGame, Current.Game))
        {
            buildingVisuals?.Dispose();
            buildingVisuals = null;
            buildingVisualClock.Reset();
            DestroyAllReliefMeshes();
            buildingVisualGame = Current.Game;
        }
        if (buildingVisuals == null && Current.Game != null)
            buildingVisuals = new ShipBuildingVisualRenderer(message => Log.Warning(message));
        buildingVisuals?.BeginFrame(Waylith.UnityCompat.UnityRuntime.Current?.FrameCount ?? 0);
    }

    private static void CollectVisualMaps(SpaceEntity entity, List<MapVisualContext> destination)
    {
        destination.Clear();
        var ids = entity.GetPartLinkedMapIdsCached();
        if (ids.Count == 0 && entity.MapId >= 0)
        {
            var primary = SpaceMapGenerator.FindPocketMap(entity.MapId);
            if (primary != null) destination.Add(new(primary, entity.GetPartForMap(entity.MapId)));
            return;
        }
        for (int i = 0; i < ids.Count; i++)
        {
            var map = SpaceMapGenerator.FindPocketMap(ids[i]);
            var part = entity.GetPartForMap(ids[i]);
            if (map != null && part?.isDestroyed != true) destination.Add(new(map, part));
        }
    }

    private float MeasureBuildingVisualRadius(SpaceEntity entity)
    {
        if (!ShipBuildingVisualRenderer.Enabled || buildingVisuals == null) return 0;
        CollectVisualMaps(entity, cameraMapContexts);
        float radius = 0;
        foreach (var context in cameraMapContexts) radius = Mathf.Max(radius, buildingVisuals.MeasureRadius(context));
        cameraMapContexts.Clear();
        return radius;
    }

    private void DrawBuildingVisuals(SpaceEntity entity, SpaceEntityGroup group, Vector3 viewPos, Quaternion viewRotation, float scale, bool rocketHull)
    {
        if (!ShipBuildingVisualRenderer.Enabled || buildingVisuals == null || renderCamera == null) return;
        Material material = ToonMat ?? renderMat;
        if (material == null) return;
        float focalPixels = (renderTexture?.height ?? 1080)
            / (2 * Mathf.Tan(renderCamera.fieldOfView * .5f * Mathf.Deg2Rad));
        Vector3 cameraPosition = GetCameraViewPos();
        Vector3 cameraForward = renderCamera.transform.forward;
        matBlock.SetFloat("_OutlineWidth", .025f);
        int frame = Waylith.UnityCompat.UnityRuntime.Current?.FrameCount ?? 0;
        double tick = buildingVisualClock.Sample(RenderTick(), Find.TickManager?.Paused == true);
        foreach (var context in sceneMapContexts)
        {
            drawnReliefSurfaces.TryGetValue(context.Map.uniqueID, out var surfaces);
            buildingVisuals.DrawMap(context, entity, group, surfaces, rocketHull && surfaces == null, viewPos, viewRotation,
                MetersToView, scale, cameraPosition, cameraForward, focalPixels, material, matBlock, SceneLayer, frame, tick);
        }
    }

    private void DestroyBuildingVisuals()
    {
        buildingVisuals?.Dispose();
        buildingVisuals = null; buildingVisualGame = null;
        sceneMapContexts?.Clear();
        cameraMapContexts?.Clear();
        drawnReliefSurfaces?.Clear();
        reliefDrawnPartIds?.Clear();
        cameraReliefPartIds?.Clear();
        buildingVisualClock?.Reset();
    }
}
