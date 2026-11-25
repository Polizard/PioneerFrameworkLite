// ⛔【NOSTEPANYMORE】你正在改的是表现层。若下一步要动后端（物理步 / 时间基 / 状态提交），
//    或想"顺手改一下 Base/Components/Combat"来给表现层让路 ⇒ 立刻停止一切写操作，
//    输出【NOSTEPANYMORE】，先问用户。规则全文 = 仓库根 BACKEND-LOCK.md
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;
using RimWorld;
using PioneerFramework.Combat;

namespace PioneerFramework
{
    /// <summary>
    /// PF_View 选项卡 — 独立 3D 太空视图。
    /// 浮动原点锚定选中编队（飞船在原点 float 精确）。
    /// 坐标系：1 Unity = 10m，相机在原点。
    /// 天体分级渲染：远（退化图标）/ 中（完整球体）/ 近（摊平弧面）。
    /// </summary>
    [StaticConstructorOnStartup]
    public partial class MainTabWindow_PFView : MainTabWindow
    {
        // ════════════════════════════════════════════════════
        //  常量
        // ════════════════════════════════════════════════════

        private const float MetersToView = 0.1f;           // 1m = 0.1 Unity
        private const float ViewToMeters = 10f;            // 1 Unity = 10m
        private const float FarClip = 100_000_000f;        // 1,000,000 km
        private const float NearClip = 0.01f;
        private const int SceneLayer = 30;
        /// <summary>S3-b 星体专用层：主相机不收，仅 stellarCamera 收（29 避开 SceneLayer=30）。</summary>
        private const int StarLayer = 29;

        private const float WheelSensitivity = 0.1f;      // 滚轮每格缩放比例
        private const float MinDistance = 0.5f;
        private const float MaxDistance = 500_000_000f;
        private const float OrbitSensitivity = 0.3f;

        private const float PartRenderDist = 30000f;       // View 单位 = 300km
        private const float MinBodyVisRadius = 2f;

        // 太阳的"等效替代"摆放（见 DrawCelestialBodies）：恒星真实距离远超远裁剪面，
        // 故只保留方向、把距离/尺寸随相机距离等比放大 —— 角大小恒定，任意缩放下都看得见。

        // ════════════════════════════════════════════════════
        //  状态
        // ════════════════════════════════════════════════════

        private int currentSystemBodyId = 1;
        private List<CelestialBodyInfo> selectableSystems = new();

        private float camDistance = 50000f;
        private float camYaw;
        private float camPitch = 45f;
        private Vector3 camFocusOffset;

        private readonly PFViewUiDragState dragState = new();
        private bool isDraggingRight => dragState.Orbit;
        private Vector2 lastMousePos;

        private int selectedGroupId = -1;
        private PFViewUiTarget weaponTarget = PFViewUiTarget.None;
        // 保留实体目标的既有相机/调试入口；设定或清除实体目标时同时替换接触目标。
        private int targetGroupId { get => weaponTarget.GroupId; set => weaponTarget = new(value, -1); }


        // 调试窗口用的实例开关（2026-10-18 用户：调试选项放回 PFView）
        internal bool DebugMapRelief { get => debugMapRelief; set => debugMapRelief = value; }
        internal bool DebugSettlements { get => debugSettlements; set => debugSettlements = value; }

        internal static bool DebugScaleParts;    // 调试：放大所有渲染部件（PFView 调试窗 + DebugTabMenu_PFL 双入口）
        internal static bool DebugShowSunMarker; // 调试：太阳位置黄色标记（入口已移到 DebugTabMenu_PFL）
        private bool debugMapRelief = true; // 有 Map 的编队画"地图浮雕"（默认开；关掉即回到旧盒体，便于 A/B）
        private int cachedRenderTick;           // 缓存渲染 tick，避免 OnPostRender 中重复获取

        // 本帧被 UI 占用的矩形（顶栏按钮 / 调试勾选 / 信息面板）。
        // 用于把鼠标输入与"点选编队、右键旋转、滚轮缩放"隔开：否则点面板上的按钮会
        // 同时选中按钮底下那艘船并把相机拽回去（§HandleGUIEvents 顺序问题）。
        private readonly List<Rect> uiBlockRects = new();

