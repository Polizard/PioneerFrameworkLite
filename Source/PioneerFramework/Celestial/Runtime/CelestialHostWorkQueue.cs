using System;
using System.Collections.Generic;
using System.Threading;

namespace PioneerFramework.Celestial
{
    internal enum CelestialHostWorkState { Queued, Running, Applied, Rejected }
    /// <summary>Native generation runs serially on the owning host thread, outside the publication monitor.</summary>
    internal sealed class CelestialHostWorkQueue(CelestialRuntime runtime)
    {
        private int hostThread = Environment.CurrentManagedThreadId;
        private readonly Queue<Job> queued = new();
        private readonly Dictionary<CelestialInstanceId, int> busyBodies = new();
        private int pumping;
        internal bool IsBusy => Volatile.Read(ref pumping) != 0 || queued.Count != 0;
        internal bool IsHostThread => Environment.CurrentManagedThreadId == hostThread;
        internal bool HasJob(CelestialInstanceId id) => busyBodies.ContainsKey(id);
        internal CelestialOperation Enqueue(string operationId, CelestialInstanceId id, long revision, Func<CelestialOperation> execute)
        {
            busyBodies[id] = busyBodies.GetValueOrDefault(id) + 1;
            queued.Enqueue(new(operationId, runtime.WorldSessionId, id, revision, execute));
            return new(operationId, CelestialOperationStatus.Pending, new[] { id });
        }
        internal void BindHostThread()
        {
            if (Volatile.Read(ref pumping) != 0) throw new InvalidOperationException("Host work must finish before changing host threads.");
            hostThread = Environment.CurrentManagedThreadId;
        }
        internal void Process()
        {
            if (runtime.IsExecutingCallbacks) throw new InvalidOperationException("Native work must wait for committed callbacks to finish.");
            if (runtime.IsPublicationHeld) throw new InvalidOperationException("Native work cannot hold the publication lock.");
            if (Environment.CurrentManagedThreadId != hostThread) throw new InvalidOperationException("Native work requires the owning host thread.");
            if (Interlocked.CompareExchange(ref pumping, 1, 0) != 0) return;
            try
            {
                while (true)
                {
                    var job = runtime.WithPublication(() => queued.Count == 0 ? null : queued.Dequeue());
                    if (job == null) return;
                    job.State = CelestialHostWorkState.Running;
                    CelestialOperation receipt;
                    try
                    {
                        receipt = runtime.IsHostJobCurrent(job.Session, job.InstanceId, job.Revision) ? job.Execute() :
                            new(job.OperationId, CelestialOperationStatus.Rejected, errors: new[] { new CelestialError("HostWorkIdentityChanged", job.InstanceId) });
                    }
                    catch (Exception exception) { receipt = new(job.OperationId, CelestialOperationStatus.Rejected,
                        errors: new[] { new CelestialError("HostWorkFailed", job.InstanceId, Detail: exception.Message) }); }
                    runtime.WithPublication(() => {
                        runtime.Operations.Update(receipt);
                        int remaining = busyBodies[job.InstanceId] - 1;
                        if (remaining == 0) busyBodies.Remove(job.InstanceId); else busyBodies[job.InstanceId] = remaining;
                        job.State = receipt.Status == CelestialOperationStatus.Applied ? CelestialHostWorkState.Applied : CelestialHostWorkState.Rejected;
                        return true;
                    });
                }
            }
            finally { Volatile.Write(ref pumping, 0); }
        }
        private sealed class Job(string operationId, Guid session, CelestialInstanceId id, long revision, Func<CelestialOperation> execute)
        {
            internal readonly string OperationId = operationId;
            internal readonly Guid Session = session;
            internal readonly CelestialInstanceId InstanceId = id;
            internal readonly long Revision = revision;
            internal readonly Func<CelestialOperation> Execute = execute;
            internal CelestialHostWorkState State = CelestialHostWorkState.Queued;
        }
    }
}
