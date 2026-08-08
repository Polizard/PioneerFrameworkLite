using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 创建运行时 PlanetLayer 所需的稳定描述。
    /// 需要跨存档使用时，调用方必须在读取存档前用同一 DefName 再次注册该描述。
    /// </summary>
    public sealed class RuntimePlanetLayerSpec
    {
        public string DefName { get; set; }

        public string Label { get; set; }

        public string Description { get; set; }

        public string BackgroundBiomeDefName { get; set; }

        public float Radius { get; set; }

        public int Subdivisions { get; set; }

        public bool UseSurfaceViewAngle { get; set; }

        public Vector3 Origin { get; set; }

        public float ViewAngle { get; set; }

        public float ExtraCameraAltitude { get; set; }

        public float BackgroundWorldCameraOffset { get; set; }

        public float BackgroundWorldCameraParallaxDistancePer100Cells { get; set; }

        public Type LayerType { get; set; }

        public Type TileType { get; set; }

        public string ElevationString { get; set; }

        public string GerundLabel { get; set; }

        public string ViewGizmoTooltip { get; set; }

        public string ViewGizmoTexPath { get; set; }

        public ModContentPack ModContentPack { get; set; }

        public List<string> WorldGenStepDefNames { get; set; } = new List<string>();

        public List<Type> WorldDrawLayerTypes { get; set; } = new List<Type>();

        public List<Type> WorldTabTypes { get; set; } = new List<Type>();

        /// <summary>
        /// 创建一个只运行 Terrain 步骤的标准球形地表描述。
        /// </summary>
        public static RuntimePlanetLayerSpec CreateSurface(
            string defName,
            string label,
            float radius,
            int subdivisions,
            string backgroundBiomeDefName)
        {
            return new RuntimePlanetLayerSpec
            {
                DefName = defName,
                Label = label,
                BackgroundBiomeDefName = backgroundBiomeDefName,
                Radius = radius,
                Subdivisions = subdivisions,
                UseSurfaceViewAngle = true,
                ViewAngle = 180f,
                LayerType = typeof(SurfaceLayer),
                TileType = typeof(SurfaceTile),
                ElevationString = "{0}m",
                GerundLabel = label,
                WorldGenStepDefNames = new List<string>
                {
                    "Terrain"
                },
                WorldDrawLayerTypes = new List<Type>
                {
                    typeof(WorldDrawLayer_CurrentMapTile),
                    typeof(WorldDrawLayer_SelectedTile),
                    typeof(WorldDrawLayer_MouseTile),
                    typeof(WorldDrawLayer_UngeneratedPlanetParts),
                    typeof(WorldDrawLayer_WorldObjects_Expandable),
                    typeof(WorldDrawLayer_WorldObjects_NonExpandable),
                    typeof(WorldDrawLayer_Glow),
                    typeof(WorldDrawLayer_Terrain),
                    typeof(WorldDrawLayer_Clouds)
                },
                WorldTabTypes = new List<Type>
                {
                    typeof(WITab_Terrain),
                    typeof(WITab_Planet)
                }
            };
        }

        internal PlanetLayerSettings CreateSettings()
        {
            return new PlanetLayerSettings
            {
                origin = Origin,
                radius = Radius,
                viewAngle = ViewAngle,
                extraCameraAltitude = ExtraCameraAltitude,
                subdivisions = Subdivisions,
                useSurfaceViewAngle = UseSurfaceViewAngle,
                backgroundWorldCameraOffset = BackgroundWorldCameraOffset,
                backgroundWorldCameraParallaxDistancePer100Cells =
                    BackgroundWorldCameraParallaxDistancePer100Cells
            };
        }
    }

    /// <summary>
    /// 动态注册、创建和移除可选 PlanetLayer 的最小服务。
    /// 本服务不会删除 PlanetLayerDef，也不会替调用方迁移地图、Pawn、任务或第三方引用。
    /// </summary>
    public static class RuntimePlanetLayerService
    {
        private static readonly FieldInfo ZoomInToLayerField =
            AccessTools.Field(typeof(PlanetLayer), "zoomInToLayer");

        private static readonly FieldInfo ZoomOutToLayerField =
            AccessTools.Field(typeof(PlanetLayer), "zoomOutToLayer");

        private static readonly FieldInfo WorldGenStepsField =
            AccessTools.Field(typeof(PlanetLayerDef), "worldGenSteps");

        private static readonly FieldInfo WorldTabsField =
            AccessTools.Field(typeof(PlanetLayerDef), "worldTabs");

        public static bool ValidateSpec(RuntimePlanetLayerSpec spec, out string error)
        {
            if (spec == null)
            {
                error = "PlanetLayer spec is null.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(spec.DefName))
            {
                error = "PlanetLayer DefName is empty.";
                return false;
            }

            if (spec.Radius <= 0f)
            {
                error = "PlanetLayer radius must be greater than zero.";
                return false;
            }

            if (spec.Subdivisions < 0)
            {
                error = "PlanetLayer subdivisions cannot be negative.";
                return false;
            }

            if (spec.LayerType == null ||
                !typeof(PlanetLayer).IsAssignableFrom(spec.LayerType))
            {
                error = "LayerType must derive from PlanetLayer.";
                return false;
            }

            if (spec.TileType == null || !typeof(Tile).IsAssignableFrom(spec.TileType))
            {
                error = "TileType must derive from Tile.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(spec.BackgroundBiomeDefName))
            {
                error = "BackgroundBiomeDefName is empty.";
                return false;
            }

            if (spec.WorldGenStepDefNames == null ||
                spec.WorldGenStepDefNames.Count == 0 ||
                spec.WorldGenStepDefNames.Any(string.IsNullOrWhiteSpace))
            {
                error = "At least one valid world generation step is required.";
                return false;
            }

            if (spec.WorldDrawLayerTypes == null ||
                spec.WorldDrawLayerTypes.Any(type =>
                    type == null || !typeof(WorldDrawLayer).IsAssignableFrom(type)))
            {
                error = "Every world draw layer type must derive from WorldDrawLayer.";
                return false;
            }

            if (spec.WorldTabTypes == null ||
                spec.WorldTabTypes.Any(type =>
                    type == null || !typeof(WITab).IsAssignableFrom(type)))
            {
                error = "Every world tab type must derive from WITab.";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// 将描述转换为 implied PlanetLayerDef。
        /// 存档会按 DefName 恢复引用，因此相同存档必须始终使用相同描述。
        /// </summary>
        public static PlanetLayerDef EnsureDefinition(
            RuntimePlanetLayerSpec spec,
            bool hotReload = false)
        {
            if (!ValidateSpec(spec, out string error))
            {
                throw new ArgumentException(error, nameof(spec));
            }

            PlanetLayerDef existing =
                DefDatabase<PlanetLayerDef>.GetNamedSilentFail(spec.DefName);
            if (existing != null)
            {
                EnsureCompatible(existing, spec);
                return existing;
            }

            BiomeDef backgroundBiome =
                DefDatabase<BiomeDef>.GetNamedSilentFail(spec.BackgroundBiomeDefName);
            if (backgroundBiome == null)
            {
                throw new InvalidOperationException(
                    $"BiomeDef '{spec.BackgroundBiomeDefName}' was not found.");
            }

            var worldGenSteps = new List<WorldGenStepDef>();
            foreach (string stepDefName in spec.WorldGenStepDefNames)
            {
                WorldGenStepDef step =
                    DefDatabase<WorldGenStepDef>.GetNamedSilentFail(stepDefName);
                if (step == null)
                {
                    throw new InvalidOperationException(
                        $"WorldGenStepDef '{stepDefName}' was not found.");
                }

                worldGenSteps.Add(step);
            }

            var layerDef = new PlanetLayerDef
            {
                defName = spec.DefName,
                label = string.IsNullOrWhiteSpace(spec.Label)
                    ? spec.DefName
                    : spec.Label,
                description = spec.Description,
                modContentPack = spec.ModContentPack,
                backgroundBiome = backgroundBiome,
                layerType = spec.LayerType,
                tileType = spec.TileType,
                elevationString = spec.ElevationString,
                gerundLabel = spec.GerundLabel,
                viewGizmoTooltip = spec.ViewGizmoTooltip,
                viewGizmoTexPath = spec.ViewGizmoTexPath,
                worldDrawLayers = new List<Type>(spec.WorldDrawLayerTypes)
            };

            SetPrivateDefinitionLists(
                layerDef,
                worldGenSteps,
                new List<Type>(spec.WorldTabTypes));
            DefGenerator.AddImpliedDef(layerDef, hotReload);
            return layerDef;
        }

        public static PlanetLayer FindByDefName(string defName)
        {
            if (string.IsNullOrWhiteSpace(defName) || Find.WorldGrid == null)
            {
                return null;
            }

            foreach (KeyValuePair<int, PlanetLayer> pair in
                     Find.WorldGrid.PlanetLayers)
            {
                PlanetLayer layer = pair.Value;
                if (layer?.Def?.defName == defName)
                {
                    return layer;
                }
            }

            return null;
        }

        /// <summary>
        /// 创建并生成一个 PlanetLayer。已有同名 Layer 时直接返回已有实例。
        /// 注册后生成失败会调用原版 RemovePlanetLayer 回滚。
        /// </summary>
        public static PlanetLayer Create(
            RuntimePlanetLayerSpec spec,
            int? seedPart = null)
        {
            if (Find.WorldGrid == null)
            {
                throw new InvalidOperationException("WorldGrid is not available.");
            }

            PlanetLayer existing = FindByDefName(spec?.DefName);
            if (existing != null)
            {
                return existing;
            }

            PlanetLayerDef layerDef = EnsureDefinition(spec);
            PlanetLayer layer =
                Find.WorldGrid.RegisterPlanetLayer(layerDef, spec.CreateSettings());

            try
            {
                RunWorldGenerationPreservingRandState(layer, seedPart);

                if (layer.TilesCount == 0)
                {
                    throw new InvalidOperationException(
                        $"PlanetLayer '{spec.DefName}' generated no tiles.");
                }

                return layer;
            }
            catch
            {
                if (Find.WorldGrid.PlanetLayers.TryGetValue(
                        layer.LayerID,
                        out PlanetLayer registeredLayer) &&
                    ReferenceEquals(registeredLayer, layer))
                {
                    Find.WorldGrid.RemovePlanetLayer(layer);
                }

                throw;
            }
        }

        private static void RunWorldGenerationPreservingRandState(
            PlanetLayer layer,
            int? seedPart)
        {
            Rand.PushState();
            try
            {
                if (seedPart.HasValue)
                {
                    layer.RunWorldGeneration(seedPart.Value);
                }
                else
                {
                    layer.RunWorldGeneration();
                }
            }
            finally
            {
                Rand.PopState();
            }
        }

        public static bool CanRemove(
            PlanetLayer layer,
            bool allowContentDestruction,
            out string reason)
        {
            if (layer == null)
            {
                reason = "PlanetLayer is null.";
                return false;
            }

            WorldGrid worldGrid = Find.WorldGrid;
            if (worldGrid == null)
            {
                reason = "WorldGrid is not available.";
                return false;
            }

            if (Scribe.mode != LoadSaveMode.Inactive)
            {
                reason = "PlanetLayer cannot be removed while Scribe is active.";
                return false;
            }

            if (!worldGrid.PlanetLayers.TryGetValue(
                    layer.LayerID,
                    out PlanetLayer registeredLayer) ||
                !ReferenceEquals(registeredLayer, layer))
            {
                reason = "PlanetLayer is not registered in the current WorldGrid.";
                return false;
            }

            if (layer.IsRootSurface ||
                ReferenceEquals(worldGrid.Surface, layer) ||
                ReferenceEquals(worldGrid.Orbit, layer))
            {
                reason = "Root surface and orbit layers cannot be removed.";
                return false;
            }

            List<WorldObject> worldObjects =
                Find.WorldObjects?.AllWorldObjectsOnLayer(layer);
            if (!allowContentDestruction &&
                worldObjects != null &&
                worldObjects.Count > 0)
            {
                reason =
                    $"PlanetLayer contains {worldObjects.Count} world object(s).";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// 移除一个可选 PlanetLayer。
        /// allowContentDestruction=false 时，Layer 上存在任何 WorldObject 都会拒绝删除。
        /// </summary>
        public static bool TryRemove(
            PlanetLayer layer,
            bool allowContentDestruction,
            out string reason)
        {
            if (!CanRemove(layer, allowContentDestruction, out reason))
            {
                return false;
            }

            WorldGrid worldGrid = Find.WorldGrid;

            try
            {
                if (Find.WorldSelector != null &&
                    ReferenceEquals(PlanetLayer.Selected, layer))
                {
                    PlanetLayer fallback = worldGrid.Surface;
                    if (fallback == null || ReferenceEquals(fallback, layer))
                    {
                        fallback = worldGrid.PlanetLayers.Values.FirstOrDefault(
                            candidate => !ReferenceEquals(candidate, layer));
                    }

                    if (fallback == null)
                    {
                        reason = "No fallback PlanetLayer is available.";
                        return false;
                    }

                    PlanetLayer.Selected = fallback;
                }

                if (allowContentDestruction)
                {
                    DestroyLayerWorldObjects(layer);
                }

                ClearZoomReferences(worldGrid, layer);
                worldGrid.RemovePlanetLayer(layer);

                if (worldGrid.PlanetLayers.ContainsKey(layer.LayerID))
                {
                    reason = "WorldGrid did not remove the PlanetLayer.";
                    return false;
                }

                reason = null;
                return true;
            }
            catch (Exception exception)
            {
                reason =
                    $"Failed to remove PlanetLayer '{layer.Def?.defName}': " +
                    exception.Message;
                return false;
            }
        }

        private static void DestroyLayerWorldObjects(PlanetLayer layer)
        {
            List<WorldObject> worldObjects =
                Find.WorldObjects?.AllWorldObjectsOnLayer(layer);
            if (worldObjects == null || worldObjects.Count == 0)
            {
                return;
            }

            foreach (WorldObject worldObject in worldObjects.ToList())
            {
                if (worldObject != null && !worldObject.Destroyed)
                {
                    worldObject.Destroy();
                }
            }

            List<WorldObject> remainingWorldObjects =
                Find.WorldObjects?.AllWorldObjectsOnLayer(layer);
            if (remainingWorldObjects != null &&
                remainingWorldObjects.Count > 0)
            {
                throw new InvalidOperationException(
                    $"PlanetLayer still contains " +
                    $"{remainingWorldObjects.Count} world object(s).");
            }
        }

        private static void EnsureCompatible(
            PlanetLayerDef existing,
            RuntimePlanetLayerSpec spec)
        {
            if (existing.layerType != spec.LayerType ||
                existing.tileType != spec.TileType)
            {
                throw new InvalidOperationException(
                    $"PlanetLayerDef '{spec.DefName}' already exists with " +
                    "incompatible layer or tile types.");
            }
        }

        private static void SetPrivateDefinitionLists(
            PlanetLayerDef layerDef,
            List<WorldGenStepDef> worldGenSteps,
            List<Type> worldTabs)
        {
            if (WorldGenStepsField == null || WorldTabsField == null)
            {
                throw new MissingFieldException(
                    "RimWorld PlanetLayerDef generation fields were not found.");
            }

            WorldGenStepsField.SetValue(layerDef, worldGenSteps);
            WorldTabsField.SetValue(layerDef, worldTabs);
        }

        private static void ClearZoomReferences(
            WorldGrid worldGrid,
            PlanetLayer removedLayer)
        {
            if (ZoomInToLayerField == null && ZoomOutToLayerField == null)
            {
                return;
            }

            foreach (PlanetLayer layer in worldGrid.PlanetLayers.Values)
            {
                if (ReferenceEquals(layer, removedLayer))
                {
                    continue;
                }

                if (ZoomInToLayerField != null &&
                    ReferenceEquals(
                        ZoomInToLayerField.GetValue(layer),
                        removedLayer))
                {
                    ZoomInToLayerField.SetValue(layer, null);
                }

                if (ZoomOutToLayerField != null &&
                    ReferenceEquals(
                        ZoomOutToLayerField.GetValue(layer),
                        removedLayer))
                {
                    ZoomOutToLayerField.SetValue(layer, null);
                }
            }
        }
    }
}
