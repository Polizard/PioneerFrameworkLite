using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PioneerFramework.Celestial;
using PioneerFramework.MapGeneration;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class NativeLocalMapExecutionTests
{
    private static FrozenLocalMapContext Frozen(params MapGenerationStepSpec[] steps) => new(new("local-owner", 123, 0, 6, 6, "Plain"), new(new LocalMapRecipe("recipe", steps)));
    private static GenStepWithParams Native(string name, GenStep step, float order = 450, GenStepParams parms = default) => new(new GenStepDef { defName = name, genStep = step, order = order }, parms);
    private static MapGenerationStepSpec Rules(params string[] names) => new("rules", LocalMapBuiltinConfigurations.NativeRules, 1, 1,
        MapGenerationStage.Fields, MapGenerationDomain.Natural, LocalMapBuiltinConfigurations.Encode(new NativeRulesConfiguration(new[] { new NativeMapStepSelector(names) })));
    private static GenStep NativeStep(string typeName)
    {
        var type = AccessTools.TypeByName("RimWorld." + typeName)
            ?? throw new InvalidOperationException("Missing native step type: " + typeName);
        return (GenStep)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Could not create native step: " + typeName));
    }

    [Fact]
    public void SkippedNativeStepsHaveNoCallbacksAndIncludedStepsKeepParametersAndObjectIdentity()
    {
        using var f = new CelestialPlanetMapDiversityTests.NativeFixture();
        var a = new CountingStep(); var b = new CountingStep();
        var map = f.Map(f.Layer("session", 1, new()));
        var frozen = Frozen(Rules("skip"));
        var plan = NativeLocalMapPlan.Build(frozen, MapGenerationPurpose.Encounter, new[] { Native("skip", a), Native("keep", b) }, LocalMapGenerationModules.CreateRegistry());
        var services = new NativeMapGenerationServices(map, frozen.Source, plan.NativeSteps);
        var run = new MapGenerationExecutor(plan.Plan, frozen.Source, new Services(services), preparedModules: plan.Modules);
        Assert.Equal(MapGenerationState.Generated, run.Execute().State); Assert.Equal(MapGenerationState.Ready, run.PostInitialize().State);
        Assert.Equal(0, a.Generated + a.Posted); Assert.Equal(1, b.Generated); Assert.Equal(1, b.Posted);
        Assert.Same(map, b.Target);
        Assert.Contains(plan.Plan.Diagnostics, d => d.Code == "OpaqueNativeStep");
    }
    [Fact]
    public void RequiredSiteNodesAndPurposeRemovalOfNaturalNodesAreRejected()
    {
        var mandatory = Native("site", new CountingStep()); mandatory.def.linkWithSite = new SitePartDef { defName = "required" };
        Assert.Contains("RequiredNativeStepConflict", Assert.Throws<InvalidOperationException>(() => NativeLocalMapPlan.Build(Frozen(Rules("site")),
            MapGenerationPurpose.Quest, new[] { mandatory }, LocalMapGenerationModules.CreateRegistry())).Message);
        var registry = LocalMapGenerationModules.CreateRegistry();
        registry.Register(new("purpose", 1, new[] { 1 }, _ => { }, _ => new Contributor(new(remove: new[] { new NativeMapStepSelector(new[] { "terrain" }) }))));
        var purpose = new MapGenerationStepSpec("purpose", "purpose", 1, 1, MapGenerationStage.Content, MapGenerationDomain.Purpose);
        Assert.Contains("PurposeCannotRemoveNaturalStep", Assert.Throws<InvalidOperationException>(() => NativeLocalMapPlan.Build(Frozen(purpose),
            MapGenerationPurpose.Quest, new[] { Native("terrain", new GenStep_Terrain(), 210) }, registry)).Message);
    }
    [Fact]
    public void QuestSiteContentIsRequiredOnlyWhenTheSiteFlagIsSet()
    {
        var scen = Native("scenParts", NativeStep("GenStep_ScenParts"), 875);
        var start = Native("findStart", NativeStep("GenStep_FindPlayerStartSpot"), 850);
        var candidates = new[] { scen, start };
        var rules = Rules("scenParts", "findStart");
        var registry = LocalMapGenerationModules.CreateRegistry();
        // requiredSiteContent:false keeps the previous behavior: a natural selector may remove both nodes.
        Assert.Empty(NativeLocalMapPlan.Build(Frozen(rules), MapGenerationPurpose.Colony, candidates, registry, requiredSiteContent: false).NativeSteps);
        // Quest + requiredSiteContent:true keeps both in the plan when nothing selects them.
        var kept = NativeLocalMapPlan.Build(Frozen(), MapGenerationPurpose.Quest, candidates, registry, requiredSiteContent: true);
        Assert.Contains(kept.Plan.Steps, step => step.Spec.Key == "native/scenParts/0");
        Assert.Contains(kept.Plan.Steps, step => step.Spec.Key == "native/findStart/0");
        // NativeRules removal is rejected.
        Assert.Contains("RequiredNativeStepConflict", Assert.Throws<InvalidOperationException>(() =>
            NativeLocalMapPlan.Build(Frozen(rules), MapGenerationPurpose.Quest, candidates, registry, requiredSiteContent: true)).Message);
        // A purpose contributor's removal is rejected too; without the flag it is allowed (Content > NaturalSurface).
        var purposeRegistry = LocalMapGenerationModules.CreateRegistry();
        purposeRegistry.Register(new("purpose", 1, new[] { 1 }, _ => { },
            _ => new Contributor(new(remove: new[] { new NativeMapStepSelector(new[] { "scenParts", "findStart" }) }))));
        var purpose = new MapGenerationStepSpec("purpose", "purpose", 1, 1, MapGenerationStage.Content, MapGenerationDomain.Purpose);
        Assert.Contains("RequiredNativeStepConflict", Assert.Throws<InvalidOperationException>(() =>
            NativeLocalMapPlan.Build(Frozen(purpose), MapGenerationPurpose.Quest, candidates, purposeRegistry, requiredSiteContent: true)).Message);
        Assert.Empty(NativeLocalMapPlan.Build(Frozen(purpose), MapGenerationPurpose.Quest, candidates, purposeRegistry, requiredSiteContent: false).NativeSteps);
    }
    [Fact]
    public void PurposeContributorCannotPreloadInitialInputs()
    {
        var registry = LocalMapGenerationModules.CreateRegistry();
        registry.Register(new("purpose", 1, new[] { 1 }, _ => { }, _ => new Contributor(new(initialInputs: new()))));
        var purpose = new MapGenerationStepSpec("purpose", "purpose", 1, 1, MapGenerationStage.Content, MapGenerationDomain.Purpose);
        Assert.Contains("PurposeCannotOverrideNaturalInputs", Assert.Throws<InvalidOperationException>(() => NativeLocalMapPlan.Build(Frozen(purpose),
            MapGenerationPurpose.Quest, Array.Empty<GenStepWithParams>(), registry)).Message);
    }
    [Fact]
    public void UnadaptedNativeAssemblyStepsAreOpaqueAndUnknownRelativeOrderIsRetained()
    {
        var steps = new[] { Native("z", new GenStep_ScatterRuinsSimple(), 750), Native("a", new CountingStep(), 750) };
        var plan = NativeLocalMapPlan.Build(Frozen(), MapGenerationPurpose.Encounter, steps, LocalMapGenerationModules.CreateRegistry());
        Assert.Equal(new[] { "native/z/0", "native/a/0" }, plan.Plan.Steps.Select(s => s.Spec.Key));
        Assert.All(plan.Plan.Steps, s => Assert.True(s.Spec.IsOpaque));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VanillaBasePlayerAndEncounterNoDlcCandidateSnapshotsCompile(bool player)
    {
        var nodes = CoreCandidateSnapshot(player);
        var plan = NativeLocalMapPlan.Build(Frozen(), player ? MapGenerationPurpose.Colony : MapGenerationPurpose.Encounter, nodes, LocalMapGenerationModules.CreateRegistry());
        Assert.Equal(nodes.Count, plan.NativeSteps.Count);
        Assert.Contains(plan.Plan.Steps, s => s.Spec.Key == "native/Terrain/0");
        Assert.Contains(plan.Plan.Steps, s => s.Spec.Key == (player ? "native/RocksFromGrid/0" : "native/RocksFromGrid_NoMinerals/0"));
    }
    [Fact]
    public void AdaptedNativeOrderMatchesVanillaAcrossStageBarriers()
    {
        var plan = NativeLocalMapPlan.Build(Frozen(), MapGenerationPurpose.Encounter,
            CoreCandidateSnapshot(false), LocalMapGenerationModules.CreateRegistry());
        var keys = plan.Plan.Steps.Select(step => step.Spec.Key).ToArray();
        int Index(string key)
        {
            var index = Array.IndexOf(keys, key);
            Assert.True(index >= 0, "Missing native step: " + key);
            return index;
        }
        // P2-2: vanilla slot order is Content(500-875) -> Plants(900) -> Geysers(950) -> Chunks(970) -> Snow(1150).
        Assert.True(Index("native/Terrain/0") < Index("native/FindPlayerStartSpot/0"));
        Assert.True(Index("native/FindPlayerStartSpot/0") < Index("native/Plants/0"));
        Assert.True(Index("native/Plants/0") < Index("native/SteamGeysers/0"));
        Assert.True(Index("native/SteamGeysers/0") < Index("native/RockChunks/0"));
        Assert.True(Index("native/RockChunks/0") < Index("native/Snow/0"));
        Assert.Equal(MapGenerationStage.Populate, plan.Plan.Steps.Single(step => step.Spec.Key == "native/SteamGeysers/0").Spec.Stage);
        Assert.Equal(MapGenerationStage.Populate, plan.Plan.Steps.Single(step => step.Spec.Key == "native/RockChunks/0").Spec.Stage);
    }
    internal static List<GenStepWithParams> CoreCandidateSnapshot(bool player)
    {
        // Snapshot of Core/Defs/MapGeneration/{Common,BasePlayer,Encounter}MapGenerator.xml.
        // Excludes DLC, biome, mutator and site-added candidates; those depend on the live world.
        var entries = new (string name, string type, int order)[] {
            ("ElevationFertility", "ElevationFertility", 10), ("MutatorPostElevationFertility", "MutatorPostElevationFertility", 20),
            ("Terrain", "Terrain", 210), ("MutatorPostTerrain", "MutatorPostTerrain", 220), ("Roads", "Roads", 390),
            ("RockChunks", "RockChunks", 970), ("MutatorCriticalStructures", "MutatorCriticalStructures", 500),
            ("MutatorNonCriticalStructures", "MutatorNonCriticalStructures", 700), ("ScatterRuinsSimple", "ScatterRuinsSimple", 750),
            ("SteamGeysers", "ScatterGeysers", 950), ("FindPlayerStartSpot", "FindPlayerStartSpot", 850),
            ("ScenParts", "ScenParts", 875), ("Plants", "Plants", 900), ("Snow", "Snow", 1150),
            ("MutatorFinal", "MutatorFinal", 1600), ("Animals", "Animals", 1200), ("Fog", "Fog", 1500) };
        var nodes = entries.Select(e => Native(e.name, (GenStep)Activator.CreateInstance(AccessTools.TypeByName("RimWorld.GenStep_" + e.type)), e.order)).ToList();
        nodes.Add(Native(player ? "RocksFromGrid" : "RocksFromGrid_NoMinerals", new GenStep_RocksFromGrid(), 200));
        if (player) { nodes.Add(Native("ScatterShrines", new GenStep_ScatterShrines(), 750)); nodes.Add(Native("CaveHives", new GenStep_CaveHives(), 1120)); }
        return nodes;
    }
    [Fact]
    public void NativeTerrainRequiresDeclaredProviderAndActualPublishedGrids()
    {
        var terrain = Native("terrain", new GenStep_Terrain(), 210);
        Assert.Contains("MissingNativeInputProvider", Assert.Throws<InvalidOperationException>(() => NativeLocalMapPlan.Build(Frozen(),
            MapGenerationPurpose.Colony, new[] { terrain }, LocalMapGenerationModules.CreateRegistry())).Message);
        using var f = new CelestialPlanetMapDiversityTests.NativeFixture();
        var registry = LocalMapGenerationModules.CreateRegistry();
        registry.Register(new("replacement", 1, new[] { 1 }, _ => { }, _ => new Contributor(new(providesNativeInputs: new[] { "Elevation", "Fertility" }))));
        var frozen = Frozen(new MapGenerationStepSpec("replacement", "replacement", 1, 1, MapGenerationStage.Fields, MapGenerationDomain.Natural));
        var plan = NativeLocalMapPlan.Build(frozen, MapGenerationPurpose.Colony, new[] { terrain }, registry);
        var map = f.Map(f.Layer("inputs", 1, new()));
        NativeLocalMapHooks.ClearWorkingData();
        var run = new MapGenerationExecutor(plan.Plan, frozen.Source, new Services(new NativeMapGenerationServices(map, frozen.Source, plan.NativeSteps)), preparedModules: plan.Modules);
        var report = run.Execute();
        Assert.Equal(MapGenerationState.Failed, report.State); Assert.Contains("NativeInputUnavailable", report.Steps.Last().Error);
        Assert.False(MapGenerator.TryGetVar<MapGenFloatGrid>("Elevation", out _));
    }
    [Fact]
    public void InitialModulePrecedesEveryFieldAndTerrainReplacementFollowsAllNaturalNodesOnEncounter()
    {
        var initial = new MapGenerationStepSpec("z.inputs", LocalMapBuiltinConfigurations.InitialInputs, 1, 1, MapGenerationStage.Fields,
            MapGenerationDomain.Natural, LocalMapBuiltinConfigurations.Encode(new InitialMapInputs()));
        var replace = new MapGenerationStepSpec("a.replace", LocalMapBuiltinConfigurations.TerrainReplacement, 1, 1, MapGenerationStage.NaturalSurface,
            MapGenerationDomain.Natural, LocalMapBuiltinConfigurations.Encode(new TerrainReplacementConfiguration(Array.Empty<string>(), "Regolith")));
        var nodes = new[] { Native("fields", new GenStep_ElevationFertility(), 10), Native("terrain", new GenStep_Terrain(), 210),
            Native("postterrain", new GenStep_MutatorPostTerrain(), 220), Native("chunks", new GenStep_RockChunks(), 970), Native("content", new CountingStep(), 750) };
        var plan = NativeLocalMapPlan.Build(Frozen(initial, replace), MapGenerationPurpose.Encounter, nodes, LocalMapGenerationModules.CreateRegistry());
        var keys = plan.Plan.Steps.Select(s => s.Spec.Key).ToArray();
        Assert.Equal("z.inputs", keys[0]);
        // P2-2: RockChunks is a Populate node again (vanilla slot 970), so terrain replacement now precedes it.
        Assert.True(Array.IndexOf(keys, "a.replace") < Array.IndexOf(keys, "native/chunks/0"));
        Assert.True(Array.IndexOf(keys, "a.replace") < Array.IndexOf(keys, "native/content/0"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeThrowAndErrorLogStopLaterStepsAndRestoreRandSession(bool log)
    {
        using var f = new CelestialPlanetMapDiversityTests.NativeFixture();
        var map = f.Map(f.Layer("failure", 1, new()));
        var first = new CountingStep { Failure = log ? "log" : "throw" }; var later = new CountingStep();
        var session = new NativeLocalMapSession(Frozen(), MapGenerationPurpose.Encounter, map.Parent) { Map = map };
        var h = new Harmony("PFL.Tests.LocalMapNoiseBoundary");
        h.Patch(AccessTools.Method(typeof(RockNoises), nameof(RockNoises.Init)), prefix: new(typeof(NativeLocalMapExecutionTests), nameof(Skip)));
        NativeLocalMapHooks.Install();
        MapGenerator.mapBeingGenerated = map;
        var candidates = new[] { Native("first", first), Native("later", later) };
        Rand.PushState(83); int expected = Rand.Int; Rand.PopState(); Rand.PushState(83);
        try
        {
            session.Activate();
            var error = Assert.ThrowsAny<Exception>(() => MapGenerator.GenerateContentsIntoMap(candidates, map, 7));
            Assert.True(session.Executor != null, error.ToString());
            Assert.Equal(expected, Rand.Int); Assert.Equal(0, later.Generated); Assert.Equal(0, first.Posted);
            Assert.Equal(MapGenerationState.Failed, session.Executor.Report.State);
            Assert.Throws<InvalidOperationException>(() => MapGenerator.MapGeneratorPostInit(Array.Empty<GenStepWithParams>(), map));
        }
        finally { session.Release(); MapGenerator.mapBeingGenerated = null; h.UnpatchAll(h.Id); Rand.PopState(); NativeLocalMapHooks.ClearWorkingData(); }
        Assert.False(NativeLocalMapHooks.IsActive);
    }
    [Fact]
    public void InitialInputsAreExactMapOnlyAndDoNotPoisonLayerDiversityOrGeology()
    {
        using var f = new CelestialPlanetMapDiversityTests.NativeFixture();
        var layer = f.Layer("isolation", 1, new(soilFertility: 2, geologyVariation: .5));
        var map = f.Map(layer); var other = f.Map(layer);
        var frozen = new FrozenLocalMapContext(new("isolation", 1, 0, 6, 6, f.Biome.defName, new Dictionary<string,double> { ["geology"] = 1 }), Frozen().Specification);
        var native = new NativeMapGenerationServices(map, frozen.Source, Array.Empty<NativeMapGenerationStep>());
        native.SetInitialOverrides(new(soilFertility: 3, geologyVariation: 1, allowCaves: false));
        var session = new NativeLocalMapSession(frozen, MapGenerationPurpose.Colony, map.Parent) { Map = map, Native = native };
        Assert.True(CelestialMapGeneration.TryGetDiversity(map.Tile, out var original));
        double baseline = CelestialMapGeneration.GeologyFactor(map.Tile, original);
        session.Activate(); MapGenerator.mapBeingGenerated = map;
        try
        {
            float fertility = 1; CelestialPlanetMapHooks.SoilFertility(map, ref fertility); Assert.Equal(6, fertility);
            fertility = 1; CelestialPlanetMapHooks.SoilFertility(other, ref fertility); Assert.Equal((float)(2 * baseline), fertility);
            Assert.False(CelestialPlanetMapHooks.Caves(map));
            Assert.True(CelestialMapGeneration.TryGetDiversity(map.Tile, out var unchanged)); Assert.Same(original, unchanged);
            Assert.Equal(baseline, CelestialMapGeneration.GeologyFactor(map.Tile, original));
        }
        finally { session.Release(); MapGenerator.mapBeingGenerated = null; }
        float after = 1; CelestialPlanetMapHooks.SoilFertility(map, ref after); Assert.Equal((float)(2 * baseline), after);
    }
    [Fact]
    public void ErrorsOutsidePflNativeWindowsDoNotPoisonSynchronousSession()
    {
        using var f = new CelestialPlanetMapDiversityTests.NativeFixture();
        var map = f.Map(f.Layer("thread", 1, new()));
        var session = new NativeLocalMapSession(Frozen(), MapGenerationPurpose.Colony, map.Parent);
        session.Activate();
        try
        {
            var thread = new System.Threading.Thread(() => NativeLocalMapHooks.ObserveError("async"));
            thread.Start(); thread.Join();
            Assert.Null(session.Error);
            // P1-2: a synchronous Log.Error raised outside a PFL-out-sourced native callback is not this map's failure.
            NativeLocalMapHooks.ObserveError("outside-window");
            Assert.Null(session.Error);
            // Inside a host window the same log still aborts at the window boundary.
            Assert.Throws<InvalidOperationException>(() =>
            {
                using (session.EnterHost("probe")) NativeLocalMapHooks.ObserveError("inside-window");
            });
            Assert.Contains("inside-window", session.Error);
            // The window depth must be balanced even when the callback threw.
            Assert.Equal(0, (int)AccessTools.Field(typeof(NativeLocalMapSession), "errorWindowDepth").GetValue(session));
        }
        finally { session.Release(); }
    }
    [Fact]
    public void SwallowedHostLogErrorFailsAtSynchronousStageBoundary()
    {
        using var f = new CelestialPlanetMapDiversityTests.NativeFixture();
        var map = f.Map(f.Layer("host-error", 1, new()));
        var session = new NativeLocalMapSession(Frozen(), MapGenerationPurpose.Colony, map.Parent);
        NativeLocalMapHooks.Install();
        CelestialXmlTestScope.ExpectedError = text => text == "host-swallowed";
        session.Activate();
        try
        {
            var method = AccessTools.Method(typeof(NativeLocalMapHooks), "Run");
            var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, new object[] { "FinalizeInit", (Action)(() => Log.Error("host-swallowed")) }));
            Assert.Contains("host-swallowed", failure.ToString());
            Assert.True(session.Error != null, failure.ToString());
            Assert.Contains("host-swallowed", session.Error);
        }
        finally { session.Release(); CelestialXmlTestScope.ExpectedError = null; }
    }
    private sealed class ThrowingMapComponent(Map map) : MapComponent(map)
    { public override void MapGenerated() => throw new InvalidOperationException("component-probe"); }
    [Fact]
    public void ActualMapGeneratedCatchesComponentExceptionAndHostBoundaryFails()
    {
        using var f = new CelestialPlanetMapDiversityTests.NativeFixture();
        var map = f.Map(f.Layer("host-component", 1, new()));
        map.components = new List<MapComponent> { new ThrowingMapComponent(map) };
        var session = new NativeLocalMapSession(Frozen(), MapGenerationPurpose.Colony, map.Parent) { Map = map };
        NativeLocalMapHooks.Install();
        CelestialXmlTestScope.ExpectedError = text => text.Contains("component-probe");
        session.Activate();
        try
        {
            var generated = AccessTools.Method(typeof(NativeLocalMapHooks), "Generated");
            var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() => generated.Invoke(null, new object[] { map }));
            Assert.Contains("component-probe", failure.ToString());
            Assert.Contains("component-probe", session.Error);
            Assert.Throws<InvalidOperationException>(() => session.EnterHost("later"));
        }
        finally { session.Release(); CelestialXmlTestScope.ExpectedError = null; }
    }
    [Fact]
    public void RegisteredHeadlessMapRunsSessionCallbacksAndPublishesReadyOnlyForExactIdentity()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var parent = f.Backend.CreateParent(f.Address);
        var map = f.Backend.Generate(parent, 50, 50, null);
        map.info.Size = new(50, 1, 50);
        var frozen = LocalMapSourceResolver.Resolve(parent, map.info.Size, new MapGeneratorDef { defName = "Encounter" }, false).Value;
        var session = new NativeLocalMapSession(frozen, MapGenerationPurpose.Encounter, parent) { Map = map };
        var step = new CountingStep();
        var patch = new Harmony("PFL.Tests.HeadlessNativeSuccess");
        patch.Patch(AccessTools.Method(typeof(RockNoises), nameof(RockNoises.Init)), prefix: new(typeof(NativeLocalMapExecutionTests), nameof(Skip)));
        session.Activate(); MapGenerator.mapBeingGenerated = map;
        try
        {
            session.Generate(new[] { Native("content", step) }, map);
            session.PostInitialize(map);
            session.Finish(map, null);
            Assert.Equal(1, step.Generated); Assert.Equal(1, step.Posted);
            Assert.Equal(MapGenerationState.Ready, Assert.Single(f.Service.CaptureSites()).Generation.State);
        }
        finally { session.Release(); MapGenerator.mapBeingGenerated = null; NativeLocalMapHooks.ClearWorkingData(); patch.UnpatchAll(patch.Id); }
    }
    [Fact]
    public void UnrelatedNativeInsertionDoesNotChangeStepIdentity()
    {
        var target = Native("target", new CountingStep(), 500);
        var registry = LocalMapGenerationModules.CreateRegistry();
        var a = NativeLocalMapPlan.Build(Frozen(), MapGenerationPurpose.Colony, new[] { target }, registry);
        var b = NativeLocalMapPlan.Build(Frozen(), MapGenerationPurpose.Colony, new[] { Native("other", new CountingStep(), 400), target }, registry);
        Assert.Equal(a.Plan.Steps.Single().Spec.Key, b.Plan.Steps.Single(s => s.Spec.Key.Contains("target")).Spec.Key);
    }
    [Fact]
    public void AuthoredNativeTokenCannotRepeatAnAutomaticallyAdaptedToken()
    {
        var step = new MapGenerationStepSpec("authored", LocalMapBuiltinConfigurations.Native, 1, 1, MapGenerationStage.Content, MapGenerationDomain.Natural,
            LocalMapBuiltinConfigurations.Encode(new NativeGenStepConfiguration("native/target/0")));
        Assert.Contains("DuplicateNativeToken", Assert.Throws<InvalidOperationException>(() => NativeLocalMapPlan.Build(Frozen(step), MapGenerationPurpose.Colony,
            new[] { Native("target", new CountingStep()) }, LocalMapGenerationModules.CreateRegistry())).Message);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthoredNativePreventionConflictsCannotBeIgnored(bool authoredPrevents)
    {
        var added = new GenStepDef { defName = "PFL_TestExplicitNative", genStep = new CountingStep(), order = 450 };
        var incoming = new GenStepDef { defName = "PFL_TestIncoming", genStep = new CountingStep(), order = 450 };
        if (authoredPrevents) added.preventsGenSteps = new() { incoming }; else incoming.preventsGenSteps = new() { added };
        DefDatabase<GenStepDef>.Add(added);
        try
        {
            var step = new MapGenerationStepSpec("authored", LocalMapBuiltinConfigurations.Native, 1, 1, MapGenerationStage.Content, MapGenerationDomain.Natural,
                LocalMapBuiltinConfigurations.Encode(new NativeGenStepConfiguration("def/" + added.defName)));
            Assert.Contains("DuplicateNativeToken", Assert.Throws<InvalidOperationException>(() => NativeLocalMapPlan.Build(Frozen(step), MapGenerationPurpose.Colony,
                new[] { new GenStepWithParams(added, default) }, LocalMapGenerationModules.CreateRegistry())).Message);
            Assert.Contains("NativePreventionConflict", Assert.Throws<InvalidOperationException>(() => NativeLocalMapPlan.Build(Frozen(step), MapGenerationPurpose.Colony,
                new[] { new GenStepWithParams(incoming, default) }, LocalMapGenerationModules.CreateRegistry())).Message);
        }
        finally { AccessTools.Method(typeof(DefDatabase<GenStepDef>), "Remove").Invoke(null, new object[] { added }); }
    }
    [Fact]
    public void AuthoredNativeFieldProviderSatisfiesNativeTerrainAndAuthoredTerrainNeedsProvider()
    {
        var fields = new GenStepDef { defName = "PFL_TestFields", genStep = new GenStep_ElevationFertility(), order = 10 };
        var terrain = new GenStepDef { defName = "PFL_TestTerrain", genStep = new GenStep_Terrain(), order = 210 };
        DefDatabase<GenStepDef>.Add(fields); DefDatabase<GenStepDef>.Add(terrain);
        MapGenerationStepSpec Step(string key, GenStepDef def, MapGenerationStage stage) => new(key, LocalMapBuiltinConfigurations.Native, 1, 1, stage, MapGenerationDomain.Natural,
            LocalMapBuiltinConfigurations.Encode(new NativeGenStepConfiguration("def/" + def.defName)));
        try
        {
            var registry = LocalMapGenerationModules.CreateRegistry();
            Assert.Contains("MissingNativeInputProvider", Assert.Throws<InvalidOperationException>(() => NativeLocalMapPlan.Build(Frozen(Step("terrain", terrain, MapGenerationStage.NaturalSurface)),
                MapGenerationPurpose.Colony, Array.Empty<GenStepWithParams>(), registry)).Message);
            var plan = NativeLocalMapPlan.Build(Frozen(Step("fields", fields, MapGenerationStage.Fields), Step("terrain", terrain, MapGenerationStage.NaturalSurface)),
                MapGenerationPurpose.Colony, Array.Empty<GenStepWithParams>(), registry);
            Assert.Equal(new[] { "fields", "terrain" }, plan.Plan.Steps.Select(s => s.Spec.Key));
            var mixed = NativeLocalMapPlan.Build(Frozen(Step("fields", fields, MapGenerationStage.Fields)), MapGenerationPurpose.Colony,
                new[] { new GenStepWithParams(terrain, default) }, registry);
            Assert.Contains(mixed.Plan.Steps, s => s.Spec.Key == "native/PFL_TestTerrain/0");
        }
        finally { var remove = AccessTools.Method(typeof(DefDatabase<GenStepDef>), "Remove"); remove.Invoke(null, new object[] { terrain }); remove.Invoke(null, new object[] { fields }); }
    }
    [Theory]
    [InlineData("Base_Player", MapGenerationPurpose.Colony)]
    [InlineData("BasePlayer_SecondArchonexusCycle", MapGenerationPurpose.Colony)]
    [InlineData("BasePlayer_ThirdArchonexusCycle", MapGenerationPurpose.Colony)]
    [InlineData("Encounter", MapGenerationPurpose.Encounter)]
    public void NativeGeneratorRoutesExpectedPurpose(string generator, MapGenerationPurpose expected)
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var parent = f.Backend.CreateParent(f.Address);
        Assert.Equal(expected, NativeLocalMapHooks.ResolvePurpose(parent, new MapGeneratorDef { defName = generator }));
    }
    [Fact]
    public void ExcludedNativeMapsBypassFailedOwnedTile()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var parent = f.Backend.CreateParent(f.Address);
        parent.def = new WorldObjectDef { mapGenerator = new MapGeneratorDef { defName = "Encounter" } };
        var frozen = LocalMapSourceResolver.Resolve(parent, new IntVec3(50, 1, 50), new MapGeneratorDef { defName = "Encounter" }, false).Value;
        var record = LocalMapGenerationRecord.Capture(new(frozen.Source, frozen.Specification, MapGenerationPurpose.Colony), MapGenerationState.Failed, "probe", "failure", null);
        f.Service.RecordNativeGeneration(f.Address with { MapParentId = parent.ID }, null, record);
        NativeLocalMapHooks.Begin(new(50, 1, 50), parent, new MapGeneratorDef { defName = "Base_Player", isUnderground = true }, false, false, out var state);
        Assert.Null(state);
        NativeLocalMapHooks.Begin(new(50, 1, 50), parent, new MapGeneratorDef { defName = "Encounter" }, true, false, out state);
        Assert.Null(state);
        Assert.Throws<InvalidOperationException>(() => NativeLocalMapHooks.Begin(new(50, 1, 50), parent, new MapGeneratorDef { defName = "Encounter" }, false, false, out _));
    }
    [Fact]
    public void ReuseGuardChecksTheExactMapParentAndExcludesSpecialMaps()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var parent = f.Backend.CreateParent(f.Address);
        parent.def = new WorldObjectDef { mapGenerator = new MapGeneratorDef { defName = "Encounter" } };
        var frozen = LocalMapSourceResolver.Resolve(parent, new IntVec3(50, 1, 50), new MapGeneratorDef { defName = "Encounter" }, false).Value;
        var record = LocalMapGenerationRecord.Capture(new(frozen.Source, frozen.Specification, MapGenerationPurpose.Colony), MapGenerationState.Failed, "probe", "failure", null);
        f.Service.RecordNativeGeneration(f.Address with { MapParentId = parent.ID }, null, record);
        Assert.Throws<InvalidOperationException>(() => NativeLocalMapHooks.CheckReuse(new object[] { parent.Tile }));
        var other = f.Backend.CreateParent(f.Address);
        var map = f.Backend.Generate(other, 50, 50, null);
        map.generatorDef = new MapGeneratorDef { defName = "special", isUnderground = true };
        NativeLocalMapHooks.CheckReuse(new object[] { parent.Tile });
        map.generatorDef = new MapGeneratorDef { defName = "Encounter" };
        NativeLocalMapHooks.CheckReuse(new object[] { parent.Tile });
    }
    [Fact]
    public void ReuseGuardAllowsExcludedMapAndRejectsExactFailedMap()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var parent = f.Backend.CreateParent(f.Address); var map = f.Backend.Generate(parent, 50, 50, null);
        var frozen = LocalMapSourceResolver.Resolve(parent, new IntVec3(50, 1, 50), new MapGeneratorDef { defName = "Encounter" }, false).Value;
        var failed = LocalMapGenerationRecord.Capture(new(frozen.Source, frozen.Specification, MapGenerationPurpose.Encounter), MapGenerationState.Failed, "probe", "failure", null);
        f.Service.RecordNativeGeneration(f.Address with { MapParentId = parent.ID }, map.uniqueID, failed);
        map.generatorDef = new MapGeneratorDef { defName = "Encounter", isUnderground = true };
        NativeLocalMapHooks.CheckReuse(new object[] { parent.Tile });
        map.generatorDef = new MapGeneratorDef { defName = "Encounter" };
        Assert.Throws<InvalidOperationException>(() => NativeLocalMapHooks.CheckReuse(new object[] { parent.Tile }));
        map.uniqueID++;
        NativeLocalMapHooks.CheckReuse(new object[] { parent.Tile });
    }
    [Fact]
    public void ReuseGuardRejectsMapRegisteredAfterFailureWithNullMapId()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var parent = f.Backend.CreateParent(f.Address);
        var frozen = LocalMapSourceResolver.Resolve(parent, new IntVec3(50, 1, 50), new MapGeneratorDef { defName = "Encounter" }, false).Value;
        var failed = LocalMapGenerationRecord.Capture(new(frozen.Source, frozen.Specification, MapGenerationPurpose.Encounter), MapGenerationState.Failed, "probe", "failure", null);
        f.Service.RecordNativeGeneration(f.Address with { MapParentId = parent.ID }, null, failed);
        var map = f.Backend.Generate(parent, 50, 50, null);
        map.generatorDef = new MapGeneratorDef { defName = "Encounter" };
        Assert.Throws<InvalidOperationException>(() => NativeLocalMapHooks.CheckReuse(new object[] { parent.Tile }));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovedNativeParentKeepsOriginalFailedProvenanceThroughSaveAndRestore(bool moveOutsideCelestialSurface)
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var parent = f.Backend.CreateParent(f.Address);
        var generator = new MapGeneratorDef { defName = "Encounter" };
        parent.def = new WorldObjectDef { mapGenerator = generator };
        NativeLocalMapHooks.Begin(new(50, 1, 50), parent, generator, false, false, out var session);
        Assert.NotNull(session);
        using var vanilla = new SurfaceLayer(900, new PlanetLayerDef { tileType = typeof(SurfaceTile) }, 100,
            UnityEngine.Vector3.zero, UnityEngine.Vector3.forward, 180, 0, 0, 0, 0);
        var nativeLayers = (Dictionary<int, PlanetLayer>)AccessTools.Field(typeof(WorldGrid), "planetLayers").GetValue(f.Native.Grid);
        if (moveOutsideCelestialSurface) nativeLayers.Add(vanilla.LayerID, vanilla);
        var patch = new Harmony("PFL.Tests.MovedLocalMapParent");
        patch.Patch(AccessTools.Method(typeof(RockNoises), nameof(RockNoises.Init)), prefix: new(typeof(NativeLocalMapExecutionTests), nameof(Skip)));
        try
        {
            var map = f.Backend.Generate(parent, 50, 50, null);
            map.info.Size = new(50, 1, 50);
            map.generatorDef = generator;
            MapGenerator.mapBeingGenerated = map;
            session.Generate(new[] { Native("content", new CountingStep()) }, map);
            session.PostInitialize(map);
            var destination = moveOutsideCelestialSurface ? new PlanetTile(0, vanilla) : new PlanetTile(1, f.Root);
            AccessTools.Field(typeof(WorldObject), "tile").SetValue(parent, destination);
            // P1-1 cleanup runs native MapDeiniter, which logs errors for the test-double map; the fixture turns an
            // unexpected Log.Error into an exception, so this window intentionally tolerates the cleanup diagnostics.
            var previousExpected = CelestialXmlTestScope.ExpectedError;
            CelestialXmlTestScope.ExpectedError = _ => true;
            try
            {
                Assert.Contains("Native map identity changed", NativeLocalMapHooks.End(null, map, session).Message);
            }
            finally { CelestialXmlTestScope.ExpectedError = previousExpected; }
            // P1-1: the failed session removed its registered map; the parent itself is retained for failure identity.
            Assert.DoesNotContain(f.Native.Game.Maps, existing => ReferenceEquals(existing, map));
            Assert.Null(parent.Map);
            Assert.Contains(parent, f.Native.World.worldObjects.MapParents);
            var captured = new CelestialSaveAdapter(f.Scope.Runtime).CaptureCheckpoint();
            Assert.True(captured.Success, string.Join(",", captured.Errors));
            var encoded = CelestialSaveCodec.Encode(captured.Value);
            Assert.True(encoded.Success, string.Join(",", encoded.Errors));
            var decoded = CelestialSaveCodec.Decode(encoded.Value);
            Assert.True(decoded.Success, string.Join(",", decoded.Errors));
            Assert.Equal(4, CelestialSaveDocument.CurrentVersion);
            var site = Assert.Single(decoded.Value.Maps);
            Assert.Equal(f.Address with { MapParentId = parent.ID }, site.Address);
            Assert.Null(site.MapId);
            Assert.Equal(MapGenerationState.Failed, site.Generation.State);
            Assert.Equal(f.Address.InstanceId.Value, site.Generation.Source.SourceId);
            Assert.Equal(f.Address.TileId, site.Generation.Source.TileId);
            Assert.Equal(MapGenerationSeeds.Root(session.Frozen.Source), site.Generation.RootSeed);
            var bridge = new PioneerFramework.TestSupport.Celestial.DeterministicTestPhysicsBridge(Guid.NewGuid());
            var restore = new CelestialRestoreCoordinator(bridge.SessionId, bridge, CelestialBuiltinCompRegistry.Create(),
                _ => throw new Exception("No resampling"), mapBackend: f.Backend);
            Assert.True(restore.RestoreIdentities(decoded.Value).Success);
            var binding = restore.BindNativeObjects();
            Assert.True(binding.Success, string.Join(",", binding.Errors));
            Assert.True(restore.CompleteRestore().Success);
            Assert.True(new CelestialSaveAdapter(restore.Runtime).CaptureCheckpoint().Success);
            Assert.Equal(site.Address, restore.Runtime.Maps.FindFailedSite(parent, map)?.Address);
            Assert.Contains("MapGenerationIncomplete", Assert.Throws<InvalidOperationException>(
                () => NativeLocalMapHooks.CheckReuse(new object[] { destination })).Message);
            Assert.Contains("MapGenerationIncomplete", Assert.Throws<InvalidOperationException>(
                () => NativeLocalMapHooks.Begin(new(50, 1, 50), parent, generator, false, false, out _)).Message);
            var underground = new MapGeneratorDef { defName = "Encounter", isUnderground = true };
            map.generatorDef = underground;
            // With no live map left, CheckReuse derives the generator from the parent, so the parent def carries it.
            parent.def.mapGenerator = underground;
            NativeLocalMapHooks.CheckReuse(new object[] { destination });
            NativeLocalMapHooks.Begin(new(50, 1, 50), parent, underground, false, false, out var excluded);
            Assert.Null(excluded);
            parent.def.mapGenerator = generator;
            map.generatorDef = generator;
            // P1-1: the failed site has no MapId, so every reuse at this parent stays rejected.
            Assert.Contains("MapGenerationIncomplete", Assert.Throws<InvalidOperationException>(
                () => NativeLocalMapHooks.CheckReuse(new object[] { destination })).Message);
            // Finish already removed the partially generated map; there is no live map left to retire.
            Assert.DoesNotContain(f.Native.Game.Maps, existing => ReferenceEquals(existing, map));
            CelestialNativeMapLifecycle.CaptureMapRemoval(f.Native.Game, map, out var removal);
            Assert.Null(removal);
            CelestialNativeMapLifecycle.CaptureParentRemoval(f.Native.World.worldObjects, parent, out removal);
            Assert.Equal(site.Address, removal?.Address);
            f.Backend.RemoveEmptyParent(parent);
            CelestialNativeMapLifecycle.ParentRemoved(f.Native.World.worldObjects, removal);
            Assert.True(Assert.Single(f.Service.CaptureSites()).ParentRetired);
        }
        finally
        {
            session.Release();
            MapGenerator.mapBeingGenerated = null;
            NativeLocalMapHooks.ClearWorkingData();
            patch.UnpatchAll(patch.Id);
            if (moveOutsideCelestialSurface) nativeLayers.Remove(vanilla.LayerID);
        }
    }
    [Fact]
    public void NativeSessionFailureIsPublishedOnlyToItsOriginalHost()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var parent = f.Backend.CreateParent(f.Address);
        var generator = new MapGeneratorDef { defName = "Encounter" };
        NativeLocalMapHooks.Begin(new(50, 1, 50), parent, generator, false, false, out var session);
        Assert.NotNull(session);
        try
        {
            using var replacement = new CelestialNativeWorldTestScope();
            // The replacement uses the same layer IDs to expose an accidental current-runtime publication.
            replacement.World.grid = f.Native.Grid;
            var bridge = new PioneerFramework.TestSupport.Celestial.DeterministicTestPhysicsBridge(Guid.NewGuid());
            var runtime = new CelestialRuntime(bridge, bridge.CommittedStamp, f.Service, 1, _ => throw new Exception("No resampling"));
            var surfaces = new CelestialSurfaceService(runtime, DefDatabase<PlanetLayerDef>.GetNamed("PF_CelestialSurface"));
            var replacementMaps = new CelestialMapService(runtime, surfaces, f.Backend);
            Assert.True(replacement.Game.GetComponent<CelestialBodyManager>().AttachCelestialRuntime(runtime).Success);
            var failure = new InvalidOperationException("original-host-probe");
            Assert.Same(failure, NativeLocalMapHooks.End(failure, null, session));
            Assert.Empty(replacementMaps.CaptureSites());
            Assert.Contains("original-host-probe", Assert.Single(f.Service.CaptureSites()).Failure);
        }
        finally { session.Release(); }
    }
    [Fact]
    public void PublicationFailureUpdatesReadyProvenanceAndSurvivesRebind()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var parent = f.Backend.CreateParent(f.Address);
        var frozen = LocalMapSourceResolver.Resolve(parent, new IntVec3(50, 1, 50), new MapGeneratorDef { defName = "Encounter" }, false).Value;
        var ready = LocalMapGenerationRecord.Capture(new(frozen.Source, frozen.Specification, MapGenerationPurpose.Colony), MapGenerationState.Ready, "Done", null, null);
        var index = new CelestialMapSiteIndex();
        Assert.True(index.Bind(new(f.Address, parent.ID, null) { Generation = ready }).Success);
        Assert.True(index.Bind(new(f.Address, parent.ID, null, "publication failure")).Success);
        Assert.True(index.Bind(new(f.Address, parent.ID, null)).Success);
        var result = Assert.Single(index.Capture());
        Assert.Equal("publication failure", result.Failure);
        Assert.Equal(MapGenerationState.Failed, result.Generation.State);
        result.Generation.Validate(frozen.Source.SourceId, frozen.Source.TileId, result.Failure);
    }
    private static void FailCleanup() => throw new InvalidOperationException("cleanup-probe");
    [Fact]
    public void CleanupFailureIsRecordedBeforePublicationAndOriginalExceptionIsPreserved()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var parent = f.Backend.CreateParent(f.Address);
        var frozen = LocalMapSourceResolver.Resolve(parent, new IntVec3(50, 1, 50), new MapGeneratorDef { defName = "Encounter" }, false).Value;
        var session = new NativeLocalMapSession(frozen, MapGenerationPurpose.Colony, parent);
        var patch = new Harmony("PFL.Tests.LocalCleanupFailure");
        patch.Patch(AccessTools.Method(typeof(RockNoises), nameof(RockNoises.Reset)), prefix: new(typeof(NativeLocalMapExecutionTests), nameof(FailCleanup)));
        session.Activate();
        var original = new InvalidOperationException("original-probe");
        try
        {
            Assert.Same(original, NativeLocalMapHooks.End(original, null, session));
            Assert.False(NativeLocalMapHooks.IsActive);
            var site = Assert.Single(f.Service.CaptureSites());
            Assert.Equal(MapGenerationState.Failed, site.Generation.State);
            Assert.Contains("original-probe", site.Failure);
        }
        finally { patch.UnpatchAll(patch.Id); session.Release(); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContentResetFailurePreservesContentFailureAndCannotProduceReady(bool contentFails)
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var parent = f.Backend.CreateParent(f.Address); var map = f.Backend.Generate(parent, 50, 50, null);
        map.info.Size = new(50, 1, 50);
        var frozen = LocalMapSourceResolver.Resolve(parent, map.info.Size, new MapGeneratorDef { defName = "Encounter" }, false).Value;
        var session = new NativeLocalMapSession(frozen, MapGenerationPurpose.Encounter, parent) { Map = map };
        var patch = new Harmony("PFL.Tests.ContentCleanupFailure");
        patch.Patch(AccessTools.Method(typeof(RockNoises), nameof(RockNoises.Init)), prefix: new(typeof(NativeLocalMapExecutionTests), nameof(Skip)));
        patch.Patch(AccessTools.Method(typeof(RockNoises), nameof(RockNoises.Reset)), prefix: new(typeof(NativeLocalMapExecutionTests), nameof(FailCleanup)));
        session.Activate(); MapGenerator.mapBeingGenerated = map;
        try
        {
            var error = Assert.ThrowsAny<Exception>(() => session.Generate(new[] { Native("content", new CountingStep { Failure = contentFails ? "throw" : null }) }, map));
            if (contentFails) Assert.Contains("native test failure", error.ToString()); else Assert.Contains("cleanup-probe", error.ToString());
            // P1-1 removes the registered test-double map; native MapDeiniter logs, which the fixture turns into
            // exceptions unless tolerated for this explicit cleanup window.
            var previousExpected = CelestialXmlTestScope.ExpectedError;
            CelestialXmlTestScope.ExpectedError = _ => true;
            try { session.Finish(map, error); }
            finally { CelestialXmlTestScope.ExpectedError = previousExpected; }
            var record = Assert.Single(f.Service.CaptureSites()).Generation;
            Assert.Equal(MapGenerationState.Failed, record.State);
            Assert.Contains("cleanup-probe", record.Error);
            if (contentFails) Assert.Contains("native test failure", record.Error);
            // P1-1: the partially generated map must not survive the failed session.
            Assert.DoesNotContain(map, f.Native.Game.Maps);
            Assert.Null(Assert.Single(f.Service.CaptureSites()).MapId);
        }
        finally { patch.UnpatchAll(patch.Id); session.Release(); MapGenerator.mapBeingGenerated = null; NativeLocalMapHooks.ClearWorkingData(); }
    }
    private static bool Skip() => false;
    private static int capturedSeed;
    private static double caveNoise;
    private static Map registeredMap;
    private static bool RegisterMapThenFail(Game __instance, Map map)
    {
        // Headless Game.AddMap ends with Find.ColonistBar.MarkColonistsDirty(), which needs a UIRoot that the
        // test scope does not build. Reproduce only the registration half so the native finalizer path under
        // test sees the map inside Game.Maps before the failure is raised.
        if (map == null || __instance.Maps.Contains(map)) throw new InvalidOperationException("addmap-contract");
        __instance.Maps.Add(map);
        registeredMap = map;
        throw new InvalidOperationException("addmap-probe");
    }
    private static bool FailMapRemoval(Game __instance, Map map) => throw new InvalidOperationException("remove-probe");
    private static Map finalizedMap;
    private static bool RegisterMapOnly(Game __instance, Map map)
    {
        // Headless Game.AddMap tail touches UIRoot (ColonistBar); keep only the registration half so the
        // native GenerateMap flow can continue to the injected host-callback failure with the map registered.
        if (map == null || __instance.Maps.Contains(map)) throw new InvalidOperationException("addmap-contract");
        __instance.Maps.Add(map);
        finalizedMap = map;
        return false;
    }
    private static void CaptureCaveAndAbort(TileMutatorWorker_Caves __instance)
    {
        var noise = (Verse.Noise.ModuleBase)AccessTools.Field(typeof(TileMutatorWorker_Caves), "directionNoise").GetValue(__instance);
        caveNoise = noise.GetValue(31, 0, 47);
        throw new InvalidOperationException("cave-init-probe");
    }
    private static bool CaptureAndAbort(Map __instance)
    {
        capturedSeed = Rand.Int;
        // This is the first native component entry; GPU-heavy component construction is deliberately not executed.
        throw new InvalidOperationException("early-seed-probe");
    }
    [Fact]
    public void ActualCavesInitConsumesOwnedStableHostSeedBeforeMapRegistration()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        NativeLocalMapHooks.Install();
        f.Native.Game.tickManager = new TickManager();
        AccessTools.Field(typeof(Game), "uniqueIDsManager").SetValue(f.Native.Game, new UniqueIDsManager());
        var generator = new MapGeneratorDef { defName = "Encounter", genSteps = new() };
        var parent = f.Backend.CreateParent(f.Address);
        var frozen = LocalMapSourceResolver.Resolve(parent, new IntVec3(50, 1, 50), generator, false).Value;
        var mutator = new TileMutatorDef { defName = "PFL_TestCaves" };
        var worker = new TileMutatorWorker_Caves(mutator);
        AccessTools.Field(typeof(TileMutatorDef), "mutatorWorker").SetValue(mutator, worker);
        f.Root[0].AddMutator(mutator);
        ulong seed = CelestialSeed.Derive(MapGenerationSeeds.Root(frozen.Source), "local-map", "native-host", "Mutator.Init:" + worker.GetType().FullName);
        Rand.PushState(NativeLocalMapSession.Fold(seed)); int perlinSeed = Rand.Int; Rand.PopState();
        var expected = new Verse.Noise.Perlin(0.002050000010058284, 2.0, 0.5, 4, perlinSeed, Verse.Noise.QualityMode.Medium).GetValue(31, 0, 47);
        var patch = new Harmony("PFL.Tests.CavesEarlySeed");
        patch.Patch(AccessTools.Method(typeof(Map), nameof(Map.ConstructComponents)), prefix: new(typeof(NativeLocalMapExecutionTests), nameof(Skip)));
        patch.Patch(AccessTools.Method(typeof(TileMutatorWorker_Caves), nameof(TileMutatorWorker_Caves.Init)), postfix: new(typeof(NativeLocalMapExecutionTests), nameof(CaptureCaveAndAbort)));
        try
        {
            var failure = Assert.ThrowsAny<Exception>(() => MapGenerator.GenerateMap(new(50, 1, 50), parent, generator));
            Assert.Contains("cave-init-probe", failure.ToString());
            Assert.Equal(expected, caveNoise);
            Assert.False(NativeLocalMapHooks.IsActive);
        }
        finally { patch.UnpatchAll(patch.Id); }
    }
    [Fact]
    public void ActualNativeGenerateMapUsesStableRootBeforeConstructComponentsAndRestoresFailureState()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        NativeLocalMapHooks.Install();
        f.Native.Game.tickManager = new TickManager();
        AccessTools.Field(typeof(Game), "uniqueIDsManager").SetValue(f.Native.Game, new UniqueIDsManager());
        var generator = new MapGeneratorDef { defName = "Encounter", genSteps = new() };
        var parent = f.Backend.CreateParent(f.Address);
        var frozen = LocalMapSourceResolver.Resolve(parent, new IntVec3(50, 1, 50), generator, false).Value;
        Rand.PushState(NativeLocalMapSession.Fold(MapGenerationSeeds.Root(frozen.Source))); int expected = Rand.Int; Rand.PopState();
        var h = new Harmony("PFL.Tests.NativeEarlySeed");
        h.Patch(AccessTools.Method(typeof(Map), nameof(Map.ConstructComponents)), prefix: new(typeof(NativeLocalMapExecutionTests), nameof(CaptureAndAbort)));
        Rand.PushState(581); int outer = Rand.Int; Rand.PopState(); Rand.PushState(581);
        try
        {
            capturedSeed = 0;
            var exception = Assert.ThrowsAny<Exception>(() => MapGenerator.GenerateMap(new(50, 1, 50), parent, generator));
            Assert.Contains("early-seed-probe", exception.ToString());
            var patches = Harmony.GetPatchInfo(CelestialNativeCompatibility.RequireMethod("MapGenerator.GenerateMap"));
            Assert.True(expected == capturedSeed, $"early seed: expected {expected}, actual {capturedSeed}; prefixes={string.Join(',', patches.Prefixes.Select(p => p.owner))}; transpilers={string.Join(',', patches.Transpilers.Select(p => p.owner))}; finalizers={string.Join(',', patches.Finalizers.Select(p => p.owner))}");
            Assert.True(outer == Rand.Int, "outer Rand state was not restored");
            Assert.False(NativeLocalMapHooks.IsActive); Assert.Null(MapGenerator.mapBeingGenerated);
            var failed = Assert.Single(f.Service.CaptureSites());
            Assert.NotNull(failed.Failure); Assert.Null(failed.MapId); Assert.NotNull(failed.Generation);
            Assert.Equal(MapGenerationState.Failed, failed.Generation.State);
            Assert.Equal(parent.ID, failed.MapParentId);
            Assert.Contains(parent, f.Native.World.worldObjects.MapParents);
            Assert.Throws<InvalidOperationException>(() => MapGenerator.GenerateMap(new(50, 1, 50), parent, generator));
            var saved = new CelestialSaveAdapter(f.Scope.Runtime).CaptureCheckpoint(); Assert.True(saved.Success, string.Join(",", saved.Errors));
            var encoded = CelestialSaveCodec.Encode(saved.Value); Assert.True(encoded.Success, string.Join(",", encoded.Errors));
            var decoded = CelestialSaveCodec.Decode(encoded.Value); Assert.True(decoded.Success, string.Join(",", decoded.Errors));
            Assert.Equal(failed.Generation.RecipeHash, Assert.Single(decoded.Value.Maps).Generation.RecipeHash);
        }
        finally { h.UnpatchAll(h.Id); Rand.PopState(); }
    }
    [Fact]
    public void ActualNativeGenerateMapFailureAfterRegistrationRemovesPartialMapAndKeepsFailedProvenance()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        NativeLocalMapHooks.Install();
        f.Native.Game.tickManager = new TickManager();
        AccessTools.Field(typeof(Game), "uniqueIDsManager").SetValue(f.Native.Game, new UniqueIDsManager());
        var generator = new MapGeneratorDef { defName = "Encounter", genSteps = new() };
        var parent = f.Backend.CreateParent(f.Address);
        // Headless: no mutator workers may run before the injected AddMap failure (their Init is unrelated to P1-1).
        AccessTools.Field(typeof(Tile), "mutatorsNullable").SetValue(f.Root[0], null);
        var h = new Harmony("PFL.Tests.LocalMapFailureCleanup");
        h.Patch(AccessTools.Method(typeof(Map), nameof(Map.ConstructComponents)), prefix: new(typeof(NativeLocalMapExecutionTests), nameof(Skip)));
        h.Patch(AccessTools.Method(typeof(Game), nameof(Game.AddMap)), prefix: new(typeof(NativeLocalMapExecutionTests), nameof(RegisterMapThenFail)));
        registeredMap = null;
        var previousExpected = CelestialXmlTestScope.ExpectedError;
        // P1-1 cleanup deinits the registered map; its native diagnostics are tolerated only inside this window.
        CelestialXmlTestScope.ExpectedError = _ => true;
        try
        {
            var exception = Assert.ThrowsAny<Exception>(() => MapGenerator.GenerateMap(new(50, 1, 50), parent, generator));
            Assert.Contains("addmap-probe", exception.ToString());
            Assert.NotNull(registeredMap);
            // P1-1: the registered partial map is removed; the parent stays for the failed identity.
            Assert.DoesNotContain(registeredMap, f.Native.Game.Maps);
            Assert.Null(parent.Map);
            Assert.Contains(parent, f.Native.World.worldObjects.MapParents);
            Assert.False(NativeLocalMapHooks.IsActive);
            var failed = Assert.Single(f.Service.CaptureSites());
            Assert.NotNull(failed.Failure);
            Assert.Null(failed.MapId);
            Assert.NotNull(failed.Generation);
            Assert.Equal(MapGenerationState.Failed, failed.Generation.State);
            Assert.Equal(parent.ID, failed.MapParentId);
            Assert.Equal(f.Address.TileId, failed.Generation.Source.TileId);
            // Reuse is rejected by the preserved failed identity, not by an unknown orphan map.
            Assert.Contains("MapGenerationIncomplete",
                Assert.Throws<InvalidOperationException>(() => MapGenerator.GenerateMap(new(50, 1, 50), parent, generator)).Message);
            var saved = new CelestialSaveAdapter(f.Scope.Runtime).CaptureCheckpoint();
            Assert.True(saved.Success, string.Join(",", saved.Errors));
            var encoded = CelestialSaveCodec.Encode(saved.Value);
            Assert.True(encoded.Success, string.Join(",", encoded.Errors));
            var decoded = CelestialSaveCodec.Decode(encoded.Value);
            Assert.True(decoded.Success, string.Join(",", decoded.Errors));
            var savedSite = Assert.Single(decoded.Value.Maps);
            Assert.Null(savedSite.MapId);
            Assert.NotNull(savedSite.Failure);
            Assert.NotNull(savedSite.Generation);
            Assert.Equal(MapGenerationState.Failed, savedSite.Generation.State);
            Assert.Equal(failed.Generation.RecipeHash, savedSite.Generation.RecipeHash);
        }
        finally { CelestialXmlTestScope.ExpectedError = previousExpected; h.UnpatchAll(h.Id); registeredMap = null; }
    }
    [Fact]
    public void MapCleanupFailureKeepsOriginalFailureAndLeavesMapRegistered()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var parent = f.Backend.CreateParent(f.Address);
        var map = f.Backend.Generate(parent, 50, 50, null);
        map.info.Size = new(50, 1, 50);
        var generator = new MapGeneratorDef { defName = "Encounter" };
        var frozen = LocalMapSourceResolver.Resolve(parent, map.info.Size, generator, false).Value;
        var session = new NativeLocalMapSession(frozen, MapGenerationPurpose.Encounter, parent) { Map = map };
        var patch = new Harmony("PFL.Tests.LocalMapCleanupFailure");
        patch.Patch(AccessTools.Method(typeof(Game), nameof(Game.DeinitAndRemoveMap)), prefix: new(typeof(NativeLocalMapExecutionTests), nameof(FailMapRemoval)));
        session.Activate();
        try
        {
            var original = new InvalidOperationException("original-probe");
            // The original exception is preserved; the cleanup diagnostic is appended, never substituted.
            Assert.Same(original, NativeLocalMapHooks.End(original, map, session));
            Assert.Contains("original-probe", session.Error);
            Assert.Contains("remove-probe", session.Error);
            Assert.Contains(map, f.Native.Game.Maps);
            var site = Assert.Single(f.Service.CaptureSites());
            Assert.NotNull(site.Failure);
            Assert.Equal(map.uniqueID, site.MapId);
            Assert.Equal(MapGenerationState.Failed, site.Generation.State);
        }
        finally { patch.UnpatchAll(patch.Id); session.Release(); }
    }
    [Fact]
    public void ActualNativeExtraInitFailureAfterRegistrationRemovesPartialMap()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        NativeLocalMapHooks.Install();
        f.Native.Game.tickManager = new TickManager();
        AccessTools.Field(typeof(Game), "uniqueIDsManager").SetValue(f.Native.Game, new UniqueIDsManager());
        var generator = new MapGeneratorDef { defName = "Encounter", genSteps = new() };
        var parent = f.Backend.CreateParent(f.Address);
        // No mutator workers before the injected failure (their Init is unrelated to the cleanup path).
        AccessTools.Field(typeof(Tile), "mutatorsNullable").SetValue(f.Root[0], null);
        var h = new Harmony("PFL.Tests.LocalMapExtraInitCleanup");
        h.Patch(AccessTools.Method(typeof(Map), nameof(Map.ConstructComponents)), prefix: new(typeof(NativeLocalMapExecutionTests), nameof(Skip)));
        h.Patch(AccessTools.Method(typeof(Game), nameof(Game.AddMap)), prefix: new(typeof(NativeLocalMapExecutionTests), nameof(RegisterMapOnly)));
        finalizedMap = null;
        var previousExpected = CelestialXmlTestScope.ExpectedError;
        // Cleanup deinits the partial map; native diagnostics are tolerated only inside this window.
        CelestialXmlTestScope.ExpectedError = _ => true;
        try
        {
            // The failure is raised through the native extraInit callback, i.e. a PFL-out-sourced host window,
            // after Game.AddMap registered the map. This is the end-to-end path the AddMap-prefix test cannot cover.
            var exception = Assert.ThrowsAny<Exception>(() => MapGenerator.GenerateMap(new(50, 1, 50), parent, generator,
                extraInitBeforeContentGen: _ => throw new InvalidOperationException("extra-init-probe")));
            Assert.Contains("extra-init-probe", exception.ToString());
            Assert.NotNull(finalizedMap);
            Assert.DoesNotContain(finalizedMap, f.Native.Game.Maps);
            Assert.Null(parent.Map);
            Assert.Contains(parent, f.Native.World.worldObjects.MapParents);
            Assert.False(NativeLocalMapHooks.IsActive);
            var failed = Assert.Single(f.Service.CaptureSites());
            Assert.NotNull(failed.Failure);
            Assert.Null(failed.MapId);
            Assert.NotNull(failed.Generation);
            Assert.Equal(MapGenerationState.Failed, failed.Generation.State);
            Assert.Equal(parent.ID, failed.MapParentId);
            Assert.Contains("MapGenerationIncomplete",
                Assert.Throws<InvalidOperationException>(() => MapGenerator.GenerateMap(new(50, 1, 50), parent, generator)).Message);
        }
        finally { CelestialXmlTestScope.ExpectedError = previousExpected; h.UnpatchAll(h.Id); finalizedMap = null; }
    }
    private sealed class Contributor(NativeMapPlanContribution contribution) : IMapGenerationModule, INativeMapPlanContributor
    { public void Execute(MapGenerationContext context) { } public NativeMapPlanContribution Contribute(MapSourceContext source) => contribution; }
    private sealed class Services(INativeMapGenerationServices native) : IServiceProvider
    { public object GetService(Type type) => type == typeof(INativeMapGenerationServices) ? native : null; }
    private sealed class CountingStep : GenStep
    {
        public int Generated, Posted; public Map Target; public string Failure;
        public override int SeedPart => 17;
        public override void Generate(Map map, GenStepParams parms) { Generated++; Target = map; _ = Rand.Int; if (Failure == "throw") throw new InvalidOperationException("native test failure"); if (Failure == "log") Log.Error("native swallowed failure"); }
        public override void PostMapInitialized(Map map, GenStepParams parms) { Assert.Same(Target, map); Posted++; }
    }
}
