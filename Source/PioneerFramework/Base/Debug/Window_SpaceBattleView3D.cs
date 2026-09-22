using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;
using PioneerFramework.Combat;
using PioneerFramework.Launch;

namespace PioneerFramework
{
    /// <summary>
    /// 太空战 3D 视角窗口。
    /// 从指定 Group（飞船）的船头位置观察战场，
    /// 对于有设计数据的飞船渲染实际部件，否则回退为长方体。
    /// </summary>
    [StaticConstructorOnStartup]
    public class Window_SpaceBattleView3D : Window
    {
        private readonly SpaceEntityGroup observerGroup;
        private CelestialBodyManager celestialManager;
        private SpaceGameComp spaceGame;

        // ── 3D 渲染资源 ──
        private Camera renderCamera;
        private RenderTexture renderTexture;
        private GameObject cameraObject;
        private GameObject sceneRoot;
        private Light sunLight;
        private Light fillLight;

        // ── 场景对象 ──
        private GameObject observerShipContainer;        // 观察者飞船的容器（含设计部件或长方体）
        private readonly Dictionary<int, GameObject> otherShipContainers = new Dictionary<int, GameObject>();

        // ── 本行星系天体 ──
        private readonly Dictionary<int, GameObject> sameSystemBodyObjects = new Dictionary<int, GameObject>();
        private HashSet<int> sameSystemBodyIds = new HashSet<int>();

        // ── 非本行星系天体（圆片）──
        private readonly Dictionary<int, GameObject> billboardObjects = new Dictionary<int, GameObject>();
        private GameObject sunBillboardObject;
        private GameObject sunBillboardGlowObject;

        // ── 射弹视觉（3D 场景中的小球，池化管理）──
        private readonly List<GameObject> projectileObjects = new List<GameObject>();
        private GameObject projectileContainer;
        private Material projectileMat;

        // ── 相机控制 ──
        private Vector3 observerPosUnity;
        private Vector3 forwardDirection;
        private float lookYaw;
        private float lookPitch;

        // ── 缓存 ──
        private int lastUpdateTick = -1;
        private Dictionary<int, Vector3> bodyPositionsUnity;

        // ── 殖民地位置标记（天地映射检测用：玩家殖民地在行星表面的 ECI 位置）──
        private GameObject colonyMarkerObject;
        private bool colonyMarkerLogged;

        // ── 飞船参数（无设计数据的回退长方体尺寸）──
        private const float FallbackShipLength = 2.0f;
        private const float FallbackShipWidth = 0.8f;
        private const float FallbackShipHeight = 0.5f;
        private const float OtherFallbackShipSize = 1.2f;

        // ── 渲染常量 ──
        private const int SceneLayer = 30;
        private const float NearClip = 0.1f;
        private const float FarClip = 500000f;
        private const float MinBodySize = 0.5f;
        private const float MaxSunDist = 30000f;
        private const float BillboardMinSize = 2f;

        // ── 材质 ──
        private static Material shipMat;
        private static Material otherShipMat;
        private static Material starMat;
        private static Material planetMat;
        private static Material moonMat;
        private static Material billboardMat;
        private static Material designerCubeMat;

        static Window_SpaceBattleView3D()
        {
            InitMaterials();
        }

        // ════════════════════════════════════════════════
        //  构造 & 初始化
        // ════════════════════════════════════════════════

        public Window_SpaceBattleView3D(SpaceEntityGroup group)
        {
            observerGroup = group;
            celestialManager = Current.Game?.GetComponent<CelestialBodyManager>();
            spaceGame = Current.Game?.GetComponent<SpaceGameComp>();

            doCloseButton = true;
            doCloseX = true;
            forcePause = false;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            resizeable = true;
            draggable = true;
            optionalTitle = string.Format("太空战视角 — Group {0}", group.GroupId);

            lookYaw = 0f;
            lookPitch = 0f;

            CreateRenderResources();
            UpdateObserverState();
            ComputeSameSystemBodyIds();
        }

        public override Vector2 InitialSize => new Vector2(1100f, 750f);

        // ════════════════════════════════════════════════
        //  渲染资源
        // ════════════════════════════════════════════════

