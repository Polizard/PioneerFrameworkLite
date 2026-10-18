using System.Linq;

namespace PioneerFramework.Megastructures
{
    /// <summary>Metadata-only migration. Native layers and maps are never created during restoration.</summary>
    public static class SurfaceBindingMigration
    {
        public static void AdoptLegacyBackend(MegastructureRuntime runtime,StructureBackendService backends,string legacyKind,string providerId)
        {
            foreach(var instance in runtime.Snapshots.Where(s=>s.Lifecycle!=StructureLifecycle.Retired))
                foreach(var node in instance.Nodes)
                    foreach(var binding in node.Bindings.Where(b=>b.Kind==legacyKind))
                    {
                        backends.AdoptLegacy(new(instance.Id,node.Id),new(providerId,binding.Id),binding.Ownership);
                        var target=runtime.Find(instance.Id)!.Nodes.First(n=>n.Id==node.Id);
                        foreach(string handler in SurfaceModules.Handlers)
                        {
                            if(target.Capabilities.Any(c=>c.HandlerId==handler))continue;
                            string key="migrated_"+handler.Replace('.','_');int suffix=0;
                            while(target.Capabilities.Any(c=>c.Id==key))key="migrated_"+handler.Replace('.','_')+"_"+(++suffix).ToString(System.Globalization.CultureInfo.InvariantCulture);
                            target.Capabilities.Add(new(){Id=key,HandlerId=handler,LastTick=runtime.CurrentTick});
                        }
                    }
        }
    }
}
