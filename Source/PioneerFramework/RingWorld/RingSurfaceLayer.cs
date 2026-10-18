using System;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    public sealed class RingSurfaceLayer : SurfaceLayer, Megastructures.IArtificialSurfaceLayer, Megastructures.IArtificialSurfaceSaveHeader
    {
        private RingWorldSaveData? saveData;
        public Megastructures.ArtificialSurfaceDescriptor Surface {get;private set;}=null!;
        public RingParameters Parameters { get; private set; } = null!;
        public RingTopology Topology { get; private set; } = null!;
        public RingGeometry Geometry { get; private set; } = null!;
        public RingSurfaceLayer() { }
        public RingSurfaceLayer(int layerId, PlanetLayerDef def, float radius, Vector3 origin,
            Vector3 viewCenter, float viewAngle, int subdivisions, float extraCameraAltitude,
            float backgroundWorldCameraOffset, float backgroundWorldCameraParallaxDistance)
            : base(layerId, def, radius, origin, viewCenter, viewAngle, subdivisions, extraCameraAltitude,
                backgroundWorldCameraOffset, backgroundWorldCameraParallaxDistance)
        {
            if (RingWorldService.PendingParameters != null) Configure(RingWorldService.PendingParameters);
        }
        public void Configure(RingParameters parameters)
        {
            if (TilesCount > 0 && Parameters != parameters)
                throw new InvalidOperationException("An initialized ring cannot change its tile identity.");
            Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            Surface=new(){ImportRequestFactory=(actor,id)=>RingBackendProvider.Build(parameters,actor,id,true),ColonyNameKey="PF_RingColonyName",Backend=new(RingBackendProvider.ProviderId,parameters.RingId),ParentBodyId=parameters.ParentBodyId,ParentAxialPose=true,Geometry=new RingSurfaceGeometryProvider(parameters),Motion=new(new DVector3(0,-1,0),60000),Environment=new()};
            Topology = new RingTopology(parameters.Columns, parameters.Rows);
            Geometry = new RingGeometry(Topology, parameters.RadiusMeters * SpaceConstants.UnitsPerMeter);
            Surface.View=new RingViewGeometryAdapter(Geometry);
            RingGridAdapter.Field("radius").SetValue(this, (float)Geometry.Radius);
        }
        public void InitializeRing()
        {
            if (Parameters == null) throw new InvalidOperationException("Ring parameters must be restored before grid initialization.");
            RingGridAdapter.Initialize(this);
        }
        internal void ExposeRingParameters()
        {
            if (Scribe.mode == LoadSaveMode.Saving) saveData = RingWorldSaveData.From(Parameters);
            Scribe_Deep.Look(ref saveData, "pfRingWorld");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (saveData == null) throw new InvalidOperationException("Ring save header is missing.");
                Configure(saveData.ToParameters());
            }
        }
        void Megastructures.IArtificialSurfaceSaveHeader.ExposeSurfaceHeader()=>ExposeRingParameters();
        public void GenerateTiles(BiomeDef biome)
        {
            tiles.Clear();
            for (int i = 0; i < Topology.TileCount; i++)
                tiles.Add(Megastructures.SurfaceEnvironmentBridge.CreateInitialTile(new PlanetTile(i,this),biome,Surface.Environment!));
        }
    }
}
