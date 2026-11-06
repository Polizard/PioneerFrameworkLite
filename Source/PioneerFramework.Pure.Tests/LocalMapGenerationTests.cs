using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using PioneerFramework.MapGeneration;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public sealed class LocalMapGenerationTests
{
    private static MapSourceContext Source(string owner = "planet:a", int tile = 17, int x = 200, int z = 150)
        => new(owner, 9182, tile, x, z, "Rock");
    private static MapGenerationStepSpec Step(string key, MapGenerationStage stage = MapGenerationStage.Fields,
        string handler = "noop", IEnumerable<string>? before = null, IEnumerable<string>? after = null,
        IEnumerable<MapGenerationPort>? inputs = null, IEnumerable<MapGenerationPort>? outputs = null,
        int? native = null, bool opaque = false, MapGenerationDomain domain = MapGenerationDomain.Natural,
        int configVersion = 1, int algorithm = 1, ReadOnlyMemory<byte> config = default)
        => new(key, handler, configVersion, algorithm, stage, domain, config, before, after, inputs, outputs, native, opaque);
    private static LocalMapGenerationSpec Spec(params MapGenerationStepSpec[] steps) => new(new("natural", steps));
    private static MapGenerationPort Port(string name, string key, string type = "int") => new(name, key, type);
    private static MapGenerationPortContract Contract(string name, string type = "int", Type? runtime = null)
        => new(name, type, runtime ?? typeof(int));
    private static MapGenerationRegistry Registry(params GenerationModuleRegistration[] registrations)
    {
        var registry = new MapGenerationRegistry();
        registry.Register(Registration("noop"));
        foreach (var item in registrations) registry.Register(item);
        registry.Freeze();
        return registry;
    }
    private static GenerationModuleRegistration Registration(string id, Action<MapGenerationContext>? execute = null,
        IEnumerable<MapGenerationPortContract>? inputs = null, IEnumerable<MapGenerationPortContract>? outputs = null,
        Func<ReadOnlyMemory<byte>, IMapGenerationModule>? factory = null, Action<ReadOnlyMemory<byte>>? validate = null)
        => new(id, 1, new[] { 1 }, validate ?? (_ => { }), factory ?? (_ => new Module(execute)), inputs, outputs);
    private static MapGenerationPlan Plan(LocalMapGenerationSpec spec, MapGenerationRegistry registry,
        MapGenerationPurpose purpose = MapGenerationPurpose.Colony, IEnumerable<MapGenerationPortContract>? external = null)
    {
        var result = MapGenerationCompiler.Compile(spec, purpose, registry, externalInputs: external);
        Assert.True(result.Success, string.Join(";", result.Diagnostics));
        return result.Plan!;
    }
    private static void Error(string code, LocalMapGenerationSpec spec, MapGenerationRegistry? registry = null,
        IEnumerable<MapGenerationStepSpec>? native = null, IEnumerable<MapGenerationPortContract>? external = null)
    {
        var result = MapGenerationCompiler.Compile(spec, MapGenerationPurpose.Colony, registry ?? Registry(), native, external);
        Assert.False(result.Success);
        Assert.Null(result.Plan);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && !diagnostic.IsWarning);
    }

    [Fact]
    public void Compiler_orders_phases_then_dependencies_then_ordinal_keys()
    {
        var plan = Plan(Spec(Step("populate", MapGenerationStage.Populate), Step("z"),
            Step("b", after: new[] { "z" }), Step("surface", MapGenerationStage.NaturalSurface),
            Step("content", MapGenerationStage.Content), Step("a")), Registry());
        Assert.Equal(new[] { "a", "z", "b", "surface", "content", "populate" }, plan.Steps.Select(step => step.Spec.Key));
    }

    [Fact]
    public void Logical_ports_rebind_reusable_handler_and_order_real_resources()
    {
        var registry = Registry(Registration("producer", c => c.Publish("value", 40), outputs: new[] { Contract("value") }),
            Registration("increment", c => c.Publish("out", c.Read<int>("in") + 1),
                new[] { Contract("in") }, new[] { Contract("out") }),
            Registration("sink", c => c.RequireService<List<int>>().Add(c.Read<int>("value")), new[] { Contract("value") }));
        var plan = Plan(Spec(Step("z-producer", handler: "producer", outputs: new[] { Port("value", "raw") }),
            Step("b-transform", handler: "increment", inputs: new[] { Port("in", "raw") }, outputs: new[] { Port("out", "eroded") }),
            Step("a-transform", handler: "increment", inputs: new[] { Port("in", "eroded") }, outputs: new[] { Port("out", "smooth") }),
            Step("sink", handler: "sink", inputs: new[] { Port("value", "smooth") })), registry);
        var values = new List<int>();
        var executor = new MapGenerationExecutor(plan, Source(), new Services(values));
        Assert.Equal(MapGenerationState.Generated, executor.Execute().State);
        Assert.Equal(new[] { 42 }, values);
        Assert.Equal(new[] { "z-producer", "b-transform", "a-transform", "sink" }, plan.Steps.Select(step => step.Spec.Key));
    }

    [Theory]
    [InlineData("DuplicateStepKey")]
    [InlineData("UnknownOrderingKey")]
    [InlineData("ReverseStageDependency")]
    [InlineData("DependencyCycle")]
    [InlineData("UnknownHandlerVersion")]
    [InlineData("UnsupportedAlgorithmVersion")]
    [InlineData("NativeOrderConflict")]
    [InlineData("DuplicateNativeOrder")]
    public void Compiler_refuses_invalid_graphs(string code)
    {
        var spec = code switch
        {
            "DuplicateStepKey" => Spec(Step("a"), Step("a")),
            "UnknownOrderingKey" => Spec(Step("a", after: new[] { "missing" })),
            "ReverseStageDependency" => Spec(Step("a", MapGenerationStage.Populate, before: new[] { "b" }), Step("b")),
            "DependencyCycle" => Spec(Step("a", after: new[] { "b" }), Step("b", after: new[] { "a" })),
            "UnknownHandlerVersion" => Spec(Step("a", configVersion: 7)),
            "UnsupportedAlgorithmVersion" => Spec(Step("a", algorithm: 7)),
            "NativeOrderConflict" => Spec(Step("a", MapGenerationStage.Populate, native: 0), Step("b", native: 1)),
            _ => Spec(Step("a", native: 0), Step("b", native: 0))
        };
        Error(code, spec);
    }

    [Fact]
    public void Native_nodes_keep_relative_order_and_opaque_warning()
    {
        var result = MapGenerationCompiler.Compile(Spec(Step("extra")), MapGenerationPurpose.Colony, Registry(),
            new[] { Step("z-native", native: 0, opaque: true), Step("a-native", native: 1) });
        Assert.True(result.Success);
        Assert.Equal(new[] { "extra", "z-native", "a-native" }, result.Plan!.Steps.Select(step => step.Spec.Key));
        Assert.Contains(result.Diagnostics, d => d.Code == "OpaqueNativeStep" && d.IsWarning && d.StepKey == "z-native");
        Error("NativeOrderConflict", Spec(Step("a", after: new[] { "b" }, native: 0), Step("b", native: 1)));
    }

    [Fact]
    public void Compiler_validates_authored_ports_against_registration()
    {
        var registry = Registry(Registration("consumer", inputs: new[] { Contract("in") }));
        Error("PortContractMismatch", Spec(Step("a", handler: "consumer", inputs: new[] { Port("in", "x", "float") })), registry);
        Error("PortContractMismatch", Spec(Step("a", handler: "consumer")), registry);
        Error("MissingInput", Spec(Step("a", handler: "consumer", inputs: new[] { Port("in", "x") })), registry);
    }

    [Fact]
    public void Compiler_rejects_duplicate_producers_and_resource_type_mismatch()
    {
        var registry = Registry(Registration("int", outputs: new[] { Contract("out") }),
            Registration("text", inputs: new[] { Contract("in", "text", typeof(string)) }));
        Error("DuplicateOutputProducer", Spec(Step("a", handler: "int", outputs: new[] { Port("out", "x") }),
            Step("b", handler: "int", outputs: new[] { Port("out", "x") })), registry);
        Error("InputTypeMismatch", Spec(Step("a", handler: "int", outputs: new[] { Port("out", "x") }),
            Step("b", handler: "text", inputs: new[] { Port("in", "x", "text") })), registry);
        Error("DuplicateOutputProducer", Spec(Step("a", handler: "int", outputs: new[] { Port("out", "x") })), registry,
            external: new[] { Contract("x") });
    }

    [Fact]
    public void Compiler_detects_conflicting_runtime_types_for_same_type_id()
    {
        var registry = Registry(Registration("producer", outputs: new[] { Contract("out") }),
            Registration("consumer", inputs: new[] { Contract("in", runtime: typeof(string)) }));
        Error("PortRuntimeTypeMismatch", Spec(Step("a", handler: "producer", outputs: new[] { Port("out", "x") }),
            Step("b", handler: "consumer", inputs: new[] { Port("in", "x") })), registry);
    }

    [Fact]
    public void External_data_schema_is_explicit_and_validated_before_execution()
    {
        var registry = Registry(Registration("consume", c => c.RequireService<List<int>>().Add(c.Read<int>("in")), new[] { Contract("in") }));
        var plan = Plan(Spec(Step("a", handler: "consume", inputs: new[] { Port("in", "external") })), registry,
            external: new[] { Contract("external") });
        Assert.Throws<ArgumentException>(() => new MapGenerationExecutor(plan, Source()));
        Assert.Throws<ArgumentException>(() => new MapGenerationExecutor(plan, Source(), externalInputs: new Dictionary<string, object> { ["external"] = "wrong" }));
        var values = new List<int>();
        var executor = new MapGenerationExecutor(plan, Source(), new Services(values), new Dictionary<string, object> { ["external"] = 7 });
        Assert.Equal(MapGenerationState.Generated, executor.Execute().State);
        Assert.Equal(new[] { 7 }, values);
    }

    [Fact]
    public void Registry_rejects_duplicates_and_mutation_after_freeze()
    {
        var registry = new MapGenerationRegistry();
        registry.Register(Registration("a"));
        Assert.Throws<ArgumentException>(() => registry.Register(Registration("a")));
        registry.Freeze();
        Assert.True(registry.IsFrozen);
        Assert.Throws<InvalidOperationException>(() => registry.Register(Registration("b")));
        Assert.False(registry.TryResolve("unknown", 1, out _));
    }

    [Fact]
    public void Invalid_configuration_is_reported_without_creating_module()
    {
        bool created = false;
        var registry = Registry(Registration("validated", factory: _ => { created = true; return new Module(); },
            validate: bytes => { if (bytes.Length != 1 || bytes.Span[0] != 4) throw new ArgumentException("expected four"); }));
        Error("InvalidConfiguration", Spec(Step("a", handler: "validated")), registry);
        Assert.False(created);
        Assert.True(MapGenerationCompiler.Compile(Spec(Step("a", handler: "validated", config: new byte[] { 4 })), MapGenerationPurpose.Colony, registry).Success);
        Assert.False(created);
    }

    [Fact]
    public void Frozen_contracts_copy_payload_collections_and_source_metadata()
    {
        byte[] bytes = { 4, 9 };
        var after = new List<string> { "other" };
        var ports = new List<MapGenerationPort> { Port("out", "resource") };
        var step = Step("a", after: after, outputs: ports, config: bytes);
        var list = new List<MapGenerationStepSpec> { step };
        var purposes = new Dictionary<MapGenerationPurpose, LocalMapRecipe> { [MapGenerationPurpose.Quest] = new("quest", list) };
        var spec = new LocalMapGenerationSpec(new("natural", list), purposes);
        var numbers = new Dictionary<string, double> { ["height"] = 2 };
        var tags = new Dictionary<string, string> { ["rock"] = "granite" };
        var source = new MapSourceContext("owner", 1, 0, 1, 1, "biome", numbers, tags);
        bytes[0] = 0; after.Clear(); ports.Clear(); list.Clear(); purposes.Clear(); numbers["height"] = 3; tags.Clear();
        Assert.True(MemoryMarshal.TryGetArray(step.Configuration, out var segment));
        segment.Array![segment.Offset] = 77;
        Assert.Equal(new byte[] { 4, 9 }, step.Configuration.ToArray());
        Assert.Equal(new[] { "other" }, step.After);
        Assert.Single(step.Outputs); Assert.Single(spec.Recipe.Steps); Assert.Single(spec.PurposeRecipes);
        Assert.Equal(2, source.NumericData["height"]); Assert.Equal("granite", source.Tags["rock"]);
        Assert.Throws<ArgumentException>(() => new MapSourceContext("owner", 1, 0, 1, 1, "biome", new Dictionary<string, double> { ["bad"] = double.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Source(x: 0));
        Assert.Throws<ArgumentException>(() => Source(owner: " "));
    }

    [Fact]
    public void Codec_roundtrip_has_canonical_bytes_hash_and_constructor_validation()
    {
        var recipe = new LocalMapRecipe("natural", new[] { Step("a", before: new[] { "z", "b" },
            inputs: new[] { Port("in", "height") }, config: new byte[] { 1, 2, 3 }, native: 9, opaque: true), Step("b"), Step("z") });
        var spec = new LocalMapGenerationSpec(recipe, new Dictionary<MapGenerationPurpose, LocalMapRecipe>
        { [MapGenerationPurpose.Quest] = new("quest", new[] { Step("quest", domain: MapGenerationDomain.Purpose) }) }, "NativeBase");
        var bytes = LocalMapGenerationCodec.Encode(spec);
        var decoded = LocalMapGenerationCodec.Decode(bytes);
        Assert.Equal(bytes.ToArray(), LocalMapGenerationCodec.Encode(decoded).ToArray());
        Assert.Equal(LocalMapGenerationCodec.SnapshotHash(spec), LocalMapGenerationCodec.SnapshotHash(decoded));
        Assert.Equal(64, LocalMapGenerationCodec.SnapshotHash(spec).Length);
        Assert.Equal("NativeBase", decoded.NativeGeneratorId);
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded.Recipe.Steps[0].Configuration.ToArray());
        string json = Encoding.UTF8.GetString(bytes.Span);
        Assert.ThrowsAny<Exception>(() => LocalMapGenerationCodec.Decode(Encoding.UTF8.GetBytes(json.Replace("\"configurationVersion\":1", "\"configurationVersion\":0", StringComparison.Ordinal))));
        Assert.ThrowsAny<Exception>(() => LocalMapGenerationCodec.Decode(Encoding.UTF8.GetBytes(json.Replace("\"formatVersion\":1", "\"formatVersion\":2", StringComparison.Ordinal))));
    }

    [Fact]
    public void Purpose_recipes_compose_without_changing_natural_seeds()
    {
        var natural = Step("natural");
        var purpose = Step("content", MapGenerationStage.Content, domain: MapGenerationDomain.Purpose);
        var spec = new LocalMapGenerationSpec(new("natural", new[] { natural }), new Dictionary<MapGenerationPurpose, LocalMapRecipe>
        { [MapGenerationPurpose.Quest] = new("quest", new[] { purpose }) });
        Assert.Single(Plan(spec, Registry()).Steps);
        Assert.Equal(2, Plan(spec, Registry(), MapGenerationPurpose.Quest).Steps.Count);
        Assert.Equal(MapGenerationSeeds.ForStep(Source(), MapGenerationPurpose.Colony, natural), MapGenerationSeeds.ForStep(Source(), MapGenerationPurpose.Quest, natural));
        Assert.NotEqual(MapGenerationSeeds.ForStep(Source(), MapGenerationPurpose.Colony, purpose), MapGenerationSeeds.ForStep(Source(), MapGenerationPurpose.Quest, purpose));
        var modified = Spec(Step("unrelated"), natural);
        var originalSeed = new MapGenerationExecutor(Plan(Spec(natural), Registry()), Source()).Execute().Steps.Single().Seed;
        Assert.Equal(originalSeed, new MapGenerationExecutor(Plan(modified, Registry()), Source()).Execute().Steps.Single(s => s.Key == "natural").Seed);
        Assert.NotEqual(originalSeed, MapGenerationSeeds.ForStep(Source("planet:b"), MapGenerationPurpose.Colony, natural));
        Assert.NotEqual(originalSeed, MapGenerationSeeds.ForStep(Source(tile: 18), MapGenerationPurpose.Colony, natural));
        Assert.NotEqual(originalSeed, MapGenerationSeeds.ForStep(Source(x: 201), MapGenerationPurpose.Colony, natural));
        Assert.NotEqual(originalSeed, MapGenerationSeeds.ForStep(Source(z: 151), MapGenerationPurpose.Colony, natural));
        Assert.NotEqual(originalSeed, MapGenerationSeeds.ForStep(Source(), MapGenerationPurpose.Colony, Step("natural", algorithm: 2)));
    }

    [Fact]
    public void Executor_stops_at_failure_keeps_error_provenance_and_never_creates_skipped_modules()
    {
        int created = 0;
        var registry = Registry(Registration("fail", _ => throw new InvalidOperationException("failure detail")),
            Registration("later", factory: _ => { created++; return new Module(); }));
        var executor = new MapGenerationExecutor(Plan(Spec(Step("a"), Step("b", handler: "fail", opaque: true), Step("c", handler: "later")), registry), Source());
        Assert.Equal(MapGenerationState.Pending, executor.Report.State);
        var report = executor.Execute();
        Assert.Equal(MapGenerationState.Failed, report.State);
        Assert.Equal(0, created);
        Assert.Equal(2, report.Steps.Count); Assert.True(report.Steps[0].Completed); Assert.False(report.Steps[1].Completed);
        Assert.True(report.Steps[1].IsOpaque); Assert.Contains("failure detail", report.Steps[1].Error);
        Assert.IsType<InvalidOperationException>(report.Steps[1].Exception);
        Assert.Equal("planet:a", report.Source.SourceId); Assert.Equal(17, report.Source.TileId);
        Assert.All(report.Steps, s => { Assert.True(s.Elapsed >= TimeSpan.Zero); Assert.True(s.AllocatedBytes >= 0); Assert.Equal(1, s.AlgorithmVersion); });
        Assert.Throws<InvalidOperationException>(() => executor.Execute());
        Assert.Throws<InvalidOperationException>(() => executor.PostInitialize());
        Assert.Equal(MapGenerationState.Pending, new MapGenerationExecutor(Plan(Spec(), Registry()), Source()).Report.State);
    }

    [Fact]
    public void Postinit_uses_same_instances_and_context_in_order_once_after_generation()
    {
        var calls = new List<string>();
        int instances = 0;
        var registry = Registry(Registration("callback", factory: _ => new CallbackModule(++instances, calls)));
        var executor = new MapGenerationExecutor(Plan(Spec(Step("b", handler: "callback"), Step("a", handler: "callback")), registry), Source());
        Assert.Throws<InvalidOperationException>(() => executor.PostInitialize());
        var generated = executor.Execute();
        Assert.Equal(new[] { "execute:1:a", "execute:2:b" }, calls);
        Assert.Equal(MapGenerationState.Generated, generated.State);
        Assert.Equal(MapGenerationState.Ready, executor.PostInitialize().State);
        Assert.Equal(new[] { "execute:1:a", "execute:2:b", "post:1:a", "post:2:b" }, calls);
        Assert.All(executor.Report.Steps, s => Assert.True(s.PostInitialized));
        Assert.All(generated.Steps, s => Assert.False(s.PostInitialized));
        Assert.Throws<InvalidOperationException>(() => executor.PostInitialize());
        Assert.Equal(2, instances);
    }

    [Fact]
    public void Postinit_failure_is_terminal_and_stops_later_callbacks()
    {
        var calls = new List<string>();
        var registry = Registry(Registration("failpost", factory: _ => new CallbackModule(1, calls, true)),
            Registration("later", factory: _ => new CallbackModule(2, calls)));
        var executor = new MapGenerationExecutor(Plan(Spec(Step("a", handler: "failpost"), Step("b", handler: "later")), registry), Source());
        executor.Execute();
        var report = executor.PostInitialize();
        Assert.Equal(MapGenerationState.Failed, report.State);
        Assert.Contains("post failure", report.Steps[0].Error);
        Assert.DoesNotContain("post:2:b", calls);
        Assert.Throws<InvalidOperationException>(() => executor.PostInitialize());
    }

    [Theory]
    [InlineData("undeclared")]
    [InlineData("wrongtype")]
    [InlineData("missing")]
    public void Runtime_data_access_enforces_declared_ports_and_types(string failure)
    {
        var registry = Registry(Registration("bad", c =>
        {
            if (failure == "undeclared") c.Publish("unknown", 1);
            else if (failure == "wrongtype") c.Publish("out", "wrong");
        }, outputs: new[] { Contract("out") }));
        var executor = new MapGenerationExecutor(Plan(Spec(Step("a", handler: "bad", outputs: new[] { Port("out", "resource") })), registry), Source());
        Assert.Equal(MapGenerationState.Failed, executor.Execute().State);
        Assert.NotNull(executor.Report.Steps[0].Error);
    }

    [Fact]
    public void Purpose_recipe_keys_are_unique_across_the_composed_graph()
    {
        var spec = new LocalMapGenerationSpec(new("natural", new[] { Step("shared") }),
            new Dictionary<MapGenerationPurpose, LocalMapRecipe> { [MapGenerationPurpose.Colony] = new("purpose", new[] { Step("shared") }) });
        Error("DuplicateStepKey", spec);
    }

    [Fact]
    public void Typed_input_edges_cannot_reverse_phases_or_form_cycles()
    {
        var registry = Registry(Registration("transform", inputs: new[] { Contract("in") }, outputs: new[] { Contract("out") }));
        Error("DependencyCycle", Spec(Step("a", handler: "transform", inputs: new[] { Port("in", "y") }, outputs: new[] { Port("out", "x") }),
            Step("b", handler: "transform", inputs: new[] { Port("in", "x") }, outputs: new[] { Port("out", "y") })), registry);
        Error("ReverseStageDependency", Spec(Step("a", handler: "transform", inputs: new[] { Port("in", "external") }, outputs: new[] { Port("out", "x") }, stage: MapGenerationStage.Populate),
            Step("b", handler: "transform", inputs: new[] { Port("in", "x") }, outputs: new[] { Port("out", "y") })), registry,
            external: new[] { Contract("external") });
    }

    [Fact]
    public void Factories_and_validators_cannot_mutate_frozen_parameter_bytes()
    {
        void Mutate(ReadOnlyMemory<byte> bytes)
        {
            Assert.True(MemoryMarshal.TryGetArray(bytes, out var segment));
            segment.Array![segment.Offset] = 99;
        }
        var step = Step("a", handler: "mutator", config: new byte[] { 4 });
        int parameter = 0;
        var registry = Registry(Registration("mutator", factory: bytes => { Mutate(bytes); return new Module(c => parameter = c.Parameters.Span[0]); }, validate: Mutate));
        var executor = new MapGenerationExecutor(Plan(Spec(step), registry), Source());
        Assert.Equal(MapGenerationState.Generated, executor.Execute().State);
        Assert.Equal(4, parameter);
        Assert.Equal(4, step.Configuration.Span[0]);
    }

    [Fact]
    public void Context_rejects_wrong_read_types_duplicate_publish_and_missing_services()
    {
        foreach (string mode in new[] { "read", "publish", "service" })
        {
            var registry = Registry(Registration("invalid", c =>
            {
                if (mode == "read") c.Read<string>("in");
                if (mode == "publish") { c.Publish("out", 1); c.Publish("out", 2); }
                if (mode == "service") c.RequireService<List<int>>();
            }, new[] { Contract("in") }, new[] { Contract("out") }));
            var plan = Plan(Spec(Step("a", handler: "invalid", inputs: new[] { Port("in", "external") }, outputs: new[] { Port("out", "result") })), registry,
                external: new[] { Contract("external") });
            Assert.Equal(MapGenerationState.Failed, new MapGenerationExecutor(plan, Source(), externalInputs: new Dictionary<string, object> { ["external"] = 1 }).Execute().State);
        }
    }

    [Fact]
    public void Factory_failure_is_attributed_to_step_and_prevents_callbacks()
    {
        var calls = new List<string>();
        var registry = Registry(Registration("callback", factory: _ => new CallbackModule(1, calls)),
            Registration("factoryFail", factory: _ => throw new InvalidOperationException("factory failure")));
        var executor = new MapGenerationExecutor(Plan(Spec(Step("a", handler: "callback"), Step("b", handler: "factoryFail")), registry), Source());
        var report = executor.Execute();
        Assert.Equal(MapGenerationState.Failed, report.State);
        Assert.Equal(new[] { "execute:1:a" }, calls);
        Assert.Equal("b", report.Steps[1].Key);
        Assert.Contains("factory failure", report.Steps[1].Error);
        Assert.Throws<InvalidOperationException>(() => executor.PostInitialize());
    }

    [Fact]
    public void Codec_rejects_duplicate_fields_and_preserves_opaque_port_bindings()
    {
        var spec = Spec(Step("a", inputs: new[] { Port("logical", "resource", "custom") }, config: new byte[] { 0, 255 }, native: -1, opaque: true));
        var bytes = LocalMapGenerationCodec.Encode(spec);
        var step = LocalMapGenerationCodec.Decode(bytes).Recipe.Steps[0];
        Assert.Equal("logical", step.Inputs[0].Name); Assert.Equal("resource", step.Inputs[0].Key);
        Assert.Equal("custom", step.Inputs[0].TypeId); Assert.Equal(-1, step.NativeOrder); Assert.True(step.IsOpaque);
        string text = Encoding.UTF8.GetString(bytes.Span);
        Assert.ThrowsAny<Exception>(() => LocalMapGenerationCodec.Decode(Encoding.UTF8.GetBytes(text.Replace("\"formatVersion\":1", "\"formatVersion\":1,\"formatVersion\":1", StringComparison.Ordinal))));
        Assert.NotEqual(LocalMapGenerationCodec.SnapshotHash(spec), LocalMapGenerationCodec.SnapshotHash(Spec(Step("changed"))));
    }

    [Fact]
    public void Seeds_ignore_biome_and_metadata_and_are_culture_independent()
    {
        var step = Step("natural");
        ulong seed = MapGenerationSeeds.ForStep(Source(), MapGenerationPurpose.Colony, step);
        var alternative = new MapSourceContext("planet:a", 9182, 17, 200, 150, "OtherBiome", new Dictionary<string, double> { ["temperature"] = 43 });
        Assert.Equal(seed, MapGenerationSeeds.ForStep(alternative, MapGenerationPurpose.Colony, step));
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Assert.Equal(seed, MapGenerationSeeds.ForStep(Source(), MapGenerationPurpose.Colony, step));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
        Assert.NotEqual(seed, MapGenerationSeeds.ForStep(new("planet:a", 9183, 17, 200, 150, "Rock"), MapGenerationPurpose.Colony, step));
    }

    private sealed class Module(Action<MapGenerationContext>? execute = null) : IMapGenerationModule
    { public void Execute(MapGenerationContext context) => execute?.Invoke(context); }
    private sealed class Services(object value) : IServiceProvider
    { public object? GetService(Type type) => type.IsInstanceOfType(value) ? value : null; }
    private sealed class CallbackModule(int instance, List<string> calls, bool fail = false) : IMapGenerationModule, IMapGenerationPostInitializer
    {
        private MapGenerationContext? original;
        public void Execute(MapGenerationContext context) { original = context; calls.Add($"execute:{instance}:{context.Step.Key}"); }
        public void PostInitialize(MapGenerationContext context)
        {
            Assert.Same(original, context);
            calls.Add($"post:{instance}:{context.Step.Key}");
            if (fail) throw new InvalidOperationException("post failure");
        }
    }
}
