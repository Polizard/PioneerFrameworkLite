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

        /// <summary>现有 RimWorld 世界对应的天体（地球）ID</summary>
        private const int EARTH_BODY_ID = 1;

        private static int selectedGroupId = -1;
        private static bool isFollowing = false;
        private static float followLerpSpeed = 5f;
        private static float lastFollowTime;

        // 视觉自转不再累积存储，改用 GetVisualRotationAngle() 即时计算
        private const float VisualDegPerDay = 360f / 60000f; // 0.006° 每 tick

        // 材质与资源
        private static Material iconMaterial;
        private static Material orbitMarkerMaterial;
        private static readonly MaterialPropertyBlock orbitMarkerPropertyBlock = new();

        // 常量（单位：Unity 世界单位，已从米制转换）
        private const float BaseIconSize = 20f;
        private const float SurfaceIconSize = 28f; // 地表图标稍大，更容易看到
        private const float IconScaleReferenceAltitude = 850f;
        private const float IconScaleMaxMultiplier = 3f;
        private const float OrbitMarkerSpacing = 14f;        // 轨道标记点间距（Unity单位）
        private const int MinOrbitSamples = 36;
        private const int MaxOrbitSamples = 180;
        private const float OrbitMarkerSize = 1.2f;
        private const float OrbitMarkerAlphaFar = 0.3f;

        // 轨道线色彩常量
        private static readonly Color OrbitColorCurrentOrbit = new Color(0.25f, 0.9f, 1f, 0.6f);       // 当前轨道：青色
        private static readonly Color OrbitColorPostManeuver = new Color(1f, 0.7f, 0.1f, 0.6f);       // 机动后轨道：橙黄
        private static readonly Color OrbitColorTransfer = new Color(0f, 1f, 0.5f, 0.6f);             // 转移轨道：亮绿
        private static readonly Color OrbitColorTarget = new Color(1f, 0.3f, 0.3f, 0.7f);             // 目标轨道：红色
        private static readonly Color MarkerColorPeriapsis = new Color(0f, 1f, 0f, 0.9f);             // Pe：绿色
        private static readonly Color MarkerColorApoapsis = new Color(1f, 0.2f, 0.2f, 0.9f);          // Ap：红色
        private static readonly Color MarkerColorAn = new Color(1f, 1f, 0f, 0.8f);                    // AN：黄色
        private static readonly Color MarkerColorDn = new Color(0.7f, 0.3f, 1f, 0.8f);                // DN：紫色

        // GUI 面板
        private const float PanelWidth = 260f;
        private const float PanelHeight = 354f;
        private const float PanelOffsetY = -44f;
        private static GUIStyle panelLabelStyle;

        /// <summary>预创建的球体网格，用于天体球体渲染。</summary>
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

            // 应用 Harmony 补丁
            var harmony = new Harmony("PioneerFramework.SpaceGroupVisualManager");
            // 补丁1：渲染世界地图时绘制图标和轨道
            harmony.Patch(AccessTools.Method(typeof(WorldRenderer), "DrawWorldLayers"),
                postfix: new HarmonyMethod(typeof(SpaceGroupVisualManager), nameof(WorldRenderer_DrawWorldLayers_Postfix)));
            // 补丁2：世界地图 GUI 事件（信息面板、点击选中）
            harmony.Patch(AccessTools.Method(typeof(WorldInterface), "WorldInterfaceOnGUI"),
                postfix: new HarmonyMethod(typeof(SpaceGroupVisualManager), nameof(WorldInterface_OnGUI_Postfix)));
            // 补丁3：相机跟随
            harmony.Patch(AccessTools.Method(typeof(WorldCameraDriver), "Update"),
                postfix: new HarmonyMethod(typeof(SpaceGroupVisualManager), nameof(WorldCameraDriver_Update_Postfix)));
            // 补丁3b：修改世界摄像机最大高度（1100→50000），让玩家能拉远看高轨道
            harmony.Patch(AccessTools.Method(typeof(WorldCameraDriver), "WorldCameraDriverOnGUI"),
                transpiler: new HarmonyMethod(typeof(SpaceGroupVisualManager), nameof(Transpiler_MaxAltitude_OnGUI)));
            harmony.Patch(AccessTools.Method(typeof(WorldCameraDriver), "Update"),
                transpiler: new HarmonyMethod(typeof(SpaceGroupVisualManager), nameof(Transpiler_MaxAltitude_Update)));
            // 补丁3c：增大 farClipPlane
            harmony.Patch(AccessTools.Method(typeof(WorldCameraDriver), "Awake"),
                postfix: new HarmonyMethod(typeof(SpaceGroupVisualManager), nameof(WorldCameraDriver_Awake_Postfix)));
            // 补丁4：在世界地图右下角添加开关按钮（更可靠）
            harmony.Patch(AccessTools.Method(typeof(WorldInterface), "WorldInterfaceOnGUI"),
                prefix: new HarmonyMethod(typeof(SpaceGroupVisualManager), nameof(WorldInterface_OnGUI_Prefix_AddGizmo)));
        }

        #region Harmony 补丁方法

        public static void WorldRenderer_DrawWorldLayers_Postfix()
        {
            if (!WorldRendererUtility.WorldSelected) return;
            if (!EnableVisualization) return;

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;

            var groups = spaceGame.GetAllGroups().ToList();
            if (groups.Count == 0) return;

            var camera = Find.WorldCamera;
            if (camera == null) return;

            // 收集地球系统（地球 + 所有卫星）的天体 ID 集合，渲染该系统中所有群组
            var earthSystemBodyIds = new HashSet<int> { EARTH_BODY_ID };
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (celestialMgr != null)
            {
                var earthDef = celestialMgr.GetBodyById(EARTH_BODY_ID);
                if (earthDef != null)
                {
                    foreach (var moon in earthDef.moonBodies)
                        earthSystemBodyIds.Add(moon.bodyId);
                }
            }

            foreach (var group in groups)
                if (group.IsActive && earthSystemBodyIds.Contains(group.CelestialBodyId))
                    DrawGroupIcon(group, camera);

            // ── 天然卫星渲染（地球的卫星，根据 XML 定义动态渲染）──
            if (celestialMgr != null)
            {
                var earthDef = celestialMgr.GetBodyById(EARTH_BODY_ID);
                if (earthDef != null && earthDef.moonBodies.Count > 0)
                {
                    int currentTick = (int)TimeScaleManager.GetPhysicsTickSafe();
                    foreach (var moon in earthDef.moonBodies)
                    {
                        DVector3 moonPosMeters = celestialMgr.GetBodyLocalPosition(moon.bodyId, currentTick);
                        Vector3 moonPosUnity = (Vector3)(moonPosMeters * SpaceConstants.UnitsPerMeter);
                        Vector3 worldPos = GetOrbitOrigin() + ApplyVisualRotation(moonPosUnity);

                        if (!IsWorldPosVisible(worldPos, 1f)) continue;

                        // 卫星大小：物理半径映射到Unity单位，保证最小可见大小
                        float moonRadiusUnity = (float)(moon.radius * SpaceConstants.UnitsPerMeter);
                        float minMoonSize = 2f;
                        float size = Mathf.Max(moonRadiusUnity, minMoonSize);

                        // 渲染为白色3D球体
                        Matrix4x4 sphereMatrix = Matrix4x4.TRS(worldPos, Quaternion.identity, Vector3.one * size);
                        Graphics.DrawMesh(sphereMesh, sphereMatrix, orbitMarkerMaterial, WorldCameraManager.WorldLayer);
                    }
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
                        DVector3 pos = selected.CurrentOrbit.position;
                        DVector3 vel = selected.CurrentOrbit.velocity;
                        var dPoints = OrbitMath.GetOrbitPoints(pos, vel, 60);
                        if (dPoints != null && dPoints.Count > 0)
                        {
                            var pts = dPoints.ConvertAll(p => (Vector3)(p * SpaceConstants.UnitsPerMeter));
                            DrawOrbitPath(pts, new Color(0.35f, 0.95f, 1f, 0.8f), OrbitMarkerSize, camera);
                        }
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
                    if (!ManeuverPlanner.HasPlanner && !RendezvousPlanner.PreviewValid
                        && selected.plannedManeuvers.Count > 0)
                    {
                        float toUnity = (float)SpaceConstants.UnitsPerMeter;
                        for (int i = 0; i < selected.plannedManeuvers.Count; i++)
                        {
                            var pm = selected.plannedManeuvers[i];
                            if (!pm.active) continue;

                            float alphaFactor = 1f - i * 0.25f;
                            if (alphaFactor < 0.25f) alphaFactor = 0.25f;

                            if (pm.previewCurrentOrbit != null && pm.previewCurrentOrbit.Count > 0)
                            {
                                var currentPts = pm.previewCurrentOrbit.ConvertAll(p => p * toUnity);
                                DrawOrbitPath(currentPts,
                                    new Color(0.25f, 0.9f, 1f, 0.4f * alphaFactor), 0.6f, camera);
                            }

                            if (pm.previewPostOrbit != null && pm.previewPostOrbit.Count > 0)
                            {
                                var postPts = pm.previewPostOrbit.ConvertAll(p => p * toUnity);
                                DrawOrbitPath(postPts,
                                    new Color(1f, 0.7f, 0.1f, 0.4f * alphaFactor), 0.6f, camera);
                            }

                            var nodePosUnity = pm.previewNodePosition * toUnity;
                            Color nodeColor = i == 0
                                ? new Color(1f, 0.9f, 0.2f, 0.9f)
                                : new Color(1f, 0.5f, 0.1f, 0.8f - i * 0.15f);
                            DrawManeuverNode(nodePosUnity, camera, nodeColor, 2.2f);
                        }
                    }

                    // ── 交会规划预览 ──
                    if (RendezvousPlanner.PreviewValid)
                    {
                        var transferPts = RendezvousPlanner.TransferOrbitUnity;
                        var depPos = RendezvousPlanner.DeparturePosUnity;
                        var arrPos = RendezvousPlanner.ArrivalPosUnity;
                        var targetPos = RendezvousPlanner.TargetPosAtArrivalUnity;

                        if (transferPts != null && transferPts.Count > 0)
                            DrawOrbitPath(transferPts, OrbitColorTransfer, 0.8f, camera);

                        if (depPos.HasValue)
                            DrawManeuverNode(depPos.Value, camera);

                        if (arrPos.HasValue)
                            DrawManeuverNode(arrPos.Value, camera);

                        if (targetPos.HasValue)
                        {
                            // 目标到达点用不同颜色
                            Vector3 origin = GetOrbitOrigin();
                            Vector3 worldPos = origin + ApplyVisualRotation(targetPos.Value);
                            if (IsWorldPosVisible(worldPos, 1f))
                            {
                                Vector3 toCam = Find.WorldCamera.transform.position - worldPos;
                                if (toCam.sqrMagnitude > 0.01f)
                                {
                                    float pulse = 1f + 0.2f * Mathf.Sin(Time.realtimeSinceStartup * 3f);
                                    float nodeSize = 2.2f * pulse;
                                    Quaternion rot = Quaternion.LookRotation(Find.WorldCamera.transform.up, toCam.normalized);
                                    Vector3 drawPos = worldPos + toCam.normalized * 0.5f;
                                    Matrix4x4 matrix = Matrix4x4.TRS(drawPos, rot, new Vector3(nodeSize, 1f, nodeSize));
                                    orbitMarkerPropertyBlock.SetColor("_Color", new Color(1f, 0.3f, 0.3f, 0.7f));
                                    Graphics.DrawMesh(MeshPool.plane10, matrix, orbitMarkerMaterial, WorldCameraManager.WorldLayer, null, 0, orbitMarkerPropertyBlock);
                                }
                            }
                        }
                    }
                }
            }
        }

        public static void WorldInterface_OnGUI_Postfix()
        {
            if (!EnableVisualization) return;
            if (!WorldRendererUtility.WorldSelected) return;
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;

            HandleInput(spaceGame);
            DrawInfoPanel(spaceGame);
        }

        public static void WorldCameraDriver_Update_Postfix(WorldCameraDriver __instance)
        {
            // 始终确保 farClipPlane 足够大（不依赖 EnableVisualization）
            if (Find.WorldCamera != null && Find.WorldCamera.farClipPlane < 200000f)
                Find.WorldCamera.farClipPlane = 200000f;

            if (!EnableVisualization) return;
            if (!isFollowing) return;
            if (!WorldRendererUtility.WorldSelected) return;
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;

            var group = spaceGame.GetGroupById(selectedGroupId);
            if (group == null || !group.IsActive)
            {
                isFollowing = false;
                return;
            }

            Vector3 worldPos = GetGroupWorldPositionUnity(group);
            Vector3 origin = GetOrbitOrigin();
            Vector3 direction = (worldPos - origin).normalized;
            if (direction == Vector3.zero) return;

            Quaternion targetRot = Quaternion.Inverse(Quaternion.LookRotation(-direction));
            float t = Mathf.Clamp01(Time.deltaTime * followLerpSpeed);
            __instance.sphereRotation = Quaternion.Slerp(__instance.sphereRotation, targetRot, t);
        }

        // 在世界地图右下角添加一个按钮
        public static void WorldInterface_OnGUI_Prefix_AddGizmo()
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

        // ──────────── 摄像机拉远距离补丁 ────────────

        private const float NEW_MAX_ALTITUDE = 50000f;

        /// <summary>
        /// Transpiler: 把 WorldCameraDriverOnGUI 中的 1100f（MaxAltitude）替换为 NEW_MAX_ALTITUDE。
        /// 影响高度限制 Mathf.Clamp(num2, MinAltitude, 1100f) 和行星层切换判断。
        /// 带上下文保护：仅替换后接 Mathf.Clamp 调用的 1100f，避免误改同名字面量。
        /// </summary>
        public static IEnumerable<CodeInstruction> Transpiler_MaxAltitude_OnGUI(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_R4 && (float)list[i].operand == 1100f)
                {
                    // 安全检查：1100f 是 Mathf.Clamp 的第三个参数（最大值）
                    // 后一条指令应为 call Mathf.Clamp
                    bool isClampArg = i + 1 < list.Count
                        && list[i + 1].opcode == OpCodes.Call
                        && list[i + 1].operand is System.Reflection.MethodInfo mi
                        && mi.Name == "Clamp"
                        && mi.DeclaringType == typeof(Mathf);
                    if (isClampArg)
                        list[i].operand = NEW_MAX_ALTITUDE;
                }
            }
            return list;
        }

        /// <summary>
        /// Transpiler: 把 WorldCameraDriver.Update 中的 1100f（MaxAltitude）替换为 NEW_MAX_ALTITUDE。
        /// 影响旋转速度归一化因子 (altitude - MinAltitude) / (1100 - MinAltitude)。
        /// 带上下文保护：仅替换后接 MinAltitude(5f) 加载的 1100f。
        /// </summary>
        public static IEnumerable<CodeInstruction> Transpiler_MaxAltitude_Update(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_R4 && (float)list[i].operand == 1100f)
                {
                    // 安全检查：1100f 应当后接 MinAltitude（5f），用于 (1100 - MinAltitude)
                    bool isInFormula = i + 1 < list.Count
                        && list[i + 1].opcode == OpCodes.Ldc_R4
                        && (float)list[i + 1].operand == 5f;
                    if (isInFormula)
                        list[i].operand = NEW_MAX_ALTITUDE;
                }
            }
            return list;
        }

        /// <summary>
        /// Awake 后立即增大世界摄像机的 farClipPlane，防止远处被裁掉。
        /// </summary>
        public static void WorldCameraDriver_Awake_Postfix()
        {
            if (Find.WorldCamera != null && Find.WorldCamera.farClipPlane < 200000f)
                Find.WorldCamera.farClipPlane = 200000f;
        }

        #endregion

        #region 核心辅助函数（单位转换关键）

        private static Vector3 GetOrbitOrigin()
        {
            // 在 PioneerFramework 中，星球中心为原点（Unity 单位）
            return Vector3.zero;
        }

        /// <summary>
        /// 对轨道相对位置叠加视觉自转（绕极轴旋转，仅影响渲染）。
        /// 输入/输出均为相对于轨道原点的 Unity 单位矢量。
        /// 视觉自转 = 360°/游戏日，用当前 tick 即时计算，无累计漂移。
        /// </summary>
        private static Vector3 ApplyVisualRotation(Vector3 posUnity)
        {
            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            float visAngle = (float)(physicsTick % 60000) * VisualDegPerDay;
            return Quaternion.AngleAxis(visAngle, Vector3.up) * posUnity;
        }

        /// <summary>
        /// 获取 Group 在世界地图中的 Unity 单位位置（已从米制转换）
        /// 视觉自转对所有 Group 统一叠加，圆轨/积分切换时无跳变。
        /// </summary>
        private static Vector3 GetGroupWorldPositionUnity(SpaceEntityGroup group)
        {
            Vector3 posMeters;
            if (group.IsCircularOrbit)
            {
                double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
                posMeters = group.GetCircularPositionAtTick(physicsTick);
            }
            else
                posMeters = group.CurrentOrbit.position;

            Vector3 posUnity = (Vector3)((DVector3)posMeters * SpaceConstants.UnitsPerMeter);
            return GetOrbitOrigin() + ApplyVisualRotation(posUnity);
        }

        private static bool IsWorldPosVisible(Vector3 worldPosUnity, float occlusionMargin)
        {
            Vector3 origin = GetOrbitOrigin();
            Vector3 offset = worldPosUnity - origin;
            float radius = offset.magnitude;
            if (radius < 0.01f) return false;

            Vector3 camDir = (Find.WorldCameraDriver.CameraPosition - origin).normalized;
            Vector3 satDir = offset / radius;
            float facing = Vector3.Dot(camDir, satDir);
            if (facing >= 0) return true;

            float perpDist = radius * Mathf.Sqrt(1 - facing * facing);
            float planetRadius = Find.WorldGrid.Surface.Radius;
            return perpDist > planetRadius + occlusionMargin;
        }

        private static void DrawGroupIcon(SpaceEntityGroup group, Camera cam)
        {
            Vector3 worldPos = GetGroupWorldPositionUnity(group);
            if (!IsWorldPosVisible(worldPos, 2f)) return;

            Vector3 toCam = cam.transform.position - worldPos;
            if (toCam.sqrMagnitude < 0.01f) return;

            Quaternion rot = Quaternion.LookRotation(cam.transform.up, toCam.normalized);
            float altitude = Find.WorldCameraDriver?.altitude ?? 1000f;
            float scale = Mathf.Clamp(Mathf.Sqrt(altitude / IconScaleReferenceAltitude), 1f, IconScaleMaxMultiplier);
            float size = SurfaceIconSize * scale;
            if (selectedGroupId == group.GroupId) size *= 1.12f;

            Vector3 drawPos = worldPos + toCam.normalized * 1.5f; // 避免深度冲突
            Matrix4x4 matrix = Matrix4x4.TRS(drawPos, rot, new Vector3(size, 1f, size));
            Graphics.DrawMesh(MeshPool.plane10, matrix, iconMaterial, WorldCameraManager.WorldLayer);
        }

        /// <summary>
        /// 生成圆轨采样点（相对轨道原点的 Unity 坐标），供 DrawOrbitPath 使用。
        /// 包含鼠标高度衰减、轨道平面支持。不含视觉旋转（由 DrawOrbitPath 统一叠加）。
        /// </summary>
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
            Vector3 radialRef = group.circularRadialRef;
            Vector3 yDir = usePlane
                ? Vector3.Cross(group.circularOrbitNormal, radialRef).normalized
                : Vector3.forward;

            var points = new List<Vector3>(sampleCount);

            // 如果中心天体不是行星，需要偏移到中心天体位置
            Vector3 centralBodyOffsetUnity = Vector3.zero;
            if (group.circularCentralBodyId >= 0)
            {
                var celestialMgrVis = Current.Game?.GetComponent<CelestialBodyManager>();
                if (celestialMgrVis != null)
                {
                    var centralDefVis = celestialMgrVis.GetBodyById(group.circularCentralBodyId);
                    if (centralDefVis != null && centralDefVis.bodyType != CelestialBodyType.Planet)
                    {
                        DVector3 offsetMeters = celestialMgrVis.GetBodyLocalPosition(
                            group.circularCentralBodyId, (int)TimeScaleManager.GetPhysicsTickSafe());
                        centralBodyOffsetUnity = (Vector3)(offsetMeters * SpaceConstants.UnitsPerMeter);
                    }
                }
            }

            for (int i = 0; i < sampleCount; i++)
            {
                float angle = 2f * Mathf.PI * i / sampleCount;
                float cosA = Mathf.Cos(angle);
                float sinA = Mathf.Sin(angle);

                if (usePlane)
                {
                    Vector3 localMeters = (Vector3)((DVector3)(radialRef * cosA + yDir * sinA) * radiusMetersD);
                    points.Add(((Vector3)((DVector3)localMeters * SpaceConstants.UnitsPerMeter)) + centralBodyOffsetUnity);
                }
                else
                {
                    points.Add(new Vector3(cosA * radiusUnity, 0f, sinA * radiusUnity) + centralBodyOffsetUnity);
                }
            }
            return points;
        }

        /// <summary>
        /// 绘制轨道路径 (点列)。自动叠加视觉自转。
        /// </summary>
        private static void DrawOrbitPath(List<Vector3> pointsUnity, Color color, float size, Camera cam)
        {
            Vector3 origin = GetOrbitOrigin();
            for (int i = 0; i < pointsUnity.Count; i++)
            {
                Vector3 worldPos = origin + ApplyVisualRotation(pointsUnity[i]);
                if (!IsWorldPosVisible(worldPos, 1f)) continue;

                Vector3 toCam = cam.transform.position - worldPos;
                if (toCam.sqrMagnitude < 0.01f) continue;

                Quaternion rot = Quaternion.LookRotation(cam.transform.up, toCam.normalized);
                Vector3 drawPos = worldPos + toCam.normalized * 0.5f;
                Matrix4x4 matrix = Matrix4x4.TRS(drawPos, rot, new Vector3(size, 1f, size));
                orbitMarkerPropertyBlock.SetColor("_Color", color);
                Graphics.DrawMesh(MeshPool.plane10, matrix, orbitMarkerMaterial, WorldCameraManager.WorldLayer, null, 0, orbitMarkerPropertyBlock);
            }
        }

        /// <summary>
        /// 绘制机动节点 (较大、醒目)。自动叠加视觉自转。
        /// </summary>
        private static void DrawManeuverNode(Vector3 nodePosUnity, Camera cam, Color? color = null, float baseSize = 2.5f)
        {
            Vector3 origin = GetOrbitOrigin();
            Vector3 worldPos = origin + ApplyVisualRotation(nodePosUnity);
            if (!IsWorldPosVisible(worldPos, 1f)) return;

            Vector3 toCam = cam.transform.position - worldPos;
            if (toCam.sqrMagnitude < 0.01f) return;

            // 节点：脉冲效果
            float pulse = 1f + 0.15f * Mathf.Sin(Time.realtimeSinceStartup * 4f);
            float nodeSize = baseSize * pulse;

            Quaternion rot = Quaternion.LookRotation(cam.transform.up, toCam.normalized);
            Vector3 drawPos = worldPos + toCam.normalized * 0.5f;
            Matrix4x4 matrix = Matrix4x4.TRS(drawPos, rot, new Vector3(nodeSize, 1f, nodeSize));
            Color c = color ?? new Color(1f, 0.9f, 0.2f, 0.9f);
            orbitMarkerPropertyBlock.SetColor("_Color", c);
            Graphics.DrawMesh(MeshPool.plane10, matrix, orbitMarkerMaterial, WorldCameraManager.WorldLayer, null, 0, orbitMarkerPropertyBlock);
        }

        /// <summary>
        /// 为指定 Group 绘制 Pe/Ap/AN/DN 标记。
        /// </summary>
        private static void DrawOrbitMarkersForGroup(SpaceEntityGroup group, Camera cam)
        {
            float toUnity = (float)SpaceConstants.UnitsPerMeter;
            Vector3 origin = GetOrbitOrigin();

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
                posMetersD = group.CurrentOrbit.position;
                velMetersD = group.CurrentOrbit.velocity;
            }

            OrbitMath.GetOrbitFrame(posMetersD, velMetersD, out double a, out double e, out double _, out DVector3 eDir, out DVector3 _);

            // 绘制 Pe/Ap 标记（仅椭圆轨且 e > 0.005 时）
            if (a > 0.0 && e > 0.005)
            {
                double peDist = OrbitMath.GetPeriapsisDistance(a, e);
                double apDist = OrbitMath.GetApoapsisDistance(a, e);
                Vector3 pePos = (Vector3)(eDir * peDist * toUnity);
                Vector3 apPos = (Vector3)(-eDir * apDist * toUnity);

                DrawOrbitMarker(origin + ApplyVisualRotation(pePos), MarkerColorPeriapsis, 1.8f, cam, "Pe");
                DrawOrbitMarker(origin + ApplyVisualRotation(apPos), MarkerColorApoapsis, 1.8f, cam, "Ap");
            }

            // 绘制 AN/DN 标记（非共面时）
            var (anPos, dnPos) = OrbitMath.GetNodePositions(posMetersD, velMetersD, DVector3.up);
            if (anPos.sqrMagnitude > 0.001)
            {
                DrawOrbitMarker(origin + ApplyVisualRotation((Vector3)(anPos * toUnity)), MarkerColorAn, 1.6f, cam, "AN");
                DrawOrbitMarker(origin + ApplyVisualRotation((Vector3)(dnPos * toUnity)), MarkerColorDn, 1.6f, cam, "DN");
            }
        }

        /// <summary>
        /// 绘制单个轨道标记点（Pe/Ap/AN/DN 通用）。
        /// </summary>
        private static void DrawOrbitMarker(Vector3 worldPos, Color color, float size, Camera cam, string label = null)
        {
            if (!IsWorldPosVisible(worldPos, 1f)) return;

            Vector3 toCam = cam.transform.position - worldPos;
            if (toCam.sqrMagnitude < 0.01f) return;

            // 标记：慢速脉冲
            float pulse = 1f + 0.1f * Mathf.Sin(Time.realtimeSinceStartup * 2f);
            float markerSize = size * pulse;

            Quaternion rot = Quaternion.LookRotation(cam.transform.up, toCam.normalized);
            Vector3 drawPos = worldPos + toCam.normalized * 0.6f;
            Matrix4x4 matrix = Matrix4x4.TRS(drawPos, rot, new Vector3(markerSize, 1f, markerSize));
            orbitMarkerPropertyBlock.SetColor("_Color", color);
            Graphics.DrawMesh(MeshPool.plane10, matrix, orbitMarkerMaterial, WorldCameraManager.WorldLayer, null, 0, orbitMarkerPropertyBlock);
        }

        #endregion

        #region GUI 交互

        private static void HandleInput(SpaceGameComp spaceGame)
        {
            if (Event.current.type != EventType.MouseDown || Event.current.button != 0) return;
            if (!WorldRendererUtility.WorldSelected) return;
            if (MouseIsOverPanel()) return;

            Vector2 mousePos = Event.current.mousePosition;
            SpaceEntityGroup clickedGroup = null;
            float minDist = 20f;
            foreach (var group in spaceGame.GetAllGroups())
            {
                if (!group.IsActive) continue;
                Vector3 worldPos = GetGroupWorldPositionUnity(group);
                if (!IsWorldPosVisible(worldPos, 2f)) continue;
                Vector2 screenPos = GenWorldUI.WorldToUIPosition(worldPos);
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
            Vector3 worldPos = GetGroupWorldPositionUnity(group);
            if (IsWorldPosVisible(worldPos, 2f))
            {
                anchor = GenWorldUI.WorldToUIPosition(worldPos);
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
            // 固定边栏：贴屏幕左侧，完全静止（不跟随 Group 位置），与顶部游戏 UI 保持间距
            return new Rect(24f, 80f, PanelWidth, PanelHeight);
        }

        private static void DrawInfoPanel(SpaceGameComp spaceGame)
        {
            if (selectedGroupId == -1) return;

            var group = spaceGame.GetGroupById(selectedGroupId);
            if (group == null) return;
            if (!GetPanelAnchor(group, out Vector2 anchor, out bool occluded)) return;
            Rect rect = CalculatePanelRect(anchor);
            DrawPanelBackground(rect, occluded);
            DrawPanelText(group, rect, occluded);
            DrawPanelButton(rect, group, spaceGame);
        }

        private static void DrawPanelBackground(Rect rect, bool occluded)
        {
            Color bg = occluded ? new Color(0.02f, 0.04f, 0.06f, 0.62f) : new Color(0.02f, 0.06f, 0.09f, 0.74f);
            Color border = occluded ? new Color(0.25f, 0.9f, 1f, 0.45f) : new Color(0.25f, 0.9f, 1f, 0.8f);
            Color accent = occluded ? new Color(0.25f, 0.9f, 1f, 0.35f) : new Color(0.25f, 0.9f, 1f, 0.85f);
            GUI.color = bg;
            Widgets.DrawBoxSolid(rect, GUI.color);
            GUI.color = border;
            Widgets.DrawBox(rect, 1);
            Widgets.DrawBoxSolid(new Rect(rect.x + 3f, rect.y + 4f, 3f, rect.height - 8f), accent);
            GUI.color = Color.white;
        }

        private static void DrawPanelText(SpaceEntityGroup group, Rect rect, bool occluded)
        {
            if (panelLabelStyle == null)
            {
                panelLabelStyle = new GUIStyle(Text.CurFontStyle);
                panelLabelStyle.fontSize = 12;
                panelLabelStyle.wordWrap = true;
            }

            string title = string.Format("Group {0}", group.GroupId);
            string typeInfo = group.HasGravityWell ? "引力源" : (group.IsMovable ? "可移动" : "静态");
            string orbitInfo = group.IsCircularOrbit ? "圆轨" : "积分";
            string status = group.IsActive ? "运行中" : "离线";

            // 中心天体
            string centerInfo = "天体: ";
            if (group.CelestialBodyId >= 0)
            {
                var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
                var body = mgr?.GetBodyById(group.CelestialBodyId);
                centerInfo += body != null
                    ? string.Format("{0}(ID={1})", body.label, group.CelestialBodyId)
                    : string.Format("ID={0}", group.CelestialBodyId);
            }
            else
            {
                centerInfo += "无";
            }

            string detail = string.Format("{0} | {1} | {2}", typeInfo, orbitInfo, status);
            string posInfo = string.Format("位置: {0:F0}, {1:F0}, {2:F0}",
                group.CurrentOrbit.position.x, group.CurrentOrbit.position.y, group.CurrentOrbit.position.z);

            Rect inner = rect.ContractedBy(10f);
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.UpperLeft;
            Widgets.Label(new Rect(inner.x, inner.y - 2f, inner.width, 30f), title);
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(inner.x, inner.y + 20f, inner.width, 20f), detail);
            Widgets.Label(new Rect(inner.x, inner.y + 44f, inner.width, 20f), centerInfo);
            Widgets.Label(new Rect(inner.x, inner.y + 66f, inner.width, 20f), posInfo);
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private static void DrawPanelButton(Rect rect, SpaceEntityGroup group, SpaceGameComp spaceGame)
        {
            // --- 第零排：切换至该Group视角（太空战） ---
            Rect battleRect = new Rect(rect.x + 10f, rect.yMax - 160f, rect.width - 20f, 22f);
            if (Widgets.ButtonText(battleRect, "切换至该Group视角"))
            {
                Find.WindowStack.Add(new Window_SpaceBattleView3D(group));
            }

            // --- 第一排：跟随 ---
            Rect followRect = new Rect(rect.x + 10f, rect.yMax - 134f, rect.width - 20f, 22f);
            string followLabel = isFollowing && selectedGroupId == group.GroupId ? "停止跟随" : "跟随";
            if (Widgets.ButtonText(followRect, followLabel))
            {
                if (isFollowing && selectedGroupId == group.GroupId)
                {
                    isFollowing = false;
                }
                else
                {
                    selectedGroupId = group.GroupId;
                    isFollowing = true;
                    lastFollowTime = Time.time;
                }
            }

            // --- 第二排：圆轨/退出圆轨 ---
            Rect circularRect = new Rect(rect.x + 10f, rect.yMax - 108f, rect.width - 20f, 22f);
            if (group.IsCircularOrbit)
            {
                if (Widgets.ButtonText(circularRect, "退出圆轨"))
                {
                    group.ExitCircularOrbit();
                }
            }
            else
            {
                if (Widgets.ButtonText(circularRect, "圆轨"))
                {
                    var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
                    if (celestialMgr == null) return;

                    var bodies = celestialMgr.GetGravitySources();
                    if (bodies.Count == 0) return;

                    var options = new List<FloatMenuOption>();
                    foreach (var body in bodies)
                    {
                        string label = body.label;
                        // 标记默认选中
                        if (body.bodyId == group.CelestialBodyId)
                            label += " (当前系统)";

                        int capturedId = body.bodyId;
                        options.Add(new FloatMenuOption(label, () =>
                        {
                            int currentTick = Find.TickManager.TicksGame;
                            group.EnterCircularOrbit(currentTick, capturedId);
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            }

            // --- 第三排：机动规划 ---
            Rect planRect = new Rect(rect.x + 10f, rect.yMax - 82f, rect.width - 20f, 22f);
            if (Widgets.ButtonText(planRect, "机动规划"))
            {
                Find.WindowStack.Add(new ManeuverPlanner(group));
            }

            // --- 第四排：交会规划 ---
            Rect rendezvousRect = new Rect(rect.x + 10f, rect.yMax - 56f, rect.width - 20f, 22f);
            if (Widgets.ButtonText(rendezvousRect, "交会规划"))
            {
                Find.WindowStack.Add(new RendezvousPlanner(group, spaceGame));
            }

            // --- 第五排：转移到目标圆轨 + 机动队列 ---
            Rect matchRect = new Rect(rect.x + 10f, rect.yMax - 44f, rect.width * 0.48f - 5f, 22f);
            if (Widgets.ButtonText(matchRect, "转移到圆轨"))
            {
                Find.WindowStack.Add(new OrbitTransferPlanner(group, spaceGame));
            }
            Rect queueRect = new Rect(rect.x + rect.width * 0.52f + 5f, rect.yMax - 44f, rect.width * 0.48f - 15f, 22f);
            string queueLabel = group.plannedManeuvers.Count > 0
                ? string.Format("机动队列 ({0})", group.plannedManeuvers.Count)
                : "机动队列 (0)";
            if (Widgets.ButtonText(queueRect, queueLabel))
            {
                Find.WindowStack.Add(new ManeuverQueueWindow(group));
            }

            // --- 第六排：封存/取出（如果关联了地图） ---
            var mapEntity = GetMapEntityForGroup(group);
            if (mapEntity != null)
            {
                // 取档进行中 → 显示进度条
                if (spaceGame.ActiveRestoreOperation != null &&
                    spaceGame.ActiveRestoreOperation.TargetEntity == mapEntity)
                {
                    Rect progressRect = new Rect(rect.x + 10f, rect.yMax - 22f, rect.width - 20f, 22f);
                    float progress = spaceGame.ActiveRestoreOperation.Progress;
                    Widgets.FillableBar(progressRect, progress, Texture2D.linearGrayTexture,
                        Texture2D.whiteTexture, false);
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(progressRect, string.Format("取出中... {0:P0}", progress));
                    Text.Anchor = TextAnchor.UpperLeft;
                }
                else if (spaceGame.ActiveArchiveOperation != null &&
                    spaceGame.ActiveArchiveOperation.TargetEntity == mapEntity)
                {
                    // 封存进行中 → 显示进度条
                    Rect progressRect = new Rect(rect.x + 10f, rect.yMax - 22f, rect.width - 20f, 22f);
                    float progress = spaceGame.ActiveArchiveOperation.Progress;
                    Widgets.FillableBar(progressRect, progress, Texture2D.linearGrayTexture,
                        Texture2D.whiteTexture, false);
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(progressRect, string.Format("封存中... {0:P0}", progress));
                    Text.Anchor = TextAnchor.UpperLeft;
                }
                else if (mapEntity.IsMapArchived)
                {
                    // 已封存 → 显示取出按钮
                    Rect restoreRect = new Rect(rect.x + 10f, rect.yMax - 22f, rect.width - 20f, 22f);
                    if (Widgets.ButtonText(restoreRect, "取出Map"))
                    {
                        spaceGame.BeginRestoreMap(mapEntity);
                    }
                }
                else
                {
                    // 有活跃地图 → 封存按钮（Lite 裁剪：冻结功能已删除）
                    Rect archiveRect = new Rect(rect.x + 10f, rect.yMax - 22f, rect.width - 20f, 22f);
                    if (Widgets.ButtonText(archiveRect, "封存Map"))
                    {
                        var pocketMap = SpaceMapGenerator.FindPocketMap(mapEntity.MapId);
                        if (pocketMap != null && pocketMap.mapPawns.AllPawns.Any())
                        {
                            Messages.Message("Cannot archive: pawns still present on the station.",
                                MessageTypeDefOf.RejectInput, false);
                        }
                        else
                        {
                            spaceGame.BeginArchiveMap(mapEntity);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 获取 group 中关联地图的 SpaceEntity（用于地图操作）。
        /// </summary>
        private static SpaceEntity GetMapEntityForGroup(SpaceEntityGroup group)
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

        #endregion
    }
}