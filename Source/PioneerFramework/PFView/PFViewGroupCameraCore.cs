using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace PioneerFramework
{
    /// <summary>
    /// 编队镜头模式（2026-10-18 用户口径：**把第三人称拆成两种「正向」**）：
    /// · <see cref="Free" />      自由环绕（世界/固连参考系，不跟姿态）；
    /// · <see cref="ThirdPersonHull" />    第三人称，**船体朝向为正向前**（船头指向哪，镜头就跟到哪条尾巴后面）；
    /// · <see cref="ThirdPersonVelocity" /> 第三人称，**速度方向为正向前**（船横着漂/倒着飞时镜头仍沿运动方向）。
    /// 枚举值会进 Mod 配置（`pfviewFocusCameraMode`），**只许追加、不许改已有数值**。
    /// </summary>
    public enum PFViewGroupCameraMode
    {
        Free = 0,
        ThirdPersonHull = 1,
        ThirdPersonVelocity = 2,
    }

    internal readonly record struct PFViewGroupCameraDistances(float MinMeters, float DefaultMeters, float MaxMeters)
    {
        public float ClampMeters(float distance)
            => float.IsFinite(distance) ? Math.Clamp(distance, MinMeters, MaxMeters) : DefaultMeters;
    }

    /// <summary>编队第三人称的显示规则，仅使用角度、尺寸和设置值。</summary>
    internal static class PFViewGroupCameraCore
    {
        public const float DefaultPitch = 15f;

        /// <summary>两种第三人称（船体正向/速度正向）共享取景与输入规则；自由视角不算。</summary>
        public static bool IsThirdPerson(PFViewGroupCameraMode mode)
            => mode == PFViewGroupCameraMode.ThirdPersonHull
               || mode == PFViewGroupCameraMode.ThirdPersonVelocity;

        /// <summary>默认模式：新装/设置缺失 = 船体正向（保留旧行为）；非法值同样回落。</summary>
        public static PFViewGroupCameraMode DefaultMode(PFViewGroupCameraMode? preference)
            => preference is { } mode && IsDefined(mode) ? mode : PFViewGroupCameraMode.ThirdPersonHull;

        /// <summary>存档/配置里的整数 → 模式。新键缺席或越界时按旧 `pfviewThirdPersonOnFocus` 迁移：
        /// true = 船体正向（旧「第三人称」的唯一含义），false = 自由。</summary>
        public static PFViewGroupCameraMode MigrateMode(int stored, bool legacyThirdPersonOnFocus)
        {
            var mode = (PFViewGroupCameraMode)stored;
            return IsDefined(mode) ? mode
                : legacyThirdPersonOnFocus ? PFViewGroupCameraMode.ThirdPersonHull : PFViewGroupCameraMode.Free;
        }

        private static bool IsDefined(PFViewGroupCameraMode mode)
            => mode is PFViewGroupCameraMode.Free
                or PFViewGroupCameraMode.ThirdPersonHull
                or PFViewGroupCameraMode.ThirdPersonVelocity;

        public static float ClampYaw(float yaw) => float.IsFinite(yaw) ? Math.Clamp(yaw, -60f, 60f) : 0;
        public static float ClampPitch(float pitch) => float.IsFinite(pitch) ? Math.Clamp(pitch, -10f, 45f) : DefaultPitch;

        public static PFViewGroupCameraDistances Distances(float radiusMeters, float verticalFov, float aspect)
        {
            float radius = float.IsFinite(radiusMeters) && radiusMeters > 0 ? Math.Max(1f, radiusMeters) : 10f;
            float fov = float.IsFinite(verticalFov) ? Math.Clamp(verticalFov, 5f, 150f) : 45f;
            float ratio = float.IsFinite(aspect) && aspect > 0 ? aspect : 16f / 9f;
            float tangent = MathF.Tan(fov * MathF.PI / 360f) * Math.Min(1f, ratio) / 3f;
            float min = Math.Max(5f, radius * 1.15f + 1f);
            float initial = Math.Max(min * 1.25f, radius * MathF.Sqrt(1f + 1f / (tangent * tangent)));
            return new(min, initial, initial * 4f);
        }
    }

    /// <summary>游戏内机位记忆；模式和编队各自保存，不写入存档。</summary>
    internal sealed class PFViewGroupCameraMemory<T> where T : class
    {
        private readonly Dictionary<(int group, PFViewGroupCameraMode mode), T> poses = new();

        public void Remember(int groupId, PFViewGroupCameraMode mode, T pose) => poses[(groupId, mode)] = pose;
        public bool TryGet(int groupId, PFViewGroupCameraMode mode, [NotNullWhen(true)] out T? pose)
            => poses.TryGetValue((groupId, mode), out pose);
        public void Clear() => poses.Clear();
    }
}
