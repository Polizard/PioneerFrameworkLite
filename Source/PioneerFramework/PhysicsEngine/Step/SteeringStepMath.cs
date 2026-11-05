namespace PioneerFramework
{
    /// <summary>
    /// 实时操舵和物理步快照共用的值输入运算。只读取显式捕获的键位和设置，避免准备期间读取实时输入。
    /// 保留原逐轴累加、单位角指令、惯量限速及精确模式的运算次序；值类型不增加每次调用的堆分配。
    /// </summary>
    internal readonly struct SteeringStepMath
    {
        private readonly bool KeyForward, KeyBack, KeyLeft, KeyRight, KeyUp, KeyDown;
        private readonly bool KeyRollL, KeyRollR, KeyYawL, KeyYawR, KeyPitchU, KeyPitchD;
        private readonly bool PrecisionHeld, AssistedStop, InvertPitch;
        private readonly float PrecisionScale;

        internal SteeringStepMath(bool forward, bool back, bool left, bool right, bool up, bool down,
            bool rollL, bool rollR, bool yawL, bool yawR, bool pitchU, bool pitchD,
            bool precisionHeld, bool assistedStop, bool invertPitch, float precisionScale)
        {
            KeyForward = forward; KeyBack = back; KeyLeft = left; KeyRight = right; KeyUp = up; KeyDown = down;
            KeyRollL = rollL; KeyRollR = rollR; KeyYawL = yawL; KeyYawR = yawR; KeyPitchU = pitchU; KeyPitchD = pitchD;
            PrecisionHeld = precisionHeld; AssistedStop = assistedStop; InvertPitch = invertPitch; PrecisionScale = precisionScale;
        }

        private bool AnyRotation => KeyRollL != KeyRollR || KeyYawL != KeyYawR || KeyPitchU != KeyPitchD;

        internal DVector3 GetTranslationWorldDir(SpaceEntityGroup g)
        {
            DVector3 dir = DVector3.zero;
            if (KeyForward) dir += g.GetThrustAxisSystem(ThrustAxis.Forward);
            if (KeyBack) dir += g.GetThrustAxisSystem(ThrustAxis.Back);
            if (KeyLeft) dir += g.GetThrustAxisSystem(ThrustAxis.Left);
            if (KeyRight) dir += g.GetThrustAxisSystem(ThrustAxis.Right);
            if (KeyUp) dir += g.GetThrustAxisSystem(ThrustAxis.Up);
            if (KeyDown) dir += g.GetThrustAxisSystem(ThrustAxis.Down);
            return dir;
        }

        internal DVector3 GetRotationWorldAngularVelocity(SpaceEntityGroup g)
        {
            double pitch = (KeyPitchU ? 1 : 0) - (KeyPitchD ? 1 : 0);
            if (InvertPitch) pitch = -pitch;
            double yaw = (KeyYawL ? 1 : 0) - (KeyYawR ? 1 : 0);
            double roll = (KeyRollL ? 1 : 0) - (KeyRollR ? 1 : 0);

            // 方向/符号沿用 SteeringMath；每轴再乘各自的 I 加权限速。
            var unit = SteeringMath.AngularCommand(pitch, yaw, roll, 1.0, 1.0);
            var local = new DVector3(
                unit.x * g.MaxAngularRateAboutBodyAxis(1, 0, 0),
                unit.y * g.MaxAngularRateAboutBodyAxis(0, 1, 0),
                unit.z * g.MaxAngularRateAboutBodyAxis(0, 0, 1));
            if (PrecisionHeld) local = local * PrecisionScale;
            return DVector3.Rotate(local, g.CurrentOrbit.rotation);
        }

        internal void ApplyManualRotation(SpaceEntityGroup group, double dtSeconds, ref SasTargetKind sasKind,
            ref bool takeoverNotice, ref bool manualTurnStarted)
        {
            if (AnyRotation)
            {
                if (sasKind != SasTargetKind.None) takeoverNotice = true;
                sasKind = SasTargetKind.None;
                manualTurnStarted = true;
                group.angularVelocity = GetRotationWorldAngularVelocity(group);
            }
            else if (sasKind == SasTargetKind.None && manualTurnStarted)
                group.angularVelocity = SteeringMath.ReleaseSpin(group.angularVelocity, AssistedStop, dtSeconds);
        }

        internal void ScaleManualThrust(ref float force, ref float massFlow)
        {
            float scale = PrecisionHeld ? PrecisionScale : 1f;
            force *= scale;
            massFlow *= scale;
        }

        internal DVector3 GetSasTarget(SpaceEntityGroup g, SasTargetKind sasKind)
        {
            switch (sasKind)
            {
                case SasTargetKind.Prograde:
                    return g.CurrentOrbit.velocitySystem.normalized.v;
                case SasTargetKind.Retrograde:
                    return (-g.CurrentOrbit.velocitySystem.normalized).v;
                case SasTargetKind.Normal:
                case SasTargetKind.Antinormal:
                {
                    DVector3 radial = g.positionBody.normalized;
                    DVector3 h = DVector3.Cross(radial, g.velocityBody).normalized;
                    if (h.sqrMagnitude < 1e-9)
                        h = DVector3.up;
                    return sasKind == SasTargetKind.Normal ? h : -h;
                }
                case SasTargetKind.RadialOut:
                    return g.positionBody.normalized;
                case SasTargetKind.RadialIn:
                    return -g.positionBody.normalized;
                default:
                    return DVector3.zero;
            }
        }
    }
}
