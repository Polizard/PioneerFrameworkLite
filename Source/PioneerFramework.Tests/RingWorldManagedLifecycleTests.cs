using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.IO;
using HarmonyLib;
using PFL.ArtificialSurfaceSample;
using PioneerFramework.Megastructures;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    [Collection("Ring Scribe")]
    public sealed class RingWorldManagedLifecycleTests
    {
        private sealed class Context:IDisposable
        {
            private readonly Game previous=Current.Game;
            private readonly ProgramState state=Current.ProgramState;
            public readonly RectangularLayer Layer;
            public readonly MegastructureManager Manager;
            public readonly WorldGrid Grid=new();
            public Context(bool layerOnly=false)
            {
                var game=(Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
                AccessTools.Field(typeof(Game),"maps").SetValue(game,new List<Map>());
                var world=(World)RuntimeHelpers.GetUninitializedObject(typeof(World));world.grid=Grid;world.worldObjects=new WorldObjectsHolder();
                AccessTools.Field(typeof(Game),"worldInt").SetValue(game,world);
                Manager=new MegastructureManager(game);game.components=new(){Manager};Current.Game=game;Current.ProgramState=ProgramState.Playing;
                Layer=new RectangularLayer(19,new PlanetLayerDef{defName="sample",tileType=typeof(SurfaceTile)},20,Vector3.zero,Vector3.forward,180,0,0,0,0);
                Layer.Configure("fixture/raw",1,3,2);PlanetLayerGridBridge.Initialize(Layer,Layer.Surface.Geometry);
                var tiles=(List<Tile>)AccessTools.Field(typeof(PlanetLayer),"tiles").GetValue(Layer);
                for(int cell=0;cell<6;cell++)tiles.Add(new SurfaceTile(new PlanetTile(cell,Layer)));
                ((Dictionary<int,PlanetLayer>)Grid.PlanetLayers).Add(19,Layer);
                Manager.Runtime.RegisterDefinition(new(){Id="PF_RectangularSample",BackendProviderId=RectangularBackend.ProviderId,Nodes={new(){Id="surface",Capabilities=(layerOnly?new[]{SurfaceModules.Layer}:SurfaceModules.Handlers).Select((id,i)=>new CapabilityState{Id="module"+i,HandlerId=id}).ToList()}}});
                Manager.Backends.Register(new RectangularBackend());
            }
            public FrameworkResult Import()
            {
                var request=RectangularBackend.Request("fixture/raw",1,"managed",7,true);
                request.Structure.SpatialOverrides["surface"]=new(){Mode=SpatialMode.FrameFixed,FrameId=1};
                return Manager.Create(request);
            }
            public Map AddMap(int cell,int parentId,int mapId,PlanetLayer layer=null)
            {
                // Only the unrelated world-object command texture needs a headless bootstrap.
                var bootstrap=new Harmony("pfl.tests.surface.texture-bootstrap");
                bootstrap.Patch(AccessTools.Method(typeof(ContentFinder<Texture2D>),"Get"),prefix:new HarmonyMethod(typeof(RingWorldManagedLifecycleTests),nameof(SkipTextureLoad)));
                try{RuntimeHelpers.RunClassConstructor(typeof(WorldObject).TypeHandle);}finally{bootstrap.UnpatchAll(bootstrap.Id);}
                var player=(Faction)RuntimeHelpers.GetUninitializedObject(typeof(Faction));player.loadID=7;
                var factions=(FactionManager)RuntimeHelpers.GetUninitializedObject(typeof(FactionManager));AccessTools.Field(typeof(FactionManager),"ofPlayer").SetValue(factions,player);
                Current.Game.World.factionManager=factions;
                var parent=(Settlement)RuntimeHelpers.GetUninitializedObject(typeof(Settlement));parent.ID=parentId;
                AccessTools.Field(typeof(WorldObject),"tile").SetValue(parent,new PlanetTile(cell,layer??Layer));
                AccessTools.Field(typeof(WorldObject),"factionInt").SetValue(parent,player);
                Current.Game.World.worldObjects.AllWorldObjects.Add(parent);Current.Game.World.worldObjects.MapParents.Add(parent);
                var map=(Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));map.uniqueID=mapId;
                map.info=new MapInfo{parent=parent};Current.Game.Maps.Add(map);
                return map;
            }
            public void Dispose(){Current.Game=previous;Current.ProgramState=state;Layer.Dispose();}
        }
        private static bool SkipTextureLoad()=>false;
        private static void ThrowHeadlessLog(string text)=>throw new InvalidOperationException(text);
        private static bool ResolveFixtureType(string typeName,ref Type __result)
        {
            if(typeName!=typeof(PioneerFramework.RingWorld.RingWorldSaveData).FullName)return true;
            __result=typeof(PioneerFramework.RingWorld.RingWorldSaveData);return false;
        }
        [Fact]
        public void NativeDescriptorsDoNotImplicitlyImportAndDetachedBackendCanBeImportedAgain()
        {
            using var context=new Context();
            Assert.True(ArtificialSurfaceLayers.TryDescribe(context.Layer,out _));Assert.Empty(context.Manager.Snapshots);
            Assert.True(context.Import().Success);Assert.Single(context.Manager.Snapshots);Assert.True(context.Import().Success);
            Assert.Equal(BindingOwnership.Borrowed,Assert.Single(context.Manager.Backends.Associations).Ownership);
            Assert.True(context.Manager.RetireBackend("detach","managed",7,BackendDisposition.DetachKeepBackend).Success);
            Assert.True(context.Grid.PlanetLayers.ContainsKey(19));Assert.Equal(StructureLifecycle.Retired,context.Manager.GetSnapshot("managed").Lifecycle);
            var result=context.Manager.ImportSurface(context.Layer,7);Assert.True(result.Success,result.Code);Assert.NotEqual("managed",result.InstanceId);
            Assert.Equal(2,context.Manager.Snapshots.Count);
        }
        [Fact]
        public void ActionsRejectStaleSelectionsAndUnauthorizedMapAccess()
        {
            using var context=new Context();Assert.True(context.Import().Success);
            var surface=Assert.Single(context.Manager.Surfaces.ForInstance("managed"));
            Assert.Equal("PermissionDenied",context.Manager.MapSites.Access(StructureMapSiteAddress.ForCell(new(surface,0)),true,8,new(250,1,250),out _).Code);
            Assert.Equal("RevisionConflict",context.Manager.Actions.Execute(new(){OperationId="stale",Key="enter",Surface=surface,CellId=0,ActorFactionId=7,ExpectedRevision=999},out _).Code);
            Assert.True(context.Manager.Actions.Execute(new(){OperationId="validate",Key="validate",Surface=surface,ActorFactionId=7},out _).Success);
            Assert.False(context.Manager.Actions.Query(surface,8,0).Single(a=>a.Key=="destroy").Available);
            var invalidSize=context.Manager.MapSites.Access(StructureMapSiteAddress.ForCell(new(surface,0)),true,7,new(501,1,250),out _);
            Assert.False(invalidSize.Success);Assert.Contains("50..500",invalidSize.Detail);
        }
        [Fact]
        public void MissingNativeBackendBlocksActivationAndPreservesAssociation()
        {
            using var context=new Context();Assert.True(context.Import().Success);
            ((Dictionary<int,PlanetLayer>)context.Grid.PlanetLayers).Remove(19);context.Manager.Backends.Reconcile();
            Assert.Equal("BackendMissing",context.Manager.Backends.ActivationGuard(context.Manager.GetSnapshot("managed")));
            Assert.Equal("fixture/raw",Assert.Single(context.Manager.Backends.Associations).Backend.StableId);
        }
        [Fact]
        public void ExistingSettlementMapIsReusedAndMissingParentIsNeverReplaced()
        {
            using var context=new Context();Assert.True(context.Import().Success);
            var existing=context.AddMap(2,41,99);context.Manager.MapSites.Reconcile();
            var site=Assert.Single(context.Manager.MapSites.Sites);
            Assert.Equal(41,site.MapParentId);Assert.Equal(99,site.MapId);
            Assert.True(context.Manager.MapSites.Access(site.Address,true,7,new(250,1,250),out var first).Success);Assert.Same(existing,first);
            Current.Game.Maps.Reverse();
            Assert.True(context.Manager.MapSites.Access(site.Address,true,7,new(250,1,250),out var second).Success);Assert.Same(first,second);
            existing.info.parent.ID=42;context.Manager.MapSites.Reconcile();
            Assert.Equal(41,Assert.Single(context.Manager.MapSites.Sites).MapParentId);
            Assert.Equal("MapParentMissing",context.Manager.MapSites.Access(site.Address,true,7,new(250,1,250),out _).Code);
            Assert.Single(Current.Game.Maps);
        }
        [Fact]
        public void ManagedSurfaceOnlyRunsDeclaredModules()
        {
            using var context=new Context(layerOnly:true);Assert.True(context.Import().Success);
            Assert.True(context.Layer.Surface.ModuleEnabled(SurfaceModules.Layer));
            Assert.False(context.Layer.Surface.ModuleEnabled(SurfaceModules.Motion));
            Assert.False(context.Layer.Surface.ModuleEnabled(SurfaceModules.Map));
            Assert.False(context.Layer.Surface.ModuleEnabled(SurfaceModules.Environment));
            var surface=Assert.Single(context.Manager.Surfaces.ForInstance("managed"));
            Assert.False(context.Manager.Actions.Query(surface,7,0).Single(a=>a.Key=="enter").Available);
        }
        [Fact]
        public void BoundStructuralModulesAndNodeIdentityCannotBeChangedIndividually()
        {
            using var context=new Context();Assert.True(context.Import().Success);
            foreach(string capability in new[]{"module0","module1","module5"})
                Assert.Equal("StructuralSurfaceBound",context.Manager.Execute(new(){OperationId="disable/"+capability,InstanceId="managed",NodeId="surface",ActorFactionId=7,Kind=StructureCommandKind.SetCapabilityEnabled,CapabilityId=capability,Enabled=false}).Code);
            Assert.Equal("StructuralSurfaceBound",context.Manager.Execute(new(){OperationId="disable-node",InstanceId="managed",NodeId="surface",ActorFactionId=7,Kind=StructureCommandKind.SetNodeEnabled,Enabled=false}).Code);
            Assert.Equal("StructuralSurfaceBound",context.Manager.Execute(new(){OperationId="rebind",InstanceId="managed",NodeId="surface",ActorFactionId=7,Kind=StructureCommandKind.BindNode,Spatial=new(){Mode=SpatialMode.BodyAnchored,BodyId=2}}).Code);
            Assert.True(context.Manager.Execute(new(){OperationId="extra",InstanceId="managed",ActorFactionId=7,Kind=StructureCommandKind.AddNode,NodeTemplate=new(){Id="extra"}}).Success);
            Assert.Equal("StructuralSurfaceBound",context.Manager.Execute(new(){OperationId="remove",InstanceId="managed",NodeId="surface",ActorFactionId=7,Kind=StructureCommandKind.RemoveNode}).Code);
            Assert.True(context.Manager.Runtime.Restore(context.Manager.Runtime.Export()).Success);
        }
        [Fact]
        public void RingDefaultFactoryPreservesExtensionDefinitionAndOverrides()
        {
            using var context=new Context();
            var bodies=new CelestialBodyManager(Current.Game);
            AccessTools.Field(typeof(CelestialBodyManager),"bodiesById").SetValue(bodies,new Dictionary<int,CelestialBodyDef>{{1,new(){radius=6000000}}});
            AccessTools.Field(typeof(CelestialBodyManager),"initialized").SetValue(bodies,true);Current.Game.components.Add(bodies);
            var request=new CreateStructureRequest{OperationId="variant",InstanceId="custom-ring",DefinitionId="ExtensionRing",OwnerFactionId=7,SpatialOverrides={["facility"]=new(){Mode=SpatialMode.BodyAnchored,BodyId=1},["extra"]=new(){Mode=SpatialMode.FrameFixed,FrameId=2}}};
            var result=new PioneerFramework.RingWorld.RingBackendProvider().CreateDefault(request,out var configured);
            Assert.True(result.Success,result.Code);Assert.Equal("ExtensionRing",configured.Structure.DefinitionId);
            Assert.Equal(2,configured.Structure.SpatialOverrides.Count);Assert.Equal(2,configured.Structure.SpatialOverrides["extra"].FrameId);
            Assert.Equal(MegastructureCodec.Encode(request),MegastructureCodec.Encode(configured.Structure));
        }
        [Fact]
        public void RingProviderRejectsAnAnchorThatDisagreesWithItsNativeSurface()
        {
            var parameters=new PioneerFramework.RingWorld.RingParameters("ring",1,123,256,3,14000000);
            var provider=new PioneerFramework.RingWorld.RingBackendProvider();
            foreach(var binding in new[]{new SpatialBinding{Mode=SpatialMode.FrameFixed,BodyId=1,FrameId=2},new(){Mode=SpatialMode.BodyAnchored,BodyId=2},new(){Mode=SpatialMode.BodyAnchored,BodyId=1,Offset=new(1,0,0)},new(){Mode=SpatialMode.BodyAnchored,BodyId=1,Velocity=new(1,0,0)},new(){Mode=SpatialMode.BodyAnchored,BodyId=1,AngularVelocity=new(0,1,0)}})
            {
                var request=PioneerFramework.RingWorld.RingBackendProvider.Build(parameters,7,"bad-anchor",false);
                request.Structure.SpatialOverrides["facility"]=binding;
                Assert.Equal("SurfaceAnchorMismatch",provider.Validate(request).Code);
            }
        }
        [Fact]
        public void ColonizedRingHeaderRestoresBeforeGridAndManagerReusesNativeIdentities()
        {
            using var context=new Context();
            var parameters=new PioneerFramework.RingWorld.RingParameters("legacy/colonized",1,194914,256,3,14000000);
            var def=new PlanetLayerDef{defName="PF_RingWorld",tileType=typeof(SurfaceTile)};
            using var ring=new PioneerFramework.RingWorld.RingSurfaceLayer(27,def,280,Vector3.zero,Vector3.forward,180,0,0,0,0);
            ring.Configure(parameters);ring.InitializeRing();ring.GenerateTiles(new BiomeDef{defName="TemperateForest"});ring[345].temperature=27;
            ((Dictionary<int,PlanetLayer>)context.Grid.PlanetLayers).Add(27,ring);
            context.Manager.Runtime.RegisterDefinition(new(){Id="PF_Megastructure_RingWorld",BackendProviderId="pfl.ring",Nodes={new(){Id="facility",Capabilities=SurfaceModules.Handlers.Select((id,i)=>new CapabilityState{Id="module"+i,HandlerId=id}).ToList()}}});
            var map=context.AddMap(345,41,99,ring);
            var imported=context.Manager.Create(PioneerFramework.RingWorld.RingBackendProvider.Build(parameters,7,"colonized",true));Assert.True(imported.Success,imported.Code);
            var site=Assert.Single(context.Manager.MapSites.Sites);Assert.Equal(345,site.Address.Cell.Value.CellId);
            string path=Path.Combine(Path.GetTempPath(),"PFL-colonized-ring-"+Guid.NewGuid()+".xml");
            var prefs=AccessTools.Field(typeof(Prefs),"data");var oldPrefs=prefs.GetValue(null);prefs.SetValue(null,RuntimeHelpers.GetUninitializedObject(prefs.FieldType));
            using var restoredRing=new PioneerFramework.RingWorld.RingSurfaceLayer(27,def,280,Vector3.zero,Vector3.forward,180,0,0,0,0);
            try
            {
                Scribe.saver.InitSaving(path,"fixture");Scribe.EnterNode("native");((IArtificialSurfaceSaveHeader)ring).ExposeSurfaceHeader();Scribe.ExitNode();context.Manager.ExposeData();Scribe.saver.FinalizeSaving();
                var restoredManager=new MegastructureManager(Current.Game);
                var diagnostic=new Harmony("pfl.tests.surface.scribe-diagnostic");
                diagnostic.Patch(AccessTools.Method(typeof(Log),nameof(Log.Error),new[]{typeof(string)}),prefix:new HarmonyMethod(typeof(RingWorldManagedLifecycleTests),nameof(ThrowHeadlessLog)));
                // The standalone host has no LoadedModManager assembly catalogue.
                diagnostic.Patch(AccessTools.Method(typeof(GenTypes),nameof(GenTypes.GetTypeInAnyAssembly)),prefix:new HarmonyMethod(typeof(RingWorldManagedLifecycleTests),nameof(ResolveFixtureType)));
                try{Scribe.loader.InitLoading(path);Scribe.EnterNode("native");((IArtificialSurfaceSaveHeader)restoredRing).ExposeSurfaceHeader();Scribe.ExitNode();restoredManager.ExposeData();Scribe.loader.FinalizeLoading();}
                finally{diagnostic.UnpatchAll(diagnostic.Id);}
                Assert.Equal(parameters,restoredRing.Parameters);Assert.Equal(0,restoredRing.TilesCount);
                restoredRing.InitializeRing();Assert.Equal(768,restoredRing.TilesCount);
                // The native world/map serializer is outside this component fixture; restore its existing objects without generation.
                ((List<Tile>)AccessTools.Field(typeof(PlanetLayer),"tiles").GetValue(restoredRing)).AddRange(ring.Tiles);
                ((Dictionary<int,PlanetLayer>)context.Grid.PlanetLayers)[27]=restoredRing;
                Current.Game.components.Remove(context.Manager);Current.Game.components.Add(restoredManager);
                Assert.NotNull(restoredManager.GetSnapshot("colonized"));Assert.Single(restoredManager.Backends.Associations);
                Assert.True(restoredManager.MapSites.Access(site.Address,true,7,new(250,1,250),out var existing).Success);Assert.Same(map,existing);
                Assert.Equal(27,restoredRing[345].temperature);Assert.Equal(41,map.Parent.ID);Assert.Equal(99,map.uniqueID);Assert.Single(Current.Game.Maps);
            }
            finally{Scribe.ForceStop();prefs.SetValue(null,oldPrefs);if(File.Exists(path))File.Delete(path);}
        }
    }
}
