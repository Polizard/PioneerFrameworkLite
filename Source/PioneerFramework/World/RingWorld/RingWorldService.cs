using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.RingWorld
{
    public static class RingWorldService
    {
        public const string LayerDefName = "PF_RingWorld";
        [ThreadStatic] internal static RingParameters? PendingParameters;
        public static RingSurfaceLayer? FindById(IEnumerable<PlanetLayer> layers, string id) =>
            layers.OfType<RingSurfaceLayer>().FirstOrDefault(layer => layer.Parameters?.RingId == id);
        public static RingSurfaceLayer? FindById(string id) => Current.Game?.World==null||Find.WorldGrid == null ? null : FindById(Find.WorldGrid.PlanetLayers.Values, id);
        public static void RequireMatching(RingSurfaceLayer layer, RingParameters parameters)
        {
            if (layer.Parameters != parameters) throw new InvalidOperationException("RingId already exists with different parameters.");
        }
        public static bool ValidateParent(RingParameters parameters, double parentRadius, out string reason)
        {
            reason = "";
            if (!double.IsFinite(parentRadius) || parentRadius <= 0 || parameters.RadiusMeters <= parentRadius)
            { reason = "Ring radius must exceed the parent body's radius."; return false; }
            return true;
        }
        public static bool TryCreate(RingParameters parameters,out RingSurfaceLayer? ring,out string reason)
        {
            ring=null;reason="";
            if(parameters==null){reason="Ring parameters are required.";return false;}
            if(Current.Game==null||Current.ProgramState!=ProgramState.Playing||Scribe.mode!=LoadSaveMode.Inactive)
            {reason="A loaded game and inactive Scribe are required.";return false;}
            var manager=Current.Game.GetComponent<Megastructures.MegastructureManager>();
            var result=manager.CreateRing(parameters,false);
            ring=result.Success?FindById(parameters.RingId):null;reason=result.Success?"":result.Code+": "+result.Detail;
            return result.Success&&ring!=null;
        }
        public static bool TryRemove(RingSurfaceLayer ring,out string reason)
        {
            var manager=Current.Game?.GetComponent<Megastructures.MegastructureManager>();
            if(manager==null){reason="A loaded game is required.";return false;}
            var association=manager.Backends.Associations.FirstOrDefault(a=>!a.Released&&a.Backend==new Megastructures.StructureBackendRef(RingBackendProvider.ProviderId,ring.Parameters.RingId));
            if(association==null)return RingWorldNativeBackend.TryRemove(ring,out reason);
            if(association.Ownership!=Megastructures.BindingOwnership.Owned){reason="BorrowedBackendProtected";return false;}
            var result=manager.RetireBackend("remove/"+association.Node.InstanceId,association.Node.InstanceId,Faction.OfPlayer.loadID,Megastructures.BackendDisposition.DestroyOwned);
            reason=result.Success?"":result.Code;return result.Success;
        }
    }
}
