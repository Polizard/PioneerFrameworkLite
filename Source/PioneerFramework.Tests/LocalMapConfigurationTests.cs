using System;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using PioneerFramework.Celestial;
using PioneerFramework.MapGeneration;
using Xunit;

namespace PioneerFramework.Tests
{
    public class LocalMapConfigurationTests
    {
        [Fact]
        public void CurrentConfigurationPersistsOnlyTheFrozenRecipe()
        {
            var specification = new LocalMapGenerationSpec(new LocalMapRecipe("natural", Array.Empty<MapGenerationStepSpec>()), nativeGeneratorId: "Generator");
            var profile = new CelestialMapProfile("profile", null, specification);
            var properties = new MapGenerationResolvedProperties("map", profile);
            Assert.Equal(MapGenerationResolvedProperties.CurrentVersion, properties.Version);
            Assert.Same(specification, properties.Specification);
            var decoded = MapGenerationResolvedProperties.Deserialize("map", MapGenerationResolvedProperties.CurrentVersion, properties.Serialize());
            Assert.True(decoded.Success, string.Join(",", decoded.Errors));
            Assert.Equal("natural", decoded.Value.Specification.Recipe.Id);
            Assert.Equal("Generator", decoded.Value.Specification.NativeGeneratorId);
            Assert.False(MapGenerationResolvedProperties.Deserialize("map", 3, properties.Serialize()).Success);
        }

        [Fact]
        public void XmlFreezesExtensionPayloadAndDefersNativeOrderingReferences()
        {
            var registry = LocalMapGenerationModules.CreateRegistry();
            int factories = 0;
            registry.Register(new("third.custom", 1, new[] { 2 }, bytes => { if (bytes.Length != 1 || bytes.Span[0] != 42) throw new ArgumentException("Bad config"); },
                _ => { factories++; return new Noop(); }));
            var config = new CustomProperties { value = 42 };
            var definition = new LocalMapRecipeDef { defName = "custom", steps = new() { new() { key = "custom", handlerId = "third.custom",
                algorithmVersion = 2, stage = MapGenerationStage.NaturalSurface, after = new() { "native/terrain" }, properties = config } } };
            var spec = definition.ToSpecification(registry);
            config.value = 1; definition.steps.Clear();
            Assert.Equal(42, Assert.Single(spec.Recipe.Steps).Configuration.Span[0]);
            Assert.Equal(0, factories);
            Assert.Contains(MapGenerationCompiler.Compile(spec, MapGenerationPurpose.Colony, registry).Diagnostics, d => d.Code == "UnknownOrderingKey");
        }

        [Fact]
        public void StaticValidationRejectsPayloadVersionAndPortMismatchWithoutConstructingModules()
        {
            var registry = LocalMapGenerationModules.CreateRegistry();
            MapGenerationStepSpec Step(ReadOnlyMemory<byte> bytes, int algorithm = 1, MapGenerationStage stage = MapGenerationStage.NaturalSurface, MapGenerationPort[] ports = null)
                => new("replace", LocalMapBuiltinConfigurations.TerrainReplacement, 1, algorithm, stage, MapGenerationDomain.Natural, bytes, inputs: ports);
            var valid = LocalMapBuiltinConfigurations.Encode(new TerrainReplacementConfiguration(Array.Empty<string>(), "Dirt"));
            Assert.ThrowsAny<Exception>(() => LocalMapGenerationModules.ValidateRecipe(new("bad", new[] { Step(Encoding.UTF8.GetBytes("{}")) }), registry));
            Assert.ThrowsAny<Exception>(() => LocalMapGenerationModules.ValidateRecipe(new("bad", new[] { Step(valid, 2) }), registry));
            Assert.ThrowsAny<Exception>(() => LocalMapGenerationModules.ValidateRecipe(new("bad", new[] { Step(valid, stage: MapGenerationStage.Content) }), registry));
            Assert.ThrowsAny<Exception>(() => LocalMapGenerationModules.ValidateRecipe(new("bad", new[] { Step(valid, ports: new[] { new MapGenerationPort("unexpected", "float") }) }), registry));
            LocalMapGenerationModules.ValidateRecipe(new("good", new[] { Step(valid) }), registry);
            registry.Freeze();
            Assert.Throws<InvalidOperationException>(() => registry.Register(new("new", 1, new[] { 1 }, _ => { }, _ => new Noop())));
        }

