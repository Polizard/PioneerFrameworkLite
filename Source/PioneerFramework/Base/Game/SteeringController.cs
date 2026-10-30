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
    public static partial class SteeringController
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
        // Effective precision state; toggle mode can remain active after the key is released.
        internal static bool PrecisionHeld;
        internal static bool PrecisionToggle { get; private set; }
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
            PrecisionToggle = settings?.pfviewPrecisionToggle ?? false;
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
            SteeringInputHost.ResetPrecision();
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

        private static SteeringStepMath CurrentStepMath => new(KeyForward, KeyBack, KeyLeft, KeyRight, KeyUp, KeyDown,
            KeyRollL, KeyRollR, KeyYawL, KeyYawR, KeyPitchU, KeyPitchD, PrecisionHeld, AssistedStop, InvertPitch, PrecisionScale);

        /// <summary>平移键合成世界方向（多键叠加）。</summary>
        public static DVector3 GetTranslationWorldDir(SpaceEntityGroup g) => CurrentStepMath.GetTranslationWorldDir(g);

        /// <summary>旋转键转为世界系角速度；逐轴惯量限速和运算次序由共享值输入运算保持。</summary>
        public static DVector3 GetRotationWorldAngularVelocity(SpaceEntityGroup g) => CurrentStepMath.GetRotationWorldAngularVelocity(g);

        internal static void ApplyManualRotation(SpaceEntityGroup group, double dtSeconds)
            => CurrentStepMath.ApplyManualRotation(group, dtSeconds, ref SasKind, ref TakeoverNotice, ref manualTurnStarted);

        internal static void ScaleManualThrust(ref float force, ref float massFlow) => CurrentStepMath.ScaleManualThrust(ref force, ref massFlow);

        /// <summary>SAS 目标世界方向（主推进轴对准目标；None 返回零）。</summary>
        public static DVector3 GetSasTarget(SpaceEntityGroup g) => CurrentStepMath.GetSasTarget(g, SasKind);
    }
}
