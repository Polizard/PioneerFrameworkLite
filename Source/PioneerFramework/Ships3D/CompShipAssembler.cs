using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    public class CompProperties_ShipAssembler : CompProperties
    {
        public CompProperties_ShipAssembler()
        {
            compClass = typeof(CompShipAssembler);
        }
    }

    /// <summary>
    /// 飞船装配台 Comp。提供 Gizmo 打开 3D 飞船装配窗口。
    /// 与部件设计台 (CompPartTemplateDesigner) 分离，各用不同 Gizmo 进入。
    /// </summary>
    public class CompShipAssembler : ThingComp
    {
        private Ship3D currentShip;

        public Ship3D CurrentShip
        {
            get
            {
                if (currentShip == null)
                    currentShip = new Ship3D();
                return currentShip;
            }
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (var g in base.CompGetGizmosExtra())
                yield return g;

            if (parent.Faction != Faction.OfPlayer)
                yield break;

            yield return new Command_Action
            {
                defaultLabel = "飞船装配",
                defaultDesc = "打开 3D 飞船装配器，从已保存的部件模板中选择并组装飞船。",
                icon = TexCommand.DesirePower,
                action = delegate
                {
                    Find.WindowStack.Add(new Window_ShipAssembler3D(this));
                }
            };
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
        }
    }
}
