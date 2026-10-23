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
                var group = GetFocusGroup();
                if (CanInspectEntity(group))
                {
                    // 船体正向：船头为前向；速度正向：把同一船体帧的最小旋转搬到速度方向（见 PFViewCameraCore）。
                    Quaternion hullInView = viewFrameRot * group.CurrentOrbit.rotation.ToQuaternion();
                    if (groupCameraMode == PFViewGroupCameraMode.ThirdPersonVelocity)
                    {
                        Vector3 velocityInView = viewFrameRot * (Vector3)group.CurrentOrbit.velocitySystem.v;
                        lastThirdPersonFrame = PFViewCameraCore.GroupVelocityOrbitFrame(hullInView, velocityInView);
                    }
                    else lastThirdPersonFrame = PFViewCameraCore.GroupOrbitFrame(hullInView);
                    lastThirdPersonFrame = ResolvePilotFollowFrame(group, lastThirdPersonFrame);
                }
                return lastThirdPersonFrame;
            }
            if (IsStarFocus) return SunFocusOrbitFrame();
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

        private void InitializeGroupCameraPose(SpaceEntityGroup group, PFViewFocusKind oldKind, CameraPose? oldPose)
        {
            camFocusOffset = Vector3.zero;
            frozenCameraPose = null;
            if (PFViewGroupCameraCore.IsThirdPerson(groupCameraMode))
            {
                camYaw = 0;
                camPitch = PFViewGroupCameraCore.DefaultPitch;
                camDistance = ThirdPersonDistances(group, true).DefaultMeters * MetersToView;
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
            var group = GetFocusGroup();
            if (!CanInspectEntity(group) || mode == groupCameraMode) return;
            RememberPreviousView();
            SaveCameraMemory();
            DVector3 oldOrigin = ResolveFocusOrigin();
            CameraPose oldPose = CapturePose();
            groupCameraMode = mode;
            ResetCameraDrag();
            if (!TryLoadPose()) InitializeGroupCameraPose(group, PFViewFocusKind.Group, oldPose);
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

        private PFViewGroupCameraDistances ThirdPersonDistances(SpaceEntityGroup? group = null, bool force = false)
        {
            group ??= GetFocusGroup();
            if (CanInspectEntity(group) && (force || hullGroupId != group.GroupId || Time.realtimeSinceStartup >= nextHullRefresh))
            {
                hullGroupId = group.GroupId;
                nextHullRefresh = Time.realtimeSinceStartup + .25f;
                hullRadiusMeters = MeasureRenderedGroupRadius(group);
            }
            float fov = renderCamera?.fieldOfView ?? 45f;
            float aspect = renderCamera?.aspect ?? 16f / 9f;
            return PFViewGroupCameraCore.Distances(hullRadiusMeters, fov, aspect);
        }

        private float MeasureRenderedGroupRadius(SpaceEntityGroup group)
        {
            float radius = 0;
            var space = Current.Game?.GetComponent<SpaceGameComp>();
            foreach (int id in group.GetEntityIds())
            {
                var entity = space?.GetEntityById(id);
                if (entity == null) continue;
                float entityRadius = 0;
                bool relief = false;
                if (debugMapRelief && entity.MapId >= 0 && !entity.IsMapArchived)
                {
                    var map = SpaceMapGenerator.GetEntityMap(entity);
                    var comp = map?.GetComponent<SpaceMapComp>();
                    byte[]? classes = comp?.GetReliefClasses();
                    if (map != null && comp != null && classes != null && comp.ReliefHasContent)
                    {
                        var set = GetOrBuildReliefMeshes(map, comp, classes);
                        foreach (var mesh in set.Meshes)
                        {
                            if (mesh == null) continue;
                            Bounds bounds = mesh.bounds;
                            entityRadius = Mathf.Max(entityRadius, bounds.center.magnitude + bounds.extents.magnitude);
                            relief = true;
                        }
                    }
                }
                if (!relief)
                {
                    foreach (var part in entity.Parts)
                    {
                        if (part.isDestroyed) continue;
                        Vector3 size = (Vector3)part.boundsSizeShip;
                        size = new(Mathf.Max(size.x, 1), Mathf.Max(size.y, 1), Mathf.Max(size.z, 1));
                        entityRadius = Mathf.Max(entityRadius, (float)part.positionShip.magnitude + size.magnitude * .5f);
                    }
                    if (entityRadius <= 0) entityRadius = (float)entity.AabbHalfSize.magnitude;
                }
                float scale = DebugScaleParts ? 200f : 1f;
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
