using System;
using PioneerFramework.Megastructures;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    public class MegastructureBackendLifecycleTests
    {
        private sealed class Backend:IStructureBackendProvider
        {
            public string Id=>"test.surface";
            public int Creates,Deletes;
            public bool Exists,Busy,RejectEnsure;
            public bool Available=true;
            public FrameworkResult Validate(StructureCreationRequest request)=>FrameworkResult.Ok();
            public FrameworkResult Ensure(StructureCreationRequest request,out BackendCreationReceipt receipt)
            {
                if(RejectEnsure){receipt=new();return FrameworkResult.Reject("InterruptedEnsure");}
                bool created=!Exists;
                if(request.ExistingOnly&&!Exists){receipt=new();return FrameworkResult.Reject("BackendMissing");}
                if(created){Creates++;Exists=true;}
                receipt=new(){Backend=request.Backend,Created=created};return FrameworkResult.Ok();
            }
            public FrameworkResult CanDestroy(StructureBackendRef backend)=>Busy?FrameworkResult.Reject("ContentsRemain"):FrameworkResult.Ok();
            public FrameworkResult Destroy(StructureBackendRef backend,string operationId){if(Exists){Deletes++;Exists=false;}return FrameworkResult.Ok();}
            public bool IsAvailable(StructureBackendRef backend)=>Exists&&Available;
        }
        private static (MegastructureRuntime,StructureBackendService,Backend) Setup()
        {
            var runtime=new MegastructureRuntime();
            runtime.RegisterDefinition(new(){Id="surface",BackendProviderId="test.surface",Nodes={new(){Id="surface"}}});
            var service=new StructureBackendService(runtime);
            var backend=new Backend();service.Register(backend);
            runtime.ActivationGuard=service.ActivationGuard;
            return(runtime,service,backend);
        }
        private static StructureCreationRequest Request(string op="create")=>new(){OperationId=op,ActorFactionId=7,Backend=new("test.surface","raw/原始"),NodeId="surface",Structure=new(){InstanceId="instance",DefinitionId="surface",OwnerFactionId=7}};

        [Fact]
        public void RetryAndRestoreKeepOneBackendAndOneInstance()
        {
            var (runtime,service,backend)=Setup();var request=Request();
            Assert.True(service.Create(request).Success);
            Assert.True(service.Create(request).Success);
            Assert.Single(runtime.Snapshots);Assert.Equal(1,backend.Creates);
            var saved=runtime.Export();Assert.Single(saved.Backends.Associations);
            Assert.True(runtime.Restore(saved).Success);
            Assert.True(service.Create(request).Success);
            request.Configuration="different";
            Assert.Equal("OperationConflict",service.Create(request).Code);
        }

        [Fact]
        public void OwnershipPermissionAndDetachAreExplicit()
        {
            var (runtime,service,backend)=Setup();var request=Request();
            request.ActorFactionId=8;Assert.Equal("PermissionDenied",service.Create(request).Code);Assert.Equal(0,backend.Creates);
            request.ActorFactionId=7;Assert.True(service.Create(request).Success);
            Assert.Equal("PermissionDenied",service.PrepareDisposition("detach","instance",8,BackendDisposition.DetachKeepBackend).Code);
            Assert.True(service.PrepareDisposition("detach","instance",7,BackendDisposition.DetachKeepBackend).Success);
            Assert.True(service.Release(runtime.GetSnapshot("instance")!).Success);
            Assert.True(backend.Exists);Assert.Equal(0,backend.Deletes);
        }

        [Fact]
        public void BorrowedBackendIsProtectedAndOwnedContentsBlockDeletion()
        {
            var (runtime,service,backend)=Setup();backend.Exists=true;var request=Request();request.ExistingOnly=true;
            Assert.True(service.Create(request).Success);
            Assert.Equal("BorrowedBackendProtected",service.PrepareDisposition("delete","instance",7,BackendDisposition.DestroyOwned).Code);
            Assert.True(service.Release(runtime.GetSnapshot("instance")!).Success);Assert.True(backend.Exists);
            var (runtime2,service2,backend2)=Setup();Assert.True(service2.Create(Request()).Success);backend2.Busy=true;
            Assert.Equal("ContentsRemain",service2.CanRelease(runtime2.GetSnapshot("instance")!).Code);
            Assert.Equal(0,backend2.Deletes);
        }

        [Fact]
        public void MissingProviderPayloadSurvivesRoundTripAndBlocksActivation()
        {
            var (runtime,service,backend)=Setup();Assert.True(service.Create(Request()).Success);
            backend.Exists=false;service.Reconcile();
            Assert.Equal("BackendMissing",service.ActivationGuard(runtime.GetSnapshot("instance")!));
            Assert.Contains("raw/",MegastructureCodec.Encode(runtime.Export()));
        }
        [Fact]
        public void InvalidCoreRequestNeverCreatesNativeBackend()
        {
            var (_,service,backend)=Setup();var request=Request();request.Structure.InstanceId="invalid/id";
            Assert.False(service.Create(request).Success);Assert.Equal(0,backend.Creates);
        }
        [Fact]
        public void FailureAfterBackendAndInstanceStagesResumesWithoutDuplicateNativeObjects()
        {
            var (runtime,service,backend)=Setup();bool fail=true;
            runtime.MutationGuard=(_,c)=>fail&&c.Kind==StructureCommandKind.AttachBackend?"Interrupted":null;
            Assert.Equal("Interrupted",service.Create(Request()).Code);
            Assert.Equal(1,backend.Creates);Assert.Single(runtime.Snapshots);
            var save=runtime.Export();Assert.Equal(BackendOperationStage.InstanceReady,Assert.Single(save.Backends.Operations).Stage);
            Assert.NotNull(save.Backends.Operations[0].Request);
            Assert.True(runtime.Restore(save).Success);fail=false;
            Assert.Equal("PermissionDenied",service.Retry("create",8).Code);
            Assert.True(service.Retry("create",7).Success);Assert.Equal(1,backend.Creates);Assert.Single(service.Associations);
        }
        [Theory]
        [InlineData(BackendOperationStage.Requested)]
        [InlineData(BackendOperationStage.BackendReady)]
        public void EarlySavedStagesResumeWithoutDuplicatingNativeObjects(BackendOperationStage stage)
        {
            var (runtime,service,backend)=Setup();
            backend.RejectEnsure=stage==BackendOperationStage.Requested;backend.Available=stage!=BackendOperationStage.BackendReady;
            Assert.False(service.Create(Request()).Success);
            var document=runtime.Export();Assert.Equal(stage,Assert.Single(document.Backends.Operations).Stage);
            Assert.True(runtime.Restore(document).Success);backend.RejectEnsure=false;backend.Available=true;
            Assert.True(service.Retry("create",7).Success);Assert.Equal(1,backend.Creates);
            Assert.Single(runtime.Snapshots);Assert.Single(service.Associations);
        }
        [Fact]
        public void BoundSavedStageCompletesWithoutReattachingOrRecreating()
        {
            var (runtime,service,backend)=Setup();Assert.True(service.Create(Request()).Success);
            var document=runtime.Export();var operation=Assert.Single(document.Backends.Operations);
            operation.Stage=BackendOperationStage.Bound;operation.Result=null;
            Assert.True(runtime.Restore(document).Success);
            Assert.True(service.Retry("create",7).Success);Assert.Equal(1,backend.Creates);
            Assert.Single(service.Associations);Assert.Single(runtime.Snapshots);
            Assert.Equal(BackendOperationStage.Completed,Assert.Single(service.Operations).Stage);
        }
        [Fact]
        public void UnregisteredProviderRetainsSavedPayloadAndBlocksActivation()
        {
            var (runtime,service,_)=Setup();Assert.True(service.Create(Request()).Success);
            var document=runtime.Export();var restored=new MegastructureRuntime();
            Assert.True(restored.Restore(document).Success);var missing=new StructureBackendService(restored);
            Assert.Equal("BackendProviderMissing",missing.ActivationGuard(restored.GetSnapshot("instance")!));
            Assert.Equal(MegastructureCodec.Encode(document),MegastructureCodec.Encode(restored.Export()));
        }
        [Fact]
        public void CorruptBackendMetadataIsRejectedWithoutReplacingCurrentState()
        {
            var (runtime,service,_)=Setup();Assert.True(service.Create(Request()).Success);
            var save=runtime.Export();save.Backends=null!;
            Assert.False(runtime.Restore(save).Success);Assert.Single(runtime.Snapshots);
            save=runtime.Export();save.Backends.Associations.Add(MegastructureCodec.Clone(save.Backends.Associations[0]));
            Assert.False(runtime.Restore(save).Success);
        }
    }
}
