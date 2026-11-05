using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialPhysicsDescriptor
    {
        public CelestialInstanceId Id { get; }
        public long DescriptorRevision { get; }
        public double RadiusMeters { get; }
        public double? GravitationalParameter { get; }
        public CelestialPhysicsDescriptor(CelestialInstanceId id, long descriptorRevision, double radiusMeters, double? gravitationalParameter = null)
        {
            if (!id.IsValid) throw new ArgumentException("Invalid instance identity.", nameof(id));
            ArgumentOutOfRangeException.ThrowIfLessThan(descriptorRevision, 1);
            if (!double.IsFinite(radiusMeters) || radiusMeters <= 0) throw new ArgumentOutOfRangeException(nameof(radiusMeters));
            if (gravitationalParameter.HasValue && (!double.IsFinite(gravitationalParameter.Value) || gravitationalParameter.Value < 0))
                throw new ArgumentOutOfRangeException(nameof(gravitationalParameter));
            Id = id;
            DescriptorRevision = descriptorRevision;
            RadiusMeters = radiusMeters;
            GravitationalParameter = gravitationalParameter;
        }
    }

    public sealed class PhysicsInitialization
    {
        private readonly byte[] payload;
        public string ProviderId { get; }
        public string ModeId { get; }
        public int Version { get; }
        public ReadOnlyMemory<byte> Payload => payload;
        public PhysicsInitialization(string providerId, string modeId, int version, ReadOnlySpan<byte> payload)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
            ArgumentException.ThrowIfNullOrWhiteSpace(modeId);
            ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
            ProviderId = providerId;
            ModeId = modeId;
            Version = version;
            this.payload = payload.ToArray();
        }
    }

    public sealed class CelestialBodyRegistration
    {
        public CelestialInstanceId InstanceId => Descriptor.Id;
        public string DefinitionId { get; }
        public CelestialPhysicsDescriptor Descriptor { get; }
        public PhysicsInitialization Initialization { get; }
        public CelestialInstanceId? ParentInstanceId { get; }
        public int? CompatibilityHandle { get; }
        public IReadOnlyDictionary<string, CelestialInstanceId> InitializationReferences { get; }
        public CelestialBodyRegistration(string definitionId, CelestialPhysicsDescriptor descriptor, PhysicsInitialization initialization,
            CelestialInstanceId? parentInstanceId = null, IReadOnlyDictionary<string, CelestialInstanceId>? initializationReferences = null,
            int? compatibilityHandle = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
            if (parentInstanceId.HasValue && !parentInstanceId.Value.IsValid) throw new ArgumentException("Invalid parent identity.", nameof(parentInstanceId));
            if (compatibilityHandle < 0) throw new ArgumentOutOfRangeException(nameof(compatibilityHandle));
            DefinitionId = definitionId;
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            Initialization = initialization ?? throw new ArgumentNullException(nameof(initialization));
            ParentInstanceId = parentInstanceId;
            CompatibilityHandle = compatibilityHandle;
            InitializationReferences = new ReadOnlyDictionary<string, CelestialInstanceId>(new Dictionary<string, CelestialInstanceId>(
                initializationReferences ?? new Dictionary<string, CelestialInstanceId>(), StringComparer.Ordinal));
            if (InitializationReferences.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || !pair.Value.IsValid))
                throw new ArgumentException("Invalid initialization reference.", nameof(initializationReferences));
        }
    }

    public enum PhysicsBodyChangeKind { Register, Update, Unregister }
    public sealed class PhysicsBodyChange
    {
        public PhysicsBodyChangeKind Kind { get; }
        public CelestialInstanceId InstanceId { get; }
        public CelestialBodyRegistration? Registration { get; }
        private PhysicsBodyChange(PhysicsBodyChangeKind kind, CelestialInstanceId id, CelestialBodyRegistration? registration)
        {
            Kind = kind;
            InstanceId = id;
            Registration = registration;
        }
        public static PhysicsBodyChange Register(CelestialBodyRegistration registration) => new(PhysicsBodyChangeKind.Register, registration.InstanceId, registration);
        public static PhysicsBodyChange Update(CelestialBodyRegistration registration) => new(PhysicsBodyChangeKind.Update, registration.InstanceId, registration);
        public static PhysicsBodyChange Unregister(CelestialInstanceId id) => new(PhysicsBodyChangeKind.Unregister, id, null);
    }

    public sealed class PhysicsChangeSet
    {
        public string OperationId { get; }
        public Guid WorldSessionId { get; }
        public long ExpectedTopologyRevision { get; }
        public IReadOnlyList<PhysicsBodyChange> Changes { get; }
        public PhysicsChangeSet(string operationId, Guid worldSessionId, long expectedTopologyRevision, IEnumerable<PhysicsBodyChange> changes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
            OperationId = operationId;
            WorldSessionId = worldSessionId;
            ExpectedTopologyRevision = expectedTopologyRevision;
            Changes = Array.AsReadOnly(changes.ToArray());
        }

        /// <summary>版本 1 的内容摘要；会话、期望修订和投递次序不参与实质内容比较。</summary>
        public string ContentHash()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write("PFL.Celestial.PhysicsChange.v1");
            writer.Write(Changes.Count);
            foreach (var change in Changes.OrderBy(change => change.InstanceId.Value, StringComparer.Ordinal).ThenBy(change => change.Kind))
            {
                writer.Write((int)change.Kind);
                writer.Write(change.InstanceId.Value);
                writer.Write(change.Registration != null);
                if (change.Registration is not { } registration) continue;
                writer.Write(registration.DefinitionId);
                writer.Write(registration.Descriptor.DescriptorRevision);
                writer.Write(registration.Descriptor.RadiusMeters);
                writer.Write(registration.Descriptor.GravitationalParameter.HasValue);
                if (registration.Descriptor.GravitationalParameter.HasValue) writer.Write(registration.Descriptor.GravitationalParameter.Value);
                writer.Write(registration.ParentInstanceId.HasValue);
                if (registration.ParentInstanceId.HasValue) writer.Write(registration.ParentInstanceId.Value.Value);
                writer.Write(registration.CompatibilityHandle.HasValue);
                if (registration.CompatibilityHandle.HasValue) writer.Write(registration.CompatibilityHandle.Value);
                writer.Write(registration.Initialization.ProviderId);
                writer.Write(registration.Initialization.ModeId);
                writer.Write(registration.Initialization.Version);
                writer.Write(registration.Initialization.Payload.Length);
                writer.Write(registration.Initialization.Payload.Span);
                writer.Write(registration.InitializationReferences.Count);
                foreach (var pair in registration.InitializationReferences.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.Write(pair.Key);
                    writer.Write(pair.Value.Value);
                }
            }
            writer.Flush();
            return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
        }
    }

    public enum PhysicsOperationStatus { Pending, Applied, Rejected }
    public sealed class PhysicsOperation
    {
        public string OperationId { get; }
        public PhysicsOperationStatus Status { get; }
        public IReadOnlyList<CelestialError> Errors { get; }
        public double? AppliedSeconds { get; }
        public long? TopologyRevision { get; }
        public PhysicsOperation(string operationId, PhysicsOperationStatus status, IEnumerable<CelestialError>? errors = null,
            double? appliedSeconds = null, long? topologyRevision = null)
        {
            OperationId = operationId;
            Status = status;
            Errors = Array.AsReadOnly((errors ?? Array.Empty<CelestialError>()).ToArray());
            AppliedSeconds = appliedSeconds;
            TopologyRevision = topologyRevision;
        }
    }
}
