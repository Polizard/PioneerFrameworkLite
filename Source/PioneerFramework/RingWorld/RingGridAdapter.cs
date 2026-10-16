using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using Unity.Collections;
using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    internal static class RingGridAdapter
    {
        private static readonly Dictionary<string, FieldInfo> Fields = new Dictionary<string, FieldInfo>();
        internal static FieldInfo Field(string name)
        {
            if (!Fields.TryGetValue(name, out FieldInfo? field))
                Fields[name] = field = AccessTools.Field(typeof(PlanetLayer), name)
                    ?? throw new MissingFieldException(typeof(PlanetLayer).FullName, name);
            return field;
        }
        internal static void Initialize(RingSurfaceLayer layer)
        {
            foreach (string name in new[] { "verts", "tileIDToVerts_offsets", "tileIDToNeighbors_offsets", "tileIDToNeighbors_values", "averageTileSize", "finder", "pather", "filler" }) _ = Field(name);
            layer.Dispose(); layer.WorldDrawLayers.Clear();
            try
            {
                int count = layer.Topology.TileCount;
                var neighbors = new List<int>(6);
                var offsets = new int[count]; var values = new List<PlanetTile>(count * 6);
                for (int i = 0; i < count; i++)
                {
                    offsets[i] = values.Count; layer.Topology.GetNeighbors(i, neighbors);
                    foreach (int neighbor in neighbors) values.Add(new PlanetTile(neighbor, layer));
                }
                var vertexData = new Vector3[count * 6]; var vertexOffsets = new int[count];
                for (int i = 0; i < count; i++)
                {
                    vertexOffsets[i] = i * 6; DVector3[] corners = layer.Geometry.Vertices(i);
                    for (int j = 0; j < 6; j++) vertexData[i * 6 + j] = (Vector3)corners[j];
                }
                Install(layer, "verts", vertexData);
                Install(layer, "tileIDToVerts_offsets", vertexOffsets);
                Install(layer, "tileIDToNeighbors_offsets", offsets);
                Install(layer, "tileIDToNeighbors_values", values.ToArray());
                Field("averageTileSize").SetValue(layer, (float)layer.Geometry.Spacing);
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
