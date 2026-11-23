using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **世界相机服务**（2026-10-15 建 / 2026-10-16 扩，"反射清理"批 **A-13**：把四处 Harmony 补丁收成一台常驻组件）。
    ///
    /// 收进来的四件事（原补丁 → 现在都在这一个 `Update` 里）：
    /// ① `WorldCameraDriver.Awake` Postfix → **farClip 兜底**（抬到 200000；不依赖视图开关，
    ///    拉远上限 50000 需要它，否则远处被裁）；
    /// ② `WorldCameraDriver.Update` Postfix → **跟随选中编队**（只写 `sphereRotation`，
    ///    见 `SpaceGroupVisualManager.ApplyFollowCameraFrame`）——Slerp 结果由**下一帧** vanilla
    ///    `Update → ApplyPositionToGameObject` 消费；
    /// ③ `WorldCameraDriver.ApplyMapPositionToGameObject` Postfix → **含图编队的背景相机接管**
    ///    （位置/朝向 + near clip；逻辑仍在 `ShipMapBackgroundCamera`）；
    /// ④ `WorldCameraDriver.Update` Postfix → 退出接管时**还原 near clip**（与 ③ 成对）。
    /// 另：**A-14 换层的帧兜底**（每 60 帧核对一次，成功后单布尔早退）也挂在这里。
    ///
    /// ⚠ **执行序 = −50**（`UnityRuntime.Current.World.RegisterExecutionOrder`，2026-10-16 核过引擎源码：
    /// `WorldCameraDriver = −103` → `Root_Entry = −50` → `Root_Play = −20`，而**世界层绘制在 Root_Play 的 Update 里**）：
    /// · ①② 不写相机 transform ⇒ 任何晚于相机驱动的时机都同效（写早写晚都行）；
    /// · ③ **必须写在世界层绘制之前**（天空盒按相机位姿提交，晚一帧会"游泳"）⇒ −50 正好：
    ///   晚于相机驱动（−103；它此刻**不写**背景相机——我们的 `MapParent.WorldCameraPosition` 是
    ///   `Vector3.zero`，vanilla 在 `ApplyMapPositionToGameObject` 里直接早退）、早于世界绘制 ✓；
    /// · GUI 阶段那条 `ApplyPositionToGameObject` 跑在**渲染之后**，只影响下一帧、会被本组件覆盖 ✓。
    /// （若执行序注册失败 ⇒ 退化为默认序 0 = 世界绘制之后 ⇒ 天空盒会漂一帧；注册处有显式报错。）
    /// </summary>
    public class PF_WorldCameraService : MonoBehaviour
    {
        /// <summary>在册对象（启动时由 `SpaceGroupVisualManager` 赋值）——供"机制在册自检"读。</summary>
        public static PF_WorldCameraService? Instance;

        /// <summary>`Update` 已跑的帧数（机制在册自检的**活体证明**：>0 = 引擎真的在泵本组件）。</summary>
        public static long UpdateFrames;

        /// <summary>
        /// 执行序 −50 是否注册成功（由 `SpaceGroupVisualManager` 的静态构造写入 —— **自证位**：
        /// 注册失败会静默退化成默认序 ⇒ 天空盒晚一帧"游泳"，故机制在册自检里有一条硬判据读它）。
        /// </summary>
        public static bool ExecutionOrderRegistered;

        public void Update()
        {
            UpdateFrames++;

            // A-14 帧兜底：每 60 帧核对一次换层（成功 = 单布尔早退，零成本；
            // 覆盖"静态构造那刻 def 还没定型/被重建"的情形——1.7 实测过）。
            if (UpdateFrames % 60 == 0)
                VanillaSkySuppression.EnsureLayerTypesSwapped("帧兜底");

            // ① farClip 兜底（持续属性，谁在何时写都行）
            var cam = Find.WorldCamera;
            if (cam != null && cam.farClipPlane < 200000f)
                cam.farClipPlane = 200000f;

            // ③④ 背景相机接管（写位姿 ⇒ 必须早于世界绘制）与 near clip 还原（成对，顺序不许换）。
            // ⚠ 守卫是**必须的**（2026-10-16 实测事故：启动窗口内每帧 NRE 60 次）：
            //   `WorldRendererUtility.WorldBackgroundNow` 里读 `Find.World.renderer.wantedMode`——
            //   而 `World.renderer` 在"世界对象已建、渲染器还没就绪"的那段窗口里是 **null**。
            //   vanilla 在它自己的调用点先挡了两道（`LongEventHandler.ShouldWaitForEvent` 与 `Find.World == null`），
            //   我们从 `WorldCameraDriver.Update` 搬走这段逻辑时丢了它们 ⇒ 自己把守卫补回（用 `renderer` 判更精确）。
            var world = Find.World;
            if (!LongEventHandler.ShouldWaitForEvent && world?.renderer != null)
            {
                ShipMapBackgroundCamera.ApplyAfterVanillaMapPosition();
                ShipMapBackgroundCamera.RestoreNearClipIfNotTakenOver();
            }

            // ② 跟随选中编队（只写 sphereRotation ⇒ 下一帧消费）
            if (!LongEventHandler.ShouldWaitForEvent)
                PFViewSurfaceBake.Update(WorldCelestialHost.Active);
            WorldCelestialHost.Update();
            if (!WorldCelestialHost.Active)
                SpaceGroupVisualManager.ApplyFollowCameraFrame();
        }
    }
}
