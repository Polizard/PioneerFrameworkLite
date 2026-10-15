using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.Launch
{
    /// <summary>
    /// 火箭/返回舱的**切线朝向**（2026-10-15，"反射清理"批 A-7：替代原先挂在
    /// `Skyfaller.GetDrawPositionAndRotation` **基类**上的 Harmony Postfix + `TargetMethod()` 反射）。
    ///
    /// 机制：`ThingDef.thingClass` 是 def 字段 ⇒ 把**我们的** skyfaller def 指到本文件的子类，
    /// 覆写 `GetDrawPositionAndRotation`：先 `base`（保住原版定位），再用 `CompRocketTangentGraphic`
    /// 重写 extraRotation。该方法在 1.7 是 `protected virtual`（`Skyfaller.cs:276`），覆写合法。
    ///
    /// 覆盖等价性（2026-10-15 逐条核对）：该 comp **只在 def XML 里声明、无任何运行期动态添加**
    ///（`CompProperties_RocketTangentGraphic` 的出现处 = 5 个 def：`PF_RocketLeavingBase`（抽象父）
    /// + 4 个 incoming def）；vanilla 的 skyfaller def 不带本 comp ⇒ 旧补丁的 guard
    ///（`comp == null` 早退）与"def 子类"的作用集**完全相同**。
    /// </summary>
    public class PF_Skyfaller_Leaving : FlyShipLeaving
    {
        protected override void GetDrawPositionAndRotation(ref Vector3 drawLoc, out float extraRotation)
        {
            base.GetDrawPositionAndRotation(ref drawLoc, out extraRotation);
            GetComp<CompRocketTangentGraphic>()?.ApplyTangentRotation(ref extraRotation, drawLoc, this);
        }
    }

    /// <summary>见 <see cref="PF_Skyfaller_Leaving"/>；本类服务 4 个 incoming def（基本/助推/CV-1/CV-3）。</summary>
    public class PF_Skyfaller_Incoming : DropPodIncoming
    {
        protected override void GetDrawPositionAndRotation(ref Vector3 drawLoc, out float extraRotation)
        {
            base.GetDrawPositionAndRotation(ref drawLoc, out extraRotation);
            GetComp<CompRocketTangentGraphic>()?.ApplyTangentRotation(ref extraRotation, drawLoc, this);
        }
    }
}
