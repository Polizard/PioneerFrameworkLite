using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using UnityEngine;
using Verse;
using RimWorld.Planet;
using RimWorld;
using HarmonyLib;

namespace PioneerFramework
{
    [StaticConstructorOnStartup]
    public static class SpaceGroupVisualManager
    {
        public static bool EnableVisualization = true;

        // ── 组件引用缓存（[M1]/[O4]：每帧路径不许裸 `GetComponent`——那是组件表线性扫描）──
        // 与 `GameComponentRef` 的既有用法一致：按 Game 缓存、换局/读档自动失效、未命中不写缓存。
        private static readonly GameComponentRef<SensorManager> sensorMgrRef = new();

        /// <summary>现有 RimWorld 世界对应的天体（地球）ID</summary>
        private const int EARTH_BODY_ID = 1;

        private static int selectedGroupId = -1;
        private static bool isFollowing ;
        private static float followLerpSpeed = 5f;
        private static float lastFollowTime;

        // 视觉自转不再累积存储，改用 `ViewFrame.ToViewUnity` 即时计算（含被查看天体的自转周期）

        // 材质与资源
        private static Material iconMaterial;
        private static Material orbitMarkerMaterial;
        private static readonly MaterialPropertyBlock orbitMarkerPropertyBlock = new();

        // 常量（单位：Unity 世界单位，已从米制转换）
        private const float BaseIconSize = 20f;
        private const float SurfaceIconSize = 28f; // 地表图标稍大，更容易看到
        private const float IconScaleReferenceAltitude = 850f;
        /// <summary>
        /// 图标随相机高度的缩放**上限**（2026-10-11 用户裁定：「icon **只有最大尺寸、没有最小尺寸**」）。
        /// 原来 `Clamp(sqrt(altitude/850), 1, 3)` —— 那个下限 1 让"贴近看"时图标仍是 28 单位：
        /// 在最近高度（125 单位）它占屏高约 65%，把视野撑满。**去掉下限**后拉近时图标跟着变小；
        /// 拉远时仍由此值封顶（世界尺寸不无限涨）。屏幕观感：∝ 1/√高度（拉远会变小 —— 用户已确认
        /// 「点不中」不是问题，所以不设屏幕最小尺寸）。
        /// </summary>
        private const float IconScaleMaxMultiplier = 3f;
        private const float OrbitMarkerSpacing = 14f;        // 轨道标记点间距（Unity单位）
        private const int MinOrbitSamples = 36;
        private const int MaxOrbitSamples = 180;
        private const float OrbitMarkerSize = 1.2f;
        private const float OrbitMarkerAlphaFar = 0.3f;

        // 轨道线色彩常量
        private static readonly Color OrbitColorCurrentOrbit = new(0.25f, 0.9f, 1f, 0.6f);       // 当前轨道：青色
        private static readonly Color OrbitColorPostManeuver = new(1f, 0.7f, 0.1f, 0.6f);       // 机动后轨道：橙黄
        private static readonly Color OrbitColorTransfer = new(0f, 1f, 0.5f, 0.6f);             // 转移轨道：亮绿
        private static readonly Color OrbitColorTarget = new(1f, 0.3f, 0.3f, 0.7f);             // 目标轨道：红色
        private static readonly Color MarkerColorPeriapsis = new(0f, 1f, 0f, 0.9f);             // Pe：绿色
        private static readonly Color MarkerColorApoapsis = new(1f, 0.2f, 0.2f, 0.9f);          // Ap：红色
        private static readonly Color MarkerColorAn = new(1f, 1f, 0f, 0.8f);                    // AN：黄色
        private static readonly Color MarkerColorDn = new(0.7f, 0.3f, 1f, 0.8f);                // DN：紫色

        // GUI 面板
        private const float PanelWidth = 320f;
        private const float PanelHeight = 500f;          // 基础高度（无 burn 区）
        private const float BurnSectionHeight = 112f;    // 机动执行区附加高度
        private const float RendezvousSectionHeight = 112f; // 交会引导区附加高度（旧范式显示：3 行）
        private const float RendezvousSectionHeightNewLaw = 133f; // 新律显示多一行（相位/距离/相对速度）
        private const float ApproachSectionHeight = 112f; // 近距离接近区附加高度

        // 面板配色（与机动队列窗口一致）
        private static readonly Color CyanColor = new(0.25f, 0.9f, 1f, 0.9f);
        private static readonly Color WarnColor = new(1f, 0.42f, 0.3f, 0.95f);
        private static readonly Color OkColor = new(0.35f, 1f, 0.55f, 0.95f);
        private static readonly Color DimColor = new(0.6f, 0.66f, 0.72f, 1f);
        /// <summary>普通按钮的统一灰色（未特别着色按钮）。</summary>
        private static readonly Color GrayButton = new(0.46f, 0.49f, 0.53f, 1f);

        /// <summary>赤道经度标记 90°E 的颜色（0° 那枚复用 `CyanColor`）。</summary>
        private static readonly Color EquatorLon90Color = new(1f, 0.6f, 0.15f, 0.9f);

        /// <summary>预创建的球体网格，用于天体球体渲染（赤道经度标记也用它）。</summary>
        private static Mesh sphereMesh;

        public static int GetSelectedGroupId() => selectedGroupId;
        static SpaceGroupVisualManager()
        {
            // 加载材质（确保图标纹理存在，若不存在则使用默认）
            iconMaterial = MaterialPool.MatFrom("UI/Icons/SatelliteIcon", ShaderDatabase.WorldOverlayCutout, Color.cyan);
            if (iconMaterial == null || iconMaterial.mainTexture == null)
                iconMaterial = MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.WorldOverlayCutout, Color.cyan);

            orbitMarkerMaterial = MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.WorldOverlayCutout, Color.white);
            orbitMarkerMaterial.renderQueue = 3575;

            // 创建球体网格（用于天体球体渲染）
            var sphereGO = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphereMesh = UnityEngine.Object.Instantiate(sphereGO.GetComponent<MeshFilter>().sharedMesh);
            UnityEngine.Object.DestroyImmediate(sphereGO);

            // 应用 Harmony 补丁。
            // B2：整段包 try/catch —— 补丁失败（如 RW 升级后 IL 改名）原本会让**静态构造抛异常**，
            // 后果有两层且都不是"整个 mod 加载失败"（框架 `StaticConstructorOnStartupUtility.CallAll`
            // 对每个类型独立 try/catch 只 Log.Error）：① 其后未执行的补丁**静默缺失**；
            // ② 类型被 CLR 标记为不可用，之后任何静态访问都抛 TypeInitializationException
            //    （发射等宿主路径会碰本类的静态成员 → **连带炸**）。
            // 现在改为逐段可失败：报错并继续，让 mod 主功能与发射流程不受牵连。
            var harmony = new Harmony("PioneerFramework.SpaceGroupVisualManager");
            try
            {
            // 2026-10-15 反射清理 A-2：原先这里是 `WorldRenderer.DrawWorldLayers` 的 Postfix（画卫星图标/轨道）
            // ——已改为 **def 驱动的全局绘制层** `PF_WorldDrawLayer_SpaceOverlay`
            //（`Defs/WorldDrawLayerDefs/PF_SpaceOverlay.xml`）；绘制主体仍是本类的
            // `RenderSpaceOverlayFrame()`（由该层每帧调用，调用点与旧 postfix 逐字相同）。
            // 补丁2：世界地图 GUI 事件（信息面板、点击选中）
            // 2026-10-16 反射清理 A-4：原先这里还有**同一条方法上的第二个安装点**——
            // `WorldInterfaceOnGUI` 的 Prefix（画"显示/隐藏卫星视图"按钮）。两处补丁本就是**同一个方法**，
            // 现已合并进 Postfix 的绘制体（`WorldInterfaceOnGUIBody` → `DrawToggleButton`）：
            // 安装点 −1、按钮位置/文案/时机**零变化**（只是画序从"vanilla 内容之前"挪到"之后"，更不会被盖）。
            harmony.Patch(AccessTools.Method(typeof(WorldInterface), "WorldInterfaceOnGUI"),
                postfix: new HarmonyMethod(typeof(SpaceGroupVisualManager), nameof(WorldInterface_OnGUI_Postfix)));
            // 2026-10-15 反射清理 A-13：原先这里还有 `WorldCameraDriver.Update` 的 Postfix（跟随 + farClip）
            // 与 `WorldCameraDriver.Awake` 的 Postfix（farClip）——已改为**常驻 MonoBehaviour**
            //（`PF_WorldCameraService`，见下）；MaxAltitude 的两处 transpiler 保留（改的是 vanilla 字面量，无可替代）。
            // 2026-10-16 反射清理 A-13b：原先另有两处属性式补丁（`WorldCameraDriver.ApplyMapPositionToGameObject`
            // 与 `.Update`，含图编队的背景相机接管 + near clip 还原）也已收进同一个常驻组件 ⇒ 属性式补丁归零。
            // 补丁3b：修改世界摄像机最大高度（1100→50000），让玩家能拉远看高轨道
            harmony.Patch(AccessTools.Method(typeof(WorldCameraDriver), "WorldCameraDriverOnGUI"),
                transpiler: new HarmonyMethod(typeof(SpaceGroupVisualManager), nameof(Transpiler_MaxAltitude_OnGUI)));
            harmony.Patch(AccessTools.Method(typeof(WorldCameraDriver), "Update"),
                transpiler: new HarmonyMethod(typeof(SpaceGroupVisualManager), nameof(Transpiler_MaxAltitude_Update)));
            }
            catch (System.Exception ex)
            {
                Log.Error("[SpaceGroupVisualManager] Harmony 补丁应用失败（世界视图轨道/相机增强可能部分失效；"
                    + "主功能与发射流程不受影响）：" + ex);
            }