        [Fact]
        public void UnproducedRecipeInputsAreRejectedBecauseTheNativeHostSuppliesNoExternalInputs()
        {
            var registry = LocalMapGenerationModules.CreateRegistry();
            registry.Register(new("third.producer", 1, new[] { 1 }, _ => { }, _ => new Noop(),
                outputs: new[] { new MapGenerationPortContract("out", "test.value", typeof(string)) }));
            registry.Register(new("third.consumer", 1, new[] { 1 }, _ => { }, _ => new Noop(),
                inputs: new[] { new MapGenerationPortContract("in", "test.value", typeof(string)) }));
            var producer = new MapGenerationStepSpec("produce", "third.producer", 1, 1, MapGenerationStage.Fields, MapGenerationDomain.Natural,
                outputs: new[] { new MapGenerationPort("out", "resource", "test.value") });
            var consumer = new MapGenerationStepSpec("consume", "third.consumer", 1, 1, MapGenerationStage.Content, MapGenerationDomain.Natural,
                inputs: new[] { new MapGenerationPort("in", "resource", "test.value") });
            // A producer/consumer pair inside one recipe stays valid.
            LocalMapGenerationModules.ValidateRecipe(new("resolved", new[] { producer, consumer }), registry);
            // P2-1: without a producer the input cannot be deferred to a host-supplied external resource.
            var failure = Assert.Throws<ArgumentException>(() => LocalMapGenerationModules.ValidateRecipe(new("unresolved", new[] { consumer }), registry));
            Assert.Contains("Unresolved recipe input", failure.Message);
            Assert.Contains("resource", failure.Message);
            Assert.Contains("native host supplies no external inputs", failure.Message);
            // A natural step may not consume a purpose output: ValidateSpecification validates the natural recipe alone.
            var purpose = new MapGenerationStepSpec("purpose", "third.producer", 1, 1, MapGenerationStage.Content, MapGenerationDomain.Purpose,
                outputs: new[] { new MapGenerationPort("out", "resource", "test.value") });
            Assert.Contains("Unresolved recipe input", Assert.Throws<ArgumentException>(() =>
                LocalMapGenerationModules.ValidateSpecification(new(new LocalMapRecipe("natural", new[] { consumer }),
                    new Dictionary<MapGenerationPurpose, LocalMapRecipe> { [MapGenerationPurpose.Quest] = new("purpose", new[] { purpose }) }), registry)).Message);
        }

        [Fact]
        public void NativeWrapperReusesExactStepAndScopesRandForGenerateAndPost()
        {
            using var fixture = new CelestialPlanetMapDiversityTests.NativeFixture();
            var layer = ReadyLayer(fixture, "callbacks", 15, null); var map = fixture.Map(layer);
            var worker = new CountingStep();
            var native = new GenStepWithParams(new GenStepDef { defName = "Counter", genStep = worker }, default);
            var services = new NativeMapGenerationServices(map, Source(), new[] { new NativeMapGenerationStep("native-token", native) });
            var step = Step("wrapped", LocalMapBuiltinConfigurations.Native, MapGenerationStage.NaturalSurface,
                new NativeGenStepConfiguration("native-token"));
            var executor = Executor(new[] { step }, services);
            Rand.PushState(100);
            float expected;
            try { expected = Rand.Value; } finally { Rand.PopState(); }
            Rand.PushState(100);
            try
            {
                Assert.Equal(MapGenerationState.Generated, executor.Execute().State);
                Assert.Equal(MapGenerationState.Ready, executor.PostInitialize().State);
                Assert.Equal(expected, Rand.Value);
            }
            finally { Rand.PopState(); }
            Assert.Equal(1, worker.Generated); Assert.Equal(1, worker.Posted); Assert.Same(map, worker.Target);
            Assert.Throws<InvalidOperationException>(() => executor.PostInitialize());
        }

