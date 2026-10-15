using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

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
        private static readonly Dictionary<int, float> celestialBodyLayers = new();

        /// <summary>当前活跃的天体 layer（= PlanetLayer.Selected 如果是天体 layer）</summary>
        public static PlanetLayer CurrentCelestialBodyLayer
        {
            get
            {
                var sel = PlanetLayer.Selected;
                if (sel != null && celestialBodyLayers.ContainsKey(sel.LayerID))
                    return sel;
                return null;
            }
        }

        /// <summary>获取指定 layer 的自转周期（tick），未注册则返回默认 60000</summary>
        public static float GetDayLength(PlanetLayer layer)
        {
            if (layer == null) return 60000f;
            return celestialBodyLayers.TryGetValue(layer.LayerID, out float len) ? len : 60000f;
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
            if (map == null || map.Tile < 0) return 60000f;

            var tile = map.Tile;
            var layerDef = tile.LayerDef;
            if (layerDef == null) return 60000f;

            var wg = Find.WorldGrid;
            if (wg == null) return 60000f;

            // 2026-10-15 反射清理：改用公开属性 `WorldGrid.PlanetLayers`（1.7 起公开，IReadOnlyDictionary）。
            var layers = wg.PlanetLayers;

            foreach (var kv in layers)
            {
                if (kv.Value?.Def == layerDef)
                {
                    return GetDayLength(kv.Value);
                }
            }
            return 60000f;
        }

        /// <summary>所有已注册的天体 layer</summary>
        public static IEnumerable<PlanetLayer> GetAllCelestialBodyLayers()
        {
            var wg = Find.WorldGrid;
            if (wg == null) yield break;
            // 2026-10-15 反射清理：公开属性 `WorldGrid.PlanetLayers`。
            var layers = wg.PlanetLayers;

            foreach (var kv in celestialBodyLayers)
            {
                if (layers.TryGetValue(kv.Key, out var layer))
                    yield return layer;
            }
        }

        /// <summary>判断一个 layer 是否为天体 layer</summary>
        public static bool IsCelestialBodyLayer(PlanetLayer layer)
        {
            return layer != null && celestialBodyLayers.ContainsKey(layer.LayerID);
        }

        // ── 实例（GameComponent）──
        private bool layersRegistered;
        private bool desertFillAttempted;

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
                        Log.Warning($"[CelestialBodyLayerManager] 天体 layer 注册连续失败 {LAYER_REGISTER_FAST_ATTEMPTS} 次，转为每 {LAYER_REGISTER_RETRY_TICKS} tick 重试（避免每帧反射 + 日志刷屏）");
                    }
                }
                TryRegisterExistingLayers();
            }

            // 确保沙漠填充至少执行一次（处理旧存档）——同门控（两者互斥：本块要求 layersRegistered）
            if (!desertFillAttempted && layersRegistered && Find.WorldGrid != null && ShouldAttemptLayerRegister())
            {
                // 2026-10-15 反射清理：公开属性 `WorldGrid.PlanetLayers`。
                var layers = Find.WorldGrid.PlanetLayers;
                if (layers.Count > 0)
                {
                    //FillCelestialLayersWithDesert(layers);
                    desertFillAttempted = true;
                }
            }
        }

        /// <summary>第 13 条：注册/填充尝试门控（前 N 次直通，之后按 tick 限频）。</summary>
        private bool ShouldAttemptLayerRegister()
        {
            if (layerRegisterAttempts < LAYER_REGISTER_FAST_ATTEMPTS) return true;
            return (Find.TickManager?.TicksGame ?? 0) >= nextLayerRegisterAttemptTick;
        }

        /// <summary>
        /// 尝试将现有 PlanetLayer 注册为天体 layer。
        /// 通过匹配 PlanetLayerDef.defName == CelestialBodyDef.defName 且 hasSurface 判定。
        /// </summary>
        private void TryRegisterExistingLayers()
        {
            var wg = Find.WorldGrid;
            if (wg == null)
            {
                Log.Warning("[CelestialBodyLayerManager] WorldGrid 为 null，跳过注册");
                return;
            }

            Log.Message("[CelestialBodyLayerManager] TryRegisterExistingLayers 开始");

            // 先确保所有天体 layer 已创建并完成世界生成
            EnsureCelestialBodyLayersExist(wg);

            // 2026-10-15 反射清理：公开属性 `WorldGrid.PlanetLayers`。
            var layers = wg.PlanetLayers;
            if (layers.Count == 0)
            {
                Log.Warning("[CelestialBodyLayerManager] planetLayers 字典为空，跳过注册");
                return;
            }

            Log.Message($"[CelestialBodyLayerManager] planetLayers 中有 {layers.Count} 个 layer");

            foreach (var kv in layers)
            {
                if (kv.Value?.Def != null)
                    Log.Message($"[CelestialBodyLayerManager]   已有 layer: {kv.Value.Def.defName} (LayerID={kv.Value.LayerID}, TilesCount={kv.Value.TilesCount})");
                TryRegisterLayer(kv.Value);
            }

            // 对所有天体 layer 统一填充沙漠
            //FillCelestialLayersWithDesert(layers); 已经修改为独立的tile生成  Feng

            layersRegistered = true;
            Log.Message("[CelestialBodyLayerManager] TryRegisterExistingLayers 完成");
        }

        /// <summary>
        /// 为所有 hasSurface=true 的 CelestialBodyDef 创建对应的 PlanetLayer（如果还不存在）。
        /// 如果 layer 已存在但 TilesCount==0（上次生成失败），重新运行世界生成。
        /// Earth/Surface 由原版 Scenario 自动创建，这里只创建额外的天体 layer（Mars、Moon 等）。
        /// </summary>
        private static void EnsureCelestialBodyLayersExist(WorldGrid wg)
        {
            // 2026-10-15 反射清理：公开属性 `WorldGrid.PlanetLayers`。
            var layers = wg.PlanetLayers;

            var allBodies = DefDatabase<CelestialBodyDef>.AllDefsListForReading;

            foreach (var body in allBodies)
            {
                if (!body.hasSurface) continue;

                string targetLayerDefName = !string.IsNullOrEmpty(body.planetLayerDefName)
                    ? body.planetLayerDefName
                    : body.defName;

                // 查找是否已存在此 layer
                PlanetLayer? existingLayer = null;
                foreach (var kv in layers)
                {
                    if (kv.Value?.Def?.defName == targetLayerDefName)
                    {
                        existingLayer = kv.Value;
                        break;
                    }
                }

                // 已存在且 tiles 正常 → 跳过
                if (existingLayer != null && existingLayer.TilesCount > 0)
                {
                    Log.Message($"[CelestialBodyLayerManager] 天体 layer {targetLayerDefName} 已存在 (TilesCount={existingLayer.TilesCount})，跳过创建");
                    continue;
                }

                // 已存在但 tiles 为空 → 需要重新生成
                if (existingLayer != null && existingLayer.TilesCount == 0)
                {
                    Log.Message($"[CelestialBodyLayerManager] 天体 layer {targetLayerDefName} 已存在但 TilesCount==0，重新运行世界生成");
                    try
                    {
                        existingLayer.RunWorldGeneration();
                        Log.Message($"[CelestialBodyLayerManager] 天体 layer {targetLayerDefName} 世界生成完成 (tiles={existingLayer.TilesCount})");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[CelestialBodyLayerManager] 天体 layer {targetLayerDefName} 世界生成失败: {ex}");
                    }
                    continue;
                }

                // 不存在 → 创建新 layer
                var layerDef = DefDatabase<PlanetLayerDef>.GetNamedSilentFail(targetLayerDefName);
                if (layerDef == null)
                {
                    Log.Warning($"[CelestialBodyLayerManager] PlanetLayerDef '{targetLayerDefName}' 未找到，跳过创建天体 layer");
                    continue;
                }

                var settingsDef = DefDatabase<PlanetLayerSettingsDef>.GetNamedSilentFail(targetLayerDefName);
                if (settingsDef == null)
                {
                    Log.Warning($"[CelestialBodyLayerManager] PlanetLayerSettingsDef '{targetLayerDefName}' 未找到，跳过创建天体 layer");
                    continue;
                }

                Log.Message($"[CelestialBodyLayerManager] 创建天体 PlanetLayer: {targetLayerDefName}");
                var newLayer = wg.RegisterPlanetLayer(layerDef, settingsDef.settings);

                try
                {
                    newLayer.RunWorldGeneration();
                    Log.Message($"[CelestialBodyLayerManager] 天体 layer {targetLayerDefName} 世界生成完成 (tiles={newLayer.TilesCount})");
                }
                catch (Exception ex)
                {
                    Log.Error($"[CelestialBodyLayerManager] 天体 layer {targetLayerDefName} 世界生成失败: {ex}");
                }
            }
        }

        /// <summary>
        /// 对所有天体 layer（地球除外）填充热带沙漠（Desert）biome。
        /// 每次加载时执行，确保 layer 切换后始终显示沙漠。
        /// </summary>
        private static void FillCelestialLayersWithDesert(Dictionary<int, PlanetLayer> layers)
        {
            var desertBiome = DefDatabase<BiomeDef>.GetNamedSilentFail("Desert");
            if (desertBiome == null)
            {
                Log.Error("[CelestialBodyLayerManager] Desert biome 未找到！填充失败");
                return;
            }

            Log.Message("[CelestialBodyLayerManager] FillCelestialLayersWithDesert 开始");

            foreach (var kv in layers)
            {
                var layer = kv.Value;
                if (layer?.Def == null) continue;

                Log.Message($"[CelestialBodyLayerManager]   检查 layer: {layer.Def.defName} (LayerID={layer.LayerID}, TilesCount={layer.TilesCount})");

                // 跳过地球的 Surface layer（由原版生成，不应覆盖）
                if (layer.Def.defName == "Surface")
                {
                    Log.Message("[CelestialBodyLayerManager]   跳过 Surface (地球)");
                    continue;
                }

                if (!IsCelestialBodyLayer(layer))
                {
                    Log.Message($"[CelestialBodyLayerManager]   {layer.Def.defName} 不是天体 layer，跳过");
                    continue;
                }

                if (layer.TilesCount == 0)
                {
                    Log.Warning($"[CelestialBodyLayerManager]   {layer.Def.defName} TilesCount==0，跳过填充（需要先重新生成）");
                    continue;
                }

                int changed = 0;
                int unchanged = 0;
                for (int i = 0; i < layer.TilesCount; i++)
                {
                    var tile = layer[i];
                    if (tile == null)
                    {
                        Log.Warning($"[CelestialBodyLayerManager]   {layer.Def.defName}[{i}] 为 null！");
                        continue;
                    }
                    if (tile.PrimaryBiome != desertBiome)
                    {
                        tile.PrimaryBiome = desertBiome;
                        changed++;
                    }
                    else
                    {
                        unchanged++;
                    }
                }

                Log.Message($"[CelestialBodyLayerManager]   {layer.Def.defName}: 已改={changed}, 未改={unchanged}, 总计={layer.TilesCount}");

                if (changed > 0)
                {
                    foreach (var drawLayer in layer.WorldDrawLayers)
                    {
                        if (drawLayer is WorldDrawLayer_Terrain)
                        {
                            drawLayer.SetDirty();
                            Log.Message($"[CelestialBodyLayerManager]   {layer.Def.defName} WorldDrawLayer_Terrain 标记为脏");
                            break;
                        }
                    }
                }
            }

            Log.Message("[CelestialBodyLayerManager] FillCelestialLayersWithDesert 完成");
        }

        /// <summary>
        /// 注册单个 layer（如果它对应一个有表面的天体）。
        /// 匹配规则：
        /// 1. 如果 CelestialBodyDef 设置了 planetLayerDefName，用该名称匹配 PlanetLayerDef.defName
        /// 2. 否则用 CelestialBodyDef.defName 匹配 PlanetLayerDef.defName
        /// </summary>
        public static void TryRegisterLayer(PlanetLayer? layer)
        {
            if (layer == null || celestialBodyLayers.ContainsKey(layer.LayerID))
                return;

            if (layer.Def == null)
            {
                Log.Warning("[CelestialBodyLayerManager] TryRegisterLayer: layer.Def 为 null，跳过");
                return;
            }

            Log.Message($"[CelestialBodyLayerManager] TryRegisterLayer: {layer.Def.defName} (LayerID={layer.LayerID})");

            var allBodies = DefDatabase<CelestialBodyDef>.AllDefsListForReading;
            CelestialBodyDef? matchedBody = null;

            foreach (var body in allBodies)
            {
                if (!body.hasSurface) continue;

                string targetLayerDefName = !string.IsNullOrEmpty(body.planetLayerDefName)
                    ? body.planetLayerDefName
                    : body.defName;

                Log.Message($"[CelestialBodyLayerManager]   对比: body={body.defName} targetDef={targetLayerDefName} vs layerDef={layer.Def.defName}");

                if (targetLayerDefName == layer.Def.defName)
                {
                    matchedBody = body;
                    break;
                }
            }

            if (matchedBody != null)
            {
                celestialBodyLayers[layer.LayerID] = (float)matchedBody.dayLengthTicks;
                Log.Message($"[CelestialBodyLayerManager] 已注册天体 layer: {layer.Def.defName} (dayLength={matchedBody.dayLengthTicks} ticks, body={matchedBody.defName})");
            }
            else
            {
                Log.Message($"[CelestialBodyLayerManager] {layer.Def.defName} 未匹配任何 CelestialBodyDef，不注册为天体 layer");
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref layersRegistered, "celBodyLayersRegistered", false);
            Scribe_Values.Look(ref desertFillAttempted, "celDesertFillAttempted", false);
        }

        // ──────────── Harmony 补丁 ────────────

        static CelestialBodyLayerManager()
        {
            var harmony = new Harmony("PioneerFramework.CelestialBodyLayerManager");

            // 2026-10-15 反射清理 A-3：原先这里还有 `PlanetLayer.Visible` / `PlanetLayer.Raycastable`
            // 两处**属性 getter** 的 Prefix 补丁 —— 已改为 **def 的 `layerType` 子类**
            //（`PF_CelestialSurfaceLayer`；原版 `Surface` def 由 `Patches/PlanetLayerDefs_CelestialSurface.xml`
            // 改指同一类）⇒ 补丁数 6 → 4。

            // 补丁1：WorldGrid.RegisterPlanetLayer 创建新 layer 时自动注册天体 layer
            var registerMethod = AccessTools.Method(typeof(WorldGrid), "RegisterPlanetLayer",
                [typeof(PlanetLayerDef), typeof(PlanetLayerSettings),
                    typeof(float?), typeof(Vector3?), typeof(Vector3?), typeof(float?)]);
            if (registerMethod != null)
            {
                harmony.Patch(registerMethod,
                    postfix: new HarmonyMethod(typeof(CelestialBodyLayerManager), nameof(RegisterPlanetLayer_Postfix)));
            }

            // 补丁2：WorldGrid.GetGizmos 追加"查看天体"Gizmo
            harmony.Patch(AccessTools.Method(typeof(WorldGrid), nameof(WorldGrid.GetGizmos)),
                postfix: new HarmonyMethod(typeof(CelestialBodyLayerManager), nameof(GetGizmos_Postfix)));

            // 补丁3：WorldGrid.CreateRequiredLayers 末尾强制注册
            var createLayersMethod = AccessTools.Method(typeof(WorldGrid), "CreateRequiredLayers");
            if (createLayersMethod != null)
            {
                harmony.Patch(createLayersMethod,
                    postfix: new HarmonyMethod(typeof(CelestialBodyLayerManager), nameof(CreateRequiredLayers_Postfix)));
            }

            // 补丁4：WorldGrid.ExposeData 加载后重新注册
            harmony.Patch(AccessTools.Method(typeof(WorldGrid), nameof(WorldGrid.ExposeData)),
                postfix: new HarmonyMethod(typeof(CelestialBodyLayerManager), nameof(WorldGrid_ExposeData_Postfix)));

            Log.Message("[CelestialBodyLayerManager] Harmony patches applied");
        }

        // ──（原 `PlanetLayer.Visible` / `PlanetLayer.Raycastable` 两处 Prefix 已迁入
        //   `PF_CelestialSurfaceLayer` 的覆写——见该类头注，2026-10-15 反射清理 A-3。）──

        // ── Postfix: RegisterPlanetLayer ──
        public static void RegisterPlanetLayer_Postfix(PlanetLayer __result)
        {
            TryRegisterLayer(__result);
        }

        // ── Postfix: CreateRequiredLayers ──
        public static void CreateRequiredLayers_Postfix()
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyLayerManager>();
            if (mgr != null)
            {
                mgr.TryRegisterExistingLayers();
            }
        }

        // ── Postfix: WorldGrid.ExposeData ──
        public static void WorldGrid_ExposeData_Postfix()
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyLayerManager>();
            if (mgr != null && !mgr.layersRegistered)
            {
                mgr.TryRegisterExistingLayers();
            }
        }

        // ──────────── "查看天体" Gizmo ────────────

        private static readonly Texture2D ViewCelestialBodyIcon = ContentFinder<Texture2D>.Get("UI/Commands/ViewPlanet");

        public static IEnumerable<Gizmo> GetGizmos_Postfix(IEnumerable<Gizmo> __result)
        {
            // 透传原有 Gizmo
            foreach (var g in __result)
                yield return g;

            // 仅在游戏中显示
            if (Current.ProgramState != ProgramState.Playing)
                yield break;

            var bodies = GetAllCelestialBodyLayers().ToList();

            var cmd = new Command_Action
            {
                defaultLabel = "查看天体",
                defaultDesc = "选择要查看的天体表面。切换后隐藏其他天体 layer。",
                icon = ViewCelestialBodyIcon,
                action = () =>
                {
                    if (bodies.Count == 0)
                    {
                        Messages.Message("没有已注册的天体 layer。请检查 CelestialBodyDef XML 配置。",
                            MessageTypeDefOf.RejectInput, false);
                        return;
                    }

                    var options = new List<FloatMenuOption>();

                    foreach (var layer in bodies)
                    {
                        var capturedLayer = layer;
                        string label = layer.Def.label ?? layer.Def.defName;

                        if (layer.IsSelected)
                            label += " (当前)";

                        options.Add(new FloatMenuOption(label, () =>
                        {
                            if (capturedLayer.IsSelected)
                                return;

                            PlanetLayer.Selected = capturedLayer;

                            // 强制重绘地形层，确保切换后 biome 更新立即显示
                            Find.World.renderer.SetDirty<WorldDrawLayer_Terrain>(capturedLayer);

                            Messages.Message($"已切换至天体视图：{(capturedLayer.Def.label ?? capturedLayer.Def.defName)}",
                                MessageTypeDefOf.SilentInput, false);
                        }));
                    }

                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };

            // 无注册天体时禁用
            if (bodies.Count == 0)
                cmd.Disable("没有已注册的天体 layer。请先配置 CelestialBodyDef XML。");

            yield return cmd;
        }
    }
}
