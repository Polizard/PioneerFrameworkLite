using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PioneerFramework.Celestial
{
    public enum PhysicsSamplePurpose { SimulationCommitted, Presentation }
    public enum PhysicsSampleKind { Committed, PresentationInterpolation, AuthoritativeInterval }
    public readonly record struct PhysicsSampleStamp(Guid WorldSessionId, long ClockRevision, long StateRevision,
        long TopologyRevision, double PhysicalSeconds, string ReferenceFrameId, PhysicsSampleKind SampleKind);
    /// <summary>向量均位于 Stamp.ReferenceFrameId 声明的共同惯性参考系；姿态从天体固连系旋入该系。</summary>
    public readonly record struct CelestialMotionState(long DescriptorRevision, DVector3 Position, DVector3 LinearVelocity,
        DQuaternion Orientation, DVector3 AngularVelocity, bool IsValid = true);

    public sealed class PhysicsSampleRequest
    {
        public Guid WorldSessionId { get; }
        public long ExpectedTopologyRevision { get; }
        public IReadOnlyList<CelestialInstanceId> InstanceIds { get; }
        public PhysicsSamplePurpose Purpose { get; }
        public double? PhysicalSeconds { get; }
        public PhysicsSampleRequest(Guid worldSessionId, long expectedTopologyRevision, IEnumerable<CelestialInstanceId> instanceIds,
            PhysicsSamplePurpose purpose = PhysicsSamplePurpose.SimulationCommitted, double? physicalSeconds = null)
        {
            WorldSessionId = worldSessionId;
            ExpectedTopologyRevision = expectedTopologyRevision;
            InstanceIds = Array.AsReadOnly(instanceIds.ToArray());
            Purpose = purpose;
            PhysicalSeconds = physicalSeconds;
        }
    }

    public interface ICelestialPhysicsSnapshot : IDisposable
    {
        PhysicsSampleStamp Stamp { get; }
        bool TryGetBodyDescriptor(CelestialInstanceId instanceId, out CelestialPhysicsDescriptor descriptor);
        bool TryGetBodyState(CelestialInstanceId instanceId, out CelestialMotionState state);
    }

    public sealed class PhysicsSnapshotResult
    {
        public bool Success => Snapshot != null;
        public ICelestialPhysicsSnapshot? Snapshot { get; }
        public IReadOnlyList<CelestialError> Errors { get; }
        private PhysicsSnapshotResult(ICelestialPhysicsSnapshot? snapshot, IEnumerable<CelestialError> errors)
        {
            Snapshot = snapshot;
            Errors = Array.AsReadOnly(errors.ToArray());
        }
        public static PhysicsSnapshotResult Ok(ICelestialPhysicsSnapshot snapshot) => new(snapshot, Array.Empty<CelestialError>());
        public static PhysicsSnapshotResult Reject(params CelestialError[] errors) => new(null, errors);
    }

    /// <summary>可供提供者复用的复制型只读快照。创建时验证，释放前可并行读取。</summary>
    public sealed class CelestialPhysicsSnapshot : ICelestialPhysicsSnapshot
    {
        private readonly Dictionary<CelestialInstanceId, CelestialPhysicsDescriptor> descriptors;
        private readonly Dictionary<CelestialInstanceId, CelestialMotionState> states;
        private int disposed;
        public PhysicsSampleStamp Stamp { get; }
        private CelestialPhysicsSnapshot(PhysicsSampleStamp stamp, Dictionary<CelestialInstanceId, CelestialPhysicsDescriptor> descriptors,
            Dictionary<CelestialInstanceId, CelestialMotionState> states)
        {
            Stamp = stamp;
            this.descriptors = descriptors;
            this.states = states;
        }
        public static PhysicsSnapshotResult Create(PhysicsSampleStamp stamp, IEnumerable<CelestialPhysicsDescriptor> descriptors,
            IReadOnlyDictionary<CelestialInstanceId, CelestialMotionState> states)
        {
            if (stamp.WorldSessionId == Guid.Empty || !double.IsFinite(stamp.PhysicalSeconds) || stamp.ClockRevision < 0 ||
                stamp.StateRevision < 0 || stamp.TopologyRevision < 0 || string.IsNullOrWhiteSpace(stamp.ReferenceFrameId) || !Enum.IsDefined(stamp.SampleKind))
                return PhysicsSnapshotResult.Reject(new CelestialError("InvalidStamp"));
            var copied = new Dictionary<CelestialInstanceId, CelestialPhysicsDescriptor>();
            foreach (var descriptor in descriptors)
                if (descriptor == null || !copied.TryAdd(descriptor.Id, descriptor)) return PhysicsSnapshotResult.Reject(new CelestialError("InvalidDescriptor"));
            if (copied.Count != states.Count) return PhysicsSnapshotResult.Reject(new CelestialError("DescriptorStateMismatch"));
            foreach (var pair in states)
            {
                if (!copied.TryGetValue(pair.Key, out var descriptor)) return PhysicsSnapshotResult.Reject(new CelestialError("DescriptorStateMismatch", pair.Key));
                var state = pair.Value;
                if (descriptor.DescriptorRevision != state.DescriptorRevision)
                    return PhysicsSnapshotResult.Reject(new CelestialError("DescriptorStateMismatch", pair.Key));
                var q = state.Orientation;
                double norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
                if (!state.IsValid || !state.Position.IsFinite() || !state.LinearVelocity.IsFinite() || !state.AngularVelocity.IsFinite() ||
                    !double.IsFinite(norm) || Math.Abs(norm - 1) > 1e-12)
                    return PhysicsSnapshotResult.Reject(new CelestialError("InvalidState", pair.Key));
            }
            return PhysicsSnapshotResult.Ok(new CelestialPhysicsSnapshot(stamp, copied, new Dictionary<CelestialInstanceId, CelestialMotionState>(states)));
        }
        public bool TryGetBodyDescriptor(CelestialInstanceId instanceId, out CelestialPhysicsDescriptor descriptor)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return descriptors.TryGetValue(instanceId, out descriptor!);
        }
        public bool TryGetBodyState(CelestialInstanceId instanceId, out CelestialMotionState state)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return states.TryGetValue(instanceId, out state);
        }
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
    }
}
