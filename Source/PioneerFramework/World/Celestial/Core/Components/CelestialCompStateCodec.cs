using System;
using System.Collections.Generic;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialRestoredComp
    {
        public CelestialCompSaveRecord OriginalRecord { get; }
        public CelestialBodyComp? Comp { get; }
        public ICelestialResolvedProperties? Properties { get; }
        public IReadOnlyList<CelestialError> Errors { get; }
        public bool IsReady => Comp != null && Properties != null && Errors.Count == 0;

        internal CelestialRestoredComp(CelestialCompSaveRecord original, CelestialBodyComp? comp,
            ICelestialResolvedProperties? properties, IReadOnlyList<CelestialError> errors)
        {
            OriginalRecord = original;
            Comp = comp;
            Properties = properties;
            Errors = errors;
        }
    }

    public static class CelestialCompStateCodec
    {
        public static CelestialCompSaveRecord Encode(CelestialBodyComp component, ICelestialResolvedProperties properties)
        {
            ArgumentNullException.ThrowIfNull(component);
            ArgumentNullException.ThrowIfNull(properties);
            if (component.Descriptor.Key != properties.Key || component.Descriptor.HandlerId != properties.HandlerId)
                throw new ArgumentException("Component properties do not match their owner.", nameof(properties));
            return new CelestialCompSaveRecord(component.Descriptor.Key, component.Descriptor.HandlerId,
                properties.Version, component.Descriptor.SchemaVersion, CelestialCostCounters.EncodeProperties(properties).Span,
                CelestialCostCounters.CaptureState(component).Span);
        }

        public static CelestialResult<CelestialRestoredComp> Decode(CelestialCompSaveRecord record, CelestialCompRegistry registry)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(registry);
            if (!registry.TryGet(record.HandlerId, out var registration))
                return Blocked(record, new[] { new CelestialError("HandlerMissing", CompKey: record.Key, Detail: record.HandlerId) });
            CelestialBodyComp? component = null;
            try
            {
                var decoded = registration.Codec.DecodeProperties(record.Key, record.ConfigurationVersion, record.ConfigurationPayload);
                if (!decoded.Success) return Blocked(record, decoded.Errors);
                var properties = decoded.Value;
                if (properties.Key != record.Key || properties.HandlerId != record.HandlerId || properties.Version != registration.ConfigurationVersion)
                    return Blocked(record, new[] { new CelestialError("HandlerContractViolation", CompKey: record.Key) });
                CelestialFrozenConfiguration.Retain(properties, record.ConfigurationPayload);
                component = registration.Factory.Create(properties);
                if (component == null || component.Descriptor.Key != record.Key || component.Descriptor.HandlerId != record.HandlerId ||
                    component.Descriptor.SchemaVersion != registration.SchemaVersion)
                    return Blocked(record, new[] { new CelestialError("HandlerContractViolation", CompKey: record.Key) }, component);
                component.Initialize(properties);
                var contract = CelestialCapabilityRegistry.Global.Validate(component);
                if (!contract.Success) return Blocked(record, contract.Errors, component);
                var restored = registration.Codec.RestoreState(component, record.SchemaVersion, record.StatePayload);
                if (!restored.Success) return Blocked(record, restored.Errors, component);
                if (!restored.Value)
                    return Blocked(record, new[] { new CelestialError("HandlerContractViolation", CompKey: record.Key) }, component);
                component.MarkRestored();
                return CelestialResult.Ok(new CelestialRestoredComp(record, component, properties, Array.Empty<CelestialError>()));
            }
            catch (Exception exception)
            {
                return Blocked(record, new[] { new CelestialError("ComponentRestoreFailed", CompKey: record.Key, Detail: exception.Message) }, component);
            }
        }

        private static CelestialResult<CelestialRestoredComp> Blocked(CelestialCompSaveRecord record,
            IReadOnlyList<CelestialError> errors, CelestialBodyComp? component = null)
        {
            var diagnostics = new List<CelestialError>(errors);
            if (component != null)
            {
                try { component.Release(); }
                catch (Exception exception)
                {
                    diagnostics.Add(new CelestialError("ComponentReleaseFailed", CompKey: record.Key, Detail: exception.Message));
                }
            }
            return CelestialResult.Ok(new CelestialRestoredComp(record, null, null, diagnostics.AsReadOnly()));
        }
    }
}
