using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    public class CompProperties_UmbilicalTower : CompProperties
    {
        public CompProperties_UmbilicalTower()
        {
            compClass = typeof(CompUmbilicalTower);
        }
    }

    /// <summary>
    /// 脐带塔：持有地面↔航天器地图的桥，并提供“登上火箭”入口。
    /// </summary>
    public class CompUmbilicalTower : ThingComp
    {
        private ThingWithComps assemblyPad;
        private SpacecraftBridge bridge;

        public ThingWithComps AssemblyPad => assemblyPad;

        public SpacecraftBridge Bridge => bridge;

        public void Bind(ThingWithComps pad)
        {
            assemblyPad = pad;
        }

        public void RebindExistingSpacecraftMap()
        {
            SpacecraftMapParent existing = SpacecraftMapRegistry.GetBySpacecraftId(assemblyPad?.ThingID);
            if (existing != null)
                RebindBridge(existing);
        }

        public SpacecraftMapParent EnsureSpacecraftMap()
        {
            if (assemblyPad == null || assemblyPad.Map == null)
                return null;

            SpacecraftMapParent existing = SpacecraftMapRegistry.GetBySpacecraftId(assemblyPad.ThingID);
            if (existing != null)
            {
                RebindBridge(existing);
                return existing;
            }

            Map map;
            // 让舱内出口（PocketMapExit）生成时能找到当前脐带塔 MapPortal 并自动绑定。
            PocketMapUtility.currentlyGeneratingPortal = parent as MapPortal;
            try
            {
                map = SpaceMapGenerator.GenerateSpacecraftMap(50, 50, assemblyPad.Map, assemblyPad.ThingID, "PF_RocketCabinPocket");
            }
            finally
            {
                PocketMapUtility.currentlyGeneratingPortal = null;
            }

            if (map == null)
                return null;

            SpacecraftMapParent spacecraft = map.Parent as SpacecraftMapParent;
            if (spacecraft == null)
                return null;

            RebindBridge(spacecraft);
            return spacecraft;
        }

        private void RebindBridge(SpacecraftMapParent spacecraft)
        {
            if (spacecraft == null || assemblyPad?.Map == null)
                return;

            IntVec3 exitCell = GenStep_RocketCabinLayout.CabinDoorCell;
            if (!exitCell.InBounds(spacecraft.Map))
            {
                foreach (IntVec3 cell in spacecraft.Map.AllCells)
                {
                    if (cell.InBounds(spacecraft.Map) && cell.Standable(spacecraft.Map))
                    {
                        exitCell = cell;
                        break;
                    }
                }
            }

            bridge = new SpacecraftBridge
            {
                groundMapId = assemblyPad.Map.uniqueID,
                groundCell = parent.Position,
                spacecraftMapId = spacecraft.Map.uniqueID,
                spacecraftCell = exitCell
            };
            spacecraft.BindGroundBridge(bridge);

            CompAssemblyPad padComp = assemblyPad.TryGetComp<CompAssemblyPad>();
            padComp?.RegisterExternalPocketMap(spacecraft.Map);
            padComp?.BuiltRocket?.TryGetComp<CompRocketPocketMap>()?.LinkMap(spacecraft.Map);

            // 双向绑定原版 MapPortal/PocketMapExit 桥。
            if (parent is MapPortal portal)
            {
                ThingDef exitDef = DefDatabase<ThingDef>.GetNamedSilentFail("PF_Building_Rocket_CabinExit");
                if (exitDef != null)
                {
                    Building_SpacecraftExit exit = spacecraft.Map.listerThings.ThingsOfDef(exitDef)
                        .OfType<Building_SpacecraftExit>()
                        .FirstOrDefault();
                    if (exit != null)
                    {
                        portal.exit = exit;
                        exit.entrance = portal;
                    }
                }
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_References.Look(ref assemblyPad, "assemblyPad");
            Scribe_Deep.Look(ref bridge, "bridge");
        }

        public override string CompInspectStringExtra()
        {
            if (assemblyPad == null || assemblyPad.Destroyed)
                return "脐带塔: 未绑定发射台";

            var padComp = assemblyPad.TryGetComp<CompAssemblyPad>();
            return padComp?.Design == null
                ? "脐带塔: 等待航天舱室设计"
                : "脐带塔: " + (padComp.Design.CabinType == RocketCabinType.Spacecraft ? "航天舱室可用" : "普通运输仓不可登船");
        }
    }
}
