using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public static class CelestialSystemGenerator
    {
        public static CelestialResult<CelestialSpawnManifest> Expand(CelestialSystemTemplate template, ulong worldSeed)
        {
            ArgumentNullException.ThrowIfNull(template);
            var ordered = OrderEntries(template);
            if (!ordered.Success) return CelestialResult.Reject<CelestialSpawnManifest>(ordered.Errors.ToArray());
            var byEntry = new Dictionary<string, List<CelestialSpawnItem>>(StringComparer.Ordinal);
            var items = new List<CelestialSpawnItem>();
            foreach (var entry in ordered.Value)
            {
                if (entry.Count.Minimum < 0) return Reject("InvalidSpawnCount", entry.Key);
                var generated = new List<CelestialSpawnItem>();
                IEnumerable<CelestialSpawnItem?> parents = entry.ParentEntry == null
                    ? new CelestialSpawnItem?[] { null } : byEntry[entry.ParentEntry];
                foreach (var parent in parents)
                {
                    string path = (parent == null ? "" : parent.GenerationPath + "/") + Uri.EscapeDataString(entry.Key);
                    int count = new CelestialGenerationContext(worldSeed, path).ForComp("$entry").SampleInt("count", entry.Count);
                    if ((long)items.Count + generated.Count + count > template.MaxInstances) return Reject("SpawnLimitExceeded", path);
                    for (int index = 0; index < count; index++)
                    {
                        string instancePath = path + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
                        var id = CelestialInstanceId.Parse(Uri.EscapeDataString(template.DefinitionId) + ":" + instancePath);
                        var resolved = entry.Body.Resolve(new CelestialGenerationContext(worldSeed, instancePath));
                        if (!resolved.Success)
                            return CelestialResult.Reject<CelestialSpawnManifest>(resolved.Errors.Select(error => error with { InstanceId = id }).ToArray());
                        generated.Add(new CelestialSpawnItem(id, entry.Key, index, instancePath, entry.Body, resolved.Value, parent?.Id));
                    }
                }
                byEntry.Add(entry.Key, generated);
                items.AddRange(generated);
            }
            if (!byEntry.TryGetValue(template.RootSurfaceEntry ?? "", out var roots) || roots.Count == 0) return Reject("MissingRootSurface");
            if (roots.Count != 1) return Reject("AmbiguousRootSurface");
            if (!roots[0].Template.HasCapability("surface")) return Reject("RootHasNoSurface");
            var bound = new List<CelestialSpawnItem>();
            var entryMap = template.Entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
            foreach (var item in items)
            {
                var references = new Dictionary<string, CelestialInstanceId>(StringComparer.Ordinal);
                foreach (var pair in entryMap[item.EntryKey].InitializationReferences.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)) return Reject("InvalidInitializationReference", item.GenerationPath);
                    var target = CelestialSpawnReferences.Resolve(pair.Value, new CelestialSpawnContext(items, item));
                    if (!target.Success)
                        return CelestialResult.Reject<CelestialSpawnManifest>(target.Errors.Select(error => error with { InstanceId = item.Id, Detail = pair.Key }).ToArray());
                    references.Add(pair.Key, target.Value);
                }
                bound.Add(new CelestialSpawnItem(item.Id, item.EntryKey, item.Index, item.GenerationPath, item.Template,
                    item.Components, item.ParentId, references));
            }
            return CelestialResult.Ok(new CelestialSpawnManifest(template.DefinitionId, worldSeed, roots[0].Id, bound));
        }

        internal static CelestialResult<IReadOnlyList<CelestialSystemEntry>> OrderEntries(CelestialSystemTemplate template)
        {
            var entries = new Dictionary<string, CelestialSystemEntry>(StringComparer.Ordinal);
            foreach (var entry in template.Entries)
            {
                if (entry == null) return RejectOrder("InvalidSystemEntry");
                if (!entries.TryAdd(entry.Key, entry)) return RejectOrder("DuplicateEntryKey", entry.Key);
            }
            foreach (var entry in entries.Values)
                if (entry.ParentEntry != null && !entries.ContainsKey(entry.ParentEntry)) return RejectOrder("MissingParentEntry", entry.Key);
            var result = new List<CelestialSystemEntry>();
            var pending = new SortedSet<string>(entries.Keys, StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (pending.Count > 0)
            {
                string? next = pending.FirstOrDefault(key => entries[key].ParentEntry == null || visited.Contains(entries[key].ParentEntry!));
                if (next == null) return RejectOrder("ParentEntryCycle");
                pending.Remove(next);
                visited.Add(next);
                result.Add(entries[next]);
            }
            return CelestialResult.Ok<IReadOnlyList<CelestialSystemEntry>>(result.AsReadOnly());
        }

        private static CelestialResult<CelestialSpawnManifest> Reject(string code, string detail = "")
            => CelestialResult.Reject<CelestialSpawnManifest>(new CelestialError(code, Detail: detail));
        private static CelestialResult<IReadOnlyList<CelestialSystemEntry>> RejectOrder(string code, string detail = "")
            => CelestialResult.Reject<IReadOnlyList<CelestialSystemEntry>>(new CelestialError(code, Detail: detail));
    }
}
