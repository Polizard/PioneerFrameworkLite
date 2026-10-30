using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using PioneerFramework.Celestial;

namespace PioneerFramework
{
    /// <summary>
    /// 天体 Layer 管理器。负责：
    /// 1. 注册/追踪所有天体表面 PlanetLayer
    /// 2. 提供"查看天体"Gizmo（FloatMenu 选择天体）
    /// 3. 确保同一时间只有一个天体 layer 可见
    /// 4. 暴露每个 layer 的自转周期（dayLengthTicks），供 CalendarAndSunPatch 使用
    /// </summary>
    [StaticConstructorOnStartup]
    public class CelestialBodyLayerManager : GameComponent
    {
        // ── 静态注册 ──
        /// <summary>PlanetLayer.LayerID → 自转周期(tick)</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlanetLayer, DayLength> celestialBodyLayers = new();
        private static long layerRegistrationRevision;
        private sealed record DayLength(float Value);

        /// <summary>当前活跃的天体 layer（= PlanetLayer.Selected 如果是天体 layer）</summary>
        public static PlanetLayer CurrentCelestialBodyLayer
        {
            get
            {
                var sel = PlanetLayer.Selected;
                if (sel != null && IsCelestialBodyLayer(sel))
                    return sel;
                return null;
            }
        }

        /// <summary>获取指定 layer 的自转周期（tick），未注册则返回默认 60000</summary>
        public static float GetDayLength(PlanetLayer layer)
        {
            if (layer == null) return 60000f;
            if (!celestialBodyLayers.TryGetValue(layer, out _)) TryRegisterLayer(layer);
            return celestialBodyLayers.TryGetValue(layer, out var length) ? length.Value : 60000f;
        }

        /// <summary>获取当前选中天体 layer 的自转周期</summary>
        public static float GetCurrentDayLength()
        {
            var layer = CurrentCelestialBodyLayer;
            return layer != null ? GetDayLength(layer) : 60000f;
        }

        /// <summary>
        /// 根据 Map 获取其所在 PlanetLayer 的自转周期。
        /// 用于 Map 光照计算：应该用 Map 实际所在星球的周期，而非当前查看的星球。
        /// </summary>
        public static float GetDayLengthForMap(Map map)
        {
            if (map == null) return 60000f;

            // 2026-10-16 降成本：原先每次都**遍历全部 PlanetLayer 比 Def**（本方法在
            // `CurCelestialSunGlow` / `GenLocalDate.DayPercent(Map)` 两条**每帧**前缀里各调一次）。
            // 1.7 的 `PlanetTile.Layer` 直接给层（`PlanetLayers[layerId]` 一次字典查），
            // 而 `GetDayLength(layer)` 也是按 `LayerID` 的字典查 ⇒ 两条 O(1)，遍历删去。
            var tile = map.Tile;
            if (!tile.Valid) return 60000f;

            var layer = tile.Layer;
            return layer != null ? GetDayLength(layer) : 60000f;
        }

        /// <summary>所有已注册的天体 layer</summary>
        public static IEnumerable<PlanetLayer> GetAllCelestialBodyLayers()
        {
            var wg = Find.WorldGrid;
            if (wg == null) yield break;
            // 2026-10-15 反射清理：公开属性 `WorldGrid.PlanetLayers`。
            var layers = wg.PlanetLayers;

            foreach (var layer in layers.Values)
                if (layer is CelestialSurfaceLayer) yield return layer;
        }

        /// <summary>判断一个 layer 是否为天体 layer</summary>
        public static bool IsCelestialBodyLayer(PlanetLayer layer)
        {
            return layer != null && layer is CelestialSurfaceLayer;
        }

        // ── 实例（GameComponent）──
        private bool layersRegistered;

        // 第 13 条（2026-09-10）：GameComponentUpdate 是**每帧**调用，而注册失败路径内含字典查询
        // 与多条 Log.Message/Warning。若某些存档永远注册不成立，就变成每帧查询 + 日志刷屏。
        // 故限频：前 N 次每帧试（覆盖 WorldGrid 刚就绪的启动窗口），之后每 RETRY_TICKS 试一次。
        // （2026-10-15 反射清理后本文件已无字段反射，限频仍保留——它挡的是日志刷屏。）
        private int layerRegisterAttempts;
        private int nextLayerRegisterAttemptTick;
        private bool layerRegisterSlowLogged;
        private const int LAYER_REGISTER_FAST_ATTEMPTS = 120; // ≈2s @60fps
        private const int LAYER_REGISTER_RETRY_TICKS = 600;