        private void CreateRenderResources()
        {
            if (Find.Camera != null)
                Find.Camera.cullingMask &= ~(1 << SceneLayer);

            cameraObject = new GameObject("SpaceBattleCamera");
            renderCamera = cameraObject.AddComponent<Camera>();
            renderCamera.clearFlags = CameraClearFlags.SolidColor;
            renderCamera.backgroundColor = new Color(0.005f, 0.005f, 0.03f);
            renderCamera.cullingMask = 1 << SceneLayer;
            renderCamera.orthographic = false;
            renderCamera.fieldOfView = 70f;
            renderCamera.nearClipPlane = NearClip;
            renderCamera.farClipPlane = FarClip;

            int w = Mathf.Max(1, Mathf.RoundToInt(1100f * 0.65f));
            int h = Mathf.Max(1, Mathf.RoundToInt(750f - 80f));
            renderTexture = new RenderTexture(w, h, 24);
            renderCamera.targetTexture = renderTexture;

            sceneRoot = new GameObject("SpaceBattleScene");
            sceneRoot.layer = SceneLayer;

            var lightObj = new GameObject("BattleSun");
            sunLight = lightObj.AddComponent<Light>();
            sunLight.type = LightType.Directional;
            sunLight.intensity = 1.5f;
            sunLight.color = new Color(1f, 0.95f, 0.85f);
            sunLight.transform.rotation = Quaternion.Euler(30f, 30f, 0f);
            lightObj.transform.SetParent(sceneRoot.transform);
            lightObj.layer = SceneLayer;

            var light2 = new GameObject("BattleFill");
            fillLight = light2.AddComponent<Light>();
            fillLight.type = LightType.Directional;
            fillLight.intensity = 0.3f;
            fillLight.color = new Color(0.4f, 0.5f, 1f);
            fillLight.transform.rotation = Quaternion.Euler(-20f, -120f, 0f);
            light2.transform.SetParent(sceneRoot.transform);
            light2.layer = SceneLayer;

            InitMaterials();
        }

        private static void InitMaterials()
        {
            if (shipMat == null)
            {
                shipMat = CreateTransparentMaterial(new Color(0.2f, 0.8f, 1.0f, 0.7f));
            }
            if (otherShipMat == null)
            {
                otherShipMat = new Material(Shader.Find("Standard"))
                {
                    color = new Color(1.0f, 0.3f, 0.3f)
                };
            }
            if (starMat == null)
            {
                starMat = new Material(Shader.Find("Standard"))
                {
                    color = new Color(1f, 0.85f, 0.4f)
                };
                starMat.EnableKeyword("_EMISSION");
                starMat.SetColor("_EmissionColor", new Color(1f, 0.7f, 0.2f) * 2f);
            }
            if (planetMat == null) planetMat = new Material(Shader.Find("Standard"));
            if (moonMat == null)
            {
                moonMat = new Material(Shader.Find("Standard"))
                {
                    color = new Color(0.7f, 0.7f, 0.7f)
                };
            }
            if (billboardMat == null)
            {
                billboardMat = new Material(Shader.Find("Standard"))
                {
                    color = Color.white
                };
            }
            // 设计器部件材质
            if (designerCubeMat == null) designerCubeMat = new Material(Shader.Find("Standard")) { color = new Color(0.3f, 0.6f, 1.0f) };
        }

        private static Material CreateTransparentMaterial(Color color)
        {
            var mat = new Material(Shader.Find("Standard"))
            {
                color = color
            };
            mat.SetFloat("_Mode", 3);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
            return mat;
        }

        private void EnsureRenderTextureSize(float w, float h)
        {
            int tw = Mathf.Max(1, Mathf.RoundToInt(w));
            int th = Mathf.Max(1, Mathf.RoundToInt(h));
            if (renderTexture == null || renderTexture.width != tw || renderTexture.height != th)
            {
                var newRT = new RenderTexture(tw, th, 24);
                renderCamera.targetTexture = newRT;
                if (renderTexture != null) renderTexture.Release();
                renderTexture = newRT;
            }
        }

        // ════════════════════════════════════════════════
        //  工具函数
        // ════════════════════════════════════════════════

        private void ComputeSameSystemBodyIds()
        {
            sameSystemBodyIds.Clear();
            if (observerGroup == null || celestialManager == null) return;

            int observerBodyId = observerGroup.CelestialBodyId;
            if (observerBodyId < 0) return;

            int rootPlanetId = celestialManager.GetRootPlanetId(observerBodyId);
            if (rootPlanetId < 0)
            {
                foreach (var body in celestialManager.GetAllBodies())
                {
                    if (body.bodyType != CelestialBodyType.Star)
                        sameSystemBodyIds.Add(body.bodyId);
                }
                return;
            }

            sameSystemBodyIds.Add(rootPlanetId);
            var planetDef = celestialManager.GetBodyById(rootPlanetId);
            if (planetDef != null)
            {
                foreach (var moon in planetDef.moonBodies)
                    sameSystemBodyIds.Add(moon.bodyId);
            }
        }

        private bool IsSameSystem(int bodyId) => sameSystemBodyIds.Contains(bodyId);

        private Vector3 GetGroupUnityPos(SpaceEntityGroup group)
        {
            int tick = (int)Math.Round(TimeScaleManager.GetPhysicsTickSafe());
            DVector3 posMeters;
            if (group.IsCircularOrbit)
                posMeters = group.GetCircularPositionAtTick(tick);
            else
                posMeters = group.RenderPosition;
            return (Vector3)(posMeters * SpaceConstants.UnitsPerMeter);
        }

