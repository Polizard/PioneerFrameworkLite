using System;
using System.IO;
using System.Linq;
using System.Reflection;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialSubmissionRetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreationSubmitCanLoseItsResponseWithoutReleasingOrResamplingTheBodies(bool accepted)
    {
        SubmissionProxy controller = null;
        var runtime = CelestialBenchFactory.Create(3, bridgeTransform: bridge => {
            var proxy = DispatchProxy.Create<ICelestialPhysicsBridge, SubmissionProxy>(); controller = (SubmissionProxy)(object)proxy;
            controller.Inner = bridge; controller.AcceptBeforeThrowing = accepted; controller.TargetKind = PhysicsBodyChangeKind.Register;
            return proxy;
        });
        Assert.Equal(accepted ? 1 : 2, controller!.SubmissionAttempts);
        Assert.Equal(3, runtime.CaptureCatalog().Bodies.Count);
        foreach (int index in Enumerable.Range(0, 3)) Assert.Equal(CelestialLifecycle.Ready, runtime.Query(CelestialBenchFactory.Id(index)).Value.Lifecycle);
    }
    [Fact]
    public void NewResourceBlockerPausesRetryUntilTheSameRemovalBecomesSafe()
    {
        SubmissionProxy controller = null;
        var runtime = CelestialBenchFactory.Create(3, bridgeTransform: bridge => {
            var proxy = DispatchProxy.Create<ICelestialPhysicsBridge, SubmissionProxy>(); controller = (SubmissionProxy)(object)proxy; controller.Inner = bridge; return proxy;
        });
        var id = CelestialBenchFactory.Id(2);
        Assert.Equal(CelestialOperationStatus.Pending, runtime.Remove(new(new("remove", runtime.WorldSessionId, runtime.Catalog.Revision), id)).Status);
        var resources = new DynamicResources(); runtime.AttachConsumerResources(resources);
        CelestialBenchFactory.Advance(runtime, 1);
        Assert.Equal(1, controller!.SubmissionAttempts); Assert.Equal(CelestialOperationStatus.Pending, runtime.GetOperation("remove").Status);
        resources.Block = false; CelestialBenchFactory.Advance(runtime, 2);
        Assert.Equal(2, controller.SubmissionAttempts); Assert.Equal(CelestialOperationStatus.Applied, runtime.GetOperation("remove").Status);
    }
    private sealed class DynamicResources : ICelestialRemovalResources
    {
        internal bool Block = true;
        public System.Collections.Generic.IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id) => Block ? new[] { new CelestialError("MapExists", id) } : Array.Empty<CelestialError>();
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => CelestialResult.Ok(true);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LostSubmitResponseKeepsRemovingAndRetriesTheSameLogicalRequest(bool accepted)
    {
        SubmissionProxy controller = null;
        var runtime = CelestialBenchFactory.Create(3, bridgeTransform: bridge => {
            var proxy = DispatchProxy.Create<ICelestialPhysicsBridge, SubmissionProxy>();
            controller = (SubmissionProxy)(object)proxy; controller.Inner = bridge; controller.AcceptBeforeThrowing = accepted;
            return proxy;
        });
        var id = CelestialBenchFactory.Id(2);
        var request = new RemoveBodyRequest(new("remove", runtime.WorldSessionId, runtime.Catalog.Revision), id);
        Assert.Equal(CelestialOperationStatus.Pending, runtime.Remove(request).Status);
        Assert.Equal(CelestialLifecycle.Removing, runtime.Query(id).Value.Lifecycle);
        CelestialBenchFactory.Advance(runtime, 1);
        Assert.Equal(CelestialOperationStatus.Applied, runtime.GetOperation("remove").Status);
        Assert.Equal(CelestialLifecycle.Removed, runtime.Query(id).Value.Lifecycle);
        Assert.Equal(accepted ? 1 : 2, controller!.SubmissionAttempts);
        Assert.Equal(CelestialOperationStatus.Applied, runtime.Remove(request).Status);
        Assert.Equal(accepted ? 1 : 2, controller.SubmissionAttempts);
    }
    public class SubmissionProxy : DispatchProxy
    {
        public ICelestialPhysicsBridge Inner;
        public bool AcceptBeforeThrowing;
        public int SubmissionAttempts;
        public PhysicsBodyChangeKind TargetKind = PhysicsBodyChangeKind.Unregister;
        public string FirstId, FirstHash;
        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == nameof(ICelestialPhysicsBridge.Submit) && args[0] is PhysicsChangeSet changes && changes.Changes.Any(change => change.Kind == TargetKind))
            {
                SubmissionAttempts++;
                if (SubmissionAttempts == 1)
                {
                    FirstId = changes.OperationId; FirstHash = changes.ContentHash();
                    if (AcceptBeforeThrowing) method.Invoke(Inner, args);
                    throw new IOException("lost provider response");
                }
                Assert.Equal(FirstId, changes.OperationId); Assert.Equal(FirstHash, changes.ContentHash());
            }
            return method.Invoke(Inner, args);
        }
    }
}
