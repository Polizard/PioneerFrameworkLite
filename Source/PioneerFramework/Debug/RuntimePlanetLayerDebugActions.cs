using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework
{
    /// <summary>PFL 测试页签的运行时地表工具；删除操作保留所有含世界物体的层。</summary>
    internal static class RuntimePlanetLayerDebugActions
    {
        private const string TestLayerDefName = "PF_DebugPlanetLayer";

        internal static void RegisterDefinition()
        {
            // 每次启动都注册同一描述，使存档中的动态 Def 引用可以在读档前恢复。
            RuntimePlanetLayerService.EnsureDefinition(CreateTestSpec());
        }

        private static RuntimePlanetLayerSpec CreateTestSpec()
        {
            return RuntimePlanetLayerSpec.CreateSurface(
                TestLayerDefName, "PFL 测试地表", 30f, 4, "Desert");
        }

        public static void CreateTestLayer()
        {
            if (!WorldReady()) return;

            try
            {
                bool existed = RuntimePlanetLayerService.FindByDefName(TestLayerDefName) != null;
                PlanetLayer layer = RuntimePlanetLayerService.Create(CreateTestSpec(), seedPart: 194913);
                ViewLayer(layer);
                string message = existed ? "测试地表已存在，已切换视图" : "已创建测试地表";
                Log.Message($"[PFL·PlanetLayer] {message}：{Describe(layer)}");
                Messages.Message(message, MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception exception)
            {
                Log.Error($"[PFL·PlanetLayer] 创建测试地表失败：{exception}");
                Messages.Message("创建测试地表失败，请查看日志", MessageTypeDefOf.RejectInput, false);
            }
        }

        public static void ChooseLayerToView()
        {
            if (!WorldReady()) return;

            var options = new List<FloatMenuOption>();
            foreach (PlanetLayer layer in Find.WorldGrid.PlanetLayers.Values.OrderBy(layer => layer.LayerID))
            {
                PlanetLayer capturedLayer = layer;
                options.Add(new FloatMenuOption(Describe(layer), () =>
                {
                    if (!WorldReady()) return;
                    if (!IsRegistered(capturedLayer))
                    {
                        Messages.Message("该 PlanetLayer 已移除，请重新打开菜单", MessageTypeDefOf.RejectInput, false);
                        return;
                    }
                    ViewLayer(capturedLayer);
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        public static void ChooseLayerToRemove()
        {
            if (!WorldReady()) return;

            var options = new List<FloatMenuOption>();
            foreach (PlanetLayer layer in Find.WorldGrid.PlanetLayers.Values.OrderBy(layer => layer.LayerID))
            {
                PlanetLayer capturedLayer = layer;
                if (RuntimePlanetLayerService.CanRemove(layer, allowContentDestruction: false, out string reason))
                {
                    options.Add(new FloatMenuOption(Describe(layer), () =>
                    {
                        if (!WorldReady()) return;
                        if (RuntimePlanetLayerService.TryRemove(
                            capturedLayer, allowContentDestruction: false, out string removeReason))
                        {
                            Log.Message($"[PFL·PlanetLayer] 已移除：{capturedLayer.Def.defName} (ID={capturedLayer.LayerID})");
                            Messages.Message("已移除 PlanetLayer", MessageTypeDefOf.TaskCompletion, false);
                        }
                        else
                        {
                            Log.Warning($"[PFL·PlanetLayer] 删除被拒绝：{removeReason}");
                            Messages.Message($"无法删除：{removeReason}", MessageTypeDefOf.RejectInput, false);
                        }
                    }));
                }
                else
                {
                    // 禁用项保留在菜单中，便于核对受保护层和含内容层的删除限制。
                    options.Add(new FloatMenuOption($"{Describe(layer)} — {reason}", null));
                }
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        public static void PrintLayers()
        {
            if (!WorldReady()) return;

            var text = new StringBuilder("[PFL·PlanetLayer] 当前层状态");
            foreach (PlanetLayer layer in Find.WorldGrid.PlanetLayers.Values.OrderBy(layer => layer.LayerID))
            {
                int objectCount = Find.WorldObjects.AllWorldObjectsOnLayer(layer).Count;
                bool removable = RuntimePlanetLayerService.CanRemove(
                    layer, allowContentDestruction: false, out string reason);
                text.Append($"\n  {Describe(layer)} | selected={layer.IsSelected} | radius={layer.Radius}" +
                    $" | worldObjects={objectCount} | removable={removable}");
                if (!removable) text.Append($" ({reason})");
            }
            Log.Message(text.ToString());
            Messages.Message("PlanetLayer 状态已输出到日志", MessageTypeDefOf.TaskCompletion, false);
        }

        private static bool WorldReady()
        {
            if (!Prefs.DevMode || Current.ProgramState != ProgramState.Playing ||
                Find.WorldGrid == null || Find.WorldSelector == null || Scribe.mode != LoadSaveMode.Inactive)
            {
                Messages.Message("请在已加载游戏的开发者模式下使用 PlanetLayer 工具", MessageTypeDefOf.RejectInput, false);
                return false;
            }
            return true;
        }

        private static bool IsRegistered(PlanetLayer layer)
        {
            return Find.WorldGrid.PlanetLayers.TryGetValue(layer.LayerID, out PlanetLayer? registered) &&
                ReferenceEquals(registered, layer);
        }

        private static void ViewLayer(PlanetLayer layer)
        {
            PlanetLayer.Selected = layer;
            CameraJumper.TryShowWorld();
        }

        private static string Describe(PlanetLayer layer)
        {
            return $"{layer.Def.LabelCap} [{layer.Def.defName}] (ID={layer.LayerID}, tiles={layer.TilesCount})";
        }

        internal static bool ShouldHideLayer(PlanetLayer? candidate, PlanetLayer? selected)
        {
            return candidate != null && !ReferenceEquals(candidate, selected) &&
                (candidate.Def?.defName == TestLayerDefName || selected?.Def?.defName == TestLayerDefName);
        }

        internal static bool FilterLayerPrefix(PlanetLayer __instance, ref bool __result)
        {
            if (!ShouldHideLayer(__instance, Find.WorldSelector?.SelectedLayer)) return true;
            __result = false;
            return false;
        }
    }

    [StaticConstructorOnStartup]
    internal static class RuntimePlanetLayerDebugStartup
    {
        static RuntimePlanetLayerDebugStartup()
        {
            // 游戏启动钩子在 Def 加载后执行；提前触达类型时避免访问尚未就绪的引擎服务。
            if (DefDatabase<BiomeDef>.GetNamedSilentFail("Desert") == null ||
                DefDatabase<WorldGenStepDef>.GetNamedSilentFail("Terrain") == null) return;

            try
            {
                RuntimePlanetLayerDebugActions.RegisterDefinition();
                // 测试层独立于物理天体表；只在选中测试层时隐藏其他地表，避免球面遮挡。
                var harmony = new Harmony("PioneerFramework.RuntimePlanetLayerDebug");
                var prefix = new HarmonyMethod(typeof(RuntimePlanetLayerDebugActions),
                    nameof(RuntimePlanetLayerDebugActions.FilterLayerPrefix));
                harmony.Patch(AccessTools.PropertyGetter(typeof(PlanetLayer), nameof(PlanetLayer.Visible)), prefix: prefix);
                harmony.Patch(AccessTools.PropertyGetter(typeof(PlanetLayer), nameof(PlanetLayer.Raycastable)), prefix: prefix);
            }
            catch (Exception exception)
            {
                Log.Error($"[PFL·PlanetLayer] 初始化测试层工具失败：{exception}");
            }
        }
    }
}
