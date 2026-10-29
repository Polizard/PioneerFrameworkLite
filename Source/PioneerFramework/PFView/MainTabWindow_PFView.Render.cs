using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;
using RimWorld;
using PioneerFramework.Combat;
using PioneerFramework.ShipSkin;

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
            atmosphereDayCapMesh = BuildDoubleSidedDayCapMesh(48, 36);  // 大气壳：受光半球（双面 ⇒ 边缘自然叠两层 = 辉光贴边）
            cubeMesh = BuildCubeMesh();
            cubeMeshHard = BuildCubeHardMesh();
            quadMesh = BuildQuadMesh();

            // 2026-10-13（P2-c）：1.6 用 Standard；1.7 上 `Find("Standard")`=null ⇒ 用自写 GLSL `PFLit`
            // （`1.7/Defs/ShaderDefs` + `1.7/Shaders/*.glsl`）。运行期择一，两版本都不改行为。
            Shader renderShader = Shader.Find("PFLit");
            if (renderShader == null) renderShader = Shader.Find("Standard");
            renderMat = new Material(renderShader) { color = Color.white };
            // 2026-10-14：PFLit 会采样 `_MainTex` ⇒ 无贴图的材质（船/射弹/轨道线）必须给**白底**，
            // 否则采到引擎的未绑定默认值（原版约定 `@default = black`）⇒ 整片发黑。
            // Standard 时代"无贴图=白"是隐式的，这里显式补上（两版本都无害）。
            renderMat.mainTexture = Texture2D.whiteTexture;
            // 下面两行只有 1.6 的 Standard 会用（PFLit 没有这两个 uniform；写未知属性无害）
            renderMat.SetFloat("_Glossiness", 0f);
            renderMat.SetFloat("_Metallic", 0f);    // 纯漫反射，依赖方向光产生明暗
            renderMat.SetVector("_SunDir", new Vector4(1f, 0f, 0f, 0f));   // 每帧由 UpdateLighting 刷新；此处兜底

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
            m.vertices = [new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0),
                new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0)];
            m.triangles = [0, 2, 1, 2, 3, 1];
            m.RecalculateNormals();
            return m;
        }

        /// <summary>单位立方体 Mesh（替代已删除的 ShipPartMeshGenerator，单 part 船统一盒体绘制）。</summary>
        private static Mesh BuildCubeMesh()
        {
            var m = new Mesh();
            m.vertices = [new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f)];
            m.triangles = [0, 2, 1, 0, 3, 2,       // -Z
                4, 5, 6, 4, 6, 7,       // +Z
                0, 1, 5, 0, 5, 4,       // -Y
                2, 3, 7, 2, 7, 6,       // +Y
                1, 2, 6, 1, 6, 5,       // +X
                3, 0, 4, 3, 4, 7        // -X
            ];
            m.RecalculateNormals();
            return m;
        }

        /// <summary>
        /// 单位立方体的**硬法线**版（24 顶点/6 面，每面独立法线）。
        /// 舰船蒙皮专用：三平面投影需要"每个面法线恒定"，且屏幕空间几何边（fwidth）靠面交界法线突变；
        /// 平滑法线的 `cubeMesh` 会让盒子边缘糊掉、贴图投影在棱角处混色。
        /// **旧路径仍用 `cubeMesh`** ⇒ 关掉蒙皮后外观逐字不变。
        /// </summary>
        private static Mesh BuildCubeHardMesh()
        {
            var m = new Mesh();
            // 角点索引沿用 BuildCubeMesh：0(-,-,-) 1(+,-,-) 2(+,+,-) 3(-,+,-) 4(-,-,+) 5(+,-,+) 6(+,+,+) 7(-,+,+)
            Vector3[] corners =
            [
                new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f),
                new(0.5f, 0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f),
                new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f),
                new(0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f),
            ];
            // 每面（quad 四角按 BuildCubeMesh 的三角形顺序排好）+ 面法线
            (int[] quad, Vector3 normal)[] faces =
            [
                (new[] { 0, 1, 2, 3 }, new Vector3(0f, 0f, -1f)),
                (new[] { 4, 5, 6, 7 }, new Vector3(0f, 0f, 1f)),
                (new[] { 0, 1, 5, 4 }, new Vector3(0f, -1f, 0f)),
                (new[] { 2, 3, 7, 6 }, new Vector3(0f, 1f, 0f)),
                (new[] { 1, 2, 6, 5 }, new Vector3(1f, 0f, 0f)),
                (new[] { 3, 0, 4, 7 }, new Vector3(-1f, 0f, 0f)),
            ];
            var verts = new Vector3[24];
            var normals = new Vector3[24];
            var uvs = new Vector2[24];
            var tris = new int[36];
            int vi = 0, ti = 0;
            for (int f = 0; f < faces.Length; f++)
            {
                (int[] quad, Vector3 normal) = faces[f];
                Vector2[] faceUv = [new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)];
                for (int k = 0; k < 4; k++)
                {
                    verts[vi] = corners[quad[k]];
                    normals[vi] = normal;
                    uvs[vi] = faceUv[k];
                    vi++;
                }
                int b = vi - 4;
                if (f == 0)
                {
                    // -Z 面沿用 BuildCubeMesh 的绕序：0,2,1 / 0,3,2
                    tris[ti++] = b; tris[ti++] = b + 2; tris[ti++] = b + 1;
                    tris[ti++] = b; tris[ti++] = b + 3; tris[ti++] = b + 2;
                }
                else
                {
                    tris[ti++] = b; tris[ti++] = b + 1; tris[ti++] = b + 2;
                    tris[ti++] = b; tris[ti++] = b + 2; tris[ti++] = b + 3;
                }
            }
            m.vertices = verts;
            m.normals = normals;
            m.uv = uvs;
            m.triangles = tris;
            m.RecalculateBounds();
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
            // 本窗口只在重绘期手动 Render；关闭 1.7 宿主对启用相机的额外自动捕获。
            renderCamera.enabled = false;
            renderCamera.clearFlags = CameraClearFlags.SolidColor;
            renderCamera.backgroundColor = SpaceBackdropColor;   // 深空色；有大气时每帧由 UpdateAtmosphereSky 混向大气色
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
            RestoreOrInitCamera();

            // 2026-10-19 舰船蒙皮：每次开窗都确保材质就绪（Cleanup 会销毁它；静态构造器只在首次跑）。
            // 取不到 shader/贴图 ⇒ 自动回退旧材质，不抛异常。
            if (!ShipSkinAssets.EnsureInitialized() && ShipSkinSettings.Enabled)
                Log.Warning("[PFView·蒙皮] 不可用（" + (ShipSkinAssets.Failure ?? "未知原因")
                    + "）⇒ 舰船回退 PFLit/Toon 旧材质");

            resourcesCreated = true;
        }

        /// <summary>按当前屏幕尺寸（重）建渲染纹理。窗口尺寸变化后不重建的话，画面会被 StretchToFill 拉变形/发糊。</summary>
        /// <remarks>P0 恒星性能探针（2026-10-28）：`PFViewStellarProbe.RenderScale` 默认 1.0 ⇒ 与历史逐字节一致；
        /// 0.75/0.5 仅用于判"是否 GPU 填充率主导"，不是玩家可见功能。</remarks>
        private void RecreateRenderTexture()
        {
            float scale = Mathf.Clamp(PFViewStellarProbe.RenderScale, 0.25f, 1f);
            int rw = Mathf.Max(1, Mathf.RoundToInt(UI.screenWidth * scale));
            int rh = Mathf.Max(1, Mathf.RoundToInt(UI.screenHeight * scale));
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
            ShipSkinAssets.Destroy();   // 舰船蒙皮材质（2026-10-19；贴图由 ContentFinder 持有）
            DestroySurfaceResources();  // 基底球材质 + 补丁网格/材质同上
            DestroyAtmosphereShellMaterials();  // 大气壳材质同上（网格是静态资源，不销毁）
            DestroyCloudResources();
            DestroyStellarVisualResources();
            if (renderTexture != null) { renderTexture.Release(); UnityEngine.Object.Destroy(renderTexture); renderTexture = null; }
            if (sunLight != null) { UnityEngine.Object.Destroy(sunLight.gameObject); sunLight = null; }
            if (fillLight != null) { UnityEngine.Object.Destroy(fillLight.gameObject); fillLight = null; }
            if (cameraObject != null) { UnityEngine.Object.Destroy(cameraObject); cameraObject = null; }
            renderCamera = null;
            lastSceneRenderFrame = int.MinValue;
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

        /// <summary>计算渲染原点（所有物体的 view-space 位置都相对此点）。
        /// 2026-10-18（U1）：跟**相机焦点**，不再跟 selectedGroupId —— 单击只改选中，不动相机。</summary>
        private DVector3 GetRenderOrigin(double? tickOverride = null)
            => ResolveFocusOrigin(tickOverride);

        /// <summary>
        /// 将编队的天体-相对坐标转换为绝对世界坐标。
        /// GetCircularPositionAtTick / CurrentOrbit.position 返回天体-相对坐标
        /// （对 Planet：相对于 Planet 中心；对 Moon：相对于 Planet 中心）。
        /// 天体绝对位置通过 GetBodyPositionAbs 递归获取。
        /// </summary>

        private DVector3 GetGroupPosAbs(SpaceEntityGroup g, double tick)
        {
            // 原点与物体共享连续渲染时钟；已提交区间内插值，区间外沿用受限外推。
            var space = Current.Game?.GetComponent<SpaceGameComp>();
            DVector3 posSystem = space != null ? space.RenderedGroupPosition(g, tick)
                : g.IsCircularOrbit ? g.GetCircularPositionAtTick(tick) : g.CurrentOrbit.positionSystem.v;

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

        /// <summary>
        /// 绝对系（米）→ 视图系：**先算相对渲染原点的差、再乘分辨率、最后过"视图参考系"旋转**。
        /// ⚠ 2026-10-12「参考系按场景选」的**唯一换系口**：`viewFrameRot` 恒等 = 惯性系（历史行为），
        /// 非恒等 = 固连系（整套场景乘"绝对系→固连系"的旋转 ⇒ 地面静止）。
        /// 放在这里的原因：所有位置（天体/编队/零件/射弹/轨道线）都走这一个口 ⇒ 换系不会漏。
        /// 朝向（自转/姿态/太阳方向）另在各产生点左乘同一个 `viewFrameRot`（见 `Scene.cs`）。
        /// </summary>
        private Vector3 AbsToView(DVector3 worldMeters, DVector3 originMeters)
        {
            return viewFrameRot * (Vector3)((worldMeters - originMeters) * MetersToView);
        }

        /// <summary>AbsToView 的逆变换，保存镜头显示状态用于跨模式过渡与丢失冻结。</summary>
        private DVector3 ViewToAbs(Vector3 viewPosition, DVector3 originMeters)
            => originMeters + (DVector3)(Quaternion.Inverse(viewFrameRot) * viewPosition) * ViewToMeters;

        // ════════════════════════════════════════════════════
        //  视图参考系（2026-10-12「PFView 参考系按场景选」）
        // ════════════════════════════════════════════════════

        /// <summary>档位（工具栏状态）：自动 / 惯性 / 固连。</summary>
        private PFViewFrameMode viewFrameMode = PFViewFrameMode.Auto;

        /// <summary>本帧解析出的"绝对系 → 视图参考系"旋转：惯性 = 恒等；固连 = 自转的逆（符号出自权威，不手写）。</summary>
        private Quaternion viewFrameRot = Quaternion.identity;

        /// <summary>本帧是否固连（读数用）。</summary>
        private bool viewFrameIsFixed;

        private double lastFrameLogTick = double.MinValue;

        /// <summary>
        /// 每帧解析参考系：`Auto` 按**相机高度**判（阈值 = `max(大气厚度一半, 100 km)`，见 `PFViewFrameCore`）；
        /// 固连档取"绝对系 → 固连系"的旋转（`PFViewBodyRotation.EciToFixedRotation`）。
        /// ⚠ 只用**船所在天体**的自转 —— 与地表贴图/补丁用的是同一个旋转 ⇒ 地面真正静止。
        /// </summary>
        private void UpdateViewFrame(CelestialBodyManager mgr, double tick, DVector3 origin)
        {
            int bodyId = ResolveFocusBodyId();
            var def = mgr?.GetBodyById(bodyId);
            double altMeters = CameraAltitudeAboveBodyMeters(mgr, bodyId, tick, origin);
            double thickness = def != null ? def.atmosphereThickness : 0.0;

            viewFrameIsFixed = PFViewFrameCore.UseFixedFrame(viewFrameMode, altMeters, thickness);
            viewFrameRot = viewFrameIsFixed ? PFViewBodyRotation.EciToFixedRotation(bodyId) : Quaternion.identity;

            // 读数（限频：换档即打，或每 300 物理秒一行）——"为什么是这个档"要能一眼看出
            if (tick - lastFrameLogTick > 300.0)
            {
                lastFrameLogTick = tick;
                Log.Message($"[PFView·参考系] {(viewFrameIsFixed ? "固连系（地面静止）" : "惯性系")}｜相机 h={altMeters / 1000.0:F1} km（阈值 {PFViewFrameCore.FixedThresholdMeters(thickness) / 1000.0:F0} km = max(厚度/2, 100 km)）｜body={(def != null ? def.defName : "?")}");
            }
        }

        /// <summary>相机相对某天体**表面**的高度（米）——自动档判据的输入（距离是标量 ⇒ 与换系无关）。</summary>
        private double CameraAltitudeAboveBodyMeters(CelestialBodyManager? mgr, int bodyId, double tick, DVector3 origin)
        {
            if (mgr == null || renderCamera == null) return double.MaxValue;
            var body = mgr.GetBodyById(bodyId);
            if (body == null) return double.MaxValue;
            Vector3 bodyPos = AbsToView(mgr.GetBodyPositionAbs(bodyId, tick), origin);
            float radiusView = Mathf.Max((float)(body.radius * MetersToView), 1f);
            float dist = Vector3.Distance(renderCamera.transform.position, bodyPos);
            return (dist - radiusView) / MetersToView;
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

    }
}
