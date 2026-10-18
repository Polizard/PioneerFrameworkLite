using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PioneerFramework.Megastructures;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    [Collection("Ring Scribe")]
    public sealed class RingWorldManagedSaveTests
    {
        [Fact]
        public void LegacyInstanceReceiptsAndUnknownCapabilitiesSurviveRepeatedMigrationAndScribe()
        {
            string path=Path.Combine(Path.GetTempPath(),"PFL-managed-ring-"+Guid.NewGuid()+".xml");
            var prefs=AccessTools.Field(typeof(Prefs),"data");var old=prefs.GetValue(null);prefs.SetValue(null,RuntimeHelpers.GetUninitializedObject(prefs.FieldType));
            try
            {
                var source=new MegastructureManager(null);
                var request=new CreateStructureRequest{OperationId="import/ring:old",InstanceId="ring:old",DefinitionId="PF_ExistingRing",OwnerFactionId=7};
                Assert.True(source.Runtime.Create(request).Success);
                Assert.True(source.Runtime.Execute(new(){OperationId="import-binding/ring:old",InstanceId="ring:old",NodeId="facility",ActorFactionId=7,Kind=StructureCommandKind.AttachBackend,Backend=new(){Kind="ring",Id="old/raw",Ownership=BindingOwnership.Borrowed}}).Success);
                Assert.True(source.Runtime.Execute(new(){OperationId="external",InstanceId="ring:old",NodeId="facility",ActorFactionId=7,Kind=StructureCommandKind.AddCapability,Capability=new(){Id="unknown",HandlerId="missing.extension",Version=7,Payload="opaque data"}}).Success);
                var receipts=MegastructureCodec.Encode(source.Runtime.Export().Operations);
                Scribe.saver.InitSaving(path,"managed-ring");source.ExposeData();Scribe.saver.FinalizeSaving();
                var restored=new MegastructureManager(null);Scribe.loader.InitLoading(path);restored.ExposeData();Scribe.loader.FinalizeLoading();
                Assert.Equal("",restored.LoadError);Assert.NotNull(restored.GetSnapshot("ring:old"));
                Assert.Equal(receipts,MegastructureCodec.Encode(restored.Runtime.Export().Operations));
                Assert.Equal("opaque data",restored.GetSnapshot("ring:old").Nodes[0].Capabilities.Single(c=>c.Id=="unknown").Payload);
                Assert.Equal(7,restored.GetSnapshot("ring:old").Nodes[0].Capabilities.Count(c=>SurfaceModules.Handlers.Contains(c.HandlerId)));
                Assert.Equal("old/raw",Assert.Single(restored.Backends.Associations).Backend.StableId);
                SurfaceBindingMigration.AdoptLegacyBackend(restored.Runtime,restored.Backends,"ring","pfl.ring");
                Assert.Single(restored.Backends.Associations);
                Assert.True(restored.Runtime.Create(request).Success);
                Assert.Equal(receipts,MegastructureCodec.Encode(restored.Runtime.Export().Operations));
            }
            finally{Scribe.ForceStop();prefs.SetValue(null,old);if(File.Exists(path))File.Delete(path);}
        }
    }
}
