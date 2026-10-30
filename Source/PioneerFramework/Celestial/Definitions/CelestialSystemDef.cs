using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialSystemDef : Def
    {
        public string rootSurfaceEntry = "";
        public int maxInstances = 10000;
        public List<CelestialSystemEntryXml> entries = new();

        public CelestialResult<CelestialSystemTemplate> ToTemplate()
        {
            if (string.IsNullOrWhiteSpace(rootSurfaceEntry)) return Reject("MissingRootSurface");
            if (entries == null || maxInstances < 1) return Reject("InvalidSystemConfiguration");
            var result = new List<CelestialSystemEntry>();
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.key) || entry.bodyDef == null || entry.count == null ||
                    entry.initializationReferences == null || entry.compOverrides == null)
                    return Reject("InvalidSystemEntry");
                var body = entry.bodyDef.ToTemplate();
                if (!body.Success) return CelestialResult.Reject<CelestialSystemTemplate>(body.Errors.ToArray());
                if (entry.compOverrides.Count > 0)
                {
                    var definitions = new List<CelestialCompDefinition>();
                    foreach (var properties in entry.compOverrides)
                    {
                        if (properties == null || properties.ConfigErrors().Any()) return Reject("InvalidCompOverride");
                        definitions.Add(properties.ToDefinition());
                    }
                    body = body.Value.WithOverrides(definitions);
                    if (!body.Success) return CelestialResult.Reject<CelestialSystemTemplate>(body.Errors.ToArray());
                }
                var count = entry.count.ToSpec();
                if (!count.Success) return CelestialResult.Reject<CelestialSystemTemplate>(count.Errors.ToArray());
                if (count.Value.Minimum < 0) return Reject("InvalidSpawnCount");
                var references = new Dictionary<string, CelestialSpawnReference>(StringComparer.Ordinal);
                foreach (var reference in entry.initializationReferences)
                {
                    if (reference == null || string.IsNullOrWhiteSpace(reference.key) || string.IsNullOrWhiteSpace(reference.entry) ||
                        reference.index < 0 || !Enum.IsDefined(reference.scope)) return Reject("InvalidSpawnReference");
                    if (!references.TryAdd(reference.key, new CelestialSpawnReference(reference.entry, reference.index, reference.scope)))
                        return Reject("DuplicateInitializationReference");
                }
                result.Add(new CelestialSystemEntry(entry.key, body.Value, count.Value, entry.parentEntry, references));
            }
            var template = new CelestialSystemTemplate(defName, rootSurfaceEntry, result, maxInstances);
            var ordered = CelestialSystemGenerator.OrderEntries(template);
            return ordered.Success ? CelestialResult.Ok(template) : CelestialResult.Reject<CelestialSystemTemplate>(ordered.Errors.ToArray());
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            foreach (var error in ToTemplate().Errors) yield return error.Code + ": " + error.Detail;
        }
        private static CelestialResult<CelestialSystemTemplate> Reject(string code)
            => CelestialResult.Reject<CelestialSystemTemplate>(new CelestialError(code));
    }

    public sealed class CelestialSystemEntryXml
    {
        public string key = "";
        public CelestialBodyDef bodyDef = null!;
        public CelestialIntParameterXml count = new() { value = 1 };
        public string? parentEntry;
        public List<CelestialSpawnReferenceXml> initializationReferences = new();
        public List<CelestialCompProperties> compOverrides = new();
    }

    public sealed class CelestialSpawnReferenceXml
    {
        public string key = "";
        public string entry = "";
        public int? index;
        public CelestialSpawnReferenceScope scope;
    }
}