        public CelestialBodyLayerManager(Game game) : base()
        {
        }

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            // 延迟注册：等待 WorldGrid 初始化完毕（失败路径限频，见字段注释）
            if (!layersRegistered && Find.WorldGrid != null && ShouldAttemptLayerRegister())
            {
                layerRegisterAttempts++;
                if (layerRegisterAttempts > LAYER_REGISTER_FAST_ATTEMPTS)
                {
                    nextLayerRegisterAttemptTick = (Find.TickManager?.TicksGame ?? 0) + LAYER_REGISTER_RETRY_TICKS;
                    if (!layerRegisterSlowLogged)
                    {
                        layerRegisterSlowLogged = true;
                        Log.Warning($"[CelestialBodyLayerManager] 天体 layer 注册连续失败 {LAYER_REGISTER_FAST_ATTEMPTS} 次，转为每 {LAYER_REGISTER_RETRY_TICKS} tick 重试（限制重复注册尝试与日志）");
                    }
                }
                TryRegisterExistingLayers();
            }

        }

        /// <summary>注册尝试门控（前 N 次直通，之后按 tick 限频）。</summary>
        private bool ShouldAttemptLayerRegister()
        {
            if (layerRegisterAttempts < LAYER_REGISTER_FAST_ATTEMPTS) return true;
            return (Find.TickManager?.TicksGame ?? 0) >= nextLayerRegisterAttemptTick;
        }

