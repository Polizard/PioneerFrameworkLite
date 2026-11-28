using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using HarmonyLib;
using PioneerFramework.GroundAssembly;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public sealed class RocketAssemblyMaterialTests
{
    private static readonly ThingDef Steel = new() { defName = "Steel", category = ThingCategory.Item };
    private static readonly ThingDef Component = new() { defName = "ComponentIndustrial", category = ThingCategory.Item };

    [Fact]
    public void QuoteCountsEveryParallelAndRadialPartAndMergesTheSameMaterial()
    {
        var engine = Part("Engine", new(Steel, 10), new(Component, 2));
        var tank = Part("Tank", new ThingDefCountClass(Steel, 30));
        var design = new RocketDesignSpec();
        design.parts.Add(new() { defName = "Engine", quantity = 2, parallelCount = 3 });
        design.parts.Add(new() { defName = "Engine", quantity = 1, useExplicitPose = true, blockId = 1 });
        design.parts.Add(new() { defName = "Tank", quantity = 1 });

        Assert.True(RocketAssemblyMaterials.TryQuote(design, out var materials, out var reason,
            name => name == "Engine" ? engine : tank), reason);
        Assert.Collection(materials,
            row => { Assert.Same(Component, row.thingDef); Assert.Equal(14, row.count); },
            row => { Assert.Same(Steel, row.thingDef); Assert.Equal(100, row.count); });
    }

    [Fact]
    public void QuoteChecksBoundsBeforeChargingForPartsThatCannotBeEmitted()
    {
        var part = Part("Tank", new ThingDefCountClass(Steel, 1));
        var design = new RocketDesignSpec();
        design.parts.Add(new() { defName = "Tank", quantity = 256 });

        Assert.True(RocketAssemblyMaterials.TryQuote(design, out var materials, out _, _ => part));
        Assert.Equal(256, Assert.Single(materials).count);
        design.parts[0].quantity = 257;
        Assert.False(RocketAssemblyMaterials.TryQuote(design, out materials, out _, _ => part));
        Assert.Empty(materials);
        design.parts[0].quantity = int.MaxValue;
        design.parts[0].parallelCount = 7;
        Assert.False(RocketAssemblyMaterials.TryQuote(design, out _, out _, _ => part));
    }

    [Fact]
    public void QuoteHonorsTheFallbackAndRejectsMissingPartsAndInvalidPrices()
    {
        var design = new RocketDesignSpec();
        design.parts.Add(new() { defName = "ThirdParty", quantity = 1 });
        var fallback = new ShipPartDef { defName = "ThirdParty" };
        Assert.True(RocketAssemblyMaterials.TryQuote(design, out var materials, out _, _ => fallback,
            name => name == "Steel" ? Steel : Component));
        Assert.Equal(2, materials[0].count);
        Assert.Equal(60, materials[1].count);
        Assert.False(RocketAssemblyMaterials.TryQuote(design, out _, out var reason, _ => null));
        Assert.Contains("ThirdParty", reason);
        Assert.False(RocketAssemblyMaterials.TryQuote(design, out _, out _, _ => Part("ThirdParty")));
        var disposable = new ThingDef { defName = "DisappearsOnDrop", category = ThingCategory.Item, destroyOnDrop = true };
        Assert.False(RocketAssemblyMaterials.TryQuote(design, out _, out _,
            _ => Part("ThirdParty", new ThingDefCountClass(disposable, 1))));
        design.parts[0].quantity = 2;
        Assert.False(RocketAssemblyMaterials.TryQuote(design, out _, out _,
            _ => Part("ThirdParty", new ThingDefCountClass(Steel, int.MaxValue))));
    }

    [Fact]
    public void LastPhysicalComponentEnablesAssemblyAndCompletionConsumesOnlyOnce()
    {
        using var catalog = new RocketAssemblyTestCatalog();
        var comp = OpenOrder(catalog);
        Assert.False(comp.HasAssemblyMaterials);
        catalog.EnsureThingOwnerHost();

        var steel = new Thing { def = catalog.Steel, stackCount = 180 };
        var components = new Thing { def = catalog.Component, stackCount = 5 };
        Assert.True(comp.GetDirectlyHeldThings().TryAdd(steel, false));
        Assert.True(comp.GetDirectlyHeldThings().TryAdd(components, false));
        comp.ForceComplete();
        Assert.Equal(AssemblerState.Building, comp.State);
        Assert.Equal(0f, comp.WorkDone);

        Assert.True(comp.GetDirectlyHeldThings().TryAdd(new Thing { def = catalog.Component, stackCount = 1 }, false));
        Assert.True(comp.HasAssemblyMaterials);
        comp.ForceComplete();
        Assert.Equal(AssemblerState.Complete, comp.State);
        Assert.Empty(comp.GetDirectlyHeldThings());
        Assert.True(steel.Destroyed);
        Assert.True(components.Destroyed);
        comp.ForceComplete();
        Assert.Equal(AssemblerState.Complete, comp.State);
    }

    [Fact]
    public void PauseResumeAndReplacementInvalidatePreviouslyIssuedJobs()
    {
        using var catalog = new RocketAssemblyTestCatalog();
        var comp = OpenOrder(catalog);
        int original = comp.BuildOrderVersion;
        Assert.True(comp.PauseBuild());
        Assert.True(comp.IsBuildPaused);
        Assert.NotEqual(original, comp.BuildOrderVersion);
        Assert.True(comp.ResumeBuild());
        Assert.False(comp.IsBuildPaused);
        int resumed = comp.BuildOrderVersion;
        comp.CancelBuild();
        Assert.True(comp.StartBuild(out string reason), reason);
        Assert.NotEqual(resumed, comp.BuildOrderVersion);
        Assert.False(comp.HasAssemblyMaterials);
    }

    [Fact]
    public void OldUnpaidBuildRetainsWorkAndPricesOnceOnLoad()
    {
        using var catalog = new RocketAssemblyTestCatalog();
        var original = OpenOrder(catalog);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
        try
        {
            var design = original.Design;
            var state = AssemblerState.Building;
            var work = 25f;
            var total = 100f;
            Scribe.saver.InitSaving(path, "assembler");
            Scribe_Deep.Look(ref design, "design");
            Scribe_Values.Look(ref state, "assemblerState");
            Scribe_Values.Look(ref work, "assemblerWorkDone");
            Scribe_Values.Look(ref total, "assemblerWorkRequired");
            Scribe.saver.FinalizeSaving();

            var loaded = new CompRocketAssembler();
            Scribe.loader.InitLoading(path);
            Scribe.loader.initer.RegisterForPostLoadInit(new CompLoad(loaded));
            loaded.PostExposeData();
            Scribe.loader.FinalizeLoading();

            Assert.Equal(25f, loaded.WorkDone);
            Assert.Equal(100f, loaded.WorkRequired);
            Assert.False(loaded.HasAssemblyMaterials);
            Assert.Equal(180, loaded.MaterialRows().Single(m => m.DefName == "Steel").Required);
        }
        finally
        {
            Scribe.ForceStop();
            File.Delete(path);
        }
    }

    [Fact]
    public void PausedOrderKeepsPhysicalStockAndOriginalQuoteAfterARecipeChange()
    {
        using var catalog = new RocketAssemblyTestCatalog();
        var comp = OpenOrder(catalog);
        catalog.EnsureThingOwnerHost();
        var steel = new Thing { def = catalog.Steel, stackCount = 45, thingIDNumber = 173713 };
        Assert.True(comp.GetDirectlyHeldThings().TryAdd(steel, false));
        Assert.True(comp.PauseBuild());
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
        try
        {
            Scribe.saver.InitSaving(path, "assembler");
            comp.PostExposeData();
            Scribe.saver.FinalizeSaving();
            DefDatabase<ShipPartDef>.GetNamed("MaterialTestTank").assemblyCost = new()
            {
                new ThingDefCountClass(catalog.Steel, 1000),
            };

            var loaded = new CompRocketAssembler();
            Scribe.loader.InitLoading(path);
            Scribe.loader.initer.RegisterForPostLoadInit(new CompLoad(loaded));
            loaded.PostExposeData();
            Scribe.loader.FinalizeLoading();

            Assert.True(loaded.IsBuildPaused);
            Assert.Equal(comp.BuildOrderVersion, loaded.BuildOrderVersion);
            var row = loaded.MaterialRows().Single(m => m.DefName == "Steel");
            Assert.Equal(180, row.Required);
            Assert.Equal(45, row.Delivered);
            Assert.Equal(45, Assert.Single(loaded.GetDirectlyHeldThings()).stackCount);
            loaded.CancelBuild();
            Assert.True(loaded.IsRefundPending);
            Assert.Equal(45, Assert.Single(loaded.GetDirectlyHeldThings()).stackCount);
        }
        finally
        {
            Scribe.ForceStop();
            File.Delete(path);
        }
    }

    [Fact]
    public void ShippedRecipesQuoteTheSmallRocketAndEveryR7Booster()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Defs", "ShipParts");
        var parts = Directory.GetFiles(directory, "ShipParts*.xml")
            .SelectMany(file => XDocument.Load(file).Root.Elements("ShipPartDef"))
            .Select(node => new ShipPartDef
            {
                defName = (string)node.Element("defName"),
                category = (string)node.Element("category"),
                massKg = (float)node.Element("massKg"),
                boundsX = (float?)node.Element("boundsX") ?? 2,
                boundsY = (float?)node.Element("boundsY") ?? 2,
                stackLengthMeters = (float?)node.Element("stackLengthMeters") ?? 0,
                assemblyCost = node.Element("assemblyCost").Elements().Select(line =>
                    new ThingDefCountClass(line.Name.LocalName switch
                    {
                        "Steel" => Steel,
                        "ComponentIndustrial" => Component,
                        _ => throw new InvalidDataException(line.Name.LocalName),
                    }, (int)line)).ToList(),
            }).ToDictionary(part => part.defName);
        Assert.Equal(21, parts.Count);
        Assert.All(parts.Values, part => Assert.All(part.assemblyCost,
            material => Assert.True(ShipPartDef.IsValidAssemblyMaterial(material))));

        var small = new RocketDesignSpec();
        small.parts.AddRange(new[] { "Tank_Debug", "ControlCore_Debug", "Thruster_Debug" }.Select(name =>
            new DesignPartSpec { defName = "PF_ShipPart_" + name }));
        Assert.True(RocketAssemblyMaterials.TryQuote(small, out var quote, out var reason,
            name => parts.GetValueOrDefault(name)), reason);
        Assert.Equal(new[] { 8, 240 }, quote.Select(material => material.count));

        var r7 = CompRocketAssembler.BuildR7TestDesign(name => parts.GetValueOrDefault(name), out reason);
        Assert.True(r7 != null, reason);
        Assert.True(RocketAssemblyMaterials.TryQuote(r7, out quote, out reason,
            name => parts.GetValueOrDefault(name)), reason);
        Assert.Equal(new[] { 61, 2590 }, quote.Select(material => material.count));
    }

    [Fact]
    public void ExistingOrderWithARemovedPartRetainsMaterialsInACancellableErrorState()
    {
        using var catalog = new RocketAssemblyTestCatalog();
        var comp = OpenOrder(catalog);
        catalog.EnsureThingOwnerHost();
        Assert.True(comp.GetDirectlyHeldThings().TryAdd(new Thing
        {
            def = catalog.Steel,
            stackCount = 12,
            thingIDNumber = 173714,
        }, false));
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
        try
        {
            Scribe.saver.InitSaving(path, "assembler");
            comp.PostExposeData();
            Scribe.saver.FinalizeSaving();
            var kept = DefDatabase<ShipPartDef>.AllDefsListForReading
                .Where(part => part.defName != "MaterialTestTank").ToArray();
            DefDatabase<ShipPartDef>.Clear();
            DefDatabase<ShipPartDef>.Add(kept);

            var loaded = new CompRocketAssembler();
            Scribe.loader.InitLoading(path);
            Scribe.loader.initer.RegisterForPostLoadInit(new CompLoad(loaded));
            loaded.PostExposeData();
            Scribe.loader.FinalizeLoading();

            Assert.Contains("MaterialTestTank", loaded.MaterialOrderError);
            Assert.False(loaded.IsOpenBuildOrder(loaded.BuildOrderVersion));
            Assert.Equal(12, Assert.Single(loaded.GetDirectlyHeldThings()).stackCount);
            loaded.CancelBuild();
            Assert.True(loaded.IsRefundPending);
        }
        finally
        {
            Scribe.ForceStop();
            File.Delete(path);
        }
    }

    private static CompRocketAssembler OpenOrder(RocketAssemblyTestCatalog catalog)
    {
        var comp = new CompRocketAssembler();
        comp.AddPart(new ShipPartDef { defName = "MaterialTestTank", category = "tank" });
        comp.AddPart(new ShipPartDef { defName = "MaterialTestControl", category = "control" });
        comp.AddPart(new ShipPartDef { defName = "MaterialTestEngine", category = "thruster" });
        Assert.True(catalog.StartBuild(comp, out string reason), reason);
        return comp;
    }

    private sealed class CompLoad(CompRocketAssembler comp) : IExposable
    {
        public void ExposeData() => comp.PostExposeData();
    }

    [Fact]
    public void AssemblySpeedScalesWithActualWorkTicksWithoutBatchDrift()
    {
        Assert.Equal(1d, CompRocketAssembler.AssemblyWorkForTicks(60, 1, 1), 9);
        Assert.Equal(2d, CompRocketAssembler.AssemblyWorkForTicks(60, 1, 2), 9);
        double ticked = Enumerable.Range(0, 60).Sum(_ => CompRocketAssembler.AssemblyWorkForTicks(1, 1, 2));
        Assert.Equal(2d, ticked, 9);
        Assert.Equal(0d, CompRocketAssembler.AssemblyWorkForTicks(0, 1, 1));
        Assert.Equal(0d, CompRocketAssembler.AssemblyWorkForTicks(60, 1, float.PositiveInfinity));
    }

    private static ShipPartDef Part(string name, params ThingDefCountClass[] cost) => new()
    {
        defName = name,
        assemblyCost = new List<ThingDefCountClass>(cost),
    };
}

