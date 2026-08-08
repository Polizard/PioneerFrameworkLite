using UnityEngine;
using Verse;
using RimWorld;
using PioneerFramework.Combat;
using System.Collections.Generic;
using System.Text;

namespace PioneerFramework.Debug
{
    public class Window_CombatTest : Window
    {
        private CombatManager combatManager;
        private SpaceGameComp spaceGame;

        // 3D 可视化
        private Camera renderCamera;
        private RenderTexture renderTexture;
        private GameObject cameraObject;
        private GameObject sceneRoot;
        private GameObject ship1Object;
        private GameObject ship2Object;
        private DVector3 ship1PosMeters, ship2PosMeters;
        private GameObject projectileContainer;
        private readonly List<GameObject> projectileObjects = new List<GameObject>();
        private GameObject boundsContainer;
        private readonly Dictionary<int, GameObject> boundsObjects = new Dictionary<int, GameObject>();
        private GameObject hitEffectObject;

        // Part AABB 可视化
        private GameObject partBoundsContainer;
        private readonly Dictionary<int, List<GameObject>> partBoundsObjects = new Dictionary<int, List<GameObject>>();
        private bool showPartBounds = true;

        // 射弹轨迹可视化
        private GameObject trailContainer;
        private readonly Dictionary<int, LineRenderer> projectileTrails = new Dictionary<int, LineRenderer>();
        private readonly Dictionary<int, List<Vector3>> projectileTrailPoints = new Dictionary<int, List<Vector3>>();
        private bool showProjectileTrails = true;
        private const int MaxTrailPoints = 30;

        // 激光射线可视化
        private GameObject laserBeamContainer;
        private readonly List<GameObject> laserBeamObjects = new List<GameObject>();
        private Material laserBeamMat;

        // 相机控制
        private Vector3 cameraTarget = Vector3.zero;
        private float cameraDistance = 100f;
        private float cameraYaw = 45f;
        private float cameraPitch = 30f;
        private Vector2 lastMousePos;
        private bool hasLastMousePos = false;

        // 渲染常量
        private const int SceneLayer = 30;
        private const float ShipScale = 2.0f;
        private const float VisualScale = 50f;       // 统一视觉放大系数，用于船/bounds/part等所有可视化
        private const int EARTH_BODY_ID = 1;
        private const double TestOrbitRadiusMeters = 7000000.0;
        private const double ShipSeparationMeters = 1000000.0;

        // 材质（延迟初始化）
        private Material friendlyMat;
        private Material enemyMat;
        private Material projectileMat;
        private Material earthMat;
        private Material orbitLineMat;
        private Material boundsMat;
        private Material hitEffectMat;
        private bool materialsInitialized = false;

