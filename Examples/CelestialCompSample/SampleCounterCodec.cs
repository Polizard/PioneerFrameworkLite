using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using PioneerFramework.Celestial;
using Verse;

namespace PFL.CelestialCompSample
{
    public sealed class SampleCounterCodec : ICelestialCompFactory, ICelestialCompStateHandler
    {
        public CelestialBodyComp Create(ICelestialResolvedProperties properties)
        {
            if (properties is not SampleCounterProperties) throw new ArgumentException("Counter properties required.", nameof(properties));
            return new SampleCounterComp(SampleCounterProperties.DescriptorFor(properties.Key));
        }
        public CelestialResult<ICelestialResolvedProperties> DecodeProperties(string key, int version, ReadOnlyMemory<byte> payload)
            => SampleCounterProperties.Decode(key, version, payload);
        public CelestialResult<bool> RestoreState(CelestialBodyComp component, int version, ReadOnlyMemory<byte> payload)
        {
            if (version != 1) return CelestialResult.Reject<bool>(new CelestialError("UnsupportedStateVersion", CompKey: component.Descriptor.Key));
            if (component is not SampleCounterComp counter || component.IsAttached)
                return CelestialResult.Reject<bool>(new CelestialError("CounterRestoreContextMismatch", CompKey: component.Descriptor.Key));
            try
            {
                var state = JsonSerializer.Deserialize<SampleCounterComp.CounterState>(payload.Span, Options) ?? throw new JsonException("Missing counter state.");
                if (state.Counter < 0) throw new JsonException("Counter must be nonnegative.");
                counter.RestoreCounter(state.Counter);
                return CelestialResult.Ok(true);
            }
            catch (JsonException exception)
            { return CelestialResult.Reject<bool>(new CelestialError("InvalidCounterState", CompKey: component.Descriptor.Key, Detail: exception.Message)); }
        }
        private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    }

    public static class SampleCounterRegistration
    {
        public static CelestialResult<bool> Register(CelestialCompRegistry registry)
        {
            var codec = new SampleCounterCodec();
            return registry.Register(new(SampleCounterProperties.Handler, 1, 1, codec, codec));
        }
    }
    [StaticConstructorOnStartup]
    internal static class SampleCounterStartup
    {
        static SampleCounterStartup()
        {
            var result = SampleCounterRegistration.Register(CelestialBuiltinCompRegistry.Global);
            if (!result.Success) throw new InvalidOperationException("Sample counter registration: " + result.Errors[0].Code);
        }
    }
}