        private Vector3 GetGroupForward(SpaceEntityGroup group)
        {
            DVector3 vel;
            if (group.IsCircularOrbit)
            {
                int tick = (int)Math.Round(TimeScaleManager.GetPhysicsTickSafe());
                DVector3 pos = group.GetCircularPositionAtTick(tick);
                DVector3 radial = pos.normalized;
                DVector3 normal = (DVector3)group.circularOrbitNormal;
                if (normal.sqrMagnitude < 0.001) normal = DVector3.up;
                DVector3 tangent = DVector3.Cross(normal, radial).normalized;
                vel = tangent * group.circularAngularVelocity * group.circularRadius;
            }
            else
            {
                vel = group.CurrentOrbit.velocity;
            }

            if (vel.sqrMagnitude < 0.01) return Vector3.forward;
            return ((Vector3)vel).normalized;
        }

        private void UpdateObserverState()
        {
            if (observerGroup == null || !observerGroup.IsActive) return;
            observerPosUnity = GetGroupUnityPos(observerGroup);
            forwardDirection = GetGroupForward(observerGroup);
        }

        private void UpdateBodyPositions()
        {
            if (celestialManager == null) return;
            var bodies = celestialManager.GetAllBodies();
            float toUnity = (float)SpaceConstants.UnitsPerMeter;
            // ⚠ 2026-09-23 参考系统一：本窗口的**船**走系统系（`GetGroupUnityPos`），天体原来却走**绝对系**
            //   （`GetBodyWorldPosition`，递归到恒星）⇒ 两者系统性错位 2,400 Unity 单位（地球绕日 120,000 km
            //   ÷ 50 km/单位）。现在天体也走系统系（`ViewFrame.BodySystemPosition`），并**保留 double tick**
            //   （int 化在 1/60× 流速下会让天体位置离散跳变）。
            double tick = TimeScaleManager.GetPhysicsTickSafe();

            bodyPositionsUnity = new Dictionary<int, Vector3>(bodies.Count);
            foreach (var body in bodies)
            {
                bodyPositionsUnity[body.bodyId] =
                    (Vector3)(ViewFrame.BodySystemPosition(body.bodyId, tick) * toUnity);
            }
            ComputeSameSystemBodyIds();
        }

        // ════════════════════════════════════════════════
        //  场景同步
        // ════════════════════════════════════════════════

        private void SyncScene()
        {
            if (sceneRoot == null || bodyPositionsUnity == null) return;
            UpdateObserverState();

            // ── 观察者飞船 ──
            SyncObserverShip();

            // ── 其他飞船 ──
            SyncOtherShips();

            // ── 本行星系天体 ──
            SyncSameSystemBodies();

            // ── 玩家殖民地标记（天地映射检测）──
            SyncColonyMarker();

            // ── 非本行星系天体 ──
            SyncOtherBodiesAsBillboards();

            // ── 太阳 ──
            SyncSunBillboard();

            // ── 射弹 ──
            UpdateProjectileVisuals();
        }

        // ════════════════════════════════════════════════
        //  射弹渲染（池化小球，跟随 CombatManager.ActiveProjectiles）
        // ════════════════════════════════════════════════

        private void UpdateProjectileVisuals()
        {
            var cm = Current.Game?.GetComponent<CombatManager>();
            if (cm == null || sceneRoot == null)
            {
                ClearProjectileObjects();
                return;
            }
            var projectiles = cm.ActiveProjectiles;
            if (projectileContainer == null)
            {
                projectileContainer = new GameObject("Projectiles");
                projectileContainer.transform.SetParent(sceneRoot.transform);
            }
            if (projectileMat == null)
            {
                projectileMat = new Material(Shader.Find("Standard")) { color = new Color(1f, 0.9f, 0.3f) };
                projectileMat.EnableKeyword("_EMISSION");
                projectileMat.SetColor("_EmissionColor", new Color(1f, 0.7f, 0.1f) * 2f);
            }

            while (projectileObjects.Count < projectiles.Count)
            {
                GameObject proj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                proj.name = "Projectile_" + projectileObjects.Count;
                proj.transform.SetParent(projectileContainer.transform);
                proj.transform.localScale = Vector3.one * 0.02f; // 1000m（1 Unity = 50km）
                var col = proj.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.Destroy(col);
                var mr = proj.GetComponent<MeshRenderer>();
                if (mr != null) mr.material = projectileMat;
                proj.layer = SceneLayer;
                projectileObjects.Add(proj);
            }
            while (projectileObjects.Count > projectiles.Count)
            {
                int last = projectileObjects.Count - 1;
                UnityEngine.Object.Destroy(projectileObjects[last]);
                projectileObjects.RemoveAt(last);
            }

            float toUnity = (float)SpaceConstants.UnitsPerMeter;
            for (int i = 0; i < projectiles.Count; i++)
                projectileObjects[i].transform.position = (Vector3)(projectiles[i].position * toUnity);
        }

        private void ClearProjectileObjects()
        {
            foreach (var go in projectileObjects)
                if (go != null) UnityEngine.Object.Destroy(go);
            projectileObjects.Clear();
        }

        // ════════════════════════════════════════════════
        //  飞船渲染（实际部件 / 回退长方体）
        // ════════════════════════════════════════════════