        /// <summary>
        /// 本帧鼠标是否落在"3D 视口"上：在窗口内、不在自己的 UI 矩形上、且没有被更上层的窗口
        /// （浮动菜单 / 对话窗）盖住。三个判据都必要——只挡自己的矩形挡不住菜单：菜单画在本窗口之后，
        /// 点菜单那一下仍会被本窗口先看到。
        /// </summary>
        private bool MouseInViewport(Rect inRect)
        {
            var e = Event.current;
            if (e == null || !inRect.Contains(e.mousePosition)) return false;

            for (int i = 0; i < uiBlockRects.Count; i++)
                if (uiBlockRects[i].Contains(e.mousePosition)) return false;

            var top = Find.WindowStack?.GetWindowAt(e.mousePosition);
            return top == null || top == this;
        }

        // 第 12 条（2026-09-10）：本窗口每帧重建 id 集合（GetLocalSystemBodyIds + 三处内联
        // {currentSystemBodyId}+卫星）。天体/卫星在开局后不可变，故按"所属天体 id"记忆化即精确复用；
        // 调用方仅 Contains/遍历、不跨帧保留。
        private readonly HashSet<int> localSystemBodyIdsCache = new();
        private int localSystemBodyIdsCacheKey = int.MinValue;
        private readonly HashSet<int> currentSystemBodyIdsCache = new();
        private int currentSystemBodyIdsCacheKey = int.MinValue;

#if DEBUG
        // 射弹渲染连续检测：相邻帧弹头渲染位置跳变超阈值 → 提示（抓折线/集体闪现）
        private readonly Dictionary<int, DVector3> debugLastProjRender = new Dictionary<int, DVector3>();
        private readonly HashSet<int> debugLiveProjectileIds = new HashSet<int>();  // 帧末清理用（免字典无界增长）
        private readonly List<int> debugDeadProjIds = new List<int>();              // 清理暂存（复用，免每帧分配）
        private int debugJumpLogTick = -9999;
#endif

        // ════════════════════════════════════════════════════
        //  3D 资源
        // ════════════════════════════════════════════════════

        private Camera renderCamera = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        private Light sunLight = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        private Light? fillLight;         // 必须留引用：DontDestroyOnLoad 的补光 GameObject 不销毁就每次开窗漏一盏
        private RenderTexture renderTexture = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        private GameObject cameraObject = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        // S3-b 星体离屏（2026-11-20）：独立相机 + 半分辨率 RT；默认关闭时保持 null。
        private Camera? stellarCamera;
        private GameObject? stellarCameraObject;
        private RenderTexture? stellarRenderTexture;
        private bool resourcesCreated;

        private static Mesh sphereMeshLOD0 = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）;   // 高精：48×36
        private static Mesh sphereMeshLOD1 = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）;   // 中精：24×18
        private static Mesh sphereMeshLOD2 = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）;   // 低精：12×9
        private static Mesh cubeMesh = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        private static Mesh cubeMeshHard = null!;   // 棱边硬法线版（舰船蒙皮专用；旧路径仍用 cubeMesh 平滑法线）
        private static Mesh quadMesh = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        private static Material renderMat = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        private static MaterialPropertyBlock matBlock = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        // 三渲二 Toon Shader 懒加载（从 AssetBundle 加载，不可用时回退 Standard）
        private static Material _toonMat = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        private static bool _toonMatTried;
        private static Material ToonMat
        {
            get
            {
                if (!_toonMatTried)
                {
                    _toonMatTried = true;
                    // 团队调好的 PF_Toon 是**资产**（AssetBundle 里）——mod 的散装 Shaders/ 目录 RimWorld
                    // 不编译也不加载，故 Shader.Find 恒失败。先取资产包，再试 Shader.Find，最后回退 Standard。
                    Shader s = PFViewToonShader.TryGet() ?? Shader.Find("PioneerFramework/Toon");
                    if (s != null)
                    {
                        _toonMat = new Material(s);
                        // V-1.4（2026-10-02，用户："要渐变色、但渐变像阶梯一样分明"）：
                        // 明暗 = **N 级阶梯**（不是二分色块），描边加粗。这些是"调色"旋钮——
                        // 级数/阈值/软化改这里只需重发 DLL；**改 shader 算法才要重打 AssetBundle**。
                        _toonMat.SetFloat("_ShadowSteps", 4f);
                        _toonMat.SetFloat("_ShadowThreshold", 0.05f);
                        _toonMat.SetFloat("_ShadowSmoothness", 0.06f);
                        _toonMat.SetColor("_ShadowColor", new Color(0.12f, 0.12f, 0.18f, 1f));
                        _toonMat.SetColor("_OutlineColor", Color.black);
                        _toonMat.SetFloat("_OutlineWidth", 0.03f);
                        Log.Message("[PFView] Toon shader loaded: PioneerFramework/Toon");
                    }
                    else
                    {
                        Log.Message("[PFView] Toon shader not found, using Standard fallback"
                            + (string.IsNullOrEmpty(PFViewToonShader.Failure)
                                ? "" : "（原因：" + PFViewToonShader.Failure + "）"));
                    }
                }
                return _toonMat;
            }
        }

