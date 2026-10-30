using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialSystemEntry
    {
        public string Key { get; }
        public CelestialBodyTemplate Body { get; }
        public IntParameterSpec Count { get; }
        public string? ParentEntry { get; }
        public IReadOnlyDictionary<string, CelestialSpawnReference> InitializationReferences { get; }
        public CelestialSystemEntry(string key, CelestialBodyTemplate body, IntParameterSpec count, string? parentEntry = null,
            IReadOnlyDictionary<string, CelestialSpawnReference>? initializationReferences = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            Key = key;
            Body = body ?? throw new ArgumentNullException(nameof(body));
            Count = count ?? throw new ArgumentNullException(nameof(count));
            ParentEntry = parentEntry;
            InitializationReferences = new ReadOnlyDictionary<string, CelestialSpawnReference>(
                new Dictionary<string, CelestialSpawnReference>(initializationReferences ?? new Dictionary<string, CelestialSpawnReference>(), StringComparer.Ordinal));
        }
    }

    public sealed class CelestialSystemTemplate
    {
        public string DefinitionId { get; }
        public string RootSurfaceEntry { get; }
        public IReadOnlyList<CelestialSystemEntry> Entries { get; }
        public int MaxInstances { get; }
        public CelestialSystemTemplate(string definitionId, string rootSurfaceEntry, IEnumerable<CelestialSystemEntry> entries, int maxInstances = 10000)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxInstances, 1);
            DefinitionId = definitionId;
            RootSurfaceEntry = rootSurfaceEntry;
            Entries = Array.AsReadOnly(entries.ToArray());
            MaxInstances = maxInstances;
        }
    }

    public sealed class CelestialSpawnItem
    {
        public CelestialInstanceId Id { get; }
        public string EntryKey { get; }
        public int Index { get; }
        public string GenerationPath { get; }
        public CelestialBodyTemplate Template { get; }
        public CelestialInstanceId? ParentId { get; }
        public IReadOnlyList<CelestialResolvedComp> Components { get; }
        public IReadOnlyDictionary<string, CelestialInstanceId> InitializationReferences { get; }
        public CelestialSpawnItem(CelestialInstanceId id, string entryKey, int index, string generationPath,
            CelestialBodyTemplate template, IEnumerable<CelestialResolvedComp> components, CelestialInstanceId? parentId = null,
            IReadOnlyDictionary<string, CelestialInstanceId>? initializationReferences = null)
        {
            if (!id.IsValid) throw new ArgumentException("Invalid instance identity.", nameof(id));
            ArgumentException.ThrowIfNullOrWhiteSpace(entryKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(generationPath);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            Id = id;
            EntryKey = entryKey;
            Index = index;
            GenerationPath = generationPath;
            Template = template ?? throw new ArgumentNullException(nameof(template));
            Components = Array.AsReadOnly(components.ToArray());
            ParentId = parentId;
            InitializationReferences = new ReadOnlyDictionary<string, CelestialInstanceId>(
                new Dictionary<string, CelestialInstanceId>(initializationReferences ?? new Dictionary<string, CelestialInstanceId>(), StringComparer.Ordinal));
        }
    }

    public sealed class CelestialSpawnManifest
    {
        public string SystemDefinitionId { get; }
        public ulong WorldSeed { get; }
        public CelestialInstanceId? RootSurfaceId { get; }
        public IReadOnlyList<CelestialSpawnItem> Items { get; }
        public CelestialSpawnManifest(string systemDefinitionId, ulong worldSeed, CelestialInstanceId? rootSurfaceId, IEnumerable<CelestialSpawnItem> items)
        {
            SystemDefinitionId = systemDefinitionId;
            WorldSeed = worldSeed;
            RootSurfaceId = rootSurfaceId;
            Items = Array.AsReadOnly(items.OrderBy(item => item.GenerationPath, StringComparer.Ordinal).ToArray());
        }
    }
}
