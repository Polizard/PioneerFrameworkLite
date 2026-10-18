using System;
using System.Collections.Generic;
using PioneerFramework.Megastructures;
using PioneerFramework.RingWorld;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    public sealed class ArtificialSurfaceGridTests
    {
        private sealed class Quad:IArtificialSurfaceGeometry
        {
            public int CellCount=>2;
            public double AverageCellSizeMeters=>100000;
            public void GetNeighbors(int cell,List<int> result){result.Clear();result.Add(1-cell);}
            public double Distance(int a,int b)=>Math.Abs(a-b);
            public IReadOnlyList<DVector3> GetPolygon(int cell)=>new[]{new DVector3(cell*100000,0,0),new DVector3((cell+1)*100000,0,0),new DVector3((cell+1)*100000,0,100000),new DVector3(cell*100000,0,100000)};
            public SurfaceLocalFrame EvaluateLocal(int cell)=>new(new(cell*100000+50000,0,50000),new(0,1,0),new(1,0,0));
        }
        [Fact]
        public void NativeBridgeSupportsQuadAndReinitialization()
        {
            var def=new PlanetLayerDef{defName="Quad",tileType=typeof(SurfaceTile)};
            using var layer=new SurfaceLayer(9,def,100,Vector3.zero,Vector3.forward,180,0,0,0,0);
            PlanetLayerGridBridge.Initialize(layer,new Quad());
            Assert.Equal(8,layer.UnsafeVerts.Length);Assert.Equal(4,layer.UnsafeTileIDToVerts_offsets[1]);
            PlanetLayerGridBridge.Initialize(layer,new Quad());Assert.Equal(8,layer.UnsafeVerts.Length);
        }
        public sealed class FailingDrawLayer:WorldDrawLayer
        {
            public FailingDrawLayer()=>throw new InvalidOperationException("Injected draw-layer initialization failure.");
        }
        [Fact]
        public void NativeBridgeReleasesInstalledArraysWhenLaterInitializationFails()
        {
            var def=new PlanetLayerDef{defName="FailingQuad",tileType=typeof(SurfaceTile),worldDrawLayers=new(){typeof(FailingDrawLayer)}};
            using var layer=new SurfaceLayer(9,def,100,Vector3.zero,Vector3.forward,180,0,0,0,0);
            Assert.Throws<System.Reflection.TargetInvocationException>(()=>PlanetLayerGridBridge.Initialize(layer,new Quad()));
            Assert.False(layer.UnsafeVerts.IsCreated);Assert.False(layer.UnsafeTileIDToVerts_offsets.IsCreated);
            Assert.False(layer.UnsafeTileIDToNeighbors_offsets.IsCreated);Assert.False(layer.UnsafeTileIDToNeighbors_values.IsCreated);
            Assert.Empty(layer.WorldDrawLayers);
        }
        [Fact]
        public void NativeRingDescriptorNeedsNoManagerAndDoesNotChangeIdentity()
        {
            using var ring=new RingSurfaceLayer(7,new PlanetLayerDef{defName="PF_RingWorld",tileType=typeof(SurfaceTile)},140,Vector3.zero,Vector3.forward,180,0,0,0,0);
            var parameters=new RingParameters("old/ring",1,123,256,3,14000000);
            ring.Configure(parameters);ring.InitializeRing();
            Assert.True(ArtificialSurfaceLayers.TryDescribe(ring,out var surface));
            Assert.Equal(parameters.RingId,surface.Backend.StableId);Assert.Equal(768,surface.Geometry.CellCount);
            Assert.Equal(parameters,ring.Parameters);Assert.Equal(7,ring.LayerID);
            Assert.False(ArtificialSurfaceLayers.TryDescribe(new SurfaceLayer(),out _));
        }
    }
}
