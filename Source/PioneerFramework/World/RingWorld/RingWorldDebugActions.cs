using System;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    public static class RingWorldDebugActions
    {
        private static bool Ready() => Prefs.DevMode && Current.ProgramState == ProgramState.Playing &&
            Find.WorldGrid != null && Scribe.mode == LoadSaveMode.Inactive;
        private static void Error(string reason) => Messages.Message("PF_RingError".Translate(reason),MessageTypeDefOf.RejectInput,false);
        public static void CreateSmall() => Create(256,3);
        public static void CreateLarge() => Create(1024,12);
        private static void Create(int columns,int rows)
        {
            if (!Ready()) return;
            int parentId=ViewFrame.ViewedBodyId;
            var body=Current.Game.GetComponent<CelestialBodyManager>()?.GetBodyById(parentId);
            if(body==null) { Error("Parent body was not found.");return; }
            var parameters=new RingParameters($"PF_Ring_{parentId}_{columns}_{rows}",parentId,194913+parentId,
                columns,rows,body.radius*3.2);
            if(!RingWorldService.TryCreate(parameters,out RingSurfaceLayer? ring,out string reason)) {Error(reason);return;}
            RingWorldColonyService.View(ring!);
            Messages.Message("PF_RingCreated".Translate(ring!.Parameters.RingId,ring.TilesCount),MessageTypeDefOf.TaskCompletion,false);
        }
        private static void Choose(Action<RingSurfaceLayer> action)
        {
            if(!Ready()) return;
            var options=Find.WorldGrid.PlanetLayers.Values.OfType<RingSurfaceLayer>()
                .Select(ring=>new FloatMenuOption($"{ring.Parameters.RingId} [{ring.LayerID}]",()=>action(ring))).ToList();
            if(options.Count==0) {Error("PF_RingNone".Translate());return;}
            Find.WindowStack.Add(new FloatMenu(options));
        }
        public static void View() => Choose(ring=>RingWorldColonyService.View(ring));
        public static void FocusSelected()
        {
            if(!Ready()) return;
            PlanetTile tile=Find.WorldSelector.SelectedTile;
            if(!tile.Valid||tile.Layer is not RingSurfaceLayer ring) {Error("PF_RingSelectTile".Translate());return;}
            RingWorldColonyService.View(ring,tile.tileId);
        }
        public static void EnterSelectedMap()
        {
            if(!Ready()) return;
            PlanetTile tile=Find.WorldSelector.SelectedTile;
            if(!tile.Valid||tile.Layer is not RingSurfaceLayer ring) {Error("PF_RingSelectTile".Translate());return;}
            int tileId=tile.tileId;
            LongEventHandler.QueueLongEvent(()=>
            {
                if(!RingWorldColonyService.TryGetOrGenerateMap(ring,tileId,new IntVec3(250,1,250),out Map? map,out string reason))
                {Error(reason);return;}
                Current.Game.CurrentMap=map; CameraJumper.TryHideWorld();
            },"GeneratingMap",false,null);
        }
        public static void Validate() => Choose(ring=>
        {
            var graph=ring.Topology;
            for(int tile=0;tile<graph.TileCount;tile++)
            {
                int[] neighbors=graph.Neighbors(tile);
                int expected=tile/graph.Columns==0||tile/graph.Columns==graph.Rows-1?4:6;
                if(neighbors.Length!=expected||neighbors.Any(other=>!graph.Neighbors(other).Contains(tile)))
                {Error($"Invalid adjacency at tile {tile}.");return;}
            }
            int row=graph.Rows/2;
            var result=RingPathSearch.Find(graph,graph.TileId(1,row),graph.TileId(graph.Columns-2,row));
            string report=$"RingId={ring.Parameters.RingId}; LayerID={ring.LayerID}; tiles={ring.TilesCount}; seamSteps={result.Cost}; " +
                $"maps={Find.Maps.Count(map=>map.Tile.Valid&&map.Tile.Layer==ring)}; parent={ring.Parameters.ParentBodyId}; format={ring.Parameters.FormatVersion}";
            Log.Message("[PFL RingWorld] "+report);
            Messages.Message("PF_RingValidated".Translate(),MessageTypeDefOf.TaskCompletion,false);
        });
        public static void Remove() => Choose(ring=>
        {
            if(!RingWorldService.TryRemove(ring,out string reason)) Error(reason);
            else Messages.Message("PF_RingRemoved".Translate(),MessageTypeDefOf.TaskCompletion,false);
        });
    }
}
