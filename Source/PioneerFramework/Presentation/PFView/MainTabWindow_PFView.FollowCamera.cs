using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
        private PFViewGroupCameraMode groupCameraMode;
        private static readonly PFViewGroupCameraMemory<CameraPose> groupCameraMemory = new();
        private Quaternion lastThirdPersonFrame = Quaternion.identity;
        private Quaternion frozenFreeFrameSystem = Quaternion.identity;
        private sealed record FrozenCameraPose(DVector3 Position, DVector3 Look, Quaternion RotationSystem);
        private FrozenCameraPose? frozenCameraPose;
        private bool hasDisplayedCameraPose;
        private DVector3 displayedCameraAbs;
        private DVector3 displayedLookAbs;
        private DVector3 displayedOrigin;
        private Quaternion displayedOrbitRotationSystem = Quaternion.identity;
        private int hullGroupId = -1;
        private float hullRadiusMeters = 10f;
        private float nextHullRefresh;

        private bool IsThirdPersonView => focusKind == PFViewFocusKind.Group && PFViewGroupCameraCore.IsThirdPerson(groupCameraMode);

        private static PFViewGroupCameraMode DefaultGroupCameraMode()
            => PFViewGroupCameraCore.DefaultMode(PioneerFrameworkLiteMod.Settings?.pfviewFocusCameraMode);

        private Quaternion ResolveOrbitFrame()
        {
            if (IsThirdPersonView)
            {
                int groupId = GetFocusGroupId();
                if (CanInspectGroup(groupId) && PhysicsApi.TryGetSpacecraft(groupId, out var snap))
                {
                    // 船体正向：船头为前向；速度正向：把同一船体帧的最小旋转搬到速度方向（见 PFViewCameraCore）。
                    Quaternion hullInView = viewFrameRot * snap.Rotation.ToQuaternion();
                    if (groupCameraMode == PFViewGroupCameraMode.ThirdPersonVelocity)
                    {
                        Vector3 velocityInView = viewFrameRot * (Vector3)snap.VelocitySystem;
                        lastThirdPersonFrame = PFViewCameraCore.GroupVelocityOrbitFrame(hullInView, velocityInView);
                    }
                    else lastThirdPersonFrame = PFViewCameraCore.GroupOrbitFrame(hullInView);
                    lastThirdPersonFrame = ResolvePilotFollowFrame(groupId, lastThirdPersonFrame);
                }
                return lastThirdPersonFrame;
            }
            return focusKind == PFViewFocusKind.Frozen ? viewFrameRot * frozenFreeFrameSystem : Quaternion.identity;
        }

        private void ResetCameraSession()
        {
            orbitDrag.Reset();
            cameraTransitioning = false;
            transitionElapsed = 0f;
            transitionStartLookAbs = DVector3.zero;
            transitionStartRotationSystem = Quaternion.identity;
            groupCameraMemory.Clear();
            frozenCameraPose = null;
            frozenFreeFrameSystem = Quaternion.identity;
            hasDisplayedCameraPose = false;
            hullGroupId = -1;
            groupCameraMode = PFViewGroupCameraMode.Free;
            ResetPilotCamera();
        }

        private void ApplyCameraPose(CameraPose pose)
        {
            groupCameraMode = pose.Mode;
            camYaw = pose.Yaw;
            camPitch = pose.Pitch;
            camDistance = pose.Distance;
            camFocusOffset = pose.Pan;
            frozenCameraPose = pose.Frozen;
            frozenFreeFrameSystem = pose.FrozenFrameSystem;
            pilotCameraOptions = pose.PilotOptions;
            stableHeadingSystem = pose.StableHeading;
            displayedFollowSystem = pose.FollowHeading;
            hasPilotFollowFrame = pose.HasPilotFrame;
            pilotFollowGroup = focusGroupId;
            pilotFollowMode = groupCameraMode;
            pilotFollowFrame = -1;
            pilotLookReturning = pose.LookReturning;
        }

        private void InitializeGroupCameraPose(int groupId, PFViewFocusKind oldKind, CameraPose? oldPose)
        {
            camFocusOffset = Vector3.zero;
            frozenCameraPose = null;
            if (PFViewGroupCameraCore.IsThirdPerson(groupCameraMode))
            {
                camYaw = 0;
                camPitch = PFViewGroupCameraCore.DefaultPitch;
                camDistance = ThirdPersonDistances(groupId, true).DefaultMeters * MetersToView;
            }
            else
            {
                if (oldPose?.Mode is { } oldMode && PFViewGroupCameraCore.IsThirdPerson(oldMode))
                {
                    Vector3 direction = oldPose.Frame * PFViewCameraCore.OrbitDir(oldPose.Yaw, oldPose.Pitch);
                    camYaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
                    camPitch = Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg;
                }
                camDistance = oldPose != null
                    ? PFViewCameraCore.ProjectDistance(oldKind, PFViewFocusKind.Group, oldPose.Distance, MetersToView, FocusBodyRadiusView())
                    : PFViewCameraCore.DefaultGroupDistanceView(MetersToView);
            }
        }

        private void SwitchGroupCameraMode(PFViewGroupCameraMode mode)
        {
            int groupId = GetFocusGroupId();
            if (!CanInspectGroup(groupId) || mode == groupCameraMode) return;
            RememberPreviousView();
            SaveCameraMemory();
            DVector3 oldOrigin = ResolveFocusOrigin();
            CameraPose oldPose = CapturePose();
            groupCameraMode = mode;
            ResetCameraDrag();
            if (!TryLoadPose()) InitializeGroupCameraPose(groupId, PFViewFocusKind.Group, oldPose);
            RefreshPilotCameraOptions();
            BeginTransition(oldOrigin, oldPose);
        }

        private void ShowCameraModeMenu()
        {
            var options = new List<FloatMenuOption>();
            foreach (PFViewGroupCameraMode mode in System.Enum.GetValues<PFViewGroupCameraMode>())
            {
                PFViewGroupCameraMode captured = mode;
                options.Add(new((groupCameraMode == mode ? "✓ " : "") + ("PFV_Camera_" + mode).Translate(),
                    () => SwitchGroupCameraMode(captured)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        /// <summary>相机专用的快照缓冲（批 5e §4.3）：与渲染段的 `entityBuf`/`partBuf` 分开，
        /// 避免"相机半径测量"与"场景绘制"同帧交错时互相清空（当前调用图不交错，但分开后不依赖该前提）。</summary>
        private readonly List<EntitySnapshot> camEntityBuf = new();
        private readonly List<PartSnapshot> camPartBuf = new();

        private PFViewGroupCameraDistances ThirdPersonDistances(int groupId = -1, bool force = false)
        {
            if (groupId < 0) groupId = GetFocusGroupId();
            if (CanInspectGroup(groupId) && (force || hullGroupId != groupId || Time.realtimeSinceStartup >= nextHullRefresh))
            {
                hullGroupId = groupId;
                nextHullRefresh = Time.realtimeSinceStartup + .25f;
                hullRadiusMeters = MeasureRenderedGroupRadius(groupId);
            }
            float fov = renderCamera?.fieldOfView ?? 45f;
            float aspect = renderCamera?.aspect ?? 16f / 9f;
            return PFViewGroupCameraCore.Distances(hullRadiusMeters, fov, aspect);
        }

        private float MeasureRenderedGroupRadius(int groupId)
        {
            float radius = 0;
            float scale = DebugScaleParts ? 200f : 1f;
            camEntityBuf.Clear();
            PhysicsApi.CopyEntities(groupId, camEntityBuf);
            for (int ei = 0; ei < camEntityBuf.Count; ei++)
            {
                var entity = camEntityBuf[ei];
                float entityRadius = 0;
                bool relief = false;
                if (debugMapRelief && entity.MapId >= 0 && !entity.IsMapArchived
                    && PhysicsQueries.TryGetMapRelief(entity.MapId, out var map, out var classes, out int reliefVersion))
                {
                    var set = GetOrBuildReliefMeshes(map, classes, reliefVersion);
                    foreach (var mesh in set.Meshes)
                    {
                        if (mesh == null) continue;
                        Bounds bounds = mesh.bounds;
                        entityRadius = Mathf.Max(entityRadius, bounds.center.magnitude + bounds.extents.magnitude);
                        relief = true;
                    }
                }
                if (!relief)
                {
                    camPartBuf.Clear();
                    PhysicsApi.TryCopyEntityParts(entity.EntityId, camPartBuf);
                    for (int pi = 0; pi < camPartBuf.Count; pi++)
                    {
                        var part = camPartBuf[pi];
                        if (part.IsDestroyed) continue;
                        Vector3 size = (Vector3)part.BoundsSizeShip;
                        size = new(Mathf.Max(size.x, 1), Mathf.Max(size.y, 1), Mathf.Max(size.z, 1));
                        entityRadius = Mathf.Max(entityRadius, (float)part.PositionShip.magnitude + size.magnitude * .5f);
                    }
                    if (entityRadius <= 0) entityRadius = (float)entity.AabbHalfSize.magnitude;
                }
                radius = Mathf.Max(radius, (float)entity.Position.magnitude + entityRadius * scale);
            }
            return radius > 0 && float.IsFinite(radius) ? radius : 10f;
        }

        private void CacheDisplayedCamera(DVector3 origin, Vector3 position, Vector3 look)
        {
            displayedOrigin = origin;
            displayedCameraAbs = ViewToAbs(position, origin);
            displayedLookAbs = ViewToAbs(look, origin);
            displayedOrbitRotationSystem = Quaternion.Inverse(viewFrameRot)
                * (renderCamera!.transform.rotation * new Quaternion(0, 1, 0, 0));
            hasDisplayedCameraPose = true;
        }

        private void LockFrozenCamera()
        {
            orbitDrag.Reset();
            if (!hasDisplayedCameraPose) return;
            frozenCameraPose = new(displayedCameraAbs, displayedLookAbs, displayedOrbitRotationSystem);
            frozenFreeFrameSystem = displayedOrbitRotationSystem;
            frozenAbs = displayedOrigin;
            cameraTransitioning = false;
        }

        private void ReleaseFrozenCamera()
        {
            if (frozenCameraPose == null) return;
            orbitDrag.Reset();
            var origin = frozenAbs;
            camFocusOffset = AbsToView(frozenCameraPose.Look, origin);
            camDistance = (float)(frozenCameraPose.Position - frozenCameraPose.Look).magnitude * MetersToView;
            camYaw = camPitch = 0;
            frozenFreeFrameSystem = frozenCameraPose.RotationSystem;
            frozenCameraPose = null;
        }
    }
}
