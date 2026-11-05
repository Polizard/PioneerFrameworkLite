using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    /// <summary>会话绑定的只读入口。Prepare 与提交后回调期间读取冻结的已提交视图；查询不推进模拟。</summary>
    public interface ICelestialQueryService : ICelestialCapabilityResolver
    {
        Guid WorldSessionId { get; }
        PhysicsSampleStamp CurrentStamp { get; }
        CelestialProviderDiagnostics Provider { get; }
        CelestialResult<CelestialBodySnapshot> Query(CelestialInstanceId id);
        CelestialCatalogSnapshot CaptureCatalog();
        IReadOnlyList<CelestialEpochUsage> CaptureEpochUsage();
        /// <summary>按提供者允许的已提交/呈现时刻采样；米、秒、统一惯性参考系，快照由调用方 Dispose。</summary>
        PhysicsSnapshotResult Sample(PhysicsSampleRequest request);
        /// <summary>返回同一读边界的能力成员引用；调用者不可将组件引用作为跨线程可变状态使用。</summary>
        IReadOnlyList<T> GetCapabilities<T>(CelestialInstanceId id) where T : class;
    }
    public sealed record CelestialOperationScope(string OperationId, Guid WorldSessionId, long ExpectedRevision, long Epoch = 0)
    {
        public string QualifiedOperationId => CelestialOperationEpochs.Qualify(Epoch, OperationId);
        public CelestialOperationContext ToContext() => new(QualifiedOperationId, WorldSessionId, ExpectedRevision);
    }
    public sealed record CelestialCreateRequest(CelestialOperationScope Scope, string DefinitionId, string GenerationKey,
        ulong? Seed = null, CelestialInstanceId? ParentId = null, IReadOnlyDictionary<string, CelestialInstanceId>? InitializationReferences = null,
        IReadOnlyList<CelestialParameterOverride>? ParameterOverrides = null);
    public sealed record CelestialParameterOverride(string CompKey, string ParameterKey, double? Value = null, double? Min = null, double? Max = null);
    public sealed class CelestialCreateBatchRequest
    {
        public CelestialOperationScope Scope { get; }
        public IReadOnlyList<CelestialCreateItem> Items { get; }
        public CelestialCreateBatchRequest(CelestialOperationScope scope, IEnumerable<CelestialCreateItem> items)
        {
            Scope = scope;
            Items = Array.AsReadOnly(System.Linq.Enumerable.Select(items, item => item with {
                InitializationReferences = new System.Collections.ObjectModel.ReadOnlyDictionary<string, CelestialInstanceId>(
                    new Dictionary<string, CelestialInstanceId>(item.InitializationReferences ?? new Dictionary<string, CelestialInstanceId>(), StringComparer.Ordinal)),
                ParameterOverrides = Array.AsReadOnly((item.ParameterOverrides ?? Array.Empty<CelestialParameterOverride>()).ToArray()) }).ToArray());
        }
    }
    public sealed record CelestialCreateItem(string Key, string DefinitionId, ulong? Seed = null, CelestialInstanceId? ParentId = null,
        IReadOnlyDictionary<string, CelestialInstanceId>? InitializationReferences = null, IReadOnlyList<CelestialParameterOverride>? ParameterOverrides = null);
}
