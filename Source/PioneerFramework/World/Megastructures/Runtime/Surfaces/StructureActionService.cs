using System.Collections.Generic;
using System.Linq;
using Verse;

namespace PioneerFramework.Megastructures
{
    public sealed class StructureActionService
    {
        private readonly MegastructureManager manager;
        public StructureActionService(MegastructureManager manager)=>this.manager=manager;
        public IReadOnlyList<StructureActionDescriptor> Query(SurfaceAddress surface,int actor,int cell=-1)
        {
            var instance=manager.GetSnapshot(surface.Node.InstanceId);
            bool resolved=manager.Surfaces.TryResolve(surface,out _,out var description);
            bool validCell=resolved&&cell>=0&&cell<description.Geometry.CellCount;
            StructureActionDescriptor Action(string key,string label,StructurePermission permission,bool needsCell=false)
            {
                string error=instance==null||instance.Lifecycle==StructureLifecycle.Retired?"NodeUnavailable":!resolved?"SurfaceMissing":needsCell&&!validCell?"CellMissing":!StructurePermissions.Allows(instance,surface.Node.NodeId,actor,permission)?"PermissionDenied":"";
                if(error.Length==0&&key=="destroy"&&manager.Backends.Associations.Any(a=>a.Node.InstanceId==surface.Node.InstanceId&&!a.Released&&a.Ownership!=BindingOwnership.Owned))error="BorrowedBackendProtected";
                string? module=key is "overview" or "focus"?SurfaceModules.View:key=="enter"?SurfaceModules.Map:key=="validate"?SurfaceModules.Navigation:null;
                if(error.Length==0&&module!=null&&!description.ModuleEnabled(module))error="CapabilityDisabled";
                return new(key,label,permission,needsCell?"SurfaceCell":"Surface",error.Length==0,error);
            }
            return new[]{Action("overview","PF_Surface_Overview",StructurePermission.View),Action("focus","PF_Surface_Focus",StructurePermission.View,true),Action("enter","PF_Surface_Enter",StructurePermission.Construct,true),Action("validate","PF_Surface_Validate",StructurePermission.View),Action("detach","PF_Surface_Detach",StructurePermission.Dismantle),Action("destroy","PF_Surface_Destroy",StructurePermission.Dismantle)};
        }
        public FrameworkResult Execute(StructureSurfaceActionRequest request,out Map? map)
        {
            map=null;if(Scribe.mode!=LoadSaveMode.Inactive)return FrameworkResult.Reject("SaveInProgress");
            if(string.IsNullOrWhiteSpace(request.OperationId))return FrameworkResult.Reject("InvalidOperationId");
            var instance=manager.GetSnapshot(request.Surface.Node.InstanceId);
            if(instance==null)return FrameworkResult.Reject("InstanceMissing");
            if(request.ExpectedRevision>=0&&instance.Revision!=request.ExpectedRevision)return FrameworkResult.Reject("RevisionConflict");
            var action=Query(request.Surface,request.ActorFactionId,request.CellId).FirstOrDefault(a=>a.Key==request.Key);
            if(action==null)return FrameworkResult.Reject("ActionMissing");
            if(!action.Available)return FrameworkResult.Reject(action.BlockedReason);
            manager.Surfaces.TryResolve(request.Surface,out var layer,out var surface);
            switch(request.Key)
            {
                case "overview":case "focus":return SurfaceViewController.View(layer,request.Key=="focus"?request.CellId:-1);
                case "enter":return manager.MapSites.Access(StructureMapSiteAddress.ForCell(new(request.Surface,request.CellId)),true,request.ActorFactionId,new(request.Width,1,request.Height),out map);
                case "detach":case "destroy":return manager.RetireBackend(request.OperationId,instance.Id,request.ActorFactionId,request.Key=="detach"?BackendDisposition.DetachKeepBackend:BackendDisposition.DestroyOwned);
                case "validate":return Validate(surface.Geometry);
                default:return FrameworkResult.Reject("ActionMissing");
            }
        }
        public static FrameworkResult Validate(IArtificialSurfaceGeometry geometry)
        {
            var neighbors=new List<int>();var reverse=new List<int>();
            for(int cell=0;cell<geometry.CellCount;cell++)
            {
                geometry.GetNeighbors(cell,neighbors);
                foreach(int other in neighbors)
                {
                    if(other<0||other>=geometry.CellCount)return FrameworkResult.Reject("InvalidNeighbor");
                    geometry.GetNeighbors(other,reverse);if(!reverse.Contains(cell))return FrameworkResult.Reject("AsymmetricNeighbor");
                }
            }
            return FrameworkResult.Ok();
        }
    }
}
