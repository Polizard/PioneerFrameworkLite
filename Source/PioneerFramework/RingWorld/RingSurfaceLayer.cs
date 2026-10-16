using System;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    public sealed class RingSurfaceLayer : SurfaceLayer
    {
        private RingWorldSaveData? saveData;
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
            Topology = new RingTopology(parameters.Columns, parameters.Rows);
            Geometry = new RingGeometry(Topology, parameters.RadiusMeters * SpaceConstants.UnitsPerMeter);
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
        public void GenerateTiles(BiomeDef biome)
        {
            tiles.Clear();
            for (int i = 0; i < Topology.TileCount; i++)
                tiles.Add(new SurfaceTile(new PlanetTile(i, this))
                {
                    PrimaryBiome = biome, elevation = 100, hilliness = Hilliness.Flat,
                    temperature = 21, rainfall = 1200, swampiness = 0, pollution = 0
                });
        }
    }
}
