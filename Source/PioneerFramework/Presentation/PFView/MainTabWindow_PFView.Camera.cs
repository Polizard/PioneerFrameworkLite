using System.Collections.Generic;
using UnityEngine;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 视图系统 v2（2026-10-18）：**焦点与选中解耦** + 相机保活 + 换焦点过渡 + 丢失冻结。
    ///
    /// 设计口径（计划案 §3）：
    /// · selection（selectedGroupId）= 详情/射手；focus = 相机锚点；target = 攻击对象；三者独立。
    /// · 只有显式动作（双击/「前往编队」/「行星视角」/Shift+左键设目标）才改相机或目标；
    ///   左键单击只选详情，**不动相机**。
    /// · 换焦点保留 yaw/pitch，距离按新档投影；0.25–0.45 s 平滑过渡（输入可打断）。
    /// · 关窗/重开不重置；编队丢失 → Frozen（相机不动）+ 视图内 toast。
    ///
    /// ⚠ 本文件只做「取哪、怎么动」；渲染/换系/天体集仍在 Scene.cs / Render.cs 的既有入口。
    /// 坐标系：view space（AbsToView 之后），1 单位 = 10 m，与旧实现一致。
    /// </summary>
    public partial class MainTabWindow_PFView
    {
        private sealed class CameraPose
        {
            public float Yaw;
            public float Pitch;
            public float Distance;
            public Vector3 Pan;
            public PFViewGroupCameraMode Mode;
            public Quaternion Frame = Quaternion.identity;
            public FrozenCameraPose? Frozen;
            public Quaternion FrozenFrameSystem = Quaternion.identity;
            public PFViewPilotCameraOptions PilotOptions = PFViewPilotCameraOptions.Default;
            public Quaternion StableHeading = Quaternion.identity;
            public Quaternion FollowHeading = Quaternion.identity;
            public bool HasPilotFrame;
            public bool LookReturning;
        }

        private sealed record PreviousView(Game? Game, PFViewFocusKind Kind, int GroupId, int BodyId,
            int SystemBodyId, DVector3 Frozen, CameraPose Pose, PFViewFrameMode Frame);
        private readonly PFViewUiViewHistory<PreviousView> viewHistory = new();
        private float nextViewLimitToast;

        private void RememberPreviousView()
        {
            if (cameraInitialized)
                viewHistory.Remember(new(Current.Game, focusKind, focusGroupId, focusBodyId,
                    currentSystemBodyId, frozenAbs, CapturePose(), viewFrameMode));
        }

        private bool IsPreviousViewValid(PreviousView snapshot)
        {
            if (!ReferenceEquals(snapshot.Game, Current.Game)) return false;
            if (snapshot.Kind == PFViewFocusKind.Group)
                return CanInspectGroup(snapshot.GroupId);
            return snapshot.Kind == PFViewFocusKind.Frozen
                || Current.Game?.GetComponent<CelestialBodyManager>()?.GetBodyById(snapshot.BodyId) != null;
        }

        private void RestorePreviousView()
        {
            bool hadHistory = viewHistory.HasValue;
            if (!viewHistory.TryTake(IsPreviousViewValid, out PreviousView? snapshot))
            {
                QueueToast((hadHistory ? "PFV_ViewUnavailable" : "PFV_NoPreviousView").Translate());
                return;
            }
            SaveCameraMemory();
            var oldOrigin = ResolveFocusOrigin();
            var oldPose = CapturePose();
            focusKind = snapshot!.Kind;
            focusGroupId = snapshot.GroupId;
            focusBodyId = snapshot.BodyId;
            currentSystemBodyId = snapshot.SystemBodyId;
            frozenAbs = snapshot.Frozen;
            ApplyCameraPose(snapshot.Pose);
            viewFrameMode = snapshot.Frame;
            objectsDirty = true;
            BeginTransition(oldOrigin, oldPose);
        }

        private void NotifyViewLimit()
        {
            if (Time.realtimeSinceStartup < nextViewLimitToast) return;
            nextViewLimitToast = Time.realtimeSinceStartup + 2f;
            QueueToast("PFV_ViewLimit".Translate());
        }

        // ── 焦点（相机锚点） ──
        /// <summary>PFView 是否打开（供 HUD 输入隔离补丁判据）。</summary>
        internal static bool IsOpen => Find.WindowStack?.WindowOfType<MainTabWindow_PFView>() != null;

        private PFViewFocusKind focusKind = PFViewFocusKind.Body;
        private int focusGroupId = -1;
        private int focusBodyId = 1;
        private DVector3 frozenAbs;
        private DVector3 lastFocusAbs;
        private bool cameraInitialized;
        private Game? cameraGame;

        // ── 实际显示值：换焦点过渡与滚轮平滑共用，camDistance 保留目标距离 ──
        private float camDisplayDistance;
        private bool cameraTransitioning;
        private float transitionElapsed;
        private float transitionDuration;
        private float transitionStartDistance;
        private DVector3 transitionStartLookAbs;
        private Quaternion transitionStartRotationSystem = Quaternion.identity;

        // 右键「点击 vs 拖拽」判定（右键单击 = 上下文菜单，拖拽 = 旋转）
        private Vector2 rightMouseDownPos;
        private readonly PFViewOrbitDragState orbitDrag = new();

        // ── 会话级机位记忆（不写档）：key = (焦点类型, id) ──
        private static readonly Dictionary<(PFViewFocusKind kind, int id), CameraPose> cameraMemory = new();

        // ════════════════════════════════════════════════════
        //  初始化 / 保活
        // ════════════════════════════════════════════════════

        /// <summary>
        /// EnsureResources 调：第一次开窗建默认取景；之后重开**保留**上次机位（U2）。
        /// 换局/读档（Game 引用变化）清空机位记忆并回到默认取景。
        /// </summary>
        internal void RestoreOrInitCamera()
        {
            if (cameraGame != null && !ReferenceEquals(cameraGame, Current.Game))
            {
                cameraMemory.Clear();
                viewHistory.Clear();
                ResetCameraSession();
                cameraInitialized = false;
                selectedGroupId = -1;   // 换局/读档：旧存档的 id 不能带进新局
                targetGroupId = -1;
                lastFocusAbs = DVector3.zero;
            }
            cameraGame = Current.Game;

            if (cameraInitialized) return;
            RefreshPilotCameraOptions();

            camYaw = 0f;
            camPitch = PFViewCameraCore.DefaultPitch;
            camFocusOffset = Vector3.zero;

            if (CanInspectGroup(selectedGroupId) && PhysicsApi.TryGetSpacecraft(selectedGroupId, out var selectedSnap))
            {
                focusKind = PFViewFocusKind.Group;
                focusGroupId = selectedGroupId;
                focusBodyId = selectedSnap.CelestialBodyId >= 0 ? selectedSnap.CelestialBodyId : currentSystemBodyId;
                groupCameraMode = DefaultGroupCameraMode();
                InitializeGroupCameraPose(selectedGroupId, PFViewFocusKind.Body, null);
            }
            else
            {
                focusKind = PFViewFocusKind.Body;
                focusGroupId = -1;
                focusBodyId = currentSystemBodyId;
                camDistance = DefaultBodyDistance();
            }

            camDisplayDistance = camDistance;
            cameraInitialized = true;
        }

        /// <summary>关窗时保存当前机位（供重开恢复；不写档）。</summary>
        internal void SaveCameraMemory()
        {
            if (!cameraInitialized) return;
            if (focusKind == PFViewFocusKind.Group)
                groupCameraMemory.Remember(focusGroupId, groupCameraMode, CapturePose());
            else if (focusKind == PFViewFocusKind.Body)
                cameraMemory[(focusKind, FocusId())] = CapturePose();
        }

        private int FocusId()
            => focusKind == PFViewFocusKind.Group ? focusGroupId : focusBodyId;

        private CameraPose CapturePose() => new()
        {
            Yaw = camYaw,
            Pitch = camPitch,
            Distance = camDistance,
            Pan = camFocusOffset,
            Mode = focusKind == PFViewFocusKind.Group ? groupCameraMode : PFViewGroupCameraMode.Free,
            Frame = ResolveOrbitFrame(),
            Frozen = frozenCameraPose,
            FrozenFrameSystem = frozenFreeFrameSystem,
            PilotOptions = pilotCameraOptions,
            StableHeading = stableHeadingSystem,
            FollowHeading = displayedFollowSystem,
            HasPilotFrame = hasPilotFollowFrame,
            LookReturning = pilotLookReturning,
        };

        private bool TryLoadPose()
        {
            if (focusKind != PFViewFocusKind.Group && focusKind != PFViewFocusKind.Body) return false;
            CameraPose? pose;
            if (focusKind == PFViewFocusKind.Group)
            {
                if (!groupCameraMemory.TryGet(focusGroupId, groupCameraMode, out pose)) return false;
            }
            else if (!cameraMemory.TryGetValue((focusKind, FocusId()), out pose)) return false;
            ApplyCameraPose(pose);
            return true;
        }

        // ════════════════════════════════════════════════════
        //  焦点动作（只有这些会动相机）
        // ════════════════════════════════════════════════════

        internal void FocusGroup(int groupId)
        {
            if (!CanInspectGroup(groupId) || !PhysicsApi.TryGetSpacecraft(groupId, out var g))
            {
                QueueToast($"编队 G{groupId} 不存在或已销毁");
                return;
            }

            PFViewGroupCameraMode requestedMode = DefaultGroupCameraMode();
            if (focusKind == PFViewFocusKind.Group && focusGroupId == groupId)
            {
                if (requestedMode != groupCameraMode) SwitchGroupCameraMode(requestedMode);
                else if (ReadPilotCameraOptions() != pilotCameraOptions)
                {
                    RememberPreviousView();
                    SaveCameraMemory();
                    var optionsOrigin = ResolveFocusOrigin();
                    var optionsPose = CapturePose();
                    RefreshPilotCameraOptions();
                    BeginTransition(optionsOrigin, optionsPose);
                }
                return;
            }

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            RememberPreviousView();
            SaveCameraMemory();
            DVector3 oldOrigin = ResolveFocusOrigin();
            CameraPose oldPose = CapturePose();
            PFViewFocusKind oldKind = focusKind;

            focusKind = PFViewFocusKind.Group;
            focusGroupId = groupId;
            groupCameraMode = requestedMode;
            focusBodyId = g.CelestialBodyId >= 0 ? g.CelestialBodyId : currentSystemBodyId;
            int root = mgr != null ? mgr.GetRootPlanetId(focusBodyId) : -1;
            // 编队可能在恒星/深空（root=-1）：至少让「当前系统」指向它自己的天体，
            // 否则 GetCurrentSystemBodyIds 不包含它 → 选中后连它自己都不画（2026-10-18 用户报障）。
            int systemBody = root >= 0 ? root : focusBodyId;
            if (systemBody >= 0) currentSystemBodyId = systemBody;

            if (!TryLoadPose())
            {
                InitializeGroupCameraPose(groupId, oldKind, oldPose);
            }
            RefreshPilotCameraOptions();

            BeginTransition(oldOrigin, oldPose);
            objectsDirty = true;
        }

        internal void FocusBody(int bodyId)
        {
            var body = Current.Game?.GetComponent<CelestialBodyManager>()?.GetBodyById(bodyId);
            if (body == null) return;
            if (focusKind == PFViewFocusKind.Body && focusBodyId == bodyId) { RefreshPilotCameraOptions(); return; }
            RememberPreviousView();
            SaveCameraMemory();
            DVector3 oldOrigin = ResolveFocusOrigin();
            CameraPose oldPose = CapturePose();

            focusKind = PFViewFocusKind.Body;
            focusBodyId = bodyId;
            focusGroupId = -1;
            groupCameraMode = PFViewGroupCameraMode.Free;
            frozenCameraPose = null;
            currentSystemBodyId = bodyId;

            if (!TryLoadPose())
            {
                camFocusOffset = Vector3.zero;
                camDistance = DefaultBodyDistance();
            }
            RefreshPilotCameraOptions();

            BeginTransition(oldOrigin, oldPose);
        }

        /// <summary>
        /// 焦点切到某天体并**直接降到指定高度**（米）。调试面板用：默认 FocusBody 的机位是 4 倍半径，
        /// 从那里看不出地表纹理/山脊（一格只有 2 px）。这里跳过过渡直接落到近景。
        /// </summary>
        internal void FocusBodyAtAltitude(int bodyId, double altitudeMeters)
        {
            FocusBody(bodyId);
            var body = Current.Game?.GetComponent<CelestialBodyManager>()?.GetBodyById(bodyId);
            if (body == null) return;
            float distance = (float)((body.radius + System.Math.Max(altitudeMeters, 1000.0)) * MetersToView);
            float radiusView = Mathf.Max((float)(body.radius * MetersToView), 1f);
            float clamped = PFViewCameraCore.ClampDistance(PFViewFocusKind.Body, distance, MetersToView, radiusView);
            Log.Message("[PFView·相机] 聚焦 " + body.defName + "：目标高度=" + (System.Math.Max(altitudeMeters, 1000.0) / 1000.0).ToString("F0") +
                " km｜实际可用=" + ((clamped - radiusView) / MetersToView / 1000.0).ToString("F0") + " km（近距下限 = 1.2×半径）");
            camDistance = distance;
            camDisplayDistance = distance;
            cameraTransitioning = false;
            transitionStartDistance = distance;
        }

        /// <summary>「行星视角」按钮/Home：焦点回到当前天体（保留选中与目标）。</summary>
        internal void FocusCurrentBody()
        {
            int bodyId = currentSystemBodyId;
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr != null && mgr.GetBodyById(bodyId) == null)
            {
                var star = mgr.GetStar();
                if (star != null) bodyId = star.bodyId;
            }
            FocusBody(bodyId);
        }

        /// <summary>「回到编队」：有选中就聚焦选中，否则聚焦焦点编队（无则提示）。</summary>
        internal void FocusSelectionOrNothing()
        {
            if (selectedGroupId >= 0)
            {
                FocusGroup(selectedGroupId);
                return;
            }
            QueueToast("没有选中的编队（先点左栏或 3D 里的船）");
        }

        internal void SetTargetGroup(int groupId)
        {
            if (groupId < 0)
            {
                targetGroupId = -1;
                QueueToast("PFV_CockpitTargetCleared".Translate());
                return;
            }
            if (!TryGetKnownContact(groupId, out var contact))
            {
                QueueToast("PFV_CannotFire".Translate());
                return;
            }
            SetTargetContact(contact);
        }

        /// <summary>R / Home：复位到当前焦点的默认机位（带过渡）。</summary>
        internal void ResetCamera()
        {
            DVector3 oldOrigin = ResolveFocusOrigin();
            CameraPose oldPose = CapturePose();

            camYaw = 0f;
            camPitch = PFViewCameraCore.DefaultPitch;
            camFocusOffset = Vector3.zero;
            frozenCameraPose = null;
            if (focusKind == PFViewFocusKind.Frozen) frozenFreeFrameSystem = Quaternion.Inverse(viewFrameRot);
            if (IsThirdPersonView)
            {
                camPitch = PFViewGroupCameraCore.DefaultPitch;
                camDistance = ThirdPersonDistances(force: true).DefaultMeters * MetersToView;
            }
            else camDistance = focusKind == PFViewFocusKind.Group
                ? PFViewCameraCore.DefaultGroupDistanceView(MetersToView)
                : focusKind == PFViewFocusKind.Frozen ? oldPose.Distance : DefaultBodyDistance();

            BeginTransition(oldOrigin, oldPose);
        }

        // ════════════════════════════════════════════════════
        //  焦点解析 / 丢失回退（U3）
        // ════════════════════════════════════════════════════

        /// <summary>当前焦点在绝对系的位置（渲染原点）。丢失 → Frozen + toast（只一次）。</summary>
        internal DVector3 ResolveFocusOrigin(double? tickOverride = null)
        {
            if (!cameraInitialized) RestoreOrInitCamera();

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            double tick = tickOverride ?? RenderTick();

            if (focusKind == PFViewFocusKind.Group)
            {
                if (!CanInspectGroup(focusGroupId) || !PhysicsApi.TryGetSpacecraft(focusGroupId, out var snap))
                {
                    return FreezeFocus();
                }
                focusBodyId = snap.CelestialBodyId;
                lastFocusAbs = GetGroupPosAbs(focusGroupId, tick);
                return lastFocusAbs;
            }

            if (focusKind == PFViewFocusKind.Body)
            {
                if (mgr != null && mgr.GetBodyById(focusBodyId) != null)
                {
                    lastFocusAbs = GetBodyPosAbs(focusBodyId, tick);
                    return lastFocusAbs;
                }

                if (mgr != null && mgr.GetBodyById(currentSystemBodyId) != null)
                {
                    focusBodyId = currentSystemBodyId;
                    lastFocusAbs = GetBodyPosAbs(focusBodyId, tick);
                    return lastFocusAbs;
                }
                return lastFocusAbs;
            }

            return frozenAbs;
        }

        private DVector3 FreezeFocus()
        {
            if (focusKind == PFViewFocusKind.Frozen) return frozenAbs;

            int lostId = focusGroupId;
            focusKind = PFViewFocusKind.Frozen;
            focusGroupId = -1;
            frozenAbs = lastFocusAbs;
            LockFrozenCamera();

            if (selectedGroupId == lostId)
            {
                selectedGroupId = -1;
                targetGroupId = -1;
            }
            if (targetGroupId == lostId) targetGroupId = -1;

            QueueToast($"焦点编队 G{lostId} 已失去信号——视角已冻结（Home 回行星）");
            Log.Message($"[PFView·焦点] 编队 G{lostId} 丢失 → Frozen，相机停在最后位置");
            return frozenAbs;
        }

        /// <summary>参考系/天体集归属用的 bodyId（Group 用编队所在天体；Frozen 保留最后天体）。</summary>
        internal int ResolveFocusBodyId()
        {
            if (focusKind == PFViewFocusKind.Group)
            {
                if (PhysicsApi.TryGetSpacecraft(focusGroupId, out var snap) && snap.CelestialBodyId >= 0)
                    focusBodyId = snap.CelestialBodyId;
            }
            // 旧组/深空组 CelestialBodyId=-1，或 id 已失效（读档/换星系）⇒ 回退当前系统天体。
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (focusBodyId < 0 || (mgr != null && mgr.GetBodyById(focusBodyId) == null))
                focusBodyId = currentSystemBodyId;
            return focusBodyId;
        }

        /// <summary>当前焦点编队 id（焦点不是编队时 -1）。</summary>
        internal int GetFocusGroupId()
            => focusKind == PFViewFocusKind.Group ? focusGroupId : -1;

        private float FocusBodyRadiusView()
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var def = mgr?.GetBodyById(ResolveFocusBodyId());
            if (def == null) return 300000f; // 兜底：地球半径量级（500 km 半径 = 5e5 视图单位的一半）
            return Mathf.Max((float)(def.radius * MetersToView), 1f);
        }

        private float DefaultBodyDistance()
        {
            float aspect = renderCamera != null ? renderCamera.aspect : 16f / 9f;
            float fov = renderCamera != null ? renderCamera.fieldOfView : 45f;
            return PFViewCameraCore.DefaultBodyDistanceView(FocusBodyRadiusView(), fov, aspect);
        }

        // ════════════════════════════════════════════════════
        //  过渡（U13 地基；P0 先接好，时长可调）
        // ════════════════════════════════════════════════════

        private void BeginTransition(DVector3 oldOrigin, CameraPose oldPose)
        {
            orbitDrag.Reset();
            if (renderCamera == null)
            {
                cameraTransitioning = false;
                return;
            }

            DVector3 newOrigin = ResolveFocusOrigin();
            transitionStartLookAbs = hasDisplayedCameraPose ? displayedLookAbs : ViewToAbs(oldPose.Pan, oldOrigin);
            transitionStartRotationSystem = hasDisplayedCameraPose ? displayedOrbitRotationSystem
                : Quaternion.Inverse(viewFrameRot) * PFViewCameraCore.OrbitRotation(oldPose.Yaw, oldPose.Pitch, oldPose.Frame);
            transitionStartDistance = hasDisplayedCameraPose ? camDisplayDistance : oldPose.Distance;
            transitionElapsed = 0f;

            float ratio = Mathf.Max(camDistance, 1f) / Mathf.Max(transitionStartDistance, 1f);
            transitionDuration = Mathf.Clamp(0.25f + Mathf.Abs(Mathf.Log(ratio)) * 0.03f, 0.20f, 0.45f);
            cameraTransitioning = true;
        }

        private void CancelTransition()
        {
            ReleaseFrozenCamera();
            if (cameraTransitioning) camDistance = camDisplayDistance;
            cameraTransitioning = false;
        }

        // ════════════════════════════════════════════════════
        //  每帧取景（替代 Render.cs 的旧 UpdateCamera）
        // ════════════════════════════════════════════════════

        private void UpdateCamera(CelestialBodyManager bodyMgr, double tick, DVector3 origin)
        {
            if (renderCamera == null) return;
            if (!cameraInitialized) RestoreOrInitCamera();

            RecreateRenderTexture();

            float bodyRadiusView = FocusBodyRadiusView();

            var draggedLook = orbitDrag.Advance(camYaw, camPitch, Time.unscaledDeltaTime);
            camYaw = draggedLook.Yaw;
            camPitch = draggedLook.Pitch;

            if (IsThirdPersonView)
            {
                AdvancePilotLook();
                camYaw = PFViewPilotCameraCore.LookYaw(camYaw);
                camPitch = PFViewCameraCore.ResolvePitch(camPitch);
                camDistance = ThirdPersonDistances().ClampMeters(camDistance * ViewToMeters) * MetersToView;
                camFocusOffset = Vector3.zero;
            }
            else if (frozenCameraPose == null)
            {
                camPitch = PFViewCameraCore.ResolvePitch(camPitch);
                camDistance = PFViewCameraCore.ClampDistance(focusKind, camDistance, MetersToView, bodyRadiusView);
                if (focusKind == PFViewFocusKind.Group)
                    camFocusOffset = PFViewCameraLimits.ClampPanOffset(camFocusOffset, MetersToView, true);
            }

            Quaternion orbitFrame = ResolveOrbitFrame();
            // RenderScene 只在 Repaint 且每帧一次调用；暂停/时间倍率不影响镜头缩放。
            float zoomDistance = PFViewCameraCore.SmoothZoomDistance(camDisplayDistance, camDistance, Time.unscaledDeltaTime);
            Vector3 targetPos = camFocusOffset + orbitFrame * PFViewCameraCore.CameraViewPos(Vector3.zero, camYaw, camPitch, zoomDistance);
            Vector3 targetLook = camFocusOffset;

            Vector3 pos;
            Vector3 look;
            Vector3 up;
            if (focusKind == PFViewFocusKind.Frozen && frozenCameraPose != null)
            {
                pos = AbsToView(frozenCameraPose.Position, origin);
                look = AbsToView(frozenCameraPose.Look, origin);
                up = viewFrameRot * frozenCameraPose.RotationSystem * Vector3.up;
                cameraTransitioning = false;
            }
            else if (cameraTransitioning)
            {
                transitionElapsed += Time.unscaledDeltaTime;
                float s = PFViewCameraCore.SmoothStep01(transitionElapsed / Mathf.Max(transitionDuration, 0.0001f));
                look = Vector3.Lerp(AbsToView(transitionStartLookAbs, origin), targetLook, s);
                var orbit = PFViewCameraCore.InterpolateOrbit(viewFrameRot * transitionStartRotationSystem, transitionStartDistance,
                    PFViewCameraCore.OrbitRotation(camYaw, camPitch, orbitFrame), camDistance, s);
                pos = look + orbit.Offset;
                up = orbit.Up;
                if (s >= 1f) cameraTransitioning = false;
            }
            else
            {
                pos = targetPos;
                look = targetLook;
                // 天体也走上方向的连续轨道解：pitch 越过 ±90° 时不再用固定 world-up（会翻转），
                // 因而可以连续绕到行星/舰船下方观察（2026-11-20）。
                up = orbitFrame * PFViewCameraCore.OrbitUp(camYaw, camPitch);
            }

            // 相机不许钻进**任何**天体（2026-10-18 用户：缩放/转视角会进入星体内部、行星局部透明）。
            // 不只看焦点天体：焦点组的 CelestialBodyId 可能失效/回退，相机仍可能贴着另一颗星。
            float minSurfaceGapView = float.MaxValue;
            if (IsThirdPersonView)
                pos = PFViewCameraCore.ClampCameraOutsideSphere(pos, Vector3.zero, hullRadiusMeters * MetersToView + NearClip * 2f, 1f);
            if (bodyMgr != null)
            {
                foreach (var b in bodyMgr.GetAllBodies())
                {
                    float r = (float)(b.radius * MetersToView);
                    if (r <= 0f) continue;
                    Vector3 c = AbsToView(GetBodyPosAbs(b.bodyId, tick), origin);
                    pos = PFViewCameraCore.ClampCameraOutsideSphere(pos, c, r);
                    float gap = (pos - c).magnitude - r;
                    if (gap < minSurfaceGapView) minSurfaceGapView = gap;
                }
            }

            camDisplayDistance = Vector3.Distance(pos, look);

            renderCamera.transform.position = pos;
            renderCamera.transform.LookAt(look, up);
            CacheDisplayedCamera(origin, pos, look);
            renderCamera.farClipPlane = FarClip;

            // 近裁随**实际视距/离最近表面余量**自适应：near 必须小于「相机→任何天体表面」的距离，
            // 否则星球近侧会被近裁面切掉（露出星空 = 用户看到的「局部半透明」）。
            float surfaceGapView = minSurfaceGapView < float.MaxValue ? minSurfaceGapView : camDisplayDistance;
            if (IsThirdPersonView)
                surfaceGapView = Mathf.Min(surfaceGapView, Mathf.Max(NearClip * 2f, pos.magnitude - hullRadiusMeters * MetersToView));
            renderCamera.nearClipPlane = PFViewCameraCore.NearClipForCamera(camDisplayDistance, surfaceGapView, NearClip, 5000f);

            // 记录焦点位置，供丢失冻结（Resolve 内已更新；这里兜底）
            if (focusKind != PFViewFocusKind.Frozen) lastFocusAbs = origin;
        }

        /// <summary>相机在 view space 的位置（供 Scene 的剔除/大气读数）。</summary>
        private Vector3 GetCameraViewPos()
        {
            if (renderCamera != null) return renderCamera.transform.position;
            return PFViewCameraCore.CameraViewPos(camFocusOffset, camYaw, camPitch, camDistance);
        }

        // ════════════════════════════════════════════════════
        //  输入（U7/U8；从 Render.cs 搬来并加窗口归属/限幅）
        // ════════════════════════════════════════════════════


        internal static bool TryHandlePauseShortcut(Event e)
        {
            if (!KeyBindingDefOf.TogglePause.KeyDownEvent || !SteeringInputHost.AcceptNativeTimeKey(e))
                return false;
            Find.TickManager.TogglePaused();
            e.Use();
            return true;
        }

        private void HandleInput(Rect inRect)
        {
            var e = Event.current;
            if (e == null) return;
            if (!Application.isFocused) { ResetCameraDrag(); return; }
            if (EndCameraDrag(e, inRect)) return;

            // 键盘只在「最上层窗口就是本窗口」时处理，避免改名/搜索窗里按 R 复位相机
            if (e.type == EventType.KeyDown && HasInputFocus())
            {
                if (KeyBindingDefOf.Cancel.KeyDownEvent)
                {
                    if (GUI.GetNameOfFocusedControl() == "PFViewSearch") GUI.FocusControl(null);
                    else if (SteeringController.IsOpen) EndSteering();
                    else if (rInspector.width > 0 || uiLayout.Drawer.Width > 0) SetDrawer(PFViewDrawerTab.None);
                    else Close();
                    e.Use();
                    return;
                }
                if (GUI.GetNameOfFocusedControl() == "PFViewSearch") return;
                if (SteeringInputHost.HandleGroupShortcut(e)) return;
                if (HandlePilotCameraShortcut(e)) return;
                if (e.type == EventType.Used || TryHandlePauseShortcut(e)) return;
            }

            // 已在视口内开始的拖拽持续拥有输入，跨过本窗口的侧栏仍逐次更新鼠标位置。
            if (HandleCameraDrag(e)) return;

            bool inViewport = MouseInViewport(inRect);
            if (!inViewport) return;

            if (e.type == EventType.ScrollWheel)
            {
                CancelTransition();
                camDistance = PFViewCameraCore.WheelTargetDistance(camDistance, camDisplayDistance,
                    e.delta.y * (pilotCameraOptions.InvertWheel ? -1 : 1));
                if (IsThirdPersonView)
                    camDistance = ThirdPersonDistances().ClampMeters(camDistance * ViewToMeters) * MetersToView;
                else
                {
                    if (focusKind == PFViewFocusKind.Group
                        && (camDistance < PFViewCameraLimits.GroupOrbitMinMeters * MetersToView
                            || camDistance > PFViewCameraLimits.GroupOrbitMaxMeters * MetersToView)) NotifyViewLimit();
                    // 每个事件即限幅，避免同帧多次输入在边界之外积累目标距离。
                    camDistance = PFViewCameraCore.ClampDistance(focusKind, camDistance, MetersToView, FocusBodyRadiusView());
                }
                e.Use();
            }

            if (e.type == EventType.MouseDown && (e.button == 1 || e.button == 2))
            {
                if (IsThirdPersonView && (e.button == 2 || e.shift)) { e.Use(); return; }
                CancelTransition();
                pilotLookReturning = false;
                orbitDrag.Reset();
                dragState.Begin(e.button == 2 || e.shift);
                rightMouseDownPos = e.mousePosition;
                lastMousePos = e.mousePosition;
                e.Use();
            }
        }

        private bool HandleCameraDrag(Event e)
        {
            if (!dragState.IsActive || e.rawType != EventType.MouseDrag) return false;
            var top = Find.WindowStack?.GetWindowAt(e.mousePosition);
            if (top != null && top != this)
            {
                ResetCameraDrag();
                return false;
            }

            if (isDraggingRight && e.button == 1)
            {
                if ((e.mousePosition - rightMouseDownPos).magnitude > 3f) dragState.MarkMoved();
                Vector2 d = e.mousePosition - lastMousePos;
                var delta = PFViewPilotCameraCore.LookDrag(0, 0, d.x, d.y,
                    OrbitSensitivity * pilotCameraOptions.Sensitivity, pilotCameraOptions.InvertMouseY);
                orbitDrag.AddDrag(camYaw, camPitch, delta.Yaw, delta.Pitch);
                lastMousePos = e.mousePosition;
                if (e.type != EventType.Used) e.Use();
                return true;
            }

            if (!IsThirdPersonView && isDraggingPan && (e.button == 1 || e.button == 2))
            {
                Vector2 d = e.mousePosition - lastMousePos;
                if (renderCamera != null)
                {
                    float worldPerPx = 2f * camDisplayDistance * Mathf.Tan(renderCamera.fieldOfView * 0.5f * Mathf.Deg2Rad)
                                       / Mathf.Max(1f, renderCamera.pixelHeight);
                    camFocusOffset += PFViewPilotCameraCore.PanDelta(renderCamera.transform.right,
                        renderCamera.transform.up, d.x, d.y, worldPerPx);
                    if (focusKind == PFViewFocusKind.Group && camFocusOffset.magnitude > PFViewCameraLimits.GroupPanMaxMeters * MetersToView)
                        NotifyViewLimit();
                }
                lastMousePos = e.mousePosition;
                if (e.type != EventType.Used) e.Use();
                return true;
            }
            return false;
        }

        /// <summary>最上层窗口是否就是本窗口（键盘归属）。</summary>
        private bool EndCameraDrag(Event e, Rect inRect)
        {
            if (e.rawType != EventType.MouseUp || (e.button != 1 && e.button != 2)) return false;
            bool active = isDraggingRight || isDraggingPan;
            bool moved = dragState.Moved;
            bool click = dragState.End();
            bool menu = active && e.button == 1 && click;
            bool recenter = active && moved && e.button == 1 && IsThirdPersonView && pilotCameraOptions.Recenter;
            ResetCameraDrag(settleRotation: active && moved && !recenter);
            if (recenter) pilotLookReturning = true;
            if (!active) return false;
            if (menu && MouseInViewport(inRect)) ShowContextMenu(e.mousePosition, inRect);
            if (e.type != EventType.Used) e.Use();
            return true;
        }

        private void ResetCameraDrag(bool settleRotation = false)
        {
            dragState.Reset();
            if (!settleRotation) orbitDrag.Reset();
            rightMouseDownPos = Vector2.zero;
            lastMousePos = Vector2.zero;
        }

        private bool HasInputFocus()
        {
            var stack = Find.WindowStack;
            if (stack == null) return false;
            var foreground = SteeringInputHost.KeyboardForeground(stack.Windows);
            return ReferenceEquals(foreground, this)
                || (HasTextInputFocus && foreground is Window_Steering);
        }
    }
}
