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
    public class MainTabWindow_PFView : MainTabWindow
    {
        // ════════════════════════════════════════════════════
        //  常量
        // ════════════════════════════════════════════════════

        private const float MetersToView = 0.1f;           // 1m = 0.1 Unity
        private const float ViewToMeters = 10f;            // 1 Unity = 10m
        private const float FarClip = 100_000_000f;        // 1,000,000 km
        private const float NearClip = 0.01f;
        private const int SceneLayer = 30;

        private const float MinPitch = 5f;
        private const float MaxPitch = 85f;
        private const float ScrollSensitivity = 0.1f;
        private const float MinDistance = 0.5f;
        private const float MaxDistance = 500_000_000f;
        private const float OrbitSensitivity = 0.3f;

        private const float PartRenderDist = 30000f;       // View 单位 = 300km
        private const float MinBodyVisRadius = 2f;

        // ════════════════════════════════════════════════════
        //  状态
        // ════════════════════════════════════════════════════

        private int currentSystemBodyId = 1;
        private List<CelestialBodyDef> selectableSystems = new List<CelestialBodyDef>();

        private float camDistance = 50000f;
        private float zoomLevel;                    // log2(camDistance), 每次滚轮 ±0.5
        private float camYaw;
        private float camPitch = 45f;
        private Vector3 camFocusOffset;

        private bool isDraggingRight;
        private Vector2 lastMousePos;

        private int selectedGroupId = -1;
        private int targetGroupId = -1;   // 手动标记的攻击目标（射手 = selectedGroupId）

        private bool debugScaleParts;    // 调试：放大所有渲染部件以观测远处飞船
        private bool debugShowSunMarker = true;  // 调试：在太阳位置显示黄色标记
        private int cachedRenderTick;           // 缓存渲染 tick，避免 OnPostRender 中重复获取

#if DEBUG
        // 射弹渲染连续检测：相邻帧弹头渲染位置跳变超阈值 → 提示（抓折线/集体闪现）
        private readonly Dictionary<int, DVector3> debugLastProjRender = new Dictionary<int, DVector3>();
        private int debugJumpLogTick = -9999;
#endif

        // ════════════════════════════════════════════════════
        //  3D 资源
        // ════════════════════════════════════════════════════

        private Camera renderCamera;
        private Light sunLight;
        private RenderTexture renderTexture;
        private GameObject cameraObject;
        private bool resourcesCreated;

        private static Mesh sphereMeshLOD0;   // 高精：48×36
        private static Mesh sphereMeshLOD1;   // 中精：24×18
        private static Mesh sphereMeshLOD2;   // 低精：12×9
        private static Mesh cubeMesh;
        private static Mesh quadMesh;

        private static Material renderMat;
        private static Material planetTerrainMat;  // 行星/卫星：RimWorld WorldTerrain shader
        private static MaterialPropertyBlock matBlock;

        // 三渲二 Toon Shader 懒加载（从 AssetBundle 加载，不可用时回退 Standard）
        private static Material _toonMat;
        private static bool _toonMatTried;
        private static Material ToonMat
        {
            get
            {
                if (!_toonMatTried)
                {
                    _toonMatTried = true;
                    Shader s = Shader.Find("PioneerFramework/Toon");
                    if (s != null)
                    {
                        _toonMat = new Material(s);
                        _toonMat.SetFloat("_ShadowThreshold", 0.4f);
                        _toonMat.SetFloat("_ShadowSmoothness", 0.02f);
                        _toonMat.SetColor("_ShadowColor", new Color(0.12f, 0.12f, 0.18f, 1f));
                        _toonMat.SetColor("_OutlineColor", Color.black);
                        _toonMat.SetFloat("_OutlineWidth", 0.02f);
                        Log.Message("[PFView] Toon shader loaded: PioneerFramework/Toon");
                    }
                    else
                    {
                        Log.Message("[PFView] Toon shader not found, using Standard fallback");
                    }
                }
                return _toonMat;
            }
        }

        // 太阳 billboard 材质：克隆 Standard + HDR 自发光
        private static Material _sunBillboardMat;
        private static Material SunBillboardMat
        {
            get
            {
                if (_sunBillboardMat == null && renderMat != null)
                {
                    _sunBillboardMat = new Material(renderMat);
                    _sunBillboardMat.EnableKeyword("_EMISSION");
                    _sunBillboardMat.SetColor("_EmissionColor", new Color(8f, 6f, 3f));
                    _sunBillboardMat.SetColor("_Color", new Color(1f, 1f, 0.9f));
                    _sunBillboardMat.SetFloat("_Glossiness", 0f);
                    _sunBillboardMat.SetFloat("_Metallic", 0f);
                }
                return _sunBillboardMat;
            }
        }

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
            preventCameraMotion = false;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = false;
            closeOnAccept = false;
            soundAppear = null;
            soundClose = SoundDefOf.TabClose;
        }

        public override Vector2 RequestedTabSize => new Vector2(UI.screenWidth, UI.screenHeight - 35);

        public override MainTabWindowAnchor Anchor => MainTabWindowAnchor.Left;

        protected override float Margin => 0f;

        public override void PostOpen()
        {
            base.PostOpen();
            EnsureResources();
            RenderPipelineManager.endCameraRendering += OnPostRender;
        }

        public override void WindowUpdate()
        {
            base.WindowUpdate();
            if (!resourcesCreated) EnsureResources();
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (!resourcesCreated) EnsureResources();

            if (selectedGroupId >= 0)
            {
                UpdateCamera();
                RenderScene();
                if (renderTexture != null)
                    GUI.DrawTexture(inRect, renderTexture, ScaleMode.StretchToFill);
            }
            else
            {
                Widgets.DrawBoxSolid(inRect, new Color(0.005f, 0.005f, 0.03f));
                Text.Font = GameFont.Medium;
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.contentColor = Color.black;
                Widgets.Label(inRect, "无 信 号");
                GUI.contentColor = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
            }

            // 2. 渲染纹理之上画 UI 覆盖层
            HandleGUIEvents(inRect);
            DrawOverlay(inRect);

            // 3. 处理本帧输入（影响下一帧渲染）
            HandleInput();
        }

        public override void PreClose()
        {
            RenderPipelineManager.endCameraRendering -= OnPostRender;
            CleanupResources();
            base.PreClose();
        }

        // ════════════════════════════════════════════════════
        //  静态资源初始化
        // ════════════════════════════════════════════════════

        private static void InitStaticResources()
        {
            sphereMeshLOD0 = BuildSphereMesh(48, 36);
            sphereMeshLOD1 = BuildSphereMesh(24, 18);
            sphereMeshLOD2 = BuildSphereMesh(12, 9);
            cubeMesh = BuildCubeMesh();
            quadMesh = BuildQuadMesh();

            renderMat = new Material(Shader.Find("Standard")) { color = Color.white };
            renderMat.SetFloat("_Glossiness", 0f);
            renderMat.SetFloat("_Metallic", 0f);    // 纯漫反射，依赖方向光产生明暗

            // 行星/卫星材质：使用 RimWorld 世界渲染 shader + 地表纹理
            planetTerrainMat = new Material(ShaderDatabase.WorldTerrain);
            var planetTex = ContentFinder<Texture2D>.Get("World/Biomes/IceSheetOcean");
            if (planetTex != null)
                planetTerrainMat.mainTexture = planetTex;

            matBlock = new MaterialPropertyBlock();
        }

        private static Mesh BuildSphereMesh(int lats, int longs)
        {
            var m = new Mesh();
            int verts = (lats + 1) * (longs + 1);
            var v = new Vector3[verts];
            var n = new Vector3[verts];
            var uv = new Vector2[verts];
            var t = new int[lats * longs * 6];
            int vi = 0;
            for (int lat = 0; lat <= lats; lat++)
            {
                float phi = Mathf.PI * lat / lats;
                float y = Mathf.Cos(phi);
                float r = Mathf.Sin(phi);
                for (int lon = 0; lon <= longs; lon++)
                {
                    float theta = 2f * Mathf.PI * lon / longs;
                    Vector3 p = new Vector3(r * Mathf.Cos(theta), y, r * Mathf.Sin(theta));
                    v[vi] = p * 0.5f;
                    n[vi] = p;
                    uv[vi] = new Vector2((float)lon / longs, (float)lat / lats);
                    vi++;
                }
            }
            int ti = 0;
            for (int lat = 0; lat < lats; lat++)
            {
                for (int lon = 0; lon < longs; lon++)
                {
                    int a = lat * (longs + 1) + lon;
                    int b = a + longs + 1;
                    t[ti++] = a; t[ti++] = a + 1; t[ti++] = b;
                    t[ti++] = a + 1; t[ti++] = b + 1; t[ti++] = b;
                }
            }
            m.vertices = v; m.normals = n; m.uv = uv; m.triangles = t;
            return m;
        }

        private static Mesh BuildQuadMesh()
        {
            var m = new Mesh();
            m.vertices = new[] {
                new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0),
                new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0)
            };
            m.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            m.RecalculateNormals();
            return m;
        }

        /// <summary>单位立方体 Mesh（替代已删除的 ShipPartMeshGenerator，单 part 船统一盒体绘制）。</summary>
        private static Mesh BuildCubeMesh()
        {
            var m = new Mesh();
            m.vertices = new[] {
                new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f)
            };
            m.triangles = new[] {
                0, 2, 1, 0, 3, 2,       // -Z
                4, 5, 6, 4, 6, 7,       // +Z
                0, 1, 5, 0, 5, 4,       // -Y
                2, 3, 7, 2, 7, 6,       // +Y
                1, 2, 6, 1, 6, 5,       // +X
                3, 0, 4, 3, 4, 7        // -X
            };
            m.RecalculateNormals();
            return m;
        }

        // ════════════════════════════════════════════════════
        //  运行时资源
        // ════════════════════════════════════════════════════

        private void EnsureResources()
        {
            if (resourcesCreated) return;

            if (Find.Camera != null)
                Find.Camera.cullingMask &= ~(1 << SceneLayer);

            cameraObject = new GameObject("PFViewCamera");
            UnityEngine.Object.DontDestroyOnLoad(cameraObject);
            renderCamera = cameraObject.AddComponent<Camera>();
            renderCamera.clearFlags = CameraClearFlags.SolidColor;
            renderCamera.backgroundColor = new Color(0.005f, 0.005f, 0.03f);
            renderCamera.cullingMask = 1 << SceneLayer;
            renderCamera.orthographic = false;
            renderCamera.fieldOfView = 45f;
            renderCamera.nearClipPlane = NearClip;
            renderCamera.farClipPlane = FarClip;

            int rw = Mathf.Max(1, UI.screenWidth);
            int rh = Mathf.Max(1, UI.screenHeight);
            renderTexture = new RenderTexture(rw, rh, 24);
            renderCamera.targetTexture = renderTexture;

            // 方向光独立于相机，避免 parent 层级干扰世界空间旋转
            var lightObj = new GameObject("PFViewSun");
            UnityEngine.Object.DontDestroyOnLoad(lightObj);
            sunLight = lightObj.AddComponent<Light>();
            sunLight.type = LightType.Directional;
            sunLight.intensity = 5f;
            sunLight.color = new Color(1f, 0.98f, 0.9f);

            var light2 = new GameObject("PFViewFill");
            UnityEngine.Object.DontDestroyOnLoad(light2);
            var fillLight = light2.AddComponent<Light>();
            fillLight.type = LightType.Directional;
            fillLight.intensity = 0.05f;
            fillLight.color = new Color(0.5f, 0.6f, 1f);

            BuildSystemList();
            InitCameraForCurrentBody();
            resourcesCreated = true;
        }

        private void CleanupResources()
        {
            if (renderTexture != null) { renderTexture.Release(); UnityEngine.Object.Destroy(renderTexture); renderTexture = null; }
            if (sunLight != null) { UnityEngine.Object.Destroy(sunLight.gameObject); sunLight = null; }
            if (cameraObject != null) { UnityEngine.Object.Destroy(cameraObject); cameraObject = null; }
            renderCamera = null;
            resourcesCreated = false;
        }

        private void BuildSystemList()
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return;
            selectableSystems.Clear();
            var star = mgr.GetStar();
            if (star != null) selectableSystems.Add(star);
            foreach (var b in mgr.GetAllBodies())
                if (b.bodyType == CelestialBodyType.Planet)
                    selectableSystems.Add(b);
        }

        // ════════════════════════════════════════════════════
        //  相机 — 浮动原点，物体位置均相对于渲染原点
        // ════════════════════════════════════════════════════

        /// <summary>计算渲染原点（所有物体的 view-space 位置都相对此点）</summary>
        private DVector3 GetRenderOrigin(double? tickOverride = null)
        {
            double tick = tickOverride ?? TimeScaleManager.GetPhysicsTickSafe();

            if (selectedGroupId >= 0)
            {
                var g = GetSelectedGroup(tick);
                if (g != DVector3.zero)
                    return g;
            }

            return GetBodyWorldPos(currentSystemBodyId, tick);
        }

        /// <summary>获取选中编队的世界坐标（tick 时刻），无选中返回 zero</summary>
        private DVector3 GetSelectedGroup(double tick)
        {
            if (selectedGroupId < 0) return DVector3.zero;
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return DVector3.zero;

            var g = spaceGame.GetGroupById(selectedGroupId);
            if (g == null) { selectedGroupId = -1; return DVector3.zero; }

            return GetGroupAbsoluteWorldPos(g, tick);
        }

        /// <summary>相机在 view space 中的位置（相对于原点）</summary>
        private Vector3 GetCameraViewPos()
        {
            float yR = camYaw * Mathf.Deg2Rad;
            float pR = camPitch * Mathf.Deg2Rad;
            float cP = Mathf.Cos(pR);
            Vector3 orbitDir = new Vector3(cP * Mathf.Sin(yR), Mathf.Sin(pR), cP * Mathf.Cos(yR));
            return camFocusOffset + orbitDir * camDistance;
        }

        /// <summary>
        /// 将编队的天体-相对坐标转换为绝对世界坐标。
        /// GetCircularPositionAtTick / CurrentOrbit.position 返回天体-相对坐标
        /// （对 Planet：相对于 Planet 中心；对 Moon：相对于 Planet 中心）。
        /// 天体绝对位置通过 GetBodyWorldPosition 递归获取。
        /// </summary>

        private DVector3 GetGroupAbsoluteWorldPos(SpaceEntityGroup g, double tick)
        {
            DVector3 localPos = g.IsCircularOrbit
                ? g.GetCircularPositionAtTick(tick)
                : g.CurrentOrbit.position;

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return localPos;

            // 找到编队所属的顶层行星
            var body = mgr.GetBodyById(g.CelestialBodyId);
            int planetBodyId = -1;
            if (body != null)
            {
                if (body.bodyType == CelestialBodyType.Planet)
                    planetBodyId = body.bodyId;
                else if (body.bodyType == CelestialBodyType.Moon && body.ParentBodyId >= 0)
                    planetBodyId = body.ParentBodyId;
            }

            DVector3 planetWorld = planetBodyId >= 0 ? mgr.GetBodyWorldPosition(planetBodyId, tick) : DVector3.zero;
            DVector3 worldPos = localPos + planetWorld;

            return worldPos;
        }

        /// <summary>获取天体世界坐标</summary>
        private static DVector3 GetBodyWorldPos(int bodyId, double tick)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            return mgr != null ? mgr.GetBodyWorldPosition(bodyId, tick) : DVector3.zero;
        }

        /// <summary>重置相机：无编队时退到天体 2 倍半径处，有编队时退到 500m 处</summary>
        private void InitCameraForCurrentBody()
        {
            camFocusOffset = Vector3.zero;
            camYaw = 0f;
            camPitch = 45f;

            if (selectedGroupId >= 0)
            {
                camDistance = 500f;
                return;
            }

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return;
            var body = mgr.GetBodyById(currentSystemBodyId);
            if (body != null)
            {
                camDistance = (float)(body.radius * MetersToView) * 2f;
                zoomLevel = Mathf.Log(camDistance, 2f);
            }
        }

        private static Vector3 WorldToView(DVector3 worldMeters, DVector3 originMeters)
        {
            return (Vector3)((worldMeters - originMeters) * MetersToView);
        }

        /// <summary>视锥剔除：球体是否在相机视野内</summary>
        private bool SphereInFrustum(Vector3 viewPos, float radius)
        {
            if (renderCamera == null) return true;

            // 转相机空间（相机朝向 +Z）
            Vector3 cs = renderCamera.transform.InverseTransformPoint(viewPos);
            float r = Mathf.Max(radius, 0.01f);

            // 远近裁剪面
            if (cs.z + r < renderCamera.nearClipPlane || cs.z - r > renderCamera.farClipPlane)
                return false;

            // 水平/垂直 FOV 裁剪
            float halfFov = renderCamera.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float tanH = Mathf.Tan(halfFov);
            float tanV = tanH / renderCamera.aspect;

            float hLimit = cs.z * tanH + r / Mathf.Max(Mathf.Cos(halfFov), 0.001f);
            float vLimit = cs.z * tanV + r / Mathf.Max(Mathf.Cos(halfFov), 0.001f);

            if (Mathf.Abs(cs.x) > hLimit || Mathf.Abs(cs.y) > vLimit)
                return false;

            return true;
        }

        private void HandleInput()
        {
            var e = Event.current;
            if (e == null) return;

            if (e.type == EventType.ScrollWheel)
            {
                camDistance = Mathf.Clamp(camDistance * (1f - e.delta.y * 0.1f), MinDistance, MaxDistance);
                e.Use();
            }
            // 右键拖拽 = 旋转
            if (e.type == EventType.MouseDown && e.button == 1) { isDraggingRight = true; lastMousePos = e.mousePosition; e.Use(); }
            if (e.type == EventType.MouseUp && e.button == 1) { isDraggingRight = false; e.Use(); }
            if (isDraggingRight && e.type == EventType.MouseDrag && e.button == 1)
            {
                Vector2 d = e.mousePosition - lastMousePos;
                camYaw -= d.x * OrbitSensitivity;
                camPitch = Mathf.Clamp(camPitch - d.y * OrbitSensitivity, MinPitch, MaxPitch);
                lastMousePos = e.mousePosition; e.Use();
            }
        }

        private void UpdateCamera()
        {
            if (renderCamera == null) return;
            Vector3 camViewPos = GetCameraViewPos();
            renderCamera.transform.position = camViewPos;
            renderCamera.transform.LookAt(camFocusOffset, Vector3.up);
            renderCamera.farClipPlane = FarClip;
        }

        /// <summary>每帧更新方向光，使其从恒星方向照射到行星</summary>
        private void UpdateLighting(CelestialBodyManager mgr, double tick, DVector3 origin)
        {
            if (sunLight == null) return;

            var star = mgr.GetStar();
            if (star == null) return;

            // 找到飞船所在的行星（不是飞船本身的位置！）
            int shipBodyId = selectedGroupId >= 0 ? GetSelectedGroupBodyId() : currentSystemBodyId;
            var shipBody = mgr.GetBodyById(shipBodyId);
            CelestialBodyDef planetBody = null;
            if (shipBody != null)
            {
                if (shipBody.bodyType == CelestialBodyType.Planet)
                    planetBody = shipBody;
                else if (shipBody.ParentBodyId >= 0)
                    planetBody = mgr.GetBodyById(shipBody.ParentBodyId);
            }
            if (planetBody == null) return;

            // 行星世界坐标 = 从恒星指向行星的矢量（恒星在(0,0,0)）
            DVector3 planetWorldPos = mgr.GetBodyWorldPosition(planetBody.bodyId, tick);

            // 方向光从恒星照向行星：forward = 恒星→行星方向
            Vector3 lightDir = new Vector3(
                (float)planetWorldPos.x,
                (float)planetWorldPos.y,
                (float)planetWorldPos.z).normalized;

            if (lightDir.sqrMagnitude > 0.001f)
                sunLight.transform.forward = lightDir;
        }

        // ════════════════════════════════════════════════════
        //  场景渲染
        // ════════════════════════════════════════════════════

        private void RenderScene()
        {
            if (renderCamera == null || renderTexture == null) return;

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            double tick = mgr != null ? TimeScaleManager.GetPhysicsTickSafe() : 0;
            cachedRenderTick = (int)Math.Round(tick); // int 缓存仅作 OnPostRender 轨道线采样/键，天体位置一律用 double tick
            DVector3 origin = GetRenderOrigin(tick);

            if (mgr != null)
            {
                UpdateLighting(mgr, tick, origin);
                DrawAllObjects(origin, mgr, tick);
            }
            else
            {
                Log.Warning("[PFView] CelestialBodyManager is null!");
            }

            renderCamera.Render();
        }

        private void DrawAllObjects(DVector3 origin, CelestialBodyManager mgr, double tick)
        {
            DrawCelestialBodies(mgr, tick, origin);
            DrawSystemGroups(mgr, tick, origin);
            DrawProjectiles(origin);
        }

        /// <summary>计算本行星系应渲染的天体 ID 集合（恒星 + 飞船所在行星 + 其卫星）</summary>
        private HashSet<int> GetLocalSystemBodyIds(CelestialBodyManager mgr)
        {
            var ids = new HashSet<int>();

            // 找到飞船所在天体
            int shipBodyId = selectedGroupId >= 0 ? GetSelectedGroupBodyId() : currentSystemBodyId;
            var shipBody = mgr.GetBodyById(shipBodyId);

            // 找到顶层恒星
            var star = mgr.GetStar();
            if (star != null) ids.Add(star.bodyId);

            if (shipBody != null)
            {
                if (shipBody.bodyType == CelestialBodyType.Planet)
                {
                    // 飞船绕行星 → 显示该行星 + 其卫星
                    ids.Add(shipBody.bodyId);
                    foreach (var moon in shipBody.moonBodies)
                        ids.Add(moon.bodyId);
                }
                else if (shipBody.bodyType == CelestialBodyType.Moon && shipBody.ParentBodyId >= 0)
                {
                    // 飞船绕卫星 → 显示父行星 + 该行星所有卫星
                    ids.Add(shipBody.ParentBodyId);
                    var parentPlanet = mgr.GetBodyById(shipBody.ParentBodyId);
                    if (parentPlanet != null)
                        foreach (var moon in parentPlanet.moonBodies)
                            ids.Add(moon.bodyId);
                }
            }

            return ids;
        }

        private int GetSelectedGroupBodyId()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return currentSystemBodyId;
            var g = spaceGame.GetGroupById(selectedGroupId);
            return g != null ? g.CelestialBodyId : currentSystemBodyId;
        }


        private void DrawCelestialBodies(CelestialBodyManager mgr, double tick, DVector3 origin)
        {
            var relevantIds = GetLocalSystemBodyIds(mgr);
            Vector3 camPos = GetCameraViewPos();

            foreach (var body in mgr.GetAllBodies())
            {
                if (!relevantIds.Contains(body.bodyId)) continue;

                bool isStar = body.bodyType == CelestialBodyType.Star;
                DVector3 wp = mgr.GetBodyWorldPosition(body.bodyId, tick);
                Vector3 vp = WorldToView(wp, origin);

                // ═══ 恒星：等效替代，方向对即可 ═══
                if (isStar)
                {
                    const float SunPlaceDist = 5000f;
                    const float SunPlaceSize = 300f;

                    Vector3 sunDir = vp.normalized;
                    if (sunDir.sqrMagnitude < 0.001f) sunDir = Vector3.forward;
                    Vector3 sunRenderPos = sunDir * SunPlaceDist;

                    if (SunBillboardMat != null)
                    {
                        // 发光球体（大，保证可见）
                        Matrix4x4 sphereMtx = Matrix4x4.TRS(sunRenderPos, Quaternion.identity,
                            Vector3.one * SunPlaceSize * 2f);
                        Graphics.DrawMesh(sphereMeshLOD2, sphereMtx, SunBillboardMat, SceneLayer);

                        // billboard 叠加（朝向相机，RW 风格）
                        Vector3 toCam = (camPos - sunRenderPos).normalized;
                        if (toCam.sqrMagnitude < 0.0001f) toCam = Vector3.forward;
                        Quaternion sunRot = Quaternion.LookRotation(toCam, Vector3.up);
                        Matrix4x4 sunMtx = Matrix4x4.TRS(sunRenderPos, sunRot, Vector3.one * SunPlaceSize);
                        Graphics.DrawMesh(quadMesh, sunMtx, SunBillboardMat, SceneLayer);
                    }

                    // ── 调试：在太阳位置放黄色大方块 ──
                    if (debugShowSunMarker && renderMat != null)
                    {
                        MaterialPropertyBlock dbgBlock = new MaterialPropertyBlock();
                        dbgBlock.SetColor("_Color", new Color(1f, 1f, 0f));
                        Matrix4x4 dbgMtx = Matrix4x4.TRS(sunRenderPos, Quaternion.identity,
                            Vector3.one * 800f);
                        Graphics.DrawMesh(cubeMesh, dbgMtx, renderMat, SceneLayer, null, 0, dbgBlock);
                    }
                    continue;
                }

                // ═══ 行星/卫星：实际位置渲染 ═══
                float bodyR = (float)(body.radius * MetersToView);

                Color c = body.bodyType == CelestialBodyType.Planet ?
                    new Color(0.2f, 0.5f, 0.8f) : new Color(0.7f, 0.7f, 0.7f);

                // 天体不自转，仅依赖方向光从恒星方向照射呈现明暗面
                Quaternion selfRot = Quaternion.identity;

                // 相机到天体的真实方向与距离
                Vector3 dirFromCam = (vp - camPos).normalized;
                float camDistToBody = Vector3.Distance(vp, camPos);

                Vector3 renderPos;
                float renderR;

                if (camDistToBody < FarClip * 0.5f)
                {
                    // 在裁剪面内：直接渲染真实位置
                    renderPos = vp;
                    renderR = bodyR;
                }
                else
                {
                    // 在裁剪面外：沿相机方向挪到 FarClip 内侧，等比缩放保持角大小
                    float placeDist = FarClip * 0.5f;
                    renderPos = camPos + dirFromCam * placeDist;
                    renderR = bodyR * (placeDist / camDistToBody);
                }

                float cullR = Mathf.Max(renderR, MinBodyVisRadius);
                if (!SphereInFrustum(renderPos, cullR))
                    continue;

                // LOD 选择：根据天体在屏幕上的角大小决定网格精度
                float camDistToRender = Vector3.Distance(renderPos, camPos);
                float angularSize = renderR / Mathf.Max(camDistToRender, 0.01f);
                Mesh lodMesh = angularSize > 0.15f ? sphereMeshLOD0 :
                               angularSize > 0.05f ? sphereMeshLOD1 :
                                                     sphereMeshLOD2;

                DrawCelestialSphere(renderPos, renderR, c, selfRot, lodMesh, renderMat, true);
            }
        }

        /// <summary>天体球体渲染（三渲二：Toon Shader 内置轮廓线 + 二分阴影，不可用时回退）</summary>
        private void DrawCelestialSphere(Vector3 pos, float radius, Color c, Quaternion selfRot, Mesh mesh, Material mat, bool drawOutline)
        {
            float r = Mathf.Max(radius, MinBodyVisRadius);

            if (ToonMat != null && drawOutline)
            {
                // Toon Shader：轮廓线由 Shader 的 OUTLINE Pass 自动处理，仅需一次 DrawMesh
                Matrix4x4 mtx = Matrix4x4.TRS(pos, selfRot, Vector3.one * r * 2f);
                matBlock.SetColor("_Color", c);
                matBlock.SetFloat("_OutlineWidth", 0.02f); // 恢复默认轮廓线宽度
                Graphics.DrawMesh(mesh, mtx, ToonMat, SceneLayer, null, 0, matBlock);
                return;
            }

            // 回退：双层绘制模拟轮廓线
            if (drawOutline)
            {
                Matrix4x4 outlineMtx = Matrix4x4.TRS(pos, selfRot, Vector3.one * r * 2f * 1.05f);
                matBlock.SetColor("_Color", Color.black);
                Graphics.DrawMesh(mesh, outlineMtx, renderMat, SceneLayer, null, 0, matBlock);
            }

            Matrix4x4 mtx2 = Matrix4x4.TRS(pos, selfRot, Vector3.one * r * 2f);
            matBlock.SetColor("_Color", c);
            Graphics.DrawMesh(mesh, mtx2, mat, SceneLayer, null, 0, matBlock);
        }

        private void DrawSystemGroups(CelestialBodyManager mgr, double tick, DVector3 origin)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;

            var sysIds = new HashSet<int> { currentSystemBodyId };
            var bodyDef = mgr.GetBodyById(currentSystemBodyId);
            if (bodyDef != null)
                foreach (var m in bodyDef.moonBodies)
                    sysIds.Add(m.bodyId);

            int totalGroups = 0, activeCount = 0, sysMatchCount = 0, partsRendered = 0, markerRendered = 0;
            float minDist = float.MaxValue, maxDist = 0f;

            foreach (var g in spaceGame.GetAllGroups())
            {
                totalGroups++;
                if (!g.IsActive) continue;
                activeCount++;
                if (!sysIds.Contains(g.CelestialBodyId)) continue;
                sysMatchCount++;

                DVector3 gWorldMeters = GetGroupAbsoluteWorldPos(g, tick);
                Vector3 vp = WorldToView(gWorldMeters, origin);
                float dist = vp.magnitude;

                if (dist > 0.01f)
                {
                    if (dist < minDist) minDist = dist;
                    if (dist > maxDist) maxDist = dist;
                }

                if (dist < PartRenderDist)
                {
                    partsRendered++;
                    DrawGroupParts(g, gWorldMeters, origin, spaceGame);
                }
                else if (SphereInFrustum(vp, 30f))
                {
                    markerRendered++;
                    // Billboard 面片：始终朝向相机，尺寸随距离增大保持屏幕可见
                    Quaternion rot = Quaternion.LookRotation(renderCamera.transform.forward, renderCamera.transform.up);
                    float s = Mathf.Clamp(dist * 0.005f, 10f, 500f);
                    Matrix4x4 mtx = Matrix4x4.TRS(vp, rot, Vector3.one * s);
                    matBlock.SetColor("_Color", new Color(0.3f, 0.9f, 1.0f, 0.9f));
                    Graphics.DrawMesh(quadMesh, mtx, renderMat, SceneLayer, null, 0, matBlock);
                }
            }

        }

        private void DrawGroupParts(SpaceEntityGroup group, DVector3 groupWorldMeters, DVector3 origin, SpaceGameComp spaceGame)
        {
            DVector3 vel = group.CurrentOrbit.velocity;
            Vector3 fwd = vel.sqrMagnitude > 0.001 ? ((Vector3)vel.normalized) : Vector3.forward;
            Quaternion gRot = Quaternion.LookRotation(fwd, Vector3.up);
            float scale = debugScaleParts ? 200f : 1f;

            foreach (int eid in group.GetEntityIds())
            {
                var entity = spaceGame.GetEntityById(eid);
                if (entity == null) continue;

                DVector3 eWorld = groupWorldMeters + entity.Position;
                Vector3 eVp = WorldToView(eWorld, origin);

                if (!SphereInFrustum(eVp, 10f * scale)) continue;

                // 单 part 化：无设计数据分支，统一按运行时 Parts 绘制盒体（§5.4 C4）
                if (entity.Parts.Count > 0)
                    DrawRuntimeParts(entity, eVp, gRot, scale);
            }
        }

        private static void DrawRuntimeParts(SpaceEntity entity, Vector3 eVp, Quaternion gRot, float scale = 1f)
        {
            Material partMat = ToonMat ?? renderMat;
            if (ToonMat != null)
                matBlock.SetFloat("_OutlineWidth", 0f); // 飞船部件不需要轮廓线

            foreach (var part in entity.Parts)
            {
                if (part.isDestroyed) continue;

                var mesh = cubeMesh;

                Vector3 pv = (Vector3)part.localPosition * MetersToView * scale;
                Vector3 sv = (Vector3)part.localBoundsSize * MetersToView * scale;
                if (sv.x < 0.1f) sv.x = 0.1f;
                if (sv.y < 0.1f) sv.y = 0.1f;
                if (sv.z < 0.1f) sv.z = 0.1f;

                Vector3 wp = eVp + gRot * pv;
                Matrix4x4 mtx = Matrix4x4.TRS(wp, gRot, sv);
                var col = new Color(0.3f, 0.6f, 1.0f);
                matBlock.SetColor("_Color", col);
                Graphics.DrawMesh(mesh, mtx, partMat, SceneLayer, null, 0, matBlock);
            }
        }

        private void DrawProjectiles(DVector3 origin)
        {
            var cm = Current.Game?.GetComponent<CombatManager>();
            if (cm == null) return;

            // 渲染插值必须用 physicsTick 基准（射弹 spawnPhysicsTick 同基准），
            // 不能用 TicksGame：physicsTick 是受时间档缩放的游戏世界秒且可为负，
            // 与 TicksGame 直接相减单位错位 → 外推恒为 0 → 渲染只显示离散物理位置 → 折线。
            // 天体偏移同样用 double physicsTick：int 化在 1/60x 下每 60 real tick 才跳 1 秒，
            // 天体位置离散跳变 → 射弹/场景"集体闪现"。全部用 double 后完全连续。
            double tick = TimeScaleManager.GetPhysicsTickSafe();

            foreach (var proj in cm.ActiveProjectiles)
            {
                // 渲染位置解析化：无重力射弹位置 = spawnPos + velocity×(physicsTick - spawnPhysicsTick)，
                // 是 physicsTick 的连续函数——任何渲染帧率下都平滑（不依赖每 2 tick 的离散步进点，
                // 低帧率时渲染点与物理点重合会离散跳动呈折线）。重力射弹回退为物理点+外推插值。
                double physicsNowT = tick;
                DVector3 renderPos = proj.affectedByGravity
                    ? proj.position + proj.velocity * Math.Max(0, physicsNowT - proj.prevPhysicsTick)
                    : proj.spawnPosition + proj.velocity * (physicsNowT - proj.spawnPhysicsTick);

                // 射弹位置是行星系相对坐标，渲染需转绝对（加所属行星世界偏移）
                DVector3 bodyOff = GetBodyWorldPos(proj.systemBodyId, tick);
                Vector3 vp = WorldToView(renderPos + bodyOff, origin);
                Vector3 camView = GetCameraViewPos();
                float dist = Vector3.Distance(vp, camView);

                // 按相机距离自适应放大：射弹在行星系尺度下极小，固定尺寸不可见
                float headSize = Mathf.Max(4f, dist * 0.008f);
                // 尾迹必须远细于弹头，否则立方体端面在视觉上像第二个球
                float trailWidth = Mathf.Max(1f, headSize * 0.15f);

                // 弹头
                Matrix4x4 mtx = Matrix4x4.TRS(vp, Quaternion.identity, Vector3.one * headSize);
                matBlock.SetColor("_Color", new Color(1f, 0.9f, 0.2f));
                Graphics.DrawMesh(sphereMeshLOD1, mtx, renderMat, SceneLayer, null, 0, matBlock);

#if DEBUG
                // 连续检测：相邻帧弹头渲染位置跳变超 1.5×物理步进 → 折线/闪现（限频提示）
                if (debugLastProjRender.TryGetValue(proj.id, out var lastRender))
                {
                    double jump = (renderPos - lastRender).magnitude;
                    double expectedStep = proj.velocity.magnitude * (2.0 / 60.0);
                    if (jump > expectedStep * 1.5 + 1.0 && Find.TickManager.TicksGame - debugJumpLogTick >= 60)
                    {
                        debugJumpLogTick = Find.TickManager.TicksGame;
                        Log.Message(string.Format("[射弹渲染检测] proj={0} 帧间跳变 {1:F0}m（预期步进 {2:F0}m）→ 疑似折线/闪现",
                            proj.id, jump, expectedStep));
                    }
                }
                debugLastProjRender[proj.id] = renderPos;
#endif

                // 尾迹：沿 velocity 反方向固定延伸 1 个物理步（长度恒定、端点连续）。
                // 原实现连接 proj.prevPosition（物理上一步，每 2 tick 跳 167m），尾迹长度
                // 每物理步从满长缩到 0 再跳回 → 伸缩抖动，视觉上呈"折线"。弹头本就平滑。
                DVector3 trailEndWorld = renderPos - proj.velocity * (2.0 / 60.0);
                Vector3 tv = WorldToView(trailEndWorld + bodyOff, origin);
                float len = Vector3.Distance(vp, tv);
                if (len > 0.01f)
                {
                    Vector3 mid = (vp + tv) * 0.5f;
                    Quaternion rot = Quaternion.LookRotation((vp - tv).normalized, Vector3.up);
                    Matrix4x4 lmtx = Matrix4x4.TRS(mid, rot, new Vector3(trailWidth, trailWidth, len));
                    matBlock.SetColor("_Color", new Color(1f, 0.8f, 0.2f, 0.55f));
                    Graphics.DrawMesh(cubeMesh, lmtx, renderMat, SceneLayer, null, 0, matBlock);
                }
            }

            foreach (var beam in cm.ActiveLaserBeams)
            {
                DVector3 bodyOff = GetBodyWorldPos(beam.systemBodyId, tick);
                Vector3 sv = WorldToView(beam.startPos + bodyOff, origin);
                Vector3 ev = WorldToView(beam.endPos + bodyOff, origin);
                Vector3 mid = (sv + ev) * 0.5f;
                float len = Vector3.Distance(sv, ev);
                if (len < 0.01f) continue;
                Quaternion rot = Quaternion.LookRotation((ev - sv).normalized, Vector3.up);
                Matrix4x4 mtx = Matrix4x4.TRS(mid, rot, new Vector3(0.15f, 0.15f, len));
                matBlock.SetColor("_Color", new Color(1f, 0.2f, 0.2f, 0.85f));
                Graphics.DrawMesh(cubeMesh, mtx, renderMat, SceneLayer, null, 0, matBlock);
            }

            // 爆炸视觉：扩散闪光球（半径随时间增长、透明度淡出）
            foreach (var exp in cm.ActiveExplosions)
            {
                float t = 1f - (float)exp.remainingTicks / exp.totalTicks; // 0→1
                DVector3 bodyOff = GetBodyWorldPos(exp.systemBodyId, tick);
                Vector3 vp = WorldToView(exp.position + bodyOff, origin);
                float radius = (float)(exp.maxRadius * MetersToView) * (0.2f + 0.8f * t);
                if (radius < 0.01f) continue;
                float alpha = 1f - t;
                Matrix4x4 mtx = Matrix4x4.TRS(vp, Quaternion.identity, Vector3.one * radius);
                matBlock.SetColor("_Color", new Color(1f, 0.45f, 0.1f, alpha));
                Graphics.DrawMesh(sphereMeshLOD1, mtx, renderMat, SceneLayer, null, 0, matBlock);
            }
        }

        private void OnPostRender(ScriptableRenderContext context, Camera cam)
        {
            if (cam != renderCamera) return;
            DrawOrbitLinesGL();
        }

        private void DrawOrbitLinesGL()
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return;

            int tick = cachedRenderTick;
            DVector3 origin = GetRenderOrigin();
            var bodyDef = mgr.GetBodyById(currentSystemBodyId);
            if (bodyDef == null) return;

            GL.PushMatrix();
            renderMat.SetPass(0);
            GL.Begin(GL.LINES);
            GL.Color(new Color(0f, 0.5f, 0.3f, 0.4f));

            var toDraw = new List<CelestialBodyDef>();
            if (bodyDef.HasOrbit) toDraw.Add(bodyDef);
            foreach (var m in bodyDef.moonBodies) toDraw.Add(m);

            foreach (var body in toDraw)
            {
                var pts = mgr.GetOrbitPathUnity(body.bodyId, tick, 72);
                if (pts == null || pts.Count < 2) continue;

                for (int i = 0; i < pts.Count; i++)
                {
                    int j = (i + 1) % pts.Count;
                    Vector3 a = WorldToView(new DVector3(
                        pts[i].x * SpaceConstants.MetersPerUnit,
                        pts[i].y * SpaceConstants.MetersPerUnit,
                        pts[i].z * SpaceConstants.MetersPerUnit), origin);
                    Vector3 b = WorldToView(new DVector3(
                        pts[j].x * SpaceConstants.MetersPerUnit,
                        pts[j].y * SpaceConstants.MetersPerUnit,
                        pts[j].z * SpaceConstants.MetersPerUnit), origin);
                    GL.Vertex(a); GL.Vertex(b);
                }
            }

            GL.End();
            GL.PopMatrix();
        }

        // ════════════════════════════════════════════════════
        //  GUI 交互
        // ════════════════════════════════════════════════════

        private void HandleGUIEvents(Rect inRect)
        {
            var e = Event.current;
            if (e == null) return;

            if (e.type == EventType.MouseDown && e.button == 0 && !isDraggingRight)
            {
                TrySelectGroup(e.mousePosition - inRect.position, inRect.size);
            }

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.R)
            {
                InitCameraForCurrentBody();
            }
        }

        private void TrySelectGroup(Vector2 mouseInView, Vector2 viewSize)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null || renderCamera == null) return;

            int tick = (int)Math.Round(TimeScaleManager.GetPhysicsTickSafe());
            DVector3 origin = GetRenderOrigin();

            var sysIds = new HashSet<int> { currentSystemBodyId };
            var bodyDef = mgr.GetBodyById(currentSystemBodyId);
            if (bodyDef != null)
                foreach (var m in bodyDef.moonBodies)
                    sysIds.Add(m.bodyId);

            float best = float.MaxValue;
            int bestId = -1;

            foreach (var g in spaceGame.GetAllGroups())
            {
                if (!g.IsActive || !sysIds.Contains(g.CelestialBodyId)) continue;

                DVector3 gWorld = GetGroupAbsoluteWorldPos(g, tick);
                Vector3 vp = WorldToView(gWorld, origin);
                Vector3 sp = renderCamera.WorldToScreenPoint(vp);
                if (sp.z < 0) continue;

                float sx = sp.x / renderCamera.pixelWidth * viewSize.x;
                float sy = (1f - sp.y / renderCamera.pixelHeight) * viewSize.y;
                float d = Vector2.Distance(mouseInView, new Vector2(sx, sy));
                if (d < 25f && sp.z < best)
                {
                    best = sp.z;
                    bestId = g.GroupId;
                }
            }

            if (bestId >= 0)
            {
                selectedGroupId = bestId;
                InitCameraForCurrentBody();
            }
        }

        private void DrawOverlay(Rect inRect)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            string sysName = mgr != null ? mgr.GetBodyName(currentSystemBodyId) : "Unknown";

            // 锚定状态：编队 or 天体
            string anchorLabel = selectedGroupId >= 0
                ? $"<b>Anchor: Group {selectedGroupId}</b> (System: {sysName})"
                : $"<b>System: {sysName}</b>";
            Widgets.Label(new Rect(inRect.x + 10, inRect.y + 10, 500, 30), anchorLabel);

            float bx = inRect.xMax - 10;
            if (Widgets.ButtonText(new Rect(bx - 120, inRect.y + 10, 120, 25), "Select Group"))
                ShowGroupMenu();
            bx -= 130;
            if (Widgets.ButtonText(new Rect(bx, inRect.y + 10, 120, 25), "Switch System"))
                ShowSystemMenu();
            bx -= 130;
            if (Widgets.ButtonText(new Rect(bx, inRect.y + 10, 120, 25), "Deploy Pair"))
                DeployCombatTestPair();

            Widgets.CheckboxLabeled(
                new Rect(inRect.x + 10, inRect.y + 35, 200, 25),
                "Scale Up Parts", ref debugScaleParts);

            if (selectedGroupId >= 0)
                DrawInfoPanel(inRect);

            DrawBodyDirectionIndicators(inRect);
        }

        private void DrawBodyDirectionIndicators(Rect inRect)
        {
            if (renderCamera == null || inRect.width < 10f) return;
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return;

            double tick = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 origin = GetRenderOrigin();
            Vector2 center = inRect.center;
            float halfW = inRect.width * 0.5f;
            float halfH = inRect.height * 0.5f;

            var bodies = mgr.GetAllBodies();
            if (bodies == null) return;

            int camW = renderCamera.pixelWidth;
            int camH = renderCamera.pixelHeight;
            if (camW < 1 || camH < 1) return;

            foreach (var body in bodies)
            {
                // 锚定天体时不显示自身指示器
                if (selectedGroupId < 0 && body.bodyId == currentSystemBodyId) continue;

                DVector3 wp = mgr.GetBodyWorldPosition(body.bodyId, tick);
                Vector3 vp = WorldToView(wp, origin);
                Vector3 sp = renderCamera.WorldToScreenPoint(vp);
                if (sp.z <= 0f) continue; // 相机后方

                bool onScreen = sp.x >= 0f && sp.x <= camW && sp.y >= 0f && sp.y <= camH;
                if (onScreen) continue;

                // 屏幕空间方向
                float sx = sp.x / camW * inRect.width;
                float sy = (1f - sp.y / camH) * inRect.height;
                Vector2 dir = new Vector2(sx - center.x, sy - center.y).normalized;

                // 钳制到屏幕边缘
                Vector2 edge;
                float dxAbs = Mathf.Abs(dir.x);
                float dyAbs = Mathf.Abs(dir.y);
                if (dxAbs > 0.0001f && dyAbs > 0.0001f)
                {
                    float scaleX = halfW / dxAbs;
                    float scaleY = halfH / dyAbs;
                    float scale = Mathf.Min(scaleX, scaleY);
                    edge = center + dir * (scale - 20f);
                }
                else
                {
                    edge = center;
                }

                edge.x = Mathf.Clamp(edge.x, inRect.x + 30f, inRect.xMax - 80f);
                edge.y = Mathf.Clamp(edge.y, inRect.y + 14f, inRect.yMax - 14f);

                string label = mgr.GetBodyName(body.bodyId);
                Color bodyColor = body.bodyType switch
                {
                    CelestialBodyType.Star => Color.yellow,
                    CelestialBodyType.Planet => new Color(0.3f, 0.6f, 1f),
                    _ => Color.gray
                };
                GUI.color = bodyColor;
                Widgets.Label(new Rect(edge.x - 30, edge.y - 10, 60, 20), label);
                GUI.color = Color.white;
            }
        }

        private void ShowGroupMenu()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null) return;

            var sysIds = new HashSet<int> { currentSystemBodyId };
            var bodyDef = mgr.GetBodyById(currentSystemBodyId);
            if (bodyDef != null)
                foreach (var m in bodyDef.moonBodies)
                    sysIds.Add(m.bodyId);

            var opts = new List<FloatMenuOption>();
            foreach (var g in spaceGame.GetAllGroups())
            {
                if (!g.IsActive || !sysIds.Contains(g.CelestialBodyId)) continue;
                var gg = g;
                int entityCount = gg.GetEntityIds()?.Count ?? 0;
                string label = $"Group {gg.GroupId} ({entityCount} entities)";
                opts.Add(new FloatMenuOption(label, () =>
                {
                    selectedGroupId = gg.GroupId;
                    InitCameraForCurrentBody();
                }));
            }

            if (opts.Count == 0)
                opts.Add(new FloatMenuOption("(No groups in this system)", null));

            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private void ShowSystemMenu()
        {
            var opts = new List<FloatMenuOption>();
            foreach (var b in selectableSystems)
            {
                var bb = b;
                opts.Add(new FloatMenuOption(bb.label, () =>
                {
                    currentSystemBodyId = bb.bodyId;
                    selectedGroupId = -1;
                    InitCameraForCurrentBody();
                }));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private void DrawInfoPanel(Rect inRect)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            var g = spaceGame.GetGroupById(selectedGroupId);
            if (g == null) { selectedGroupId = -1; return; }

            var combatManager = Current.Game?.GetComponent<CombatManager>();
            var weapons = combatManager != null ? combatManager.GetGroupWeapons(selectedGroupId) : new List<WeaponInstance>();
            int shownWeapons = Mathf.Min(weapons.Count, 4);

            // 收集本编队的舱室部件（用于"进入舱室"）
            var roomEntities = new List<SpaceEntity>();
            var roomParts = new List<SpacePart>();
            foreach (int eid in g.GetEntityIds())
            {
                var e = spaceGame.GetEntityById(eid);
                if (e == null) continue;
                foreach (var part in e.Parts)
                {
                    if (part.IsRoomModulePart)
                    {
                        roomEntities.Add(e);
                        roomParts.Add(part);
                    }
                }
            }
            float roomSectionH = roomParts.Count > 0 ? 30 + roomParts.Count * 26 : 0;

            float pw = 280;
            float ph = 195 + shownWeapons * 22 + (targetGroupId >= 0 ? 80 : 0) + roomSectionH;
            Rect p = new Rect(inRect.xMax - pw - 10, inRect.yMax - ph - 10, pw, ph);
            Widgets.DrawBox(p);

            float x = p.x + 5;
            float w = pw - 10;
            float y = p.y + 5;

            Widgets.Label(new Rect(x, y, w, 22), $"<b>Group {g.GroupId}</b> (射手)");
            y += 22;

            var target = targetGroupId >= 0 ? spaceGame.GetGroupById(targetGroupId) : null;
            if (target != null)
            {
                double dist = (GetGroupRelPos(g) - GetGroupRelPos(target)).magnitude;
                Widgets.Label(new Rect(x, y, w, 18), $"目标 G{target.GroupId}  距离 {dist / 1000.0:F0} km");
            }
            else
            {
                Widgets.Label(new Rect(x, y, w, 18), "目标: 无（点 Mark 设置）");
            }
            y += 18;

            if (Widgets.ButtonText(new Rect(x, y, w, 24), targetGroupId == selectedGroupId ? "清除目标" : "Mark as Target"))
            {
                targetGroupId = targetGroupId == selectedGroupId ? -1 : selectedGroupId;
            }
            y += 30;

            // 武器列表（逐把 Fire）
            for (int i = 0; i < shownWeapons; i++)
            {
                var wp = weapons[i];
                string wState = wp.isOverheated ? "过热" : (wp.cooldownRemaining > 0 ? "冷却" + wp.cooldownRemaining : "就绪");
                Widgets.Label(new Rect(x, y, w - 60, 18), $"W{i} {wp.def.defName} [{wState}]");
                if (Widgets.ButtonText(new Rect(x + w - 55, y, 50, 18), "Fire"))
                    FireAtTarget(wp);
                y += 22;
            }
            if (weapons.Count > shownWeapons)
            {
                Widgets.Label(new Rect(x, y, w, 16), $"... +{weapons.Count - shownWeapons} 把武器");
                y += 18;
            }
            y += 4;

            if (Widgets.ButtonText(new Rect(x, y, w, 24), "Fire All Weapons"))
            {
                if (target != null)
                    foreach (var wp in weapons)
                        if (wp.CanFire())
                            FireAtTarget(wp);
            }
            y += 30;

            // 目标操作
            if (target != null)
            {
                if (Widgets.ButtonText(new Rect(x, y, w, 24), "Teleport Target (+1km 前方)"))
                    TeleportTargetHere();
                y += 26;
                if (Widgets.ButtonText(new Rect(x, y, w * 0.48f, 24), "Target +100m/s"))
                    AdjustTargetSpeed(100.0);
                if (Widgets.ButtonText(new Rect(x + w * 0.52f, y, w * 0.48f, 24), "Target -100m/s"))
                    AdjustTargetSpeed(-100.0);
                y += 30;
            }

            // 舱室操作：进入舱室 / 预生成地图（测试）
            if (roomParts.Count > 0)
            {
                Widgets.Label(new Rect(x, y, w, 18), "<b>舱室:</b>");
                y += 20;
                for (int i = 0; i < roomParts.Count; i++)
                {
                    var rp = roomParts[i];
                    var ent = roomEntities[i];
                    string label = rp.RoomTypeDef != null
                        ? rp.RoomTypeDef.label + " P" + rp.partId
                        : "舱室 P" + rp.partId;
                    bool hasMap = rp.linkedMapId >= 0 && SpaceMapGenerator.FindPocketMap(rp.linkedMapId) != null;
                    if (hasMap) label += " (已生成)";

                    if (Widgets.ButtonText(new Rect(x, y, w, 22), "进入 " + label))
                    {
                        var m = SpaceMapGenerator.GenerateRoomMap(ent, rp);
                        if (m != null) SpaceMapGenerator.EnterPocketMap(m);
                    }
                    y += 26;
                }
                if (Widgets.ButtonText(new Rect(x, y, w, 22), "预生成所有舱室地图（测试）"))
                {
                    for (int i = 0; i < roomParts.Count; i++)
                        SpaceMapGenerator.GenerateRoomMap(roomEntities[i], roomParts[i]);
                }
                y += 26;
            }

            if (Widgets.ButtonText(new Rect(x, y, w, 24), "Deselect"))
            {
                selectedGroupId = -1;
                targetGroupId = -1;
                InitCameraForCurrentBody();
            }
        }

        /// <summary>获取编队在行星系相对坐标下的当前位置（与 CombatManager 开火基准一致）。</summary>
        private DVector3 GetGroupRelPos(SpaceEntityGroup g)
        {
            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            return g.IsCircularOrbit ? g.GetCircularPositionAtTick(physicsTick) : g.CurrentOrbit.position;
        }

        private void FireAtTarget(WeaponInstance weapon)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var combatManager = Current.Game?.GetComponent<CombatManager>();
            var target = targetGroupId >= 0 ? spaceGame?.GetGroupById(targetGroupId) : null;
            if (combatManager == null || target == null) return;

            DVector3 targetPos = GetGroupRelPos(target);
            combatManager.FireWeapon(selectedGroupId, weapon, targetPos);
        }

        private void TeleportTargetHere()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var shooter = spaceGame?.GetGroupById(selectedGroupId);
            var target = spaceGame?.GetGroupById(targetGroupId);
            if (shooter == null || target == null) return;

            DVector3 shooterPos = GetGroupRelPos(shooter);
            DVector3 shooterVel = shooter.CurrentOrbit.velocity;
            if (shooter.IsCircularOrbit)
            {
                double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
                DVector3 pos = shooter.GetCircularPositionAtTick(physicsTick);
                DVector3 radial = (pos - shooter.GetCentralBodyOffset(physicsTick)).normalized;
                DVector3 tan = shooter.circularOrbitNormal.sqrMagnitude > 0.001
                    ? DVector3.Cross(shooter.circularOrbitNormal, radial).normalized
                    : new DVector3(-radial.z, 0, radial.x).normalized;
                shooterVel = tan * (shooter.circularAngularVelocity * shooter.circularRadius);
            }

            if (target.IsCircularOrbit)
                target.ExitCircularOrbit();
            DVector3 dir = shooterVel.sqrMagnitude > 1e-6 ? shooterVel.normalized : new DVector3(1, 0, 0);
            target.CurrentOrbit.position = shooterPos + dir * 1000.0;
            target.CurrentOrbit.velocity = shooterVel;
            target.OrbitDirty = true;

            Log.Message(string.Format(
                "[PFView] 传送 G{0} → G{1} 前方1km | shooter circular={2} pos=({3:F0},{4:F0},{5:F0}) vel=({6:F0},{7:F0},{8:F0}) | target circular={9} pos=({10:F0},{11:F0},{12:F0}) vel=({13:F0},{14:F0},{15:F0})",
                target.GroupId, shooter.GroupId,
                shooter.IsCircularOrbit, shooterPos.x, shooterPos.y, shooterPos.z,
                shooterVel.x, shooterVel.y, shooterVel.z,
                target.IsCircularOrbit, target.CurrentOrbit.position.x, target.CurrentOrbit.position.y, target.CurrentOrbit.position.z,
                target.CurrentOrbit.velocity.x, target.CurrentOrbit.velocity.y, target.CurrentOrbit.velocity.z));
        }

        private void AdjustTargetSpeed(double dv)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var shooter = spaceGame?.GetGroupById(selectedGroupId);
            var target = spaceGame?.GetGroupById(targetGroupId);
            if (shooter == null || target == null) return;

            if (target.IsCircularOrbit)
                target.ExitCircularOrbit();

            DVector3 toShooter = GetGroupRelPos(shooter) - GetGroupRelPos(target);
            double len = toShooter.magnitude;
            if (len < 1e-6) return;
            toShooter /= len;
            // 手动 ΔV 走合冲量累积器：物理步首统一施加（与 burn/碰撞同入口，避免一步内多次改速度）
            target.pendingImpulse += toShooter * dv;
            target.OrbitDirty = true;

            Log.Message(string.Format("[PFView] G{0} 速度沿射向 G{1} 方向调整 {2:+#;-#;0} m/s", target.GroupId, shooter.GroupId, dv));
        }

        /// <summary>部署一对 100m 间距的交火测试船（敌对 faction），绕当前系统天体 500km 轨道。</summary>
        private void DeployCombatTestPair()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;

            int gidA = GetNextGroupId(spaceGame);
            int gidB = gidA + 1;
            int eidA = GetNextEntityId(spaceGame);
            int eidB = eidA + 1;

            int bodyId = currentSystemBodyId;
            double orbitRadius = SpaceConstants.PlanetRadiusMeters + 500000.0;
            double angle = Find.TickManager.TicksGame * 0.001;
            DVector3 basePos = new DVector3(System.Math.Cos(angle) * orbitRadius, 0, System.Math.Sin(angle) * orbitRadius);
            double mu = spaceGame.GetBodyGM(bodyId);
            double speed = System.Math.Sqrt(mu / orbitRadius);
            DVector3 vel = new DVector3(-System.Math.Sin(angle) * speed, 0, System.Math.Cos(angle) * speed);

            CreateTestShip(spaceGame, gidA, eidA, basePos, vel, 0, bodyId);
            CreateTestShip(spaceGame, gidB, eidB, basePos + new DVector3(200, 0, 0), vel, 1, bodyId);

            selectedGroupId = gidA;
            targetGroupId = gidB;
            InitCameraForCurrentBody();

            Log.Message(string.Format("[PFView] 部署交火测试对: G{0}(f0) vs G{1}(f1) 间距200m @ {2:F0}km 轨道",
                gidA, gidB, orbitRadius / 1000.0));
        }

        /// <summary>创建单 part 测试船（Lite 裁剪：无 Ship3D 蓝本，直接构造唯一 part 盒体）。</summary>
        private static void CreateTestShip(SpaceGameComp spaceGame, int groupId, int entityId,
            DVector3 pos, DVector3 vel, int factionId, int bodyId)
        {
            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = bodyId
            };
            group.CurrentOrbit = new OrbitState { position = pos, velocity = vel, rotation = Quaternion.identity };
            spaceGame.RegisterGroup(group);
            group.EnterCircularOrbit(Find.TickManager.TicksGame, bodyId);

            var entity = new SpaceEntity(entityId, factionId)
            {
                GroupId = groupId,
                IsCollidable = true,
                IsMovable = true,
                IsAffectedByGravity = true
            };
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);
            entity.SetParts(new List<SpacePart>
            {
                new SpacePart
                {
                    partId = 1,
                    defName = "PF_TestShip",
                    localPosition = DVector3.zero,
                    localBoundsSize = new DVector3(100.0, 40.0, 100.0)
                }
            });
            spaceGame.RegisterEntity(entity);
        }

        private static int GetNextGroupId(SpaceGameComp spaceGame)
        {
            int maxId = 0;
            foreach (var g in spaceGame.GetAllGroups())
                if (g.GroupId > maxId) maxId = g.GroupId;
            return maxId + 1;
        }

        private static int GetNextEntityId(SpaceGameComp spaceGame)
        {
            int maxId = 0;
            foreach (var g in spaceGame.GetAllGroups())
                foreach (int eid in g.GetEntityIds())
                    if (eid > maxId) maxId = eid;
            return maxId + 1;
        }
    }
}
