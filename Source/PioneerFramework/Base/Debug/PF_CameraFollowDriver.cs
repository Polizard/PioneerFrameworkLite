using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 世界相机的每帧服务（2026-10-15，"反射清理"批 **A-13**：替代
    /// `WorldCameraDriver.Update` 与 `WorldCameraDriver.Awake` 两处 Harmony Postfix）。
    ///
    /// ① **farClipPlane 兜底**：抬到 200000（不依赖视图开关）——拉远上限 50000（MaxAltitude
    ///    transpiler）需要它，否则远处被裁；
    /// ② **跟随选中编队**：把 `sphereRotation` 朝"从球心看编队"的方向 Slerp（原补丁逻辑原样搬运，
    ///    见 `SpaceGroupVisualManager.ApplyFollowCameraFrame`）。
    ///
    /// ⚠ 时序等价性（2026-10-15 核过）：两项都**不写相机 transform**——
    /// ① 是持续属性（谁在何时写都行）；② 只改 `sphereRotation`，而它由**下一帧**
    /// vanilla `WorldCameraDriver.Update → ApplyPositionToGameObject` 消费
    /// （旧 postfix 也是在 `Update` 之后写同一个字段）⇒ 一个普通 MonoBehaviour（默认执行序、
    /// 每帧、**Entry 态也在跑**）与原补丁同效，**不必碰引擎的执行序注册**。
    /// </summary>
    public class PF_CameraFollowDriver : MonoBehaviour
    {
        public void Update()
        {
            var cam = Find.WorldCamera;
            if (cam != null && cam.farClipPlane < 200000f)
                cam.farClipPlane = 200000f;

            SpaceGroupVisualManager.ApplyFollowCameraFrame();
        }
    }
}
