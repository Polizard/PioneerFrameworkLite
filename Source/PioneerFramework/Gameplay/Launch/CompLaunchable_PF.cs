using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace PioneerFramework.Launch
{
    /// <summary>
    /// PFL 管理火箭的**发射 Gizmo 压制**（2026-10-15，"反射清理"批 A-5：替代原先挂在
    /// `CompLaunchable.CompGetGizmosExtra` 基类上的 Harmony Prefix）。
    ///
    /// 机制：`ThingDef.comps` 里 comp 的 `Class=` 就是属性类 ⇒ 我们那 4 个火箭 def
    /// 把 `CompProperties_Launchable_TransportPod` 换成**本文件的**属性类（`compClass` 指向
    /// `CompLaunchable_PF`），覆写 `CompGetGizmosExtra` 即可——判据与旧补丁**同一条**
    /// （`RocketLaunchManager.IsManagedRocket` = 带 `CompRocketPocketMap`，而该 comp 只在
    /// 我们这 4 个 def 里声明、无运行期动态添加 ⇒ 与旧补丁的基类拦截**同集**）。
    ///
    /// 语义：PFL 火箭的发射入口统一走「发射…」（`RocketLaunchManager`/`Dialog_RocketLaunch`），
    /// 不再显示原版 `CompLaunchable` 的发射 Gizmo。
    /// </summary>
    public class CompProperties_Launchable_PF : CompProperties_Launchable_TransportPod
    {
        public CompProperties_Launchable_PF()
        {
            compClass = typeof(CompLaunchable_PF);
        }
    }

    /// <summary>见 <see cref="CompProperties_Launchable_PF"/>。</summary>
    public class CompLaunchable_PF : CompLaunchable_TransportPod
    {
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (RocketLaunchManager.IsManagedRocket(parent))
                return Enumerable.Empty<Gizmo>();

            return base.CompGetGizmosExtra();
        }
    }
}
