using UnityEngine;
using Verse;
using RimWorld;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework
{
    [StaticConstructorOnStartup]
    public class Window_SolarSystemMap : Window
    {
        private CelestialBodyManager celestialManager;
        private Camera renderCamera;
        private RenderTexture renderTexture;
        private GameObject cameraObject;

        // ── Unity 场景对象 ──
        private GameObject sceneRoot;
        private readonly Dictionary<int, GameObject> bodyObjects = new Dictionary<int, GameObject>();
        private readonly Dictionary<int, LineRenderer> orbitRenderers = new Dictionary<int, LineRenderer>();
        private readonly Dictionary<int, GameObject> groupObjects = new Dictionary<int, GameObject>();
        private Light sunLight;
        private Light fillLight;

        private Vector3 cameraTargetUnity = Vector3.zero;
        private float cameraDistanceUnity = 4000f;
        private float cameraYaw = 45f;
        private float cameraPitch = 30f;

        private CelestialBodyDef selectedBody;

        // 缓存位置（Unity单位）
        private Dictionary<int, Vector3> bodyPositionsUnity;
        private int lastUpdateTick = -1;
        private Dictionary<int, List<Vector3>> orbitPathCache;
        private Dictionary<int, Vector3> groupPositionsUnity;
        private Dictionary<int, int> groupBodyIds;

        // UI 滚动
        private Vector2 bodyListScrollPos = Vector2.zero;
        private Rect lastWindowRect;

        // 专用渲染层
        private const int SceneLayer = 30;
        private int savedMainCameraCullingMask;

        // 材质（静态初始化）
        private static Material starMat;
        private static Material planetMat;
        private static Material groupMat;

        static Window_SolarSystemMap()
        {
            starMat = new Material(Shader.Find("Standard"));
            starMat.color = new Color(1f, 0.85f, 0.4f);
            starMat.EnableKeyword("_EMISSION");
            starMat.SetColor("_EmissionColor", new Color(1f, 0.7f, 0.2f) * 2f);

            planetMat = new Material(Shader.Find("Standard"));

            groupMat = new Material(Shader.Find("Standard"));
            groupMat.color = Color.cyan;
        }

        private const int EARTH_BODY_ID = 1;

        public Window_SolarSystemMap()
        {
            celestialManager = Current.Game.GetComponent<CelestialBodyManager>();
            doCloseButton = true;
            doCloseX = true;
            forcePause = false;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            resizeable = true;
            draggable = true;
            optionalTitle = "Solar System Map";
            CreateRenderResources();
        }

        public override Vector2 InitialSize => new Vector2(1100f, 750f);

        private void CreateRenderResources()
        {
            // ── 层隔离 ──
            int layerMask = 1 << SceneLayer;
            if (Find.Camera != null)
            {
                savedMainCameraCullingMask = Find.Camera.cullingMask;
                Find.Camera.cullingMask &= ~layerMask;
            }

            // ── Camera ──
            cameraObject = new GameObject("SolarSystemMapCamera");
            renderCamera = cameraObject.AddComponent<Camera>();
            renderCamera.clearFlags = CameraClearFlags.SolidColor;
            renderCamera.backgroundColor = new Color(0.02f, 0.02f, 0.05f);
            renderCamera.cullingMask = layerMask;
            renderCamera.orthographic = false;
            renderCamera.fieldOfView = 60f;
            renderCamera.nearClipPlane = 1f;
            renderCamera.farClipPlane = 50000f;

            int width = Mathf.Max(1, Mathf.RoundToInt(1100f * 0.65f));
            int height = Mathf.Max(1, Mathf.RoundToInt(750f - 80f));
            renderTexture = new RenderTexture(width, height, 24);
            renderCamera.targetTexture = renderTexture;

            // ── Scene Root ──
            sceneRoot = new GameObject("SolarSystemScene");
            sceneRoot.layer = SceneLayer;

            // ── 光照 ──
            var lightObj = new GameObject("SolarSystemSun");
            sunLight = lightObj.AddComponent<Light>();
            sunLight.type = LightType.Directional;
            sunLight.intensity = 1.2f;
            sunLight.color = new Color(1f, 0.95f, 0.85f);
            sunLight.transform.rotation = Quaternion.Euler(30f, 30f, 0f);
            lightObj.transform.SetParent(sceneRoot.transform);
            lightObj.layer = SceneLayer;

            var light2 = new GameObject("SolarSystemFill");
            fillLight = light2.AddComponent<Light>();
            fillLight.type = LightType.Directional;
            fillLight.intensity = 0.4f;
            fillLight.color = new Color(0.5f, 0.6f, 1f);
            fillLight.transform.rotation = Quaternion.Euler(-20f, -120f, 0f);
            light2.transform.SetParent(sceneRoot.transform);
            light2.layer = SceneLayer;

            // ── 材质 ──
            if (starMat == null)
            {
                starMat = new Material(Shader.Find("Standard"))
                {
                    color = new Color(1f, 0.85f, 0.4f)
                };
                starMat.EnableKeyword("_EMISSION");
                starMat.SetColor("_EmissionColor", new Color(1f, 0.7f, 0.2f) * 2f);
            }
            if (planetMat == null)
            {
                planetMat = new Material(Shader.Find("Standard"));
            }
            if (groupMat == null)
            {
                groupMat = new Material(Shader.Find("Standard"))
                {
                    color = Color.cyan
                };
            }
        }

        private void EnsureRenderTextureSize(float viewWidth, float viewHeight)
        {
            int targetWidth = Mathf.Max(1, Mathf.RoundToInt(viewWidth));
            int targetHeight = Mathf.Max(1, Mathf.RoundToInt(viewHeight));
            if (renderTexture != null && (renderTexture.width != targetWidth || renderTexture.height != targetHeight))
            {
                var newRT = new RenderTexture(targetWidth, targetHeight, 24);
                renderCamera.targetTexture = newRT;
                renderTexture.Release();
                renderTexture = newRT;
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            int currentTick = (int)System.Math.Round(TimeScaleManager.GetPhysicsTickSafe());
            if (currentTick != lastUpdateTick)
            {
                UpdateBodyPositions(currentTick);
                SyncSceneObjects();
                lastUpdateTick = currentTick;
            }

            float rightWidth = Mathf.Max(220f, inRect.width * 0.35f);
            float leftWidth = inRect.width - rightWidth - 10f;
            float contentHeight = inRect.height - 40f;

            Rect viewRect = new Rect(inRect.x, inRect.y, leftWidth, contentHeight);
            Rect rightRect = new Rect(inRect.x + leftWidth + 10f, inRect.y, rightWidth, contentHeight);

            if (windowRect != lastWindowRect)
            {
                EnsureRenderTextureSize(viewRect.width, viewRect.height);
                lastWindowRect = windowRect;
            }

            if (viewRect.Contains(Event.current.mousePosition))
                HandleCameraInput();
            UpdateCameraTransform();

            if (renderCamera != null && renderTexture != null)
            {
                renderCamera.Render();
                GUI.DrawTexture(viewRect, renderTexture);
            }
            else
                Widgets.Label(viewRect, "Camera not ready");

            DrawRightPanel(rightRect);

            string controlsText = "LMB拖拽: 旋转视角 | 滚轮: 缩放 | RMB拖拽: 平移";
            Text.Anchor = TextAnchor.LowerLeft;
            Widgets.Label(new Rect(inRect.x, inRect.y + contentHeight + 2f, leftWidth, 18f), controlsText);
            Text.Anchor = TextAnchor.UpperLeft;
        }

        // ═══════════════════════════════════════
        //  场景对象同步
        // ═══════════════════════════════════════

        private void SyncSceneObjects()
        {
            if (sceneRoot == null) return;
            if (bodyPositionsUnity == null) return;

            float toUnity = (float)SpaceConstants.UnitsPerMeter;

            // ── 天体对象 ──
            // 清理已移除的天体
            var toRemove = new List<int>();
            foreach (var kv in bodyObjects)
                if (!bodyPositionsUnity.ContainsKey(kv.Key))
                    toRemove.Add(kv.Key);
            foreach (int id in toRemove)
            {
                Object.Destroy(bodyObjects[id]);
                bodyObjects.Remove(id);
            }

            foreach (var kv in bodyPositionsUnity)
            {
                int bodyId = kv.Key;
                Vector3 pos = kv.Value;
                var body = celestialManager.GetBodyById(bodyId);
                if (body == null) continue;

                if (!bodyObjects.TryGetValue(bodyId, out var go) || go == null)
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = "Body_" + body.defName;
                    go.layer = SceneLayer;
                    go.transform.SetParent(sceneRoot.transform);

                    var collider = go.GetComponent<Collider>();
                    if (collider != null) Object.Destroy(collider);

                    var mr = go.GetComponent<MeshRenderer>();

                    if (body.bodyType == CelestialBodyType.Star)
                    {
                        mr.material = new Material(starMat);
                        // 恒星发光光晕：加一个更大的半透明球
                        var glow = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        glow.name = "Body_" + body.defName + "_Glow";
                        glow.layer = SceneLayer;
                        glow.transform.SetParent(go.transform);
                        var gc = glow.GetComponent<Collider>();
                        if (gc != null) Object.Destroy(gc);
                        var gm = glow.GetComponent<MeshRenderer>();
                        gm.material = new Material(Shader.Find("Standard"))
                        {
                            color = new Color(1f, 0.8f, 0.3f, 0.08f)
                        };
                        gm.material.SetFloat("_Mode", 3);
                        gm.material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        gm.material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        gm.material.SetInt("_ZWrite", 0);
                        gm.material.DisableKeyword("_ALPHATEST_ON");
                        gm.material.EnableKeyword("_ALPHABLEND_ON");
                        gm.material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                        gm.material.renderQueue = 3000;
                        glow.transform.localScale = Vector3.one * 2.5f;
                    }
                    else
                    {
                        mr.material = new Material(planetMat);
                        mr.material.color = CelestialBodyManager.GetBodyColor(body);
                    }

                    bodyObjects[bodyId] = go;
                }

                // 更新位置和大小
                float radius = (float)(body.radius * toUnity);
                float minSize = cameraDistanceUnity * 0.008f;
                float size = Mathf.Max(radius, minSize);
                if (body.bodyType == CelestialBodyType.Star)
                    size = Mathf.Max(size, cameraDistanceUnity * 0.04f);

                go.transform.position = pos;
                go.transform.localScale = Vector3.one * size;

                // 选中高亮：调亮颜色
                var renderer = go.GetComponent<MeshRenderer>();
                if (renderer != null && body.bodyType != CelestialBodyType.Star)
                {
                    Color baseColor = CelestialBodyManager.GetBodyColor(body);
                    renderer.material.color = (body == selectedBody) ?
                        Color.Lerp(baseColor, Color.yellow, 0.5f) : baseColor;
                }
            }

            // ── 轨道线 ──
            if (orbitPathCache != null)
            {
                // 清理已移除的轨道
                toRemove.Clear();
                foreach (var kv in orbitRenderers)
                    if (!orbitPathCache.ContainsKey(kv.Key))
                        toRemove.Add(kv.Key);
                foreach (int id in toRemove)
                {
                    Object.Destroy(orbitRenderers[id].gameObject);
                    orbitRenderers.Remove(id);
                }

                foreach (var kv in orbitPathCache)
                {
                    int bodyId = kv.Key;
                    var points = kv.Value;

                    if (!orbitRenderers.TryGetValue(bodyId, out var lr) || lr == null)
                    {
                        var go = new GameObject("Orbit_" + bodyId);
                        go.layer = SceneLayer;
                        go.transform.SetParent(sceneRoot.transform);
                        lr = go.AddComponent<LineRenderer>();
                        lr.material = new Material(Shader.Find("Hidden/Internal-Colored"));
                        lr.material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        lr.material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        lr.material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                        lr.material.SetInt("_ZWrite", 0);
                        lr.startColor = new Color(0.3f, 0.3f, 0.5f, 0.4f);
                        lr.endColor = new Color(0.3f, 0.3f, 0.5f, 0.4f);
                        lr.startWidth = 2f;
                        lr.endWidth = 2f;
                        lr.loop = true;
                        orbitRenderers[bodyId] = lr;
                    }

                    lr.positionCount = points.Count;
                    lr.SetPositions(points.ToArray());
                }
            }

            // ── 群组标记 ──
            if (groupPositionsUnity != null)
            {
                toRemove.Clear();
                foreach (var kv in groupObjects)
                    if (!groupPositionsUnity.ContainsKey(kv.Key))
                        toRemove.Add(kv.Key);
                foreach (int id in toRemove)
                {
                    Object.Destroy(groupObjects[id]);
                    groupObjects.Remove(id);
                }

                foreach (var kv in groupPositionsUnity)
                {
                    int groupId = kv.Key;
                    Vector3 pos = kv.Value;

                    if (!groupObjects.TryGetValue(groupId, out var go) || go == null)
                    {
                        go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        go.name = "Group_" + groupId;
                        go.layer = SceneLayer;
                        go.transform.SetParent(sceneRoot.transform);
                        var collider = go.GetComponent<Collider>();
                        if (collider != null) Object.Destroy(collider);
                        var mr = go.GetComponent<MeshRenderer>();
                        mr.material = new Material(groupMat);
                        groupObjects[groupId] = go;
                    }

                    float size = cameraDistanceUnity * 0.004f;
                    go.transform.position = pos;
                    go.transform.localScale = Vector3.one * size;
                }
            }
        }

        // ═══════════════════════════════════════
        //  位置更新（原逻辑）
        // ═══════════════════════════════════════

        private void UpdateBodyPositions(int currentTick)
        {
            if (celestialManager == null) return;
            var bodies = celestialManager.GetAllBodies();
            float toUnity = (float)SpaceConstants.UnitsPerMeter;

            bodyPositionsUnity = new Dictionary<int, Vector3>(bodies.Count);
            orbitPathCache = new Dictionary<int, List<Vector3>>(bodies.Count);

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var groups = spaceGame?.GetAllGroups()?.ToList();
            groupPositionsUnity = new Dictionary<int, Vector3>();
            groupBodyIds = new Dictionary<int, int>();
            if (groups != null)
            {
                foreach (var g in groups)
                {
                    if (!g.IsActive) continue;
                    groupPositionsUnity[g.GroupId] = GetGroupWorldPosUnity(g, currentTick);
                    groupBodyIds[g.GroupId] = g.CelestialBodyId;
                }
            }

            foreach (var body in bodies)
            {
                Vector3 worldPos = (Vector3)celestialManager.GetBodyWorldPosition(body.bodyId, currentTick);
                bodyPositionsUnity[body.bodyId] = worldPos * toUnity;

                if (body.HasOrbit)
                {
                    var path = celestialManager.GetOrbitPathUnity(body.bodyId, currentTick, 64);
                    if (path != null)
                        orbitPathCache[body.bodyId] = path;
                }
            }
        }

        // ═══════════════════════════════════════
        //  右侧面板（原逻辑，不变）
        // ═══════════════════════════════════════

        private void DrawRightPanel(Rect rect)
        {
            if (celestialManager == null)
            {
                Widgets.Label(rect, "CelestialBodyManager not available.");
                return;
            }
            var bodies = celestialManager.GetAllBodies();
            if (bodies.Count == 0)
            {
                Widgets.Label(rect, "No celestial bodies defined.");
                return;
            }
            float listHeight = rect.height * 0.35f;
            Rect listRect = new Rect(rect.x, rect.y, rect.width, listHeight);
            Rect infoRect = new Rect(rect.x, rect.y + listHeight + 5f, rect.width, rect.height - listHeight - 5f);
            DrawBodyList(listRect, bodies);
            DrawBodyInfo(infoRect);
        }

        private void DrawBodyList(Rect rect, List<CelestialBodyDef> bodies)
        {
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 22f), "--- Celestial Bodies ---");
            rect.yMin += 25f;
            float lineHeight = 28f;
            float totalHeight = bodies.Count * lineHeight;
            Rect viewRect = new Rect(0f, 0f, rect.width - 16f, totalHeight);
            Widgets.BeginScrollView(rect, ref bodyListScrollPos, viewRect);
            float y = 0f;
            foreach (var body in bodies)
            {
                Color bodyColor = CelestialBodyManager.GetBodyColor(body);
                string typeStr = GetBodyTypeString(body.bodyType);
                string label = string.Format("{0} ({1})", body.label, typeStr);
                Rect fullRow = new Rect(0f, y, viewRect.width, lineHeight - 2f);
                if (body == selectedBody)
                    Widgets.DrawBoxSolid(fullRow, new Color(0.3f, 0.3f, 0.5f, 0.4f));
                Widgets.DrawBoxSolid(new Rect(fullRow.x, fullRow.y, 4f, fullRow.height), bodyColor);
                Rect btnRect = new Rect(fullRow.x + 6f, fullRow.y, fullRow.width - 6f, fullRow.height);
                if (Widgets.ButtonText(btnRect, label))
                    selectedBody = body;
                y += lineHeight;
            }
            Widgets.EndScrollView();
        }

        private void DrawBodyInfo(Rect rect)
        {
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 22f), "--- Body Info ---");
            rect.yMin += 25f;
            if (selectedBody == null)
            {
                Widgets.Label(rect, "Click a body to see info.");
                return;
            }
            string info = string.Format("Name: {0} ({1})\n", selectedBody.label, selectedBody.defName);
            info += string.Format("Type: {0}\n", GetBodyTypeString(selectedBody.bodyType));
            info += string.Format("Body ID: {0}\n", selectedBody.bodyId);
            info += string.Format("Radius: {0:N0} m\n", selectedBody.radius);
            info += string.Format("GM: {0:E2} m\x00B3/s\x00B2\n", selectedBody.gravitationalParameter);
            info += string.Format("Surface: {0}\n", selectedBody.hasSurface ? "Yes" : "No");
            if (selectedBody.HasOrbit)
            {
                info += string.Format("Orbit Radius: {0:N0} m\n", selectedBody.orbitRadius);
                info += string.Format("Orbit Period: {0:N0} s ({1:F2} h)\n", selectedBody.orbitPeriod, selectedBody.orbitPeriod / 3600.0);
                info += string.Format("Orbit Normal: ({0:F4}, {1:F4}, {2:F4})\n",
                    selectedBody.orbitNormal.x, selectedBody.orbitNormal.y, selectedBody.orbitNormal.z);
                info += string.Format("Phase: {0:F2} rad\n", selectedBody.initialPhase);
                if (bodyPositionsUnity != null && bodyPositionsUnity.TryGetValue(selectedBody.bodyId, out var posU))
                {
                    DVector3 posM = (DVector3)posU / SpaceConstants.UnitsPerMeter;
                    info += string.Format("Pos: ({0:N0}, {1:N0}, {2:N0}) m\n", posM.x, posM.y, posM.z);
                }
            }
            if (selectedBody.ParentBodyId >= 0)
            {
                var parent = celestialManager.GetBodyById(selectedBody.ParentBodyId);
                if (parent != null)
                    info += string.Format("Parent: {0} (ID {1})\n", parent.label, selectedBody.ParentBodyId);
            }
            Widgets.Label(rect, info);
        }

        private static string GetBodyTypeString(CelestialBodyType type)
        {
            switch (type)
            {
                case CelestialBodyType.Star: return "Star";
                case CelestialBodyType.Planet: return "Planet";
                case CelestialBodyType.Moon: return "Moon";
                default: return "Unknown";
            }
        }

        // ═══════════════════════════════════════
        //  相机控制
        // ═══════════════════════════════════════

        private void HandleCameraInput()
        {
            Event e = Event.current;
            if (e.type == EventType.MouseDrag)
            {
                if (e.button == 0)
                {
                    cameraYaw += e.delta.x * 0.5f;
                    cameraPitch -= e.delta.y * 0.5f;
                    cameraPitch = Mathf.Clamp(cameraPitch, 5f, 85f);
                    e.Use();
                }
                else if (e.button == 1)
                {
                    Vector3 right = renderCamera.transform.right;
                    Vector3 up = renderCamera.transform.up;
                    float moveSpeed = cameraDistanceUnity * 0.01f;
                    cameraTargetUnity += right * (-e.delta.x * moveSpeed) + up * (e.delta.y * moveSpeed);
                    e.Use();
                }
            }
            else if (e.type == EventType.ScrollWheel)
            {
                cameraDistanceUnity -= e.delta.y * 10f;
                cameraDistanceUnity = Mathf.Clamp(cameraDistanceUnity, 100f, 30000f);
                e.Use();
            }
        }

        private void UpdateCameraTransform()
        {
            if (renderCamera == null) return;
            Quaternion rotation = Quaternion.Euler(cameraPitch, cameraYaw, 0);
            Vector3 offset = rotation * new Vector3(0, 0, -cameraDistanceUnity);
            renderCamera.transform.position = cameraTargetUnity + offset;
            renderCamera.transform.LookAt(cameraTargetUnity);
        }

        private Vector3 GetGroupWorldPosUnity(SpaceEntityGroup group, int currentTick)
        {
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

        // ═══════════════════════════════════════
        //  清理
        // ═══════════════════════════════════════

        public override void PostClose()
        {
            base.PostClose();

            if (Find.Camera != null && savedMainCameraCullingMask != 0)
                Find.Camera.cullingMask = savedMainCameraCullingMask;

            if (renderTexture != null)
            {
                renderTexture.Release();
                renderTexture = null;
            }
            if (cameraObject != null)
            {
                Object.Destroy(cameraObject);
                cameraObject = null;
            }
            if (sceneRoot != null)
            {
                Object.Destroy(sceneRoot);
                sceneRoot = null;
            }
            bodyObjects.Clear();
            orbitRenderers.Clear();
            groupObjects.Clear();
        }
    }
}
