using Verse;
using UnityEngine;

namespace PioneerFramework
{
    public static partial class SteeringController
    {
        internal static StepInput CaptureStepInput() => new() {
                KeyForward = SteeringController.KeyForward,
                KeyBack = SteeringController.KeyBack,
                KeyLeft = SteeringController.KeyLeft,
                KeyRight = SteeringController.KeyRight,
                KeyUp = SteeringController.KeyUp,
                KeyDown = SteeringController.KeyDown,
                KeyRollL = SteeringController.KeyRollL,
                KeyRollR = SteeringController.KeyRollR,
                KeyYawL = SteeringController.KeyYawL,
                KeyYawR = SteeringController.KeyYawR,
                KeyPitchU = SteeringController.KeyPitchU,
                KeyPitchD = SteeringController.KeyPitchD,
                PrecisionHeld = SteeringController.PrecisionHeld,
                AssistedStop = SteeringController.AssistedStop,
                InvertPitch = SteeringController.InvertPitch,
                PrecisionScale = SteeringController.PrecisionScale,
                ActualThrust = SteeringController.ActualThrust,
                ThrustUnavailable = SteeringController.ThrustUnavailable,
                TakeoverNotice = SteeringController.TakeoverNotice,
                manualTurnStarted = SteeringController.manualTurnStarted,
                SasKind = SteeringController.SasKind,
                GroupId = SteeringController.GroupId,
                IsOpen = GroupId >= 0 && ReferenceEquals(sessionGame, Current.Game) };

        internal sealed class StepInput
        {
            internal int GroupId;
            internal bool IsOpen;
            internal bool KeyForward, KeyBack, KeyLeft, KeyRight, KeyUp, KeyDown;
            internal bool KeyRollL, KeyRollR, KeyYawL, KeyYawR, KeyPitchU, KeyPitchD;
            internal bool PrecisionHeld, AssistedStop, InvertPitch, ThrustUnavailable, TakeoverNotice, manualTurnStarted;
            internal float PrecisionScale, ActualThrust;
            internal SasTargetKind SasKind;
            private bool closeRequested;
            internal bool AnyTranslation => KeyForward != KeyBack || KeyLeft != KeyRight || KeyUp != KeyDown;
            internal bool AnyRotation => KeyRollL != KeyRollR || KeyYawL != KeyYawR || KeyPitchU != KeyPitchD;
            internal void Close() { IsOpen = false; closeRequested = true; }
            internal void Publish()
            {
                SteeringController.ActualThrust = ActualThrust;
                SteeringController.ThrustUnavailable = ThrustUnavailable;
                SteeringController.TakeoverNotice = TakeoverNotice;
                SteeringController.SasKind = SasKind;
                SteeringController.manualTurnStarted = manualTurnStarted;
            }
            internal void Complete() { if (closeRequested) SteeringController.Close(); }

            // 物理步继续使用捕获时的输入；共享运算只接收当前副本的值，不读取实时静态状态。
            private SteeringStepMath StepMath => new(KeyForward, KeyBack, KeyLeft, KeyRight, KeyUp, KeyDown,
                KeyRollL, KeyRollR, KeyYawL, KeyYawR, KeyPitchU, KeyPitchD, PrecisionHeld, AssistedStop, InvertPitch, PrecisionScale);

            public DVector3 GetTranslationWorldDir(SpaceEntityGroup g) => StepMath.GetTranslationWorldDir(g);
            public DVector3 GetRotationWorldAngularVelocity(SpaceEntityGroup g) => StepMath.GetRotationWorldAngularVelocity(g);
            internal void ApplyManualRotation(SpaceEntityGroup group, double dtSeconds)
                => StepMath.ApplyManualRotation(group, dtSeconds, ref SasKind, ref TakeoverNotice, ref manualTurnStarted);
            internal void ScaleManualThrust(ref float force, ref float massFlow) => StepMath.ScaleManualThrust(ref force, ref massFlow);
            public DVector3 GetSasTarget(SpaceEntityGroup g) => StepMath.GetSasTarget(g, SasKind);
        }
    }
}
