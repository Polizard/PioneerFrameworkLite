using System.Collections.Generic;
using System.Linq;
using System.Text;
using PioneerFramework.GroundAssembly;
using PioneerFramework.Launch;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **机制在册自检**（2026-10-16）：把"反射清理 / 行为迁移"这批改动的**可机判部分**钉进一键验收——
    /// 判据 = "我们的 def/组件/图层替换在**运行时确实生效**"，而不是"代码写了"。
    ///
    /// 覆盖（对应交接件 §5-9 各批"待进游戏验"里可机判的那半）：
    ///   · **A-14 天空**：Core 的 `Sun`/`Stars` 两条 `GlobalWorldDrawLayerDef` 的 layerType 已换成我们的子类；
    ///     **并且**世界里的层实例确实是我们的子类（实例级证据——旧档要重载存档才换得掉）；
    ///     我们补画的天空层与 **A-2** 的世界图叠加层两条 def 在册且指向我们的类；
    ///   · **A-3 地表层**：原版 `Surface` 与本模 `Moon` 的 layerType 已换 `PF_CelestialSurfaceLayer`；
    ///   · **A-1**：每张已载入地图都自动拿到 `MapComponent_LaunchBlastFX` + `MapComponent_RocketBoosterReturnScheduler`
    ///     （旧注入补丁删掉之后仍然在 ⇒ 自动实例化这条机制真的生效）；**A-8**：每张图都有浮雕置脏 watcher；
    ///   · **A-5/A-15/A-7/A-6**：4 个火箭 def 的 comps / `blueprintClass`、2 个 skyfaller def 的 `thingClass`、
    ///     发射台与火箭的 `placeWorkers` 都指向我们的实现；
    ///   · **A-13a**：相机跟随驱动器对象在册，且 `Update` 真被引擎泵（帧计数 > 0）。
    ///
    /// 全部**只读**（Def 表 + 已载入地图的组件表），不造景、不改状态；可单独点，也被
    /// 「一键验收」（`PerfAcceptanceRunner` 即时电池段）与「一键测试（全覆盖）」调用。
    /// </summary>
    public static class RefactorMechanismsSelfTest
    {
        private static int passed;
        private static int failed;
        private static readonly List<string> failures = new();
        private static readonly StringBuilder log = new();

        public static int LastPassed => passed;
        public static int LastFailed => failed;
        public static IReadOnlyList<string> LastFailures => failures;

        private static readonly string[] RocketDefNames =
        [
            "PF_Building_Rocket_Basic",
            "PF_Building_Rocket_Recoverable_Basic",
            "PF_Building_Rocket_CVOne",
            "PF_Building_Rocket_CVThree",
        ];

        public static void Run()
        {
            passed = 0;
            failed = 0;
            failures.Clear();
            log.Clear();
            log.AppendLine("[机制自检] ═══ 机制在册自检（反射清理 / 行为迁移批）═══");

            SkyAndLayerChecks();
            WorldLayerInstanceChecks();
            MapComponentChecks();
            RocketChainChecks();
            CameraDriverChecks();

            log.AppendLine(System.Globalization.CultureInfo.CurrentCulture,
                $"[机制自检] ═══ 结果：通过 {passed} 项，失败 {failed} 项 ═══");
            if (failed == 0)
                Log.Message(log.ToString());
            else
            {
                Log.Error(log.ToString());
                foreach (string f in failures) Log.Error("[机制自检] 失败：" + f);
            }
        }

        // ───────── A-14 / A-2 / A-3：图层替换 ─────────

        private static void SkyAndLayerChecks()
        {
            log.AppendLine("── 图层替换（A-14 天空 / A-2 叠加 / A-3 地表）──");

            CheckLayerDef("Sun", typeof(PF_GlobalDrawLayer_Sun), "A-14 原版太阳层 → 我们的子类");
            CheckLayerDef("Stars", typeof(PF_GlobalDrawLayer_Stars), "A-14 原版星空层 → 我们的子类");
            CheckLayerDef("PF_SkyBillboards", typeof(PF_WorldDrawLayer_SkyBillboards), "A-14 补画天空层在册");
            CheckLayerDef("PF_SpaceOverlay", typeof(PF_WorldDrawLayer_SpaceOverlay), "A-2 世界图叠加层在册");

            // 2026-11-01：类重组后 Def 已指向 `PioneerFramework.Celestial.CelestialSurfaceLayer`
            //（`PF_CelestialSurfaceLayer` 退为基类）⇒ 断言跟着权威 Def 走，否则一条过时断言会把一键验收拖红。
            var surface = DefDatabase<PlanetLayerDef>.GetNamedSilentFail("Surface");
            Check(surface is { layerType: not null } && surface.layerType == typeof(PioneerFramework.Celestial.CelestialSurfaceLayer),
                "A-3 原版 Surface 层 type 已换",
                surface == null ? "def 缺失" : "实际 " + (surface.layerType?.Name ?? "null"));

            var moon = DefDatabase<PlanetLayerDef>.GetNamedSilentFail("Moon");
            Check(moon is { layerType: not null } && moon.layerType == typeof(PioneerFramework.Celestial.CelestialSurfaceLayer),
                "A-3 月球层 type 已换",
                moon == null ? "def 缺失" : "实际 " + (moon.layerType?.Name ?? "null"));
        }

        private static void CheckLayerDef(string defName, System.Type expected, string label)
        {
            var def = DefDatabase<GlobalWorldDrawLayerDef>.GetNamedSilentFail(defName);
            Check(def != null && def.worldDrawLayer == expected, label,
                def == null ? "def 缺失" : "实际 " + (def.worldDrawLayer?.Name ?? "null"));
        }

        // ───────── A-14 运行时实例：世界里真的装上了我们的子类 ─────────

        /// <summary>
        /// def 换了 ≠ 世界里的层实例换了：层实例在**开世界时**建（`WorldGrid.InitializeGlobalLayers`
        /// 读 def 字段）⇒ 旧档（换层前建的层）要**重载存档**才换得掉。这里直接读运行时实例列表。
        /// </summary>
        private static void WorldLayerInstanceChecks()
        {
            log.AppendLine("── 世界层实例（A-14：实例级证据）──");

            var world = Find.World;
            var layers = world?.grid?.GlobalLayers;
            if (layers == null || layers.Count == 0)
            {
                Check(true, "世界层实例检查（无世界/无全局层 ⇒ 跳过）",
                    world == null ? "Find.World 为空" : "GlobalLayers 为空");
                return;
            }

            int ours = 0, vanilla = 0;
            foreach (WorldDrawLayerBase layer in layers)
            {
                System.Type t = layer.GetType();
                if (t == typeof(PF_GlobalDrawLayer_Sun) || t == typeof(PF_GlobalDrawLayer_Stars))
                    ours++;
                else if (t == typeof(GlobalDrawLayer_Sun) || t == typeof(GlobalDrawLayer_Stars))
                    vanilla++;
            }

            Check(vanilla == 0, $"A-14：世界里的原版太阳/星空层实例已是我们的子类（本世界共 {layers.Count} 层）",
                vanilla == 0 ? $"我们的子类 {ours} 条" : $"原版实例仍有 {vanilla} 条 ⇒ 重载存档后生效");
        }

        // ───────── A-1 / A-8：每张地图的组件 ─────────

        private static void MapComponentChecks()
        {
            log.AppendLine("── 地图组件（A-1 自动实例化 / A-8 浮雕 watcher）──");

            int maps = 0, missInject = 0, missWatcher = 0;
            foreach (Map m in Find.Maps)
            {
                maps++;
                if (m.GetComponent<MapComponent_LaunchBlastFX>() == null
                    || m.GetComponent<MapComponent_RocketBoosterReturnScheduler>() == null)
                {
                    missInject++;
                }

                if (m.GetComponent<MapComponent_ReliefDirtyWatcher>() == null)
                    missWatcher++;
            }

            if (maps == 0)
            {
                Check(true, "地图组件检查（当前无已载入地图 ⇒ 跳过）", "Find.Maps 为空");
                return;
            }

            Check(missInject == 0, $"A-1：{maps} 张已载入地图都有爆炸特效 / 回收调度两个组件",
                missInject == 0 ? "全部在册（自动实例化生效）" : $"{missInject} 张缺失");
            Check(missWatcher == 0, $"A-8：{maps} 张已载入地图都有浮雕置脏 watcher",
                missWatcher == 0 ? "全部在册（MapEvents 订阅生效）" : $"{missWatcher} 张缺失");
        }

        // ───────── A-5 / A-15 / A-7 / A-6：火箭链 def ─────────

        private static void RocketChainChecks()
        {
            log.AppendLine("── 火箭链 def（A-5 发射压制 / A-15 建造链 / A-7 切线 / A-6 worker）──");

            foreach (string name in RocketDefNames)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                if (def == null)
                {
                    Check(false, name + " 存在", "def 缺失");
                    continue;
                }

                bool blueprint = def.building != null && def.building.blueprintClass == typeof(PF_Blueprint_Rocket);
                bool notify = def.comps != null && def.comps.Any(c => c is CompProperties_RocketCompletionNotify);
                bool launch = def.comps != null && def.comps.Any(c => c is CompProperties_Launchable_PF);
                // A-6：只有 CV 两条带我们的放置 worker；Basic 两条**本来就没有 placeWorkers**
                //（取证 2026-10-16：1.6 冻结树 / 改造前 be3a838 / 现版**三处一致** ⇒ 非本轮差异，按现状钉住；
                //  若将来裁定"Basic 也要受总装台约束"，那是**另立一件**：加 def 字段 + 改这里的期望）。
                bool hasOurWorker = def.placeWorkers != null
                    && def.placeWorkers.Any(t => t == typeof(PF_PlaceWorker_NeedsFuelingPort));
                bool expectWorker = name.Contains("CV");

                Check(blueprint, $"{name}｜A-15 蓝图类",
                    blueprint ? "PF_Blueprint_Rocket" : "实际 " + (def.building?.blueprintClass?.Name ?? "null"));
                Check(notify, $"{name}｜A-15 完工通知 comp", notify ? "在册" : "缺失");
                Check(launch, $"{name}｜A-5 发射 Gizmo 子类 comp", launch ? "在册" : "缺失");
                Check(hasOurWorker == expectWorker, $"{name}｜A-6 放置 worker",
                    expectWorker
                        ? (hasOurWorker ? "PF_PlaceWorker_NeedsFuelingPort" : "缺失")
                        : (hasOurWorker ? "多出我们的 worker（与 def 表不符）" : "无 placeWorkers（与 1.6/改造前一致）"));
            }

            var pad = DefDatabase<ThingDef>.GetNamedSilentFail("PF_Building_Launchpad_Small");
            bool padWorker = pad?.placeWorkers != null
                && pad.placeWorkers.Any(t => t == typeof(PF_PlaceWorker_FuelingPort));
            Check(pad != null && padWorker, "发射台｜A-6 燃料口 worker",
                pad == null ? "def 缺失" : padWorker ? "PF_PlaceWorker_FuelingPort" : "缺失");

            CheckSkyfaller("PF_Skyfaller_Incoming_Basic", typeof(PF_Skyfaller_Incoming));
            CheckSkyfaller("PF_Skyfaller_Leaving_Basic", typeof(PF_Skyfaller_Leaving));
        }

        private static void CheckSkyfaller(string defName, System.Type expected)
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            Check(def != null && def.thingClass == expected, $"A-7 {defName}｜切线朝向子类",
                def == null ? "def 缺失" : "实际 " + (def.thingClass?.Name ?? "null"));
        }

        // ───────── A-13a/A-13b：世界相机服务 ─────────

        private static void CameraDriverChecks()
        {
            log.AppendLine("── 世界相机服务（A-13a 跟随/farClip + A-13b 背景接管 的常驻件）──");
            Check(PF_WorldCameraService.Instance != null, "服务对象在册",
                PF_WorldCameraService.Instance != null ? "在册" : "null");
            Check(PF_WorldCameraService.UpdateFrames > 0, "服务 Update 真被引擎泵（帧计数 > 0）",
                $"帧计数 {PF_WorldCameraService.UpdateFrames}");
            // A-13b 的关键前提：背景接管必须写在世界层绘制之前（退化 = 默认序 ⇒ 天空盒晚一帧"游泳"）
            Check(PF_WorldCameraService.ExecutionOrderRegistered, "执行序 −50 注册成功（背景接管早于世界绘制）",
                PF_WorldCameraService.ExecutionOrderRegistered ? "已注册 −50" : "未注册 ⇒ 退化为默认序（背景相机会晚一帧）");
        }

        // ───────── 工具 ─────────

        private static void Check(bool ok, string label, string detail)
        {
            if (ok)
            {
                passed++;
                log.AppendLine("  ✓ " + label + "｜" + detail);
            }
            else
            {
                failed++;
                string line = label + "｜" + detail;
                failures.Add(line);
                log.AppendLine("  ✗ " + line);
            }
        }
    }
}
