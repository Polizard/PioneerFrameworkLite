using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PioneerFramework.RingWorld;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    public class RingWorldIntegrationTests
    {
        private static RingSurfaceLayer Create(string id="ring",int layerId=7,int columns=64,int rows=12)
        {
            var def=new PlanetLayerDef{defName="PF_RingWorld",layerType=typeof(RingSurfaceLayer),tileType=typeof(SurfaceTile)};
            var layer=new RingSurfaceLayer(layerId,def,140,Vector3.zero,Vector3.forward,180,0,0,0,0);
            layer.Configure(new RingParameters(id,1,123,columns,rows,14000000));
            return layer;
        }

        [Fact]
        public void NativeGridUsesActualLayerIdentityAndNoSentinelTile()
        {
            using var layer=Create(); layer.InitializeRing();
            Assert.Equal(768,layer.TilesCount);
            Assert.Equal(768,layer.UnsafeTileIDToVerts_offsets.Length);
            Assert.Equal(4608,layer.UnsafeVerts.Length);
            var neighbors=new List<PlanetTile>(); layer.GetTileNeighbors(320,neighbors);
            Assert.Contains(neighbors,t=>t.tileId==383);
            Assert.All(neighbors,t=>Assert.Equal(7,(int)AccessTools.Field(typeof(PlanetTile),"layerId").GetValue(t)));
            Assert.NotNull(layer.Pather); Assert.NotNull(layer.FastTileFinder); Assert.NotNull(layer.Filler);
            layer.InitializeRing();
            Assert.Equal(768,layer.TilesCount);
        }

        [Fact]
        public void InitialEnvironmentIsHabitableAndSurvivesRebuild()
        {
            using var layer=Create(); layer.InitializeRing();
            var biome=new BiomeDef{defName="TemperateForest"};
            layer.GenerateTiles(biome);
            Assert.Equal(768,layer.Tiles.Count);
            Assert.All(layer.Tiles,t=>{Assert.IsType<SurfaceTile>(t);Assert.Same(biome,t.PrimaryBiome);Assert.Equal(21,t.temperature);});
            layer[123].temperature=27;
            layer.InitializeRing();
            Assert.Equal(27,layer[123].temperature);
        }

        [Fact]
        public void SameDefinitionCanRepresentDistinctRingInstances()
        {
            using var first=Create("first",7); using var second=Create("second",8);
            Assert.NotEqual(first.Parameters.RingId,second.Parameters.RingId);
            Assert.Equal(1,first.Parameters.ParentBodyId); Assert.Equal(1,second.Parameters.ParentBodyId);
            Assert.Same(second,RingWorldService.FindById(new[]{first,second},"second"));
            Assert.Throws<InvalidOperationException>(()=>RingWorldService.RequireMatching(first,second.Parameters));
        }

        [Fact]
        public void RestoredParametersDetermineGridBeforeInitialization()
        {
            using var layer=Create(columns:32,rows:8);
            RingWorldSaveData data=RingWorldSaveData.From(layer.Parameters);
            using var restored=new RingSurfaceLayer(9,layer.Def,140,Vector3.zero,Vector3.forward,180,0,0,0,0);
            restored.Configure(data.ToParameters()); restored.InitializeRing();
            Assert.Equal(256,restored.TilesCount);
            Assert.Equal(layer.Parameters,restored.Parameters);
            data.formatVersion=99;
            Assert.Throws<NotSupportedException>(()=>data.ToParameters());
        }

        [Fact]
        public void RingValidationRejectsInsidePlanetAndOrdinaryLayerIsNotHidden()
        {
            var parameters=new RingParameters("x",1,0,64,12,14000000);
            Assert.False(RingWorldService.ValidateParent(parameters,15000000,out _));
            Assert.True(RingWorldService.ValidateParent(parameters,10000000,out _));
            Assert.False(RingWorldPatches.ShouldHide(new SurfaceLayer(),new SurfaceLayer()));
        }

        [Fact]
        public void TileQueryDistanceCrossesSeamAndDoesNotWrapAcrossWidth()
        {
            using var layer=Create();
            int[] candidates=RingWorldQueries.SelectByDistance(layer,320,1,1).ToArray();
            Assert.Equal(6,candidates.Length); Assert.Contains(383,candidates); Assert.DoesNotContain(320,candidates);
            Assert.DoesNotContain(64*11,RingWorldQueries.SelectByDistance(layer,0,0,1));
        }

        [Fact]
        public void HarmonyInitializesRingThroughActualVanillaEntryPoint()
        {
            RingWorldPatches.Install();
            using var layer=Create(); layer.InitializeLayer();
            Assert.Equal(768,layer.TilesCount);
            Assert.Equal(1,layer.ApproxDistanceInTiles(320,383));
            Assert.Equal(270,layer.GetHeadingFromTo(320,383));
            Vector3 center=layer.GetTileCenter(320);
            Assert.InRange(Math.Sqrt(center.x*center.x+center.z*center.z),279.99,280.01);
            Assert.Equal(280,layer.Radius);
        }

        [Fact]
        public void NativeDrawAndColliderRotationUseSameQuarterTurnAsTileGeometry()
        {
            Type motion=typeof(RingSurfaceLayer).Assembly.GetType("PioneerFramework.RingWorld.RingWorldMotion",true);
            var rotation=(Quaternion)AccessTools.Method(motion,"RotationAt").Invoke(null,new object[]{15000d});
            Vector3 actual=rotation*new Vector3(280,17,0);
            Assert.InRange(actual.x,-.001f,.001f);
            Assert.InRange(actual.z,279.999f,280.001f);
            Assert.InRange(actual.y,16.999f,17.001f);
            DVector3 pure=RingRotation.ToView(new DVector3(280,17,0),15000);
            Assert.InRange((actual-(Vector3)pure).magnitude,0,.001f);
        }

        public sealed class FailingDrawLayer : WorldDrawLayer
        {
            public FailingDrawLayer() {throw new InvalidOperationException("draw initialization failed");}
        }

        [Fact]
        public void FailedInitializationReleasesNativeBuffersAndCanBeRetried()
        {
            using var layer=Create(); layer.Def.worldDrawLayers.Add(typeof(FailingDrawLayer));
            Assert.ThrowsAny<Exception>(()=>layer.InitializeRing());
            Assert.False(layer.UnsafeVerts.IsCreated);
            Assert.False(layer.UnsafeTileIDToNeighbors_offsets.IsCreated);
            Assert.False(layer.UnsafeTileIDToNeighbors_values.IsCreated);
            Assert.Empty(layer.WorldDrawLayers);
            layer.Def.worldDrawLayers.Clear(); layer.InitializeRing();
            Assert.Equal(768,layer.TilesCount);
        }
    }
}
