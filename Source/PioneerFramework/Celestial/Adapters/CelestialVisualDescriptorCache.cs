using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PioneerFramework.Celestial
{
    internal sealed class CelestialVisualDescriptorCache(CelestialRuntime runtime)
    {
        private readonly Dictionary<CelestialInstanceId, Entry> entries = new();
        private long catalogRevision = -1;
        internal void Forget(CelestialInstanceId id) => entries.Remove(id);
        internal void Prune()
        {
            var catalog = runtime.CaptureCatalog();
            if (catalogRevision == catalog.Revision) return;
            foreach (var id in entries.Keys.Where(id => !catalog.TryGet(id, out _)).ToArray()) entries.Remove(id);
            catalogRevision = catalog.Revision;
        }
        internal Entry Get(CelestialBodyDescription description, int? layerId)
        {
            long faultRevision = runtime.Creation.FaultRevision;
            bool sameConfiguration = entries.TryGetValue(description.Id, out var entry) &&
                entry.Key.DescriptorRevision == description.DescriptorRevision && entry.FaultRevision == faultRevision;
            if (sameConfiguration && entry!.Key.LayerId == layerId && VisualsCurrent(entry)) return entry;
            // Configuration is immutable between descriptor/fault revisions; render time only changes the physics sample.
            var components = sameConfiguration ? entry!.Components : runtime.GetCapabilities<CelestialBodyComp>(description.Id);
            var capabilities = sameConfiguration ? entry!.Capabilities : components.OfType<ICelestialVisualCapability>().ToArray();
            var visuals = Array.AsReadOnly(capabilities.Select(capability => capability.GetVisualDescription()).OrderBy(visual => visual.CompKey, StringComparer.Ordinal).ToArray());
            var surface = components.Select(component => component.ResolvedProperties).OfType<SurfaceResolvedProperties>().SingleOrDefault();
            var atmosphere = components.OfType<ICelestialAtmosphereCapability>().SingleOrDefault()?.GetProfile() ?? default;
            var environment = components.Select(component => component.ResolvedProperties).OfType<ExistingEnvironmentResolvedProperties>().SingleOrDefault();
            CelestialInstanceId? primaryLight = runtime.TryGetInitializationReference(description.Id, environment?.Profile.PrimaryLightReference ?? "primaryLight", out var light) ? light : null;
            runtime.Catalog.TryGetInstance(description.Id, out var instance);
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write("PFL.Celestial.Visual.v1");
                writer.Write(instance.GenerationOrigin.WorldSeed); writer.Write(instance.GenerationOrigin.GenerationKey); writer.Write(instance.GenerationOrigin.AlgorithmVersion);
                var configuration = CelestialGenerationConfiguration.Encode(instance.CompStateRecords.Values);
                writer.Write(configuration.Length); writer.Write(configuration.Span);
                foreach (var visual in visuals)
                {
                    writer.Write(visual.CompKey); writer.Write((int)visual.Kind);
                    CelestialCostCounters.VisualProfileEncoded();
                    var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(visual.Profile);
                    writer.Write(payload.Length); writer.Write(payload);
                }
            }
            CelestialCostCounters.VisualHashComputed();
            var key = new CelestialViewCacheKey(runtime.WorldSessionId, description.Id, description.DescriptorRevision, layerId, Convert.ToHexString(SHA256.HashData(stream.ToArray())));
            entry = new(key, visuals, surface == null ? null : description.RadiusMeters / surface.MetersPerLayerUnit, components, capabilities,
                capabilities.Select(capability => capability.VisualRevision).ToArray(), faultRevision, atmosphere, CelestialDayLength.ResolveFrom(components), primaryLight);
            entries[description.Id] = entry;
            return entry;
        }
        internal bool TryGet(CelestialViewSnapshot snapshot, out Entry entry)
        {
            if (entries.TryGetValue(snapshot.Description.Id, out entry!) && entry.Key == snapshot.CacheKey) return true;
            entry = null!;
            return false;
        }
        internal bool IsCurrent()
        {
            long faults = runtime.Creation.FaultRevision;
            foreach (var entry in entries.Values)
                if (entry.FaultRevision != faults || !VisualsCurrent(entry)) return false;
            return true;
        }
        private static bool VisualsCurrent(Entry entry)
        {
            for (int index = 0; index < entry.Capabilities.Count; index++)
                if (entry.Capabilities[index].VisualRevision != entry.Revisions[index]) return false;
            return true;
        }
        internal sealed record Entry(CelestialViewCacheKey Key, IReadOnlyList<CelestialVisualDescription> Visuals, double? NativeRadius,
            IReadOnlyList<CelestialBodyComp> Components, IReadOnlyList<ICelestialVisualCapability> Capabilities, long[] Revisions,
            long FaultRevision, AtmosphereProfile Atmosphere, double DayLengthTicks, CelestialInstanceId? PrimaryLightId);
    }
}