        /// <summary>只登记实际存在的实例地表，不创建、不补生成任何层。</summary>
        private void TryRegisterExistingLayers()
        {
            var grid = Current.CreatingWorld?.grid ?? Current.Game?.World?.grid;
            if (grid == null) return;
            foreach (var layer in grid.PlanetLayers.Values) TryRegisterLayer(layer);
            layersRegistered = true;
        }
        public static void TryRegisterLayer(PlanetLayer? layer)
        {
            if (layer is not CelestialSurfaceLayer surface) return;
            if (celestialBodyLayers.TryGetValue(layer, out _)) return;
            var configuration = CelestialGenerationConfiguration.Decode(surface.Header.GenerationConfiguration);
            if (!configuration.Success) return;
            float day = 60000f;
            var saved = configuration.Value.SingleOrDefault(record => record.HandlerId == ExistingEnvironmentResolvedProperties.Handler);
            if (saved != null)
            {
                var profile = ExistingEnvironmentResolvedProperties.Deserialize(saved.Key, saved.ConfigurationVersion, saved.ConfigurationPayload);
                if (profile.Success) day = (float)profile.Value.Profile.DayLengthTicks;
            }
            var entry = new DayLength(day);
            if (ReferenceEquals(celestialBodyLayers.GetValue(surface, _ => entry), entry)) System.Threading.Interlocked.Increment(ref layerRegistrationRevision);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref layersRegistered, "celBodyLayersRegistered", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Current.Game?.GetComponent<CelestialBodyManager>()?.HasCelestialState == true)
            {
                layersRegistered = false;
            }
        }

        // ──────────── Harmony 补丁 ────────────

        static CelestialBodyLayerManager()
        {
            var harmony = new Harmony("PioneerFramework.CelestialBodyLayerManager");

            // 2026-10-15 反射清理 A-3：原先这里还有 `PlanetLayer.Visible` / `PlanetLayer.Raycastable`
            // 两处**属性 getter** 的 Prefix 补丁 —— 已改为 **def 的 `layerType` 子类**
            //（`PF_CelestialSurfaceLayer`；原版 `Surface` def 由 `Patches/PlanetLayerDefs_CelestialSurface.xml`
            // 改指同一类）⇒ 补丁数 6 → 4。

            // 2026-10-16 反射清理 A-5（WorldGrid 族细判）：安装点 **4 → 2**（两条按源码核实为冗余）。
            //  · 删「`WorldGrid.RegisterPlanetLayer` 的 Postfix」：读 1.7 源码 —— 该函数在 vanilla 里
            //    **唯一的生产调用者就是 `WorldGrid.CreateRequiredLayers`**（另一处是开发者调试动作
            //    `DebugActionsMisc.TestLayer`，造的是 Orbit 层、本就不该注册为天体层），
            //    而下面的补丁3 会在 `CreateRequiredLayers` 之后把**全部层**扫一遍统一注册 ⇒ 逐层钩子冗余。
            //    我们自己那两条建层路径（`RuntimePlanetLayerService.CreateLayerIfMissing` /
            //    `EnsureCelestialBodyLayersExist`——后者本就随 `TryRegisterExistingLayers` 的全量扫描一起注册）
            //    改为显式调用 `NotifyLayerCreated`（见 `RuntimePlanetLayerService` 那处）。
            //  · 删「`WorldGrid.ExposeData` 的 Postfix」：读档路径里 `ExposeData` 的 `PostLoadInit` 分支
            //    **自己就会调 `CreateRequiredLayers()`**（`WorldGrid.cs:924`，无条件）⇒ 已被补丁3 覆盖，重复。

            // 补丁3：WorldGrid.CreateRequiredLayers 末尾强制注册（新世界 / 读档两条路都经它）
            var createLayersMethod = CelestialNativeCompatibility.RequireMethod("WorldGrid.CreateRequiredLayers");
            if (createLayersMethod != null)
            {
                harmony.Patch(createLayersMethod,
                    postfix: new HarmonyMethod(typeof(CelestialBodyLayerManager), nameof(CreateRequiredLayers_Postfix)));
            }

            // 补丁4：WorldGrid.GetGizmos 追加"查看天体"Gizmo（保留：**无 def 通道**——vanilla 的层 Gizmo 由
            // `PlanetLayerDef.viewGizmoTexPath` 生成，管不到我们这套"按天体列浮窗切视图"。
            // ⚠ 这是**每帧**路径（Gizmo 每帧枚举）：2026-10-16 起命令走缓存，见 `GetOrBuildCelestialViewCommand`。）
            harmony.Patch(CelestialNativeCompatibility.RequireMethod("WorldGrid.GetGizmos"),
                postfix: new HarmonyMethod(typeof(CelestialBodyLayerManager), nameof(GetGizmos_Postfix)));

            CelestialNativeCompatibility.RequireInstalledHooks("WorldGrid.CreateRequiredLayers.postfix.CreateRequiredLayers_Postfix", "WorldGrid.GetGizmos.postfix.GetGizmos_Postfix");
            Log.Message("[CelestialBodyLayerManager] Harmony patches applied");
        }

        // ──（原 `PlanetLayer.Visible` / `PlanetLayer.Raycastable` 两处 Prefix 已迁入
        //   `PF_CelestialSurfaceLayer` 的覆写——见该类头注，2026-10-15 反射清理 A-3。）──

        // ── Postfix: CreateRequiredLayers ──
        public static void CreateRequiredLayers_Postfix()
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyLayerManager>();
            if (mgr != null)
            {
                mgr.TryRegisterExistingLayers();
            }
        }

        /// <summary>
        /// **层新建后的显式注册入口**（2026-10-16 反射清理 A-5：替代原 `WorldGrid.RegisterPlanetLayer` 的 Postfix）。
        /// 只转发到 <see cref="TryRegisterLayer"/>（它自己会判"不匹配任何 CelestialBodyDef 就不注册"）。
        /// 调用点 = 我们自己的建层路径（`RuntimePlanetLayerService.CreateLayerIfMissing`）。
        /// </summary>
        public static void NotifyLayerCreated(PlanetLayer? layer)
        {
            TryRegisterLayer(layer);
        }

        // ──────────── "查看天体" Gizmo ────────────

        private static readonly Texture2D ViewCelestialBodyIcon = ContentFinder<Texture2D>.Get("UI/Commands/ViewPlanet");

        // 缓存（2026-10-16）：`WorldGrid.GetGizmos` 是**每帧**枚举 ⇒ 不许每帧 LINQ + new。
        private static readonly List<PlanetLayer> cachedBodies = new();
        private static Command_Action? cachedViewCommand;
        private static CelestialBodyLayerManager? cachedGizmoMgr;
        private static WorldGrid? cachedGizmoGrid;
        private static long cachedLayerRevision = -1;
        private static int cachedSelectedLayerId = int.MinValue;
        private static long cachedSurfaceRevision = -1;

        public static IEnumerable<Gizmo> GetGizmos_Postfix(IEnumerable<Gizmo> __result)
        {
            // 透传原有 Gizmo
            foreach (var g in __result)
                yield return g;

            // 仅在游戏中显示
            if (Current.ProgramState != ProgramState.Playing)
                yield break;

            yield return GetOrBuildCelestialViewCommand();
        }

        /// <summary>
        /// "查看天体"命令 —— **缓存版**（2026-10-16 降成本）：旧实现每帧走
        /// `GetAllCelestialBodyLayers().ToList()`（LINQ 迭代器 + List 分配）并 `new Command_Action`
        /// （含闭包）⇒ 每帧 GC 垃圾。现在只在"**换局 / 换世界 / 注册数变化 / 选中的天体层变化**"时重建；
        /// 浮窗候选项本来就是点击时才建（标签含"(当前)"态 ⇒ 每次点开都是最新）。
        /// </summary>
        private static Command_Action GetOrBuildCelestialViewCommand()
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyLayerManager>();
            var grid = Find.WorldGrid;
            int selectedId = PlanetLayer.Selected?.LayerID ?? -1;
            var bodies = Current.Game?.GetComponent<CelestialBodyManager>();
            long surfaceRevision = bodies?.TryGetCelestialRuntime(out var runtime) == true ? runtime.Surfaces?.ResourceRevision ?? 0 : 0;

            if (cachedViewCommand == null
                || !ReferenceEquals(cachedGizmoMgr, mgr)
                || !ReferenceEquals(cachedGizmoGrid, grid)
                || cachedLayerRevision != layerRegistrationRevision
                || cachedSelectedLayerId != selectedId
                || cachedSurfaceRevision != surfaceRevision)
            {
                cachedGizmoMgr = mgr;
                cachedGizmoGrid = grid;
                cachedLayerRevision = layerRegistrationRevision;
                cachedSelectedLayerId = selectedId;
                cachedSurfaceRevision = surfaceRevision;

                cachedBodies.Clear();
                cachedBodies.AddRange(GetAllCelestialBodyLayers());

                cachedViewCommand = BuildCelestialViewCommand();
            }

            return cachedViewCommand;
        }

        private static Command_Action BuildCelestialViewCommand()
        {
            var cmd = new Command_Action
            {
                defaultLabel = "查看天体",
                defaultDesc = "选择要查看的天体表面。切换后隐藏其他天体 layer。",
                icon = ViewCelestialBodyIcon,
                action = () =>
                {
                    if (cachedBodies.Count == 0)
                    {
                        Messages.Message("没有已注册的天体 layer。请检查 CelestialBodyDef XML 配置。",
                            MessageTypeDefOf.RejectInput, false);
                        return;
                    }

                    var options = new List<FloatMenuOption>();
                    var session = Current.Game;
                    var manager = session?.GetComponent<CelestialBodyManager>();
                    var grid = Find.WorldGrid;

                    foreach (var layer in cachedBodies)
                    {
                        var capturedLayer = layer;
                        int handle = Launch.GetSurfaceState.GetBodyForLayer(manager, layer);
                        var captured = manager?.CaptureTarget(handle);
                        if (captured?.Success != true) continue;
                        string bodyName = manager!.GetBodyName(handle);
                        string label = bodyName;

                        if (layer.IsSelected)
                            label += " (当前)";

                        options.Add(new FloatMenuOption(label, () =>
                        {
                            if (!ReferenceEquals(Current.Game, session) || !ReferenceEquals(Find.WorldGrid, grid) ||
                                !grid.PlanetLayers.TryGetValue(capturedLayer.LayerID, out var current) || !ReferenceEquals(current, capturedLayer) ||
                                !manager.ValidateTarget(captured.Value).Success) return;
                            if (capturedLayer.IsSelected)
                                return;

                            PlanetLayer.Selected = capturedLayer;

                            Messages.Message($"已切换至天体视图：{bodyName}",
                                MessageTypeDefOf.SilentInput, false);
                        }));
                    }

                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };

            // 无注册天体时禁用
            if (cachedBodies.Count == 0)
                cmd.Disable("没有已注册的天体 layer。请先配置 CelestialBodyDef XML。");

            return cmd;
        }
    }
}
