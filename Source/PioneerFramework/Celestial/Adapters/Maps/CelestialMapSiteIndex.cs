using System.Collections.Generic;
using System.Linq;
using System;
using PioneerFramework.MapGeneration;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialMapSiteIndex
    {
        private readonly Dictionary<(CelestialInstanceId, int, int), CelestialMapSiteRecord> sites = new();
        private static (CelestialInstanceId, int, int) Key(CelestialMapAddress address) => (address.InstanceId, address.LayerId, address.TileId);
        internal void CompactEpoch(long epoch)
        {
            bool Keep(string id) => !CelestialOperationEpochs.TryGetEpoch(id, out long owner) || owner != epoch;
            foreach (var key in sites.Keys)
            {
                var site = sites[key];
                sites[key] = site with { OperationIds = Array.AsReadOnly(site.OperationIds.Where(Keep).ToArray()),
                    RetiredOperationIds = Array.AsReadOnly(site.RetiredOperationIds.Where(Keep).ToArray()) };
            }
        }
        public IReadOnlyList<CelestialMapSiteRecord> Capture() => sites.Values.OrderBy(site => site.Address.InstanceId.Value, System.StringComparer.Ordinal)
            .ThenBy(site => site.Address.LayerId).ThenBy(site => site.Address.TileId).ToArray();
        public bool TryGet(CelestialMapAddress address, out CelestialMapSiteRecord record) => sites.TryGetValue(Key(address), out record!);
        internal CelestialMapSiteRecord? FindFailedSite(int parentId, int? mapId) =>
            sites.Values.SingleOrDefault(site => !site.ParentRetired && site.Failure != null && site.MapParentId == parentId &&
                (!mapId.HasValue || !site.MapId.HasValue || site.MapId == mapId));
        public CelestialResult<bool> Bind(CelestialMapSiteRecord record)
        {
            if (record.Address.MapParentId.HasValue && record.Address.MapParentId != record.MapParentId || record.MapParentId < 0 || record.MapId < 0)
                return CelestialResult.Reject<bool>(new CelestialError("MapIdentityMismatch", record.Address.InstanceId));
            if (record.OperationIds == null || record.RetiredOperationIds == null || record.ParentRetired && record.OperationIds.Count != 0 ||
                record.OperationIds.Concat(record.RetiredOperationIds).Any(string.IsNullOrWhiteSpace) ||
                record.OperationIds.Intersect(record.RetiredOperationIds, StringComparer.Ordinal).Any())
                return CelestialResult.Reject<bool>(new CelestialError("MapIdentityMismatch", record.Address.InstanceId));
            sites.TryGetValue(Key(record.Address), out var old);
            if (old != null && record.OperationIds.Intersect(old.RetiredOperationIds, StringComparer.Ordinal).Any())
                return CelestialResult.Reject<bool>(new CelestialError("MapIdentityMismatch", record.Address.InstanceId));
            if (old != null && (!old.ParentRetired && (old.MapParentId != record.MapParentId || old.MapId.HasValue && old.MapId != record.MapId) ||
                old.ParentRetired && old.MapParentId == record.MapParentId && !record.ParentRetired))
                return CelestialResult.Reject<bool>(new CelestialError("MapIdentityMismatch", record.Address.InstanceId));
            if (sites.Values.Any(site => Key(site.Address) != Key(record.Address) && (site.MapParentId == record.MapParentId || record.MapId.HasValue && site.MapId == record.MapId)))
                return CelestialResult.Reject<bool>(new CelestialError("MapIdentityMismatch", record.Address.InstanceId));
            var failure = old is { ParentRetired: false } ? old.Failure ?? record.Failure : record.Failure;
            var generation = old is { ParentRetired: false, Failure: not null } ? old.Generation ?? record.Generation : record.Generation ?? (old is { ParentRetired: false } ? old.Generation : null);
            if (failure != null && generation is { State: MapGenerationState.Ready })
                generation = generation with { State = MapGenerationState.Failed, Stage = "Publication", Error = failure };
            sites[Key(record.Address)] = record with { Address = record.Address with { MapParentId = record.MapParentId },
                Failure = failure,
                Generation = generation,
                OperationIds = Array.AsReadOnly((old?.OperationIds ?? Array.Empty<string>()).Concat(record.OperationIds).Distinct(StringComparer.Ordinal).ToArray()),
                RetiredOperationIds = Array.AsReadOnly((old?.RetiredOperationIds ?? Array.Empty<string>()).Concat(record.RetiredOperationIds).Distinct(StringComparer.Ordinal).ToArray()) };
            return CelestialResult.Ok(true);
        }
        internal CelestialResult<bool> RecordOperation(CelestialMapAddress address, string operationId)
        {
            if (!sites.TryGetValue(Key(address), out var site) || site.ParentRetired)
                return CelestialResult.Reject<bool>(new CelestialError("MapIdentityMismatch", address.InstanceId));
            return Bind(site with { OperationIds = site.OperationIds.Append(operationId).ToArray() });
        }
        internal CelestialResult<bool> RetireMap(CelestialMapAddress address, int mapId, bool preserveFailure)
        {
            if (!sites.TryGetValue(Key(address), out var site)) return CelestialResult.Ok(true);
            if (site.MapParentId != address.MapParentId || site.MapId.HasValue && site.MapId != mapId)
                return CelestialResult.Reject<bool>(new CelestialError("MapIdentityMismatch", address.InstanceId));
            if (!site.MapId.HasValue) return CelestialResult.Ok(true);
            sites[Key(address)] = site with { MapId = null, Failure = preserveFailure ? site.Failure : null,
                Generation = preserveFailure ? site.Generation : null, OperationIds = Array.Empty<string>(),
                RetiredOperationIds = Array.AsReadOnly(site.RetiredOperationIds.Concat(site.OperationIds).Distinct(StringComparer.Ordinal).ToArray()) };
            return CelestialResult.Ok(true);
        }
        internal CelestialResult<bool> RetireParent(CelestialMapAddress address)
        {
            if (!sites.TryGetValue(Key(address), out var site)) return CelestialResult.Ok(true);
            if (site.MapParentId != address.MapParentId) return CelestialResult.Reject<bool>(new CelestialError("MapIdentityMismatch", address.InstanceId));
            sites[Key(address)] = site with { ParentRetired = true, OperationIds = Array.Empty<string>(),
                RetiredOperationIds = Array.AsReadOnly(site.RetiredOperationIds.Concat(site.OperationIds).Distinct(StringComparer.Ordinal).ToArray()) };
            return CelestialResult.Ok(true);
        }
    }
}
