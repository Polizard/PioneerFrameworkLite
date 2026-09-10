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
        private static readonly Dictionary<int, float> celestialBodyLayers = new Dictionary<int, float>();

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

            var layersField = AccessTools.Field(typeof(WorldGrid), "planetLayers");
            var layers = layersField?.GetValue(wg) as Dictionary<int, PlanetLayer>;
            if (layers == null) return 60000f;

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
            // 反射访问 planetLayers（private 字典）
            var layersField = AccessTools.Field(typeof(WorldGrid), "planetLayers");
            var layers = layersField?.GetValue(wg) as Dictionary<int, PlanetLayer>;
            if (layers == null) yield break;

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

        // 第 13 条（2026-09-10）：GameComponentUpdate 是**每帧**调用，而注册失败路径内含反射
        // （AccessTools.Field）与多条 Log.Message/Warning。若某些存档永远注册不成立，就变成每帧反射 + 日志刷屏。
        // 故限频：前 N 次每帧试（覆盖 WorldGrid 刚就绪的启动窗口），之后每 RETRY_TICKS 试一次。
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
                        Log.Warning(string.Format(
                            "[CelestialBodyLayerManager] 天体 layer 注册连续失败 {0} 次，转为每 {1} tick 重试（避免每帧反射 + 日志刷屏）",
                            LAYER_REGISTER_FAST_ATTEMPTS, LAYER_REGISTER_RETRY_TICKS));
                    }
                }
                TryRegisterExistingLayers();
            }

            // 确保沙漠填充至少执行一次（处理旧存档）——同门控（两者互斥：本块要求 layersRegistered）
            if (!desertFillAttempted && layersRegistered && Find.WorldGrid != null && ShouldAttemptLayerRegister())
            {
                var layersField = AccessTools.Field(typeof(WorldGrid), "planetLayers");
                var layers = layersField?.GetValue(Find.WorldGrid) as Dictionary<int, PlanetLayer>;
                if (layers != null && layers.Count > 0)
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

            var layersField = AccessTools.Field(typeof(WorldGrid), "planetLayers");
            var layers = layersField?.GetValue(wg) as Dictionary<int, PlanetLayer>;
            if (layers == null || layers.Count == 0)
            {
                Log.Warning("[CelestialBodyLayerManager] planetLayers 字典为空，跳过注册");
                return;
            }

            Log.Message(string.Format("[CelestialBodyLayerManager] planetLayers 中有 {0} 个 layer", layers.Count));

            foreach (var kv in layers)
            {
                if (kv.Value?.Def != null)
                    Log.Message(string.Format("[CelestialBodyLayerManager]   已有 layer: {0} (LayerID={1}, TilesCount={2})",
                        kv.Value.Def.defName, kv.Value.LayerID, kv.Value.TilesCount));
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
            var layersField = AccessTools.Field(typeof(WorldGrid), "planetLayers");
            var layers = layersField?.GetValue(wg) as Dictionary<int, PlanetLayer>;
            if (layers == null) return;

            var allBodies = DefDatabase<CelestialBodyDef>.AllDefsListForReading;

            foreach (var body in allBodies)
            {
                if (!body.hasSurface) continue;

                string targetLayerDefName = !string.IsNullOrEmpty(body.planetLayerDefName)
                    ? body.planetLayerDefName
                    : body.defName;

                // 查找是否已存在此 layer
                PlanetLayer existingLayer = null;
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
                    Log.Message(string.Format("[CelestialBodyLayerManager] 天体 layer {0} 已存在 (TilesCount={1})，跳过创建",
                        targetLayerDefName, existingLayer.TilesCount));
                    continue;
                }

                // 已存在但 tiles 为空 → 需要重新生成
                if (existingLayer != null && existingLayer.TilesCount == 0)
                {
                    Log.Message(string.Format("[CelestialBodyLayerManager] 天体 layer {0} 已存在但 TilesCount==0，重新运行世界生成",
                        targetLayerDefName));
                    try
                    {
                        existingLayer.RunWorldGeneration();
                        Log.Message(string.Format("[CelestialBodyLayerManager] 天体 layer {0} 世界生成完成 (tiles={1})",
                            targetLayerDefName, existingLayer.TilesCount));
                    }
                    catch (Exception ex)
                    {
                        Log.Error(string.Format("[CelestialBodyLayerManager] 天体 layer {0} 世界生成失败: {1}", targetLayerDefName, ex));
                    }
                    continue;
                }

                // 不存在 → 创建新 layer
                var layerDef = DefDatabase<PlanetLayerDef>.GetNamedSilentFail(targetLayerDefName);
                if (layerDef == null)
                {
                    Log.Warning(string.Format("[CelestialBodyLayerManager] PlanetLayerDef '{0}' 未找到，跳过创建天体 layer", targetLayerDefName));
                    continue;
                }

                var settingsDef = DefDatabase<PlanetLayerSettingsDef>.GetNamedSilentFail(targetLayerDefName);
                if (settingsDef == null)
                {
                    Log.Warning(string.Format("[CelestialBodyLayerManager] PlanetLayerSettingsDef '{0}' 未找到，跳过创建天体 layer", targetLayerDefName));
                    continue;
                }

                Log.Message(string.Format("[CelestialBodyLayerManager] 创建天体 PlanetLayer: {0}", targetLayerDefName));
                var newLayer = wg.RegisterPlanetLayer(layerDef, settingsDef.settings);

                try
                {
                    newLayer.RunWorldGeneration();
                    Log.Message(string.Format("[CelestialBodyLayerManager] 天体 layer {0} 世界生成完成 (tiles={1})",
                        targetLayerDefName, newLayer.TilesCount));
                }
                catch (Exception ex)
                {
                    Log.Error(string.Format("[CelestialBodyLayerManager] 天体 layer {0} 世界生成失败: {1}", targetLayerDefName, ex));
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

                Log.Message(string.Format("[CelestialBodyLayerManager]   检查 layer: {0} (LayerID={1}, TilesCount={2})",
                    layer.Def.defName, layer.LayerID, layer.TilesCount));

                // 跳过地球的 Surface layer（由原版生成，不应覆盖）
                if (layer.Def.defName == "Surface")
                {
                    Log.Message(string.Format("[CelestialBodyLayerManager]   跳过 Surface (地球)"));
                    continue;
                }

                if (!IsCelestialBodyLayer(layer))
                {
                    Log.Message(string.Format("[CelestialBodyLayerManager]   {0} 不是天体 layer，跳过", layer.Def.defName));
                    continue;
                }

                if (layer.TilesCount == 0)
                {
                    Log.Warning(string.Format("[CelestialBodyLayerManager]   {0} TilesCount==0，跳过填充（需要先重新生成）", layer.Def.defName));
                    continue;
                }

                int changed = 0;
                int unchanged = 0;
                for (int i = 0; i < layer.TilesCount; i++)
                {
                    var tile = layer[i];
                    if (tile == null)
                    {
                        Log.Warning(string.Format("[CelestialBodyLayerManager]   {0}[{1}] 为 null！", layer.Def.defName, i));
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

                Log.Message(string.Format("[CelestialBodyLayerManager]   {0}: 已改={1}, 未改={2}, 总计={3}",
                    layer.Def.defName, changed, unchanged, layer.TilesCount));

                if (changed > 0)
                {
                    foreach (var drawLayer in layer.WorldDrawLayers)
                    {
                        if (drawLayer is WorldDrawLayer_Terrain)
                        {
                            drawLayer.SetDirty();
                            Log.Message(string.Format("[CelestialBodyLayerManager]   {0} WorldDrawLayer_Terrain 标记为脏", layer.Def.defName));
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
        public static void TryRegisterLayer(PlanetLayer layer)
        {
            if (layer == null || celestialBodyLayers.ContainsKey(layer.LayerID))
                return;

            if (layer.Def == null)
            {
                Log.Warning("[CelestialBodyLayerManager] TryRegisterLayer: layer.Def 为 null，跳过");
                return;
            }

            Log.Message(string.Format("[CelestialBodyLayerManager] TryRegisterLayer: {0} (LayerID={1})", layer.Def.defName, layer.LayerID));

            var allBodies = DefDatabase<CelestialBodyDef>.AllDefsListForReading;
            CelestialBodyDef matchedBody = null;

            foreach (var body in allBodies)
            {
                if (!body.hasSurface) continue;

                string targetLayerDefName = !string.IsNullOrEmpty(body.planetLayerDefName)
                    ? body.planetLayerDefName
                    : body.defName;

                Log.Message(string.Format("[CelestialBodyLayerManager]   对比: body={0} targetDef={1} vs layerDef={2}",
                    body.defName, targetLayerDefName, layer.Def.defName));

                if (targetLayerDefName == layer.Def.defName)
                {
                    matchedBody = body;
                    break;
                }
            }

            if (matchedBody != null)
            {
                celestialBodyLayers[layer.LayerID] = (float)matchedBody.dayLengthTicks;
                Log.Message(string.Format("[CelestialBodyLayerManager] 已注册天体 layer: {0} (dayLength={1} ticks, body={2})",
                    layer.Def.defName, matchedBody.dayLengthTicks, matchedBody.defName));
            }
            else
            {
                Log.Message(string.Format("[CelestialBodyLayerManager] {0} 未匹配任何 CelestialBodyDef，不注册为天体 layer", layer.Def.defName));
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

            // 补丁0：PlanetLayer.Visible — 确保非选中的天体 layer 不可见
            // 防止 Earth 的 Surface layer (radius=100) 在选中 Mars/Moon (radius<100) 时仍然渲染
            harmony.Patch(AccessTools.PropertyGetter(typeof(PlanetLayer), nameof(PlanetLayer.Visible)),
                prefix: new HarmonyMethod(typeof(CelestialBodyLayerManager), nameof(PlanetLayer_Visible_Prefix)));

            // 补丁0b：PlanetLayer.Raycastable — 确保非选中的天体 layer 不参与射线检测
            // 否则 Earth 的大球壳 collider 会挡住射线，导致火星/月球的 tile 无法被点击
            harmony.Patch(AccessTools.PropertyGetter(typeof(PlanetLayer), nameof(PlanetLayer.Raycastable)),
                prefix: new HarmonyMethod(typeof(CelestialBodyLayerManager), nameof(PlanetLayer_Raycastable_Prefix)));

            // 补丁1：WorldGrid.RegisterPlanetLayer 创建新 layer 时自动注册天体 layer
            var registerMethod = AccessTools.Method(typeof(WorldGrid), "RegisterPlanetLayer",
                new Type[] { typeof(PlanetLayerDef), typeof(PlanetLayerSettings),
                    typeof(float?), typeof(Vector3?), typeof(Vector3?), typeof(float?) });
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

        // ── Prefix: PlanetLayer.Visible ──
        /// <summary>
        /// 当选中的是某个天体 layer 时，强制其他天体 layer（包括 Earth 的 Surface）不可见。
        /// 防止 Earth 的 Sphere(radius=100) 在选中 Mars(70)/Moon(35) 时仍然渲染并覆盖小星球。
        /// </summary>
        public static bool PlanetLayer_Visible_Prefix(PlanetLayer __instance, ref bool __result)
        {
            var selected = CurrentCelestialBodyLayer;
            if (selected == null) return true; // 未选中天体 layer，正常行为

            // 如果当前查询的 layer 是另一个已注册的天体 layer，隐藏它
            if (IsCelestialBodyLayer(__instance) && __instance != selected)
            {
                __result = false;
                return false;
            }

            return true;
        }

        // ── Prefix: PlanetLayer.Raycastable ──
        /// <summary>
        /// 当选中的是某个天体 layer 时，强制其他天体 layer 不参与射线检测。
        /// 否则 Earth 的大半径 collider 会先被射线命中，挡住火星/月球的小半径 layer。
        /// </summary>
        public static bool PlanetLayer_Raycastable_Prefix(PlanetLayer __instance, ref bool __result)
        {
            var selected = CurrentCelestialBodyLayer;
            if (selected == null) return true;

            if (IsCelestialBodyLayer(__instance) && __instance != selected)
            {
                __result = false;
                return false;
            }

            return true;
        }

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

                            Messages.Message(string.Format("已切换至天体视图：{0}",
                                capturedLayer.Def.label ?? capturedLayer.Def.defName),
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