        /// <summary>
        /// 渲染观察者飞船。有运行时 Parts 时从 Parts 渲染，否则用半透明长方体。
        /// （Lite 裁剪：Ship3D 设计分支已删除）
        /// </summary>
        private void SyncObserverShip()
        {
            SpaceEntity observerEntity = GetFirstEntity(observerGroup);

            if (observerShipContainer != null)
            {
                UnityEngine.Object.Destroy(observerShipContainer);
                observerShipContainer = null;
            }

            observerShipContainer = new GameObject("ObserverShip");
            observerShipContainer.layer = SceneLayer;
            observerShipContainer.transform.SetParent(sceneRoot.transform);

            if (observerEntity != null && observerEntity.Parts.Count > 0)
            {
                BuildShipFromParts(observerShipContainer, observerEntity, true);
            }
            else
            {
                // 回退长方体
                var go = CreateShipCuboid("ObserverFallback",
                    new Color(0.2f, 0.8f, 1.0f, 0.7f),
                    FallbackShipWidth, FallbackShipHeight, FallbackShipLength, true);
                go.transform.SetParent(observerShipContainer.transform);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
            }

            observerShipContainer.transform.position = observerPosUnity - forwardDirection * 1.0f;
            observerShipContainer.transform.rotation = Quaternion.LookRotation(forwardDirection);
        }

        /// <summary>
        /// 渲染其他飞船。有运行时 Parts 时从 Parts 渲染，否则用不透明长方体。
        /// （Lite 裁剪：Ship3D 设计分支已删除）
        /// </summary>
        private void SyncOtherShips()
        {
            if (spaceGame == null) return;
            var groups = spaceGame.GetAllGroups().ToList();

            // 清理已消失的飞船
            var toRemove = new List<int>();
            foreach (var kv in otherShipContainers)
            {
                bool exists = groups.Any(g => g.GroupId == kv.Key && g.IsActive && g.GroupId != observerGroup.GroupId);
                if (!exists) toRemove.Add(kv.Key);
            }
            foreach (int id in toRemove)
            {
                UnityEngine.Object.Destroy(otherShipContainers[id]);
                otherShipContainers.Remove(id);
            }

            foreach (var g in groups)
            {
                if (!g.IsActive || g.GroupId == observerGroup.GroupId) continue;

                Vector3 otherPos = GetGroupUnityPos(g);
                Vector3 otherForward = GetGroupForward(g);

                Vector3 relPos = otherPos - observerPosUnity;
                float dist = relPos.magnitude;

                if (!otherShipContainers.TryGetValue(g.GroupId, out var container) || container == null)
                {
                    container = new GameObject("OtherShip_" + g.GroupId);
                    container.layer = SceneLayer;
                    container.transform.SetParent(sceneRoot.transform);
                    otherShipContainers[g.GroupId] = container;

                    SpaceEntity entity = GetFirstEntity(g);

                    if (entity != null && entity.Parts.Count > 0)
                    {
                        BuildShipFromParts(container, entity, false);
                    }
                    else
                    {
                        var go = CreateShipCuboid("Fallback_" + g.GroupId,
                            GetGroupColor(g.GroupId),
                            OtherFallbackShipSize, OtherFallbackShipSize * 0.6f, OtherFallbackShipSize * 1.5f,
                            false);
                        go.transform.SetParent(container.transform);
                        go.transform.localPosition = Vector3.zero;
                        go.transform.localRotation = Quaternion.identity;
                    }
                }

                if (dist > FarClip * 0.5f)
                {
                    container.SetActive(false);
                    continue;
                }
                container.SetActive(true);
                container.transform.position = otherPos;
                container.transform.rotation = Quaternion.LookRotation(otherForward);

                // 远处飞船缩放
                float sizeFactor = Mathf.Max(1f, dist * 0.02f);
                container.transform.localScale = Vector3.one * sizeFactor;
            }
        }

        /// <summary>
        /// 从 SpaceEntity.Parts 运行时数据构建部件 GameObject。
        /// Part 尺寸为物理米，在此转换为视觉 Unity 单位。
        /// </summary>
        private void BuildShipFromParts(GameObject container, SpaceEntity entity, bool isObserver)
        {
            if (entity == null || entity.Parts == null) return;

            // 找到最长轴，以此确定缩放比（最长轴 = 回退尺寸长度，确保在 3D 视图中可见）
            double maxDim = 0.0;
            foreach (var part in entity.Parts)
            {
                if (part.isDestroyed) continue;
                var sz = part.localBoundsSize;
                maxDim = System.Math.Max(maxDim, System.Math.Max(sz.x, System.Math.Max(sz.y, sz.z)));
            }
            float visScale = (float)((maxDim > 0.0) ? FallbackShipLength / maxDim : 1.0);

            foreach (var part in entity.Parts)
            {
                if (part.isDestroyed) continue;

                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "Part_" + part.partId + "_" + part.defName;
                go.layer = SceneLayer;
                var collider = go.GetComponent<Collider>();
                if (collider != null) UnityEngine.Object.Destroy(collider);
                var mr = go.GetComponent<MeshRenderer>();
                if (isObserver)
                    mr.material = CreateTransparentMaterial(new Color(0.2f, 0.8f, 1.0f, 0.7f));
                else
                    mr.material = designerCubeMat;

                // 转换：物理米 → 视觉 Unity 单位
                Vector3 pos = (Vector3)(part.localPosition * visScale);
                Vector3 scale = (Vector3)(part.localBoundsSize * visScale);

                go.transform.localPosition = pos;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = scale;
                go.transform.SetParent(container.transform);
            }
        }

