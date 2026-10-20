using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
        private PFViewPilotCameraOptions pilotCameraOptions = PFViewPilotCameraOptions.Default;
        private Quaternion stableHeadingSystem = Quaternion.identity;
        private Quaternion displayedFollowSystem = Quaternion.identity;
        private bool hasPilotFollowFrame;
        private int pilotFollowGroup = -1;
        private PFViewGroupCameraMode pilotFollowMode;
        private int pilotFollowFrame = -1;
        private bool pilotLookReturning;

        internal bool HandlePilotCameraShortcut(Event e)
        {
            if (!SteeringInputHost.TryShortcut(e, false, out var action)) return false;
            if (action == SteeringAction.ResetCamera) ResetCamera();
            else if (action == SteeringAction.FocusControl && SteeringController.IsOpen)
                FocusGroup(SteeringController.GroupId);
            return true;
        }

        private static PFViewPilotCameraOptions ReadPilotCameraOptions()
        {
            var settings = PioneerFrameworkLiteMod.Settings;
            if (settings == null) return PFViewPilotCameraOptions.Default;
            settings.NormalizePilotSettings();
            return new(settings.pfviewFollowRoll, settings.pfviewInvertMouseY, settings.pfviewInvertWheel,
                settings.pfviewRecenter, settings.pfviewSensitivity, settings.pfviewFollowSmoothing);
        }

        private bool RefreshPilotCameraOptions()
        {
            var next = ReadPilotCameraOptions();
            if (next == pilotCameraOptions) return false;
            pilotCameraOptions = next;
            hasPilotFollowFrame = false;
            pilotFollowFrame = -1;
            pilotLookReturning = false;
            return true;
        }

        private void ResetPilotCamera()
        {
            pilotCameraOptions = PFViewPilotCameraOptions.Default;
            stableHeadingSystem = displayedFollowSystem = Quaternion.identity;
            hasPilotFollowFrame = false;
            pilotFollowGroup = -1;
            pilotFollowFrame = -1;
            pilotLookReturning = false;
        }

        private Quaternion ResolvePilotFollowFrame(SpaceEntityGroup group, Quaternion rawOrbitInView)
        {
            var halfTurn = new Quaternion(0, 1, 0, 0);
            Quaternion rawHeading = Quaternion.Inverse(viewFrameRot) * rawOrbitInView * halfTurn;
            Vector3 forward = rawHeading * Vector3.forward;
            if (!hasPilotFollowFrame || pilotFollowGroup != group.GroupId || pilotFollowMode != groupCameraMode)
            {
                var seed = hasDisplayedCameraPose ? displayedOrbitRotationSystem * halfTurn : Quaternion.identity;
                stableHeadingSystem = PFViewPilotCameraCore.TransportHeading(seed, forward);
                displayedFollowSystem = pilotCameraOptions.FollowRoll ? rawHeading : stableHeadingSystem;
                hasPilotFollowFrame = true;
                pilotFollowGroup = group.GroupId;
                pilotFollowMode = groupCameraMode;
                pilotFollowFrame = -1;
            }
            if (pilotFollowFrame != RealTime.frameCount)
            {
                stableHeadingSystem = PFViewPilotCameraCore.TransportHeading(stableHeadingSystem, forward);
                var target = pilotCameraOptions.FollowRoll ? rawHeading : stableHeadingSystem;
                displayedFollowSystem = PFViewPilotCameraCore.SmoothFollow(displayedFollowSystem, target,
                    Time.unscaledDeltaTime, pilotCameraOptions.Smoothing);
                pilotFollowFrame = RealTime.frameCount;
            }
            return PFViewCameraCore.GroupOrbitFrame(viewFrameRot * displayedFollowSystem);
        }

        private void AdvancePilotLook()
        {
            if (!IsThirdPersonView || !pilotLookReturning || isDraggingRight) return;
            var look = PFViewPilotCameraCore.RecenterLook(camYaw, camPitch, Time.unscaledDeltaTime);
            camYaw = look.Yaw;
            camPitch = look.Pitch;
            if (camYaw == 0 && camPitch == PFViewGroupCameraCore.DefaultPitch) pilotLookReturning = false;
        }
    }
}
