using System.Linq;
using RimWorld;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 火箭建造链的两处衔接（2026-10-15，"反射清理"批 **A-15**：替代原先的两个 Harmony 补丁——
    /// `Blueprint_Build.MakeSolidThing` 的 Postfix 与 `Frame.CompleteConstruction` 的 Prefix+Postfix）。
    ///
    /// ## ① 规格传递（蓝图 → 框架）
    /// `MakeSolidThing` 在 `Blueprint` 上是 abstract 链、可覆写 ⇒ 我方火箭 def 的
    /// `<building><blueprintClass>` 指向 <see cref="PF_Blueprint_Rocket"/>，在"蓝图变框架"的那一刻把
    /// `CompRocketBlueprint.Spec` 交给 `CompUnfinishedRocket.AssignSpec`。
    /// ⚠ 帧侧拉取不可行（`Blueprint_Build` 里蓝图先销毁、帧才 spawn）——这正是当年要打补丁的原因。
    ///
    /// ## ② 完工通知（框架 → 发射台）
    /// 原补丁在 `Frame.CompleteConstruction` 的 Postfix 里"按位置找火箭、找台、通知"；本实现改为
    /// **火箭自己**在 `PostSpawnSetup` 里做同一件事——`Frame.CompleteConstruction` 的最后是
    /// `GenSpawn.Spawn(thing, frame.Position, …)` ⇒ 火箭出生的那一格就是框架那一格。
    /// 守卫三连（与旧补丁的触发窗口等价）：台在建造中 · 台还没记录火箭 · 台的 `Design` 指的就是本 def。
    /// `respawningAfterLoad` 直接排除读档（读档不是"刚完工"）。
    /// </summary>
    public class PF_Blueprint_Rocket : Blueprint_Build
    {
        protected override Thing MakeSolidThing(out bool shouldSelect)
        {
            Thing made = base.MakeSolidThing(out shouldSelect);

            if (made is Frame frame)
            {
                CompRocketBlueprint? blueprintComp = this.TryGetComp<CompRocketBlueprint>();
                CompUnfinishedRocket? frameComp = frame.TryGetComp<CompUnfinishedRocket>();
                if (blueprintComp != null && frameComp != null)
                    frameComp.AssignSpec(blueprintComp.Spec);
            }

            return made;
        }
    }

    public class CompProperties_RocketCompletionNotify : CompProperties
    {
        public CompProperties_RocketCompletionNotify()
        {
            compClass = typeof(CompRocketCompletionNotify);
        }
    }

    /// <summary>完工通知（见 <see cref="PF_Blueprint_Rocket"/> 头注 ②）。挂在我方 4 个火箭 def 上。</summary>
    public class CompRocketCompletionNotify : ThingComp
    {
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (respawningAfterLoad)
                return;

            Thing? self = parent;
            if (self == null)
                return;
            Map? map = self.Map;
            if (map == null)
                return;

            Building? pad = map.thingGrid.ThingsListAt(self.Position)
                .OfType<Building>()
                .FirstOrDefault(b => LaunchPadUtility.IsAssemblyPad(b.def));
            CompAssemblyPad? padComp = pad?.TryGetComp<CompAssemblyPad>();
            if (padComp == null)
                return;

            if (padComp.State != LaunchPadState.Building || padComp.BuiltRocket != null)
                return;
            if (padComp.Design?.rocketDefName != self.def.defName)
                return;

            padComp.NotifyRocketCompleted(self);
        }
    }
}