        [Fact]
        public void InitialModuleUsesExactMapServiceWithoutTouchingFrozenDiversity()
        {
            var mapA = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map)); var mapB = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
            var diversity = new CelestialPlanetMapDiversity(soilFertility: 3);
            var a = new NativeMapGenerationServices(mapA, Source(), Array.Empty<NativeMapGenerationStep>());
            var b = new NativeMapGenerationServices(mapB, Source(), Array.Empty<NativeMapGenerationStep>());
            var input = new InitialMapInputs(soilFertility: .25, allowCaves: false);
            var executor = Executor(new[] { Step("initial", LocalMapBuiltinConfigurations.InitialInputs, MapGenerationStage.Fields, input) }, a);
            Assert.Equal(MapGenerationState.Generated, executor.Execute().State);
            Assert.Equal(.25, a.InitialOverrides.SoilFertility); Assert.False(a.InitialOverrides.AllowCaves);
            Assert.Null(b.InitialOverrides); Assert.Equal(3, diversity.SoilFertility);
        }

        [Fact]
        public void ContributorsProvideFrozenSelectionBeforeExecutionAndTerrainOrdering()
        {
            var selector = new NativeMapStepSelector(prefixes: new[] { "Ancient" }, types: new[] { typeof(GenStep_RockChunks).FullName }, biomeId: "Flat");
            var contribution = new NativeRulesModule(new(new[] { selector })).Contribute(Source("Flat"));
            Assert.Same(selector, Assert.Single(contribution.Remove));
            Assert.True(NativeMapStepSelection.Matches(selector, "Flat", new GenStepDef { defName = "AncientX" }));
            Assert.False(NativeMapStepSelection.Matches(selector, "Other", new GenStepDef { defName = "AncientX" }));
            Assert.True(NativeMapStepSelection.Matches(selector, "Flat", new GenStepDef { defName = "Chunks", genStep = new DerivedChunks() }));
            var terrain = new TerrainReplacementModule(new(Array.Empty<string>(), "Dirt", replaces: new(names: new[] { "Terrain" }))).Contribute(Source());
            Assert.Single(terrain.Remove); Assert.Contains(typeof(GenStep_Terrain).FullName, Assert.Single(terrain.After).Types);
        }

        [Fact]
        public void SourceSnapshotsClimateAndStableOwnedIdentityWithoutMapOrLayerIds()
        {
            using var fixture = new CelestialPlanetMapDiversityTests.NativeFixture();
            var layer = ReadyLayer(fixture, "source", 19, null); var map = fixture.Map(layer);
            layer[0].temperature = -17; layer[0].elevation = 2100; layer[0].rainfall = 99; layer[0].hilliness = Hilliness.LargeHills;
            var source = LocalMapSourceResolver.CaptureSource(map, new(27, "source-key"));
            map.uniqueID += 100; layer[0].temperature = 77;
            Assert.Equal(-17, source.NumericData["temperature"]); Assert.Equal(2100, source.NumericData["elevation"]);
            Assert.Equal(99, source.NumericData["rainfall"]); Assert.Equal((int)Hilliness.LargeHills, source.NumericData["hilliness"]);
            Assert.Equal("source-key", source.Tags["generationKey"]);
            Assert.Equal(MapGenerationSeeds.Root(source), MapGenerationSeeds.Root(LocalMapSourceResolver.CaptureSource(map, new(27, "source-key"))));
            Assert.DoesNotContain("layerId", source.Tags.Keys); Assert.DoesNotContain("mapId", source.Tags.Keys);
        }

        [Fact]
        public void SourceResolverRequiresEffectiveCapabilityAndSupportsSpecificationOnlyExtensions()
        {
            using var fixture = new CelestialPlanetMapDiversityTests.NativeFixture();
            var map = fixture.Map(ReadyLayer(fixture, "extension", 10, null));
            var generator = new MapGeneratorDef { defName = "Base_Player" };
            var capabilities = new Resolver();
            Assert.False(LocalMapSourceResolver.Resolve(map, generator, capabilities).Success);
            capabilities.Capabilities = new[] { new SpecOnlyCapability() };
            var result = LocalMapSourceResolver.Resolve(map, generator, capabilities);
            Assert.True(result.Success, string.Join(",", result.Errors));
            Assert.Same(capabilities.Capabilities[0].GetSpecification(), result.Value.Specification);
            Assert.False(LocalMapSourceResolver.IsTarget(map, new MapGeneratorDef { defName = "UnknownSpecial" }));
            Assert.False(LocalMapSourceResolver.IsTarget(map, new MapGeneratorDef { defName = "Base_Player", isUnderground = true }));
        }

        [Fact]
        public void OrdinaryQuestAndLandingVariantsUseStandardSurfacePipelineWhileSpecialGeneratorsOptOut()
        {
            using var fixture = new CelestialPlanetMapDiversityTests.NativeFixture();
            var map = fixture.Map(ReadyLayer(fixture, "variant", 9, null));
            var natural = new List<GenStepDef> { new() { defName = "ElevationFertility", genStep = new GenStep_ElevationFertility() },
                new() { defName = "Terrain", genStep = new GenStep_Terrain() } };
            Assert.True(LocalMapSourceResolver.IsTarget(map, new MapGeneratorDef { defName = "BasePlayer_SecondArchonexusCycle", genSteps = natural }));
            Assert.True(LocalMapSourceResolver.IsTarget(map, new MapGeneratorDef { defName = "ThirdPartyLanding", genSteps = natural }));
            Assert.False(LocalMapSourceResolver.IsTarget(map, new MapGeneratorDef { defName = "Space", renderWorld = true, genSteps = natural }));
        }

        [Fact]
        public void TerrainModuleActuallyReplacesOnlySelectedCellsInTargetMapsTerrainGrid()
        {
            using var fixture = new CelestialPlanetMapDiversityTests.NativeFixture();
            var map = fixture.Map(ReadyLayer(fixture, "grid", 7, null));
            var other = fixture.Map(ReadyLayer(fixture, "grid-other", 7, null));
            TerrainDef Terrain(string id) { var terrain = (TerrainDef)RuntimeHelpers.GetUninitializedObject(typeof(TerrainDef)); terrain.defName = id; terrain.tags = new(); return terrain; }
            var gravel = Terrain("LM_Gravel"); var dirt = Terrain("LM_Dirt"); var keep = Terrain("LM_Keep");
            fixture.Native.AddDefs(gravel, dirt, keep);
            foreach (var target in new[] { map, other })
            {
                target.cellIndices = new CellIndices(target); target.terrainGrid = new TerrainGrid(target);
                Array.Fill(target.terrainGrid.topGrid, keep); target.terrainGrid.topGrid[0] = gravel;
            }
            var skipEffects = new Harmony("PFL.Tests.LocalTerrainSideEffects");
            try
            {
                // The real SetTerrain writes all terrain grids; renderer/pathing callbacks require a running game.
                skipEffects.Patch(AccessTools.Method(typeof(TerrainGrid), "DoTerrainChangedEffects"), prefix: new(typeof(LocalMapConfigurationTests), nameof(SkipTerrainEffects)));
                var services = new NativeMapGenerationServices(map, Source(), Array.Empty<NativeMapGenerationStep>());
                var executor = Executor(new[] { Step("replace", LocalMapBuiltinConfigurations.TerrainReplacement, MapGenerationStage.NaturalSurface,
                    new TerrainReplacementConfiguration(new[] { gravel.defName }, dirt.defName)) }, services);
                Assert.Equal(MapGenerationState.Generated, executor.Execute().State);
                Assert.Same(dirt, map.terrainGrid.TerrainAt(0)); Assert.Same(keep, map.terrainGrid.TerrainAt(1));
                Assert.Same(gravel, other.terrainGrid.TerrainAt(0));
            }
            finally { skipEffects.UnpatchAll(skipEffects.Id); }
        }

        [Fact]
        public void ExplicitRecipeInputsAreSampledWithTheActualComponentFreezeContext()
        {
            var profile = new CelestialMapProfileDef { defName = "sampled", localRecipe = new LocalMapRecipeDef { defName = "local", steps = new() {
                new() { key = "inputs", handlerId = LocalMapBuiltinConfigurations.InitialInputs, stage = MapGenerationStage.Fields,
                    properties = new InitialInputsModuleProperties { inputs = new() { soilFertility = 1 } } } } } };
            var context = new CelestialGenerationContext(1, "test", "map", new[] { new CelestialParameterOverride("map", "diversity.soilFertility", 7) });
            var resolved = profile.ToProfile(context);
            Assert.True(resolved.Success, string.Join(",", resolved.Errors));
            Assert.Equal(7, LocalMapBuiltinConfigurations.Decode<InitialMapInputs>(resolved.Value.LocalSpecification.Recipe.Steps[0].Configuration).SoilFertility);
        }

        [Fact]
        public void ExplicitInitialInputsMustMatchProfileDiversity()
        {
            var diversity = new CelestialPlanetMapDiversity(soilFertility: 2.5, allowCaves: false);
            var projected = CelestialLocalMapCompatibility.FreezeInitialInputs("probe", diversity);
            // Equal explicit inputs pass; the automatic projection keys are derived and skipped.
            CelestialLocalMapCompatibility.RequireInitialInputsMatchDiversity(diversity, new LocalMapRecipe("equal", new[] { projected }));
            var mismatch = new MapGenerationStepSpec("explicit", LocalMapBuiltinConfigurations.InitialInputs, 1, 1,
                MapGenerationStage.Fields, MapGenerationDomain.Natural,
                LocalMapBuiltinConfigurations.Encode(new InitialMapInputs(soilFertility: 3, allowCaves: false)));
            var failure = Assert.Throws<ArgumentException>(() =>
                CelestialLocalMapCompatibility.RequireInitialInputsMatchDiversity(diversity, new LocalMapRecipe("mismatch", new[] { mismatch })));
            Assert.Contains("Initial inputs disagree with profile diversity", failure.Message);
            // No profile diversity means there is no long-lived source to drift from.
            CelestialLocalMapCompatibility.RequireInitialInputsMatchDiversity(null, new LocalMapRecipe("standalone", new[] { mismatch }));
            var auto = new MapGenerationStepSpec("profile.initial-inputs", LocalMapBuiltinConfigurations.InitialInputs, 1, 1,
                MapGenerationStage.Fields, MapGenerationDomain.Natural,
                LocalMapBuiltinConfigurations.Encode(new InitialMapInputs(soilFertility: 3, allowCaves: false)));
            CelestialLocalMapCompatibility.RequireInitialInputsMatchDiversity(diversity, new LocalMapRecipe("auto", new[] { auto }));
        }

        [Fact]
        public void NativeValidatorRejectsMissingAndWronglyTypedTerrainReferencesBeforeAllocation()
        {
            using var fixture = new CelestialPlanetMapDiversityTests.NativeFixture();
            var registry = LocalMapGenerationModules.CreateRegistry(true);
            var recipe = new LocalMapRecipe("missing", new[] { Step("terrain", LocalMapBuiltinConfigurations.TerrainReplacement, MapGenerationStage.NaturalSurface,
                new TerrainReplacementConfiguration(Array.Empty<string>(), "LM_MissingTerrain")) });
            Assert.Throws<ArgumentException>(() => LocalMapGenerationModules.ValidateRecipe(recipe, registry));
        }
        [Fact]
        public void EarlyParentResolutionMatchesMapSourceBeforeConstructComponents()
        {
            using var fixture = new CelestialPlanetMapDiversityTests.NativeFixture();
            var map = fixture.Map(ReadyLayer(fixture, "early", 19, null));
            var generator = new MapGeneratorDef { defName = "Base_Player" };
            var resolver = new Resolver { Capabilities = new[] { new SpecOnlyCapability() } };
            Assert.True(LocalMapSourceResolver.IsTarget(map.Parent.Tile, generator, false));
            var early = LocalMapSourceResolver.Resolve(map.Parent, map.Size, generator, false, resolver);
            var late = LocalMapSourceResolver.Resolve(map, generator, resolver);
            Assert.True(early.Success, string.Join(",", early.Errors)); Assert.True(late.Success);
            Assert.Equal(MapGenerationSeeds.Root(early.Value.Source), MapGenerationSeeds.Root(late.Value.Source));
            Assert.Equal(early.Value.Source.NumericData, late.Value.Source.NumericData);
            Assert.False(LocalMapSourceResolver.Resolve(map.Parent, map.Size, generator, true, resolver).Success);
        }
        [Fact]
        public void UnknownNestedRecipeFormatReturnsStructuredConfigurationRejection()
        {
            var spec = new LocalMapGenerationSpec(new LocalMapRecipe("empty", Array.Empty<MapGenerationStepSpec>()));
            var profile = new CelestialMapProfile("new", null, spec);
            string json = Encoding.UTF8.GetString(new MapGenerationResolvedProperties("map", profile).Serialize().Span)
                .Replace("\"formatVersion\":1", "\"formatVersion\":123", StringComparison.Ordinal);
            var result = MapGenerationResolvedProperties.Deserialize("map", MapGenerationResolvedProperties.CurrentVersion, Encoding.UTF8.GetBytes(json));
            Assert.False(result.Success); Assert.Equal("InvalidConfigurationPayload", result.Errors[0].Code);
        }

        [Fact]
        public void ExtensionComponentSourceUsesItsFrozenPropertiesWithNoBuiltinHandlerOrAccessorSniffing()
        {
            using var fixture = new CelestialPlanetMapDiversityTests.NativeFixture();
            var map = fixture.Map(ReadyLayer(fixture, "frozen-comp", 19, null));
            var properties = new CustomFrozenProperties();
            var component = new FrozenComponent(); component.Attach(new FrozenContext(map.Tile.Layer is CelestialSurfaceLayer layer ? layer.Header.OwnerId : default, properties));
            var resolver = new Resolver { Capabilities = new[] { component } };
            var result = LocalMapSourceResolver.Resolve(map, new MapGeneratorDef { defName = "Base_Player" }, resolver);
            Assert.True(result.Success, string.Join(",", result.Errors));
            Assert.Same(properties.Specification, result.Value.Specification);
            resolver.Capabilities = Array.Empty<ICelestialMapGenerationCapability>();
            Assert.False(LocalMapSourceResolver.Resolve(map, new MapGeneratorDef { defName = "Base_Player" }, resolver).Success);
        }

        [Fact]
        public void BuiltinComponentReturnsTheSameFrozenSpecificationAndProjection()
        {
            var xml = new CompProperties_MapGeneration { key = "map", profile = new() { defName = "snapshot", localRecipe = new LocalMapRecipeDef { defName = "snapshot-local" }, diversity = new() { soilFertility = 2 } } };
            var frozen = (MapGenerationResolvedProperties)xml.Resolve(new(7, "snapshot", "map")).Value;
            var component = (CompMapGeneration)xml.CreateComp(); component.Attach(new FrozenContext(CelestialInstanceId.Parse("snapshot"), frozen));
            xml.profile.diversity.soilFertility = 9;
            Assert.Same(frozen.Specification, component.GetSpecification()); Assert.Same(frozen.Profile, component.GetProfile());
            Assert.Equal(2, component.GetProfile().Diversity.SoilFertility);
            Assert.True(LocalMapGenerationModules.Registry.IsFrozen);
        }
        [Fact]
        public void StaticValidationRejectsKnownRecipeOrderingConflictBeforeNativeAllocation()
        {
            var rules = new MapGenerationStepSpec("rules", LocalMapBuiltinConfigurations.NativeRules, 1, 1, MapGenerationStage.Fields, MapGenerationDomain.Natural,
                LocalMapBuiltinConfigurations.Encode(new NativeRulesConfiguration(Array.Empty<NativeMapStepSelector>())), after: new[] { "terrain" });
            var terrain = Step("terrain", LocalMapBuiltinConfigurations.TerrainReplacement, MapGenerationStage.NaturalSurface,
                new TerrainReplacementConfiguration(Array.Empty<string>(), "Dirt"));
            Assert.Throws<ArgumentException>(() => LocalMapGenerationModules.ValidateRecipe(new("conflict", new[] { rules, terrain }), LocalMapGenerationModules.CreateRegistry()));
        }
        [Fact]
        public void InitialInputsContributeBeforeEveryNativeFieldsNodeAndTerrainAfterNaturalSurface()
        {
            var initial = new InitialInputsModule(new(soilFertility: 2)).Contribute(Source());
            Assert.Equal(MapGenerationStage.Fields, initial.BeforeNativeStage);
            Assert.Empty(initial.Remove); Assert.Null(initial.AfterNativeStage);
            var terrain = new TerrainReplacementModule(new(Array.Empty<string>(), "Dirt")).Contribute(Source());
            Assert.Equal(MapGenerationStage.NaturalSurface, terrain.AfterNativeStage);
        }
        [Fact]
        public void InitialInputContributionCanBePreloadedAndConfirmedWithoutReplacingFrozenData()
        {
            using var fixture = new CelestialPlanetMapDiversityTests.NativeFixture();
            var map = fixture.Map(ReadyLayer(fixture, "preload", 7, null));
            var services = new NativeMapGenerationServices(map, Source(), Array.Empty<NativeMapGenerationStep>());
            var contribution = new InitialInputsModule(new(soilFertility: 2, allowCaves: false)).Contribute(Source());
            var property = typeof(NativeMapPlanContribution).GetProperty("InitialInputs");
            Assert.NotNull(property);
            var preloaded = Assert.IsType<InitialMapInputs>(property.GetValue(contribution));
            services.SetInitialOverrides(preloaded);
            var executor = Executor(new[] { Step("initial", LocalMapBuiltinConfigurations.InitialInputs, MapGenerationStage.Fields,
                new InitialMapInputs(soilFertility: 2, allowCaves: false)) }, services);
            Assert.Equal(MapGenerationState.Generated, executor.Execute().State);
            Assert.Same(preloaded, services.InitialOverrides);
            Assert.Throws<InvalidOperationException>(() => services.SetInitialOverrides(new InitialMapInputs(soilFertility: 3, allowCaves: false)));
            Assert.Same(preloaded, services.InitialOverrides);
        }
        [Fact]
        public void PurposeRecipesCannotOverrideNaturalStagesOrNaturalBuiltinModules()
        {
            var registry = LocalMapGenerationModules.CreateRegistry();
            registry.Register(new("third.purpose", 1, new[] { 1 }, _ => { }, _ => new Noop()));
            var natural = new LocalMapRecipe("natural", Array.Empty<MapGenerationStepSpec>());
            void Reject(MapGenerationStepSpec step) => Assert.Throws<ArgumentException>(() =>
                LocalMapGenerationModules.ValidateSpecification(new(natural, new Dictionary<MapGenerationPurpose, LocalMapRecipe> {
                    [MapGenerationPurpose.Quest] = new("purpose", new[] { step }) }), registry));
            Reject(new("early", "third.purpose", 1, 1, MapGenerationStage.Fields, MapGenerationDomain.Purpose));
            Reject(new("surface", "third.purpose", 1, 1, MapGenerationStage.NaturalSurface, MapGenerationDomain.Purpose));
            Reject(new("rules", LocalMapBuiltinConfigurations.NativeRules, 1, 1, MapGenerationStage.Fields, MapGenerationDomain.Purpose,
                LocalMapBuiltinConfigurations.Encode(new NativeRulesConfiguration(Array.Empty<NativeMapStepSelector>()))));
            Reject(new("disguised", "third.purpose", 1, 1, MapGenerationStage.Content, MapGenerationDomain.Natural));
            var valid = new MapGenerationStepSpec("content", "third.purpose", 1, 1, MapGenerationStage.Content, MapGenerationDomain.Purpose);
            LocalMapGenerationModules.ValidateSpecification(new(natural, new Dictionary<MapGenerationPurpose, LocalMapRecipe> {
                [MapGenerationPurpose.Quest] = new("purpose", new[] { valid }) }), registry);
        }
        [Fact]
        public void PurposeNativeWrappersCannotExecuteOrRemoveNaturalSteps()
        {
            var registry = LocalMapGenerationModules.CreateRegistry();
            var natural = new LocalMapRecipe("natural", Array.Empty<MapGenerationStepSpec>());
            foreach (var config in new[] {
                new NativeGenStepConfiguration("def/Terrain"),
                new NativeGenStepConfiguration("def/Terrain", new NativeMapStepSelector(names: new[] { "Terrain" })) })
            {
                var step = new MapGenerationStepSpec("native", LocalMapBuiltinConfigurations.Native, 1, 1,
                    MapGenerationStage.Content, MapGenerationDomain.Purpose, LocalMapBuiltinConfigurations.Encode(config));
                var specification = new LocalMapGenerationSpec(natural, new Dictionary<MapGenerationPurpose, LocalMapRecipe> {
                    [MapGenerationPurpose.Quest] = new("purpose", new[] { step }) });
                Assert.Throws<ArgumentException>(() => LocalMapGenerationModules.ValidateSpecification(specification, registry));
            }
        }
        [Fact]
        public void InitialXmlCannotSilentlyAcceptRuntimeWeatherPolicy()
        {
            var xml = new InitialInputsModuleProperties { inputs = new() { weather = new() { new() { def = new WeatherDef { defName = "Weather" } } } } };
            Assert.Throws<ArgumentException>(() => xml.FreezeConfiguration());
        }
        [Fact]
        public void ActualVerseXmlLoaderUsesTypedModulePropertiesAndFreezesScalarConfiguration()
        {
            using var fixture = new CelestialPlanetMapDiversityTests.NativeFixture();
            var xml = new System.Xml.XmlDocument();
            xml.LoadXml("<LocalMapRecipeDef><defName>LM_XmlRecipe</defName><recipeId>xml-recipe</recipeId><steps><li><key>inputs</key><handlerId>PFL.LocalMap.InitialInputs</handlerId><configVersion>1</configVersion><algorithmVersion>1</algorithmVersion><stage>Fields</stage><domain>Natural</domain><properties Class=\"PioneerFramework.MapGeneration.InitialInputsModuleProperties\"><inputs><soilFertility>2</soilFertility><allowCaves>false</allowCaves></inputs></properties></li></steps></LocalMapRecipeDef>");
            var definition = DirectXmlToObject.ObjectFromXml<LocalMapRecipeDef>(xml.DocumentElement, false);
            var frozen = definition.ToSpecification(LocalMapGenerationModules.CreateRegistry());
            Assert.Equal("xml-recipe", frozen.Recipe.Id);
            var inputs = LocalMapBuiltinConfigurations.Decode<InitialMapInputs>(Assert.Single(frozen.Recipe.Steps).Configuration);
            Assert.Equal(2, inputs.SoilFertility); Assert.False(inputs.AllowCaves);
        }
        [Fact]
        public void InvalidNativeAddressCannotReachLayerLookupAndPartialSurfaceCannotAuthorizeGeneration()
        {
            Assert.False(LocalMapSourceResolver.IsTarget(PlanetTile.Invalid, new MapGeneratorDef { defName = "Base_Player" }, false));
            using var fixture = new CelestialPlanetMapDiversityTests.NativeFixture();
            Assert.False(LocalMapSourceResolver.IsTarget(new PlanetTile(0, int.MaxValue), new MapGeneratorDef { defName = "Base_Player" }, false));
            var layer = ReadyLayer(fixture, "partial", 12, null); var map = fixture.Map(layer);
            var resolver = new Resolver { Capabilities = new[] { new SpecOnlyCapability() } };
            var generator = new MapGeneratorDef { defName = "Base_Player" };
            layer.Header.State = CelestialSurfaceState.Failed;
            Assert.False(LocalMapSourceResolver.Resolve(map, generator, resolver).Success);
            layer.Header.State = CelestialSurfaceState.Ready; layer.Tiles.RemoveAt(layer.Tiles.Count - 1);
            Assert.False(LocalMapSourceResolver.Resolve(map, generator, resolver).Success);
        }
        [Fact]
        public void OptionalDlcSkipNamesDoNotRequireDefinitionsWhileExplicitNativeReferencesStayStrict()
        {
            using var fixture = new CelestialPlanetMapDiversityTests.NativeFixture();
            var registry = LocalMapGenerationModules.CreateRegistry(true);
            var skip = Step("skip", LocalMapBuiltinConfigurations.NativeRules, MapGenerationStage.Fields,
                new NativeRulesConfiguration(new[] { new NativeMapStepSelector(names: new[] { "MissingDlc_VoidMonolith" }) }));
            LocalMapGenerationModules.ValidateRecipe(new("optional", new[] { skip }), registry);
            var explicitStep = Step("explicit", LocalMapBuiltinConfigurations.Native, MapGenerationStage.Content, new NativeGenStepConfiguration("def/MissingDlc_VoidMonolith"));
            Assert.Throws<ArgumentException>(() => LocalMapGenerationModules.ValidateRecipe(new("explicit", new[] { explicitStep }), registry));
            var replaces = Step("replace", LocalMapBuiltinConfigurations.TerrainReplacement, MapGenerationStage.NaturalSurface,
                new TerrainReplacementConfiguration(Array.Empty<string>(), "MissingTypedTerrain"));
            Assert.Throws<ArgumentException>(() => LocalMapGenerationModules.ValidateRecipe(new("typed", new[] { replaces }), registry));
            Assert.Throws<ArgumentException>(() => new NativeMapStepSelector(types: new[] { "" }));
            Assert.Throws<ArgumentException>(() => new NativeMapStepSelector(names: new[] { "Repeated", "Repeated" }));
        }

        private sealed class CustomFrozenProperties : ICelestialLocalMapGenerationProperties
        {
            public string Key => "extension"; public string HandlerId => "third.frozen"; public int Version => 1;
            public LocalMapGenerationSpec Specification { get; } = new(new LocalMapRecipe("extension", Array.Empty<MapGenerationStepSpec>()));
            public ReadOnlyMemory<byte> Serialize() => ReadOnlyMemory<byte>.Empty;
        }
        private sealed class FrozenComponent() : CelestialBodyComp(new("extension", "third.frozen", 1)), ICelestialMapGenerationCapability
        {
            public LocalMapGenerationSpec GetSpecification() => throw new InvalidOperationException("Must use attached frozen properties.");
        }
        private sealed class FrozenContext(CelestialInstanceId owner, ICelestialResolvedProperties properties) : ICelestialCompContext
        {
            public CelestialInstanceId InstanceId => owner; public Guid WorldSessionId => Guid.Empty;
            public long CatalogRevision => 1; public long DescriptorRevision => 1; public double PhysicalSeconds => 0;
            public ICelestialResolvedProperties Properties => properties;
            public bool TryGetCapability<T>(out T capability) where T : class { capability = null; return false; }
            public IReadOnlyList<T> GetCapabilities<T>() where T : class => Array.Empty<T>();
            public bool TryGetService<T>(out T service) where T : class { service = null; return false; }
            public CelestialResult<string> Submit(CelestialCommand command) => throw new InvalidOperationException();
        }
        private static bool SkipTerrainEffects() => false;

        private static CelestialSurfaceLayer ReadyLayer(CelestialPlanetMapDiversityTests.NativeFixture fixture, string id, int seed, CelestialPlanetMapDiversity diversity)
        {
            var layer = fixture.Layer(id, seed, diversity);
            layer.Header.State = CelestialSurfaceState.Ready;
            return layer;
        }
        private static MapSourceContext Source(string biome = "Plain") => new("owner", 42, 0, 6, 6, biome);
        private static MapGenerationStepSpec Step<T>(string key, string handler, MapGenerationStage stage, T config) => new(key, handler, 1, 1, stage, MapGenerationDomain.Natural, LocalMapBuiltinConfigurations.Encode(config));
        private static MapGenerationExecutor Executor(IEnumerable<MapGenerationStepSpec> steps, INativeMapGenerationServices services)
        {
            var registry = LocalMapGenerationModules.CreateRegistry();
            var compiled = MapGenerationCompiler.Compile(new(new("test", steps)), MapGenerationPurpose.Colony, registry);
            Assert.True(compiled.Success, string.Join(",", compiled.Diagnostics));
            return new(compiled.Plan, services.Source, new Provider(services));
        }
        private sealed class Provider(INativeMapGenerationServices services) : IServiceProvider { public object GetService(Type type) => type == typeof(INativeMapGenerationServices) ? services : null; }
        private sealed class Noop : IMapGenerationModule { public void Execute(MapGenerationContext context) { } }
        private sealed class CustomProperties : LocalMapModuleProperties { public byte value; public override ReadOnlyMemory<byte> FreezeConfiguration() => new byte[] { value }; }
        private sealed class DerivedChunks : GenStep_RockChunks { }
        private sealed class CountingStep : GenStep
        {
            public int Generated, Posted; public Map Target;
            public override int SeedPart => 7;
            public override void Generate(Map map, GenStepParams parms) { Generated++; Target = map; _ = Rand.Value; }
            public override void PostMapInitialized(Map map, GenStepParams parms) { Posted++; Assert.Same(Target, map); _ = Rand.Value; }
        }
        private sealed class SpecOnlyCapability : ICelestialMapGenerationCapability
        {
            private readonly LocalMapGenerationSpec specification = new(new LocalMapRecipe("third", Array.Empty<MapGenerationStepSpec>()));
            public LocalMapGenerationSpec GetSpecification() => specification;
        }
        private sealed class Resolver : ICelestialCapabilityResolver
        {
            public ICelestialMapGenerationCapability[] Capabilities = Array.Empty<ICelestialMapGenerationCapability>();
            public IReadOnlyList<T> GetCapabilities<T>(CelestialInstanceId id) where T : class => Capabilities.OfType<T>().ToArray();
            public bool TryGetInitializationReference(CelestialInstanceId owner, string key, out CelestialInstanceId target) { target = default; return false; }
        }
    }
}
