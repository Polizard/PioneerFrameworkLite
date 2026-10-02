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
    public partial class MainTabWindow_PFView : MainTabWindow
    {
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

            RecreateRenderTexture();
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
            fillLight = light2.AddComponent<Light>();
            fillLight.type = LightType.Directional;
            fillLight.intensity = 0.05f;
            fillLight.color = new Color(0.5f, 0.6f, 1f);

            BuildSystemList();
            InitCameraForCurrentBody();
            resourcesCreated = true;
        }

        /// <summary>按当前屏幕尺寸（重）建渲染纹理。窗口尺寸变化后不重建的话，画面会被 StretchToFill 拉变形/发糊。</summary>
        private void RecreateRenderTexture()
        {
            int rw = Mathf.Max(1, UI.screenWidth);
            int rh = Mathf.Max(1, UI.screenHeight);
            if (renderTexture != null)
            {
                if (renderTexture.width == rw && renderTexture.height == rh) return;
                renderTexture.Release();
                UnityEngine.Object.Destroy(renderTexture);
            }
            renderTexture = new RenderTexture(rw, rh, 24);
            if (renderCamera != null) renderCamera.targetTexture = renderTexture;
        }

        private void CleanupResources()
        {
            DestroyAllReliefMeshes();   // 浮雕网格是运行时 Mesh：不显式销毁会跟着会话泄漏
            if (renderTexture != null) { renderTexture.Release(); UnityEngine.Object.Destroy(renderTexture); renderTexture = null; }
            if (sunLight != null) { UnityEngine.Object.Destroy(sunLight.gameObject); sunLight = null; }
            if (fillLight != null) { UnityEngine.Object.Destroy(fillLight.gameObject); fillLight = null; }
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

            return GetBodyPosAbs(currentSystemBodyId, tick);
        }

        /// <summary>获取选中编队的世界坐标（tick 时刻），无选中返回 zero</summary>
        private DVector3 GetSelectedGroup(double tick)
        {
            if (selectedGroupId < 0) return DVector3.zero;
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return DVector3.zero;

            var g = spaceGame.GetGroupById(selectedGroupId);
            if (g == null) { selectedGroupId = -1; return DVector3.zero; }

            return GetGroupPosAbs(g, tick);
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
        /// 天体绝对位置通过 GetBodyPositionAbs 递归获取。
        /// </summary>

        private DVector3 GetGroupPosAbs(SpaceEntityGroup g, double tick)
        {
            DVector3 posSystem = g.IsCircularOrbit
                ? g.GetCircularPositionAtTick(tick)
                : g.CurrentOrbit.positionSystem.v;

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return posSystem;

            // 找到编队所属的顶层行星
            // ⚠ 2026-09-26（D8）：原先这里手搓"行星→自身 / 卫星→父体"。那正是
            //   `CelestialBodyManager.GetRootPlanetId` 的规则（"沿父链遇到的第一个行星"）⇒ 收敛到它，
            //   不再留第二份（当前天体集下逐值相同；对更深的父链还更正确）。
            int planetBodyId = mgr.GetRootPlanetId(g.CelestialBodyId);

            DVector3 planetPosAbs = planetBodyId >= 0 ? mgr.GetBodyPositionAbs(planetBodyId, tick) : DVector3.zero;
            DVector3 posAbs = posSystem + planetPosAbs;

            return posAbs;
        }

        /// <summary>获取天体世界坐标</summary>
        private static DVector3 GetBodyPosAbs(int bodyId, double tick)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            return mgr != null ? mgr.GetBodyPositionAbs(bodyId, tick) : DVector3.zero;
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
                camDistance = (float)(body.radius * MetersToView) * 2f;
        }

        private static Vector3 AbsToView(DVector3 worldMeters, DVector3 originMeters)
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

            // 水平/垂直 FOV 裁剪。注意 Unity 的 fieldOfView 是**垂直** FOV，水平半角 = atan(tanV·aspect)；
            // 曾把两者写反（水平用 tanV、垂直用 tanV/aspect）⇒ 屏幕上半高之外的可视区域被误剔：
            // 16:9 下水平方向只有真实视野的 ~56%，靠屏幕左右/上下边缘的船与天体标记会凭空消失。
            float halfFov = renderCamera.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float tanV = Mathf.Tan(halfFov);
            float tanH = tanV * renderCamera.aspect;
            float margin = r / Mathf.Max(Mathf.Cos(halfFov), 0.001f);

            float hLimit = cs.z * tanH + margin;
            float vLimit = cs.z * tanV + margin;

            if (Mathf.Abs(cs.x) > hLimit || Mathf.Abs(cs.y) > vLimit)
                return false;

            return true;
        }

        private void HandleInput(Rect inRect)
        {
            var e = Event.current;
            if (e == null) return;

            // 鼠标不在视口上时不接管相机输入（面板按钮/勾选/信息面板/上层菜单占用的区域见 MouseInViewport）
            bool inViewport = MouseInViewport(inRect);

            if (e.type == EventType.ScrollWheel && inViewport)
            {
                camDistance = Mathf.Clamp(camDistance * (1f - e.delta.y * WheelSensitivity), MinDistance, MaxDistance);
                e.Use();
            }
            // 右键拖拽 = 旋转；中键（或 Shift+右键）拖拽 = 平移。
            // 只在视口内起手，避免在面板上拖拽时相机乱转/乱移。
            if (e.type == EventType.MouseDown && inViewport && (e.button == 1 || e.button == 2))
            {
                if (e.button == 2 || e.shift) isDraggingPan = true;
                else isDraggingRight = true;
                lastMousePos = e.mousePosition;
                e.Use();
            }
            // 只在真的拖过时才消费抬起（否则会把别人的事件吃掉，比如上层菜单/取消操作）
            if (e.type == EventType.MouseUp && (e.button == 1 || e.button == 2))
            {
                if (isDraggingRight || isDraggingPan) e.Use();
                isDraggingRight = false;
                isDraggingPan = false;
            }
            if (isDraggingRight && e.type == EventType.MouseDrag && e.button == 1)
            {
                Vector2 d = e.mousePosition - lastMousePos;
                camYaw -= d.x * OrbitSensitivity;
                camPitch = Mathf.Clamp(camPitch - d.y * OrbitSensitivity, MinPitch, MaxPitch);
                lastMousePos = e.mousePosition; e.Use();
            }
            if (isDraggingPan && e.type == EventType.MouseDrag && (e.button == 1 || e.button == 2))
            {
                Vector2 d = e.mousePosition - lastMousePos;
                if (renderCamera != null)
                {
                    // 像素→世界尺度：焦点平面上的 1:1 拖拽（垂直 FOV 45°）
                    float worldPerPx = 2f * camDistance * Mathf.Tan(renderCamera.fieldOfView * 0.5f * Mathf.Deg2Rad)
                                       / Mathf.Max(1f, renderCamera.pixelHeight);
                    camFocusOffset -= (renderCamera.transform.right * d.x + renderCamera.transform.up * d.y) * worldPerPx;
                }
                lastMousePos = e.mousePosition; e.Use();
            }
        }

        private void UpdateCamera()
        {
            if (renderCamera == null) return;
            RecreateRenderTexture();   // 分辨率/窗口尺寸变化 → 重建（尺寸相同则空转）
            Vector3 camViewPos = GetCameraViewPos();
            renderCamera.transform.position = camViewPos;
            renderCamera.transform.LookAt(camFocusOffset, Vector3.up);
            renderCamera.farClipPlane = FarClip;
        }
    }
}
