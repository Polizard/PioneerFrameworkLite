using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PioneerFramework.Celestial;

namespace PFL.CelestialCompSample
{
    /// <summary>已采样配置独立于 XML 属性对象；保存该结果，恢复时直接解码。</summary>
    public sealed class SampleCounterProperties : ICelestialResolvedProperties
    {
        public const string Handler = "PFL.Sample.Counter";
        public string Key { get; }
        public string HandlerId => Handler;
        public int Version => 1;
        public int InitialCount { get; }
        public SampleCounterProperties(string key, int initialCount)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentOutOfRangeException.ThrowIfNegative(initialCount);
            Key = key; InitialCount = initialCount;
        }
        public ReadOnlyMemory<byte> Serialize() => JsonSerializer.SerializeToUtf8Bytes(new Configuration(InitialCount));
        public static CelestialCompDescriptor DescriptorFor(string key) => new(key, Handler, 1,
            provides: new[] { "sample.counter" }, updatePolicy: CelestialUpdatePolicy.EventsOnly);
        public static CelestialResult<ICelestialResolvedProperties> Decode(string key, int version, ReadOnlyMemory<byte> payload)
        {
            if (version != 1) return CelestialResult.Reject<ICelestialResolvedProperties>(new CelestialError("UnsupportedConfigurationVersion", CompKey: key));
            try
            {
                var data = JsonSerializer.Deserialize<Configuration>(payload.Span, Options) ?? throw new JsonException("Missing counter configuration.");
                return CelestialResult.Ok<ICelestialResolvedProperties>(new SampleCounterProperties(key, data.InitialCount));
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            { return CelestialResult.Reject<ICelestialResolvedProperties>(new CelestialError("InvalidCounterConfiguration", CompKey: key, Detail: exception.Message)); }
        }
        private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        private sealed record Configuration([property: JsonRequired] int InitialCount);
    }

    public sealed class CompProperties_SampleCounter : CelestialCompProperties
    {
        public CelestialIntParameterXml initialCount = new() { value = 3 };
        public override string HandlerId => SampleCounterProperties.Handler;
        protected override IEnumerable<string> Provides => new[] { "sample.counter" };
        protected override CelestialUpdatePolicy DefaultUpdatePolicy => CelestialUpdatePolicy.EventsOnly;
        public override CelestialBodyComp CreateComp() => new SampleCounterComp(Descriptor);
        public override CelestialResult<ICelestialResolvedProperties> Resolve(CelestialGenerationContext context)
        {
            string[] errors = ConfigErrors().ToArray();
            if (errors.Length > 0) return CelestialResult.Reject<ICelestialResolvedProperties>(errors.Select(error => new CelestialError(error, CompKey: key)).ToArray());
            int initial = context.SampleInt(nameof(initialCount), initialCount.ToSpec().Value);
            return CelestialResult.Ok<ICelestialResolvedProperties>(new SampleCounterProperties(key, initial));
        }
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            var specification = initialCount?.ToSpec();
            if (specification?.Success != true || specification.Value.Minimum < 0) yield return "InvalidCounterInitialRange";
            if (updateIntervalSeconds != 0) yield return "CounterUsesEventsOnly";
        }
    }

    public sealed class SampleCounterComp(CelestialCompDescriptor descriptor) : CelestialBodyComp(descriptor)
    {
        public const string IncrementEvent = "SampleIncrement";
        public long Counter { get; private set; }
        protected override void OnInitialized()
        {
            Counter = ((SampleCounterProperties)Context.Properties).InitialCount;
        }
        public override void Handle(CelestialEvent message)
        {
            if (message.Kind == IncrementEvent) Counter = checked(Counter + 1);
        }
        public override ReadOnlyMemory<byte> CaptureState() => JsonSerializer.SerializeToUtf8Bytes(new CounterState(Counter));
        internal void RestoreCounter(long value) { Counter = value; }
        internal sealed record CounterState([property: JsonRequired] long Counter);
    }
}
