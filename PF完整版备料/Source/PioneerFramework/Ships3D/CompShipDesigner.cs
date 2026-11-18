using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public class CompProperties_ShipDesigner : CompProperties
    {
        public CompProperties_ShipDesigner()
        {
            compClass = typeof(CompShipDesigner);
        }
    }

    /// <summary>
    /// [兼容] 飞船设计台 Comp（旧入口）。
    /// 现重定向到 Window_ShipAssembler3D。
    /// </summary>
    public class CompShipDesigner : ThingComp
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
                defaultLabel = "3D 飞船搭建",
                defaultDesc = "打开 3D 飞船装配视口，自由摆放部件。",
                icon = TexCommand.DesirePower,
                action = delegate
                {
                    // 为兼容旧 Comp，构造一个 CompShipAssembler 适配器
                    var adapter = new ShipDesignerToAssemblerAdapter(this);
                    Find.WindowStack.Add(new Window_ShipAssembler3D(adapter));
                }
            };
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
        }

        /// <summary>将旧的 CompShipDesigner 包装为 CompShipAssembler 接口。</summary>
        private class ShipDesignerToAssemblerAdapter : CompShipAssembler
        {
            private readonly CompShipDesigner designer;

            public ShipDesignerToAssemblerAdapter(CompShipDesigner designer)
            {
                this.designer = designer;
            }

            public new Ship3D CurrentShip => designer.CurrentShip;
        }
    }
}
