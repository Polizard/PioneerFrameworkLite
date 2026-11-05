using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public enum CelestialSpawnReferenceScope { Global, Siblings, Parent, Self }
    public sealed record CelestialSpawnReference(string EntryKey, int? Index = null, CelestialSpawnReferenceScope Scope = CelestialSpawnReferenceScope.Global);
    public sealed class CelestialSpawnContext
    {
        public IReadOnlyList<CelestialSpawnItem> Items { get; }
        public CelestialSpawnItem Current { get; }
        public CelestialSpawnContext(IReadOnlyList<CelestialSpawnItem> items, CelestialSpawnItem current)
        {
            Items = items;
            Current = current;
        }
    }

    public static class CelestialSpawnReferences
    {
        public static CelestialResult<CelestialInstanceId> Resolve(CelestialSpawnReference reference, CelestialSpawnContext context)
        {
            if (reference == null || string.IsNullOrWhiteSpace(reference.EntryKey) || reference.Index < 0 || !Enum.IsDefined(reference.Scope))
                return Reject("InvalidSpawnReference");
            var candidates = context.Items.Where(item => item.EntryKey == reference.EntryKey);
            candidates = reference.Scope switch
            {
                CelestialSpawnReferenceScope.Siblings => candidates.Where(item => item.ParentId == context.Current.ParentId),
                CelestialSpawnReferenceScope.Parent => candidates.Where(item => item.Id == context.Current.ParentId),
                CelestialSpawnReferenceScope.Self => candidates.Where(item => item.Id == context.Current.Id),
                _ => candidates,
            };
            if (reference.Index.HasValue) candidates = candidates.Where(item => item.Index == reference.Index.Value);
            var selected = candidates.Take(2).ToArray();
            return selected.Length == 1 ? CelestialResult.Ok(selected[0].Id) : Reject(selected.Length == 0 ? "MissingReference" : "AmbiguousReference");
        }

        private static CelestialResult<CelestialInstanceId> Reject(string code)
            => CelestialResult.Reject<CelestialInstanceId>(new CelestialError(code));
    }
}
