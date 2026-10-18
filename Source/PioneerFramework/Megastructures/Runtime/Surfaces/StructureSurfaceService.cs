using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Megastructures
{
    public sealed class StructureSurfaceService
    {
        private readonly MegastructureManager manager;
        private readonly Dictionary<SurfaceAddress,(SpatialState Anchor,PeriodicMotion? Motion,SurfacePoseTransform Transform)> poses=new();
        public StructureSurfaceService(MegastructureManager manager)=>this.manager=manager;
        public IEnumerable<SurfaceAddress> ForInstanceIds()=>manager.Snapshots.Where(s=>s.Lifecycle!=StructureLifecycle.Retired).SelectMany(s=>ForInstance(s.Id));
        public void Reconcile()
        {
            poses.Clear();
            if(Current.Game?.World==null||Find.WorldGrid==null)return;
            foreach(var layer in Find.WorldGrid.PlanetLayers.Values)
            {
                if(!ArtificialSurfaceLayers.TryDescribe(layer,out var surface))continue;
                var a=manager.Backends.Associations.FirstOrDefault(a=>!a.Released&&a.Backend==surface.Backend);
                if(a==null){surface.SetDisabledModules(Array.Empty<string>());continue;}
                var instance=manager.GetSnapshot(a.Node.InstanceId);var node=instance?.Nodes.FirstOrDefault(n=>n.Id==a.Node.NodeId);
                surface.SetDisabledModules(node==null||!node.Enabled?SurfaceModules.Handlers:SurfaceModules.Handlers.Where(handler=>!node.Capabilities.Any(c=>c.HandlerId==handler&&manager.Runtime.CheckCapability(instance!,node.Id,c).Success)));
                var address=new SurfaceAddress(a.Node,surface.SurfaceId);
                var binding=manager.Runtime.SurfaceState.FirstOrDefault(s=>s.Address==address);
                if(binding==null){binding=new(){Address=address};manager.Runtime.SurfaceState.Add(binding);}
                binding.Backend=a.Backend;binding.Ownership=a.Ownership;binding.AssociationGeneration=a.Generation;binding.BlockedReason=a.BlockedReason;
            }
        }
        public bool TryResolve(SurfaceAddress address,out PlanetLayer layer,out ArtificialSurfaceDescriptor description)
        {
            layer=null!;description=null!;
            var association=manager.Backends.Associations.FirstOrDefault(a=>!a.Released&&a.Node==address.Node);
            if(association==null||Current.Game?.World==null||Find.WorldGrid==null)return false;
            foreach(var candidate in Find.WorldGrid.PlanetLayers.Values)
                if(ArtificialSurfaceLayers.TryDescribe(candidate,out var surface)&&surface.Backend==association.Backend&&surface.SurfaceId==address.SurfaceId)
                {if(!surface.ModuleEnabled(SurfaceModules.Layer))return false;layer=candidate;description=surface;return true;}
            return false;
        }
        public IReadOnlyList<SurfaceAddress> ForInstance(string instanceId)
        {
            var result=new List<SurfaceAddress>();
            if(Current.Game?.World==null||Find.WorldGrid==null)return result;
            foreach(var a in manager.Backends.Associations.Where(a=>!a.Released&&a.Node.InstanceId==instanceId))
                foreach(var layer in Find.WorldGrid.PlanetLayers.Values)
                    if(ArtificialSurfaceLayers.TryDescribe(layer,out var s)&&s.Backend==a.Backend)result.Add(new(a.Node,s.SurfaceId));
            return result;
        }
        public FrameworkResult Evaluate(SurfaceCellAddress address,double physicsTick,out SurfaceCellState state)
        {
            state=default;if(!TryResolve(address.Surface,out _,out var surface))return FrameworkResult.Reject("SurfaceMissing");
            if(address.CellId<0||address.CellId>=surface.Geometry.CellCount)return FrameworkResult.Reject("CellMissing");
            var anchor=manager.Spatial.Evaluate(address.Surface.Node,physicsTick);if(!anchor.Success)return FrameworkResult.Reject(anchor.Code);
            var motion=surface.ModuleEnabled(SurfaceModules.Motion)?surface.Motion:null;
            if(!poses.TryGetValue(address.Surface,out var cached)||cached.Anchor!=anchor.State||!ReferenceEquals(cached.Motion,motion))
            {
                cached=(anchor.State,motion,new SurfacePoseTransform(anchor.State,motion,physicsTick,surface.ParentAxialPose));poses[address.Surface]=cached;
            }
            state=cached.Transform.Evaluate(surface.Geometry.EvaluateLocal(address.CellId));
            return FrameworkResult.Ok();
        }
    }
}
