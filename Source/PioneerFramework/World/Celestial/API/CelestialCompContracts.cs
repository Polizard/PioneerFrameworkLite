using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PioneerFramework.Celestial
{
    /// <summary>实例创建时解析并冻结的组件配置；Serialize 返回版本化原始载荷，恢复不重新采样。</summary>
    public interface ICelestialResolvedProperties
    {
        string Key { get; }
        string HandlerId { get; }
        int Version { get; }
        ReadOnlyMemory<byte> Serialize();
    }

    /// <summary>Mod 初始化阶段登记的组件工厂；输入已冻结配置，工厂不得阻塞等待宿主查询或操作原生地图。</summary>
    public interface ICelestialCompFactory
    {
        CelestialBodyComp Create(ICelestialResolvedProperties properties);
    }

    public interface ICelestialGeometryProperties : ICelestialResolvedProperties
    {
        double RadiusMeters { get; }
    }

    public interface ICelestialResolvedConfigurationValidator
    {
        CelestialResult<bool> Validate(IReadOnlyList<ICelestialResolvedProperties> components);
    }

    /// <summary>恢复版本化配置与状态；未知版本必须结构化拒绝并保留原始字节，不猜测默认状态。</summary>
    public interface ICelestialCompStateHandler
    {
        CelestialResult<ICelestialResolvedProperties> DecodeProperties(string key, int version, ReadOnlyMemory<byte> payload);
        CelestialResult<bool> RestoreState(CelestialBodyComp component, int version, ReadOnlyMemory<byte> payload);
    }

    /// <summary>附着期间的会话上下文；命令排入宿主 FIFO，长期动作显式使用新代次，不能借用已关闭的创建代次。</summary>
    public interface ICelestialCompContext
    {
        CelestialInstanceId InstanceId { get; }
        Guid WorldSessionId { get; }
        long CatalogRevision { get; }
        long DescriptorRevision { get; }
        ICelestialResolvedProperties Properties { get; }
        double PhysicalSeconds { get; }
        long OperationEpoch => 0;
        bool TryGetCapability<T>(out T capability) where T : class;
        IReadOnlyList<T> GetCapabilities<T>() where T : class;
        bool TryGetService<T>(out T service) where T : class;
        CelestialResult<string> Submit(CelestialCommand command);
        CelestialResult<string> SubmitNewAction(long epoch, CelestialCommand command)
        {
            if (epoch < 0) return CelestialResult.Reject<string>(new CelestialError("InvalidOperationEpoch", InstanceId));
            if (command.OperationId.StartsWith("epoch/", StringComparison.Ordinal))
                return CelestialResult.Reject<string>(new CelestialError("InvalidActionScope", InstanceId));
            return Submit(new(command.Kind, CelestialOperationEpochs.Qualify(epoch, command.OperationId), command.TargetId,
                command.WorldSessionId, command.ExpectedRevision, command.Payload.Span));
        }
    }

    public enum CelestialUpdateMode { None, Scheduled, EventsOnly }
    public enum CelestialAttachReason { Created, Restored }
    public enum CelestialCompFailurePolicy { BlockBody, DisableOptional }

    public sealed class CelestialUpdatePolicy
    {
        public CelestialUpdateMode Mode { get; }
        public double IntervalSeconds { get; }
        public static CelestialUpdatePolicy None { get; } = new(CelestialUpdateMode.None, 0);
        public static CelestialUpdatePolicy EventsOnly { get; } = new(CelestialUpdateMode.EventsOnly, 0);

        private CelestialUpdatePolicy(CelestialUpdateMode mode, double intervalSeconds)
        {
            Mode = mode;
            IntervalSeconds = intervalSeconds;
        }

        public static CelestialUpdatePolicy Every(double intervalSeconds)
        {
            if (!double.IsFinite(intervalSeconds) || intervalSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
            return new(CelestialUpdateMode.Scheduled, intervalSeconds);
        }
    }

    public sealed class CelestialCompDescriptor
    {
        public string Key { get; }
        public string HandlerId { get; }
        public int SchemaVersion { get; }
        public IReadOnlyList<string> Provides { get; }
        public IReadOnlyList<string> Requires { get; }
        public string? ExclusiveSlot { get; }
        public IReadOnlyList<string> Before { get; }
        public IReadOnlyList<string> After { get; }
        public CelestialUpdatePolicy UpdatePolicy { get; }
        [System.Text.Json.Serialization.JsonIgnore]
        public CelestialCompFailurePolicy FailurePolicy { get; }

        public CelestialCompDescriptor(string key, string handlerId, int schemaVersion,
            IEnumerable<string>? provides = null, IEnumerable<string>? requires = null, string? exclusiveSlot = null,
            IEnumerable<string>? before = null, IEnumerable<string>? after = null, CelestialUpdatePolicy? updatePolicy = null,
            CelestialCompFailurePolicy failurePolicy = CelestialCompFailurePolicy.BlockBody)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentException.ThrowIfNullOrWhiteSpace(handlerId);
            ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
            if (exclusiveSlot != null) ArgumentException.ThrowIfNullOrWhiteSpace(exclusiveSlot);
            Key = key;
            HandlerId = handlerId;
            SchemaVersion = schemaVersion;
            Provides = Copy(provides);
            Requires = Copy(requires);
            Before = Copy(before);
            After = Copy(after);
            ExclusiveSlot = exclusiveSlot;
            UpdatePolicy = updatePolicy ?? CelestialUpdatePolicy.None;
            if (!Enum.IsDefined(failurePolicy)) throw new ArgumentOutOfRangeException(nameof(failurePolicy));
            FailurePolicy = failurePolicy;
        }

        private static ReadOnlyCollection<string> Copy(IEnumerable<string>? values)
        {
            var copied = (values ?? Array.Empty<string>()).ToArray();
            if (copied.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Capability and order identifiers must be nonempty.", nameof(values));
            return Array.AsReadOnly(copied);
        }
    }

    public readonly record struct CelestialUpdateContext(double StartSeconds, double EndSeconds, long StateRevision);

    public sealed class CelestialEvent
    {
        private readonly byte[] payload;
        public string Kind { get; }
        public ReadOnlyMemory<byte> Payload => payload;
        public CelestialEvent(string kind, ReadOnlySpan<byte> payload = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(kind);
            Kind = kind;
            this.payload = payload.ToArray();
        }
    }

    public sealed class CelestialCommand
    {
        private readonly byte[] payload;
        public string Kind { get; }
        public string OperationId { get; }
        public CelestialInstanceId TargetId { get; }
        public Guid WorldSessionId { get; }
        public long ExpectedRevision { get; }
        public ReadOnlyMemory<byte> Payload => payload;

        public CelestialCommand(string kind, string operationId, CelestialInstanceId targetId, Guid worldSessionId,
            long expectedRevision, ReadOnlySpan<byte> payload = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(kind);
            ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
            if (!targetId.IsValid) throw new ArgumentException("Missing target identity.", nameof(targetId));
            if (worldSessionId == Guid.Empty) throw new ArgumentException("Missing world session.", nameof(worldSessionId));
            ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
            Kind = kind;
            OperationId = operationId;
            TargetId = targetId;
            WorldSessionId = worldSessionId;
            ExpectedRevision = expectedRevision;
            this.payload = payload.ToArray();
        }
    }

    public sealed class CelestialRemovalCheck
    {
        public bool Allowed => Blockers.Count == 0;
        public IReadOnlyList<CelestialError> Blockers { get; }
        public CelestialRemovalCheck(IEnumerable<CelestialError> blockers)
        {
            ArgumentNullException.ThrowIfNull(blockers);
            Blockers = Array.AsReadOnly(blockers.ToArray());
            if (Blockers.Any(blocker => blocker == null)) throw new ArgumentException("Null removal blocker.", nameof(blockers));
        }
        public static CelestialRemovalCheck Permit() => new(Array.Empty<CelestialError>());
    }
}