        // 太阳 billboard 材质：克隆 Standard + HDR 自发光

        static MainTabWindow_PFView()
        {
            InitStaticResources();
        }

        // ════════════════════════════════════════════════════
        //  MainTabWindow — 全屏视图模式
        // ════════════════════════════════════════════════════

        public MainTabWindow_PFView()
        {
            layer = WindowLayer.GameUI;
            doCloseButton = false;
            doCloseX = false;
            draggable = false;
            resizeable = false;
            // MainTabWindow 基类给的是 false（普通页签只占屏幕中间一块，地图相机该继续能动）。
            // 本页签是**全屏不透明**视图：再让地图相机跟着鼠标走，玩家一边看太空一边就把地图相机的
            // 位置拖到别处去了（返回地图时视角已漂）。故按全屏窗口的惯例挡住它（只有 CameraDriver/
            // WorldCameraDriver 读这个标志，不影响本视图自己的相机）。
            preventCameraMotion = true;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = false;
            closeOnAccept = false;
            closeOnCancel = false;
            soundAppear = null;
            soundClose = SoundDefOf.TabClose;
        }

        public override Vector2 RequestedTabSize => new(UI.screenWidth, UI.screenHeight - 35);

        public override MainTabWindowAnchor Anchor => MainTabWindowAnchor.Left;

        protected override float Margin => 0f;

        public override void PostOpen()
        {
            base.PostOpen();
            ResetUiSession();
            SteeringInputHost.Rearm();
            EnsureResources();
            // 2026-11-24 用户裁定：地表烘焙补丁缓存超过 7 天未用自动淘汰。
            // 打开 PFView 时请求一次（后台任务，不阻塞；每次烘焙成功后还会再请求一次）。
            PFViewSurfaceBake.RequestDiskSweep();
            // 2026-10-13（P2-b）：原挂 RenderPipelineManager.endCameraRendering 画轨道线；
            // 1.7 无该类型（且 GL 已退化为 2D）⇒ 轨道线改在 RenderScene 里以 mesh 提交，钩子整个删除。
        }

        public override void WindowUpdate()
        {
            base.WindowUpdate();
            if (!Application.isFocused) ResetCameraDrag();
            if (!resourcesCreated) EnsureResources();
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (!resourcesCreated) EnsureResources();
            ResolveUiContext();
            if (!SteeringController.IsOpen || FindSteeringWindow() == null)
                SteeringInputHost.Poll(HasInputFocus() && !HasTextInputFocus);

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr != null)
            {
                RenderScene(mgr);
                if (renderTexture != null)
                {
                    GUI.DrawTexture(inRect, renderTexture, ScaleMode.StretchToFill);
                    DrawAtmosphereHaze(inRect);   // 大气"物体雾"层：画面之上、HUD 之下（α = 天空 α × 0.3）
                }
            }
            else
            {
                // 只有"没有天体管理器"（未开局）才无可看之物。旧版把"未选中编队"也归进这个分支，
                // 于是刚打开页签就是一块黑屏：而相机取景（RestoreOrInitCamera 已按天体半径/FOV 退开）、
                // 渲染原点（GetRenderOrigin 回退到天体）、天体/轨道线绘制全都支持无选中状态 —— 那条
                // 路径从没被执行过。现在未选中编队 = 正常渲染当前星系视图。
                Widgets.DrawBoxSolid(inRect, new Color(0.005f, 0.005f, 0.03f));
                Text.Font = GameFont.Medium;
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.contentColor = Color.black;
                Widgets.Label(inRect, "无 信 号");
                GUI.contentColor = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;   // 与 Anchor 一起还原：字体是全局状态，不还原会污染后画的页签栏
            }

            // 顺序：先画覆盖层（控件自行消费落在自己身上的左键），再处理输入。
            // 旧顺序（先输入后画 UI）会让点面板按钮的那次点击**同时**触发"点选按钮底下那艘船"，
            // 并把相机拽回新选中的编队（initCameraForCurrentBody）。
            DrawOverlay(inRect);

            // 处理本帧输入（影响下一帧渲染）
            HandleGUIEvents(inRect);
            HandleInput(inRect);
        }

        public override void PreClose()
        {
            if (SteeringController.IsOpen && SteeringController.FromPFView) EndSteering();
            SaveCameraMemory();
            ResetUiSession();
            CleanupResources();
            base.PreClose();
        }
    }
}
