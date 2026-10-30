#if !PURE_MATH
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PioneerFramework.Celestial;

namespace PioneerFramework.TestSupport.Celestial;

internal static class CelestialBenchFactory
{
    internal static readonly Guid Session = Guid.Parse("28c10000-0000-4000-8000-000000000001");
    internal static CelestialInstanceId Id(int index) => CelestialInstanceId.Parse("bench:body-" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture));
    internal static CelestialRuntime Create(int count, bool pending = false, bool stateful = false, bool visuals = false, bool commands = false,
        Func<CelestialBodyTemplate, CelestialBodyTemplate>? transform = null, System.Collections.Generic.IEnumerable<ICelestialStepParticipant>? participants = null,
        Func<ExistingCelestialEphemerisBridge, ICelestialPhysicsBridge>? bridgeTransform = null)
    {
        var provider = new ExistingCelestialEphemerisBridge(Session);
        var prototype = transform?.Invoke(Template(1000, stateful, visuals)) ?? Template(1000, stateful, visuals);
        var runtime = new CelestialRuntime(bridgeTransform?.Invoke(provider) ?? provider, provider.CommittedStamp, new Resources(), 17,
            _ => CelestialResult.Ok(prototype), participants);
        if (commands) Require(runtime.RegisterCommandHandler("bench.increment", new Increment(runtime)).Success, "Handler registration failed.");
        var items = Enumerable.Range(0, count).Select(index => {
            var template = Template(1000 + index, stateful, visuals);
            if (transform != null) template = transform(template);
            return new CelestialSpawnItem(Id(index), "body", index, "body[" + index + "]", template,
                template.Resolve(new CelestialGenerationContext(17, "body[" + index + "]")).Value,
                index == 0 ? null : Id(0), new Dictionary<string, CelestialInstanceId> { ["frameOrigin"] = Id(0) });
        }).ToArray();
        var operation = runtime.CreateBatch(new(new("bench/bootstrap", Session, 0), new("bench", 17, null, items)));
        Require(operation.Status == CelestialOperationStatus.Pending, "Benchmark creation rejected.");
        if (!pending) Require(runtime.Advance(new(Session, 0, 0, 0)).Success, "Benchmark bootstrap failed.");
        return runtime;
    }

    internal static CelestialBodyTemplate Template(double radius, bool stateful, bool visuals = false)
    {
        var binding = new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = ExistingCelestialEphemerisBridge.Id,
            modeId = "existing.circular.v1", gravitationalParameter = new() { value = 1e8 },
            initializationPayload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new {
                orbitRadius = 0d, orbitPeriod = 0d, initialPhase = 0d, orbitNormal = new[] { 0, 1, 0 },
                dayLengthTicks = 60000d, bodyType = "Planet", frameOriginReference = "frameOrigin" })) };
        var definition = new CelestialBodyDef { defName = "PF_BenchBody", label = "benchmark", comps = new() {
            new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = radius } }, binding } };
        if (visuals) definition.comps.Add(new CompProperties_CelestialVisual { key = "visual",
            profile = new CelestialVisualProfileDef { defName = "PF_BenchVisual" } });
        var template = definition.ToTemplate().Value;
        if (!stateful) return template;
        var descriptor = new CelestialCompDescriptor("counter", "PFL.Bench.Counter", 1, requires: new[] { "geometry" },
            updatePolicy: CelestialUpdatePolicy.Every(1));
        return CelestialBodyTemplate.Create(template.DefinitionId, template.Label, template.Category, template.Tags,
            template.Components.Append(new CelestialCompDefinition(descriptor, new CounterFactory(descriptor),
                _ => CelestialResult.Ok<ICelestialResolvedProperties>(new CounterProperties())))).Value;
    }

    internal static void Advance(CelestialRuntime runtime, double seconds)
    {
        var stamp = runtime.CurrentStamp;
        var result = runtime.Advance(new(runtime.WorldSessionId, stamp.TopologyRevision, stamp.PhysicalSeconds, seconds));
        Require(result.Success, string.Join(",", result.Errors.Select(error => error.Code)));
    }
    internal static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Resources : ICelestialRemovalResources
    {
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id) => Array.Empty<CelestialError>();
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => CelestialResult.Ok(true);
    }
    private sealed class CounterProperties : ICelestialResolvedProperties
    {
        public string Key => "counter";
        public string HandlerId => "PFL.Bench.Counter";
        public int Version => 1;
        public ReadOnlyMemory<byte> Serialize() => JsonSerializer.SerializeToUtf8Bytes(new { Initial = 0 });
    }
    private sealed class CounterFactory(CelestialCompDescriptor descriptor) : ICelestialCompFactory
    { public CelestialBodyComp Create(ICelestialResolvedProperties properties) => new Counter(descriptor); }
    private sealed class Counter(CelestialCompDescriptor descriptor) : CelestialBodyComp(descriptor)
    {
        private long count;
        public override void Advance(CelestialUpdateContext update) => count++;
        public override void Handle(CelestialEvent message) => count++;
        public override ReadOnlyMemory<byte> CaptureState() => BitConverter.GetBytes(count);
    }
    private sealed class Increment(CelestialRuntime runtime) : ICelestialCommandHandler
    {
        public CelestialResult<bool> Execute(CelestialCommand command)
            => runtime.EnqueueEvent(runtime.WorldSessionId, command.TargetId, new CelestialEvent("increment"), "counter");
    }
}
#endif
