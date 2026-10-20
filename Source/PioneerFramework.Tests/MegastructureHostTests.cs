using System;
using System.IO;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PioneerFramework.Megastructures;
using Verse;
using RimWorld;
using Xunit;

namespace PioneerFramework.Tests
{
    [Collection("Ring Scribe")]
    public sealed class MegastructureHostTests
    {
        [Fact]
        public void ManagerScribeRoundTripPreservesUnknownExtensionPayloadAndIdentity()
        {
            string path=Path.Combine(Path.GetTempPath(),"PFL-framework-"+Guid.NewGuid()+".xml");
            var prefs=AccessTools.Field(typeof(Prefs),"data");object old=prefs.GetValue(null);prefs.SetValue(null,RuntimeHelpers.GetUninitializedObject(prefs.FieldType));
            try
            {
                var manager=new MegastructureManager(null);
                manager.Runtime.RegisterDefinition(new StructureDefinition {Id="test",Nodes={new NodeTemplate {Id="root",Capabilities={new CapabilityState {Id="external",HandlerId="unloaded.mod",Version=4,Payload="<state count='19'/>"}}}}});
                Assert.True(manager.Runtime.Create(new CreateStructureRequest {OperationId="create",InstanceId="persisted",DefinitionId="test"}).Success);
                Scribe.saver.InitSaving(path,"framework-test");manager.ExposeData();Scribe.saver.FinalizeSaving();
                var restored=new MegastructureManager(null);Scribe.loader.InitLoading(path);restored.ExposeData();Scribe.loader.FinalizeLoading();
                Assert.Equal("",restored.LoadError);
                var snapshot=restored.Runtime.GetSnapshot("persisted");Assert.NotNull(snapshot);
                Assert.Equal("<state count='19'/>",snapshot.Nodes[0].Capabilities[0].Payload);
                Assert.Equal(4,snapshot.Nodes[0].Capabilities[0].Version);
                Assert.True(restored.Runtime.GetOperationResult("create").Success);
            }
            finally {Scribe.ForceStop();prefs.SetValue(null,old);if(File.Exists(path))File.Delete(path);}
        }
        [Fact]
        public void XmlDefinitionConversionKeepsTypedStorageConfigurationAndRelativeNodes()
        {
            var def=new MegastructureDef {defName="array",nodes={new MegastructureNodeConfig {key="hub",frameId=1,capabilities={new MegastructureCapabilityConfig {key="storage",handlerId="pfl.resource.storage",properties=new StorageCapabilityProperties {resources={new ResourceSpecificationConfig {resourceId="energy",unit="kWd",capacity=30}}}}}},new MegastructureNodeConfig {key="remote",spatialMode=SpatialMode.NodeRelative,parentNode="hub"}}};
            var runtime=new MegastructureRuntime();runtime.RegisterDefinition(def.ToDefinition());
            Assert.True(runtime.Create(new CreateStructureRequest {OperationId="c",InstanceId="array-1",DefinitionId="array"}).Success);
            var snapshot=runtime.GetSnapshot("array-1");
            Assert.Equal(new NodeAddress("array-1","hub"),snapshot.Nodes[1].Spatial.Parent);
            Assert.Equal(30,MegastructureCodec.Read<StorageCapabilityData>(snapshot.Nodes[0].Capabilities[0].Payload).Resources[0].Capacity);
        }
        [Fact]
        public void PhysicalItemStoreRetainsOriginalThingsAndReservationsProtectAvailableStock()
        {
            var bootstrap=new Harmony("pfl.tests.megastructure.def-bootstrap");
            bootstrap.Patch(AccessTools.Method(typeof(DefOfHelper),nameof(DefOfHelper.EnsureInitializedInCtor)),prefix:new HarmonyMethod(typeof(MegastructureHostTests),nameof(SkipDefBootstrapWarning)));
            try{RuntimeHelpers.RunClassConstructor(typeof(ThingDefOf).TypeHandle);}finally{bootstrap.UnpatchAll(bootstrap.Id);}
            // BuildableDef's constructor loads shaders; keep the real ThingOwner path without bootstrapping rendering.
            var def=(ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef));def.defName="TestMaterial";def.stackLimit=75;def.category=ThingCategory.Item;
            var thing=new Thing {def=def,stackCount=5};
            var previousGame=Current.Game;var game=(Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            AccessTools.Field(typeof(Game),"maps").SetValue(game,new System.Collections.Generic.List<Map>());Current.Game=game;
            try
            {
            var store=new ThingResourceContainer("cargo",new("a","hub"));
            Assert.True(store.Configure(new ResourceSpecification {Id="TestMaterial",Unit="item",IsItem=true,Capacity=10}).Success);
            Assert.True(store.Deposit(thing).Success);
            Assert.Same(thing,store.GetDirectlyHeldThings()[0]);
            Assert.Equal(5,store.Query("TestMaterial").Amount);
            var leg=new ResourceLeg {ProviderId="cargo",ResourceId="TestMaterial",Amount=4};
            Assert.True(store.Reserve("reserve",leg).Success);Assert.True(store.Reserve("reserve",leg).Success);
            Assert.Equal(1,store.Query("TestMaterial").Available);store.Cancel("reserve");Assert.Equal(5,store.Query("TestMaterial").Available);
            AccessTools.Field(typeof(Thing),"hitPointsInt").SetValue(thing,23);
            var target=new ThingResourceContainer("target",new("a","other"));target.Configure(new ResourceSpecification {Id="TestMaterial",Unit="item",IsItem=true,Capacity=10});
            var resources=new StructureResourceService(new ResourceDocument());resources.RegisterProvider(store);resources.RegisterProvider(target);
            Assert.True(resources.Transfer("move","cargo","target","TestMaterial",5).Success);
            Assert.True(resources.Transfer("move","cargo","target","TestMaterial",5).Success);
            Assert.Same(thing,target.GetDirectlyHeldThings()[0]);Assert.Equal(23,thing.HitPoints);Assert.Equal(0,store.Count);Assert.Equal(5,target.Count);
            var manager=new MegastructureManager(game);game.components=new System.Collections.Generic.List<GameComponent>{manager};
            manager.Runtime.RegisterDefinition(new StructureDefinition {Id="warehouse",Nodes={new NodeTemplate {Id="hub"},new NodeTemplate {Id="other"}}});
            manager.Runtime.Create(new CreateStructureRequest {OperationId="warehouse-create",InstanceId="a",DefinitionId="warehouse"});
            Assert.True(manager.EnsureStore("native",new("a","hub"),new ResourceSpecification {Id="TestMaterial",Unit="item",IsItem=true,Capacity=10}).Success);
            Assert.True(manager.ThingStores[0].Deposit(thing).Success);
            Assert.Equal("StoredResourcesRemain",manager.Runtime.Execute(new StructureCommand {OperationId="remove-stocked",InstanceId="a",NodeId="hub",Kind=StructureCommandKind.RemoveNode}).Code);
            Assert.Equal("SeparateItemAndScalarStoresRequired",manager.EnsureStore("native",new("a","hub"),new ResourceSpecification {Id="power",Unit="kWd",Capacity=10}).Code);
            }
            finally {Current.Game=previousGame;}
        }
        private static bool SkipDefBootstrapWarning()=>false;
        [Fact]
        public void ManeuverUsesExistingBurnAndDoesNotTeleportOrDebitFuelAtCommandTime()
        {
            var previous=Current.Game;var game=(Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            var simulation=new SpaceGameComp(game);var manager=new MegastructureManager(game);game.components=new System.Collections.Generic.List<GameComponent> {simulation,manager};
            AccessTools.Field(typeof(Game),"maps").SetValue(game,new System.Collections.Generic.List<Map>());
            var group=new SpaceEntityGroup(7,3) {CurrentOrbit=new OrbitState {positionSystem=new SystemVec3(new DVector3(100,0,0)),velocitySystem=new SystemVec3(new DVector3(0,20,0)),rotation=DQuaternion.identity}};
            var entity=new SpaceEntity(11,3) {GroupId=7};var part=new SpacePart();part.components.Add(new PioneerFramework.Components.RoomComponentData {dryMass=5000});entity.SetParts(new System.Collections.Generic.List<SpacePart> {part},false);
            entity.AddPropulsion(new PropulsionData {propellant=new PropellantDef {defName="framework-fuel"},ve=4000,massFlow=4,mass=1000,capacity=1000});group.GetEntityIds().Add(11);
            ((System.Collections.Generic.Dictionary<int,SpaceEntity>)AccessTools.Field(typeof(SpaceGameComp),"entities").GetValue(simulation)).Add(11,entity);
            ((System.Collections.Generic.Dictionary<int,SpaceEntityGroup>)AccessTools.Field(typeof(SpaceGameComp),"groups").GetValue(simulation)).Add(7,group);
            try
            {
                Current.Game=game;manager.Runtime.RegisterDefinition(new StructureDefinition {Id="ship",Nodes={new NodeTemplate {Id="drive",Spatial=new SpatialBinding {Mode=SpatialMode.PhysicalGroup,GroupId=7},Capabilities={new CapabilityState {Id="engine",HandlerId="pfl.maneuver"}}}}});
                manager.Runtime.Create(new CreateStructureRequest {OperationId="create",InstanceId="ship-1",DefinitionId="ship",OwnerFactionId=3});manager.Runtime.Execute(new StructureCommand {OperationId="active",InstanceId="ship-1",ActorFactionId=3,Kind=StructureCommandKind.Activate});
                var command=new StructureCommand {OperationId="burn",InstanceId="ship-1",NodeId="drive",ActorFactionId=3,Kind=StructureCommandKind.Maneuver,DeltaV=new DVector3(0,10,0)};
                Assert.True(manager.Execute(new StructureCommand {OperationId="bad-version",InstanceId="ship-1",NodeId="drive",ActorFactionId=3,Kind=StructureCommandKind.UpdateCapability,CapabilityId="engine",Capability=new CapabilityState {HandlerId="pfl.maneuver",Version=99}}).Success);
                Assert.Equal("CapabilityVersion",manager.Execute(command).Code);Assert.False(group.HasActiveBurn);
                Assert.True(manager.Execute(new StructureCommand {OperationId="good-version",InstanceId="ship-1",NodeId="drive",ActorFactionId=3,Kind=StructureCommandKind.UpdateCapability,CapabilityId="engine",Capability=new CapabilityState {HandlerId="pfl.maneuver",Version=1}}).Success);
                Assert.True(manager.Execute(command).Success);Assert.True(manager.Execute(command).Success);
                Assert.True(group.HasActiveBurn);Assert.Equal(10,group.remainingBurnDV);
                Assert.Equal(new DVector3(0,20,0),group.CurrentOrbit.velocitySystem.v);Assert.Equal(1000,entity.TotalPropellantMass);
            }
            finally {Current.Game=previous;}
        }
        [Fact]
        public void OwnedGroupCannotRetireABorrowedOrArchivedMapThroughIndirectDestruction()
        {
            var previous=Current.Game;var game=(Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));var simulation=new SpaceGameComp(game);var manager=new MegastructureManager(game);
            game.components=new System.Collections.Generic.List<GameComponent> {simulation,manager};AccessTools.Field(typeof(Game),"maps").SetValue(game,new System.Collections.Generic.List<Map>());
            var group=new SpaceEntityGroup(7,3);group.GetEntityIds().Add(11);var entity=new SpaceEntity(11,3) {GroupId=7,MapId=42,HasMap=true};
            ((System.Collections.Generic.Dictionary<int,SpaceEntity>)AccessTools.Field(typeof(SpaceGameComp),"entities").GetValue(simulation)).Add(11,entity);
            ((System.Collections.Generic.Dictionary<int,SpaceEntityGroup>)AccessTools.Field(typeof(SpaceGameComp),"groups").GetValue(simulation)).Add(7,group);
            try
            {
                Current.Game=game;manager.Runtime.RegisterDefinition(new StructureDefinition {Id="station",Nodes={new NodeTemplate {Id="node",Spatial=new SpatialBinding {Mode=SpatialMode.PhysicalGroup,GroupId=7,Ownership=BindingOwnership.Owned}}}});
                manager.Runtime.Create(new CreateStructureRequest {OperationId="create",InstanceId="station-1",DefinitionId="station",OwnerFactionId=3});
                var result=manager.Execute(new StructureCommand {OperationId="retire",InstanceId="station-1",ActorFactionId=3,Kind=StructureCommandKind.BeginRetirement});
                Assert.Equal("BorrowedMapProtected",result.Code);Assert.Same(group,simulation.GetGroupById(7));Assert.Same(entity,simulation.GetEntityById(11));
            }
            finally {Current.Game=previous;}
        }
    }
}