        private void EnsureMaterialsInitialized()
        {
            if (materialsInitialized) return;
            materialsInitialized = true;

            if (friendlyMat == null)
            {
                friendlyMat = MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.Cutout, new Color(0.2f, 0.8f, 1.0f));
            }
            if (enemyMat == null)
            {
                enemyMat = MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.Cutout, new Color(1.0f, 0.3f, 0.3f));
            }
            if (projectileMat == null)
            {
                projectileMat = MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.Cutout, Color.yellow);
            }
            if (earthMat == null)
            {
                earthMat = MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.Cutout, new Color(0.3f, 0.5f, 1.0f));
            }
            if (orbitLineMat == null)
            {
                orbitLineMat = MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.WorldOverlayCutout, new Color(0.5f, 0.7f, 1.0f, 0.6f));
            }
            if (boundsMat == null)
            {
                boundsMat = MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.WorldOverlayCutout, new Color(0.0f, 1.0f, 0.0f, 0.8f));
            }
            if (hitEffectMat == null)
            {
                hitEffectMat = MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.Cutout, new Color(1.0f, 0.5f, 0.0f));
            }
            if (laserBeamMat == null)
            {
                // 使用 Unity 原生 Sprites/Default shader，确保 LineRenderer 可靠渲染
                laserBeamMat = new Material(Shader.Find("Sprites/Default"));
                laserBeamMat.color = new Color(1.0f, 0.05f, 0.05f, 1.0f);
            }
        }

        // 日志
        private StringBuilder statusLog = new StringBuilder();
        private Vector2 scrollPos = Vector2.zero;
        private const int MaxLogLines = 50;

        // 上次更新的 tick
        private int lastUpdateTick = -1;
        private int lastShipLogTick = -999;     // 用于限频输出ShipPos日志
        private int lastBoundsLogTick = -999;   // 用于限频输出BoundsPos日志
        private int lastProjLogTick = -999;     // 用于限频输出ProjectilePos日志
        private int lastLaserLogTick = -999;   // 用于限频输出LaserVis日志

        // 真实 Group 数据
        private readonly List<int> earthOrbitGroupIds = new List<int>();
        private readonly Dictionary<int, GameObject> groupDisplayObjects = new Dictionary<int, GameObject>();
        private readonly Dictionary<int, Vector3> groupPositions = new Dictionary<int, Vector3>();

        public Window_CombatTest()
        {
            doWindowBackground = true;
            doCloseButton = true;
            doCloseX = true;
            draggable = true;
            resizeable = true;
            windowRect = new Rect(50f, 50f, 900f, 700f);
            optionalTitle = "战斗系统测试";
        }

        public override void PostClose()
        {
            if (renderCamera != null)
            {
                renderCamera.targetTexture = null;
            }
            if (renderTexture != null)
            {
                renderTexture.Release();
                renderTexture = null;
            }
            if (cameraObject != null)
            {
                Object.Destroy(cameraObject);
            }
            if (sceneRoot != null)
            {
                Object.Destroy(sceneRoot);
            }

            partBoundsObjects.Clear();
            projectileTrails.Clear();
            projectileTrailPoints.Clear();

            base.PostClose();
        }

        public override void DoWindowContents(Rect inRect)
        {
            // 初始化引用
            if (spaceGame == null)
            {
                spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            }
            if (combatManager == null)
            {
                combatManager = Current.Game?.GetComponent<CombatManager>();
            }

            // 分割布局：左侧 3D 视图，右侧控制面板+日志
            float viewWidth = inRect.width * 0.6f;
            float viewHeight = inRect.height - 40f;
            float rightWidth = inRect.width - viewWidth - 15f;
            float logHeight = inRect.height * 0.35f;
            float controlHeight = inRect.height - logHeight - 40f;

            Rect viewRect = new Rect(inRect.x, inRect.y + 30f, viewWidth, viewHeight);
            Rect controlRect = new Rect(inRect.x + viewWidth + 10f, inRect.y + 30f, rightWidth, controlHeight);
            Rect logRect = new Rect(inRect.x + viewWidth + 10f, inRect.y + 35f + controlHeight, rightWidth, logHeight);

            // 控制面板（先处理输入，确保开火等操作在3D渲染前完成）
            DrawControlPanel(controlRect);

            // 渲染 3D 视图
            Render3DView(viewRect);

            // 日志区域
            DrawLogPanel(logRect);
        }

        private void Render3DView(Rect viewRect)
        {
            // 确保渲染资源已创建
            if (renderCamera == null)
            {
                CreateRenderResources();
            }

            // 确保渲染纹理尺寸正确
            EnsureRenderTextureSize(viewRect.width, viewRect.height);

            // 处理相机输入
            if (viewRect.Contains(Event.current.mousePosition))
            {
                HandleCameraInput();
            }

            // 更新相机变换
            UpdateCameraTransform();

            // 每 tick 更新一次 Group 位置
            int currentTick = 0;
            if (Find.TickManager != null)
            {
                currentTick = Find.TickManager.TicksGame;
            }
            if (currentTick != lastUpdateTick && lastUpdateTick >= 0)
            {
                UpdateGroupPositions(currentTick);
                UpdateShipVisuals(currentTick);
            }
            lastUpdateTick = currentTick;

            // 更新射弹可视化
            UpdateProjectileVisuals();

            // 更新射弹轨迹
            UpdateProjectileTrails();

            // 更新碰撞箱可视化
            UpdateBoundsVisuals();

            // 更新 Part AABB 可视化
            UpdatePartBoundsVisuals();

            // 更新命中效果
            UpdateHitEffect();

            // 更新激光射线可视化
            UpdateLaserBeams();

            // 渲染
            if (renderCamera != null && renderTexture != null)
            {
                renderCamera.Render();
                GUI.DrawTexture(viewRect, renderTexture);
            }

            // 边框
            Widgets.DrawBox(viewRect);

            // 视图标签
            Rect labelRect = new Rect(viewRect.x + 5f, viewRect.y + 5f, 120f, 20f);
            Widgets.Label(labelRect, "3D 战斗视图");
        }

        private void CreateRenderResources()
        {
            EnsureMaterialsInitialized();

            cameraObject = new GameObject("CombatTestCamera");
            cameraObject.transform.position = new Vector3(0f, 50f, -100f);
            cameraObject.transform.LookAt(Vector3.zero);
            renderCamera = cameraObject.AddComponent<Camera>();
            if (renderCamera != null)
            {
                renderCamera.cullingMask = 1 << SceneLayer;
                renderCamera.clearFlags = CameraClearFlags.SolidColor;
                renderCamera.backgroundColor = new Color(0.05f, 0.05f, 0.1f);
                renderCamera.nearClipPlane = 0.1f;
                renderCamera.farClipPlane = 50000f;
                renderCamera.fieldOfView = 60f;
            }

            renderTexture = new RenderTexture(512, 384, 24);
            renderTexture.Create();
            if (renderCamera != null)
            {
                renderCamera.targetTexture = renderTexture;
            }

            sceneRoot = new GameObject("CombatTestScene");
            sceneRoot.layer = SceneLayer;

            GameObject light1 = new GameObject("CombatLight1");
            light1.transform.SetParent(sceneRoot.transform);
            light1.layer = SceneLayer;
            Light sunLight = light1.AddComponent<Light>();
            if (sunLight != null)
            {
                sunLight.type = LightType.Directional;
                sunLight.intensity = 1f;
                sunLight.transform.rotation = Quaternion.Euler(30f, -45f, 0f);
                sunLight.color = new Color(0.9f, 0.9f, 1.0f);
            }

            GameObject light2 = new GameObject("CombatLight2");
            light2.transform.SetParent(sceneRoot.transform);
            light2.layer = SceneLayer;
            Light fillLight = light2.AddComponent<Light>();
            if (fillLight != null)
            {
                fillLight.type = LightType.Directional;
                fillLight.intensity = 0.3f;
                fillLight.transform.rotation = Quaternion.Euler(-30f, 120f, 0f);
                fillLight.color = new Color(0.5f, 0.5f, 0.7f);
            }

            projectileContainer = new GameObject("Projectiles");
            projectileContainer.transform.SetParent(sceneRoot.transform);
            projectileContainer.layer = SceneLayer;

            boundsContainer = new GameObject("Bounds");
            boundsContainer.transform.SetParent(sceneRoot.transform);
            boundsContainer.layer = SceneLayer;

            partBoundsContainer = new GameObject("PartBounds");
            partBoundsContainer.transform.SetParent(sceneRoot.transform);
            partBoundsContainer.layer = SceneLayer;

            trailContainer = new GameObject("Trails");
            trailContainer.transform.SetParent(sceneRoot.transform);
            trailContainer.layer = SceneLayer;

            laserBeamContainer = new GameObject("LaserBeams");
            laserBeamContainer.transform.SetParent(sceneRoot.transform);
            laserBeamContainer.layer = SceneLayer;

            CreateReferenceGrid();

            CreateEarth();

            LogToStatus("渲染资源已创建");
        }

        private void EnsureRenderTextureSize(float viewWidth, float viewHeight)
        {
            if (renderCamera == null) return;

            int targetWidth = Mathf.Max(1, Mathf.RoundToInt(viewWidth));
            int targetHeight = Mathf.Max(1, Mathf.RoundToInt(viewHeight));
            if (renderTexture != null && (renderTexture.width != targetWidth || renderTexture.height != targetHeight))
            {
                RenderTexture newRT = new RenderTexture(targetWidth, targetHeight, 24);
                newRT.Create();
                renderCamera.targetTexture = newRT;
                renderTexture.Release();
                renderTexture = newRT;
            }
        }

        private void HandleCameraInput()
        {
            Event e = Event.current;

            if (e.type == EventType.MouseDown)
            {
                if (e.button == 0 || e.button == 1)
                {
                    lastMousePos = e.mousePosition;
                    hasLastMousePos = true;
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseDrag)
            {
                if (!hasLastMousePos)
                {
                    lastMousePos = e.mousePosition;
                    hasLastMousePos = true;
                }

                Vector2 delta = e.mousePosition - lastMousePos;
                lastMousePos = e.mousePosition;

                if (e.button == 0)
                {
                    cameraYaw += delta.x * 0.5f;
                    cameraPitch -= delta.y * 0.5f;
                    cameraPitch = Mathf.Clamp(cameraPitch, 5f, 85f);
                    e.Use();
                }
                else if (e.button == 1)
                {
                    if (renderCamera != null)
                    {
                        Vector3 right = renderCamera.transform.right;
                        Vector3 up = renderCamera.transform.up;
                        float moveSpeed = cameraDistance * 0.01f;
                        cameraTarget += right * (-delta.x * moveSpeed) + up * (delta.y * moveSpeed);
                    }
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseUp)
            {
                if (e.button == 0 || e.button == 1)
                {
                    hasLastMousePos = false;
                    e.Use();
                }
            }
            else if (e.type == EventType.ScrollWheel)
            {
                cameraDistance -= e.delta.y * cameraDistance * 0.05f;
                cameraDistance = Mathf.Clamp(cameraDistance, 20f, 10000f);
                e.Use();
            }
        }

        private void UpdateCameraTransform()
        {
            if (renderCamera == null) return;

            Quaternion rotation = Quaternion.Euler(cameraPitch, cameraYaw, 0f);
            Vector3 offset = rotation * new Vector3(0f, 0f, -cameraDistance);
            renderCamera.transform.position = cameraTarget + offset;
            renderCamera.transform.LookAt(cameraTarget);
        }

        private void CreateReferenceGrid()
        {
            // 简单的参考线
            GameObject linesObj = new GameObject("ReferenceLines");
            linesObj.transform.SetParent(sceneRoot.transform);
            linesObj.layer = SceneLayer;

            // X 轴（红色）
            CreateLine("XAxis", new Vector3(-50f, 0f, 0f), new Vector3(50f, 0f, 0f), Color.red, linesObj.transform);
            // Z 轴（蓝色）
            CreateLine("ZAxis", new Vector3(0f, 0f, -50f), new Vector3(0f, 0f, 50f), Color.blue, linesObj.transform);
            // Y 轴（绿色）
            CreateLine("YAxis", new Vector3(0f, -10f, 0f), new Vector3(0f, 50f, 0f), Color.green, linesObj.transform);
        }

        private void CreateEarth()
        {
            GameObject earth = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            earth.name = "Earth";
            earth.transform.SetParent(sceneRoot.transform);
            earth.layer = SceneLayer;

            Collider col = earth.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);

            MeshRenderer mr = earth.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.material = earthMat;
            }

            earth.transform.position = Vector3.zero;
            float earthRadiusUnity = (float)(SpaceConstants.PlanetRadiusMeters * SpaceConstants.UnitsPerMeter);
            earth.transform.localScale = Vector3.one * earthRadiusUnity * 2f;
        }

        private LineRenderer CreateLine(string name, Vector3 start, Vector3 end, Color color, Transform parent)
        {
            GameObject lineObj = new GameObject(name);
            lineObj.transform.SetParent(parent);
            lineObj.layer = SceneLayer;

            LineRenderer lr = lineObj.AddComponent<LineRenderer>();
            EnsureMaterialsInitialized();
            lr.material = orbitLineMat;
            lr.startColor = color;
            lr.endColor = color;
            lr.startWidth = 0.3f;
            lr.endWidth = 0.3f;
            lr.SetPosition(0, start);
            lr.SetPosition(1, end);

            return lr;
        }

        private void CreateShipVisual(GameObject shipObj, Vector3 position, bool isFriendly, Vector3 localScale)
        {
            EnsureMaterialsInitialized();

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = isFriendly ? "ShipBody_Friendly" : "ShipBody_Enemy";
            body.transform.SetParent(shipObj.transform);
            body.transform.localPosition = Vector3.zero;
            body.transform.localScale = localScale;

            Collider col = body.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);

            MeshRenderer mr = body.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.material = isFriendly ? friendlyMat : enemyMat;
            }

            shipObj.transform.position = position;
            shipObj.layer = SceneLayer;
            body.layer = SceneLayer;
        }

        private void UpdateGroupPositions(int currentTick)
        {
            if (spaceGame == null || sceneRoot == null) return;

            EnsureMaterialsInitialized();

            earthOrbitGroupIds.Clear();

            var allGroups = spaceGame.GetAllGroups();
            if (allGroups == null) return;

            foreach (var group in allGroups)
            {
                if (group == null || !group.IsActive) continue;
                // 跳过测试飞船 Group，它们有专用的视觉对象（ship1Object/ship2Object）
                if (group.GroupId == 1001 || group.GroupId == 1002) continue;

                Vector3 pos = GetGroupUnityPosition(group, currentTick);

                earthOrbitGroupIds.Add(group.GroupId);
                groupPositions[group.GroupId] = pos;

                if (!groupDisplayObjects.TryGetValue(group.GroupId, out GameObject go) || go == null)
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = "Group_" + group.GroupId;
                    go.transform.SetParent(sceneRoot.transform);
                    go.layer = SceneLayer;

                    Collider c = go.GetComponent<Collider>();
                    if (c != null) Object.Destroy(c);

                    MeshRenderer mr = go.GetComponent<MeshRenderer>();
                    if (mr != null)
                    {
                        mr.material = friendlyMat;
                    }

                    groupDisplayObjects[group.GroupId] = go;
                }

                float size = Mathf.Max(0.3f, cameraDistance * 0.005f);
                go.transform.position = pos;
                go.transform.localScale = Vector3.one * size;
            }

            List<int> toRemove = new List<int>();
            foreach (var kv in groupDisplayObjects)
            {
                if (!earthOrbitGroupIds.Contains(kv.Key))
                {
                    toRemove.Add(kv.Key);
                }
            }
            foreach (int id in toRemove)
            {
                if (groupDisplayObjects[id] != null)
                {
                    Object.Destroy(groupDisplayObjects[id]);
                }
                groupDisplayObjects.Remove(id);
                groupPositions.Remove(id);
            }
        }

        private Vector3 GetGroupUnityPosition(SpaceEntityGroup group, int currentTick)
        {
            if (group == null)
            {
                return Vector3.zero;
            }

            DVector3 posMeters;
            if (group.IsCircularOrbit)
                posMeters = group.GetCircularPositionAtTick(currentTick);
            else
                posMeters = group.RenderPosition;

            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (celestialMgr != null)
                posMeters += celestialMgr.GetBodyWorldPosition(EARTH_BODY_ID, currentTick);

            return (Vector3)posMeters * (float)SpaceConstants.UnitsPerMeter;
        }

        private void DrawControlPanel(Rect rect)
        {
            Listing_Standard list = new Listing_Standard();
            list.Begin(rect);

            list.Label("=== 战斗系统测试 ===");
            list.Gap();

            // 状态显示
            list.Label("[战斗状态]");
            list.Indent();

            string battleStatus = combatManager == null ? "未初始化" : (combatManager.IsBattleActive ? "进行中" : "未激活");
            list.Label("战斗: " + battleStatus);
            list.Label("射弹数: " + GetActiveProjectiles().Count);
            list.Label("细网格格数: " + GetGridActiveCells());

            // Part 状态
            if (spaceGame != null)
            {
                var e1 = spaceGame.GetEntityById(10001);
                var e2 = spaceGame.GetEntityById(10002);
                if (e1 != null) list.Label("Entity1 Parts: " + e1.Parts.Count);
                if (e2 != null) list.Label("Entity2 Parts: " + e2.Parts.Count);
            }

            string tickStr = Find.TickManager != null ? Find.TickManager.TicksAbs.ToString() : "?";
            list.Label("Tick: " + tickStr);

            list.Outdent();
            list.Gap();

            // Group 数量
            list.Label("[轨道 Group]");
            list.Indent();
            list.Label("地球轨道 Group: " + earthOrbitGroupIds.Count.ToString());
            list.Outdent();
            list.Gap();

            // 控制按钮
            list.Label("[控制]");

            if (list.ButtonText("扫描地球轨道 Group"))
            {
                ScanEarthOrbitGroups();
            }

            if (list.ButtonText("创建测试轨道 Group (近地)"))
            {
                CreateTestOrbitGroup(7000000.0, 0);
            }

            if (list.ButtonText("创建测试轨道 Group (中轨)"))
            {
                CreateTestOrbitGroup(20000000.0, 1);
            }

            if (list.ButtonText("将相机对准两船"))
            {
                if (ship1Object != null && ship2Object != null)
                {
                    cameraTarget = (ship1Object.transform.position + ship2Object.transform.position) * 0.5f;
                }
                cameraDistance = 100f;
                LogToStatus("相机已对准两船");
            }

            if (list.ButtonText("创建测试飞船"))
            {
                CreateTestShips();
            }

            if (ship1Object != null && list.ButtonText("飞船1 开火 → 飞船2"))
            {
                TestFireWeapon();
            }

            if (ship2Object != null && list.ButtonText("飞船2 开火 → 飞船1"))
            {
                TestFireWeapon2();
            }

            if (combatManager != null && ship1Object != null && ship2Object != null && list.ButtonText("发起战斗（两船互殴）"))
            {
                if (combatManager.CanEngage(1001, 1002))
                {
                    combatManager.EngageGroups(1001, 1002);
                    LogToStatus("战斗已发起！两船开始互殴");
                }
                else
                {
                    LogToStatus("无法发起战斗：两船距离超过世界区块边长（1000km）");
                }
            }

            if (combatManager != null && list.ButtonText("结束战斗"))
            {
                combatManager.EndBattle();
                LogToStatus("战斗已结束");
            }

            if (list.ButtonText("清除日志"))
            {
                statusLog.Clear();
            }

            list.Gap();
            list.Label("[调试覆盖]");

            bool prevShowPart = showPartBounds;
            list.CheckboxLabeled("Part AABB (黄框)", ref showPartBounds);
            if (prevShowPart != showPartBounds)
            {
                if (!showPartBounds)
                {
                    foreach (var kv in partBoundsObjects)
                        foreach (var obj in kv.Value)
                            if (obj != null) Object.Destroy(obj);
                    partBoundsObjects.Clear();
                }
            }

            bool prevShowTrails = showProjectileTrails;
            list.CheckboxLabeled("射弹轨迹 (黄线)", ref showProjectileTrails);
            if (prevShowTrails != showProjectileTrails)
            {
                if (!showProjectileTrails)
                {
                    foreach (var kv in projectileTrails)
                        if (kv.Value != null) Object.Destroy(kv.Value.gameObject);
                    projectileTrails.Clear();
                    projectileTrailPoints.Clear();
                }
            }

            list.Gap();
            list.Label("[状态信息]");
            if (ship1Object != null)
            {
                list.Label("  飞船1位置: " + ship1Object.transform.position.ToString("F2"));
            }
            if (ship2Object != null)
            {
                list.Label("  飞船2位置: " + ship2Object.transform.position.ToString("F2"));
            }
            foreach (var kv in boundsObjects)
            {
                list.Label("  Bounds " + kv.Key + "位置: " + kv.Value.transform.position.ToString("F2"));
            }

            list.End();
        }

        private void DrawLogPanel(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.1f, 0.1f, 0.1f, 0.95f));
            Widgets.DrawBox(rect);

            Rect innerRect = new Rect(rect.x + 5f, rect.y + 5f, rect.width - 10f, rect.height - 10f);

            GameFont oldFont = Text.Font;
            Text.Font = GameFont.Tiny;

            string[] logLines = statusLog.ToString().Split('\n');
            float contentHeight = logLines.Length * 14f;
            Rect contentRect = new Rect(0f, 0f, innerRect.width - 16f, Mathf.Max(contentHeight, innerRect.height));

            Widgets.BeginScrollView(innerRect, ref scrollPos, contentRect);

            float y = 0f;
            float lineHeight = 14f;
            for (int i = 0; i < logLines.Length; i++)
            {
                Rect lineRect = new Rect(0f, y, contentRect.width, lineHeight);
                Widgets.Label(lineRect, logLines[i]);
                y += lineHeight;
            }

            Widgets.EndScrollView();

            Text.Font = oldFont;
        }

        private void ScanEarthOrbitGroups()
        {
            if (spaceGame == null)
            {
                LogToStatus("[错误] SpaceGameComp 未找到");
                return;
            }

            var allGroups = spaceGame.GetAllGroups();
            if (allGroups == null)
            {
                LogToStatus("[错误] GetAllGroups() 返回 null");
                return;
            }

            int count = 0;
            foreach (var group in allGroups)
            {
                if (group != null && group.IsActive)
                {
                    count++;
                }
            }

            LogToStatus("[扫描] 活跃 Group 数量: " + count);
            foreach (var group in allGroups)
            {
                if (group != null && group.IsActive)
                {
                    string orbitType = group.IsCircularOrbit ? "圆轨" : "椭圆";
                    string radius = group.IsCircularOrbit ?
                        (group.circularRadius / 1000.0).ToString("F0") + "km" :
                        "?";
                    LogToStatus("  Group " + group.GroupId + ": " + orbitType + " R=" + radius);
                }
            }
        }

        private void CreateTestOrbitGroup(double altitudeMeters, int index)
        {
            if (spaceGame == null)
            {
                LogToStatus("[错误] SpaceGameComp 未找到");
                return;
            }

            if (Find.TickManager == null)
            {
                LogToStatus("[错误] TickManager 未就绪");
                return;
            }

            int groupId = 10000 + index + earthOrbitGroupIds.Count;
            int factionId = 0;

            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = EARTH_BODY_ID
            };

            double angle = index * 1.5;
            double x = System.Math.Cos(angle) * altitudeMeters;
            double z = System.Math.Sin(angle) * altitudeMeters;

            group.CurrentOrbit = new OrbitState
            {
                position = new DVector3(x, 0, z),
                velocity = DVector3.zero,
                rotation = Quaternion.identity
            };

            spaceGame.RegisterGroup(group);

            group.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            LogToStatus("[创建] 测试轨道 Group " + groupId + " 已创建");
            LogToStatus("  轨道半径: " + (altitudeMeters / 1000.0).ToString("F0") + " km");
            LogToStatus("  圆轨模式: " + group.IsCircularOrbit);
        }

        private void CreateTestShips()
        {
            try
            {
                if (spaceGame == null || combatManager == null)
                {
                    LogToStatus("[错误] 系统未初始化");
                    return;
                }

                if (Find.TickManager == null)
                {
                    LogToStatus("[错误] TickManager 未就绪");
                    return;
                }

                LogToStatus("[创建] 开始创建测试飞船...");

                if (renderCamera == null)
                {
                    LogToStatus("[创建] 创建渲染资源...");
                    CreateRenderResources();
                }

                if (ship1Object != null)
                {
                    Object.Destroy(ship1Object);
                    ship1Object = null;
                }
                if (ship2Object != null)
                {
                    Object.Destroy(ship2Object);
                    ship2Object = null;
                }

                // 创建友军飞船的轨道 Group
                int currentTick = Find.TickManager.TicksGame;

                double mu = SpaceConstants.PrimaryGravitationalParameter;
                double orbitalSpeed = System.Math.Sqrt(mu / TestOrbitRadiusMeters);

                double angle1 = 0;
                double angle2 = ShipSeparationMeters / TestOrbitRadiusMeters;

                DVector3 pos1 = new DVector3(System.Math.Cos(angle1) * TestOrbitRadiusMeters, 0, System.Math.Sin(angle1) * TestOrbitRadiusMeters);
                DVector3 pos2 = new DVector3(System.Math.Cos(angle2) * TestOrbitRadiusMeters, 0, System.Math.Sin(angle2) * TestOrbitRadiusMeters);

                DVector3 vel1 = new DVector3(-System.Math.Sin(angle1) * orbitalSpeed, 0, System.Math.Cos(angle1) * orbitalSpeed);
                DVector3 vel2 = new DVector3(-System.Math.Sin(angle2) * orbitalSpeed, 0, System.Math.Cos(angle2) * orbitalSpeed);

                LogToStatus(string.Format("[创建] 轨道位置 ship1=({0:F0},{1:F0},{2:F0})m ship2=({3:F0},{4:F0},{5:F0})m",
                    pos1.x, pos1.y, pos1.z, pos2.x, pos2.y, pos2.z));

                // 直接用代码构造测试武器定义（不依赖 XML/DefDatabase）
                WeaponDef cannonDef = new WeaponDef
                {
                    defName = "PF_Weapon_Cannon",
                    weaponType = WeaponType.Projectile,
                    damagePerShot = 15f,
                    fireRate = 0.5f,
                    maxRange = 2000000f,
                    projectileSpeed = 500000f,
                    projectileLifetime = 600,
                    cooldownTicks = 60,
                    affectedByGravity = true
                };

                WeaponDef laserDef = new WeaponDef
                {
                    defName = "PF_Weapon_Laser",
                    weaponType = WeaponType.Ray,
                    damagePerShot = 10f,
                    fireRate = 1f,
                    maxRange = 1500000f,
                    projectileSpeed = 500000f,
                    projectileLifetime = 300,
                    cooldownTicks = 40,
                    affectedByGravity = true
                };

                LogToStatus("[创建] 注册轨道Group...");
                var group1 = new SpaceEntityGroup(1001, 0)
                {
                    IsMovable = true,
                    IsCollidable = true,
                    CelestialBodyId = EARTH_BODY_ID
                };
                group1.CurrentOrbit = new OrbitState
                {
                    position = pos1,
                    velocity = vel1,
                    rotation = Quaternion.identity
                };
                spaceGame.RegisterGroup(group1);
                group1.EnterCircularOrbit(currentTick, EARTH_BODY_ID);

                LogToStatus("[创建] 创建Entity + Part...");
                var entity1 = new SpaceEntity(10001, 0)
                {
                    GroupId = 1001,
                    IsCollidable = true,
                    BoundsSize = new DVector3(5000.0, 3000.0, 5000.0)
                };
                entity1.SetPositionVelocity(DVector3.zero, DVector3.zero);
                var testParts1 = CreateTestParts(10001);
                entity1.SetParts(testParts1);
                spaceGame.RegisterEntity(entity1);

                var group2 = new SpaceEntityGroup(1002, 1)
                {
                    IsMovable = true,
                    IsCollidable = true,
                    CelestialBodyId = EARTH_BODY_ID
                };
                group2.CurrentOrbit = new OrbitState
                {
                    position = pos2,
                    velocity = vel2,
                    rotation = Quaternion.identity
                };
                spaceGame.RegisterGroup(group2);
                group2.EnterCircularOrbit(currentTick, EARTH_BODY_ID);

                var entity2 = new SpaceEntity(10002, 1)
                {
                    GroupId = 1002,
                    IsCollidable = true,
                    BoundsSize = new DVector3(5000.0, 3000.0, 5000.0)
                };
                entity2.SetPositionVelocity(DVector3.zero, DVector3.zero);
                var testParts2 = CreateTestParts(10002);
                entity2.SetParts(testParts2);
                spaceGame.RegisterEntity(entity2);

                LogToStatus("[创建] 创建飞船视觉...");
                // 使用 Entity 实际 BoundsSize * VisualScale 作为视觉尺寸，与 bounds 线框统一
                Vector3 ship1VisualSize = (Vector3)(entity1.BoundsSize * (float)SpaceConstants.UnitsPerMeter) * VisualScale;
                Vector3 ship2VisualSize = (Vector3)(entity2.BoundsSize * (float)SpaceConstants.UnitsPerMeter) * VisualScale;
                ship1Object = new GameObject("TestShip1_Friendly");
                CreateShipVisual(ship1Object, Vector3.zero, true, ship1VisualSize);

                ship2Object = new GameObject("TestShip2_Enemy");
                CreateShipVisual(ship2Object, Vector3.zero, false, ship2VisualSize);

                // 创建武器实例
                WeaponInstance weapon1 = new WeaponInstance();
                weapon1.def = cannonDef;

                WeaponInstance weapon2 = new WeaponInstance();
                weapon2.def = laserDef;

                List<WeaponInstance> weapons1 = new List<WeaponInstance>();
                weapons1.Add(weapon1);

                List<WeaponInstance> weapons2 = new List<WeaponInstance>();
                weapons2.Add(weapon2);

                combatManager.InitializeGroupWeapons(1001, weapons1);
                combatManager.InitializeGroupWeapons(1002, weapons2);

                LogToStatus("[创建] 战斗测试飞船已创建（环绕地球轨道）");
                LogToStatus("  飞船1 [友军]: Group 1001, Entity 10001, 轨道半径=" + (TestOrbitRadiusMeters / 1000.0).ToString("F0") + "km, 武器=加农炮, Part数=" + testParts1.Count);
                LogToStatus("  飞船2 [敌方]: Group 1002, Entity 10002, 轨道半径=" + (TestOrbitRadiusMeters / 1000.0).ToString("F0") + "km, 武器=激光炮, Part数=" + testParts2.Count);
                LogToStatus("  两船距离: " + (ShipSeparationMeters / 1000.0).ToString("F0") + "km");
                LogToStatus("  碰撞箱: " + entity1.BoundsSize.x.ToString("F0") + "m × " + entity1.BoundsSize.y.ToString("F0") + "m × " + entity1.BoundsSize.z.ToString("F0") + "m (从Part自动计算)");

                // 立即更新视觉位置和相机目标
                LogToStatus("[创建] 更新视觉位置...");
                UpdateShipVisuals(currentTick);
                LogToStatus(string.Format("[创建] 完成! ship1可视pos=({0:F1},{1:F1},{2:F1}) cameraTarget=({3:F1},{4:F1},{5:F1})",
                    ship1PosMeters.x, ship1PosMeters.y, ship1PosMeters.z,
                    cameraTarget.x, cameraTarget.y, cameraTarget.z));
            }
            catch (System.Exception ex)
            {
                LogToStatus("[创建] 异常: " + ex.GetType().Name + " - " + ex.Message);
                Log.Error("CreateTestShips failed: " + ex);
            }
        }

        private void TestFireWeapon()
        {
            if (combatManager == null)
            {
                LogToStatus("[错误] CombatManager 未初始化");
                return;
            }
            if (ship2Object == null)
            {
                LogToStatus("[错误] 测试飞船未创建，请先点击「创建测试飞船」");
                return;
            }

            // 直接使用存储的米位置（物理层全程 double）
            DVector3 spawnPosMeters = ship1PosMeters;
            DVector3 targetPosMeters = ship2PosMeters;

            double distKm = (spawnPosMeters - targetPosMeters).magnitude / 1000.0;
            LogToStatus(string.Format("[开火] 起点=({0:F0},{1:F0},{2:F0})m 目标=({3:F0},{4:F0},{5:F0})m 距离={6:F0}km",
                spawnPosMeters.x, spawnPosMeters.y, spawnPosMeters.z,
                targetPosMeters.x, targetPosMeters.y, targetPosMeters.z, distKm));

            combatManager.FireWeapon(1001, combatManager.GetGroupWeapon(1001, 0), targetPosMeters, spawnPosMeters);

            int projCount = GetActiveProjectiles().Count;
            LogToStatus(string.Format("[开火] 当前活跃射弹数={0}", projCount));
        }

        private void TestFireWeapon2()
        {
            if (combatManager == null)
            {
                LogToStatus("[错误] CombatManager 未初始化");
                return;
            }
            if (ship1Object == null)
            {
                LogToStatus("[错误] 测试飞船未创建，请先点击「创建测试飞船」");
                return;
            }

            DVector3 spawnPosMeters = ship2PosMeters;
            DVector3 targetPosMeters = ship1PosMeters;

            double distKm = (spawnPosMeters - targetPosMeters).magnitude / 1000.0;
            LogToStatus(string.Format("[开火2] 起点=({0:F0},{1:F0},{2:F0})m 目标=({3:F0},{4:F0},{5:F0})m 距离={6:F0}km",
                spawnPosMeters.x, spawnPosMeters.y, spawnPosMeters.z,
                targetPosMeters.x, targetPosMeters.y, targetPosMeters.z, distKm));

            combatManager.FireWeapon(1002, combatManager.GetGroupWeapon(1002, 0), targetPosMeters, spawnPosMeters);

            int projCount = GetActiveProjectiles().Count;
            LogToStatus(string.Format("[开火2] 当前活跃射弹/激光数={0}", projCount));
        }

        private void UpdateShipVisuals(int currentTick)
        {
            if (ship1Object != null && spaceGame != null)
            {
                var group1 = spaceGame.GetGroupById(1001);
                if (group1 != null)
                {
                    DVector3 posMeters;
                    if (group1.IsCircularOrbit)
                        posMeters = group1.GetCircularPositionAtTick(currentTick);
                    else
                        posMeters = group1.CurrentOrbit.position;
                    ship1PosMeters = posMeters;
                    float tickDelta = (currentTick - lastShipLogTick) / 60f;
                    // 每60tick(1秒)输出一次位置对比
                    if (lastShipLogTick == -999 || currentTick - lastShipLogTick >= 60)
                    {
                        Vector3 unityPos = (Vector3)posMeters * (float)SpaceConstants.UnitsPerMeter;
                        Log.Message(string.Format("[ShipPos] tick={6} ship1 unity=({0:F2},{1:F2},{2:F2}) meters=({3:F0},{4:F0},{5:F0})",
                            unityPos.x, unityPos.y, unityPos.z, posMeters.x, posMeters.y, posMeters.z, currentTick));
                        lastShipLogTick = currentTick;
                    }
                    ship1Object.transform.position = (Vector3)posMeters * (float)SpaceConstants.UnitsPerMeter;
                }
            }

            if (ship2Object != null && spaceGame != null)
            {
                var group2 = spaceGame.GetGroupById(1002);
                if (group2 != null)
                {
                    DVector3 posMeters;
                    if (group2.IsCircularOrbit)
                        posMeters = group2.GetCircularPositionAtTick(currentTick);
                    else
                        posMeters = group2.CurrentOrbit.position;
                    ship2PosMeters = posMeters;
                    ship2Object.transform.position = (Vector3)posMeters * (float)SpaceConstants.UnitsPerMeter;
                }
            }

            if (ship1Object != null && ship2Object != null)
            {
                cameraTarget = (ship1Object.transform.position + ship2Object.transform.position) * 0.5f;
            }
        }

        private void UpdateProjectileVisuals()
        {
            if (combatManager == null || projectileContainer == null)
            {
                return;
            }

            EnsureMaterialsInitialized();

            var projectiles = GetActiveProjectiles();
            int projectileCount = projectiles.Count;

            while (projectileObjects.Count < projectileCount)
            {
                GameObject proj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                proj.name = "Projectile_" + projectileObjects.Count;
                proj.transform.SetParent(projectileContainer.transform);
                proj.transform.localScale = new Vector3(1.0f, 1.0f, 1.0f);

                Collider c = proj.GetComponent<Collider>();
                if (c != null) Object.Destroy(c);

                MeshRenderer mr = proj.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    mr.material = projectileMat;
                }

                proj.layer = SceneLayer;
                projectileObjects.Add(proj);
            }

            while (projectileObjects.Count > projectileCount && projectileObjects.Count > 0)
            {
                int lastIndex = projectileObjects.Count - 1;
                if (projectileObjects[lastIndex] != null)
                {
                    Object.Destroy(projectileObjects[lastIndex]);
                }
                projectileObjects.RemoveAt(lastIndex);
            }

            for (int i = 0; i < projectileCount && i < projectileObjects.Count; i++)
            {
                if (projectileObjects[i] != null && i < projectiles.Count)
                {
                    var proj = projectiles[i];
                    Vector3 worldPos = (Vector3)proj.position;
                    Vector3 unityPos = worldPos * (float)SpaceConstants.UnitsPerMeter;
                    projectileObjects[i].transform.position = unityPos;

                    // 每 60 tick 输出射弹位置日志
                    int currentTick = Find.TickManager?.TicksGame ?? 0;
                    if (currentTick - lastProjLogTick >= 60)
                    {
                        Log.Message(string.Format("[ProjPos] tick={7} proj{0} meters=({1:F0},{2:F0},{3:F0}) unity=({4:F2},{5:F2},{6:F2}) sphereScale={8}",
                            proj.id, proj.position.x, proj.position.y, proj.position.z,
                            unityPos.x, unityPos.y, unityPos.z, currentTick,
                            projectileObjects[i].transform.localScale.x));
                        lastProjLogTick = currentTick;
                    }
                }
            }
        }

        private static readonly IReadOnlyList<ProjectileInstance> emptyProjectiles = new List<ProjectileInstance>();

        private IReadOnlyList<ProjectileInstance> GetActiveProjectiles()
        {
            if (combatManager == null) return emptyProjectiles;
            return combatManager.ActiveProjectiles;
        }

        private int GetGridActiveCells()
        {
            if (combatManager == null) return 0;
            var combatGrid = Current.Game?.GetComponent<CombatGrid>();
            if (combatGrid == null) return 0;
            return combatGrid.CellCount;
        }

        private void UpdateBoundsVisuals()
        {
            if (spaceGame == null || boundsContainer == null) return;

            EnsureMaterialsInitialized();

            HashSet<int> aliveEntityIds = new HashSet<int>();

            var group1 = spaceGame.GetGroupById(1001);
            var group2 = spaceGame.GetGroupById(1002);

            if (group1 != null)
            {
                foreach (int eid in group1.GetEntityIds())
                {
                    aliveEntityIds.Add(eid);
                    UpdateSingleBounds(eid, group1);
                }
            }
            if (group2 != null)
            {
                foreach (int eid in group2.GetEntityIds())
                {
                    aliveEntityIds.Add(eid);
                    UpdateSingleBounds(eid, group2);
                }
            }

            List<int> toRemove = new List<int>();
            foreach (var kv in boundsObjects)
            {
                if (!aliveEntityIds.Contains(kv.Key))
                {
                    toRemove.Add(kv.Key);
                }
            }
            foreach (int eid in toRemove)
            {
                if (boundsObjects[eid] != null)
                {
                    Object.Destroy(boundsObjects[eid]);
                }
                boundsObjects.Remove(eid);
            }
        }

        private void UpdateSingleBounds(int entityId, SpaceEntityGroup group)
        {
            var entity = spaceGame.GetEntityById(entityId);
            if (entity == null || !entity.IsCollidable) return;

            GameObject boundsObj;
            if (!boundsObjects.TryGetValue(entityId, out boundsObj) || boundsObj == null)
            {
                boundsObj = CreateWireframeCube();
                boundsObj.name = "Bounds_" + entityId;
                boundsObj.transform.SetParent(boundsContainer.transform);
                boundsObj.layer = SceneLayer;
                boundsObjects[entityId] = boundsObj;
            }

            // 直接从飞船视觉对象的 transform 取位置，与视觉 100% 一致
            Vector3 groupPosUnity;
            if (group.GroupId == 1001 && ship1Object != null)
                groupPosUnity = ship1Object.transform.position;
            else if (group.GroupId == 1002 && ship2Object != null)
                groupPosUnity = ship2Object.transform.position;
            else
            {
                DVector3 posMeters;
                if (group.IsCircularOrbit)
                    posMeters = group.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe());
                else
                    posMeters = group.RenderPosition;
                groupPosUnity = (Vector3)posMeters * (float)SpaceConstants.UnitsPerMeter;
            }

            Vector3 entityOffsetUnity = (Vector3)entity.Position * (float)SpaceConstants.UnitsPerMeter;
            Vector3 newPos = groupPosUnity + entityOffsetUnity;

            // 每60 tick 输出bounds位置对比
            if (entityId == 10001 && lastBoundsLogTick + 60 <= (Find.TickManager?.TicksGame ?? 0))
            {
                Log.Message(string.Format("[BoundsPos] entity=10001 shipObj=({0:F2},{1:F2},{2:F2}) bounds=({3:F2},{4:F2},{5:F2}) offset=({6:F2},{7:F2},{8:F2})",
                    ship1Object != null ? ship1Object.transform.position.x : -1f,
                    ship1Object != null ? ship1Object.transform.position.y : -1f,
                    ship1Object != null ? ship1Object.transform.position.z : -1f,
                    newPos.x, newPos.y, newPos.z,
                    entityOffsetUnity.x, entityOffsetUnity.y, entityOffsetUnity.z));
                lastBoundsLogTick = Find.TickManager?.TicksGame ?? 0;
            }

            boundsObj.transform.position = newPos;

            Vector3 size = (Vector3)(entity.BoundsSize * (float)SpaceConstants.UnitsPerMeter);
            boundsObj.transform.localScale = size * VisualScale;
        }

        private GameObject CreateWireframeCube()
        {
            GameObject cube = new GameObject("WireframeCube");

            LineRenderer lr = cube.AddComponent<LineRenderer>();
            lr.material = boundsMat;
            lr.widthMultiplier = 0.3f;
            lr.positionCount = 24;
            lr.loop = false;
            lr.useWorldSpace = false;

            Vector3 half = Vector3.one * 0.5f;
            Vector3[] corners = new Vector3[]
            {
                new Vector3(-half.x, -half.y, -half.z),
                new Vector3(half.x, -half.y, -half.z),
                new Vector3(half.x, half.y, -half.z),
                new Vector3(-half.x, half.y, -half.z),
                new Vector3(-half.x, -half.y, half.z),
                new Vector3(half.x, -half.y, half.z),
                new Vector3(half.x, half.y, half.z),
                new Vector3(-half.x, half.y, half.z),
            };

            int[] lineIndices = new int[]
            {
                0, 1, 1, 2, 2, 3, 3, 0,
                4, 5, 5, 6, 6, 7, 7, 4,
                0, 4, 1, 5, 2, 6, 3, 7
            };

            Vector3[] positions = new Vector3[24];
            for (int i = 0; i < 24; i++)
            {
                positions[i] = corners[lineIndices[i]];
            }
            lr.SetPositions(positions);

            return cube;
        }

        private void UpdateHitEffect()
        {
            if (combatManager == null) return;

            // 激光武器不需要命中大球特效，激光线本身就是视觉反馈
            if (combatManager.HasRecentHit && combatManager.LastHitWeaponType == WeaponType.Ray)
            {
                if (hitEffectObject != null)
                    hitEffectObject.SetActive(false);
                return;
            }

            if (combatManager.HasRecentHit)
            {
                if (hitEffectObject == null)
                {
                    hitEffectObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    hitEffectObject.name = "HitEffect";
                    hitEffectObject.layer = SceneLayer;

                    Collider c = hitEffectObject.GetComponent<Collider>();
                    if (c != null) Object.Destroy(c);

                    MeshRenderer mr = hitEffectObject.GetComponent<MeshRenderer>();
                    if (mr != null)
                    {
                        mr.material = hitEffectMat;
                    }
                }

                Vector3 hitPosUnity = (Vector3)(combatManager.LastHitPosition * SpaceConstants.UnitsPerMeter);
                hitEffectObject.transform.position = hitPosUnity;
                hitEffectObject.transform.localScale = Vector3.one * 5f;

                hitEffectObject.SetActive(true);
            }
            else if (hitEffectObject != null)
            {
                hitEffectObject.SetActive(false);
            }
        }

        private void UpdateLaserBeams()
        {
            if (combatManager == null || laserBeamContainer == null) return;
            EnsureMaterialsInitialized();

            var beams = combatManager.ActiveLaserBeams;

            // 每 120 tick 日志确认激光视觉在更新
            int currentTick = Find.TickManager?.TicksGame ?? 0;
            if (beams.Count > 0 && currentTick - lastLaserLogTick >= 120)
            {
                var b = beams[0];
                Vector3 s = (Vector3)b.startPos;
                Vector3 e = (Vector3)b.endPos;
                Log.Message(string.Format("[LaserVis] tick={0} beams={1} start=({2:F0},{3:F0},{4:F0}) end=({5:F0},{6:F0},{7:F0}) remain={8}",
                    currentTick, beams.Count, s.x, s.y, s.z, e.x, e.y, e.z, b.remainingTicks));
                lastLaserLogTick = currentTick;
            }

            // 清理多余的视觉对象
            while (laserBeamObjects.Count > beams.Count)
            {
                int lastIdx = laserBeamObjects.Count - 1;
                if (laserBeamObjects[lastIdx] != null)
                    Object.Destroy(laserBeamObjects[lastIdx]);
                laserBeamObjects.RemoveAt(lastIdx);
            }

            for (int i = 0; i < beams.Count; i++)
            {
                var beam = beams[i];

                // 创建新的 LineRenderer
                while (laserBeamObjects.Count <= i)
                {
                    GameObject beamObj = new GameObject("LaserBeam_" + i);
                    beamObj.transform.SetParent(laserBeamContainer.transform);
                    beamObj.layer = SceneLayer;
                    LineRenderer lr = beamObj.AddComponent<LineRenderer>();
                    lr.material = laserBeamMat;
                    lr.useWorldSpace = true;
                    lr.positionCount = 2;
                    lr.numCapVertices = 2;
                    laserBeamObjects.Add(beamObj);
                }

                GameObject obj = laserBeamObjects[i];
                if (obj == null) continue;

                LineRenderer line = obj.GetComponent<LineRenderer>();
                if (line == null) continue;

                Vector3 startUnity = (Vector3)beam.startPos * (float)SpaceConstants.UnitsPerMeter;
                Vector3 endUnity = (Vector3)beam.endPos * (float)SpaceConstants.UnitsPerMeter;

                line.SetPosition(0, startUnity);
                line.SetPosition(1, endUnity);

                // 激光核心：亮红色，粗线确保可见
                Color beamColor = new Color(1.0f, 0.05f, 0.05f, 1.0f);
                line.startColor = beamColor;
                line.endColor = beamColor;
                line.startWidth = 6.0f;
                line.endWidth = 6.0f;
            }
        }

        /// <summary>
        /// 为测试创建一组 SpacePart（不使用真实 ShipPartDef，直接构造位置/尺寸）。
        /// 生成：1个主体、1个引擎段、2个武器挂点。
        /// </summary>
        private List<SpacePart> CreateTestParts(int entityId)
        {
            int baseId = entityId * 1000;
            return new List<SpacePart>
            {
                // 主体（中心大块）
                new SpacePart
                {
                    partId = baseId + 1,
                    defName = "Test_Hull",
                    localPosition = new DVector3(0, 0, 0),
                    localBoundsSize = new DVector3(3000, 2000, 4000),
                    hp = 200f
                },
                // 引擎段（后方）
                new SpacePart
                {
                    partId = baseId + 2,
                    defName = "Test_Engine",
                    localPosition = new DVector3(0, 0, -2500),
                    localBoundsSize = new DVector3(1500, 1500, 1000),
                    hp = 100f
                },
                // 左武器挂点
                new SpacePart
                {
                    partId = baseId + 3,
                    defName = "Test_WeaponMount_L",
                    localPosition = new DVector3(-1000, 500, 1500),
                    localBoundsSize = new DVector3(500, 500, 800),
                    hp = 80f
                },
                // 右武器挂点
                new SpacePart
                {
                    partId = baseId + 4,
                    defName = "Test_WeaponMount_R",
                    localPosition = new DVector3(1000, 500, 1500),
                    localBoundsSize = new DVector3(500, 500, 800),
                    hp = 80f
                }
            };
        }

        // ── Part AABB 可视化 ──

        private void UpdatePartBoundsVisuals()
        {
            if (spaceGame == null || partBoundsContainer == null || !showPartBounds) return;
            EnsureMaterialsInitialized();

            HashSet<int> alivePartIds = new HashSet<int>();

            var group1 = spaceGame.GetGroupById(1001);
            var group2 = spaceGame.GetGroupById(1002);

            DrawEntityParts(1001, group1, alivePartIds);
            DrawEntityParts(1002, group2, alivePartIds);

            // 清理已移除的 Part 视觉对象
            List<int> toRemove = new List<int>();
            foreach (var kv in partBoundsObjects)
            {
                foreach (var obj in kv.Value)
                {
                    if (obj != null && !alivePartIds.Contains(obj.GetInstanceID()))
                        Object.Destroy(obj);
                }
                kv.Value.RemoveAll(o => o == null || !alivePartIds.Contains(o.GetInstanceID()));
                if (kv.Value.Count == 0) toRemove.Add(kv.Key);
            }
            foreach (int eid in toRemove)
                partBoundsObjects.Remove(eid);
        }

        private void DrawEntityParts(int groupId, SpaceEntityGroup group, HashSet<int> alivePartIds)
        {
            if (group == null) return;

            Vector3 groupPosUnity = GetGroupVisualPosition(group);

            foreach (int eid in group.GetEntityIds())
            {
                var entity = spaceGame.GetEntityById(eid);
                if (entity == null || entity.Parts.Count == 0) continue;

                Vector3 entityOffsetUnity = (Vector3)entity.Position * (float)SpaceConstants.UnitsPerMeter;
                Vector3 entityWorldPos = groupPosUnity + entityOffsetUnity;

                if (!partBoundsObjects.TryGetValue(eid, out var objList))
                {
                    objList = new List<GameObject>();
                    partBoundsObjects[eid] = objList;
                }

                // 保证对象数量匹配
                while (objList.Count < entity.Parts.Count)
                {
                    var obj = CreateWireframeCubeWithColor(new Color(1.0f, 0.85f, 0.0f, 0.7f), 0.2f);
                    obj.name = "PartBounds_" + eid + "_" + objList.Count;
                    obj.transform.SetParent(partBoundsContainer.transform);
                    obj.layer = SceneLayer;
                    objList.Add(obj);
                }
                while (objList.Count > entity.Parts.Count && objList.Count > 0)
                {
                    int last = objList.Count - 1;
                    if (objList[last] != null) Object.Destroy(objList[last]);
                    objList.RemoveAt(last);
                }

                for (int i = 0; i < entity.Parts.Count && i < objList.Count; i++)
                {
                    var part = entity.Parts[i];
                    if (part.isDestroyed) continue;

                    var obj = objList[i];
                    if (obj == null) continue;

                    Vector3 partLocalPos = (Vector3)part.localPosition * (float)SpaceConstants.UnitsPerMeter;
                    obj.transform.position = entityWorldPos + partLocalPos;
                    obj.transform.localScale = (Vector3)(part.localBoundsSize * (float)SpaceConstants.UnitsPerMeter) * 50f;

                    alivePartIds.Add(obj.GetInstanceID());
                }
            }
        }

        private Vector3 GetGroupVisualPosition(SpaceEntityGroup group)
        {
            if (group.GroupId == 1001 && ship1Object != null)
                return ship1Object.transform.position;
            if (group.GroupId == 1002 && ship2Object != null)
                return ship2Object.transform.position;

            DVector3 posMeters;
            if (group.IsCircularOrbit)
                posMeters = group.GetCircularPositionAtTick(Find.TickManager?.TicksGame ?? 0);
            else
                posMeters = group.RenderPosition;
            return (Vector3)posMeters * (float)SpaceConstants.UnitsPerMeter;
        }

        // ── 射弹轨迹可视化 ──

        private void UpdateProjectileTrails()
        {
            if (combatManager == null || trailContainer == null || !showProjectileTrails) return;
            EnsureMaterialsInitialized();

            var projectiles = GetActiveProjectiles();
            HashSet<int> aliveProjIds = new HashSet<int>();

            foreach (var proj in projectiles)
            {
                aliveProjIds.Add(proj.id);

                if (!projectileTrailPoints.TryGetValue(proj.id, out var points))
                {
                    points = new List<Vector3>();
                    projectileTrailPoints[proj.id] = points;
                }

                Vector3 unityPos = (Vector3)proj.position * (float)SpaceConstants.UnitsPerMeter;
                if (points.Count == 0 || Vector3.Distance(points[points.Count - 1], unityPos) > 1f)
                {
                    points.Add(unityPos);
                    if (points.Count > MaxTrailPoints)
                        points.RemoveAt(0);
                }

                if (!projectileTrails.TryGetValue(proj.id, out var lr) || lr == null)
                {
                    GameObject trailObj = new GameObject("Trail_" + proj.id);
                    trailObj.transform.SetParent(trailContainer.transform);
                    trailObj.layer = SceneLayer;
                    lr = trailObj.AddComponent<LineRenderer>();
                    lr.material = projectileMat;
                    lr.startColor = new Color(1.0f, 1.0f, 0.3f, 0.8f);
                    lr.endColor = new Color(1.0f, 0.5f, 0.0f, 0.2f);
                    lr.startWidth = 2.0f;
                    lr.endWidth = 0.5f;
                    lr.useWorldSpace = true;
                    lr.positionCount = 0;
                    lr.numCapVertices = 4;
                    projectileTrails[proj.id] = lr;
                }

                lr.positionCount = points.Count;
                for (int i = 0; i < points.Count; i++)
                    lr.SetPosition(i, points[i]);
            }

            // 清理已消亡的射弹轨迹
            List<int> toRemove = new List<int>();
            foreach (var kv in projectileTrails)
            {
                if (!aliveProjIds.Contains(kv.Key))
                {
                    toRemove.Add(kv.Key);
                }
            }
            foreach (int id in toRemove)
            {
                if (projectileTrails[id] != null)
                    Object.Destroy(projectileTrails[id].gameObject);
                projectileTrails.Remove(id);
                projectileTrailPoints.Remove(id);
            }
        }

        // ── 辅助：创建带颜色的线框立方体 ──

        private GameObject CreateWireframeCubeWithColor(Color color, float widthMultiplier)
        {
            GameObject cube = new GameObject("WireframeCube");

            LineRenderer lr = cube.AddComponent<LineRenderer>();
            lr.material = boundsMat;
            lr.startColor = color;
            lr.endColor = color;
            lr.widthMultiplier = widthMultiplier;
            lr.positionCount = 24;
            lr.loop = false;
            lr.useWorldSpace = false;

            Vector3 half = Vector3.one * 0.5f;
            Vector3[] corners = new Vector3[]
            {
                new Vector3(-half.x, -half.y, -half.z),
                new Vector3(half.x, -half.y, -half.z),
                new Vector3(half.x, half.y, -half.z),
                new Vector3(-half.x, half.y, -half.z),
                new Vector3(-half.x, -half.y, half.z),
                new Vector3(half.x, -half.y, half.z),
                new Vector3(half.x, half.y, half.z),
                new Vector3(-half.x, half.y, half.z),
            };

            int[] lineIndices = new int[]
            {
                0, 1, 1, 2, 2, 3, 3, 0,
                4, 5, 5, 6, 6, 7, 7, 4,
                0, 4, 1, 5, 2, 6, 3, 7
            };

            Vector3[] positions = new Vector3[24];
            for (int i = 0; i < 24; i++)
                positions[i] = corners[lineIndices[i]];
            lr.SetPositions(positions);

            return cube;
        }

        private void LogToStatus(string message)
        {
            string timestamp = Find.TickManager != null ? Find.TickManager.TicksAbs.ToString() : "?";
            string logLine = "[" + timestamp + "] " + message;

            if (statusLog.Length > 0)
            {
                statusLog.Append("\n");
            }
            statusLog.Append(logLine);

            // 限制日志行数
            string[] lines = statusLog.ToString().Split('\n');
            if (lines.Length > MaxLogLines)
            {
                statusLog.Clear();
                for (int i = lines.Length - MaxLogLines; i < lines.Length; i++)
                {
                    if (i > lines.Length - MaxLogLines)
                    {
                        statusLog.Append("\n");
                    }
                    statusLog.Append(lines[i]);
                }
            }
        }
    }
}
