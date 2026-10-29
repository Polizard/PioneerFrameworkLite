using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    /// <summary>稳定引用名用于保存；返回的释放凭据带加载会话，避免旧回调解除新关联。</summary>
    public sealed class CelestialReferenceIndex
    {
        private readonly CelestialCatalog catalog;
        private readonly Dictionary<string, CelestialReference> references = new(StringComparer.Ordinal);
        private readonly Dictionary<CelestialInstanceId, Dictionary<string, CelestialReference>> byBody = new();
        public Guid WorldSessionId { get; }
        public CelestialReferenceIndex(CelestialCatalog catalog, Guid worldSessionId, IEnumerable<CelestialReference>? restored = null)
        {
            if (worldSessionId == Guid.Empty) throw new ArgumentException("Missing world session.", nameof(worldSessionId));
            this.catalog = catalog;
            WorldSessionId = worldSessionId;
            foreach (var reference in restored ?? Array.Empty<CelestialReference>())
                Add(reference with { WorldSessionId = worldSessionId });
        }
        public CelestialResult<string> Register(CelestialReference reference)
        {
            if (reference.WorldSessionId != WorldSessionId) return Reject<string>("StaleSession", reference.InstanceId);
            if (string.IsNullOrWhiteSpace(reference.ReferenceId) || string.IsNullOrWhiteSpace(reference.OwnerId) || !reference.InstanceId.IsValid || !Enum.IsDefined(reference.Kind))
                return Reject<string>("InvalidReference", reference.InstanceId);
            if (references.TryGetValue(reference.ReferenceId, out var existing))
                return existing.InstanceId == reference.InstanceId && existing.OwnerId == reference.OwnerId && existing.Kind == reference.Kind
                    ? CelestialResult.Ok(Lease(reference.ReferenceId)) : Reject<string>("ReferenceConflict", reference.InstanceId);
            if (reference.ExpectedRevision != catalog.Revision) return Reject<string>("RevisionConflict", reference.InstanceId);
            if (!catalog.TryGetInstance(reference.InstanceId, out var instance)) return Reject<string>("BodyMissing", reference.InstanceId);
            if (instance.Lifecycle == CelestialLifecycle.Removing) return Reject<string>("BodyRemoving", reference.InstanceId);
            if (instance.Lifecycle != CelestialLifecycle.Ready) return Reject<string>("BodyNotReady", reference.InstanceId);
            Add(reference);
            return CelestialResult.Ok(Lease(reference.ReferenceId));
        }
        public CelestialResult<bool> Release(string lease)
        {
            int separator = lease?.IndexOf('/', StringComparison.Ordinal) ?? -1;
            if (separator < 0) return Reject<bool>("InvalidReferenceLease");
            if (lease![..separator] != WorldSessionId.ToString("N")) return Reject<bool>("StaleSession");
            string referenceId = Uri.UnescapeDataString(lease[(separator + 1)..]);
            if (!references.Remove(referenceId, out var removed)) return CelestialResult.Ok(false);
            var body = byBody[removed.InstanceId]; body.Remove(referenceId);
            if (body.Count == 0) byBody.Remove(removed.InstanceId);
            return CelestialResult.Ok(true);
        }
        public IReadOnlyList<CelestialReference> Capture()
            => Array.AsReadOnly(references.Values.OrderBy(reference => reference.ReferenceId, StringComparer.Ordinal).ToArray());
        public IReadOnlyList<CelestialReference> ForBody(CelestialInstanceId id)
            => byBody.TryGetValue(id, out var found) ? Array.AsReadOnly(found.Values.OrderBy(reference => reference.ReferenceId, StringComparer.Ordinal).ToArray()) : Array.Empty<CelestialReference>();
        private void Add(CelestialReference reference)
        {
            references.Add(reference.ReferenceId, reference);
            if (!byBody.TryGetValue(reference.InstanceId, out var body)) byBody.Add(reference.InstanceId, body = new(StringComparer.Ordinal));
            body.Add(reference.ReferenceId, reference);
        }
        private string Lease(string id) => WorldSessionId.ToString("N") + "/" + Uri.EscapeDataString(id);
        private static CelestialResult<T> Reject<T>(string code, CelestialInstanceId? id = null) => CelestialResult.Reject<T>(new CelestialError(code, id));
    }
}
