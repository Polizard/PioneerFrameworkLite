using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialFixtureTests
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Celestial", "v1", name);

    [Theory]
    [InlineData("ready.json")]
    [InlineData("pending.json")]
    [InlineData("unknown.json")]
    [InlineData("retired-map.json")]
    [InlineData("malformed.json")]
    [InlineData("unsupported.json")]
    public void FrozenFixtureBytesMatchTheReviewedManifest(string name)
    {
        Assert.True(File.Exists(Data(name)), "Missing independent fixture: " + name);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Data("manifest.json")));
        Assert.Equal(manifest.RootElement.GetProperty(name).GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Data(name)))));
    }

    [Fact]
    public void FrozenReadyAndPendingInputsKeepKnownValuesAndUncommittedMembership()
    {
        using var readyFile = JsonDocument.Parse(File.ReadAllBytes(Data("ready.json")));
        var ready = readyFile.RootElement;
        Assert.Equal(3, ready.GetProperty("Bodies").GetArrayLength());
        Assert.Equal(1000, ready.GetProperty("Bodies")[0].GetProperty("Description").GetProperty("RadiusMeters").GetDouble());
        Assert.Equal(1e8, ready.GetProperty("Bodies")[0].GetProperty("Description").GetProperty("GravitationalParameter").GetDouble());
        Assert.Equal(3, ready.GetProperty("CommittedBodies").GetArrayLength());
        using var pendingFile = JsonDocument.Parse(File.ReadAllBytes(Data("pending.json")));
        var pending = pendingFile.RootElement;
        Assert.Equal(0, pending.GetProperty("CommittedBodies").GetArrayLength());
        Assert.Equal(2, pending.GetProperty("Bodies").GetArrayLength());
        Assert.Equal(1, pending.GetProperty("Creations").GetArrayLength());
        Assert.Equal((int)CelestialOperationStatus.Pending, pending.GetProperty("Operations")[0].GetProperty("Operation").GetProperty("Status").GetInt32());
    }

    [Fact]
    public void UnknownPayloadAndRetiredNativeIdentityRemainOpaqueAndProtected()
    {
        using var unknown = JsonDocument.Parse(File.ReadAllBytes(Data("unknown.json")));
        var component = unknown.RootElement.GetProperty("Bodies").EnumerateArray().SelectMany(body => body.GetProperty("Components").EnumerateArray())
            .Single(component => component.GetProperty("HandlerId").GetString() == "fixture.missing");
        var record = new CelestialCompSaveRecord("unknown", "fixture.missing", 1, 1, component.GetProperty("Configuration").GetBytesFromBase64(), component.GetProperty("State").GetBytesFromBase64());
        Assert.Equal(new byte[] { 0, 255, 7 }, record.ConfigurationPayload.ToArray());
        Assert.Equal(new byte[] { 31, 0, 0, 72 }, record.StatePayload.ToArray());
        var restored = CelestialCompStateCodec.Decode(record, CelestialBuiltinCompRegistry.Create()).Value;
        Assert.False(restored.IsReady);
        Assert.Equal("HandlerMissing", Assert.Single(restored.Errors).Code);
        Assert.Equal(record.StatePayload.ToArray(), restored.OriginalRecord.StatePayload.ToArray());
        using var retiredFile = JsonDocument.Parse(File.ReadAllBytes(Data("retired-map.json")));
        var retired = retiredFile.RootElement.GetProperty("Maps")[0];
        Assert.True(retired.GetProperty("ParentRetired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, retired.GetProperty("MapId").ValueKind);
        Assert.Equal(0, retired.GetProperty("OperationIds").GetArrayLength());
        Assert.Equal("fixture/native-map", retired.GetProperty("RetiredOperationIds")[0].GetString());
    }

    [Theory]
    [InlineData("malformed.json", "InvalidSaveDocument")]
    [InlineData("unsupported.json", "UnsupportedSaveVersion")]
    public void CorruptAndUnsupportedFrozenInputsRejectExplicitly(string name, string error)
    {
        var result = CelestialSaveCodec.Decode(File.ReadAllBytes(Data(name)));
        Assert.False(result.Success);
        Assert.Equal(error, Assert.Single(result.Errors).Code);
    }
}
