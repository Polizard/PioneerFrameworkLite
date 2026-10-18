using System;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Megastructures
{
    public static class SettlementMapSiteProvider
    {
        public static bool TryGetOrGenerateMap(PlanetLayer layer,int cell,IntVec3 size,bool generate,out Map? map,out string reason)
        {
            map=null;reason="";
            if(Current.ProgramState!=ProgramState.Playing||Find.WorldGrid==null||Scribe.mode!=LoadSaveMode.Inactive)
            {reason="A loaded game and inactive Scribe are required.";return false;}
            if(!Find.WorldGrid.PlanetLayers.TryGetValue(layer.LayerID,out var current)||!ReferenceEquals(current,layer)||!ArtificialSurfaceLayers.TryDescribe(layer,out var surface))
            {reason="Surface is not registered.";return false;}
            if(cell<0||cell>=surface.Geometry.CellCount||size.x<50||size.z<50||size.x>500||size.z>500)
            {reason="Invalid tile or map size (50..500 cells per side).";return false;}
            var tile=new PlanetTile(cell,layer);var parent=Find.WorldObjects.MapParentAt(tile);
            if(parent!=null&&(parent is not Settlement||parent.Faction!=Faction.OfPlayer))
            {reason="The tile already has another world object.";return false;}
            if(parent?.Map!=null){map=parent.Map;return true;}
            if(!generate){reason="MapMissing";return false;}
            if(parent==null&&!TileFinder.IsValidTileForNewSettlement(tile))
            {reason="This tile is not valid for a new settlement.";return false;}
            Settlement? created=null;
            try
            {
                if(parent==null)
                {
                    created=(Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
                    created.Tile=tile;created.SetFaction(Faction.OfPlayer);
                    created.Name=surface.ColonyNameKey.Translate(surface.Backend.StableId,cell);
                    Find.WorldObjects.Add(created);
                }
                map=GetOrGenerateMapUtility.GetOrGenerateMap(tile,size,WorldObjectDefOf.Settlement);
                if(map==null)throw new InvalidOperationException("Map generation returned no map.");
                return true;
            }
            catch(Exception e)
            {
                if(created!=null&&!created.HasMap&&created.Spawned)created.Destroy();
                reason=e.Message;Log.Error("[PFL ArtificialSurface] Colony generation failed: "+e);return false;
            }
        }
    }
}
