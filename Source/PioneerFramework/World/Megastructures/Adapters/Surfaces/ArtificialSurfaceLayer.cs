using RimWorld.Planet;
using System.Linq;
using Verse;
using System.Collections.Generic;
using System;

namespace PioneerFramework.Megastructures
{
    /// <summary>Native surface discovery works before any framework manager exists or imports a layer.</summary>
    public interface IArtificialSurfaceLayer
    {
        ArtificialSurfaceDescriptor Surface { get; }
    }
    public sealed class ArtificialSurfaceDescriptor
    {
        private readonly HashSet<string> disabledModules=new();
        private PeriodicMotionSample? motionSample;
        public bool ModuleEnabled(string handler)=>!disabledModules.Contains(handler);
        internal void SetDisabledModules(IEnumerable<string> handlers)
        {
            var next=handlers.ToArray();if(disabledModules.SetEquals(next))return;
            disabledModules.Clear();disabledModules.UnionWith(next);motionSample=null;
        }
        public PeriodicMotionSample GetMotionSample(double physicsTick)
        {
            if(!double.IsFinite(physicsTick))throw new ArgumentOutOfRangeException(nameof(physicsTick));
            if(motionSample?.Tick!=physicsTick)motionSample=Motion!=null&&ModuleEnabled(SurfaceModules.Motion)?Motion.SampleAt(physicsTick):PeriodicMotionSample.Identity(physicsTick);
            return motionSample;
        }
        public StructureBackendRef Backend { get; init; }
        public string SurfaceId { get; init; }="habitat";
        public int ParentBodyId { get; init; }
        public string ColonyNameKey { get; init; }="PF_Surface_ColonyName";
        public IArtificialSurfaceGeometry Geometry { get; init; }=null!;
        public PeriodicMotion? Motion { get; init; }
        public bool ParentAxialPose { get; init; }
        public ArtificialEnvironmentProfile? Environment { get; init; }
        public ISurfaceViewGeometry? View { get; set; }
        public Func<int,string,StructureCreationRequest>? ImportRequestFactory { get; init; }
    }
    public static class ArtificialSurfaceLayers
    {
        public static bool ModuleAvailable(PlanetLayer layer,string handler)
        {
            if(!TryDescribe(layer,out var surface))return false;
            return surface.ModuleEnabled(handler);
        }
        public static bool TryDescribe(PlanetLayer? layer,out ArtificialSurfaceDescriptor surface)
        {surface=(layer as IArtificialSurfaceLayer)?.Surface!;return surface!=null;}
    }
}
