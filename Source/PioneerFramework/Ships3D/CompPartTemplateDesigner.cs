using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    public class CompProperties_PartTemplateDesigner : CompProperties
    {
        public CompProperties_PartTemplateDesigner()
        {
            compClass = typeof(CompPartTemplateDesigner);
        }
    }

    /// <summary>
    /// 部件模板设计台 Comp。提供 Gizmo 打开部件模板设计窗口。
    /// </summary>
    public class CompPartTemplateDesigner : ThingComp
    {
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (var g in base.CompGetGizmosExtra())
                yield return g;

            if (parent.Faction != Faction.OfPlayer)
                yield break;

            yield return new Command_Action
            {
                defaultLabel = "部件设计",
                defaultDesc = "打开部件模板设计器，创建和编辑飞船部件型号。",
                icon = TexCommand.DesirePower,
                action = delegate
                {
                    Find.WindowStack.Add(new Window_PartTemplateDesigner());
                }
            };
        }
    }
}
