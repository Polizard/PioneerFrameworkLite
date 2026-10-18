using System;
using System.Collections.Generic;
using PFL.ArtificialSurfaceSample;
using PioneerFramework.Megastructures;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    public sealed class ArtificialSurfaceReuseTests
    {
        private static RectangularLayer Create()
        {
            var def=new PlanetLayerDef{defName="sample",tileType=typeof(SurfaceTile)};
            var layer=new RectangularLayer(12,def,20,new Vector3(7,11,13),Vector3.forward,180,0,0,0,0);
            layer.Configure("fixture",1,3,2);return layer;
        }
        [Fact]
        public void SeparateAssemblyReusesNativeGridNavigationEnvironmentAndWorldPlacement()
        {
            using var layer=Create();PlanetLayerGridBridge.Initialize(layer,layer.Surface.Geometry);
            Assert.Equal(24,layer.UnsafeVerts.Length);
            Assert.Equal(new[]{0,1,2,5},SurfacePathSearch.Find(layer.Surface.Geometry,0,5).Nodes);
            Assert.Equal(new[]{0,1,3},SurfaceQueryService.SelectByDistance(layer.Surface.Geometry,0,0,1));
            var initial=SurfaceEnvironmentBridge.CreateInitialTile(new PlanetTile(0,layer),new BiomeDef(),layer.Surface.Environment!);
            Assert.Equal(21,initial.temperature);Assert.Equal(1200,initial.rainfall);
            var center=SurfaceWorldObjectBridge.CellPoint(layer,0);
            var local=layer.Surface.Geometry.EvaluateLocal(0).PositionMeters;
            Assert.InRange((center-layer.Origin-(Vector3)(local*SpaceConstants.UnitsPerMeter)).magnitude,0,1e-6);
            Assert.True(StructureActionService.Validate(layer.Surface.Geometry).Success);
        }
        [Fact]
        public void QuadMeshUsesSharedPickingPolicyAndCameraUsesSharedMotion()
        {
            var mesh=new LayerSubMesh(null,null);
            int mappings=0;
            SurfaceMeshParts.AddCellFan(mesh,0,4,SurfaceMeshRole.Habitat,()=>mappings++);
            Assert.Equal(4,mappings);Assert.Equal(12,mesh.tris.Count);
            SurfaceMeshParts.AddCellFan(mesh,0,4,SurfaceMeshRole.Glow,()=>mappings++);
            Assert.Equal(4,mappings);Assert.False(SurfaceMeshParts.CanPick(SurfaceMeshRole.Decoration));
            var motion=new PeriodicMotion(new DVector3(0,-1,0),60000,777,.3);
            var follow=new SurfaceCameraFollow();var initial=new DVector3(1,0,0);
            Assert.Equal(initial,follow.Update(12,true,initial,100,motion));
            Assert.InRange((follow.Update(12,true,initial,15100,motion)-new DVector3(0,0,1)).magnitude,0,1e-12);
            follow.Reset();Assert.Equal(initial,follow.Update(12,true,initial,16000,motion));
        }
        [Fact]
        public void SurfaceModulesValidateCompositionAndRejectDisabledLayerDependencies()
        {
            var registry=new MegastructureCapabilityRegistry();SurfaceModules.Register(registry);
            var capabilities=new List<CapabilityState>();
            foreach(string handler in SurfaceModules.Handlers)capabilities.Add(new(){Id=handler,HandlerId=handler});
            Assert.True(registry.Validate(capabilities).Success);
            capabilities[0].Enabled=false;Assert.Equal("CapabilityDependency",registry.Validate(capabilities).Code);
        }
        [Fact]
        public void NativeConsumersShareOneImmutableMotionSamplePerPhysicsTime()
        {
            using var layer=Create();
            var first=layer.Surface.GetMotionSample(15000);
            Assert.Same(first,layer.Surface.GetMotionSample(15000));
            Assert.NotSame(first,layer.Surface.GetMotionSample(30000));
            var local=new DVector3(20,3,0);
            Assert.Equal(PioneerFramework.RingWorld.RingRotation.ToView(local,15000),first.Rotate(local));
            using var other=Create();Assert.NotSame(first,other.Surface.GetMotionSample(15000));
        }
    }
}
