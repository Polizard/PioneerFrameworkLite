using UnityEngine;

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
        public static bool IsOpen => GroupId >= 0;

        // 平移键（W/S/A/D + R/F，船体轴世界方向）
        public static bool KeyForward, KeyBack, KeyLeft, KeyRight, KeyUp, KeyDown;
        // 旋转键（动量轮）
        public static bool KeyRollL, KeyRollR, KeyYawL, KeyYawR, KeyPitchU, KeyPitchD;

        /// <summary>SAS 目标（None = 关闭）。</summary>
        public static SasTargetKind SasKind;

        public static bool AnyTranslation => KeyForward || KeyBack || KeyLeft || KeyRight || KeyUp || KeyDown;
        public static bool AnyRotation => KeyRollL || KeyRollR || KeyYawL || KeyYawR || KeyPitchU || KeyPitchD;

        public static void Open(int groupId)
        {
            GroupId = groupId;
            ResetInput();
        }

        public static void Close()
        {
            GroupId = -1;
            ResetInput();
        }

        private static void ResetInput()
        {
            KeyForward = KeyBack = KeyLeft = KeyRight = KeyUp = KeyDown = false;
            KeyRollL = KeyRollR = KeyYawL = KeyYawR = KeyPitchU = KeyPitchD = false;
            SasKind = SasTargetKind.None;
        }

        /// <summary>平移键合成世界方向（多键叠加）。</summary>
        public static DVector3 GetTranslationWorldDir(SpaceEntityGroup g)
        {
            DVector3 dir = DVector3.zero;
            if (KeyForward) dir += g.GetWorldAxis(ThrustAxis.Forward);
            if (KeyBack) dir += g.GetWorldAxis(ThrustAxis.Back);
            if (KeyLeft) dir += g.GetWorldAxis(ThrustAxis.Left);
            if (KeyRight) dir += g.GetWorldAxis(ThrustAxis.Right);
            if (KeyUp) dir += g.GetWorldAxis(ThrustAxis.Up);
            if (KeyDown) dir += g.GetWorldAxis(ThrustAxis.Down);
            return dir;
        }

        /// <summary>
        /// 旋转键 → 世界系角速度（船体局部轴经 rotation 旋转）。
        /// 符号约定：Q/E 滚转（绕船头轴）、方向键 偏航（绕船垂直轴）/俯仰（绕船横向轴）；手感符号实现时可调。
        /// </summary>
        public static DVector3 GetRotationWorldAngularVelocity(SpaceEntityGroup g)
        {
            float r = SpaceEntityGroup.DefaultMaxAngularRate;
            DVector3 w = DVector3.zero;
            Quaternion rot = g.CurrentOrbit.rotation;
            if (KeyRollL) w += DVector3.Rotate(DVector3.back * r, rot);    // 绕 -Forward 滚转
            if (KeyRollR) w += DVector3.Rotate(DVector3.forward * r, rot);
            if (KeyYawL) w += DVector3.Rotate(DVector3.up * r, rot);
            if (KeyYawR) w += DVector3.Rotate(DVector3.down * r, rot);
            if (KeyPitchU) w += DVector3.Rotate(DVector3.left * r, rot);   // 绕 -Right 抬头
            if (KeyPitchD) w += DVector3.Rotate(DVector3.right * r, rot);
            return w;
        }

        /// <summary>SAS 目标世界方向（主推进轴对准目标；None 返回零）。</summary>
        public static DVector3 GetSasTarget(SpaceEntityGroup g)
        {
            switch (SasKind)
            {
                case SasTargetKind.Prograde:
                    return g.CurrentOrbit.velocity.normalized;
                case SasTargetKind.Retrograde:
                    return -g.CurrentOrbit.velocity.normalized;
                case SasTargetKind.Normal:
                case SasTargetKind.Antinormal:
                {
                    DVector3 radial = g.localPosition.normalized;
                    DVector3 h = DVector3.Cross(radial, g.localVelocity).normalized;
                    if (h.sqrMagnitude < 1e-9)
                        h = DVector3.up;
                    return SasKind == SasTargetKind.Normal ? h : -h;
                }
                case SasTargetKind.RadialOut:
                    return g.localPosition.normalized;
                case SasTargetKind.RadialIn:
                    return -g.localPosition.normalized;
                default:
                    return DVector3.zero;
            }
        }
    }
}
