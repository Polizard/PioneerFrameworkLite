using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    /// <summary>
    /// 环世界视觉层的**每帧共享状态**（2026-10-17 电影感视觉升级新增）。
    ///
    /// 为什么需要它：环的所有受光/辉光 shader 需要三个每帧变化的世界量——太阳方向、
    /// 世界相机位置、动画时间。1.7 的 world shader 虽然有引擎的 FragmentUniforms，
    /// 但 Mod 自定义 shader 是否能自动拿到它**未在游戏内验证**（见 IMPL §2.3）。因此
    /// 这里统一由 C# 显式写给所有登记材质，走 Material.SetVector（原版 Clouds 层的
    /// `Render()` 每帧 SetFloat 是同一机制，1.7 下已被原版自己使用）。
    ///
    /// 纪律：
    /// - 每个 ring 绘制层在 `Render()` 开头调用 `UpdatePerFrame()`；同帧内重复调用只刷新一次。
    /// - 登记材质由创建者调用 `Register`；材质被销毁后自动跳过（不做强引用清理，环世界数量极少）。
    /// - 本类不做几何/状态判断，只在 `Find.World` 可用时取太阳与相机；取不到时保留上一帧值。
    /// </summary>
    internal static class RingWorldVisualState
    {
        private static readonly List<Material> materials = new List<Material>();
        private static readonly int SunDirId = Shader.PropertyToID("_RingSunDir");
        private static readonly int CameraPosId = Shader.PropertyToID("_RingCameraPos");
        private static readonly int TimeId = Shader.PropertyToID("_RingTime");

        private static int lastFrame = int.MinValue;
        private static Vector3 sunDirection = new Vector3(0.3f, 0.75f, -0.55f).normalized;
        private static Vector3 lightPropagation = -new Vector3(0.3f, 0.75f, -0.55f).normalized;
        private static Vector3 cameraPosition;
        private static float visualTime;

        /// <summary>指向太阳的单位向量（与 WorldSkyBillboards / GenCelestial 同源）。</summary>
        internal static Vector3 SunDirection => sunDirection;

        /// <summary>光传播方向（太阳 → 环），受光 shader 的 `_RingSunDir` 口径。</summary>
        internal static Vector3 LightPropagationDirection => lightPropagation;

        /// <summary>世界相机位置（视图单位）。</summary>
        internal static Vector3 CameraPosition => cameraPosition;

        /// <summary>秒级动画时间（用于灯光呼吸/闪烁）。</summary>
        internal static float Time => visualTime;

        internal static void Register(Material material)
        {
            if (material == null || materials.Contains(material)) return;
            materials.Add(material);
        }

        /// <summary>
        /// 每帧刷新一次：读太阳方向/相机位置/时间，并把三个 vec4 uniform 写给所有登记材质。
        /// 任何 ring 绘制层都可以在 Render() 开头安全调用（幂等）。
        /// </summary>
        internal static void UpdatePerFrame()
        {
            int frame = UnityEngine.Time.frameCount;
            if (frame == lastFrame) return;
            lastFrame = frame;

            visualTime = UnityEngine.Time.realtimeSinceStartup;

            if (Find.World != null)
            {
                Vector3 sun = GenCelestial.CurSunPositionInWorldSpace();
                if (sun.sqrMagnitude > 1e-8f)
                {
                    sunDirection = sun.normalized;
                    lightPropagation = -sunDirection;
                }
            }

            Camera camera = WorldCameraManager.WorldCamera;
            if (camera != null) cameraPosition = camera.transform.position;

            ApplyToMaterials();
        }

        private static void ApplyToMaterials()
        {
            var sun = new Vector4(lightPropagation.x, lightPropagation.y, lightPropagation.z, 0f);
            var pos = new Vector4(cameraPosition.x, cameraPosition.y, cameraPosition.z, 1f);
            var time = new Vector4(visualTime, 0f, 0f, 0f);
            for (int i = materials.Count - 1; i >= 0; i--)
            {
                Material material = materials[i];
                if (material == null)
                {
                    materials.RemoveAt(i);
                    continue;
                }
                material.SetVector(SunDirId, sun);
                material.SetVector(CameraPosId, pos);
                material.SetVector(TimeId, time);
            }
        }
    }
}
