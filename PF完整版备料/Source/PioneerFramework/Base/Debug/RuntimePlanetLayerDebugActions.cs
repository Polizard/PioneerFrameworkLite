using System;
using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 在主菜单读取存档前注册固定测试 Def，保证包含测试 Layer 的存档能够恢复 Def 引用。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class RuntimePlanetLayerDebugDefinitionBootstrap
    {
        static RuntimePlanetLayerDebugDefinitionBootstrap()
        {
            try
            {
                RuntimePlanetLayerService.EnsureDefinition(
                    RuntimePlanetLayerDebugActions.BuildTestSpec());
            }
            catch (Exception exception)
            {
                Log.Error(
                    "[PioneerFramework] Failed to register the runtime " +
                    $"PlanetLayer debug Def: {exception}");
            }
        }
    }

    /// <summary>
    /// 开发者模式中的固定 PlanetLayer 测试入口。
    /// 生命周期烟雾测试结束后会保留一个重新创建的测试 Layer，供保存和重载测试使用。
    /// </summary>
    public static class RuntimePlanetLayerDebugActions
    {
        public const string TestLayerDefName = "PF_Debug_RuntimeSurface";

        private const string DebugCategory = "Pioneer Framework";

        private const int TestSeedPart = 0x50464C;

        [DebugAction(
            DebugCategory,
            "PlanetLayer: 创建固定测试 Layer",
            allowedGameStates = AllowedGameStates.PlayingOnWorld,
            displayPriority = 1000)]
        public static void CreateTestLayer()
        {
            QueueLayerOperation(() =>
            {
                RuntimePlanetLayerSpec spec = BuildTestSpec();
                RuntimePlanetLayerService.EnsureDefinition(spec);

                PlanetLayer layer =
                    RuntimePlanetLayerService.Create(spec, TestSeedPart);
                PrepareTestLayer(layer);
                SelectAndRefresh(layer);

                ReportSuccess(
                    $"测试 Layer 已就绪：{DescribeLayer(layer)}");
            });
        }

        [DebugAction(
            DebugCategory,
            "PlanetLayer: 切换到固定测试 Layer",
            allowedGameStates = AllowedGameStates.PlayingOnWorld,
            displayPriority = 999)]
        public static void SelectTestLayer()
        {
            PlanetLayer layer =
                RuntimePlanetLayerService.FindByDefName(TestLayerDefName);
            if (layer == null)
            {
                ReportFailure("固定测试 Layer 尚未创建。");
                return;
            }

            PrepareTestLayer(layer);
            SelectAndRefresh(layer);
            ReportSuccess($"已切换到测试 Layer：{DescribeLayer(layer)}");
        }

        [DebugAction(
            DebugCategory,
            "PlanetLayer: 验证固定测试 Layer/加载状态",
            allowedGameStates = AllowedGameStates.PlayingOnWorld,
            displayPriority = 998)]
        public static void ValidateTestLayer()
        {
            PlanetLayer layer =
                RuntimePlanetLayerService.FindByDefName(TestLayerDefName);
            List<string> failures = ValidateLayerState(layer);

            if (failures.Count == 0)
            {
                ReportSuccess(
                    $"测试 Layer 验证通过：{DescribeLayer(layer)}");
                return;
            }

            ReportFailure(
                "测试 Layer 验证失败：" + Environment.NewLine +
                string.Join(Environment.NewLine, failures));
        }

        [DebugAction(
            DebugCategory,
            "PlanetLayer: 运行完整创建/删除烟雾测试",
            allowedGameStates = AllowedGameStates.PlayingOnWorld,
            displayPriority = 997)]
        public static void RunLifecycleSmokeTest()
        {
            QueueLayerOperation(() =>
            {
                PlanetLayer existing =
                    RuntimePlanetLayerService.FindByDefName(TestLayerDefName);
                if (existing != null &&
                    !RuntimePlanetLayerService.TryRemove(
                        existing,
                        allowContentDestruction: false,
                        out string cleanupReason))
                {
                    throw new InvalidOperationException(
                        "烟雾测试无法清理现有测试 Layer：" + cleanupReason);
                }

                RuntimePlanetLayerSpec spec = BuildTestSpec();
                RuntimePlanetLayerService.EnsureDefinition(spec);

                PlanetLayer first =
                    RuntimePlanetLayerService.Create(spec, TestSeedPart);
                PrepareTestLayer(first);

                PlanetLayer second =
                    RuntimePlanetLayerService.Create(spec, TestSeedPart);
                if (!ReferenceEquals(first, second))
                {
                    throw new InvalidOperationException(
                        "重复创建没有返回同一个 PlanetLayer 实例。");
                }

                ThrowIfInvalid(first, "首次创建");

                PlanetLayer rootSurface = Find.WorldGrid.Surface;
                if (rootSurface == null)
                {
                    throw new InvalidOperationException(
                        "当前世界没有根 Surface。");
                }

                if (RuntimePlanetLayerService.CanRemove(
                        rootSurface,
                        allowContentDestruction: true,
                        out string rootRemoveReason))
                {
                    throw new InvalidOperationException(
                        "根 Surface 删除保护测试失败：" +
                        rootRemoveReason);
                }

                SelectAndRefresh(first);
                if (!ReferenceEquals(PlanetLayer.Selected, first))
                {
                    throw new InvalidOperationException(
                        "测试 Layer 无法成为当前选中 Layer。");
                }

                if (!RuntimePlanetLayerService.TryRemove(
                        first,
                        allowContentDestruction: false,
                        out string removeReason))
                {
                    throw new InvalidOperationException(
                        "安全删除测试失败：" + removeReason);
                }

                if (RuntimePlanetLayerService.FindByDefName(
                        TestLayerDefName) != null ||
                    PlanetLayer.Selected == null ||
                    ReferenceEquals(PlanetLayer.Selected, first))
                {
                    throw new InvalidOperationException(
                        "安全删除后 Layer 仍存在或没有切换到备用 Layer。");
                }

                PlanetLayer contentLayer =
                    RuntimePlanetLayerService.Create(spec, TestSeedPart);
                PrepareTestLayer(contentLayer);
                ThrowIfInvalid(contentLayer, "安全删除后重建");

                WorldObject marker = CreateTestContentMarker(contentLayer);
                SelectAndRefresh(contentLayer);
                if (RuntimePlanetLayerService.TryRemove(
                        contentLayer,
                        allowContentDestruction: false,
                        out string guardedRemoveReason))
                {
                    throw new InvalidOperationException(
                        "测试 Layer 含有世界物体时，安全删除没有被拒绝。");
                }

                if (string.IsNullOrWhiteSpace(guardedRemoveReason) ||
                    marker.Destroyed ||
                    !ReferenceEquals(PlanetLayer.Selected, contentLayer) ||
                    RuntimePlanetLayerService.FindByDefName(
                        TestLayerDefName) == null)
                {
                    throw new InvalidOperationException(
                        "安全删除拒绝后，Layer 或测试世界物体状态异常。");
                }

                if (!RuntimePlanetLayerService.TryRemove(
                        contentLayer,
                        allowContentDestruction: true,
                        out string forcedRemoveReason))
                {
                    throw new InvalidOperationException(
                        "强制删除含内容测试 Layer 失败：" +
                        forcedRemoveReason);
                }

                if (!marker.Destroyed ||
                    PlanetLayer.Selected == null ||
                    ReferenceEquals(PlanetLayer.Selected, contentLayer) ||
                    RuntimePlanetLayerService.FindByDefName(
                        TestLayerDefName) != null)
                {
                    throw new InvalidOperationException(
                        "强制删除后 Layer、测试世界物体或选中状态异常。");
                }

                PlanetLayer recreated =
                    RuntimePlanetLayerService.Create(spec, TestSeedPart);
                PrepareTestLayer(recreated);
                ThrowIfInvalid(recreated, "强制删除后重建");
                SelectAndRefresh(recreated);

                ReportSuccess(
                    "完整烟雾测试通过：Def 注册、创建、重复创建、" +
                    "Tile/Biome 验证、根层保护、选中层回退、空 Layer 安全删除、" +
                    "有内容时拒绝安全删除、强制销毁内容和删除后重建均成功。" +
                    Environment.NewLine +
                    "测试 Layer 已保留，可继续执行保存和重载验证。");
            });
        }

        [DebugAction(
            DebugCategory,
            "PlanetLayer: 安全删除固定测试 Layer",
            allowedGameStates = AllowedGameStates.PlayingOnWorld,
            displayPriority = 996)]
        public static void RemoveTestLayerSafe()
        {
            RemoveTestLayer(allowContentDestruction: false);
        }

        [DebugAction(
            DebugCategory,
            "PlanetLayer: 强制删除固定测试 Layer（销毁内容）",
            allowedGameStates = AllowedGameStates.PlayingOnWorld,
            displayPriority = 995)]
        public static void RemoveTestLayerForce()
        {
            Find.WindowStack.Add(
                Dialog_MessageBox.CreateConfirmation(
                    "强制删除会销毁固定测试 Layer 上的地图、据点、商队及其他世界物体。" +
                    Environment.NewLine +
                    "确认继续？",
                    () => RemoveTestLayer(allowContentDestruction: true),
                    destructive: true,
                    title: "强制删除测试 PlanetLayer"));
        }

        internal static RuntimePlanetLayerSpec BuildTestSpec()
        {
            RuntimePlanetLayerSpec spec =
                RuntimePlanetLayerSpec.CreateSurface(
                    TestLayerDefName,
                    "运行时测试地表",
                    115f,
                    4,
                    "Desert");

            spec.Description =
                "Pioneer Framework 动态 PlanetLayer 创建与删除测试专用地表。";
            spec.GerundLabel = "在运行时测试地表";
            spec.ViewGizmoTooltip = "查看运行时 PlanetLayer 测试地表。";
            spec.ViewGizmoTexPath = "UI/Commands/ViewPlanet";
            return spec;
        }

        private static void QueueLayerOperation(Action action)
        {
            LongEventHandler.QueueLongEvent(
                action,
                "GeneratingWorld",
                doAsynchronously: false,
                exceptionHandler: exception =>
                {
                    ReportFailure(
                        "运行时 PlanetLayer 测试发生异常：" +
                        exception);
                });
        }

        private static void PrepareTestLayer(PlanetLayer layer)
        {
            if (layer == null)
            {
                throw new ArgumentNullException(nameof(layer));
            }

            BiomeDef desert =
                DefDatabase<BiomeDef>.GetNamedSilentFail("Desert");
            if (desert == null)
            {
                throw new InvalidOperationException(
                    "BiomeDef 'Desert' was not found.");
            }

            for (int tileId = 0; tileId < layer.TilesCount; tileId++)
            {
                Tile tile = layer[tileId];
                if (tile == null)
                {
                    throw new InvalidOperationException(
                        $"测试 Layer 的 Tile {tileId} 为 null。");
                }

                tile.PrimaryBiome = desert;
            }

            Find.World?.renderer?.SetDirty<WorldDrawLayer_Terrain>(layer);
        }

        private static WorldObject CreateTestContentMarker(PlanetLayer layer)
        {
            WorldObject marker =
                WorldObjectMaker.MakeWorldObject(
                    WorldObjectDefOf.DestroyedSettlement);
            marker.Tile = new PlanetTile(0, layer);
            Find.WorldObjects.Add(marker);
            return marker;
        }

        private static void SelectAndRefresh(PlanetLayer layer)
        {
            PlanetLayer.Selected = layer;
            Find.World?.renderer?.SetDirty<WorldDrawLayer_Terrain>(layer);
        }

        private static List<string> ValidateLayerState(PlanetLayer layer)
        {
            var failures = new List<string>();
            PlanetLayerDef layerDef =
                DefDatabase<PlanetLayerDef>.GetNamedSilentFail(
                    TestLayerDefName);

            if (layerDef == null)
            {
                failures.Add("- 动态 PlanetLayerDef 未注册。");
            }

            if (layer == null)
            {
                failures.Add("- 固定测试 Layer 不存在。");
                return failures;
            }

            if (!Find.WorldGrid.PlanetLayers.TryGetValue(
                    layer.LayerID,
                    out PlanetLayer registeredLayer) ||
                !ReferenceEquals(registeredLayer, layer))
            {
                failures.Add("- WorldGrid 未保存同一个 Layer 实例。");
            }

            if (layer.Def == null ||
                layer.Def.defName != TestLayerDefName)
            {
                failures.Add("- Layer 的 Def 引用或 DefName 错误。");
            }

            if (layerDef != null &&
                !ReferenceEquals(layer.Def, layerDef))
            {
                failures.Add("- Layer 没有引用当前 DefDatabase 中的 Def。");
            }

            if (layer.TilesCount <= 0)
            {
                failures.Add("- Layer 没有生成 Tile。");
                return failures;
            }

            BiomeDef desert =
                DefDatabase<BiomeDef>.GetNamedSilentFail("Desert");
            int nullTiles = 0;
            int wrongBiomeTiles = 0;
            for (int tileId = 0; tileId < layer.TilesCount; tileId++)
            {
                Tile tile = layer[tileId];
                if (tile == null)
                {
                    nullTiles++;
                }
                else if (tile.PrimaryBiome != desert)
                {
                    wrongBiomeTiles++;
                }
            }

            if (nullTiles > 0)
            {
                failures.Add($"- 存在 {nullTiles} 个 null Tile。");
            }

            if (wrongBiomeTiles > 0)
            {
                failures.Add(
                    $"- 存在 {wrongBiomeTiles} 个非 Desert Tile。");
            }

            if (layer.WorldDrawLayers == null ||
                layer.WorldDrawLayers.Count == 0)
            {
                failures.Add("- Layer 没有初始化 WorldDrawLayer。");
            }

            if (layer.Def == null ||
                !layer.Def.GenStepsInOrder.Any(
                    step => step.defName == "Terrain"))
            {
                failures.Add("- LayerDef 没有 Terrain 世界生成步骤。");
            }

            return failures;
        }

        private static void ThrowIfInvalid(
            PlanetLayer layer,
            string phase)
        {
            List<string> failures = ValidateLayerState(layer);
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{phase}验证失败：" +
                    Environment.NewLine +
                    string.Join(Environment.NewLine, failures));
            }
        }

        private static void RemoveTestLayer(bool allowContentDestruction)
        {
            PlanetLayer layer =
                RuntimePlanetLayerService.FindByDefName(TestLayerDefName);
            if (layer == null)
            {
                ReportFailure("固定测试 Layer 不存在。");
                return;
            }

            if (!RuntimePlanetLayerService.TryRemove(
                    layer,
                    allowContentDestruction,
                    out string reason))
            {
                ReportFailure("删除固定测试 Layer 失败：" + reason);
                return;
            }

            ReportSuccess(
                allowContentDestruction
                    ? "已强制删除固定测试 Layer。"
                    : "已安全删除固定测试 Layer。");
        }

        private static string DescribeLayer(PlanetLayer layer)
        {
            int worldObjectCount =
                Find.WorldObjects?
                    .AllWorldObjectsOnLayer(layer)
                    .Count ?? 0;

            return
                $"Def={layer.Def?.defName}, " +
                $"LayerID={layer.LayerID}, " +
                $"Tiles={layer.TilesCount}, " +
                $"WorldObjects={worldObjectCount}, " +
                $"Selected={layer.IsSelected}";
        }

        private static void ReportSuccess(string text)
        {
            Log.Message("[PioneerFramework] " + text);
            Messages.Message(
                text,
                MessageTypeDefOf.PositiveEvent,
                historical: false);
        }

        private static void ReportFailure(string text)
        {
            Log.Error("[PioneerFramework] " + text);
            Messages.Message(
                text,
                MessageTypeDefOf.RejectInput,
                historical: false);
        }
    }
}
