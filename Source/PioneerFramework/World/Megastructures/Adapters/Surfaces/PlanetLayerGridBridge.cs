using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using Unity.Collections;
using UnityEngine;
using Verse;

namespace PioneerFramework.Megastructures
{
    public static class PlanetLayerGridBridge
    {
        private static readonly Dictionary<string, FieldInfo> Fields = new Dictionary<string, FieldInfo>();
        internal static FieldInfo Field(string name)
        {
            if (!Fields.TryGetValue(name, out FieldInfo? field))
                Fields[name] = field = AccessTools.Field(typeof(PlanetLayer), name)
                    ?? throw new MissingFieldException(typeof(PlanetLayer).FullName, name);
            return field;
        }
        public static void Initialize(PlanetLayer layer,IArtificialSurfaceGeometry geometry)
        {
            foreach (string name in new[] { "verts", "tileIDToVerts_offsets", "tileIDToNeighbors_offsets", "tileIDToNeighbors_values", "averageTileSize", "finder", "pather", "filler" }) _ = Field(name);
            layer.Dispose(); layer.WorldDrawLayers.Clear();
            try
            {
                var data=SurfaceGridData.Build(geometry);
                var vertexData=new Vector3[data.Vertices.Length];
                for(int i=0;i<vertexData.Length;i++)vertexData[i]=(Vector3)(data.Vertices[i]*SpaceConstants.UnitsPerMeter);
                var values=new PlanetTile[data.Neighbors.Length];
                for(int i=0;i<values.Length;i++)values[i]=new PlanetTile(data.Neighbors[i],layer);
                Install(layer,"verts",vertexData);
                Install(layer,"tileIDToVerts_offsets",data.VertexOffsets);
                Install(layer,"tileIDToNeighbors_offsets",data.NeighborOffsets);
                Install(layer,"tileIDToNeighbors_values",values);
                Field("averageTileSize").SetValue(layer,(float)(geometry.AverageCellSizeMeters*SpaceConstants.UnitsPerMeter));
                foreach (Type type in layer.Def.worldDrawLayers)
                {
                    var drawLayer = (WorldDrawLayer)Activator.CreateInstance(type)!;
                    drawLayer.Initialize(layer); layer.WorldDrawLayers.Add(drawLayer);
                }
                Field("finder").SetValue(layer, new FastTileFinder(layer));
                Field("pather").SetValue(layer, new WorldPathing(layer));
                Field("filler").SetValue(layer, new WorldFloodFiller(layer));
            }
            catch { layer.Dispose(); layer.WorldDrawLayers.Clear(); throw; }
        }
        private static void Install<T>(PlanetLayer layer, string name, T[] data) where T : unmanaged
        {
            var native = new NativeArray<T>(data, Allocator.Persistent);
            try { Field(name).SetValue(layer, native); }
            catch { native.Dispose(); throw; }
        }
    }
}