        private static SpaceEntity GetFirstEntity(SpaceEntityGroup group)
        {
            if (group == null) return null;
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return null;

            foreach (int eid in group.GetEntityIds())
            {
                var entity = spaceGame.GetEntityById(eid);
                if (entity != null) return entity;
            }
            return null;
        }

        private static GameObject CreateShipCuboid(string name, Color color, float w, float h, float l, bool transparent)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.layer = SceneLayer;
            var collider = go.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);
            var mr = go.GetComponent<MeshRenderer>();

            if (transparent)
            {
                mr.material = CreateTransparentMaterial(color);
            }
            else
            {
                mr.material = new Material(Shader.Find("Standard"))
                {
                    color = color
                };
            }
            go.transform.localScale = new Vector3(w, h, l);
            return go;
        }

        // ════════════════════════════════════════════════
        //  天体渲染（原有逻辑保持不变）
        // ════════════════════════════════════════════════

        /// <summary>
        /// 渲染玩家殖民地标记（天地映射检测用）：把殖民地 tile 经纬度按 GetSurfaceState
        /// 映射到 ECI 地表位置（与火箭出生同构的变换）。若火箭从标记处起飞 → 经度/自转相位原点正确；
        /// 若偏离 → 偏离量即经度/相位误差。亮黄色发光球，40km 视觉尺寸（相对行星半径可见）。
        /// </summary>
        private void SyncColonyMarker()
        {
            if (sceneRoot == null || celestialManager == null || observerGroup == null) return;

            // 玩家殖民地（settlement）tile
            Faction playerFaction = Faction.OfPlayer;
            if (playerFaction == null) return;
            int tile = -1;
            foreach (var s in Find.WorldObjects.Settlements)
            {
                if (s != null && s.Faction == playerFaction && s.Tile >= 0) { tile = s.Tile; break; }
            }
            if (tile < 0) return;

            // 观察者所在行星系根天体（殖民地所在的星球）
            int rootBodyId = celestialManager.GetRootPlanetId(observerGroup.CelestialBodyId);
            if (rootBodyId < 0) return;

            double radius = GetSurfaceState.BodyRadius(celestialManager, rootBodyId);
            double omega = GetSurfaceState.RotationOmega(celestialManager, rootBodyId);
            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();

            Vector2 longLat = Find.WorldGrid.LongLatOf(tile);
            double latRad = longLat.y * Mathf.Deg2Rad;
            double lonRad0 = longLat.x * Mathf.Deg2Rad;
            var surf = GetSurfaceState.SurfaceState(radius, latRad, lonRad0, omega, physicsTick);
            // 天体**局部系** → **系统系**（本窗口的船与天体都在系统系；2026-09-23 前这里少加了这份偏移）
            Vector3 markPos = (Vector3)((surf.pos + ViewFrame.BodySystemPosition(rootBodyId, physicsTick))
                * SpaceConstants.UnitsPerMeter);

            if (colonyMarkerObject == null)
            {
                colonyMarkerObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                colonyMarkerObject.name = "ColonyMarker";
                colonyMarkerObject.layer = SceneLayer;
                var coll = colonyMarkerObject.GetComponent<Collider>();
                if (coll != null) UnityEngine.Object.Destroy(coll);
                var mr = colonyMarkerObject.GetComponent<MeshRenderer>();
                mr.material = new Material(Shader.Find("Standard")) { color = new Color(1f, 1f, 0f) };
                mr.material.EnableKeyword("_EMISSION");
                mr.material.SetColor("_EmissionColor", new Color(1f, 1f, 0f) * 1.5f);
                colonyMarkerObject.transform.SetParent(sceneRoot.transform);
            }
            colonyMarkerObject.transform.position = markPos;
            float toUnity = (float)SpaceConstants.UnitsPerMeter;
            colonyMarkerObject.transform.localScale = Vector3.one * Mathf.Max((float)(40000.0 * toUnity), 0.5f);
            colonyMarkerObject.SetActive(true);

            // 与观察者（火箭）的角距/距离，量化经度/相位误差（首次打印一次，不刷屏）
            if (!colonyMarkerLogged)
            {
                colonyMarkerLogged = true;
                double observerDistM = (observerPosUnity - markPos).magnitude / toUnity;
                Log.Message(string.Format(
                    "[ColonyMarker] 玩家殖民地标记 ECI=({0:F0},{1:F0},{2:F0})km (φ={3:F1}° λ0={4:F1}°) 距观察者(火箭)={5:F0}km",
                    surf.pos.x / 1000.0, surf.pos.y / 1000.0, surf.pos.z / 1000.0,
                    latRad * Mathf.Rad2Deg, lonRad0 * Mathf.Rad2Deg, observerDistM / 1000.0));
            }
        }

        private void SyncSameSystemBodies()
        {
            if (bodyPositionsUnity == null) return;

            var toRemove = new List<int>();
            foreach (var kv in sameSystemBodyObjects)
            {
                if (!sameSystemBodyIds.Contains(kv.Key) || !bodyPositionsUnity.ContainsKey(kv.Key))
                    toRemove.Add(kv.Key);
            }
            foreach (int id in toRemove)
            {
                UnityEngine.Object.Destroy(sameSystemBodyObjects[id]);
                sameSystemBodyObjects.Remove(id);
            }

            foreach (int bodyId in sameSystemBodyIds)
            {
                if (!bodyPositionsUnity.TryGetValue(bodyId, out Vector3 worldPos)) continue;
                var body = celestialManager?.GetBodyById(bodyId);
                if (body == null) continue;

                float dist = Vector3.Distance(worldPos, observerPosUnity);
                if (dist > FarClip * 0.8f) continue;

                if (!sameSystemBodyObjects.TryGetValue(bodyId, out var go) || go == null)
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = "SameSystemBody_" + body.defName;
                    go.layer = SceneLayer;
                    go.transform.SetParent(sceneRoot.transform);
                    var collider = go.GetComponent<Collider>();
                    if (collider != null) UnityEngine.Object.Destroy(collider);

                    var mr = go.GetComponent<MeshRenderer>();
                    mr.material = new Material(body.bodyType == CelestialBodyType.Moon ? moonMat : planetMat);
                    if (body.bodyType != CelestialBodyType.Moon)
                        mr.material.color = CelestialBodyManager.GetBodyColor(body);

                    sameSystemBodyObjects[bodyId] = go;
                }

                go.transform.position = worldPos;
                // ⚠ 球网格直径 1 ⇒ 半径要乘 2（`ViewFrame.RadiusToSphereScale`；2026-09-23 与叠加层一起修）
                var bodyMesh = go.GetComponent<MeshFilter>()?.sharedMesh;
                float scale = Mathf.Max(ViewFrame.RadiusToSphereScale((float)body.radius, bodyMesh), MinBodySize * 2f);
                go.transform.localScale = Vector3.one * scale;
                go.SetActive(true);
            }
        }

        private void SyncOtherBodiesAsBillboards()
        {
            if (bodyPositionsUnity == null) return;

            var otherBodyIds = bodyPositionsUnity.Keys
                .Where(id => !sameSystemBodyIds.Contains(id))
                .ToHashSet();

            var toRemove = new List<int>();
            foreach (var kv in billboardObjects)
            {
                if (!otherBodyIds.Contains(kv.Key))
                    toRemove.Add(kv.Key);
            }
            foreach (int id in toRemove)
            {
                UnityEngine.Object.Destroy(billboardObjects[id]);
                billboardObjects.Remove(id);
            }

            foreach (int bodyId in otherBodyIds)
            {
                Vector3 worldPos = bodyPositionsUnity[bodyId];
                var body = celestialManager?.GetBodyById(bodyId);
                if (body == null) continue;
                if (body.bodyType == CelestialBodyType.Star) continue;

                float dist = Vector3.Distance(worldPos, observerPosUnity);
                if (dist > FarClip * 0.8f) continue;

                if (!billboardObjects.TryGetValue(bodyId, out var billboard) || billboard == null)
                {
                    Color color = body.bodyType == CelestialBodyType.Moon
                        ? new Color(0.7f, 0.7f, 0.7f)
                        : CelestialBodyManager.GetBodyColor(body);
                    billboard = CreateBillboard("Billboard_" + body.defName, color, BillboardMinSize);
                    billboard.transform.SetParent(sceneRoot.transform);
                    billboardObjects[bodyId] = billboard;
                }

                billboard.transform.position = worldPos;
                float toUnity = (float)SpaceConstants.UnitsPerMeter;
                float radius = Mathf.Max((float)(body.radius * toUnity), BillboardMinSize);
                float distScale = Mathf.Max(1f, dist * 0.001f);
                billboard.transform.localScale = Vector3.one * radius * distScale;
            }
        }

        private void SyncSunBillboard()
        {
            if (celestialManager == null) return;
            var star = celestialManager.GetStar();
            if (star == null)
            {
                if (sunBillboardObject != null) sunBillboardObject.SetActive(false);
                return;
            }

            float toUnity = (float)SpaceConstants.UnitsPerMeter;
            int tick = (int)Math.Round(TimeScaleManager.GetPhysicsTickSafe());
            DVector3 starWorldPos = celestialManager.GetBodyWorldPosition(star.bodyId, tick);
            Vector3 starPosUnity = (Vector3)(starWorldPos * toUnity);

            Vector3 toSun = starPosUnity - observerPosUnity;
            float sunDist = toSun.magnitude;
            float clampedDist = Mathf.Min(sunDist, MaxSunDist);
            Vector3 sunDir = toSun.normalized;
            Vector3 renderPos = observerPosUnity + sunDir * clampedDist;

            if (sunBillboardObject == null)
                CreateSunBillboard();
            if (sunBillboardObject == null) return; // 创建失败（缺资源）→ 跳过太阳标记，避免 NRE

            sunBillboardObject.transform.position = renderPos;
            sunBillboardObject.SetActive(true);

            float sunRadius = Mathf.Max((float)(star.radius * toUnity), MaxSunDist * 0.06f);
            float sizeScale = Mathf.Max(1f, clampedDist * 0.008f);
            sunBillboardObject.transform.localScale = Vector3.one * sunRadius * sizeScale;

            if (sunLight != null)
            {
                sunLight.transform.position = renderPos;
                sunLight.transform.LookAt(observerPosUnity);
            }
        }

        private void CreateSunBillboard()
        {
            sunBillboardObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
            sunBillboardObject.name = "SunBillboard";
            sunBillboardObject.layer = SceneLayer;
            var collider = sunBillboardObject.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);
            var mr = sunBillboardObject.GetComponent<MeshRenderer>();
            mr.material = new Material(Shader.Find("Standard"))
            {
                color = new Color(1f, 0.85f, 0.4f)
            };
            mr.material.EnableKeyword("_EMISSION");
            mr.material.SetColor("_EmissionColor", new Color(1f, 0.7f, 0.2f) * 2f);
            sunBillboardObject.transform.SetParent(sceneRoot.transform);

            sunBillboardGlowObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
            sunBillboardGlowObject.name = "SunBillboardGlow";
            sunBillboardGlowObject.layer = SceneLayer;
            var gc = sunBillboardGlowObject.GetComponent<Collider>();
            if (gc != null) UnityEngine.Object.Destroy(gc);
            var gm = sunBillboardGlowObject.GetComponent<MeshRenderer>();
            gm.material = new Material(Shader.Find("Standard"))
            {
                color = new Color(1f, 0.8f, 0.3f, 0.12f)
            };
            gm.material.SetFloat("_Mode", 3);
            gm.material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            gm.material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            gm.material.SetInt("_ZWrite", 0);
            gm.material.DisableKeyword("_ALPHATEST_ON");
            gm.material.EnableKeyword("_ALPHABLEND_ON");
            gm.material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            gm.material.renderQueue = 3000;

            sunBillboardGlowObject.transform.SetParent(sunBillboardObject.transform);
            sunBillboardGlowObject.transform.localScale = Vector3.one * 2.5f;
        }

        private static GameObject CreateBillboard(string name, Color color, float size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.layer = SceneLayer;
            var collider = go.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);
            var mr = go.GetComponent<MeshRenderer>();
            mr.material = new Material(Shader.Find("Standard"))
            {
                color = color
            };
            go.transform.localScale = Vector3.one * size;
            return go;
        }

        private void FaceBillboardsToCamera()
        {
            if (renderCamera == null) return;
            Transform camT = renderCamera.transform;
            foreach (var kv in billboardObjects)
            {
                if (kv.Value != null && kv.Value.activeInHierarchy)
                    kv.Value.transform.LookAt(camT);
            }
            if (sunBillboardObject != null && sunBillboardObject.activeInHierarchy)
                sunBillboardObject.transform.LookAt(camT);
        }

        // ════════════════════════════════════════════════
        //  窗口内容
        // ════════════════════════════════════════════════

        public override void DoWindowContents(Rect inRect)
        {
            int currentTick = Find.TickManager.TicksGame;
            if (currentTick != lastUpdateTick)
            {
                UpdateBodyPositions();
                SyncScene();
                lastUpdateTick = currentTick;
            }

            float rightWidth = Mathf.Max(200f, inRect.width * 0.3f);
            float leftWidth = inRect.width - rightWidth - 10f;
            float contentHeight = inRect.height - 40f;

            var viewRect = new Rect(inRect.x, inRect.y, leftWidth, contentHeight);
            var rightRect = new Rect(inRect.x + leftWidth + 10f, inRect.y, rightWidth, contentHeight);

            EnsureRenderTextureSize(viewRect.width, viewRect.height);

            if (viewRect.Contains(Event.current.mousePosition))
                HandleCameraInput();

            UpdateCameraTransform();
            FaceBillboardsToCamera();

            if (renderTexture != null)
                GUI.DrawTexture(viewRect, renderTexture);
            else
                Widgets.Label(viewRect, "Camera not ready");

            DrawRightPanel(rightRect);

            string controlsText = "LMB拖拽: 旋转视角 | 滚轮: 缩放";
            Text.Anchor = TextAnchor.LowerLeft;
            Widgets.Label(new Rect(inRect.x, inRect.y + contentHeight + 2f, leftWidth, 18f), controlsText);
            Text.Anchor = TextAnchor.UpperLeft;
        }

        // ════════════════════════════════════════════════
        //  相机控制
        // ════════════════════════════════════════════════

        private float cameraDistance = 5f;

        private void HandleCameraInput()
        {
            var e = Event.current;
            if (e.type == EventType.MouseDrag)
            {
                if (e.button == 0)
                {
                    lookYaw += e.delta.x * 0.3f;
                    lookPitch -= e.delta.y * 0.3f;
                    lookPitch = Mathf.Clamp(lookPitch, -80f, 80f);
                    e.Use();
                }
            }
            else if (e.type == EventType.ScrollWheel)
            {
                cameraDistance -= e.delta.y * 0.5f;
                cameraDistance = Mathf.Clamp(cameraDistance, 0.5f, 100f);
                e.Use();
            }
        }

        private void UpdateCameraTransform()
        {
            if (renderCamera == null || observerGroup == null) return;

            Quaternion baseRot = Quaternion.LookRotation(forwardDirection);
            Quaternion lookRot = baseRot * Quaternion.Euler(lookPitch, lookYaw, 0f);

            Vector3 camPos = observerPosUnity + forwardDirection * cameraDistance;

            renderCamera.transform.position = camPos;
            renderCamera.transform.rotation = lookRot;
        }

        // ════════════════════════════════════════════════
        //  右侧面板
        // ════════════════════════════════════════════════

        private Vector2 infoScrollPos;

        private void DrawRightPanel(Rect rect)
        {
            if (observerGroup == null) { Widgets.Label(rect, "Group not available."); return; }

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 24f),
                string.Format("Group {0} 视角", observerGroup.GroupId));
            Text.Font = GameFont.Small;

            float y = rect.y + 30f;
            float lineHeight = 20f;

            // 飞船部件信息（Lite 裁剪：无 Ship3D 设计数据，仅显示部件数）
            SpaceEntity observerEntity = GetFirstEntity(observerGroup);
            Widgets.Label(new Rect(rect.x, y, rect.width, lineHeight),
                string.Format("部件数: {0}", observerEntity?.Parts.Count ?? 0));
            y += lineHeight;

            y += 4f;
            Widgets.Label(new Rect(rect.x, y, rect.width, lineHeight),
                string.Format("位置: ({0:F0}, {1:F0}, {2:F0})",
                    observerPosUnity.x, observerPosUnity.y, observerPosUnity.z));
            y += lineHeight;
            Widgets.Label(new Rect(rect.x, y, rect.width, lineHeight),
                string.Format("朝向: ({0:F2}, {1:F2}, {2:F2})",
                    forwardDirection.x, forwardDirection.y, forwardDirection.z));
            y += lineHeight + 4f;

            Widgets.Label(new Rect(rect.x, y, rect.width, 22f), "--- 本行星系天体 (3D球体) ---");
            y += 24f;

            var bodies = celestialManager?.GetAllBodies();
            if (bodies != null && bodies.Count > 0)
            {
                float listHeight = rect.height - (y - rect.y) - 10f;
                if (listHeight > 30f)
                {
                    var sameSystemBodies = bodies.Where(b => sameSystemBodyIds.Contains(b.bodyId)).ToList();
                    var otherBodies = bodies.Where(b => !sameSystemBodyIds.Contains(b.bodyId) && b.bodyType != CelestialBodyType.Star).ToList();

                    float totalHeight = (sameSystemBodies.Count + otherBodies.Count + 2) * lineHeight;
                    var scrollRect = new Rect(rect.x, y, rect.width, listHeight);
                    var viewPortRect = new Rect(0f, 0f, rect.width - 16f, totalHeight);
                    Widgets.BeginScrollView(scrollRect, ref infoScrollPos, viewPortRect);
                    float ly = 0f;

                    foreach (var body in sameSystemBodies)
                    {
                        Widgets.Label(new Rect(4f, ly, viewPortRect.width - 8f, lineHeight),
                            string.Format("● {0} ({1})", body.label, body.bodyType));
                        ly += lineHeight;
                    }

                    ly += lineHeight * 0.5f;
                    Widgets.Label(new Rect(4f, ly, viewPortRect.width - 8f, lineHeight), "--- 非本行星系 (圆片) ---");
                    ly += lineHeight;

                    foreach (var body in otherBodies)
                    {
                        Widgets.Label(new Rect(4f, ly, viewPortRect.width - 8f, lineHeight),
                            string.Format("○ {0} ({1})", body.label, body.bodyType));
                        ly += lineHeight;
                    }

                    Widgets.EndScrollView();
                }
            }
        }

        private static Color GetGroupColor(int groupId)
        {
            float hue = (groupId * 0.618f) % 1f;
            return Color.HSVToRGB(hue, 0.8f, 0.9f);
        }

        // ════════════════════════════════════════════════
        //  清理
        // ════════════════════════════════════════════════

        public override void PostClose()
        {
            base.PostClose();

            if (renderTexture != null) { renderTexture.Release(); renderTexture = null; }
            if (cameraObject != null) { UnityEngine.Object.Destroy(cameraObject); cameraObject = null; }
            if (sceneRoot != null) { UnityEngine.Object.Destroy(sceneRoot); sceneRoot = null; }
            observerShipContainer = null;
            otherShipContainers.Clear();
            sameSystemBodyObjects.Clear();
            billboardObjects.Clear();
            sunBillboardObject = null;
            sunBillboardGlowObject = null;
            if (colonyMarkerObject != null) { UnityEngine.Object.Destroy(colonyMarkerObject); colonyMarkerObject = null; }
        }
    }
}