internal sealed class RocketAssemblyTestCatalog : IDisposable
{
    private readonly ShipPartDef[] previousParts = DefDatabase<ShipPartDef>.AllDefsListForReading.ToArray();
    private readonly ThingDef[] previousThings = DefDatabase<ThingDef>.AllDefsListForReading.ToArray();
    private readonly FieldInfo prefs = AccessTools.Field(typeof(Prefs), "data");
    private readonly object previousPrefs;
    private readonly Game previousGame = Current.Game;

    public ThingDef Steel { get; } = RegisterItem("Steel");
    public ThingDef Component { get; } = RegisterItem("ComponentIndustrial");

    public RocketAssemblyTestCatalog()
    {
        previousPrefs = prefs.GetValue(null);
        if (previousPrefs == null) prefs.SetValue(null, RuntimeHelpers.GetUninitializedObject(prefs.FieldType));
    }

    public void EnsureThingOwnerHost()
    {
        if (Current.Game == null)
        {
            var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            AccessTools.Field(typeof(Game), "maps").SetValue(game, new List<Map>());
            game.components = new List<GameComponent>();
            Current.Game = game;
        }
    }

    private static ThingDef RegisterItem(string name)
    {
        var def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
        if (def != null) return def;
        def = new ThingDef { defName = name, category = ThingCategory.Item, stackLimit = 75 };
        DefDatabase<ThingDef>.Add(def);
        return def;
    }

    public bool StartBuild(CompRocketAssembler comp, out string reason)
    {
        foreach (var row in comp.Design.parts)
        {
            if (DefDatabase<ShipPartDef>.GetNamedSilentFail(row.defName) == null)
                DefDatabase<ShipPartDef>.Add(new ShipPartDef { defName = row.defName, category = row.category });
        }
        return comp.StartBuild(out reason);
    }

    public void DeliverAll(CompRocketAssembler comp)
    {
        EnsureThingOwnerHost();
        foreach (var material in comp.MaterialRows())
        {
            Assert.NotNull(material.Def);
            Assert.True(comp.GetDirectlyHeldThings().TryAdd(new Thing
            {
                def = material.Def,
                stackCount = material.Required,
            }, false));
        }
    }

    public void Dispose()
    {
        Current.Game = previousGame;
        prefs.SetValue(null, previousPrefs);
        DefDatabase<ShipPartDef>.Clear();
        DefDatabase<ShipPartDef>.Add(previousParts);
        DefDatabase<ThingDef>.Clear();
        DefDatabase<ThingDef>.Add(previousThings);
    }
}
