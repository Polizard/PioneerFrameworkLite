using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PioneerFramework.Celestial;
using Xunit;

public class CelestialSaveCodecTests
{
    [Fact]
    public void EnvelopeVersionMayFollowAllOtherProperties()
    {
        var document = CelestialSaveDocument.CreateEmpty(Guid.NewGuid(), 314);
        var envelope = JsonNode.Parse(CelestialSaveCodec.Encode(document).Value.Span)!.AsObject();
        var version = envelope["formatVersion"];
        envelope.Remove("formatVersion");
        envelope.Add("formatVersion", version);
        var decoded = CelestialSaveCodec.Decode(JsonSerializer.SerializeToUtf8Bytes(envelope));
        Assert.True(decoded.Success);
        Assert.Equal(document.WorldSeed, decoded.Value.WorldSeed);
    }

    [Theory]
    [InlineData("{\"nested\":{\"formatVersion\":2}}")]
    [InlineData("{\"nested\":[{\"formatVersion\":2}],\"formatVersion\":1}")]
    [InlineData("{\"nested\":{\"formatVersion\":1},\"formatVersion\":99}")]
    [InlineData("{\"formatVersion\":1,\"nested\":{\"formatVersion\":2}}")]
    public void OnlyTheTopLevelVersionDeterminesSupport(string json)
    {
        var decoded = CelestialSaveCodec.Decode(Encoding.UTF8.GetBytes(json));
        Assert.Equal("UnsupportedSaveVersion", Assert.Single(decoded.Errors).Code);
    }

    [Theory]
    [InlineData("{\"nested\":{\"formatVersion\":1},\"formatVersion\":2}")]
    [InlineData("{\"formatVersion\":1,\"formatVersion\":2}")]
    [InlineData("{\"formatVersion\":2,\"unknown\":0}")]
    [InlineData("{\"formatVersion\":1,\"nested\":[}")]
    [InlineData("{\"formatVersion\":1} true")]
    [InlineData("{\"formatVersion\":2} {}")]
    public void VersionScanPreservesEnvelopeAndJsonRejections(string json)
    {
        var decoded = CelestialSaveCodec.Decode(Encoding.UTF8.GetBytes(json));
        Assert.Equal("InvalidSaveDocument", Assert.Single(decoded.Errors).Code);
    }

    [Fact]
    public void RoundTripPreservesResolvedUnknownPayloadManifestAndMembershipSeparateFromLifecycle()
    {
        var id = CelestialInstanceId.Parse("saved:moon");
        var descriptor = new CelestialCompDescriptor("unknown", "extension.missing", 7, new[] { "saved.capability" }, new[] { "geometry" },
            "extension.slot", after: new[] { "shape" }, updatePolicy: CelestialUpdatePolicy.Every(12));
        var record = new CelestialCompSaveRecord("unknown", "extension.missing", 3, 7, new byte[] { 0, 255, 7 }, new byte[] { 31, 0, 0, 72 });
        var body = new CelestialSavedBody(new(id, "OldDefinition", 73, 8, "Moon", 1543210.25, 5e12, new[] { "cold" }),
            new(ulong.MaxValue, "system/moon[2]"), CelestialLifecycle.Removing, true, null,
            new[] { new CelestialError("Retained", id, "unknown", "full details") }, new[] { descriptor }, new[] { record }, null);
        var document = CelestialSaveDocument.CreateEmpty(Guid.NewGuid(), 17) with {
            CatalogRevision = 22, NextHandle = 101, Bodies = new[] { body }, PublishedBodies = new[] { id }, CommittedBodies = new[] { id },
            Operations = new[] { new CelestialOperationRecord("saved hash", new("remove", CelestialOperationStatus.Pending, new[] { id })) },
            Removals = new[] { new CelestialRemovalRecord("remove", id, "physics/remove/remove", CelestialRemovalPhase.AwaitingPhysics) },
            Maps = new[] { new CelestialMapSiteRecord(new(id, 6, 42, 81), 81, 91, "retained failure") } };
        var restored = CelestialSaveCodec.Decode(CelestialSaveCodec.Encode(document).Value).Value;
        var saved = Assert.Single(restored.Bodies);
        Assert.Equal(1543210.25, saved.Description.RadiusMeters);
        Assert.Equal(73, saved.Description.Handle);
        Assert.Equal(ulong.MaxValue, saved.GenerationOrigin.WorldSeed);
        Assert.Equal(record.StatePayload.ToArray(), Assert.Single(saved.Components).StatePayload.ToArray());
        Assert.Equal(record.ConfigurationPayload.ToArray(), Assert.Single(saved.Components).ConfigurationPayload.ToArray());
        Assert.Equal(descriptor.After, Assert.Single(saved.Descriptors).After);
        Assert.Equal(12, Assert.Single(saved.Descriptors).UpdatePolicy.IntervalSeconds);
        Assert.Equal(new[] { id }, restored.CommittedBodies);
        Assert.Equal(CelestialLifecycle.Removing, saved.Lifecycle);
        Assert.Equal(101, restored.NextHandle);
        Assert.Equal("full details", Assert.Single(saved.Errors).Detail);
        Assert.Equal(91, Assert.Single(restored.Maps).MapId);
        Assert.Equal("retained failure", Assert.Single(restored.Maps).Failure);
        Assert.Equal(document.Removals, restored.Removals);
    }
    [Fact]
    public void UnsupportedVersionMalformedAndDuplicateOrInconsistentIdentityAreRejected()
    {
        Assert.Contains(CelestialSaveCodec.Decode("{}"u8.ToArray()).Errors, e => e.Code == "UnsupportedSaveVersion");
        var empty = CelestialSaveDocument.CreateEmpty(Guid.NewGuid(), 0);
        Assert.False(CelestialSaveCodec.Encode(empty with { FormatVersion = 99 }).Success);
        var id = CelestialInstanceId.Parse("a");
        var body = new CelestialSavedBody(new(id, "def", 0, 1, "", 1), new(0, "a"), CelestialLifecycle.Ready, false, null,
            Array.Empty<CelestialError>(), Array.Empty<CelestialCompDescriptor>(), Array.Empty<CelestialCompSaveRecord>(), null);
        Assert.False(CelestialSaveCodec.Encode(empty with { Bodies = new[] { body, body }, NextHandle = 1 }).Success);
        Assert.False(CelestialSaveCodec.Encode(empty with { CommittedBodies = new[] { id } }).Success);
        Assert.False(CelestialSaveCodec.Decode(new byte[] { 1, 2, 0xff }).Success);
    }
    [Fact]
    public void RejectedRequestForUnreservedIdentityIsStillASaveableReceipt()
    {
        var missing = CelestialInstanceId.Parse("never-created");
        var document = CelestialSaveDocument.CreateEmpty(Guid.NewGuid(), 0) with { Operations = new[] {
            new CelestialOperationRecord("invalid manifest hash", new("rejected", CelestialOperationStatus.Rejected,
                new[] { missing }, new[] { new CelestialError("GeometryMissing", missing, "shape", "full rejection") })) } };
        var result = CelestialSaveCodec.Encode(document);
        Assert.True(result.Success);
        Assert.Equal("full rejection", CelestialSaveCodec.Decode(result.Value).Value.Operations[0].Operation.Errors[0].Detail);
    }
}