            // 2026-10-15 反射清理 A-13 / A-13b：相机相关四件事（farClip / 跟随 / 背景接管 / near clip 还原）
            // 的常驻服务，替代原四处 Postfix。**执行序必须 −50**：晚于相机驱动（−103）、
            // 早于世界层绘制（在 Root_Play 的 Update 里）——否则背景相机会晚一帧、天空盒"游泳"。
            var followGo = new GameObject("PF_WorldCameraService");
            UnityEngine.Object.DontDestroyOnLoad(followGo);
            PF_WorldCameraService.Instance = followGo.AddComponent<PF_WorldCameraService>();
            try
            {
                var runtime = Waylith.UnityCompat.UnityRuntime.Current;
                var unityWorld = runtime?.World;
                if (unityWorld != null)
                {
                    unityWorld.RegisterExecutionOrder(typeof(PF_WorldCameraService), -50);
                    PF_WorldCameraService.ExecutionOrderRegistered = true;
                }
                else
                {
                    Log.Error("[SpaceGroupVisualManager] 拿不到 Waylith UnityWorld（UnityRuntime.Current 为空）⇒ "
                        + "世界相机服务退化为默认执行序（世界层绘制之后）：背景相机接管会晚一帧、天空盒可能“游泳”。");
                }
            }
            catch (System.Exception ex)
            {
                Log.Error("[SpaceGroupVisualManager] 世界相机服务未注册执行序 −50 ⇒ 退化为默认序（世界层绘制之后）："
                    + "背景相机接管会晚一帧、天空盒可能“游泳”：" + ex);
            }
        }

        // ── Harmony 补丁方法 ──

        // ★ 每帧耗时探针（2026-09-20）：整步墙钟测的是**物理步**（实测 0.2 ms/步 ⇒ 不是"卡"的原因 ✗），
        //   而"一直卡"的代价在**每帧**的渲染/UI 这一侧 —— 这一侧此前**从未量过**。
        //   口径与步钟相同：累加 + 最慢 + 每 1200 帧（≈20 s）在开发者模式下打一行并清零。
        private static long _frameClocks, _frameTicksTotal, _frameTicksMax;
        private const long FrameAutoLogEvery = 1200;

        /// <summary>
        /// **draw call 计数**（2026-09-30，批7 旁路·③ 的仪器；**2026-10-01 补全为"尖峰归因"版**）。
        ///
        /// ## 上一版的两个错（读码核过 ⇒ 连我都被它骗过一次，故留档）
        /// ① 计数器**只在 `DrawOrbitPath` 里加**，而全文件另有 5 处 `Graphics.DrawMesh`（天体球面 / 群体图标 /
        ///    发射点 / 机动标记 / 预览标记）**完全没被数** ⇒ 它对"非轨道绘制"是**瞎的**；
        /// ② 打印标签写 `本帧 {N}`，但 `N` 是**窗口（1200 帧）累计**（清零也只在窗口末）⇒
        ///    实际每帧 ≈ N/1200（实测 14–60 次/帧，与"轨道线只画被选中的那一组"一致）；
        ///    而 `峰值` 字段**全文件从未赋值** ⇒ 恒印 0（死字段）。
        ///
        /// ## 这一版做什么
        /// · **按类别计数**（球 / 图标 / 轨道点 / 标记 / 发射点）—— 每帧一份 + 窗口一份；
        /// · **真记"每帧最大"**（修掉死字段）；
        /// · **尖峰归因**：本帧墙钟超 `SpikeThresholdUs` 时**立刻**打一行
        ///   （该帧类别计数 + GC 三代增量 + 活跃组数），每窗口至多 `SpikeMaxLinesPerWindow` 行；
        /// · 标签改成真话：`窗口累计 N（每帧均 …，最大 …）`。
        ///
        /// ⚠ 现状（读码核过）：轨道线**只画被选中的那一组**（`selectedGroupId`）＋机动预览段
        /// ⇒ 每帧 ≈ 1 条轨道 + 若干标记，与组数 N **无关**。所以"多轨道同屏"是**功能决策**，不是现存性能问题。
        /// </summary>
        private static long _frameDrawCalls, _frameDrawCallsMax;   // 窗口累计 + **每帧最大**（后者此前是死字段）
        private static long _frameOrbitPaths;                       // 窗口累计：画了几条轨道线
        private static long _frameOrbitPathsThisFrame;              // 本帧：画了几条轨道线

        private const int DcSphere = 0, DcIcon = 1, DcOrbitPoint = 2, DcMarker = 3, DcLaunch = 4, DcCats = 5;
        private static readonly long[] _dcWindow = new long[DcCats];   // 窗口累计（按类别）
        private static readonly long[] _dcFrame = new long[DcCats];    // 本帧（按类别）
        private static long _dcFrameTotal;                             // 本帧合计
        private static long _gc0, _gc1, _gc2;                          // 本帧 GC 增量（三代）
        private static int _gc0Last, _gc1Last, _gc2Last;
        private static int _spikeLinesThisWindow;
        private const double SpikeThresholdUs = 2000.0;                // 本帧墙钟超它 ⇒ 打一行归因
        private const int SpikeMaxLinesPerWindow = 8;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static void CountDraw(int cat)
        {
            _dcWindow[cat]++;
            _dcFrame[cat]++;
            _dcFrameTotal++;
            _frameDrawCalls++;
        }

        // 2026-10-16 合批后 draw 数 ≠ 可见点数（轨道点/点阵都并成一批）⇒ 点数单独一列，读数不失真。
        private static readonly long[] _dcQuadWindow = new long[DcCats];
        private static readonly long[] _dcQuadFrame = new long[DcCats];

        private static void CountQuads(int cat, int n)
        {
            _dcQuadWindow[cat] += n;
            _dcQuadFrame[cat] += n;
        }


        /// <summary>
        /// 世界地图叠加的**逐帧入口**（2026-10-15 反射清理 A-2：原名 `WorldRenderer_DrawWorldLayers_Postfix`，
        /// 现由 def 驱动的 `PF_WorldDrawLayer_SpaceOverlay.Render()` 调用——调用点与旧 postfix 同一处）。
        /// 含帧耗时/GC/draw-call 探针（尖峰归因），绘制主体见 <see cref="DrawWorldLayersBody"/>。
        /// </summary>
        public static void RenderSpaceOverlayFrame()
        {
            long frameClock = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                DrawWorldLayersBody();
            }
            finally
            {
                long dt = System.Diagnostics.Stopwatch.GetTimestamp() - frameClock;
                _frameClocks++;
                _frameTicksTotal += dt;
                if (dt > _frameTicksMax) _frameTicksMax = dt;

                double toUsNow = 1e6 / System.Diagnostics.Stopwatch.Frequency;   // µs
                double frameUs = dt * toUsNow;

                // GC 三代**本帧增量**（尖峰归因的关键一项：窗口级读数分不清"这一帧是不是被 GC 打断的"）
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                _gc0 = g0 - _gc0Last; _gc1 = g1 - _gc1Last; _gc2 = g2 - _gc2Last;
                _gc0Last = g0; _gc1Last = g1; _gc2Last = g2;

                // **每帧最大 draw call**（修掉原来那个恒 0 的死字段）
                if (_dcFrameTotal > _frameDrawCallsMax) _frameDrawCallsMax = _dcFrameTotal;

                // **尖峰归因**（2026-10-01）：本帧超阈值 ⇒ 立刻打一行，把"这一帧在画什么 + 有没有 GC"钉下来。
                //   为什么要它：窗口级的 `最慢=` 只能说明"有这么一帧"，而**归因**要靠该帧自己的计数
                //   （先前的读数就吃过这个亏：点数与帧耗时对不上，因为打的是窗口累计而不是尖峰那一帧）。
                if (Prefs.DevMode && frameUs > SpikeThresholdUs && _spikeLinesThisWindow < SpikeMaxLinesPerWindow)
                {
                    _spikeLinesThisWindow++;
                    Log.Message($"[帧耗时·尖峰] 本帧={frameUs:F0}µs｜draw call={_dcFrameTotal}（球{_dcFrame[DcSphere]}/图标{_dcFrame[DcIcon]}/轨道点{_dcFrame[DcOrbitPoint]}/标记{_dcFrame[DcMarker]}/发射点{_dcFrame[DcLaunch]}）｜点数：轨道{_dcQuadFrame[DcOrbitPoint]}/标记{_dcQuadFrame[DcMarker]}｜GC 本帧={_gc0}/{_gc1}/{_gc2}｜轨道线={_frameOrbitPathsThisFrame} 条");
                }

                _dcFrameTotal = 0;
                for (int i = 0; i < DcCats; i++) { _dcFrame[i] = 0; _dcQuadFrame[i] = 0; }
                _frameOrbitPathsThisFrame = 0;

                if (_frameClocks >= FrameAutoLogEvery)
                {
                    if (Prefs.DevMode)
                    {
                        // ⚠ 2026-09-23 口径改 **µs**：原来 F2 的 ms 在本机量级下恒印 `0.00`（GUI 段实测平均 ~0.003 ms）
                        //   ⇒ "探针看不懂的读数"；与 `[步耗时]` 统一到 µs（那边是 F1 µs）。
                        // ⚠ 2026-10-01 修标签：`{3}` 是**窗口累计**不是"本帧"（老标签把 69006 说成每帧 ⇒ 我读错过一次），
                        //   真正的"每帧最大"是 `{4}`（老代码里它恒 0）。分类别计数让"谁在画"一眼可判。
                        Log.Message(string.Format(
                            "[帧耗时] 帧数={0} 平均={1:F1}µs/帧 最慢={2:F1}µs｜draw call **窗口累计** {3}（每帧均 {11:F1}，**每帧最大 {4}**）"
                            + "｜分类（draw｜点数）：球 {5}/图标 {6}/轨道点 {7}｜{12}/标记 {8}｜{13}/发射点 {9}｜轨道线 {10} 条",
                            _frameClocks, _frameTicksTotal * toUsNow / _frameClocks, _frameTicksMax * toUsNow,
                            _frameDrawCalls, _frameDrawCallsMax,
                            _dcWindow[DcSphere], _dcWindow[DcIcon], _dcWindow[DcOrbitPoint], _dcWindow[DcMarker], _dcWindow[DcLaunch],
                            _frameOrbitPaths, (double)_frameDrawCalls / _frameClocks,
                            _dcQuadWindow[DcOrbitPoint], _dcQuadWindow[DcMarker]));
                    }
                    _frameClocks = 0; _frameTicksTotal = 0; _frameTicksMax = 0;
                    _frameDrawCalls = 0; _frameDrawCallsMax = 0; _frameOrbitPaths = 0;
                    for (int i = 0; i < DcCats; i++) { _dcWindow[i] = 0; _dcQuadWindow[i] = 0; }
                    _spikeLinesThisWindow = 0;
                }
            }
        }

        private static void DrawWorldLayersBody()
        {
            if (!WorldRendererUtility.WorldSelected) return;
            if (!EnableVisualization) return;

            var camera = Find.WorldCamera;
            if (camera == null) return;

            // 发射点标记（红色正方体，固连系）：不依赖 group 存在，未发射也可作天地对照锚点
            DrawEquatorLonMarkers(camera);

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;

            var groups = spaceGame.GetAllGroups(); // 第 11 条：直接遍历字典视图（此前每帧 ToList 复制整表）

            // 收集地球系统（地球 + 所有卫星）的天体 ID 集合，渲染该系统中所有群组
            // ⚠ P52（2026-09-27）：**这个集合每帧都在 `OnGUI` 路径上新建**（一个 `HashSet` + 每次 `Add`
            // 的桶扩容）⇒ 每帧 GC。依据与 P32 的 `cachedAllBodies` **同一条**：天体 Def 静态
            // ⇒ 建一次即可。生命周期按"管理器换局"失效（不按帧）。
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (earthSystemBodyIds == null || !ReferenceEquals(earthSystemBodyIdsMgr, celestialMgr))
            {
                earthSystemBodyIdsMgr = celestialMgr;
                earthSystemBodyIds = new HashSet<int> { EARTH_BODY_ID };
                var earthDef = celestialMgr?.GetBodyById(EARTH_BODY_ID);
                if (earthDef != null)
                {
                    foreach (var moon in earthDef.moonBodies)
                        earthSystemBodyIds.Add(moon.bodyId);
                }
            }

            // [V3] 认知层过滤（S3，2026-10-02）：只画"玩家真的看得见"的棋子。
            // dev 真值开关（`SensorManager.DebugShowGroundTruth`）开启 ⇒ 绕过认知层，直接看真实棋子。
            // 认知层缺位（组件没加载）⇒ 放行（= 旧行为，别让缺组件变成"世界空了"）。
            var sensor = sensorMgrRef.Get();
            int playerFid = Faction.OfPlayer?.loadID ?? -1;
            foreach (var group in groups)
                if (group.IsActive && !group.IsSurfaceAnchor && earthSystemBodyIds.Contains(group.CelestialBodyId))
                {
                    if (sensor != null && !SensorManager.DebugShowGroundTruth && !sensor.PlayerCanSee(group)) continue;

                    // [V-2] 影子接触：判据已断、记忆未过期 ⇒ **画在最后已知位置**（不是真值位置），
                    // 配 σ 圈表达不确定度（口径见 ContactCore.CoastedPosition 注释：画面不外推）。
                    Vector3? posOverride = null;
                    if (sensor != null && !SensorManager.DebugShowGroundTruth && playerFid >= 0
                        && sensor.ContactPhaseOf(playerFid, group.GroupId) == TrackPhase.Shadow
                        && sensor.TryGetContact(playerFid, group.GroupId, out Contact memContact))
                    {
                        posOverride = ViewFrame.ToViewUnity(memContact.position, PhysicsTickNow());
                    }
                    DrawGroupIcon(group, camera, posOverride);
                }
            IconBatchFlush();   // 图标批收口（2026-10-16 合批；空批无操作）

            // ── [V-2] 认知层图层：影子 σ 圈 / 假接触 / 波束锥（同一准入：dev 真值开关下跳过）──
            DrawCognitionOverlay(camera, spaceGame, sensor);

            // ── 天体渲染（2026-09-23：**除"被查看的那颗"以外**的全部，含地球与太阳）──
            //   被查看的那颗由 RimWorld 的 PlanetLayer 画（球心 = 视图系原点）；其余天体按**系统系位置**摆
            //   ⇒ 切到月球时，地球与绕地的船都在 −月球偏移 处（1,570 Unity 单位外，看得见但很远）。
            if (celestialMgr != null)
            {
                double bodyTick = PhysicsTickNow();
                foreach (var body in celestialMgr.GetAllBodies())
                {
                    if (body.bodyId == ViewFrame.ViewedBodyId) continue;   // 自己被 RW 画
                    if (!body.IsGravitySource) continue;                    // 无质量装饰天体不画
                    Vector3 viewPos = ViewFrame.ToViewUnity(
                        ViewFrame.BodySystemPosition(body.bodyId, bodyTick), bodyTick);
                    if (!IsViewPosVisible(viewPos, 1f)) continue;

                    // 物理半径 → Unity 缩放（⚠ 球网格直径 1 ⇒ 见 ViewFrame.RadiusToSphereScale；原来按半径缩放
                    //   画出来只有一半，切到月球视图时"地球半径/绕地轨道看着不对"就是这么来的）
                    float scale = ViewFrame.RadiusToSphereScale((float)body.radius, sphereMesh);
                    if (scale < 2f) scale = 2f;   // 远距离最小可见尺寸（缩放值口径）
                    Matrix4x4 sphereMatrix = Matrix4x4.TRS(viewPos, Quaternion.identity, Vector3.one * scale);
                    CountDraw(DcSphere);
                    Graphics.DrawMesh(sphereMesh, sphereMatrix, orbitMarkerMaterial, WorldCameraManager.WorldLayer);
                }
            }

            if (selectedGroupId != -1)
            {
                var selected = spaceGame.GetGroupById(selectedGroupId);
                if (selected != null && selected.IsActive)
                {
                    // 画 Pe/Ap/AN/DN 标记
                    DrawOrbitMarkersForGroup(selected, camera);

                    // ── 始终绘制当前轨道路径（非圆轨/圆轨都支持）──
                    if (selected.IsCircularOrbit)
                    {
                        var circPoints = GetCircularOrbitPointsUnity(selected);
                        if (circPoints != null && circPoints.Count > 0)
                            DrawOrbitPath(circPoints, new Color(0.35f, 0.95f, 1f, 0.8f), OrbitMarkerSize, camera);
                    }
                    else
                    {
                        // 非圆轨模式：从 CurrentOrbit 位置/速度生成轨道点
                        // 第 11 条：按「组 id + pos + vel」记忆化——路径是这两者的纯函数，滑行期不变，
                        // 此前每帧都跑 60 点积分 + 两次列表分配。圆轨那条不缓存（采样数随相机高度变）。
                        DVector3 pos = selected.CurrentOrbit.positionSystem.v;
                        DVector3 vel = selected.CurrentOrbit.velocitySystem.v;
                        if (orbitPathCacheGroupId != selected.GroupId
                            || !orbitPathCachePos.Equals(pos) || !orbitPathCacheVel.Equals(vel))
                        {
                            // ⚠ 2026-09-26（D6）：**必须显式给 mu**。原先是 `GetOrbitPoints(pos, vel, 60)`
                            // —— `mu` 省略 ⇒ `ResolveGM` 静默取**默认 GM**（地球）⇒ **绕月编队画的轨道线是按地球 GM 算的**（活缺陷）。
                            // ⚠⚠ 批5.C（2026-09-29，用户报"交会引导中的 Group 看不到轨迹点"）：
                            //   **光给对 mu 还不够** —— `CurrentOrbit` 是**世界系（系统系 = 根行星中心）**，
                            //   而 mu 是**中心天体**的 ⇒ 绕月编队等于"用月球的 GM 去算一条以地心为焦点的轨道"
                            //   ⇒ 算出一条**穿过地球的假轨道**，点全在视野外 ⇒ **什么都看不到**（用户报的就是它）。
                            //   正确做法：**在中心天体的体心系里算轨道，再把点搬回世界系**（渲染用世界系）。
                            double tickNow = TimeScaleManager.GetPhysicsTickSafe();
                            SystemVec3 bodyOff = selected.GetCentralBodyOffset((int)tickNow);
                            SystemVec3 bodyOffV = selected.GetCentralBodyVelocity((int)tickNow);
                            // 系统系 → 天体局部系（`FrameConvert` 是唯一换算）……
                            var dPoints = OrbitMath.GetOrbitPoints(
                                FrameConvert.SystemToBody(pos, bodyOff).v,
                                FrameConvert.SystemToBodyVelocity(vel, bodyOffV).v, 60,
                                spaceGame.GetBodyGM(selected.CelestialBodyId));
                            // ……再搬回系统系（渲染用系统系）
                            orbitPathCachePoints = dPoints != null && dPoints.Count > 0
                                ? dPoints.ConvertAll(p => (Vector3)(FrameConvert.BodyToSystem(new BodyVec3(p), bodyOff) * SpaceConstants.UnitsPerMeter))
                                : null;
                            orbitPathCacheGroupId = selected.GroupId;
                            orbitPathCachePos = pos;
                            orbitPathCacheVel = vel;
                        }
                        if (orbitPathCachePoints != null && orbitPathCachePoints.Count > 0)
                            DrawOrbitPath(orbitPathCachePoints, new Color(0.35f, 0.95f, 1f, 0.8f), OrbitMarkerSize, camera);
                    }

                    // ── 规划器预览段（规划器打开且有数据时叠加）──
                    if (ManeuverPlanner.HasPlanner && ManeuverPlanner.PreviewSegments != null)
                    {
                        var segments = ManeuverPlanner.PreviewSegments;
                        if (segments != null)
                        {
                            for (int si = 0; si < segments.Count; si++)
                            {
                                var seg = segments[si];
                                if (seg.pointsUnity == null || seg.pointsUnity.Count == 0)
                                    continue;

                                DrawOrbitPath(seg.pointsUnity, seg.color, 0.8f, camera);

                                if (seg.nodePositionMeters.HasValue)
                                {
                                    Vector3 nodeUnity = (Vector3)(seg.nodePositionMeters.Value * SpaceConstants.UnitsPerMeter);
                                    Color nodeColor = (si == segments.Count - 2 && seg.isPostManeuver == false)
                                        ? new Color(1f, 0.9f, 0.2f, 0.9f)
                                        : new Color(1f, 0.9f, 0.5f, 0.7f);
                                    DrawManeuverNode(nodeUnity, camera, nodeColor, 2.5f);
                                }
                            }
                        }

                        // ── 预测标记（卫星 + 行星系卫星）──
                        if (ManeuverPlanner.PredictionTimeSec > 1f)
                        {
                            var predPos = ManeuverPlanner.PredictedSatellitePosUnity;
                            if (predPos.HasValue)
                                DrawManeuverNode(predPos.Value, camera, Color.green, 3f);

                            var moonPts = ManeuverPlanner.PredictedMoonPositionsUnity;
                            if (moonPts != null)
                            {
                                foreach (var mp in moonPts)
                                    DrawManeuverNode(mp, camera, new Color(0.5f, 0.7f, 1f, 0.8f), 2.5f);
                            }
                        }
                    }

                    // ── 已确认机动队列（规划器关闭时）──
                    if (!ManeuverPlanner.HasPlanner && selected.plannedManeuvers.Count > 0)
                    {
                        float toUnity = (float)SpaceConstants.UnitsPerMeter;
                        for (int i = 0; i < selected.plannedManeuvers.Count; i++)
                        {
                            var pm = selected.plannedManeuvers[i];
                            if (!pm.active) continue;

                            float alphaFactor = 1f - i * 0.25f;
                            if (alphaFactor < 0.25f) alphaFactor = 0.25f;

                            // ⚠ P52（2026-09-27）：`ConvertAll` **每帧各分配一个新 List**（每条预览轨道一份）
                            // ⇒ 改用**复用 scratch**（`DrawOrbitPath` 只当帧读它、不持有 ⇒ 复用它安全；
                            //    两个分支是顺序调用且各自立即绘制 ⇒ 共用一份 scratch 也安全）。
                            if (pm.previewCurrentOrbit != null && pm.previewCurrentOrbit.Count > 0)
                            {
                                FillScaled(pm.previewCurrentOrbit, toUnity, previewPtsScratch);
                                DrawOrbitPath(previewPtsScratch,
                                    new Color(0.25f, 0.9f, 1f, 0.4f * alphaFactor), 0.6f, camera);
                            }

                            if (pm.previewPostOrbit != null && pm.previewPostOrbit.Count > 0)
                            {
                                FillScaled(pm.previewPostOrbit, toUnity, previewPtsScratch);
                                DrawOrbitPath(previewPtsScratch,
                                    new Color(1f, 0.7f, 0.1f, 0.4f * alphaFactor), 0.6f, camera);
                            }

                            var nodePosUnity = pm.previewNodePosition * toUnity;
                            Color nodeColor = i == 0
                                ? new Color(1f, 0.9f, 0.2f, 0.9f)
                                : new Color(1f, 0.5f, 0.1f, 0.8f - i * 0.15f);
                            DrawManeuverNode((Vector3)nodePosUnity, camera, nodeColor, 2.2f);
                        }
                    }

                }
            }
        }

        public static void WorldInterface_OnGUI_Postfix()
        {
            // ★ 每帧 GUI 侧同样计时（2026-09-20）：与"世界层绘制"同一把尺 ⇒ 两侧合起来才是我们代码的每帧代价。
            long guiClock = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                WorldInterfaceOnGUIBody();
            }
            finally
            {
                long dt = System.Diagnostics.Stopwatch.GetTimestamp() - guiClock;
                _guiClocks++; _guiTicksTotal += dt;
                if (dt > _guiTicksMax) _guiTicksMax = dt;
                if (_guiClocks >= FrameAutoLogEvery)
                {
                    if (Prefs.DevMode)
                    {
                        double toUs = 1e6 / System.Diagnostics.Stopwatch.Frequency;   // µs
                        // ⚠ 2026-09-23 同上改 µs（这一条原来恒印 `平均=0.00 ms/帧`，最慢才有值）
                        Log.Message($"[帧耗时·GUI] 帧数={_guiClocks} 平均={_guiTicksTotal * toUs / _guiClocks:F1}µs/帧 最慢={_guiTicksMax * toUs:F1}µs");
                    }
                    _guiClocks = 0; _guiTicksTotal = 0; _guiTicksMax = 0;
                }
            }
        }

        private static long _guiClocks, _guiTicksTotal, _guiTicksMax;

        private static void WorldInterfaceOnGUIBody()
        {
            // 开关按钮：**不依赖 EnableVisualization**（关着的时候要能开回来）。
            // 2026-10-16 反射清理 A-4：原为同方法上的 Prefix 安装点，合并到此处（安装点 −1，行为不变）。
            DrawToggleButton();

            if (!EnableVisualization) return;
            if (!WorldRendererUtility.WorldSelected) return;
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;

            HandleInput(spaceGame);
            DrawInfoPanel(spaceGame);
        }

        /// <summary>
        /// 世界地图右下角的"显示/隐藏卫星视图"开关（原 `WorldInterface_OnGUI_Prefix_AddGizmo` 原样搬入）。
        /// </summary>
        private static void DrawToggleButton()
        {
            if (!WorldRendererUtility.WorldSelected) return;
            if (Current.ProgramState != ProgramState.Playing) return;
            if (Find.World == null) return;

            // 绘制按钮（右下角固定位置）
            float x = UI.screenWidth - 180f;
            float y = UI.screenHeight - 50f;
            Rect rect = new Rect(x, y, 160f, 40f);
            string label = EnableVisualization ? "隐藏卫星视图" : "显示卫星视图";
            if (Widgets.ButtonText(rect, label))
            {
                EnableVisualization = !EnableVisualization;
                // 关闭可视化时同时停止跟随和清除选中
                if (!EnableVisualization)
                {
                    isFollowing = false;
                    selectedGroupId = -1;
                }
            }
        }

        /// <summary>
        /// 跟随选中编队（2026-10-15 反射清理 A-13：原 `WorldCameraDriver_Update_Postfix`，
        /// 现由常驻 `PF_WorldCameraService` 每帧调用；farClip / 背景相机接管也都在它里面）。
        /// 只写 `sphereRotation`（由下一帧 vanilla Update 消费）⇒ 时序与原 postfix 同效。
        /// </summary>
        public static void ApplyFollowCameraFrame()
        {
            if (!EnableVisualization) return;
            if (!isFollowing) return;
            if (!WorldRendererUtility.WorldSelected) return;

            var driver = Find.WorldCameraDriver;
            if (driver == null) return;

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;

            var group = spaceGame.GetGroupById(selectedGroupId);
            if (group == null || !group.IsActive)
            {
                isFollowing = false;
                return;
            }

            Vector3 viewPos = GetGroupWorldPositionUnity(group);
            Vector3 origin = GetOrbitOrigin();
            Vector3 direction = (viewPos - origin).normalized;
            if (direction == Vector3.zero) return;

            Quaternion targetRot = Quaternion.Inverse(Quaternion.LookRotation(-direction));
            float t = Mathf.Clamp01(Time.deltaTime * followLerpSpeed);
            driver.sphereRotation = Quaternion.Slerp(driver.sphereRotation, targetRot, t);
        }

        // 在世界地图右下角添加一个按钮 —— 2026-10-16 反射清理 A-4：原为 `WorldInterfaceOnGUI` 的
        // Prefix 安装点，已合并进同方法的 Postfix 绘制体（见 `DrawToggleButton`）⇒ 该方法体删去。

        // ──────────── 摄像机拉远距离补丁 ────────────

        private const float NEW_MAX_ALTITUDE = 50000f;

        /// <summary>
        /// Transpiler: 把 WorldCameraDriverOnGUI 中的 1100f（MaxAltitude）替换为 NEW_MAX_ALTITUDE。
        /// **只**替换 `Mathf.Clamp(num2, MinAltitude, 1100f)` 里的上界（放开拉远）；
        /// 同行前的行星层切换判断（`num2 &gt; 1100f`）**保持原版**——层系统照旧，
        /// 与 1.6 的既有行为一致（1.6 时代该字面量同样未替换，观感已被验收）。
        /// 带上下文保护：仅替换后接 Mathf.Clamp 调用的 1100f，避免误改同名字面量。
        /// </summary>
        public static IEnumerable<CodeInstruction> Transpiler_MaxAltitude_OnGUI(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var next = i + 1 < list.Count ? list[i + 1] : null;
                if (PatchIlGuards.IsClampUpperBound(list[i], next))
                    list[i].operand = NEW_MAX_ALTITUDE;
            }
            return list;
        }

        /// <summary>
        /// Transpiler: 把 WorldCameraDriver.Update 中的 1100f（MaxAltitude）替换为 NEW_MAX_ALTITUDE。
        /// 影响旋转速度归一化因子 (altitude - MinAltitude) / (1100 - MinAltitude)。
        /// ⚠ 2026-10-15（补丁核对发现的真缺陷）：旧守卫要求 `ldc.r4 1100` 后接 `ldc.r4 5f`
        /// （1.6 的 `MinAltitude` 是常量 5f）；**1.7 的 `MinAltitude` 改成了静态属性**
        /// （`get_MinAltitude`，桌面 = 125f）⇒ 守卫永不匹配、替换静默失效（症状：拉远到高高度时
        /// 世界拖拽转速被放大到 ~43×）。现在两种形态都认（旧形态只为鲁棒性保留）。
        /// </summary>
        public static IEnumerable<CodeInstruction> Transpiler_MaxAltitude_Update(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var next = i + 1 < list.Count ? list[i + 1] : null;
                if (PatchIlGuards.IsMaxAltitudeDenominator(list[i], next))
                    list[i].operand = NEW_MAX_ALTITUDE;
            }
            return list;
        }

        // ──（原 `WorldCameraDriver.Awake` 的 farClip Postfix 已随 A-13 搬进
        //   `PF_CameraFollowDriver.Update`——见该类头注。）──


        // ── 核心辅助函数（单位转换关键） ──

        /// <summary>本帧的物理时刻（**保留 double**）：渲染与物理必须同一 tick —— int 化在 1/60× 流速下
        /// 会让天体位置每 60 真实 tick 跳 1 秒（PFView `:1003-1008` 的实测结论）。</summary>
        /// <summary>
        /// 世界视图的当前时刻（**帧级连续时钟**，2026-10-11 起）：与 PFView 同一条
        /// `TimeScaleManager.GetRenderTick()` —— 位置/轨道/波束全是"任意实数 tick 的解析函数"，
        /// 喂连续值 ⇒ 图标/轨道/波束**不再一格一格跳**（"别的航天器位置在抖"的另一半）。
        /// </summary>
        private static double PhysicsTickNow()
        {
            var tsm = Current.Game?.GetComponent<TimeScaleManager>();
            return tsm != null ? tsm.GetRenderTick() : TimeScaleManager.GetPhysicsTickSafe();
        }

        /// <summary>系统系（Unity 单位）→ 视图系。**薄转发**，算式在 `ViewFrame` 里只有一份。</summary>
        private static Vector3 ToView(Vector3 systemFrameUnity)
            => ViewFrame.ToViewUnityFromUnity(systemFrameUnity, PhysicsTickNow());

        /// <summary>
        /// 视图系的原点 = **被查看天体的球心**（`ViewFrame.ToViewUnity` 已减掉它的系统系偏移）
        /// ⇒ 这里恒为零。⚠ 2026-09-23 前它被当作"根行星球心"用，而玩家切到月球时那就不成立了。
        /// </summary>
        private static Vector3 GetOrbitOrigin() => Vector3.zero;

        /// <summary>
        /// 获取 Group 在**视图系**中的 Unity 单位位置。
        /// 位置源恒为**系统系**（圆轨解析位置内部已含中心天体偏移）；换系与视觉自转由 `ViewFrame` 一并处理
        /// ⇒ 圆轨/积分切换无跳变，切天体视图时整场一起平移（2026-09-23）。
        /// </summary>
        private static Vector3 GetGroupWorldPositionUnity(SpaceEntityGroup group)
        {
            double physicsTick = PhysicsTickNow();
            DVector3 posSystemFrame = group.IsCircularOrbit
                ? group.GetCircularPositionAtTick(physicsTick)
                : group.CurrentOrbit.positionSystem.v;
            return ViewFrame.ToViewUnity(posSystemFrame, physicsTick);
        }

        private static bool IsViewPosVisible(Vector3 viewPosUnity, float occlusionMargin)
        {
            Vector3 origin = GetOrbitOrigin();
            Vector3 offset = viewPosUnity - origin;
            float radius = offset.magnitude;
            if (radius < 0.01f) return false;

            Vector3 camDir = (Find.WorldCameraDriver.CameraPosition - origin).normalized;
            Vector3 satDir = offset / radius;
            float facing = Vector3.Dot(camDir, satDir);
            if (facing >= 0) return true;

            float perpDist = radius * Mathf.Sqrt(1 - facing * facing);
            float planetRadius = ViewFrame.ViewedBodyRadiusUnity;   // 被查看天体的球半径（地球 100 / 月球 30）
            return perpDist > planetRadius + occlusionMargin;
        }

        private static void DrawGroupIcon(SpaceEntityGroup group, Camera cam, Vector3? viewPosOverride = null)
        {
            Vector3 viewPos = viewPosOverride ?? GetGroupWorldPositionUnity(group);
            if (!IsViewPosVisible(viewPos, 2f)) return;

            Vector3 toCam = cam.transform.position - viewPos;
            if (toCam.sqrMagnitude < 0.01f) return;

            Quaternion rot = Quaternion.LookRotation(cam.transform.up, toCam.normalized);
            float altitude = Find.WorldCameraDriver?.altitude ?? 1000f;
            // 用户裁定（2026-10-11）：**只留最大尺寸、不要最小尺寸**（原来下限 1× ⇒ 贴脸时图标占屏高 ~65%）。
            // 相机高度下限（125 单位）本身保证 sqrt 项为正 ⇒ 不需要额外下限保护。
            float scale = Mathf.Min(Mathf.Sqrt(altitude / IconScaleReferenceAltitude), IconScaleMaxMultiplier);
            float size = SurfaceIconSize * scale;
            if (selectedGroupId == group.GroupId) size *= 1.12f;

            Vector3 drawPos = viewPos + toCam.normalized * 1.5f; // 避免深度冲突
            // 2026-10-16 合批：图标**同 mesh 同材质、连 MPB 都没有**（全同色）⇒ 整帧所有图标并成一次提交
            //（原来是"每个编队一次"——蜂群/星座动辄上百上千群）。矩阵自带位置/尺寸（选中放大也在矩阵里）⇒ 外观不变。
            IconBatchAdd(Matrix4x4.TRS(drawPos, rot, new Vector3(size, 1f, size)));
        }

        /// <summary>图标批是否已开（一帧内一次：首个图标开批、组循环结束后 <see cref="IconBatchFlush"/> 收口）。</summary>
        private static bool iconBatchOpen;

        private static void IconBatchAdd(Matrix4x4 matrix)
        {
            if (!iconBatchOpen)
            {
                WorldQuadBatch.Reset();
                iconBatchOpen = true;
            }
            WorldQuadBatch.AddQuad(matrix);
        }

        /// <summary>收口图标批（空批 = 无操作）。逐个提交时的顺序 = 加入顺序 ⇒ 外观不变。</summary>
        private static void IconBatchFlush()
        {
            if (!iconBatchOpen) return;
            iconBatchOpen = false;

            if (WorldQuadBatch.ItemCount > 0)
            {
                CountDraw(DcIcon);
                CountQuads(DcIcon, WorldQuadBatch.ItemCount);
                // `null` 属性 ⇒ 与原 4 参调用同链（最终都是 ShadowCastingMode.On，已核 UnityStubs 重载链）
                WorldQuadBatch.Flush(iconMaterial, null, WorldCameraManager.WorldLayer);
            }
        }

        /// <summary>
        /// 生成圆轨采样点（相对轨道原点的 Unity 坐标），供 DrawOrbitPath 使用。
        /// 包含鼠标高度衰减、轨道平面支持。不含视觉旋转（由 DrawOrbitPath 统一叠加）。
        /// </summary>
        // 第 11 条（2026-09-10）：选中组非圆轨路径的 per-frame 缓存（键 = 组 id + pos + vel，
        // 路径是 pos/vel 的纯函数；滑行期命中，变轨/点火时自然重建）。
        // （静态类故为 static；键含组 id + pos + vel，换局/变轨时键必不同 → 自动重建，无跨局陈旧）
        private static int orbitPathCacheGroupId = -1;
        private static DVector3 orbitPathCachePos;
        private static DVector3 orbitPathCacheVel;
        private static List<Vector3>? orbitPathCachePoints;

        private static List<Vector3> GetCircularOrbitPointsUnity(SpaceEntityGroup group)
        {
            double radiusMetersD = group.circularRadius;
            float radiusUnity = (float)(radiusMetersD * SpaceConstants.UnitsPerMeter);
            int sampleCount = Mathf.Clamp(Mathf.RoundToInt(2 * Mathf.PI * radiusUnity / OrbitMarkerSpacing), MinOrbitSamples, MaxOrbitSamples);

            // 根据摄像机高度衰减采样点数量
            float camAlt = Find.WorldCameraDriver?.altitude ?? 1000f;
            float altFactor = Mathf.Clamp01(5000f / camAlt);
            sampleCount = Mathf.Max(MinOrbitSamples, Mathf.RoundToInt(sampleCount * altFactor));

            // 轨道平面
            bool usePlane = group.circularOrbitNormal.sqrMagnitude > 0.001f
                         && group.circularRadialRef.sqrMagnitude > 0.001f;
            Vector3 radialRef = (Vector3)group.circularRadialRef;
            Vector3 yDir = usePlane
                ? Vector3.Cross((Vector3)group.circularOrbitNormal, radialRef).normalized
                : Vector3.forward;

            var points = new List<Vector3>(sampleCount);

            // 中心天体在**系统系**里的偏移：根行星为 0、卫星为绕地位置。
            // ⚠ 走 `ViewFrame.BodySystemPosition`（"天体在系统系的位置"的唯一口径），
            //   并**保留 double tick** —— 原来这里是 `(int)Math.Round` ⇒ 1/60× 流速下位置离散跳变。
            Vector3 centralBodyOffsetUnity = Vector3.zero;
            if (group.circularCentralBodyId >= 0)
            {
                centralBodyOffsetUnity = (Vector3)(ViewFrame.BodySystemPosition(
                    group.circularCentralBodyId, PhysicsTickNow()) * SpaceConstants.UnitsPerMeter);
            }

            for (int i = 0; i < sampleCount; i++)
            {
                float angle = 2f * Mathf.PI * i / sampleCount;
                float cosA = Mathf.Cos(angle);
                float sinA = Mathf.Sin(angle);

                if (usePlane)
                {
                    Vector3 metersBody = (Vector3)((DVector3)(radialRef * cosA + yDir * sinA) * radiusMetersD);
                    points.Add(((Vector3)((DVector3)metersBody * SpaceConstants.UnitsPerMeter)) + centralBodyOffsetUnity);
                }
                else
                {
                    points.Add(new Vector3(cosA * radiusUnity, 0f, sinA * radiusUnity) + centralBodyOffsetUnity);
                }
            }
            return points;
        }

        /// <summary>
        /// 世界视图**赤道经度标记**（2026-10-01，用户裁定：把原来的"红色正方体钉在据点/发射点"**换成这个**）：
        /// 在被查看天体的**赤道上**画两枚小标记 —— **0°（青）/ 90°E（橙）**，不脉动。
        ///
        /// 经纬度 → ECI 走 `SurfaceFrame.SurfaceState`（**单权威**：`p = R·(cosφ·sinλ, sinφ, −cosφ·cosλ)`，
        /// λ 就是 `EciToLongLat` 的反函数 ⇒ 两处同一套口径），再走唯一换系口 `ViewFrame.ToViewUnity`。
        /// ⚠ 时刻用 `PhysicsTickNow()`（**physicsTick 轴** —— 天体位置是它的函数；见 `ManeuverPlanner` 的 U-12 同族教训）。
        /// </summary>
        private static void DrawEquatorLonMarkers(Camera cam)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            int viewed = ViewFrame.ViewedBodyId;
            var viewBody = mgr?.GetBodyById(viewed);
            if (viewBody == null) return;

            double tick = PhysicsTickNow();
            double omega = Launch.GetSurfaceState.RotationOmega(mgr, viewed);
            for (int i = 0; i < 2; i++)
            {
                double lonRad = i == 0 ? 0.0 : Math.PI / 2.0;      // 固连系经度：0° / 90°E
                (DVector3 pEci, _) = SurfaceFrame.SurfaceState(viewBody.radius, 0.0, lonRad, omega, tick);
                Vector3 onSurface = ViewFrame.ToViewUnity(pEci, tick);
                Vector3 radialView = onSurface.sqrMagnitude > 1e-6f ? onSurface.normalized : Vector3.up;
                Vector3 drawPos = onSurface + radialView * 4f;    // 贴表面略抬（同原来那档）

                if (!IsViewPosVisible(drawPos, 0.5f)) continue;

                Quaternion rot = Quaternion.FromToRotation(Vector3.up, radialView);
                Matrix4x4 matrix = Matrix4x4.TRS(drawPos, rot, Vector3.one * 8f);
                orbitMarkerPropertyBlock.SetColor("_Color", i == 0 ? CyanColor : EquatorLon90Color);
                CountDraw(DcMarker);
                Graphics.DrawMesh(sphereMesh, matrix, orbitMarkerMaterial, WorldCameraManager.WorldLayer, null, 0, orbitMarkerPropertyBlock);
            }
        }

        // ── P52（2026-09-27）：渲染路径上的每帧分配，改成"建一次/复用" ──
        // ⚠ 两条都声明为**可空**：它们靠"管理器同一性"判有效性（`== null` 就是"还没建"）
        //   ⇒ 声明成非空会让 nullable 分析报 CS8618/CS8601（新增警告 = 0 是硬线）。
        private static HashSet<int>? earthSystemBodyIds;              // 地球系天体 id 集（建一次）
        private static CelestialBodyManager? earthSystemBodyIdsMgr;   // 建它时的管理器（换局 ⇒ 重建）
        private static readonly List<Vector3> previewPtsScratch = new();  // 预览轨道点（复用）

        /// <summary>`src` 逐点乘 `scale` 填进 `dst`（替代 `ConvertAll`：**不分配**，`dst` 复用）。
        /// 只在渲染路径用 ⇒ `dst` 一定是本帧刚清空/刚填满的。</summary>
        private static void FillScaled(List<Vector3> src, float scale, List<Vector3> dst)
        {
            dst.Clear();
            if (dst.Capacity < src.Count) dst.Capacity = src.Count;
            for (int i = 0; i < src.Count; i++)
                dst.Add(src[i] * scale);   // 与原来的 lambda 逐字同式（`Vector3 * float`）
        }

        /// <summary>
        /// 绘制轨道路径 (点列)。自动叠加视觉自转。
        /// </summary>
        private static void DrawOrbitPath(List<Vector3> pointsUnity, Color color, float size, Camera cam)
        {
            _frameOrbitPaths++; _frameOrbitPathsThisFrame++;   // 轨道线计数（现状应恒为 ≤1 + 机动预览段）
            // 2026-10-16 合批：整条路径**同 mesh 同材质同一个颜色** ⇒ 拼一个 mesh、一次 DrawMesh
            //（原来是"每个可见轨道点一次"——采样 36–180 点 ⇒ 36–180 次/帧）。
            orbitMarkerPropertyBlock.SetColor("_Color", color);   // 一色一批；原在循环内逐点设（冗余）
            WorldQuadBatch.Reset();
            for (int i = 0; i < pointsUnity.Count; i++)
            {
                Vector3 viewPos = ToView(pointsUnity[i]);
                if (!IsViewPosVisible(viewPos, 1f)) continue;

                Vector3 toCam = cam.transform.position - viewPos;
                if (toCam.sqrMagnitude < 0.01f) continue;

                Quaternion rot = Quaternion.LookRotation(cam.transform.up, toCam.normalized);
                Vector3 drawPos = viewPos + toCam.normalized * 0.5f;
                WorldQuadBatch.AddQuad(Matrix4x4.TRS(drawPos, rot, new Vector3(size, 1f, size)));
            }

            if (WorldQuadBatch.ItemCount > 0)
            {
                CountDraw(DcOrbitPoint);
                CountQuads(DcOrbitPoint, WorldQuadBatch.ItemCount);
                WorldQuadBatch.Flush(orbitMarkerMaterial, orbitMarkerPropertyBlock, WorldCameraManager.WorldLayer);
            }
        }

        /// <summary>
        /// 绘制机动节点 (较大、醒目)。自动叠加视觉自转。
        /// </summary>
        private static void DrawManeuverNode(Vector3 nodePosUnity, Camera cam, Color? color = null, float baseSize = 2.5f)
        {
            Vector3 viewPos = ToView(nodePosUnity);
            if (!IsViewPosVisible(viewPos, 1f)) return;

            Vector3 toCam = cam.transform.position - viewPos;
            if (toCam.sqrMagnitude < 0.01f) return;

            // 节点：脉冲效果
            float pulse = 1f + 0.15f * Mathf.Sin(Time.realtimeSinceStartup * 4f);
            float nodeSize = baseSize * pulse;

            Quaternion rot = Quaternion.LookRotation(cam.transform.up, toCam.normalized);
            Vector3 drawPos = viewPos + toCam.normalized * 0.5f;
            Matrix4x4 matrix = Matrix4x4.TRS(drawPos, rot, new Vector3(nodeSize, 1f, nodeSize));
            Color c = color ?? new Color(1f, 0.9f, 0.2f, 0.9f);
            orbitMarkerPropertyBlock.SetColor("_Color", c);
            CountDraw(DcMarker);
            Graphics.DrawMesh(MeshPool.plane10, matrix, orbitMarkerMaterial, WorldCameraManager.WorldLayer, null, 0, orbitMarkerPropertyBlock);
        }

        /// <summary>
        /// 为指定 Group 绘制 Pe/Ap/AN/DN 标记。
        /// </summary>
        private static void DrawOrbitMarkersForGroup(SpaceEntityGroup group, Camera cam)
        {
            float toUnity = (float)SpaceConstants.UnitsPerMeter;

            // ⚠ 2026-09-26（D6）：中心天体 GM 必须**显式**给（唯一权威 `GetBodyGM`）。
            //   原先下面两处 `OrbitMath` 调用都省略 `mu` ⇒ `ResolveGM` 静默取默认 GM（地球）
            //   ⇒ **绕月编队的 Pe/Ap 标记是按地球 GM 算的**（与轨道线同一个病）。
            //   取不到就直接不画：宁可什么都不画，也不拿错的 GM 画。
            var sgForGm = Current.Game?.GetComponent<SpaceGameComp>();
            if (sgForGm == null) return;
            double muBody = sgForGm.GetBodyGM(group.CelestialBodyId);

            // 获取轨道状态
            DVector3 posMetersD, velMetersD;
            if (group.IsCircularOrbit)
            {
                double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
                posMetersD = group.GetCircularPositionAtTick(physicsTick);
                // 用解析速度（当前相位切线），保证 pos/vel 相位一致；
                // 否则裸用固定 radialRef/yDir 会算出随相位旋转的虚假偏心率，导致 Pe/Ap 标记漂移。
                velMetersD = group.GetCircularVelocityAtTick(physicsTick);
            }
            else
            {
                posMetersD = group.CurrentOrbit.positionSystem.v;
                velMetersD = group.CurrentOrbit.velocitySystem.v;
            }

            OrbitMath.GetOrbitFrame(posMetersD, velMetersD, out double a, out double e, out double _, out DVector3 eDir, out DVector3 _, muBody);

            // 绘制 Pe/Ap 标记（仅椭圆轨且 e > 0.005 时）
            if (a > 0.0 && e > 0.005)
            {
                double peDist = OrbitMath.GetPeriapsisDistance(a, e);
                double apDist = OrbitMath.GetApoapsisDistance(a, e);
                Vector3 pePos = (Vector3)(eDir * peDist * toUnity);
                Vector3 apPos = (Vector3)(-eDir * apDist * toUnity);

                DrawOrbitMarker(ToView(pePos), MarkerColorPeriapsis, 1.8f, cam, "Pe");
                DrawOrbitMarker(ToView(apPos), MarkerColorApoapsis, 1.8f, cam, "Ap");
            }

            // 绘制 AN/DN 标记（非共面时）。
            // 【V2 收敛 2026-09-18】原先走 `OrbitMath.GetNodePositions(pos, vel, DVector3.up)`；
            // 那是同一能力的第二份实现，且它的参考法向是"调用方传 +Y"而适配层是 +z。
            // 现在改走 `OrbitView` 的交点族：`FromGameStateVectors` 会把游戏系 +Y 映射成适配层 +z，
            // 于是 `*Equatorial*` 一族要的正是**相对游戏世界赤道**的交点 —— 与这里的意图一致。
            var celestialMgrDbg = Current.Game?.GetComponent<CelestialBodyManager>();
            CelestialBodyDef? defDbg = celestialMgrDbg?.GetBodyById(group.CelestialBodyId);
            if (defDbg != null)
            {
                OrbitView ovDbg = OrbitView.FromGameStateVectors(posMetersD, velMetersD,
                    defDbg.gravitationalParameter, TimeScaleManager.GetPhysicsTickSafe(), DVector3.up, defDbg.radius);
                // ⚠ 这两个位置来自 `*Equatorial*` 族 ⇒ 是**适配层系**的；下面 `ToView` 的读者按**游戏系**理解。
                //   批4 P4 之后两系同轴（换系恒等），批6 S2 起跨系只经 `OrbitView` 的进/出两个口 ⇒ 这里直传。
                // 批6 S3-3a：交点位置改走 `Try*` —— 旧写法只判了 AN 的非零性、却**无条件**画 DN
                //（DN 在双曲开口里时会把标记画到原点 ⇒ 那是 A6"忘了判就拿零矢量继续算"的原样标本）。
                if (ovDbg.TryAscendingNodePositionEquatorial(0.0, out DVector3 anPos)
                    && ovDbg.TryDescendingNodePositionEquatorial(0.0, out DVector3 dnPos))
                {
                    DrawOrbitMarker(ToView((Vector3)(anPos * toUnity)), MarkerColorAn, 1.6f, cam, "AN");
                    DrawOrbitMarker(ToView((Vector3)(dnPos * toUnity)), MarkerColorDn, 1.6f, cam, "DN");
                }
            }
        }

        /// <summary>
        /// 绘制单个轨道标记点（Pe/Ap/AN/DN 通用）。
        /// </summary>
        private static void DrawOrbitMarker(Vector3 viewPos, Color color, float size, Camera cam, string? label = null)
        {
            if (!IsViewPosVisible(viewPos, 1f)) return;

            Vector3 toCam = cam.transform.position - viewPos;
            if (toCam.sqrMagnitude < 0.01f) return;

            // 标记：慢速脉冲
            float pulse = 1f + 0.1f * Mathf.Sin(Time.realtimeSinceStartup * 2f);
            float markerSize = size * pulse;

            Quaternion rot = Quaternion.LookRotation(cam.transform.up, toCam.normalized);
            Vector3 drawPos = viewPos + toCam.normalized * 0.6f;
            Matrix4x4 matrix = Matrix4x4.TRS(drawPos, rot, new Vector3(markerSize, 1f, markerSize));
            orbitMarkerPropertyBlock.SetColor("_Color", color);
            CountDraw(DcMarker);
            Graphics.DrawMesh(MeshPool.plane10, matrix, orbitMarkerMaterial, WorldCameraManager.WorldLayer, null, 0, orbitMarkerPropertyBlock);
        }


        // ── [V-2] 认知层可视化（影子 / 假接触 / 波束锥） ──

        private static readonly List<Contact> cognitionContactBuf = new();
        private static readonly List<Vector3> cognitionPointBuf = new();
        private static readonly DVector3[] coneRimScratch = new DVector3[24];

        private static readonly Color ShadowColor = new(0.78f, 0.78f, 0.82f, 0.85f);      // 影子：灰白
        private static readonly Color DecoyColor = new(0.95f, 0.55f, 0.35f, 0.9f);        // 假接触（未识破）
        private static readonly Color DecoySpottedColor = new(0.6f, 0.32f, 0.32f, 0.9f);  // 已识破
        private static readonly Color BeamColor = new(0.35f, 0.95f, 1f, 0.8f);            // 波束锥（2026-10-11：α 0.45→0.8，原值在行星底图上太淡 = "看不见"的一半原因）

        /// <summary>
        /// [V-2] 认知层可视化：影子接触（σ 圈）/ 假接触（识破换标记）/ 雷达波束锥。
        /// 数据一律走认知层读入口（`AppendVisible` / `ContactPhaseOf`），**不读真值**；
        /// dev 真值开关打开时**影子/假接触**整体跳过（那时要看的本来就是真值）。
        ///
        /// ⚠ 2026-10-11（用户报"看不见波束锥"）：**波束锥从 dev 开关的跳过里摘出来**，两条理由 ——
        /// ① 它画的是"**我自己的雷达指向哪**"（我方已知状态、不是对敌认知）⇒ dev 模式下同样该看得见；
        /// ② 原实现整体被 dev 开关跳过 ⇒ 开了 dev 就永远看不到锥（一个容易踩的盲区）。
        /// 同批补 `[波束锥]` 限频读数：**"该画没画"必须给出原因**（未开机 / RadarCap 为空 / 全被行星剔除 / 顶点在屏外）。
        /// </summary>
        private static void DrawCognitionOverlay(Camera cam, SpaceGameComp spaceGame, SensorManager? sensor)
        {
            DrawBeamCones(cam, spaceGame);   // 先画锥（不受 dev 开关影响，见上）

            if (sensor == null || SensorManager.DebugShowGroundTruth) return;
            var player = Faction.OfPlayer;
            if (player == null) return;

            double now = PhysicsTickNow();
            int fid = player.loadID;

            // ── 影子接触（Live 已由棋子图标表达）+ 假接触 ──
            cognitionContactBuf.Clear();
            sensor.AppendVisible(fid, cognitionContactBuf);
            for (int i = 0; i < cognitionContactBuf.Count; i++)
            {
                var c = cognitionContactBuf[i];
                if (c.IsForged)
                {
                    // 假接触**没有真值对应物**：画在"我测到的"位置上；识破（isDecoy）⇒ 换标记
                    Vector3 dvp = ViewFrame.ToViewUnity(c.position, now);
                    DrawCognitionDot(dvp, cam, c.isDecoy ? DecoySpottedColor : DecoyColor, c.isDecoy ? 1.2f : 1.7f);
                    continue;
                }
                if (sensor.ContactPhaseOf(fid, c.targetGroupId) != TrackPhase.Shadow) continue;
                Vector3 svp = ViewFrame.ToViewUnity(c.position, now);
                DrawCognitionDot(svp, cam, ShadowColor, 1.3f);
                DrawSigmaRing(svp, c.positionSigma, cam);
            }
        }

        /// <summary>波束锥读数的时间戳（限频日志用）。</summary>
        private static double lastBeamConeLogTick = double.MinValue;

        /// <summary>
        /// 波束锥一层：**开机（`ActivePingEnabled`）且"选中 或 我方"**的编队各画一支
        /// （顶点 = 编队位置、轴向 = `RadarBoresight`、几何取群级 `RadarCap`）。
        /// 每帧调用（世界视图绘制路径）+ **限频读数**：把"该画没画"的原因变成数字（2026-10-11 加）。
        /// </summary>
        private static void DrawBeamCones(Camera cam, SpaceGameComp spaceGame)
        {
            if (spaceGame == null) return;
            var player = Faction.OfPlayer;
            if (player == null) return;
            int fid = player.loadID;
            double now = PhysicsTickNow();

            int pingGroups = 0, matched = 0, drew = 0, pts = 0, visPts = 0;
            string? blockReason = null;
            string? perGroup = null;

            // 点尺寸（2026-10-11 修：原 1.1 单位 = 屏上 3–5 px、还半透明 ⇒ 用户"看不见"）。
            // 口径照图标那套"屏幕尺寸恒定"（`sqrt(altitude/850)`），但基准小一档：3–9 单位（图标是 28–84）。
            float altitude = Find.WorldCameraDriver?.altitude ?? 1000f;
            float dotSize = 3f * Mathf.Clamp(Mathf.Sqrt(altitude / IconScaleReferenceAltitude), 1f, IconScaleMaxMultiplier);

            foreach (var g in spaceGame.GetAllGroups())
            {
                if (g == null || !g.IsActive || !g.ActivePingEnabled) continue;
                pingGroups++;
                if (g.GroupId != selectedGroupId && g.FactionId != fid) continue;
                matched++;

                if (DrawBeamCone(g, cam, now, dotSize, out int p, out int v, out string? why))
                {
                    drew++; pts += p; visPts += v;
                    if (perGroup == null)
                    {
                        var rc = g.RadarCap;
                        perGroup = $"G{g.GroupId} 半锥角 {rc.beamHalfAngleDeg:F1}° · 距离 {rc.beamRangeMeters / 1000f:F0} km · 锥点 {p}/视可见 {v} · 点径 {dotSize:F1} 单位";
                    }
                }
                else if (blockReason == null) blockReason = why;
            }

            // 限频：**有开机编队 = 60 物理秒一行**（现场排查节奏）；没有 = 300 物理秒一行（常态心跳，证路径通）
            double interval = pingGroups > 0 ? 60.0 : 300.0;
            if (now - lastBeamConeLogTick < interval) return;
            lastBeamConeLogTick = now;
            Log.Message($"[波束锥] 开机编队 {pingGroups}（命中\"选中或我方\" {matched}）⇒ 绘制 {drew}｜锥点 {pts}/视可见 {visPts}{(blockReason != null ? "｜未画原因：" + blockReason : "")}{(perGroup != null ? "｜" + perGroup : "")}");
        }

        /// <summary>
        /// 画一支波束锥。返回 false = **没画**（`blockReason` 写明哪一条没满足 —— 读数用，不是判据）。
        /// `dotSize` = 点径（视图单位；由调用方按相机高度算 ⇒ 屏幕尺寸恒定）。
        /// </summary>
        private static bool DrawBeamCone(SpaceEntityGroup g, Camera cam, double now, float dotSize,
            out int points, out int visiblePoints, out string? blockReason)
        {
            points = 0; visiblePoints = 0; blockReason = null;

            var rc = g.RadarCap;
            if (rc.beamRangeMeters <= 0f || rc.beamHalfAngleDeg <= 0f)
            {
                blockReason = $"G{g.GroupId} 的 RadarCap 为空（半锥角 {rc.beamHalfAngleDeg:F1}° / 距离 {rc.beamRangeMeters / 1000f:F0} km）——本船没有主动雷达建筑，或建筑投影还没跑";
                return false;
            }

            DVector3 dir = g.RadarBoresight(now);
            if (dir.sqrMagnitude < 1e-9) { blockReason = $"G{g.GroupId} 的 RadarBoresight 退化（零向量）"; return false; }
            dir = dir.normalized;

            DVector3 apex = g.IsCircularOrbit ? g.GetCircularPositionAtTick(now) : g.CurrentOrbit.positionSystem.v;

            // ⚠ 真·视锥读数（2026-10-11 补）：`IsViewPosVisible` 只判**行星遮挡**，不是"在不在屏幕上" ——
            //   之前那行 `视可见 84` 会给人假安心（点全在屏外时它也报 84）。这里把锥顶的**视口坐标**打出来。
            Vector3 apexView = ViewFrame.ToViewUnity(apex, now);
            Vector3 vpApex = cam.WorldToViewportPoint(apexView);
            bool apexOnScreen = vpApex.z > 0f && vpApex.x >= 0f && vpApex.x <= 1f && vpApex.y >= 0f && vpApex.y <= 1f;

            DVector3 axisRef = Math.Abs(dir.y) < 0.9 ? new DVector3(0, 1, 0) : new DVector3(1, 0, 0);
            DVector3 u = DVector3.Cross(dir, axisRef).normalized;
            DVector3 v = DVector3.Cross(dir, u).normalized;

            float halfAngleRad = rc.beamHalfAngleDeg * Mathf.Deg2Rad;
            float sinHalf = Mathf.Sin(halfAngleRad);
            double range = rc.beamRangeMeters;
            DVector3 rimDir = dir * Math.Cos(halfAngleRad);

            const int ring = 20;
            for (int i = 0; i < ring; i++)
            {
                float a = Mathf.PI * 2f * i / ring;
                coneRimScratch[i] = apex + (rimDir + (u * (Math.Cos(a) * sinHalf) + v * (Math.Sin(a) * sinHalf))) * range;
            }

            cognitionPointBuf.Clear();
            // 端面圆环
            for (int i = 0; i < ring; i++)
                cognitionPointBuf.Add(ViewFrame.ToViewUnity(coneRimScratch[i], now));
            // 母线（8 条，各 8 点 ⇒ 点阵锥面）
            for (int k = 0; k < 8; k++)
            {
                DVector3 rimPt = coneRimScratch[k * (ring / 8)];
                for (int s = 1; s <= 8; s++)
                {
                    double t = s / 8.0;
                    cognitionPointBuf.Add(ViewFrame.ToViewUnity(apex + (rimPt - apex) * t, now));
                }
            }

            points = cognitionPointBuf.Count;
            visiblePoints = DrawMarkerPoints(cognitionPointBuf, BeamColor, dotSize, cam);
            if (!apexOnScreen)
                blockReason = $"G{g.GroupId} 锥顶**在屏幕外**（视口 {vpApex.x:F2},{vpApex.y:F2}）——把镜头转向本船再看";
            else if (visiblePoints == 0)
                blockReason = $"G{g.GroupId} 锥顶在屏内（视口 {vpApex.x:F2},{vpApex.y:F2}）但 {points} 个点**全部被行星剔除**——换个角度看";
            return true;
        }

        private static void DrawCognitionDot(Vector3 viewPos, Camera cam, Color color, float size)
        {
            if (!IsViewPosVisible(viewPos, 1f)) return;
            Vector3 toCam = cam.transform.position - viewPos;
            if (toCam.sqrMagnitude < 0.01f) return;

            Quaternion rot = Quaternion.LookRotation(cam.transform.up, toCam.normalized);
            Matrix4x4 matrix = Matrix4x4.TRS(viewPos + toCam.normalized * 0.5f, rot, new Vector3(size, 1f, size));
            orbitMarkerPropertyBlock.SetColor("_Color", color);
            CountDraw(DcMarker);
            Graphics.DrawMesh(MeshPool.plane10, matrix, orbitMarkerMaterial, WorldCameraManager.WorldLayer, null, 0, orbitMarkerPropertyBlock);
        }

        /// <summary>点阵画笔：输入**视图系**点列（与 `DrawOrbitPath` 的区别：不做 ToView、单独计数）。
        /// 返回**实际画出的点数**（被行星/视锥剔除的不计）——供 `[波束锥]` 读数用。
        /// 2026-10-16 **合批**：一次调用 = 一个颜色 ⇒ 拼一个 mesh、一次 DrawMesh（原来每点一次；
        /// σ 圈 24 点、波束锥数十点都在这里受益）；返回值口径不变（= 实际点数）。</summary>
        private static int DrawMarkerPoints(List<Vector3> viewPoints, Color color, float size, Camera cam)
        {
            int drawn = 0;
            orbitMarkerPropertyBlock.SetColor("_Color", color);   // 一色一批；原在循环内逐点设（冗余）
            WorldQuadBatch.Reset();
            for (int i = 0; i < viewPoints.Count; i++)
            {
                Vector3 viewPos = viewPoints[i];
                if (!IsViewPosVisible(viewPos, 1f)) continue;
                Vector3 toCam = cam.transform.position - viewPos;
                if (toCam.sqrMagnitude < 0.01f) continue;
                Quaternion rot = Quaternion.LookRotation(cam.transform.up, toCam.normalized);
                WorldQuadBatch.AddQuad(Matrix4x4.TRS(viewPos + toCam.normalized * 0.5f, rot, new Vector3(size, 1f, size)));
                drawn++;
            }

            if (drawn > 0)
            {
                CountDraw(DcMarker);
                CountQuads(DcMarker, drawn);
                WorldQuadBatch.Flush(orbitMarkerMaterial, orbitMarkerPropertyBlock, WorldCameraManager.WorldLayer);
            }
            return drawn;
        }

        /// <summary>σ 不确定圈：绕最后已知点、在垂直于视线的平面上画圆（半径 = σ）。</summary>
        private static void DrawSigmaRing(Vector3 viewPos, double sigmaMeters, Camera cam)
        {
            if (sigmaMeters <= 0.5) return;
            float radiusUnity = (float)(sigmaMeters * SpaceConstants.UnitsPerMeter);
            if (radiusUnity < 0.05f) return;

            Vector3 toCam = (cam.transform.position - viewPos).normalized;
            Vector3 u = Vector3.Cross(toCam, Mathf.Abs(toCam.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 v = Vector3.Cross(toCam, u).normalized;

            cognitionPointBuf.Clear();
            const int n = 24;
            for (int i = 0; i < n; i++)
            {
                float a = Mathf.PI * 2f * i / n;
                cognitionPointBuf.Add(viewPos + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * radiusUnity);
            }
            DrawMarkerPoints(cognitionPointBuf, ShadowColor, 1.0f, cam);
        }


        // ── GUI 交互 ──

        private static void HandleInput(SpaceGameComp spaceGame)
        {
            if (Event.current.type != EventType.MouseDown || Event.current.button != 0) return;
            if (!WorldRendererUtility.WorldSelected) return;
            if (MouseIsOverPanel()) return;

            Vector2 mousePos = Event.current.mousePosition;
            SpaceEntityGroup? clickedGroup = null;
            float minDist = 20f;
            var sensor = sensorMgrRef.Get();          // 点击与绘制**同一准入判据**（否则会点到看不见的棋子）
            foreach (var group in spaceGame.GetAllGroups())
            {
                if (!group.IsActive) continue;
                if (sensor != null && !SensorManager.DebugShowGroundTruth && !sensor.PlayerCanSee(group)) continue;
                Vector3 viewPos = GetGroupWorldPositionUnity(group);
                if (!IsViewPosVisible(viewPos, 2f)) continue;
                Vector2 screenPos = GenWorldUI.WorldToUIPosition(viewPos);
                float dist = Vector2.Distance(mousePos, screenPos);
                if (dist < minDist)
                {
                    minDist = dist;
                    clickedGroup = group;
                }
            }

            if (clickedGroup != null)
            {
                selectedGroupId = clickedGroup.GroupId;
                Event.current.Use();
            }
            else
            {
                selectedGroupId = -1;
                isFollowing = false;
            }
        }

        private static bool MouseIsOverPanel()
        {
            if (selectedGroupId == -1) return false;
            if (!TryGetPanelRect(out Rect rect)) return false;
            return rect.Contains(Event.current.mousePosition);
        }

        private static bool TryGetPanelRect(out Rect rect)
        {
            rect = default;
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return false;

            var group = spaceGame.GetGroupById(selectedGroupId);
            if (group == null) return false;
            if (!GetPanelAnchor(group, out Vector2 anchor, out _)) return false;
            rect = CalculatePanelRect(anchor);
            return true;
        }

        private static bool GetPanelAnchor(SpaceEntityGroup group, out Vector2 anchor, out bool occluded)
        {
            anchor = default;
            occluded = false;
            Vector3 viewPos = GetGroupWorldPositionUnity(group);
            if (IsViewPosVisible(viewPos, 2f))
            {
                anchor = GenWorldUI.WorldToUIPosition(viewPos);
                return true;
            }
            // 被遮挡时，计算屏幕边缘指示点
            Vector3 origin = GetOrbitOrigin();
            Vector2 centerScreen = GenWorldUI.WorldToUIPosition(origin);
            Vector2 dirScreen = (anchor - centerScreen).normalized;
            float radius = Mathf.Min(UI.screenWidth, UI.screenHeight) * 0.22f;
            anchor = centerScreen + dirScreen * radius;
            anchor.x = Mathf.Clamp(anchor.x, 32f, UI.screenWidth - 32f);
            anchor.y = Mathf.Clamp(anchor.y, 80f, UI.screenHeight - 130f);
            occluded = true;
            return true;
        }

        private static Rect CalculatePanelRect(Vector2 anchor)
        {
            // 固定边栏：贴屏幕左侧，完全静止（不跟随 Group 位置），与顶部游戏 UI 保持间距。
            // burn 执行时加高，容纳机动执行区。
            float h = PanelHeight;
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var grp = selectedGroupId != -1 ? spaceGame?.GetGroupById(selectedGroupId) : null;
            if (grp != null && grp.HasActiveBurn) h += BurnSectionHeight;
            if (grp != null && grp.HasApproachGuidance) h += ApproachSectionHeight;
            if (grp != null && grp.HasRendezvousGuidance && !grp.rendezvousIsApproach)
                h += SpaceGameComp.UseNewRendezvousLaw ? RendezvousSectionHeightNewLaw : RendezvousSectionHeight;
            return new Rect(24f, 80f, PanelWidth, h);
        }

        private static void DrawInfoPanel(SpaceGameComp spaceGame)
        {
            if (selectedGroupId == -1) return;

            var group = spaceGame.GetGroupById(selectedGroupId);
            if (group == null) return;
            if (!GetPanelAnchor(group, out Vector2 anchor, out bool occluded)) return;
            Rect rect = CalculatePanelRect(anchor);
            DrawPanelBackground(rect, occluded);

            float y = rect.y + 10f;
            DrawPanelHeader(group, rect, ref y);
            DrawPanelInfo(group, rect, ref y, spaceGame);
            if (group.HasActiveBurn)
                DrawPanelBurn(group, rect, ref y, spaceGame);
            if (group.HasRendezvousGuidance && !group.rendezvousIsApproach)
                DrawPanelRendezvous(group, rect, ref y, spaceGame);
            if (group.HasApproachGuidance)
                DrawPanelApproach(group, rect, ref y, spaceGame);
            DrawPanelActions(group, rect, ref y, spaceGame);
            DrawPanelMap(group, rect, ref y, spaceGame);
        }

        private static void DrawPanelBackground(Rect rect, bool occluded)
        {
            Color bg = occluded ? new Color(0.03f, 0.05f, 0.07f, 0.72f) : new Color(0.04f, 0.06f, 0.09f, 0.85f);
            Color border = occluded ? new Color(0.25f, 0.9f, 1f, 0.4f) : new Color(0.25f, 0.9f, 1f, 0.7f);
            Color accent = occluded ? new Color(0.25f, 0.9f, 1f, 0.35f) : new Color(0.25f, 0.9f, 1f, 0.8f);
            GUI.color = bg;
            Widgets.DrawBoxSolid(rect, GUI.color);
            GUI.color = border;
            Widgets.DrawBox(rect, 1);
            Widgets.DrawBoxSolid(new Rect(rect.x + 3f, rect.y + 4f, 3f, rect.height - 8f), accent);
            GUI.color = Color.white;
        }

        private static void DrawSectionTitle(Rect panel, ref float y, string title, Color accent)
        {
            Text.Font = GameFont.Tiny;
            GUI.color = accent;
            Widgets.Label(new Rect(panel.x + 12f, y, panel.width - 24f, 18f), title);
            Widgets.DrawLineHorizontal(panel.x + 12f, y + 18f, panel.width - 24f);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y += 28f;
        }

        private static void DrawPanelHeader(SpaceEntityGroup group, Rect panel, ref float y)
        {
            string status = group.HasActiveBurn ? "燃烧中"
                : group.plannedManeuvers.Count > 0 ? "机动待执行"
                : group.IsActive ? "运行中" : "离线";
            Color statusColor = group.HasActiveBurn ? WarnColor
                : group.plannedManeuvers.Count > 0 ? OkColor
                : group.IsActive ? OkColor : DimColor;

            Text.Font = GameFont.Medium;
            GUI.color = statusColor;
            Widgets.Label(new Rect(panel.x + 12f, y, panel.width - 24f, 28f),
                $"Group {group.GroupId}");
            GUI.color = Color.white;

            string typeInfo = group.HasGravityWell ? "引力源" : (group.IsMovable ? "可移动" : "静态");
            string orbitInfo = group.IsCircularOrbit ? "圆轨" : "积分";
            // [V1] 探针（步 1/2/3）：匿踪闸门 / 侦测技术 / 信号强度都还没有消费方，用面板读数充当
            // "存档往返存活"与"派生量是否正确"的可执行检查——设置 / 观测 / 存档 / 读档 / 再看一眼。
            // 单次 string.Format：面板是每帧绘制路径，原写法（再套一层 Format + 拼接）每帧多两次分配。
            Text.Font = GameFont.Tiny;
            GUI.color = DimColor;
            Widgets.Label(new Rect(panel.x + 12f, y + 30f, panel.width - 24f, 18f),
                $"{typeInfo} · {orbitInfo} · {status} · {(group.IsStealth ? "匿踪" : "公开")}{(group.ActivePingEnabled ? "/主动" : "/被动")} · 灵{group.DetectTech.sensitivityMeters / 1000f:F0}km/抗压{group.DetectTech.ecmResist:F1}/抗骗{group.DetectTech.eccmResist:F1} · S={group.SignalStrength:F3}");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y += 54f;

            // [V-2/裁定⑥] 认知与 EW 读数：相位 / σ / 受扰强度（"我知道什么、我被压到什么程度"）
            string? cog = CognitionLine(group);
            if (cog != null)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = DimColor;
                Widgets.Label(new Rect(panel.x + 12f, y - 8f, panel.width - 24f, 18f), cog);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                y += 14f;
            }
        }

        /// <summary>[V-2] 一条认知/EW 读数：相位（自有/公开/实况/影子/无接触）+ σ + 受扰。</summary>
        private static string? CognitionLine(SpaceEntityGroup group)
        {
            var sensor = sensorMgrRef.Get();
            var player = Faction.OfPlayer;
            if (sensor == null || player == null) return null;
            int fid = player.loadID;

            string phase;
            string sigma = "";
            if (group.FactionId == fid)
            {
                phase = "自有遥测";
            }
            else if (!group.IsStealth)
            {
                phase = "公开应答";
            }
            else
            {
                var ph = sensor.ContactPhaseOf(fid, group.GroupId);
                phase = ph == TrackPhase.Live ? "实况" : ph == TrackPhase.Shadow ? "影子" : "无接触";
                if (sensor.TryGetContact(fid, group.GroupId, out Contact c) && c.positionSigma > 0.5)
                    sigma = $" σ={c.positionSigma / 1000.0:F1}km";
            }

            string jam = "";
            if (sensor.TryGetJamInfo(group.GroupId, out SensorManager.JamInfo ji) && ji.jam > 0.0)
            {
                double ratio = ji.factorNoJam > 0.0 ? ji.factorWithJam / ji.factorNoJam : 1.0;
                jam = $" · 受扰 J={ji.jam:F2} 灵敏度{ratio:P0}";
            }

            // 主动雷达/波束读数（[E2]/B1，2026-10-11）：**"看不见波束锥"要能一眼分流** ——
            // 0°/0km = 这艘船没有可用的主动雷达（建筑投影为空）⇒ 世界视图里不会有锥（见 `[波束锥]` 日志）。
            string beam = "";
            if (group.ActivePingEnabled || group.RadarCap.beamHalfAngleDeg > 0f)
            {
                var rc = group.RadarCap;
                beam = $" · 波束 {rc.beamHalfAngleDeg:F0}°/{rc.beamRangeMeters / 1000f:F0}km{(group.ActivePingEnabled ? "（开机）" : "")}";
            }

            return $"认知: {phase}{sigma}{jam}{beam}";
        }

        private static void DrawPanelInfo(SpaceEntityGroup group, Rect panel, ref float y, SpaceGameComp spaceGame)
        {
            DrawSectionTitle(panel, ref y, "轨道信息", CyanColor);

            string centerInfo = "天体: ";
            if (group.CelestialBodyId >= 0)
            {
                var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
                var body = mgr?.GetBodyById(group.CelestialBodyId);
                centerInfo += body != null
                    ? $"{body.label}(ID={group.CelestialBodyId})"
                    : $"ID={group.CelestialBodyId}";
            }
            else
            {
                centerInfo += "深空";
            }

            DVector3 pos = group.CurrentOrbit.positionSystem.v;
            double radiusKm = pos.magnitude / 1000.0;
            double speed = group.IsCircularOrbit
                ? group.GetCircularVelocityAtTick(TimeScaleManager.GetPhysicsTickSafe()).magnitude
                : group.CurrentOrbit.velocitySystem.magnitude;
            var (remainingDv, maxDv, propMass, propCap) = spaceGame.GetGroupDeltaV(group);

            float rowH = 21f;
            float leftW = panel.width * 0.5f - 6f;
            float rightX = panel.x + panel.width * 0.5f;

            // 行1：天体 | 距中心
            Widgets.Label(new Rect(panel.x + 12f, y, leftW, rowH), centerInfo);
            GUI.color = DimColor;
            Widgets.Label(new Rect(rightX, y, panel.width * 0.5f - 12f, rowH),
                $"距中心 {radiusKm:F0} km");
            GUI.color = Color.white;
            y += rowH;

            // 行2：速度 | 位置
            Widgets.Label(new Rect(panel.x + 12f, y, leftW, rowH),
                $"速度 {speed:F0} m/s");
            GUI.color = DimColor;
            Widgets.Label(new Rect(rightX, y, panel.width * 0.5f - 12f, rowH),
                $"({pos.x / 1000.0:F0},{pos.y / 1000.0:F0},{pos.z / 1000.0:F0})");
            GUI.color = Color.white;
            y += rowH;

            // 行3：剩余 dV
            GUI.color = remainingDv > 0f ? Color.white : DimColor;
            Widgets.Label(new Rect(panel.x + 12f, y, panel.width - 24f, rowH),
                $"剩余 dV: {remainingDv:F0} / 最大 {maxDv:F0} m/s");
            GUI.color = Color.white;
            y += rowH;

            // 行4：工质
            Widgets.Label(new Rect(panel.x + 12f, y, panel.width - 24f, rowH),
                $"工质: {propMass:F0} / {propCap:F0} kg");
            y += rowH + 12f;
        }

        private static void DrawPanelBurn(SpaceEntityGroup group, Rect panel, ref float y, SpaceGameComp spaceGame)
        {
            DrawSectionTitle(panel, ref y, "机动执行中", WarnColor);

            double applied = group.appliedBurnDV;
            double planned = applied + group.remainingBurnDV;
            float frac = planned > 0.001 ? (float)(applied / planned) : 0f;
            Rect barRect = new Rect(panel.x + 12f, y, panel.width - 24f, 16f);
            Widgets.DrawBoxSolid(barRect, new Color(0.02f, 0.03f, 0.05f, 0.9f));
            float fillW = barRect.width * Mathf.Clamp01(frac);
            if (fillW > 2f)
                Widgets.DrawBoxSolid(new Rect(barRect.x, barRect.y, fillW, barRect.height), WarnColor);
            Widgets.DrawBox(barRect, 1);
            y += 22f;

            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(panel.x + 12f, y, panel.width - 24f, 18f),
                $"已消耗 dV: {applied:F1} / {planned:F1} m/s ({frac:P0}) · 工质 {group.burnConsumedPropellant:F1} kg");
            Text.Font = GameFont.Small;
            y += 24f;

            GUI.color = new Color(0.85f, 0.28f, 0.24f);
            if (Widgets.ButtonText(new Rect(panel.x + 12f, y, panel.width - 24f, 28f), "紧急终止当前机动"))
            {
                spaceGame.EmergencyStopBurn(group);
            }
            GUI.color = Color.white;
            y += 36f;
        }

        /// <summary>交会引导状态区：目标 / 状态 / 剩余 / 取消按钮（精确交会引导激活时显示）。
        /// 读数按**当前引擎**给：新律要的是相位差（它的状态量），旧范式要的是到达倒计时——
        /// 混着显示会出现"面板说还剩 3 小时，律根本不看这个数"的错配。</summary>
        private static void DrawPanelRendezvous(SpaceEntityGroup group, Rect panel, ref float y, SpaceGameComp spaceGame)
        {
            DrawSectionTitle(panel, ref y, "交会引导", CyanColor);

            var target = spaceGame.GetGroupById(group.rendezvousTargetId);
            string targetLabel = target != null
                ? $"Group {group.rendezvousTargetId}"
                : $"Group {group.rendezvousTargetId}（已消失）";
            double now = TimeScaleManager.GetPhysicsTickSafe();

            float rowH = 21f;
            Widgets.Label(new Rect(panel.x + 12f, y, panel.width - 24f, rowH),
                $"目标: {targetLabel}");
            y += rowH;

            if (SpaceGameComp.UseNewRendezvousLaw)
            {
                // ── 阶梯引导读数（批5.B 2026-09-28 改）：状态 / 半长轴进度 / 相位与相对状态 ──
                //   ⚠ **必须给"在干活"的证据**：交会要跑很多圈，面板只给一个"引导中"会让玩家以为"飞一半不干了"。
                //   ⚠ 旧律的"变轨段/相位段"与"本段剩 X/Y h"**已退役**（用户："相位让我们的 MJ 接管"）：
                //     那两处读的状态（`rendezvousLaw*`）只有旧入口会写 ⇒ 新路上它们恒为初值 = **撒谎读数**。
                Widgets.Label(new Rect(panel.x + 12f, y, panel.width - 24f, rowH),
                    $"状态: {(group.rendezvousArmed ? "引导中" : "待命")}");
                y += rowH;

                double aSelf = double.NaN, aTgt = double.NaN, phaseDeg = double.NaN, distKm = double.NaN, rel = double.NaN;
                if (target != null)
                {
                    aSelf = OrbitMath.SemiMajorAxis(group.positionBody, group.velocityBody,
                        spaceGame.GetBodyGM(group.CelestialBodyId));
                    DVector3 vTgtNow = target.velocityBody;
                    if (spaceGame.TryGetRendezvousReadout(group, target, out double phiDeg, out double dist, out double rv))
                    {
                        phaseDeg = phiDeg;      // 已是度（RendezvousGoal.SignedPhaseDeg）
                        distKm = dist / 1000.0;
                        rel = rv;
                    }
                    aTgt = OrbitMath.SemiMajorAxis(target.positionBody, vTgtNow,
                        spaceGame.GetBodyGM(group.CelestialBodyId));
                }

                GUI.color = DimColor;
                if (!double.IsNaN(aTgt))
                {
                    double remainA = (aTgt - aSelf) / 1000.0;
                    Widgets.Label(new Rect(panel.x + 12f, y, panel.width - 24f, rowH),
                        $"半长轴 {aSelf / 1000.0:F0} / {aTgt / 1000.0:F0} km（{(System.Math.Abs(remainA) < 1.0 ? "已对齐" : (remainA > 0 ? "待升 " : "待降 "))}{System.Math.Abs(remainA):F0} km）");
                }
                y += rowH;
                Widgets.Label(new Rect(panel.x + 12f, y, panel.width - 24f, rowH),
                    $"沿轨相位差 {phaseDeg:F1}°（+ = 目标在前）· 距离 {distKm:F0} km · 相对速度 {rel:F1} m/s");
                GUI.color = Color.white;
                y += rowH + 6f;
            }
            else
            {
                // ── 旧范式读数：等待出发段 / 到达倒计时 ──
                string state = group.rendezvousArmed ? "引导中" : "待命（等待出发段点火）";
                double remain = group.rendezvousArrivalTick - now;
                Widgets.Label(new Rect(panel.x + 12f, y, panel.width - 24f, rowH),
                    $"状态: {state}");
                y += rowH;
                GUI.color = DimColor;
                Widgets.Label(new Rect(panel.x + 12f, y, panel.width - 24f, rowH),
                    remain > 0
                        ? $"距到达约 {remain:F0} 秒（{remain / 60.0:F1} 分钟）"
                        : "已过到达时刻");
                GUI.color = Color.white;
                y += rowH + 6f;
            }

            GUI.color = WarnColor;
            if (Widgets.ButtonText(new Rect(panel.x + 12f, y, panel.width - 24f, 28f), "取消交会引导"))
                group.ClearRendezvousGuidance();
            GUI.color = Color.white;
            y += 36f;
        }

        /// <summary>近距离接近状态区：目标 / 距离 / 相对速度 / 停止按钮（接近引导激活时显示）。</summary>
        private static void DrawPanelApproach(SpaceEntityGroup group, Rect panel, ref float y, SpaceGameComp spaceGame)
        {
            DrawSectionTitle(panel, ref y, "近距离接近", OkColor);

            var target = spaceGame.GetGroupById(group.ApproachTargetId);
            string targetLabel = target != null
                ? $"Group {group.ApproachTargetId}"
                : $"Group {group.ApproachTargetId}（已消失）";
            double dist = double.NaN, relvel = double.NaN;
            if (target != null)
            {
                DVector3 dr = target.positionBody - group.positionBody;
                DVector3 dv = target.velocityBody - group.velocityBody;
                dist = dr.magnitude;
                relvel = dv.magnitude;
            }

            float rowH = 21f;
            Widgets.Label(new Rect(panel.x + 12f, y, panel.width - 24f, rowH),
                $"目标: {targetLabel}");
            y += rowH;

            GUI.color = DimColor;
            Widgets.Label(new Rect(panel.x + 12f, y, panel.width - 24f, rowH),
                double.IsNaN(dist)
                    ? "距离 / 相对速度: --"
                    : $"距离 {dist:F0} m · 相对速度 {relvel:F1} m/s");
            GUI.color = Color.white;
            y += rowH + 6f;

            GUI.color = WarnColor;
            if (Widgets.ButtonText(new Rect(panel.x + 12f, y, panel.width - 24f, 28f), "停止接近"))
                group.ClearApproachGuidance();
            GUI.color = Color.white;
            y += 36f;
        }

        private static void DrawPanelActions(SpaceEntityGroup group, Rect panel, ref float y, SpaceGameComp spaceGame)
        {
            DrawSectionTitle(panel, ref y, "行动", CyanColor);
            GUI.color = GrayButton; // 普通按钮统一灰色

            float btnW = (panel.width - 24f - 10f) * 0.5f;
            float rowH = 28f;
            float rowGap = 6f;
            float leftX = panel.x + 12f;
            float rightX = panel.x + 12f + btnW + 10f;

            // 行1：跟随
            string followLabel = isFollowing && selectedGroupId == group.GroupId ? "停止跟随" : "跟随";
            if (Widgets.ButtonText(new Rect(leftX, y, btnW, rowH), followLabel))
            {
                if (isFollowing && selectedGroupId == group.GroupId)
                    isFollowing = false;
                else
                {
                    selectedGroupId = group.GroupId;
                    isFollowing = true;
                    lastFollowTime = Time.time;
                }
            }
            y += rowH + rowGap;

            // 行2：圆轨/退出圆轨 | 机动规划
            if (group.IsCircularOrbit)
            {
                if (Widgets.ButtonText(new Rect(leftX, y, btnW, rowH), "退出圆轨"))
                    group.ExitCircularOrbit();
            }
            else
            {
                if (Widgets.ButtonText(new Rect(leftX, y, btnW, rowH), "圆轨 (消耗dV)"))
                {
                    var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
                    if (celestialMgr == null) return;
                    var bodies = celestialMgr.GetGravitySources();
                    if (bodies.Count == 0) return;
                    var options = new List<FloatMenuOption>();
                    foreach (var body in bodies)
                    {
                        string label = body.label;
                        if (body.bodyId == group.CelestialBodyId)
                            label += " (当前系统)";
                        int capturedId = body.bodyId;
                        options.Add(new FloatMenuOption(label, () =>
                        {
                            var sg = Current.Game?.GetComponent<SpaceGameComp>();
                            if (sg != null)
                                sg.BeginCircularize(group, capturedId);
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            }
            if (Widgets.ButtonText(new Rect(rightX, y, btnW, rowH), "机动规划"))
                Find.WindowStack.Add(new ManeuverPlanner(group));
            y += rowH + rowGap;

            // 行3：转移到圆轨
            // ⚠ 批5.B（2026-09-28）：本行左边原本是「交会」按钮 → 旧律的"选时间/燃料档"二级菜单
            //   （`OpenRendezvousLegMenu`）——那是**已作废自制律**的旋钮（腿长/螺旋价），按用户裁定
            //   **撤销**（原话："我们之前的冗余按钮就不需要了"）。交会入口现在只有「行7 · 交会（选目标）」。
            if (Widgets.ButtonText(new Rect(leftX, y, btnW, rowH), "转移到圆轨"))
                Find.WindowStack.Add(new OrbitTransferPlanner(group, spaceGame));
            y += rowH + rowGap;

            // 行4：近距离接近（目标选择 FloatMenu；接近中点击可重新选择/换目标）
            string approachLabel = group.HasApproachGuidance
                ? $"近距离接近中 → G{group.ApproachTargetId}"
                : "近距离接近";
            if (Widgets.ButtonText(new Rect(leftX, y, panel.width - 24f, rowH), approachLabel))
            {
                var candidates = spaceGame.GetAllGroups()
                    .Where(g => g.IsActive && g.GroupId != group.GroupId
                        && (g.positionBody - group.positionBody).magnitude < SpaceGameComp.ApproachMaxStartDistance)
                    .ToList();
                if (candidates.Count == 0)
                {
                    Messages.Message("附近（1000km 内）没有可接近的群组，距离太远建议用「交会（选目标）」", MessageTypeDefOf.RejectInput);
                }
                else
                {
                    var options = new List<FloatMenuOption>();
                    foreach (var c in candidates)
                    {
                        int capturedId = c.GroupId;
                        options.Add(new FloatMenuOption($"Group {c.GroupId}（{(c.positionBody - group.positionBody).magnitude / 1000.0:F0} km）", () =>
                        {
                            var sg = Current.Game?.GetComponent<SpaceGameComp>();
                            if (sg == null) return;
                            var grp = sg.GetGroupById(selectedGroupId); // 面板当前选中群组（最新引用）
                            if (grp == null) return;
                            HashSet<PropellantDef>? nets = grp.currentNetwork != null
                                ? new HashSet<PropellantDef> { grp.currentNetwork } : null;
                            grp.RegisterApproachGuidance(capturedId, nets);
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            }
            y += rowH + rowGap;

            // 行5：机动队列（全宽）
            string queueLabel = group.plannedManeuvers.Count > 0
                ? $"机动队列 ({group.plannedManeuvers.Count})"
                : "机动队列 (0)";
            if (Widgets.ButtonText(new Rect(leftX, y, panel.width - 24f, rowH), queueLabel))
                Find.WindowStack.Add(new ManeuverQueueWindow(group));
            y += rowH + rowGap;

            // 行6：主动降落（选 tile + 付 Δv；结算在 SpaceGameComp.TryLandAtTile）
            if (Widgets.ButtonText(new Rect(leftX, y, panel.width - 24f, rowH), "降落（选落点）"))
                Find.WindowStack.Add(new Window_LandingTarget(spaceGame, group));
            GUI.color = Color.white;
            y += rowH + rowGap;

            // 行7：交会（**独立入口**，2026-09-28 用户："把 MJ 的那套 UI 从机动节点下面迁移出来"）
            //   = 选目标 + 接合/断开，照 MJ 的 `MechJebModuleRendezvousAutopilotWindow` 的控件面。
            //   ✅ 批5.B（2026-09-28）：**前置已满足**（用户游戏内亲验"点击正常"）⇒ 旧律那串
            //   "时间/燃料档"按钮（`OpenRendezvousLegMenu`）已随之撤销，本行是交会**唯一**的玩家入口。
            if (Widgets.ButtonText(new Rect(leftX, y, panel.width - 24f, rowH), "交会（选目标）"))
                Find.WindowStack.Add(new Window_Rendezvous(spaceGame, group));
            y += rowH + 12f;
        }

        // 批5.B（2026-09-28）：此处原有 `OpenRendezvousLegMenu`（旧律的第二级菜单：选时间/燃料档，
        // 价目由 `SpaceGameComp.BuildRendezvousLawOptions` 现算）。它与那一族一起**撤销**——
        // 旧律已作废（用户 2026-09-22 裁定），而阶梯**没有"腿长"这个旋钮**（价格由它自己出）。
        // 要回看：git 历史 / `_backups/20260928-2315-pre-legacy-rendezvous-removal-459f7e2b1fc681fa/`。

        private static void DrawPanelMap(SpaceEntityGroup group, Rect panel, ref float y, SpaceGameComp spaceGame)        {
            var mapEntity = GetMapEntityForGroup(group);
            if (mapEntity == null) return;

            DrawSectionTitle(panel, ref y, "空间站地图", CyanColor);
            GUI.color = GrayButton; // 普通按钮统一灰色

            float btnW = (panel.width - 24f - 10f) * 0.5f;
            float rowH = 28f;
            float leftX = panel.x + 12f;
            float rightX = panel.x + 12f + btnW + 10f;

            // 进入空间站
            if (Widgets.ButtonText(new Rect(leftX, y, btnW, rowH), "进入空间站"))
                SpaceMapGenerator.EnterStation(mapEntity);

            // 取档/封存中 → 进度条；否则 取出/封存 按钮
            if (spaceGame.ActiveRestoreOperation != null && spaceGame.ActiveRestoreOperation.TargetEntity == mapEntity)
            {
                Rect progressRect = new Rect(rightX, y, btnW, rowH);
                float progress = spaceGame.ActiveRestoreOperation.Progress;
                Widgets.FillableBar(progressRect, progress, Texture2D.linearGrayTexture, Texture2D.whiteTexture, false);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(progressRect, $"取出中 {progress:P0}");
                Text.Anchor = TextAnchor.UpperLeft;
            }
            else if (spaceGame.ActiveArchiveOperation != null && spaceGame.ActiveArchiveOperation.TargetEntity == mapEntity)
            {
                Rect progressRect = new Rect(rightX, y, btnW, rowH);
                float progress = spaceGame.ActiveArchiveOperation.Progress;
                Widgets.FillableBar(progressRect, progress, Texture2D.linearGrayTexture, Texture2D.whiteTexture, false);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(progressRect, $"封存中 {progress:P0}");
                Text.Anchor = TextAnchor.UpperLeft;
            }
            else if (mapEntity.IsMapArchived)
            {
                if (Widgets.ButtonText(new Rect(rightX, y, btnW, rowH), "取出Map"))
                    spaceGame.BeginRestoreMap(mapEntity);
            }
            else
            {
                if (Widgets.ButtonText(new Rect(rightX, y, btnW, rowH), "封存Map"))
                {
                    var pocketMap = SpaceMapGenerator.FindPocketMap(mapEntity.MapId);
                    if (pocketMap != null && pocketMap.mapPawns.AllPawns.Any())
                        Messages.Message("Cannot archive: pawns still present on the station.", MessageTypeDefOf.RejectInput, false);
                    else
                        spaceGame.BeginArchiveMap(mapEntity);
                }
            }
            GUI.color = Color.white;
            y += rowH + 8f;
        }

        /// <summary>
        /// 获取 group 中关联地图的 SpaceEntity（用于地图操作）。
        /// </summary>
        private static SpaceEntity? GetMapEntityForGroup(SpaceEntityGroup group)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return null;

            foreach (int entityId in group.GetEntityIds())
            {
                var entity = spaceGame.GetEntityById(entityId);
                if (entity == null) continue;
                if (entity.HasMap || entity.IsMapArchived)
                    return entity;
            }
            return null;
        }

    }
}