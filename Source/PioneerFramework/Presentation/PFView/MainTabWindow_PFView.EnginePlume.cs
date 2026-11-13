// ⛔【NOSTEPANYMORE】表现层文件（PFView 引擎喷焰）。只读后端状态，不写任何业务字段。
//    若下一步要改 Base/**、Components/**、Combat/**、Launch/** 等后端，按 BACKEND-LOCK.md 停机请示。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using PioneerFramework.Components;
using PioneerFramework.PFView.ShipBuildingVisuals;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
        /// <summary>
        /// 一台引擎的绘制缓存（视图系）。为什么不是每帧现算：
        /// 熄火后还要用最后一帧的几何做 0.3 s 余焰淡出；每帧清空会让火焰硬切消失。
        /// 字典按 (groupId, partId) 稳定键管理，未刷新且已淡完的项才移除。
        /// </summary>
        private sealed class EnginePlumeDrawItem
        {
            public int groupId;
            public int partId;
            public Vector3 nozzleView;
            public Vector3 dirView;
            public float radiusView;
            public float lengthView;
            public float inverseScale = 1f;
            public EnginePlumeRole role;
            public int colorBucket;
            public bool inAtmosphere;
            public int activeFrame = int.MinValue;
        }

        /// <summary>编队级平滑/闪光/交付进展状态。</summary>
        private sealed class EnginePlumeGroupState
        {
            public float smooth;
            public float target;
            public float intensity = 1f;
            public bool everSeen;
            public int lastSeenFrame = int.MinValue;
            public double lastTick;
            public double flashUntilTick;
            public double lastAppliedDv;
            public double lastPhysicsTick;
            public bool appliedValid;
        }

        private const float PlumeFadeInSeconds = 0.10f;
        private const float PlumeFadeOutSeconds = 0.30f;
        private const float PlumeFlashSeconds = 0.16f;
        private const int PlumeStatePruneFrames = 600;

        private PFViewEnginePlumeBatch? enginePlumeBatch;
        private readonly Dictionary<long, EnginePlumeDrawItem> enginePlumeDraw = new();
        private readonly List<long> enginePlumeRemoveScratch = new(32);
        private readonly Dictionary<int, EnginePlumeGroupState> enginePlumeGroups = new();
        private readonly List<int> enginePlumeStateScratch = new(16);
        private CelestialBodyManager? enginePlumeCelestialMgr;
        private Game? enginePlumeGame;

        private static long EnginePlumeKey(int groupId, int partId)
            => ((long)groupId << 32) ^ (uint)partId;

        /// <summary>每帧开始：换游戏实例时清缓存；记下本帧天体表（大气判定）。</summary>
        private void BeginEnginePlumeFrame(CelestialBodyManager? celestialMgr)
        {
            enginePlumeCelestialMgr = celestialMgr;
            PFViewEnginePlumeProbe.LastCollectedGroups = 0;
            PFViewEnginePlumeProbe.LastWholeReliefEntities = 0;
            PFViewEnginePlumeProbe.LastMapThrusters = 0;
            Game? game = Current.Game;
            if (!ReferenceEquals(game, enginePlumeGame))
            {
                enginePlumeGame = game;
                enginePlumeDraw.Clear();
                enginePlumeGroups.Clear();
            }
            // 有界：正常场景远小于此；异常（大量瞬态组）时整表清，避免无上限增长。
            if (enginePlumeDraw.Count > PFViewEnginePlumeProbe.MaxQuads / 3 * 4)
                enginePlumeDraw.Clear();
            PFViewEnginePlumeProbe.LastSkippedEngines = 0;
            if (!PFViewEnginePlumeProbe.LoggedFirstFrame)
            {
                PFViewEnginePlumeProbe.LoggedFirstFrame = true;
                Log.Message("[PFView·引擎喷焰] 采集钩子已运行（PFView 引擎特效已启用）");
            }
        }

        /// <summary>
        /// 采集一个实体上的引擎/推进器。调用点在 DrawGroupParts 的实体循环内：
        /// 此时 eVp/gRot/scale 已经是本帧真正用于画船的那一套。两条来源都收：
        /// · 零件引擎（外壳/零件船、舱盒级地图的引擎件）——partsVisible 时才画（被壳包住的件由 coveredPartIds 排除）；
        /// · 地图推进器建筑（旧式/遭遇舰：EncounterShipFactory 只造 hull part + 推进器建筑，没有 EngineComponentData）
        ///   ——按 CompThruster.ShipAxis 与建筑格坐标挂到浮雕/船体上。
        /// </summary>
        private void CollectEnginePlumes(SpaceEntity entity, SpaceEntityGroup group, Vector3 eVp,
            Quaternion gRot, float scale, HashSet<int>? coveredPartIds, bool partsVisible)
        {
            if (!PFViewEnginePlumeProbe.Enabled) return;
            if (PFViewEnginePlumeProbe.DrawOnlySelectedGroup && group.GroupId != selectedGroupId) return;

            bool force = PFViewEnginePlumeProbe.ForceAllEngines;
            EnginePlumeGroupState state = GetEnginePlumeState(group.GroupId);
            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();

            bool burning = group.HasActiveBurn && group.burnLastCmdDv > 0.0;
            if (burning)
            {
                // 交付进展门：HasActiveBurn/burnLastCmdDv 只说明"命令还在"；若**物理步**前进而
                // appliedBurnDV 没动 ⇒ 本步没有真的交付 Δv（对准等待/断油/断电），不画假火焰。
                if (state.appliedValid && physicsTick > state.lastPhysicsTick + 1e-6
                    && group.appliedBurnDV <= state.lastAppliedDv + 1e-9)
                {
                    burning = false;
                }
                state.lastAppliedDv = group.appliedBurnDV;
                state.lastPhysicsTick = physicsTick;
                state.appliedValid = true;
            }
            else
            {
                state.appliedValid = false;
            }

            bool hovering = !burning && group.hoverActive && !group.hoverAnalytic
                && group.hoverLastThrustN > 0.0;
            EnginePlumeRole role = force
                ? EnginePlumeRole.Main
                : EnginePlumeCore.ResolveRole(burning, group.burnUseRcs, hovering);
            if (!force && role == EnginePlumeRole.None) return;

            float itemIntensity = force
                ? 0.60f
                : hovering ? 0.35f : (float)EnginePlumeCore.PlumeIntensity(role);
            bool inAtmosphere = IsEnginePlumeInAtmosphere(group);
            int frame = RealTime.frameCount;
            state.target = 1f;
            state.lastSeenFrame = frame;
            state.intensity = itemIntensity;
            // 首次看到（例如先暂停、再打开 PFView）：立即到位；暂停只冻结"已有状态"。
            if (!state.everSeen)
            {
                state.everSeen = true;
                state.smooth = 1f;
            }
            PFViewEnginePlumeProbe.LastCollectedGroups++;

            int maxItems = PFViewEnginePlumeProbe.MaxQuads / 3;
            float viewScale = MetersToView * scale;
            float inverseScale = scale > 1e-6f ? 1f / scale : 1f;

            if (partsVisible && entity.Parts.Count > 0)
            {
                var parts = entity.Parts;
                int activeStage = 0;
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    if (p == null || p.isDestroyed) continue;
                    if (p.stageId > activeStage) activeStage = p.stageId;
                }

                for (int i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    if (part == null) continue;
                    if (coveredPartIds != null && coveredPartIds.Contains(part.partId)) continue;

                    var engine = part.GetComponent<EngineComponentData>();
                    bool hasEngine = engine != null;
                    DVector3 axisLocal = hasEngine ? engine!.thrustAxis.ShipVector() : DVector3.zero;

                    var input = new EnginePlumePartInput(part.partId, hasEngine, part.isDestroyed, part.stageId,
                        hasEngine && engine!.isVectoredRcs, part.positionShip, part.boundsSizeShip,
                        part.rotationShip, axisLocal,
                        hasEngine ? engine!.exhaustVelocity : 0f,
                        hasEngine ? engine!.massFlowRate : 0f,
                        hasEngine ? engine!.propellantDefName : null);

                    // 强制显示（锚点检查）时按件自身角色选：主网/RCS 都画；正常模式只画本帧角色。
                    EnginePlumeRole partRole = force
                        ? (hasEngine && engine!.isVectoredRcs ? EnginePlumeRole.Rcs : EnginePlumeRole.Main)
                        : role;
                    if (!EnginePlumeCore.TryBuildSpec(input, activeStage, partRole, out EnginePlumeSpec spec)) continue;
                    if (EnginePlumeCore.IsIntakeAir(engine!.propellantDefName)) continue;

                    Vector3 nozzleView = eVp + gRot * ((Vector3)spec.nozzleShip * viewScale);
                    Vector3 dirView = gRot * (Vector3)spec.exhaustShip;
                    AddEnginePlumeItem(group.GroupId, spec.partId, nozzleView, dirView,
                        (float)spec.nozzleRadiusMeters, itemIntensity, inAtmosphere, partRole,
                        spec.colorBucket, viewScale, inverseScale, frame, maxItems);
                }
            }

            CollectMapThrusterPlumes(entity, group, eVp, gRot, viewScale, inverseScale,
                itemIntensity, inAtmosphere, force, role, frame, maxItems);
        }

        /// <summary>
        /// 地图推进器建筑（旧式/遭遇舰）：EncounterShipFactory 的舰只有 hull part + 模板建筑，
        /// 推进网络来自 SpaceMapComp 的建筑推进器（没有 EngineComponentData）。这里按建筑格坐标
        /// + CompThruster.ShipAxis 画喷焰；只读 CompThruster 的公开状态，不碰后端。
        /// </summary>
        private void CollectMapThrusterPlumes(SpaceEntity entity, SpaceEntityGroup group, Vector3 eVp,
            Quaternion gRot, float viewScale, float inverseScale, float itemIntensity, bool inAtmosphere,
            bool force, EnginePlumeRole role, int frame, int maxItems)
        {
            if (sceneMapContexts.Count == 0) return;
            List<ThingDef> defs = EnginePlumeThrusterDefs();
            if (defs.Count == 0) return;

            for (int c = 0; c < sceneMapContexts.Count; c++)
            {
                MapVisualContext context = sceneMapContexts[c];
                Map? map = context.Map;
                if (map == null) continue;
                DQuaternion ownerRotation = context.OwnerPart?.rotationShip ?? DQuaternion.identity;

                for (int d = 0; d < defs.Count; d++)
                {
                    List<Thing>? things = map.listerThings.ThingsOfDef(defs[d]);
                    if (things == null) continue;
                    for (int i = 0; i < things.Count; i++)
                    {
                        Thing thing = things[i];
                        if (thing == null || thing.Destroyed || !thing.Spawned) continue;
                        CompThruster? thruster = thing.TryGetComp<CompThruster>();
                        if (thruster == null) continue;

                        bool isRcs = thruster.IsVectoredRcs;
                        if (!force)
                        {
                            if (role == EnginePlumeRole.Main && isRcs) continue;
                            if (role == EnginePlumeRole.Rcs && !isRcs) continue;
                            if (!thruster.IsBurning) continue;
                        }

                        DVector3 axisShip = thruster.ShipAxis.ShipVector();
                        if (ownerRotation != DQuaternion.identity)
                            axisShip = DVector3.Rotate(axisShip, ownerRotation);
                        if (axisShip.sqrMagnitude < 1e-12) continue;
                        axisShip = axisShip.normalized;

                        DVector3 cellShip = (DVector3)MapCoordMapper.MapCellToShip(map, thing.Position);
                        // 建筑格在 y=0 的船体**中面**上：直接画点会被 8–10 m 厚的浮雕/船体深度挡掉
                        // （用户实测"计数正常但看不见"）。沿排气方向推到船体 AABB 表面外 0.25 m。
                        DVector3 half = entity.AabbHalfSize;
                        double halfAlong = Math.Abs(axisShip.x) * half.x
                            + Math.Abs(axisShip.y) * half.y
                            + Math.Abs(axisShip.z) * half.z;
                        double fromCenter = cellShip.x * axisShip.x + cellShip.y * axisShip.y
                            + cellShip.z * axisShip.z;
                        double escape = halfAlong - fromCenter + 0.25;
                        if (escape < 0.25) escape = 0.25;
                        DVector3 nozzleShip = cellShip + axisShip * escape;
                        string? propellant = thruster.PropellantDef?.defName;
                        if (EnginePlumeCore.IsIntakeAir(propellant)) continue;

                        Vector3 nozzleView = eVp + gRot * ((Vector3)nozzleShip * viewScale);
                        Vector3 dirView = gRot * (Vector3)axisShip;
                        AddEnginePlumeItem(group.GroupId, -thing.thingIDNumber, nozzleView, dirView,
                            1.0f, itemIntensity, inAtmosphere,
                            isRcs ? EnginePlumeRole.Rcs : EnginePlumeRole.Main,
                            EnginePlumeCore.ColorBucketFromPropellant(propellant),
                            viewScale, inverseScale, frame, maxItems);
                        PFViewEnginePlumeProbe.LastMapThrusters++;
                    }
                }
            }
        }

        /// <summary>把一台引擎的视图系参数并入持久化绘制表（同键覆盖几何；熄火后留作余焰）。</summary>
        private void AddEnginePlumeItem(int groupId, int itemId, Vector3 nozzleView, Vector3 dirView,
            float radiusMeters, float itemIntensity, bool inAtmosphere, EnginePlumeRole role, int colorBucket,
            float viewScale, float inverseScale, int frame, int maxItems)
        {
            float radiusView = radiusMeters * viewScale;
            double baseLengthMeters = EnginePlumeCore.PlumeLengthMeters(radiusMeters, itemIntensity, inAtmosphere);
            float lengthView = (float)baseLengthMeters * viewScale;
            if (!IsFinite(nozzleView) || !IsFinite(dirView)
                || !float.IsFinite(radiusView) || !float.IsFinite(lengthView)
                || radiusView <= 0f || lengthView <= 0f || dirView.sqrMagnitude < 1e-8f)
            {
                PFViewEnginePlumeProbe.LastSkippedEngines++;
                return;
            }

            long key = EnginePlumeKey(groupId, itemId);
            if (!enginePlumeDraw.TryGetValue(key, out EnginePlumeDrawItem? item))
            {
                if (enginePlumeDraw.Count >= maxItems)
                {
                    PFViewEnginePlumeProbe.LastSkippedEngines++;
                    return;
                }
                item = new EnginePlumeDrawItem { groupId = groupId, partId = itemId };
                enginePlumeDraw[key] = item;
            }

            item.nozzleView = nozzleView;
            item.dirView = dirView;
            item.radiusView = radiusView;
            item.lengthView = lengthView;
            item.inverseScale = inverseScale;
            item.role = role;
            item.colorBucket = colorBucket;
            item.inAtmosphere = inAtmosphere;
            item.activeFrame = frame;
        }

        private static List<ThingDef>? enginePlumeThrusterDefs;

        /// <summary>所有带 CompProperties_Thruster 的 ThingDef（一次性缓存；地图推进器用）。</summary>
        private static List<ThingDef> EnginePlumeThrusterDefs()
        {
            if (enginePlumeThrusterDefs != null) return enginePlumeThrusterDefs;
            var list = new List<ThingDef>();
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def?.comps == null) continue;
                for (int i = 0; i < def.comps.Count; i++)
                {
                    if (def.comps[i] is CompProperties_Thruster)
                    {
                        list.Add(def);
                        break;
                    }
                }
            }
            enginePlumeThrusterDefs = list;
            return list;
        }

        /// <summary>
        /// 每帧推进平滑/闪光状态，然后逐台画"喷口点 + 焰心 + 外辉光"。
        ///
        /// 2026-11-08 第四轮：不再依赖 PF_ProjectileGlow 的自拼 mesh（该 shader 没有其它消费者，
        /// 用户实测"计数正常但不可见"）⇒ 改用**已验证在 PFView 相机下可见**的
        /// ShaderDatabase.WorldOverlayAdditiveTwoSided + 代码生成的软点纹理 + quadMesh，
        /// 世界空间逐片绘制（喷焰/辉光/喷口点各 1 次 DrawMesh）。牺牲 1 批变 N 次，换可见性。
        /// </summary>
        private void DrawEnginePlumes(double tick)
        {
            UpdateEnginePlumeStates(tick);
            int frame = RealTime.frameCount;

            if (!PFViewEnginePlumeProbe.Enabled || enginePlumeDraw.Count == 0 || renderCamera == null
                || renderTexture == null)
            {
                PFViewEnginePlumeProbe.LastEngineCount = 0;
                PFViewEnginePlumeProbe.LastQuadCount = 0;
                return;
            }

            Material? plumeMaterial = EnsureEnginePlumeMaterial();
            if (plumeMaterial == null)
            {
                PFViewEnginePlumeProbe.LastEngineCount = 0;
                PFViewEnginePlumeProbe.LastQuadCount = 0;
                return;
            }

            Vector3 cameraPosition = renderCamera.transform.position;
            Quaternion cameraRotation = renderCamera.transform.rotation;
            Quaternion toEye = Quaternion.Inverse(cameraRotation);
            Vector3 cameraRight = cameraRotation * Vector3.right;
            Vector3 cameraUp = cameraRotation * Vector3.up;
            float near = renderCamera.nearClipPlane;
            float focalPixels = renderTexture.height * 0.5f
                / Mathf.Tan(renderCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            int drawnEngines = 0;
            int drawnQuads = 0;
            enginePlumeRemoveScratch.Clear();

            foreach (var pair in enginePlumeDraw)
            {
                EnginePlumeDrawItem item = pair.Value;
                if (!enginePlumeGroups.TryGetValue(item.groupId, out var state))
                {
                    enginePlumeRemoveScratch.Add(pair.Key);
                    continue;
                }
                if (state.smooth <= 0.01f)
                {
                    if (item.activeFrame != frame) enginePlumeRemoveScratch.Add(pair.Key);
                    continue;
                }
                if (PFViewEnginePlumeProbe.DrawOnlySelectedGroup && item.groupId != selectedGroupId)
                    continue;

                Vector3 nozzleEye = toEye * (item.nozzleView - cameraPosition);
                if (nozzleEye.z <= near) continue;

                double flicker = EnginePlumeCore.Flicker(item.partId, tick);
                float flick = EnginePlumeStyle.ApplyFlicker(flicker, item.role);
                float intensity = Mathf.Clamp01(state.intensity * state.smooth
                    * PFViewEnginePlumeProbe.IntensityScale);
                if (intensity <= 0.01f) continue;

                float radius = item.radiusView;
                float length = item.lengthView * state.smooth * flick * PFViewEnginePlumeProbe.IntensityScale;
                float projectedLengthPixels = length / Mathf.Max(nozzleEye.z, 0.001f) * focalPixels;
                int lod = EnginePlumeCore.LodQuadCountForProjectedLength(projectedLengthPixels);
                if (lod <= 0) continue;

                EnginePlumeColors colors = EnginePlumeStyle.ColorsFor(item.colorBucket, item.inAtmosphere);
                bool nozzleOnly = PFViewEnginePlumeProbe.NozzleOnly;
                Vector3 dirWorld = item.dirView.sqrMagnitude > 1e-8f
                    ? item.dirView.normalized : cameraUp;

                if (!nozzleOnly && lod >= 3)
                {
                    DrawSoftQuad(item.nozzleView, dirWorld, length * EnginePlumeStyle.GlowLengthFraction,
                        radius * EnginePlumeStyle.GlowTailWidthFraction,
                        EnginePlumeStyle.WithAlpha(colors.glow, EnginePlumeStyle.GlowHeadAlpha * intensity),
                        cameraRight, cameraUp, plumeMaterial);
                    drawnQuads++;
                }
                if (!nozzleOnly && lod >= 2)
                {
                    float coreAlpha = EnginePlumeStyle.CoreHeadAlpha * intensity;
                    Color coreColor = Color.Lerp(colors.head, colors.tail, 0.35f);
                    DrawSoftQuad(item.nozzleView, dirWorld, length,
                        radius * EnginePlumeStyle.CoreTailWidthFraction,
                        EnginePlumeStyle.WithAlpha(coreColor, coreAlpha),
                        cameraRight, cameraUp, plumeMaterial);
                    drawnQuads++;
                }

                float minDotPixels = PFViewEnginePlumeProbe.NozzleOnly ? 3.0f : 2.0f;
                float dotRadius = Mathf.Max(radius * EnginePlumeStyle.DotRadiusFraction,
                    nozzleEye.z * minDotPixels / Mathf.Max(focalPixels, 1f));
                float dotAlpha = EnginePlumeStyle.DotAlpha * intensity;
                if (state.flashUntilTick > tick)
                {
                    float flash = Mathf.Clamp01((float)((state.flashUntilTick - tick) / PlumeFlashSeconds));
                    dotRadius *= 1f + 1.2f * flash;
                    dotAlpha = Mathf.Clamp01(dotAlpha + 0.5f * flash);
                }
                DrawSoftDot(item.nozzleView, dotRadius,
                    EnginePlumeStyle.WithAlpha(colors.dot, dotAlpha),
                    cameraRight, cameraUp, plumeMaterial);
                drawnQuads++;

                if (!PFViewEnginePlumeProbe.LoggedFirstMarker)
                {
                    PFViewEnginePlumeProbe.LoggedFirstMarker = true;
                    Log.Message("[PFView·引擎喷焰] 首个喷口：anchor=("
                        + item.nozzleView.x.ToString("F2") + "," + item.nozzleView.y.ToString("F2")
                        + "," + item.nozzleView.z.ToString("F2") + ") eye.z=" + nozzleEye.z.ToString("F2")
                        + " near=" + near.ToString("F3") + " 投影长度px=" + projectedLengthPixels.ToString("F1")
                        + " dotRadius=" + dotRadius.ToString("F3") + " lod=" + lod
                        + " 相机距=" + ((item.nozzleView - cameraPosition).magnitude).ToString("F2"));
                }
                drawnEngines++;
            }

            for (int i = 0; i < enginePlumeRemoveScratch.Count; i++)
                enginePlumeDraw.Remove(enginePlumeRemoveScratch[i]);

            PFViewEnginePlumeProbe.LastEngineCount = drawnEngines;
            PFViewEnginePlumeProbe.LastQuadCount = drawnQuads;

            if ((PFViewEnginePlumeProbe.ForceAllEngines || PFViewEnginePlumeProbe.NozzleOnly)
                && frame >= PFViewEnginePlumeProbe.NextLogFrame)
            {
                PFViewEnginePlumeProbe.NextLogFrame = frame + 180;
                Log.Message("[PFView·引擎喷焰] 调试读数：采集实体=" + PFViewEnginePlumeProbe.LastCollectedGroups
                    + " 引擎件=" + enginePlumeDraw.Count
                    + " 绘制=" + drawnEngines
                    + " 片=" + drawnQuads
                    + " 超限=" + PFViewEnginePlumeProbe.LastSkippedEngines
                    + " 整船浮雕=" + PFViewEnginePlumeProbe.LastWholeReliefEntities
                    + " 推进器建筑=" + PFViewEnginePlumeProbe.LastMapThrusters
                    + " 材质=WorldOverlayAdditiveTwoSided");
            }
        }

        /// <summary>把 unit quad 拉成一根"沿排气方向、宽度方向取相机右"的软边光带。</summary>
        private void DrawSoftQuad(Vector3 anchorWorld, Vector3 dirWorld, float lengthWorld, float halfWidthWorld,
            Color color, Vector3 cameraRight, Vector3 cameraUp, Material material)
        {
            if (lengthWorld <= 1e-4f || halfWidthWorld <= 1e-4f) return;
            Vector3 dir = dirWorld.sqrMagnitude > 1e-8f ? dirWorld.normalized : cameraUp;
            Vector3 side = Vector3.Cross(cameraRight, dir);
            if (side.sqrMagnitude < 1e-6f) side = cameraUp;
            side.Normalize();

            Matrix4x4 m = Matrix4x4.identity;
            m.SetColumn(0, new Vector4(cameraRight.x * halfWidthWorld * 2f, cameraRight.y * halfWidthWorld * 2f,
                cameraRight.z * halfWidthWorld * 2f, 0f));
            m.SetColumn(1, new Vector4(dir.x * lengthWorld, dir.y * lengthWorld, dir.z * lengthWorld, 0f));
            m.SetColumn(2, new Vector4(side.x, side.y, side.z, 0f));
            m.SetColumn(3, new Vector4(anchorWorld.x + dir.x * lengthWorld * 0.5f,
                anchorWorld.y + dir.y * lengthWorld * 0.5f,
                anchorWorld.z + dir.z * lengthWorld * 0.5f, 1f));

            matBlock.SetColor("_Color", color);
            Graphics.DrawMesh(EnginePlumeQuadMesh(), m, material, SceneLayer, null, 0, matBlock);
        }

        /// <summary>正对相机的软圆点（喷口辉光）。</summary>
        private void DrawSoftDot(Vector3 centerWorld, float radiusWorld, Color color,
            Vector3 cameraRight, Vector3 cameraUp, Material material)
        {
            if (radiusWorld <= 1e-4f) return;
            Matrix4x4 m = Matrix4x4.identity;
            m.SetColumn(0, new Vector4(cameraRight.x * radiusWorld * 2f, cameraRight.y * radiusWorld * 2f,
                cameraRight.z * radiusWorld * 2f, 0f));
            m.SetColumn(1, new Vector4(cameraUp.x * radiusWorld * 2f, cameraUp.y * radiusWorld * 2f,
                cameraUp.z * radiusWorld * 2f, 0f));
            m.SetColumn(2, new Vector4(0f, 0f, 1f, 0f));
            m.SetColumn(3, new Vector4(centerWorld.x, centerWorld.y, centerWorld.z, 1f));
            matBlock.SetColor("_Color", color);
            Graphics.DrawMesh(EnginePlumeQuadMesh(), m, material, SceneLayer, null, 0, matBlock);
        }


        private static Mesh? enginePlumeQuadMesh;

        /// <summary>
        /// 喷焰专用单位四边形：**必须带 UV0**。窗口的 quadMesh 没有 UV（只用于不受贴图影响的 PFLit），
        /// 拿它配软点纹理会永远采样 (0,0)（alpha=0）⇒ 完全透明、看不见（2026-11-08 用户实测根因）。
        /// </summary>
        private static Mesh EnginePlumeQuadMesh()
        {
            if (enginePlumeQuadMesh != null) return enginePlumeQuadMesh;
            var mesh = new Mesh { name = "PF_EnginePlumeQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 1f), new Vector2(1f, 1f),
            };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateNormals();
            enginePlumeQuadMesh = mesh;
            return mesh;
        }

        private static Material? enginePlumeMaterial;
        private static Texture2D? enginePlumeDotTexture;
        private static bool enginePlumeMaterialWarned;

        /// <summary>喷焰材质：原版世界叠加（加性、双面）+ 代码生成软点纹理（零新增资产）。</summary>
        private static Material? EnsureEnginePlumeMaterial()
        {
            if (enginePlumeMaterial != null) return enginePlumeMaterial;
            Texture2D? tex = BuildEnginePlumeSoftDot();
            if (tex == null) return null;
            enginePlumeDotTexture = tex;
            var mat = new Material(ShaderDatabase.WorldOverlayAdditiveTwoSided) { mainTexture = tex };
            mat.renderQueue = 3400;
            enginePlumeMaterial = mat;
            return mat;
        }

        private static Texture2D? BuildEnginePlumeSoftDot()
        {
            try
            {
                const int n = 32;
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "PF_EnginePlumeDot" };
                var px = new Color32[n * n];
                const float half = (n - 1) * 0.5f;
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        float dx = (x - half) / half;
                        float dy = (y - half) / half;
                        float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                        a = a * a * (3f - 2f * a);
                        byte v = (byte)Mathf.RoundToInt(a * 255f);
                        px[y * n + x] = new Color32(v, v, v, v);
                    }
                }
                tex.SetPixels32(px);
                tex.Apply();
                tex.wrapMode = TextureWrapMode.Clamp;
                return tex;
            }
            catch (System.Exception e)
            {
                if (!enginePlumeMaterialWarned)
                {
                    enginePlumeMaterialWarned = true;
                    Log.Warning("[PFL] PFView engine plume soft-dot texture failed: " + e.Message);
                }
                return null;
            }
        }

        private void UpdateEnginePlumeStates(double tick)
        {
            int frame = RealTime.frameCount;
            enginePlumeStateScratch.Clear();
            foreach (var pair in enginePlumeGroups)
            {
                var state = pair.Value;
                double dt = state.lastTick > 0.0 ? tick - state.lastTick : 0.0;
                if (dt < 0.0) dt = 0.0;
                if (dt > 0.5) dt = 0.5;   // 切档/卡顿保护：不要一步跳满
                state.lastTick = tick;

                bool seen = state.lastSeenFrame == frame;
                float target = seen ? state.target : 0f;
                if (target > 0f && state.smooth <= 0f && state.flashUntilTick <= tick)
                    state.flashUntilTick = tick + PlumeFlashSeconds;

                if (target > state.smooth)
                    state.smooth = Mathf.Min(target, state.smooth + (float)(dt / PlumeFadeInSeconds));
                else if (target < state.smooth)
                    state.smooth = Mathf.Max(target, state.smooth - (float)(dt / PlumeFadeOutSeconds));

                if (state.smooth <= 0.001f && target <= 0f && frame - state.lastSeenFrame > PlumeStatePruneFrames)
                    enginePlumeStateScratch.Add(pair.Key);
            }
            for (int i = 0; i < enginePlumeStateScratch.Count; i++)
                enginePlumeGroups.Remove(enginePlumeStateScratch[i]);
        }

        private EnginePlumeGroupState GetEnginePlumeState(int groupId)
        {
            if (!enginePlumeGroups.TryGetValue(groupId, out var state))
            {
                state = new EnginePlumeGroupState();
                enginePlumeGroups[groupId] = state;
            }
            return state;
        }

        /// <summary>本编队是否在某天体的物理大气顶以内（大气内羽流略长；无大气/深空 = false）。</summary>
        private bool IsEnginePlumeInAtmosphere(SpaceEntityGroup group)
        {
            CelestialBodyManager? mgr = enginePlumeCelestialMgr;
            if (mgr == null) return false;
            var def = mgr.GetBodyById(group.CelestialBodyId);
            if (def == null || def.atmosphereThickness <= 0.0) return false;
            double altitude = group.CurrentOrbit.positionSystem.v.magnitude - def.radius;
            return altitude >= 0.0 && altitude < def.atmosphereThickness;
        }

        private static bool IsFinite(Vector3 v)
            => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

        private void ReleaseEnginePlumeResources()
        {
            enginePlumeBatch?.Release();
            enginePlumeBatch = null;
            enginePlumeGroups.Clear();
            enginePlumeDraw.Clear();
            enginePlumeRemoveScratch.Clear();
            enginePlumeStateScratch.Clear();
            enginePlumeCelestialMgr = null;
            enginePlumeGame = null;
            PFViewEnginePlumeProbe.LastEngineCount = 0;
            PFViewEnginePlumeProbe.LastQuadCount = 0;
            PFViewEnginePlumeProbe.LastSkippedEngines = 0;
        }
    }
}