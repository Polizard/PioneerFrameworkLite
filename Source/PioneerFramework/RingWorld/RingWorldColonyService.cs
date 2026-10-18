using System;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    public static class RingWorldColonyService
    {
        public static bool TryGetOrGenerateMap(RingSurfaceLayer ring, int tileId, IntVec3 size,
            out Map? map, out string reason)
        =>Megastructures.StructureMapSiteService.AccessNative(ring,tileId,size,out map,out reason);

        public static void View(RingSurfaceLayer ring, int tileId = -1)
        {
            var result=Megastructures.SurfaceViewController.View(ring,tileId);
            if(!result.Success)throw new InvalidOperationException(result.Code);
        }
    }
}
