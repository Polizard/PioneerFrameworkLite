using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Reflection;
using HarmonyLib;
using PioneerFramework.Celestial;
using PioneerFramework.MapGeneration;
using PioneerFramework.TestSupport.Celestial;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialSaveFormatTests
{
    // 由冻结 v2 样本 Fixtures/Celestial/v2/ready.json（字节由 manifest 锁定）解出的已复核创建逻辑 hash。
    // v3 不改创建 hash schema（HashSchemas["creation"] == 2），因此该值仍是 v3 写出的基线；
    // 提取脚本：.zcode/tmp/inspect-v2-fixture.mjs（只读冻结字节，不经过生产解码器）。
    private const string ReviewedV2CreationHash = "D8B95CEDB1C918C0F73CFC130EA3CD0D4F76E2CE64B03BEC7E955A4BAFC816D4";

    private static CelestialSaveDocument Empty() => CelestialSaveDocument.CreateEmpty(Guid.Parse("28c10000-0000-4000-8000-000000000001"), 28);

    [Theory]
    [InlineData("ready.json", "UnsupportedSaveVersion")]
    [InlineData("pending.json", "UnsupportedSaveVersion")]
    [InlineData("unknown.json", "UnsupportedSaveVersion")]
    [InlineData("retired-map.json", "UnsupportedSaveVersion")]
    [InlineData("malformed.json", "InvalidSaveDocument")]
    [InlineData("unsupported.json", "UnsupportedSaveVersion")]
    public void FrozenV2InputsKeepManifestBytesAndAreExplicitlyRejected(string name, string error)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Celestial", "v2");
        var bytes = File.ReadAllBytes(Path.Combine(directory, name));
        var hashes = JsonNode.Parse(File.ReadAllBytes(Path.Combine(directory, "manifest.json")))!;
        Assert.Equal(hashes[name]!.GetValue<string>(), Convert.ToHexString(SHA256.HashData(bytes)));
        var decoded = CelestialSaveCodec.Decode(bytes);
        Assert.False(decoded.Success);
        Assert.Equal(error, Assert.Single(decoded.Errors).Code);
    }

    [Fact]
    public void FrozenV2EnvelopesStillDeclareTheRejectedVersionInsteadOfByteCorruption()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Celestial", "v2");
        var header = JsonNode.Parse(File.ReadAllBytes(Path.Combine(directory, "ready.json")))!;
        Assert.Equal(2, header["formatVersion"]!.GetValue<int>());
        Assert.Equal("brotli", header["compression"]!.GetValue<string>());
        Assert.Equal(1, header["chunkCount"]!.GetValue<int>());
    }

    [Fact]
    public void LogicalCreationHashIgnoresTranslationAndRoutingRevisionWhileMatchingTheFrozenSchema()
    {
        using var native = new CelestialMapIdentityTests.Fixture();
        var runtime = CelestialBenchFactory.Create(3, pending: true);
        var items = Enumerable.Range(0, 3).Select(index => {
            var original = CelestialBenchFactory.Template(1000 + index, false);
            var translated = CelestialBodyTemplate.Create(original.DefinitionId, "translated label", original.Category, original.Tags, original.Components).Value;
            return new CelestialSpawnItem(CelestialBenchFactory.Id(index), "body", index, "body[" + index + "]", translated,
                translated.Resolve(new CelestialGenerationContext(17, "body[" + index + "]")).Value, index == 0 ? null : CelestialBenchFactory.Id(0),
                new Dictionary<string, CelestialInstanceId> { ["frameOrigin"] = CelestialBenchFactory.Id(0) });
        }).ToArray();
        Assert.Equal(CelestialOperationStatus.Pending, runtime.CreateBatch(new(new("bench/bootstrap", runtime.WorldSessionId, 999), new("bench", 17, null, items))).Status);
        Assert.Equal(ReviewedV2CreationHash, Assert.Single(runtime.Operations.Capture()).ContentHash);
        var saved = CelestialSaveCodec.Decode(CelestialSaveCodec.Encode(new CelestialSaveAdapter(runtime).CaptureCheckpoint().Value).Value).Value;
        Assert.Equal(ReviewedV2CreationHash, Assert.Single(saved.Operations).ContentHash);
        Assert.Equal(2, saved.HashSchemas["creation"]);
    }

    [Fact]
    public void V1V2AndV3AreExplicitlyRejectedAndV4HasAnIntegrityCheckedCompressedEnvelope()
    {
        var v1 = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Celestial", "v1", "ready.json"));
        Assert.Equal("UnsupportedSaveVersion", Assert.Single(CelestialSaveCodec.Decode(v1).Errors).Code);
        var v2 = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Celestial", "v2", "ready.json"));
        Assert.Equal("UnsupportedSaveVersion", Assert.Single(CelestialSaveCodec.Decode(v2).Errors).Code);
        var legacy = CelestialSaveCodec.Encode(Empty() with { FormatVersion = 2 });
        Assert.False(legacy.Success);
        Assert.Equal("UnsupportedSaveVersion", Assert.Single(legacy.Errors).Code);
        Assert.Equal("UnsupportedSaveVersion", Assert.Single(CelestialSaveCodec.Decode(System.Text.Encoding.UTF8.GetBytes("{\"formatVersion\":3}")).Errors).Code);
        Assert.Equal(4, Empty().FormatVersion);
        var encoded = CelestialSaveCodec.Encode(Empty()).Value;
        var json = JsonNode.Parse(encoded.Span)!;
        Assert.Equal("brotli", json["compression"]!.GetValue<string>());
        Assert.Equal(4, json["formatVersion"]!.GetValue<int>());
        Assert.True(json["decodedLength"]!.GetValue<int>() > 0);
        Assert.True(CelestialSaveCodec.Decode(encoded).Success);
    }

    [Fact]
    public void V3ProgrammaticReadyRoundTripPreservesKnownPhysicalValuesWithoutDecodingFrozenPayloads()
    {
        using var native = new CelestialMapIdentityTests.Fixture();
        var ready = CelestialSaveCodec.Decode(CelestialSaveCodec.Encode(new CelestialSaveAdapter(CelestialBenchFactory.Create(3)).CaptureCheckpoint().Value).Value).Value;
        Assert.Equal(3, ready.Bodies.Count);
        Assert.Equal(3, ready.CommittedBodies.Count);
        Assert.Equal(1000, ready.Bodies[0].Description.RadiusMeters);
        Assert.Equal(1e8, ready.Bodies[0].Description.GravitationalParameter);
        Assert.Equal(2, ready.PhysicsCheckpoint!.PayloadVersion);
    }

    [Fact]
    public void V3ProgrammaticPendingRoundTripPreservesUncommittedMembershipWithoutDecodingFrozenPayloads()
    {
        using var native = new CelestialMapIdentityTests.Fixture();
        var pending = CelestialSaveCodec.Decode(CelestialSaveCodec.Encode(new CelestialSaveAdapter(CelestialBenchFactory.Create(3, pending: true)).CaptureCheckpoint().Value).Value).Value;
        Assert.Empty(pending.CommittedBodies);
        Assert.Equal(3, pending.Bodies.Count);
        Assert.Single(pending.Creations);
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

    [Fact]
    public void SavePrecheckCapturesOnceAndExposeDataConsumesThePreparedDocument()
    {
        using var native = new CelestialMapIdentityTests.Fixture();
        var runtime = native.Scope.Runtime;
        Assert.Equal(CelestialOperationStatus.Applied, native.Request("map", CelestialMapAction.Generate).Operation.Status);
        Assert.True(CelestialSaveGuard.BeforeSave());
        var manager = native.Native.Game.GetComponent<CelestialBodyManager>();
        var adapter = (CelestialSaveAdapter)AccessTools.Field(typeof(CelestialBodyManager), "celestialSaveData").GetValue(manager);
        var prepared = adapter.PreparedDocument;
        Assert.NotNull(prepared);
        // 预检之后原生身份被改坏：新的捕获必然失败，但保存必须复用预检文档，不能再次现场捕获。
        native.Backend.LastMap.uniqueID++;
        Assert.Equal("MapIdentityMismatch", Assert.Single(new CelestialSaveAdapter(runtime).PrepareSave().Errors).Code);
        string path = Path.Combine(Path.GetTempPath(), "PFL-save-precheck-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            Scribe.saver.InitSaving(path, "state");
            manager.ExposeData();
            Scribe.saver.FinalizeSaving();
            Assert.Null(adapter.PreparedDocument);
            var loaded = new CelestialSaveAdapter();
            Scribe.loader.InitLoading(path);
            loaded.ExposeData();
            Scribe.loader.FinalizeLoading();
            Assert.NotNull(loaded.Document);
            Assert.Equal(prepared.Bodies.Select(body => body.Description.Id).ToArray(), loaded.Document!.Bodies.Select(body => body.Description.Id).ToArray());
        }
        finally { Scribe.ForceStop(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SavePrecheckRejectsMapIdentityMismatchBeforeAnyScribeWrite()
    {
        using var native = new CelestialMapIdentityTests.Fixture();
        Assert.Equal(CelestialOperationStatus.Applied, native.Request("map", CelestialMapAction.Generate).Operation.Status);
        native.Backend.LastMap.uniqueID++;
        Assert.False(CelestialSaveGuard.BeforeSave());
        var manager = native.Native.Game.GetComponent<CelestialBodyManager>();
        var adapter = (CelestialSaveAdapter)AccessTools.Field(typeof(CelestialBodyManager), "celestialSaveData").GetValue(manager);
        Assert.Null(adapter.PreparedDocument);
        var prepared = manager.PrepareCelestialSave();
        Assert.False(prepared.Success);
        Assert.Equal("MapIdentityMismatch", Assert.Single(prepared.Errors).Code);
    }

    [Fact]
    public void SurfaceTerrainAlgorithmVersionRoundTripsThroughDocumentAndRestore()
    {
        using var native = new CelestialMapIdentityTests.Fixture();
        var runtime = native.Scope.Runtime;
        Assert.Equal(CelestialPlanetTerrain.AlgorithmVersion, Assert.Single(runtime.Surfaces.CaptureHeaders()).TerrainAlgorithmVersion);
        var capture = new CelestialSaveAdapter(runtime).CaptureCheckpoint();
        Assert.True(capture.Success, string.Join(";", capture.Errors.Select(error => error.Code)));
        Assert.Equal(CelestialPlanetTerrain.AlgorithmVersion, Assert.Single(capture.Value.Surfaces).TerrainAlgorithmVersion);
        var encoded = CelestialSaveCodec.Encode(capture.Value);
        Assert.True(encoded.Success);
        var decoded = CelestialSaveCodec.Decode(encoded.Value);
        Assert.True(decoded.Success);
        var saved = Assert.Single(decoded.Value.Surfaces);
        Assert.Equal(CelestialPlanetTerrain.AlgorithmVersion, saved.TerrainAlgorithmVersion);
        var provider = new DeterministicTestPhysicsBridge(Guid.NewGuid());
        var restore = new CelestialRestoreCoordinator(provider.SessionId, provider, CelestialBuiltinCompRegistry.Create(), _ => throw new Exception("No XML"));
        Assert.True(restore.RestoreIdentities(decoded.Value).Success);
        Assert.True(restore.BindNativeObjects().Success);
        Assert.True(restore.CompleteRestore().Success);
        Assert.Equal(saved.LayerId, Assert.Single(restore.Runtime.Surfaces.CaptureHeaders()).LayerId);
        Assert.Equal(saved.TerrainAlgorithmVersion, Assert.Single(restore.Runtime.Surfaces.CaptureHeaders()).TerrainAlgorithmVersion);
    }

    [Fact]
    public void SurfaceHeaderRejectsForeignTerrainAlgorithmVersion()
    {
        using var native = new CelestialMapIdentityTests.Fixture();
        var original = native.Root.Header;
        var invalid = Assert.Throws<InvalidDataException>(() => new CelestialSurfaceHeader(original.OwnerId, original.LayerId,
            original.PhysicalRadiusMeters, original.NativeRadius, original.Subdivisions, original.GenerationSeed,
            original.GenerationConfiguration, CelestialPlanetTerrain.AlgorithmVersion + 1));
        Assert.Contains("UnsupportedTerrainAlgorithmVersion", invalid.Message);
    }

    [Fact]
    public void SurfaceHeaderWithoutTerrainAlgorithmVersionFieldStaysReadableForLegacyV4Saves()
    {
        using var native = new CelestialMapIdentityTests.Fixture();
        var original = native.Root.Header;
        var current = new CelestialSurfaceHeader(original.OwnerId, original.LayerId, original.PhysicalRadiusMeters, original.NativeRadius,
            original.Subdivisions, original.GenerationSeed, original.GenerationConfiguration);
        string path = Path.Combine(Path.GetTempPath(), "PFL-surface-header-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            Scribe.saver.InitSaving(path, "surface-header");
            Scribe_Deep.Look(ref current, "header");
            Scribe.saver.FinalizeSaving();
            // 模拟引入字段之前写出的 v4 档：缺失 terrainAlgorithmVersion 时按字段引入前的版本读取，仍然可读。
            var legacy = Regex.Replace(File.ReadAllText(path), @"\s*<terrainAlgorithmVersion>[^<]*</terrainAlgorithmVersion>", "");
            Assert.DoesNotContain("terrainAlgorithmVersion", legacy);
            File.WriteAllText(path, legacy);
            CelestialSurfaceHeader restored = null;
            Scribe.loader.InitLoading(path);
            Scribe_Deep.Look(ref restored, "header");
            Scribe.loader.FinalizeLoading();
            Assert.NotNull(restored);
            Assert.Equal(CelestialPlanetTerrain.LegacyVersionWithoutHeaderField, restored.TerrainAlgorithmVersion);
            Assert.Equal(CelestialPlanetTerrain.AlgorithmVersion, restored.TerrainAlgorithmVersion);
        }
        finally { Scribe.ForceStop(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void OldV4SurfaceJsonWithoutTerrainAlgorithmVersionDecodesWithTheLegacyDefault()
    {
        using var native = new CelestialMapIdentityTests.Fixture();
        var capture = new CelestialSaveAdapter(native.Scope.Runtime).CaptureCheckpoint();
        Assert.True(capture.Success, string.Join(";", capture.Errors.Select(error => error.Code)));
        var envelope = JsonNode.Parse(CelestialSaveCodec.Encode(capture.Value).Value.Span)!;
        var chunkList = Assert.Single(envelope["chunks"]!.AsArray());
        string raw;
        using (var input = new MemoryStream(Convert.FromBase64String(chunkList!["data"]!.GetValue<string>())))
        using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
        using (var reader = new StreamReader(brotli)) raw = reader.ReadToEnd();
        var legacy = Regex.Replace(raw, @",\s*""terrainAlgorithmVersion""\s*:\s*\d+", "");
        if (legacy == raw) legacy = Regex.Replace(raw, @"""terrainAlgorithmVersion""\s*:\s*\d+\s*,", "");
        Assert.NotEqual(raw, legacy);
        Assert.DoesNotContain("terrainAlgorithmVersion", legacy);
        using var output = new MemoryStream();
        using (var compressor = new BrotliStream(output, CompressionLevel.Optimal, true)) compressor.Write(Encoding.UTF8.GetBytes(legacy));
        var bytes = output.ToArray(); string hash = Convert.ToHexString(SHA256.HashData(bytes));
        chunkList["data"] = Convert.ToBase64String(bytes); chunkList["sha256"] = hash;
        envelope["compressedSha256"] = hash; envelope["decodedLength"] = Encoding.UTF8.GetByteCount(legacy);
        var decoded = CelestialSaveCodec.Decode(Encoding.UTF8.GetBytes(envelope.ToJsonString()));
        Assert.True(decoded.Success, string.Join(";", decoded.Errors.Select(error => error.Code)));
        Assert.Equal(CelestialPlanetTerrain.LegacyVersionWithoutHeaderField, Assert.Single(decoded.Value.Surfaces).TerrainAlgorithmVersion);
    }

    [Fact]
    public void CodecRejectsSurfaceDocumentWhoseTerrainAlgorithmVersionIsNotCurrent()
    {
        using var native = new CelestialMapIdentityTests.Fixture();
        var capture = new CelestialSaveAdapter(native.Scope.Runtime).CaptureCheckpoint();
        Assert.True(capture.Success, string.Join(";", capture.Errors.Select(error => error.Code)));
        var surface = Assert.Single(capture.Value.Surfaces);
        var tampered = capture.Value with { Surfaces = new[] { surface with { TerrainAlgorithmVersion = CelestialPlanetTerrain.AlgorithmVersion + 1 } } };
        var valid = CelestialSaveCodec.Validate(tampered);
        Assert.False(valid.Success);
        Assert.Equal("InvalidSaveDocument", Assert.Single(valid.Errors).Code);
    }

    private static LocalMapGenerationRecord CapturedGenerationRecord(CelestialMapIdentityTests.Fixture fixture,
        MapGenerationState state, string? failure = null)
    {
        var frozen = LocalMapSourceResolver.Resolve(new PlanetTile(0, fixture.Root), 50, 50,
            new MapGeneratorDef { defName = "Encounter" }, false).Value;
        return LocalMapGenerationRecord.Capture(new(frozen.Source, frozen.Specification, MapGenerationPurpose.Encounter),
            state, state == MapGenerationState.Failed ? "probe" : "Done", failure, null);
    }

    [Fact]
    public void GenerationRecordUsesTheDecoupledCurrentVersionRecipeHash()
    {
        using var native = new CelestialMapIdentityTests.Fixture();
        var record = CapturedGenerationRecord(native, MapGenerationState.Ready);
        Assert.Equal(LocalMapGenerationRecord.CurrentVersion, record.Version);
        Assert.Equal(LocalMapGenerationRecord.ComputeRecipeHash(record.FrozenRecipe), record.RecipeHash);
        var spec = LocalMapGenerationCodec.Decode(Convert.FromBase64String(record.FrozenRecipe));
        // 新 hash 针对冻结字符串，不再等于 canonical 字节的 SnapshotHash（codec 演进不再影响新记录可读性）。
        Assert.NotEqual(LocalMapGenerationCodec.SnapshotHash(spec), record.RecipeHash);
        record.Validate(record.Source.SourceId, record.Source.TileId, null);
    }

    [Fact]
    public void GenerationRecordHashValidationIsVersionedAndRejectsMismatchedCombinations()
    {
        using var native = new CelestialMapIdentityTests.Fixture();
        var record = CapturedGenerationRecord(native, MapGenerationState.Ready);
        var spec = LocalMapGenerationCodec.Decode(Convert.FromBase64String(record.FrozenRecipe));
        var legacyHash = LocalMapGenerationCodec.SnapshotHash(spec);
        var legacy = record with { Version = LocalMapGenerationRecord.LegacyVersion, RecipeHash = legacyHash };
        legacy.Validate(legacy.Source.SourceId, legacy.Source.TileId, null);
        var legacyWithCurrentHash = record with { Version = LocalMapGenerationRecord.LegacyVersion };
        Assert.Throws<ArgumentException>(() => legacyWithCurrentHash.Validate(
            legacyWithCurrentHash.Source.SourceId, legacyWithCurrentHash.Source.TileId, null));
        var currentWithLegacyHash = record with { RecipeHash = legacyHash };
        Assert.Throws<ArgumentException>(() => currentWithLegacyHash.Validate(
            currentWithLegacyHash.Source.SourceId, currentWithLegacyHash.Source.TileId, null));
        var tampered = record with { FrozenRecipe = record.FrozenRecipe + "AA" };
        Assert.Throws<ArgumentException>(() => tampered.Validate(tampered.Source.SourceId, tampered.Source.TileId, null));
        var unknownVersion = record with { Version = 99 };
        Assert.Throws<ArgumentException>(() => unknownVersion.Validate(
            unknownVersion.Source.SourceId, unknownVersion.Source.TileId, null));
    }

    [Fact]
    public void CurrentGenerationRecordSurvivesTheSaveDocumentRoundTrip()
    {
        using var native = new CelestialMapIdentityTests.Fixture();
        var parent = native.Backend.CreateParent(native.Address);
        var frozen = LocalMapSourceResolver.Resolve(parent, new IntVec3(50, 1, 50),
            new MapGeneratorDef { defName = "Encounter" }, false).Value;
        var record = LocalMapGenerationRecord.Capture(new(frozen.Source, frozen.Specification, MapGenerationPurpose.Colony),
            MapGenerationState.Failed, "probe", "failure", null);
        native.Service.RecordNativeGeneration(native.Address with { MapParentId = parent.ID }, null, record);
        var capture = new CelestialSaveAdapter(native.Scope.Runtime).CaptureCheckpoint();
        Assert.True(capture.Success, string.Join(";", capture.Errors.Select(error => error.Code)));
        var encoded = CelestialSaveCodec.Encode(capture.Value);
        Assert.True(encoded.Success, string.Join(";", encoded.Errors.Select(error => error.Code)));
        var decoded = CelestialSaveCodec.Decode(encoded.Value);
        Assert.True(decoded.Success, string.Join(";", decoded.Errors.Select(error => error.Code)));
        var saved = Assert.Single(decoded.Value.Maps).Generation;
        Assert.NotNull(saved);
        Assert.Equal(LocalMapGenerationRecord.CurrentVersion, saved!.Version);
        Assert.Equal(record.RecipeHash, saved.RecipeHash);
        saved.Validate(saved.Source.SourceId, saved.Source.TileId, "failure");
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
