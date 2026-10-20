using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>SAS 对准目标类型（主推进轴对准的世界方向）。</summary>
    public enum SasTargetKind
    {
        None,
        Prograde,    // 顺向（速度方向）
        Retrograde,  // 逆向
        Normal,      // 法向（轨道面法线）
        Antinormal,  // 反法向
        RadialOut,   // 径向朝外
        RadialIn     // 径向朝内
    }

    /// <summary>
    /// 手动操舵输入状态（由 Window_Steering 写入，SpaceGameComp 每物理步读取应用）。
    /// 单例语义：同一时刻只能操舵一艘船（打开新操舵台窗口即切换目标）。
    /// </summary>
    public static class SteeringController
    {
        /// <summary>当前被操舵的 group；-1 = 未开启。</summary>
        public static int GroupId = -1;
        private static Game? sessionGame;
        public static bool IsOpen
        {
            get
            {
                if (GroupId < 0) return false;
                if (ReferenceEquals(sessionGame, Current.Game)) return true;
                // Group ids can be reused in another save. Never resolve the old id in the new game.
                Close();
                return false;
            }
        }

        // 按键映射由统一输入适配器读取；这里保留船体系动作状态。
        public static bool KeyForward, KeyBack, KeyLeft, KeyRight, KeyUp, KeyDown;
        // 旋转键（动量轮）
        public static bool KeyRollL, KeyRollR, KeyYawL, KeyYawR, KeyPitchU, KeyPitchD;

        /// <summary>SAS 目标（None = 关闭）。</summary>
        public static SasTargetKind SasKind;

        public static bool AnyTranslation => KeyForward != KeyBack || KeyLeft != KeyRight || KeyUp != KeyDown;
        public static bool AnyRotation => KeyRollL != KeyRollR || KeyYawL != KeyYawR || KeyPitchU != KeyPitchD;
        internal static bool PrecisionHeld;
        internal static bool FromPFView { get; private set; }
        internal static bool AssistedStop { get; private set; } = true;
        internal static bool InvertPitch { get; private set; }
        internal static float PrecisionScale { get; private set; } = .25f;
        internal static float ActualThrust;
        internal static bool ThrustUnavailable;
        internal static bool TakeoverNotice;
        private static bool manualTurnStarted;

        public static void Open(int groupId) => Open(groupId, false);

        internal static void Open(int groupId, bool fromPFView)
        {
            sessionGame = Current.Game;
            GroupId = groupId;
            ResetInput();
            FromPFView = fromPFView;
            var settings = PioneerFrameworkLiteMod.Settings;
            settings?.NormalizePilotSettings();
            AssistedStop = settings?.pfviewAssistStop ?? true;
            InvertPitch = settings?.pfviewInvertPitch ?? false;
            PrecisionScale = settings?.pfviewPrecisionScale ?? .25f;
            SteeringInputHost.Rearm();
        }

        public static void Close()
        {
            GroupId = -1;
            sessionGame = null;
            ResetInput();
            FromPFView = false;
            SteeringInputHost.Rearm();
        }

        private static void ResetInput()
        {
            ClearHeldInput();
            SasKind = SasTargetKind.None;
            manualTurnStarted = false;
            ActualThrust = 0;
            ThrustUnavailable = TakeoverNotice = false;
        }

        internal static void ClearHeldInput()
        {
            KeyForward = KeyBack = KeyLeft = KeyRight = KeyUp = KeyDown = false;
            KeyRollL = KeyRollR = KeyYawL = KeyYawR = KeyPitchU = KeyPitchD = false;
            PrecisionHeld = false;
        }

        internal static void HoldPointerAction(SteeringAction action)
        {
            switch (action)
            {
                case SteeringAction.PitchUp: KeyPitchU = true; break;
                case SteeringAction.PitchDown: KeyPitchD = true; break;
                case SteeringAction.YawLeft: KeyYawL = true; break;
                case SteeringAction.YawRight: KeyYawR = true; break;
                case SteeringAction.RollLeft: KeyRollL = true; break;
                case SteeringAction.RollRight: KeyRollR = true; break;
                case SteeringAction.Forward: KeyForward = true; break;
                case SteeringAction.Back: KeyBack = true; break;
                case SteeringAction.Left: KeyLeft = true; break;
                case SteeringAction.Right: KeyRight = true; break;
                case SteeringAction.Up: KeyUp = true; break;
                case SteeringAction.Down: KeyDown = true; break;
            }
        }

        /// <summary>平移键合成世界方向（多键叠加）。</summary>
        public static DVector3 GetTranslationWorldDir(SpaceEntityGroup g)
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

        /// <summary>
        /// 旋转键 → 世界系角速度（船体局部轴经 rotation 旋转）。
        /// 船头 +Z、船上 +Y：抬头为 -X，左偏航为 -Y，左滚转为 +Z。
        /// </summary>
        public static DVector3 GetRotationWorldAngularVelocity(SpaceEntityGroup g)
        {
            double pitch = (KeyPitchU ? 1 : 0) - (KeyPitchD ? 1 : 0);
            if (InvertPitch) pitch = -pitch;
            double yaw = (KeyYawL ? 1 : 0) - (KeyYawR ? 1 : 0);
            double roll = (KeyRollL ? 1 : 0) - (KeyRollR ? 1 : 0);

            // 方向/符号沿用远端 SteeringMath；每轴再乘各自的 I 加权限速（批 1）。
            var unit = SteeringMath.AngularCommand(pitch, yaw, roll, 1.0, 1.0);
            var local = new DVector3(
                unit.x * g.MaxAngularRateAboutBodyAxis(1, 0, 0),
                unit.y * g.MaxAngularRateAboutBodyAxis(0, 1, 0),
                unit.z * g.MaxAngularRateAboutBodyAxis(0, 0, 1));
            if (PrecisionHeld) local = local * PrecisionScale;
            return DVector3.Rotate(local, g.CurrentOrbit.rotation);
        }

        internal static void ApplyManualRotation(SpaceEntityGroup group, double dtSeconds)
        {
            if (AnyRotation)
            {
                if (SasKind != SasTargetKind.None) TakeoverNotice = true;
                SasKind = SasTargetKind.None;
                manualTurnStarted = true;
                group.angularVelocity = GetRotationWorldAngularVelocity(group);
            }
            else if (SasKind == SasTargetKind.None && manualTurnStarted)
                group.angularVelocity = SteeringMath.ReleaseSpin(group.angularVelocity, AssistedStop, dtSeconds);
        }

        internal static void ScaleManualThrust(ref float force, ref float massFlow)
        {
            float scale = PrecisionHeld ? PrecisionScale : 1f;
            force *= scale;
            massFlow *= scale;
        }

        /// <summary>SAS 目标世界方向（主推进轴对准目标；None 返回零）。</summary>
        public static DVector3 GetSasTarget(SpaceEntityGroup g)
        {
            switch (SasKind)
            {
                case SasTargetKind.Prograde:
                    return g.CurrentOrbit.velocitySystem.normalized.v;   // 批6 S3-3c：读带系字段（旧名是过渡桥）
                case SasTargetKind.Retrograde:
                    return (-g.CurrentOrbit.velocitySystem.normalized).v;
                case SasTargetKind.Normal:
                case SasTargetKind.Antinormal:
                {
                    DVector3 radial = g.positionBody.normalized;
                    DVector3 h = DVector3.Cross(radial, g.velocityBody).normalized;
                    if (h.sqrMagnitude < 1e-9)
                        h = DVector3.up;
                    return SasKind == SasTargetKind.Normal ? h : -h;
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
