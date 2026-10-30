using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Collections.Generic;
using System.Linq;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Reflection;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialSaveV2Tests
{
    private static CelestialSaveDocument Empty() => CelestialSaveDocument.CreateEmpty(Guid.Parse("28c10000-0000-4000-8000-000000000001"), 28);

    [Theory]
    [InlineData("ready.json")]
    [InlineData("pending.json")]
    [InlineData("unknown.json")]
    [InlineData("retired-map.json")]
    public void FrozenV2InputsHaveIndependentKnownValuesAndUnchangedManifestBytes(string name)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Celestial", "v2");
        var bytes = File.ReadAllBytes(Path.Combine(directory, name));
        var hashes = JsonNode.Parse(File.ReadAllBytes(Path.Combine(directory, "manifest.json")))!;
        Assert.Equal(hashes[name]!.GetValue<string>(), Convert.ToHexString(SHA256.HashData(bytes)));
        var document = CelestialSaveCodec.Decode(bytes).Value;
        Assert.Equal(1000, document.Bodies[0].Description.RadiusMeters);
        Assert.Equal(1e8, document.Bodies[0].Description.GravitationalParameter);
        Assert.Equal(2, document.PhysicsCheckpoint!.PayloadVersion);
        if (name == "pending.json") { Assert.Empty(document.CommittedBodies); Assert.Single(document.Creations); }
        else Assert.Equal(3, document.CommittedBodies.Count);
        if (name == "unknown.json")
        {
            var unknown = document.Bodies.SelectMany(b => b.Components).Single(c => c.HandlerId == "fixture.missing");
            Assert.Equal(new byte[] { 0, 255, 7 }, unknown.ConfigurationPayload.ToArray());
            Assert.Equal(new byte[] { 31, 0, 0, 72 }, unknown.StatePayload.ToArray());
        }
        if (name == "retired-map.json") Assert.True(Assert.Single(document.Maps).ParentRetired);
        Assert.True(CelestialSaveCodec.Decode(CelestialSaveCodec.Encode(document).Value).Success);
    }

    [Fact]
    public void LogicalCreationHashIgnoresTranslationAndRoutingRevisionWhileMatchingTheFrozenSchema()
    {
        var runtime = CelestialBenchFactory.Create(3, pending: true);
        var items = Enumerable.Range(0, 3).Select(index => {
            var original = CelestialBenchFactory.Template(1000 + index, false);
            var translated = CelestialBodyTemplate.Create(original.DefinitionId, "translated label", original.Category, original.Tags, original.Components).Value;
            return new CelestialSpawnItem(CelestialBenchFactory.Id(index), "body", index, "body[" + index + "]", translated,
                translated.Resolve(new CelestialGenerationContext(17, "body[" + index + "]")).Value, index == 0 ? null : CelestialBenchFactory.Id(0),
                new Dictionary<string, CelestialInstanceId> { ["frameOrigin"] = CelestialBenchFactory.Id(0) });
        }).ToArray();
        Assert.Equal(CelestialOperationStatus.Pending, runtime.CreateBatch(new(new("bench/bootstrap", runtime.WorldSessionId, 999), new("bench", 17, null, items))).Status);
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Celestial", "v2", "ready.json");
        Assert.Equal(Assert.Single(CelestialSaveCodec.Decode(File.ReadAllBytes(path)).Value.Operations).ContentHash,
            Assert.Single(runtime.Operations.Capture()).ContentHash);
    }

    [Fact]
    public void V1IsExplicitlyRejectedAndV2HasAnIntegrityCheckedCompressedEnvelope()
    {
        var old = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Celestial", "v1", "ready.json"));
        Assert.Equal("UnsupportedSaveVersion", Assert.Single(CelestialSaveCodec.Decode(old).Errors).Code);
        Assert.Equal(2, Empty().FormatVersion);
        var encoded = CelestialSaveCodec.Encode(Empty()).Value;
        var json = JsonNode.Parse(encoded.Span)!;
        Assert.Equal("brotli", json["compression"]!.GetValue<string>());
        Assert.Equal(2, json["formatVersion"]!.GetValue<int>());
        Assert.True(json["decodedLength"]!.GetValue<int>() > 0);
        Assert.True(CelestialSaveCodec.Decode(encoded).Success);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("duplicate")]
    [InlineData("order")]
    [InlineData("hash")]
    [InlineData("typo")]
    [InlineData("truncated")]
    public void CorruptEnvelopeRejectsBeforeReturningAnyDomainState(string fault)
    {
        var bytes = CelestialSaveCodec.Encode(Empty()).Value.ToArray();
        if (fault == "truncated") { Assert.False(CelestialSaveCodec.Decode(bytes.AsMemory(0, bytes.Length - 1)).Success); return; }
        var json = JsonNode.Parse(bytes)!;
        switch (fault)
        {
            case "length": json["decodedLength"] = 134217729; break;
            case "duplicate": json["chunks"]!.AsArray().Add(json["chunks"]![0]!.DeepClone()); break;
            case "order": json["chunks"]![0]!["index"] = 1; break;
            case "hash": json["chunks"]![0]!["sha256"] = new string('0', 64); break;
            case "typo": json["decodedLenght"] = 1; break;
        }
        Assert.False(CelestialSaveCodec.Decode(Encoding.UTF8.GetBytes(json.ToJsonString())).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownExtensionsAndDisabledComponentBytesSurviveWithoutCallingTheirMissingFactory(bool orderedAfterOptional)
    {
        using var native = new CelestialMapIdentityTests.Fixture();
        var runtime = CelestialBenchFactory.Create(3, transform: template => {
            var descriptor = new CelestialCompDescriptor("optional", "v2.missing", 1, updatePolicy: CelestialUpdatePolicy.Every(1), failurePolicy: CelestialCompFailurePolicy.DisableOptional);
            var components = template.Components.Select(component => {
                var d = component.Descriptor;
                if (!orderedAfterOptional || d.Key != "shape") return component;
                var ordered = new CelestialCompDescriptor(d.Key, d.HandlerId, d.SchemaVersion, d.Provides, d.Requires, d.ExclusiveSlot, d.Before, new[] { "optional" }, d.UpdatePolicy, d.FailurePolicy);
                return new CelestialCompDefinition(ordered, component.Factory, context => CelestialResult.Ok(component.Resolve(context).Value.Properties));
            });
            return CelestialBodyTemplate.Create(template.DefinitionId, template.Label, template.Category, template.Tags,
                components.Append(new CelestialCompDefinition(descriptor, new FaultFactory(descriptor), _ => CelestialResult.Ok<ICelestialResolvedProperties>(new FaultProperties())))).Value;
        });
        CelestialBenchFactory.Advance(runtime, 1);
        var payload = new byte[1024 * 1024]; new Random(28).NextBytes(payload);
        var document = new CelestialSaveAdapter(runtime).CaptureCheckpoint().Value with { Extensions = new Dictionary<string, byte[]> { ["test:opaque"] = payload } };
        var encoded = CelestialSaveCodec.Encode(document).Value;
        Assert.True(JsonNode.Parse(encoded.Span)!["chunks"]!.AsArray().Count > 1);
        document = CelestialSaveCodec.Decode(encoded).Value;
        Assert.Equal(payload, document.Extensions["test:opaque"]);
        Assert.All(document.Bodies, body => { Assert.True(Assert.Single(body.ComponentFaults).Disabled); Assert.Equal(CelestialCompFailurePolicy.DisableOptional, body.Descriptors.Single(d => d.Key == "optional").FailurePolicy); });
        var provider = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var restore = new CelestialRestoreCoordinator(provider.SessionId, provider, CelestialBuiltinCompRegistry.Create(), _ => throw new Exception("No XML"));
        Assert.True(restore.RestoreIdentities(document).Success);
        Assert.True(restore.BindNativeObjects().Success);
        Assert.True(restore.CompleteRestore().Success);
        CelestialBenchFactory.Advance(restore.Runtime, 2);
        Assert.All(document.Bodies, body => Assert.Equal(CelestialLifecycle.Ready, restore.Runtime.Query(body.Description.Id).Value.Lifecycle));
        var savedAgain = new CelestialSaveAdapter(restore.Runtime).CaptureCheckpoint().Value;
        Assert.Equal(payload, savedAgain.Extensions["test:opaque"]);
        Assert.All(savedAgain.Bodies, body => Assert.Equal(new byte[] { 0, 255, 28 }, body.Components.Single(c => c.Key == "optional").ConfigurationPayload.ToArray()));
        if (orderedAfterOptional) Assert.All(savedAgain.Bodies, body => Assert.Equal(new[] { "optional" }, body.Descriptors.Single(d => d.Key == "shape").After));
    }

    [Fact]
    public void ClosedEpochHighWaterAndPendingReceiptsSurviveNewSessionAndProviderReload()
    {
        using var native = new CelestialMapIdentityTests.Fixture();
        var runtime = CelestialBenchFactory.Create(3, commands: true);
        Assert.True(runtime.CloseEpoch(runtime.WorldSessionId, 0).Success);
        long epoch = runtime.OpenEpoch(runtime.WorldSessionId).Value;
        string pending = CelestialOperationEpochs.Qualify(epoch, "pending");
        Assert.True(runtime.Submit(new("bench.increment", pending, CelestialBenchFactory.Id(0), runtime.WorldSessionId, 1)).Success);
        var document = CelestialSaveCodec.Decode(CelestialSaveCodec.Encode(new CelestialSaveAdapter(runtime).CaptureCheckpoint().Value).Value).Value;
        var provider = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var restore = new CelestialRestoreCoordinator(provider.SessionId, provider, CelestialBuiltinCompRegistry.Create(), _ => throw new Exception("No XML"));
        Assert.True(restore.RestoreIdentities(document).Success);
        Assert.True(restore.BindNativeObjects().Success);
        Assert.True(restore.CompleteRestore().Success);
        Assert.Equal("OperationExpired", Assert.Single(restore.Runtime.GetOperation("bench/bootstrap").Errors).Code);
        Assert.Equal("OperationExpired", Assert.Single(provider.GetOperation("physics/create/" + Uri.EscapeDataString("bench/bootstrap")).Errors).Code);
        Assert.Equal(CelestialOperationStatus.Pending, restore.Runtime.GetOperation(pending).Status);
        Assert.Equal("EpochHasPendingOperations", Assert.Single(restore.Runtime.CloseEpoch(provider.SessionId, epoch).Errors).Code);
        Assert.Equal(epoch + 1, restore.Runtime.OpenEpoch(provider.SessionId).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingFailedCreationCompensationResumesWithoutReactivatingMissingComponents(bool acceptedBeforeResponseLoss)
    {
        using var native = new CelestialMapIdentityTests.Fixture();
        var runtime = CelestialBenchFactory.Create(3, transform: template => {
            var descriptor = new CelestialCompDescriptor("optional", "v2.missing", 1);
            return CelestialBodyTemplate.Create(template.DefinitionId, template.Label, template.Category, template.Tags,
                template.Components.Append(new CelestialCompDefinition(descriptor, new ReadyFaultFactory(descriptor), _ => CelestialResult.Ok<ICelestialResolvedProperties>(new FaultProperties())))).Value;
        }, bridgeTransform: inner => {
            var proxy = DispatchProxy.Create<ICelestialPhysicsBridge, CompensationResponseLoss>();
            var control = (CompensationResponseLoss)(object)proxy; control.Inner = inner; control.AcceptedBeforeLoss = acceptedBeforeResponseLoss; return proxy;
        });
        Assert.Equal(CelestialOperationStatus.Pending, runtime.GetOperation("bench/bootstrap").Status);
        Assert.All(runtime.Catalog.CaptureInstances(), body => Assert.True(body.CommittedMembership && body.FailedCreation));
        var saved = CelestialSaveCodec.Decode(CelestialSaveCodec.Encode(new CelestialSaveAdapter(runtime).CaptureCheckpoint().Value).Value).Value;
        string compensation = Assert.Single(saved.Creations).CompensationId!;
        var provider = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var restore = new CelestialRestoreCoordinator(provider.SessionId, provider, CelestialBuiltinCompRegistry.Create(), _ => throw new Exception("No XML"));
        Assert.True(restore.RestoreIdentities(saved).Success);
        Assert.True(restore.BindNativeObjects().Success);
        Assert.True(restore.CompleteRestore().Success);
        CelestialBenchFactory.Advance(restore.Runtime, restore.Runtime.CurrentStamp.PhysicalSeconds);
        Assert.Equal(PhysicsOperationStatus.Applied, provider.GetOperation(compensation).Status);
        Assert.Equal(CelestialOperationStatus.Rejected, restore.Runtime.GetOperation("bench/bootstrap").Status);
        Assert.All(restore.Runtime.Catalog.CaptureInstances(), body => Assert.False(body.CommittedMembership));
        Assert.True(restore.Runtime.CloseEpoch(provider.SessionId, 0).Success);
        Assert.All(restore.Runtime.Catalog.CaptureInstances(), body => Assert.Equal(CelestialLifecycle.Removed, body.Lifecycle));
    }
    public class CompensationResponseLoss : DispatchProxy
    {
        public ICelestialPhysicsBridge Inner;
        public bool AcceptedBeforeLoss;
        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == nameof(ICelestialPhysicsBridge.Submit) && ((PhysicsChangeSet)args[0]).OperationId.StartsWith("physics/compensate/", StringComparison.Ordinal))
            { if (AcceptedBeforeLoss) method.Invoke(Inner, args); throw new InvalidOperationException("compensation response lost"); }
            return method.Invoke(Inner, args);
        }
    }
    private sealed class ReadyFaultFactory(CelestialCompDescriptor descriptor) : ICelestialCompFactory
    { public CelestialBodyComp Create(ICelestialResolvedProperties properties) => new ReadyFaultComp(descriptor); }
    private sealed class ReadyFaultComp(CelestialCompDescriptor descriptor) : CelestialBodyComp(descriptor)
    { public override void OnReady() => throw new InvalidOperationException("ready fixture"); }

    [Theory]
    [InlineData("coreTypo")]
    [InlineData("missing")]
    [InlineData("duplicateCore")]
    [InlineData("expansion")]
    public void ValidIntegrityCannotHideCoreFieldCorruptionOrExpansionBeyondDeclaredLength(string fault)
    {
        var envelope = JsonNode.Parse(CelestialSaveCodec.Encode(Empty()).Value.Span)!;
        string raw;
        using (var input = new MemoryStream(Convert.FromBase64String(envelope["chunks"]![0]!["data"]!.GetValue<string>())))
        using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
        using (var reader = new StreamReader(brotli)) raw = reader.ReadToEnd();
        switch (fault)
        {
            case "coreTypo": raw = raw.Replace("\"worldSeed\"", "\"worldSead\""); break;
            case "missing": var node = JsonNode.Parse(raw)!.AsObject(); node.Remove("worldSeed"); raw = node.ToJsonString(); break;
            case "duplicateCore": raw = raw.Insert(1, "\"worldSeed\":28,"); break;
        }
        using var output = new MemoryStream();
        using (var compressor = new BrotliStream(output, CompressionLevel.Optimal, true)) compressor.Write(Encoding.UTF8.GetBytes(raw));
        var bytes = output.ToArray(); string hash = Convert.ToHexString(SHA256.HashData(bytes));
        envelope["chunks"]![0]!["data"] = Convert.ToBase64String(bytes); envelope["chunks"]![0]!["sha256"] = hash;
        envelope["compressedSha256"] = hash; envelope["decodedLength"] = fault == "expansion" ? 1 : Encoding.UTF8.GetByteCount(raw);
        Assert.False(CelestialSaveCodec.Decode(Encoding.UTF8.GetBytes(envelope.ToJsonString())).Success);
    }

    private sealed class FaultProperties : ICelestialResolvedProperties
    {
        public string Key => "optional"; public string HandlerId => "v2.missing"; public int Version => 1;
        public ReadOnlyMemory<byte> Serialize() => new byte[] { 0, 255, 28 };
    }
    private sealed class FaultFactory(CelestialCompDescriptor descriptor) : ICelestialCompFactory
    { public CelestialBodyComp Create(ICelestialResolvedProperties properties) => new FaultComp(descriptor); }
    private sealed class FaultComp(CelestialCompDescriptor descriptor) : CelestialBodyComp(descriptor)
    { public override void Advance(CelestialUpdateContext update) => throw new InvalidOperationException("v2 fixture"); }
}
