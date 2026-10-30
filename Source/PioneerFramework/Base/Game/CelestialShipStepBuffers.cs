using System;
using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>One private graph per host. Domain identities stay live; preparation overwrites every input before reuse.</summary>
    internal sealed class CelestialShipStepBuffers
    {
        internal SpaceGameComp? Prepared { get; set; }
        internal readonly CelestialPrivateContainers Containers = new();
        internal Dictionary<int, SpatialGrid3D<ISpatialEntity>> PublishedGrids = new();
        internal readonly Dictionary<int, object> SourceMaps = new();
    }
    internal sealed class CelestialPrivateContainers
    {
        private readonly Dictionary<string, object> spares = new(StringComparer.Ordinal);
        internal T Choose<T>(string key, T source, T? current, Func<T> create) where T : class
        {
            if (current != null && !ReferenceEquals(current, source)) return current;
            var target = spares.TryGetValue(key, out var spare) && !ReferenceEquals(spare, source) ? (T)spare : create();
            if (current != null) spares[key] = current;
            return target;
        }
        internal List<T> List<T>(string key, List<T> source, List<T>? current)
        { var target = Choose(key, source, current, () => new List<T>(source.Count)); target.Clear(); target.AddRange(source); return target; }
        internal HashSet<T> Set<T>(string key, HashSet<T> source, HashSet<T>? current)
        { var target = Choose(key, source, current, () => new HashSet<T>(source.Comparer)); target.Clear(); target.UnionWith(source); return target; }
        internal List<T> Project<T>(string key, List<T> source, List<T>? current, Func<T, T?, T> transform)
        {
            var target = Choose(key, source, current, () => new List<T>(source.Count));
            for (int index = 0; index < source.Count; index++)
            {
                var value = transform(source[index], index < target.Count ? target[index] : default);
                if (index < target.Count) target[index] = value; else target.Add(value);
            }
            if (target.Count > source.Count) target.RemoveRange(source.Count, target.Count - source.Count);
            return target;
        }
        internal Dictionary<K, V> Map<K, V>(string key, Dictionary<K, V> source, Dictionary<K, V>? current,
            Func<V, V?, V>? transform = null) where K : notnull
        {
            var target = Choose(key, source, current, () => new Dictionary<K, V>(source.Count, source.Comparer));
            // .NET supports removal during Dictionary enumeration; no temporary key array on the steady path.
            foreach (var existing in target.Keys) if (!source.ContainsKey(existing)) target.Remove(existing);
            foreach (var pair in source)
            {
                target.TryGetValue(pair.Key, out var previous);
                target[pair.Key] = transform == null ? pair.Value : transform(pair.Value, previous);
            }
            return target;
        }
    }
}
