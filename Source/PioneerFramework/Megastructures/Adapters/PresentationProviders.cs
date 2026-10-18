using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace PioneerFramework.Megastructures
{
    public sealed record StructurePresentation(NodeAddress Address,SpatialState State,double SizeMeters,string Label)
    {public Mesh? Mesh { get; init; }public Material? Material { get; init; }public Color Color { get; init; }=Color.cyan;}
    public interface IMegastructurePresentationProvider
    {IEnumerable<StructurePresentation> Query(MegastructureManager manager,double physicsTick);}
    public sealed partial class MegastructureManager
    {
        private readonly List<IMegastructurePresentationProvider> presentationProviders=new();
        private double presentationTick=double.NaN;private long presentationRevision=-1;
        private IReadOnlyList<StructurePresentation> presentationCache=Array.Empty<StructurePresentation>();
        public void RegisterPresentationProvider(IMegastructurePresentationProvider provider){presentationProviders.Add(provider);presentationTick=double.NaN;}
        public IReadOnlyList<StructurePresentation> Presentations(double physicsTick)
        {
            EnsureInitialized();long revision=runtime.LiveInstances.Sum(i=>i.Revision)+runtime.LiveInstances.Count();
            if(presentationTick==physicsTick&&presentationRevision==revision)return presentationCache;
            var result=new List<StructurePresentation>();
            foreach(var instance in runtime.LiveInstances)
            {
                if(instance.Lifecycle!=StructureLifecycle.Active||!string.IsNullOrEmpty(instance.BlockedReason))continue;
                foreach(var node in instance.Nodes)
                {
                    if(!node.Enabled||node.Spatial.Mode==SpatialMode.PhysicalGroup||!node.Capabilities.Any(c=>c.HandlerId=="pfl.presentation"&&runtime.CheckCapability(instance,node.Id,c).Success))continue;
                    var pose=Spatial.Evaluate(new(instance.Id,node.Id),physicsTick);if(!pose.Success)continue;
                    result.Add(new(new(instance.Id,node.Id),pose.State,10000,instance.Id+"/"+node.Id));
                }
            }
            foreach(var provider in presentationProviders)
                try {result.AddRange(provider.Query(this,physicsTick));}catch(Exception e){Log.Error("[PFL Megastructures] Presentation provider: "+e);}
            presentationTick=physicsTick;presentationRevision=revision;presentationCache=result.ToArray();return presentationCache;
        }
    }
}
