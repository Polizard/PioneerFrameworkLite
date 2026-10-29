using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PioneerFramework.Celestial
{
    /// <summary>严格 v2 编解码：先流式定位版本，再校验有界 envelope；未知组件字节保留，坏核心字段拒绝。</summary>
    public static partial class CelestialSaveCodec
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();
        public static CelestialResult<ReadOnlyMemory<byte>> Encode(CelestialSaveDocument document)
        {
            var valid = Validate(document);
            if (!valid.Success) return CelestialResult.Reject<ReadOnlyMemory<byte>>(valid.Errors.ToArray());
            try { return CelestialResult.Ok<ReadOnlyMemory<byte>>(CelestialSaveEnvelope.Encode(document, Options)); }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or System.IO.IOException or System.IO.InvalidDataException)
            { return CelestialResult.Reject<ReadOnlyMemory<byte>>(new CelestialError("InvalidSaveDocument", Detail: exception.Message)); }
        }
        public static CelestialResult<CelestialSaveDocument> Decode(ReadOnlyMemory<byte> payload)
        {
            try
            {
                if (payload.Length > CelestialSaveEnvelope.MaximumEncodedBytes) throw new System.IO.InvalidDataException("Encoded save exceeds the limit.");
                // Old raw JSON documents are deliberately unsupported. Do not migrate or rewrite user saves.
                if (!HasSupportedEnvelopeVersion(payload.Span))
                    return CelestialResult.Reject<CelestialSaveDocument>(new CelestialError("UnsupportedSaveVersion"));
                var document = CelestialSaveEnvelope.Decode(payload, Options);
                var valid = Validate(document);
                return valid.Success ? CelestialResult.Ok(document) : CelestialResult.Reject<CelestialSaveDocument>(valid.Errors.ToArray());
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or NullReferenceException or OverflowException or System.IO.IOException or System.IO.InvalidDataException)
            { return CelestialResult.Reject<CelestialSaveDocument>(new CelestialError("InvalidSaveDocument", Detail: exception.Message)); }
        }

        // 仅流式探测顶层版本，不为 chunk Base64 或未知载荷建立 DOM；完整格式与完整性仍由 envelope 校验。
        private static bool HasSupportedEnvelopeVersion(ReadOnlySpan<byte> payload)
        {
            var reader = new Utf8JsonReader(payload);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected a save envelope object.");
            bool found = false, supported = false;
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                bool isVersion = reader.CurrentDepth == 1 && reader.ValueTextEquals("formatVersion"u8);
                if (!reader.Read()) throw new JsonException("Missing envelope property value.");
                if (isVersion)
                {
                    if (found) throw new JsonException("Duplicate formatVersion field.");
                    found = true;
                    supported = reader.TryGetInt32(out int version) && version == CelestialSaveDocument.CurrentVersion;
                }
                reader.Skip();
            }
            return found && supported;
        }

        public static CelestialResult<bool> Validate(CelestialSaveDocument document)
        {
            if (document == null || document.FormatVersion != CelestialSaveDocument.CurrentVersion)
                return CelestialResult.Reject<bool>(new CelestialError("UnsupportedSaveVersion"));
            try
            {
                void Require(bool condition) { if (!condition) throw new ArgumentException("Inconsistent celestial save identity or state."); }
                Require(document.GenerationAlgorithmVersion == 1 && document.CatalogRevision >= 0 && document.NextHandle >= 0 && document.NextHandle <= (long)int.MaxValue + 1);
                Require(document.Stamp.WorldSessionId != Guid.Empty && double.IsFinite(document.Stamp.PhysicalSeconds) && document.Stamp.PhysicalSeconds >= 0 &&
                    document.Stamp.ClockRevision >= 0 && document.Stamp.StateRevision >= 0 && document.Stamp.TopologyRevision >= 0 &&
                    !string.IsNullOrWhiteSpace(document.Stamp.ReferenceFrameId) && document.Stamp.SampleKind == PhysicsSampleKind.Committed);
                Require(document.HashSchemas.Count == 4 && document.HashSchemas.GetValueOrDefault("creation") == 2 &&
                    document.HashSchemas.GetValueOrDefault("command") == 1 && document.HashSchemas.GetValueOrDefault("physics") == 1 && document.HashSchemas.GetValueOrDefault("removal") == 1);
                Require(document.Extensions.All(pair => pair.Key.Contains(':') && pair.Value != null));
                var epochs = new CelestialOperationEpochs(); epochs.Restore(document.Epochs);
                foreach (var record in document.Operations) epochs.Recount(record.Operation.OperationId, record.Operation.Status);
                foreach (var record in document.Commands) epochs.Recount(record.Command.OperationId, record.Receipt.Status);
                var bodies = document.Bodies.ToDictionary(body => body.Description.Id);
                Require(document.Bodies.Select(body => body.Description.Handle).Distinct().Count() == document.Bodies.Count);
                foreach (var body in document.Bodies)
                {
                    Require(body.Description.Id.IsValid && body.Description.Handle < document.NextHandle && Enum.IsDefined(body.Lifecycle) &&
                        body.GenerationOrigin.AlgorithmVersion == 1 && !string.IsNullOrWhiteSpace(body.GenerationOrigin.GenerationKey));
                    Require(!body.ParentId.HasValue || body.ParentId.Value != body.Description.Id && bodies.ContainsKey(body.ParentId.Value));
                    Require(body.Descriptors.Select(d => d.Key).Distinct(StringComparer.Ordinal).Count() == body.Descriptors.Count &&
                        body.Components.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() == body.Components.Count && body.Components.Count == body.Descriptors.Count);
                    foreach (var component in body.Components)
                        Require(body.Descriptors.Any(d => d.Key == component.Key && d.HandlerId == component.HandlerId && d.SchemaVersion == component.SchemaVersion));
                    Require(body.OperationEpoch >= 0 && body.OperationEpoch < document.Epochs.NextEpoch &&
                        (!body.FailedCreation || body.Lifecycle == CelestialLifecycle.Blocked) &&
                        body.ComponentFaults.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count() == body.ComponentFaults.Count);
                    foreach (var fault in body.ComponentFaults)
                        Require(fault.Error != null && !string.IsNullOrWhiteSpace(fault.Error.Code) && body.Descriptors.Any(d => d.Key == fault.Key &&
                            (!fault.Disabled || d.FailurePolicy == CelestialCompFailurePolicy.DisableOptional && !d.Provides.Any(c => c is "geometry" or "physics.binding" or "surface"))));
                    if (body.ComponentFaults.Any(f => f.Disabled) && body.Lifecycle is not (CelestialLifecycle.Blocked or CelestialLifecycle.Removed))
                        Require(CelestialCompGraph.ResolveActive(body.Descriptors, body.ComponentFaults.Where(f => f.Disabled).Select(f => f.Key).ToHashSet(StringComparer.Ordinal)).Success);
                    Require(body.Errors.All(error => error != null && !string.IsNullOrWhiteSpace(error.Code)));
                    if (body.RestoreSuspension is { } suspension)
                        Require(body.Lifecycle == CelestialLifecycle.Blocked && Enum.IsDefined(suspension.Lifecycle) &&
                            suspension.Lifecycle is not (CelestialLifecycle.Blocked or CelestialLifecycle.Removed) &&
                            suspension.PreviousErrors != null && suspension.PreviousErrors.All(error => error != null && !string.IsNullOrWhiteSpace(error.Code)));
                    if (body.Assembly?.Registration is { } registration)
                        Require(registration.InstanceId == body.Description.Id && registration.DefinitionId == body.Description.DefinitionId &&
                            registration.Descriptor.DescriptorRevision == body.Description.DescriptorRevision && registration.Descriptor.RadiusMeters == body.Description.RadiusMeters &&
                            registration.Descriptor.GravitationalParameter == body.Description.GravitationalParameter && registration.ParentInstanceId == body.ParentId &&
                            registration.CompatibilityHandle == body.Description.Handle);
                }
                bool UniqueIds(IReadOnlyList<CelestialInstanceId> ids) => ids.Distinct().Count() == ids.Count && ids.All(bodies.ContainsKey);
                Require(UniqueIds(document.PublishedBodies) && UniqueIds(document.CommittedBodies));
                Require(document.CommittedBodies.ToHashSet().SetEquals(document.Bodies.Where(body => body.CommittedMembership).Select(body => body.Description.Id)));
                Require(document.CommittedBodies.All(id => bodies[id].Lifecycle != CelestialLifecycle.Removed));
                Require(document.PublishedBodies.All(id => bodies[id].Lifecycle != CelestialLifecycle.Removed));
                Require(!document.RootSurfaceId.HasValue || bodies.ContainsKey(document.RootSurfaceId.Value));
                var operations = document.Operations.ToDictionary(record => record.Operation.OperationId, StringComparer.Ordinal);
                Require(operations.Values.All(record => !string.IsNullOrWhiteSpace(record.ContentHash) && !string.IsNullOrWhiteSpace(record.Operation.OperationId) &&
                    Enum.IsDefined(record.Operation.Status) && record.Operation.InstanceIds.All(id => id.IsValid &&
                        (record.Operation.Status == CelestialOperationStatus.Rejected || bodies.ContainsKey(id)))));
                Require(document.Creations.Select(record => record.OperationId).Distinct(StringComparer.Ordinal).Count() == document.Creations.Count &&
                    document.Creations.All(record => operations.ContainsKey(record.OperationId) && UniqueIds(record.InstanceIds)));
                Require(document.Removals.Select(record => record.OperationId).Distinct(StringComparer.Ordinal).Count() == document.Removals.Count &&
                    document.Removals.All(record => operations.ContainsKey(record.OperationId) && bodies.ContainsKey(record.InstanceId) && Enum.IsDefined(record.Phase)));
                Require(document.References.Select(reference => reference.ReferenceId).Distinct(StringComparer.Ordinal).Count() == document.References.Count &&
                    document.References.All(reference => bodies.ContainsKey(reference.InstanceId) && Enum.IsDefined(reference.Kind)));
                Require(document.Commands.Select(record => record.Command.OperationId).Distinct(StringComparer.Ordinal).Count() == document.Commands.Count &&
                    document.Commands.All(record => !operations.ContainsKey(record.Command.OperationId) && bodies.ContainsKey(record.Command.TargetId) && record.Command.OperationId == record.Receipt.OperationId));
                Require(document.SchedulerCursors.Select(cursor => (cursor.InstanceId, cursor.CompKey)).Distinct().Count() == document.SchedulerCursors.Count &&
                    document.SchedulerCursors.All(cursor => bodies.ContainsKey(cursor.InstanceId) && double.IsFinite(cursor.LastSeconds) && cursor.LastSeconds <= document.Stamp.PhysicalSeconds && cursor.LastSeconds >= 0));
                Require(document.Events.All(message => bodies.ContainsKey(message.InstanceId) && message.Message != null));
                Require(document.Surfaces.Select(surface => surface.OwnerId).Distinct().Count() == document.Surfaces.Count &&
                    document.Surfaces.Select(surface => surface.LayerId).Distinct().Count() == document.Surfaces.Count);
                Require(document.Surfaces.All(surface => bodies.TryGetValue(surface.OwnerId, out var body) && surface.LayerId >= 0 && surface.PhysicalRadiusMeters == body.Description.RadiusMeters &&
                    float.IsFinite(surface.NativeRadius) && surface.NativeRadius > 0 && surface.Subdivisions >= 0 && surface.Subdivisions <= SurfaceResolvedProperties.MaxBuiltinSubdivisions &&
                    Enum.IsDefined(surface.State) && CelestialGenerationConfiguration.Decode(surface.GenerationConfiguration).Success));
                Require(document.Maps.Select(map => (map.Address.InstanceId, map.Address.LayerId, map.Address.TileId)).Distinct().Count() == document.Maps.Count &&
                    document.Maps.Select(map => map.MapParentId).Distinct().Count() == document.Maps.Count &&
                    document.Maps.Where(map => map.MapId.HasValue).Select(map => map.MapId).Distinct().Count() == document.Maps.Count(map => map.MapId.HasValue));
                Require(document.Maps.All(map => bodies.ContainsKey(map.Address.InstanceId) && map.Address.LayerId >= 0 && map.Address.TileId >= 0 && map.MapParentId >= 0 &&
                    (!map.Address.MapParentId.HasValue || map.Address.MapParentId == map.MapParentId) && (!map.MapId.HasValue || map.MapId >= 0)));
                Require(document.Maps.All(map => map.OperationIds != null && map.RetiredOperationIds != null &&
                    map.OperationIds.Distinct(StringComparer.Ordinal).Count() == map.OperationIds.Count &&
                    map.RetiredOperationIds.Distinct(StringComparer.Ordinal).Count() == map.RetiredOperationIds.Count &&
                    !map.OperationIds.Intersect(map.RetiredOperationIds, StringComparer.Ordinal).Any() && (!map.ParentRetired || map.OperationIds.Count == 0) &&
                    map.OperationIds.Concat(map.RetiredOperationIds).All(id => !string.IsNullOrWhiteSpace(id) && operations.TryGetValue(id, out var operation) &&
                        operation.Operation.Status == CelestialOperationStatus.Applied && operation.Operation.InstanceIds.Contains(map.Address.InstanceId))));
                if (document.PhysicsCheckpoint is { } checkpoint)
                    Require(checkpoint.Stamp == document.Stamp && document.CommittedBodies.All(id => bodies[id].Assembly?.Registration != null));
                return CelestialResult.Ok(true);
            }
            catch (Exception exception) when (exception is ArgumentException or NullReferenceException or InvalidOperationException)
            { return CelestialResult.Reject<bool>(new CelestialError("InvalidSaveDocument", Detail: exception.Message)); }
        }

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
            options.Converters.Add(new IdentityConverter());
            void Add<T, TData>(Func<T, TData> encode, Func<TData, T> decode) => options.Converters.Add(new Surrogate<T, TData>(encode, decode));
            Add<CelestialBodyDescription, DescriptionData>(v => new(v.Id, v.DefinitionId, v.Handle, v.DescriptorRevision, v.Category, v.RadiusMeters, v.GravitationalParameter, v.Tags),
                v => new(v.Id, v.DefinitionId, v.Handle, v.DescriptorRevision, v.Category, v.RadiusMeters, v.GravitationalParameter, v.Tags));
            Add<CelestialCompDescriptor, DescriptorData>(v => new(v.Key, v.HandlerId, v.SchemaVersion, v.Provides, v.Requires, v.ExclusiveSlot, v.Before, v.After, v.UpdatePolicy.Mode, v.UpdatePolicy.IntervalSeconds, v.FailurePolicy),
                v => new(v.Key, v.HandlerId, v.SchemaVersion, v.Provides, v.Requires, v.ExclusiveSlot, v.Before, v.After, v.Mode switch {
                    CelestialUpdateMode.None when v.IntervalSeconds == 0 => CelestialUpdatePolicy.None,
                    CelestialUpdateMode.EventsOnly when v.IntervalSeconds == 0 => CelestialUpdatePolicy.EventsOnly,
                    CelestialUpdateMode.Scheduled => CelestialUpdatePolicy.Every(v.IntervalSeconds), _ => throw new JsonException("Invalid update policy.") }, v.FailurePolicy));
            Add<CelestialCompSaveRecord, ComponentData>(v => new(v.Key, v.HandlerId, v.ConfigurationVersion, v.SchemaVersion, v.ConfigurationPayload.ToArray(), v.StatePayload.ToArray()),
                v => new(v.Key, v.HandlerId, v.ConfigurationVersion, v.SchemaVersion, v.Configuration, v.State));
            Add<PhysicsInitialization, InitializationData>(v => new(v.ProviderId, v.ModeId, v.Version, v.Payload.ToArray()), v => new(v.ProviderId, v.ModeId, v.Version, v.Payload));
            Add<PhysicsCheckpoint, CheckpointData>(v => new(v.ProviderId, v.ContractVersion, v.PayloadVersion, v.CheckpointId, v.Stamp, v.Payload.ToArray()),
                v => new(v.ProviderId, v.ContractVersion, v.PayloadVersion, v.CheckpointId, v.Stamp, v.Payload));
            Add<CelestialOperation, OperationData>(v => new(v.OperationId, v.Status, v.InstanceIds, v.Errors, v.AppliedRevision, v.RelatedOperationId),
                v => new(v.OperationId, v.Status, v.InstanceIds, v.Errors, v.AppliedRevision, v.RelatedOperationId));
            Add<CelestialCommand, CommandData>(v => new(v.Kind, v.OperationId, v.TargetId, v.WorldSessionId, v.ExpectedRevision, v.Payload.ToArray()),
                v => new(v.Kind, v.OperationId, v.TargetId, v.WorldSessionId, v.ExpectedRevision, v.Payload));
            Add<CelestialEvent, EventData>(v => new(v.Kind, v.Payload.ToArray()), v => new(v.Kind, v.Payload));
            AddV2Converters(options);
            return options;
        }
        private sealed class IdentityConverter : JsonConverter<CelestialInstanceId>
        {
            public override CelestialInstanceId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => CelestialInstanceId.Parse(reader.GetString()!);
            public override void Write(Utf8JsonWriter writer, CelestialInstanceId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
        }
        private sealed class Surrogate<T, TData>(Func<T, TData> encode, Func<TData, T> decode) : JsonConverter<T>
        {
            public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => decode(JsonSerializer.Deserialize<TData>(ref reader, options)!);
            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, encode(value), options);
        }
    }
}
