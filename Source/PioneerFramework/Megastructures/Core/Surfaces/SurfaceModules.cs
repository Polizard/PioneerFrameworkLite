using System;

namespace PioneerFramework.Megastructures
{
    public sealed class SurfaceModuleData
    {
        public string SurfaceId { get; set; }="habitat";
    }
    public static class SurfaceModules
    {
        public const string Layer="pfl.surface.layer",Motion="pfl.motion.periodic",Navigation="pfl.surface.navigation",Map="pfl.map.surface",Environment="pfl.environment.artificial",Presentation="pfl.presentation.surface",View="pfl.view.surface";
        public static readonly string[] Handlers={Layer,Motion,Navigation,Map,Environment,Presentation,View};
        public static void Register(MegastructureCapabilityRegistry registry)
        {
            foreach(string handler in Handlers)
                registry.Register(new(handler,()=>new SurfaceModule(),requires:handler==Layer?Array.Empty<string>():new[]{Layer}){AllowInstance=false});
        }
        // Modules provide on-demand services. They do not integrate motion, generate maps or rebuild meshes on a timer.
        private sealed class SurfaceModule:IStructureCapability
        {
            public FrameworkResult Validate(string payload)
            {
                try{var data=MegastructureCodec.Read<SurfaceModuleData>(payload);return FrameworkValidation.StableId(data.SurfaceId)?FrameworkResult.Ok():FrameworkResult.Reject("InvalidSurfaceId");}
                catch(Exception e) when(e is System.Text.Json.JsonException or FormatException){return FrameworkResult.Reject("InvalidPayload");}
            }
            public string Advance(CapabilityContext context,string payload,long elapsedTicks)=>payload;
        }
    }
}
