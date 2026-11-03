using PioneerFramework.Celestial;
using Xunit;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
namespace PioneerFramework.Tests;
public class CelestialPlanetRoomTests
{
    [Fact]
    public void NativePoweredToxicRoomBecomesSafeDestinationThenPowerLossAndRoofBreachBecomeDangerous()
    {
        using var f = new CelestialPlanetNativeEnvironmentTests.Fixture(null, false, new CelestialExposureXml { toxicityPerDay = 2 });
        var map = f.Map; if (!f.Native.Game.Maps.Contains(map)) f.Native.Game.Maps.Add(map);
        f.Native.AddDefs(new RoomStatDef { defName = "PF_FilterRoomStat" });
        map.cellIndices = new CellIndices(map); map.regionGrid = new RegionGrid(map); map.roofGrid = new RoofGrid(map);
        map.edificeGrid = new EdificeGrid(map); map.regionAndRoomUpdater = new RegionAndRoomUpdater(map);
        map.regionDirtyer = new RegionDirtyer(map); map.regionAndRoomUpdater.Enabled = false; map.mapPawns = new MapPawns(map);
        var region = (Region)Activator.CreateInstance(typeof(Region), true);
        region.mapIndex = 0; region.type = RegionType.Normal; region.extentsClose = new CellRect(1, 1, 2, 2); region.valid = true;
        var district = new District { mapIndex = 0 }; region.District = district; map.mapTemperature = new MapTemperature(map);
        var setup = new Harmony("PFL.Tests.SafeFilterRoom");
        setup.Patch(AccessTools.PropertyGetter(typeof(MapTemperature), nameof(MapTemperature.OutdoorTemp)), prefix: new HarmonyMethod(typeof(CelestialPlanetRoomTests), nameof(SetupOutdoorTemperature)));
        try { district.Room = Room.MakeNew(map); } finally { setup.UnpatchAll(setup.Id); }
        var roofs = (RoofDef[])AccessTools.Field(typeof(RoofGrid), "roofGrid").GetValue(map.roofGrid);
        foreach (var cell in region.extentsClose.Cells) { map.regionGrid.SetRegionAt(cell, region); roofs[map.cellIndices.CellToIndex(cell)] = new RoofDef { defName = "sealed" }; }
        var buildingDef = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef)); buildingDef.defName = "PF_FilterProbe";
        var building = new Building { def = buildingDef, Position = new(1, 0, 1) };
        AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(building, (sbyte)0);
        var power = new CompPowerTrader { parent = building }; power.Initialize(new CompProperties_Power());
        var filter = new CompCelestialLifeSupport { parent = building }; filter.Initialize(new CompProperties_CelestialLifeSupport { filtersToxins = true });
        AccessTools.Field(typeof(ThingWithComps), "comps").SetValue(building, new List<ThingComp> { power, filter }); map.listerThings.AllThings.Add(building);
        AccessTools.Field(typeof(CompPowerTrader), "powerOnInt").SetValue(power, true);
        var component = new MapComponent_CelestialEnvironment(map); map.components = new() { component };
        var pawn = CelestialPlanetExposureTests.BiologicalPawn(); pawn.Position = new(1, 0, 1);
        AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(pawn, (sbyte)0);
        var oldToxic = StatDefOf.ToxicResistance; var oldEnvironment = StatDefOf.ToxicEnvironmentResistance; var oldVacuum = StatDefOf.VacuumResistance;
        StatDefOf.ToxicResistance = new StatDef { defName = "filterToxic", defaultBaseValue = 0, immutable = true };
        StatDefOf.ToxicEnvironmentResistance = new StatDef { defName = "filterEnvironment", defaultBaseValue = 0, immutable = true };
        StatDefOf.VacuumResistance = new StatDef { defName = "filterVacuum", defaultBaseValue = 0, immutable = true };
        StatDefOf.ToxicResistance.Worker.SetCacheability(true); StatDefOf.ToxicEnvironmentResistance.Worker.SetCacheability(true);
        StatDefOf.VacuumResistance.Worker.SetCacheability(true);
        try
        {
            for (int tick = 0; tick <= 600; tick += 60) { f.Native.Game.tickManager.DebugSetTicksGame(tick); component.MapComponentTick(); }
            Assert.Null(component.BlockedReason); Assert.False(component.IsDangerous(pawn, pawn.Position));
            var destination = pawn.Position; bool allowed = true;
            CelestialPlanetEnvironmentHooks.SafeTemperatureDestination(pawn, ref destination, ref allowed);
            Assert.True(allowed); Assert.Equal(pawn.Position, destination);
            AccessTools.Field(typeof(CompPowerTrader), "powerOnInt").SetValue(power, false);
            f.Native.Game.tickManager.DebugSetTicksGame(660); component.MapComponentTick(); Assert.True(component.IsDangerous(pawn, pawn.Position));
            AccessTools.Field(typeof(CompPowerTrader), "powerOnInt").SetValue(power, true);
            f.Native.Game.tickManager.DebugSetTicksGame(720); component.MapComponentTick(); Assert.False(component.IsDangerous(pawn, pawn.Position));
            roofs[map.cellIndices.CellToIndex(pawn.Position)] = null; district.Notify_RoofChanged();
            Assert.True(component.IsDangerous(pawn, pawn.Position));
            destination = pawn.Position; allowed = true;
            CelestialPlanetEnvironmentHooks.SafeTemperatureDestination(pawn, ref destination, ref allowed);
            Assert.False(allowed); Assert.Equal(IntVec3.Invalid, destination);
        }
        finally { StatDefOf.ToxicResistance = oldToxic; StatDefOf.ToxicEnvironmentResistance = oldEnvironment; StatDefOf.VacuumResistance = oldVacuum; }
    }
    private static Pawn failingExposurePawn;
    private static bool injectExposureFailure;
    private static readonly Dictionary<Pawn, double> appliedRadiation = new();
    [Fact]
    public void ExplicitRetryRecoversExistingRoomStateWithoutReplayingClaimedDamage()
    {
        using var diagnostics = new CelestialExpectedRestoreErrorScope();
        using var f = new CelestialPlanetNativeEnvironmentTests.Fixture(null, false, new CelestialExposureXml { radiationPerDay = 1 });
        var map = f.Map;
        map.cellIndices = new CellIndices(map); map.regionGrid = new RegionGrid(map); map.roofGrid = new RoofGrid(map);
        map.edificeGrid = new EdificeGrid(map); map.regionAndRoomUpdater = new RegionAndRoomUpdater(map);
        map.regionDirtyer = new RegionDirtyer(map); map.regionAndRoomUpdater.Enabled = false;
        map.mapPawns = new MapPawns(map);
        var first = CelestialPlanetExposureTests.BiologicalPawn(); var second = CelestialPlanetExposureTests.BiologicalPawn();
        foreach (var pawn in new[] { first, second })
        {
            pawn.Position = new IntVec3(1, 0, 1);
            AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(pawn, (sbyte)0);
        }
        ((List<Pawn>)AccessTools.Field(typeof(MapPawns), "pawnsSpawned").GetValue(map.mapPawns)).AddRange(new[] { first, second });
        var state = new CelestialRoomEnvironmentState(f.Planet.Value);
        state.Advance(0, Array.Empty<CelestialRoomTopology>(), new(null, null, 0, 1));
        var environment = new MapComponent_CelestialEnvironment(map); map.components = new() { environment };
        AccessTools.Field(typeof(MapComponent_CelestialEnvironment), "state").SetValue(environment, state);
        var payload = AccessTools.Field(typeof(MapComponent_CelestialEnvironment), "savedPayload");
        string raw = System.Text.Encoding.UTF8.GetString(state.Serialize()); payload.SetValue(environment, raw);
        AccessTools.Field(typeof(MapComponent_CelestialEnvironment), "savedOwner").SetValue(environment, f.Planet.Value);
        var oldToxic = StatDefOf.ToxicResistance; var oldEnvironment = StatDefOf.ToxicEnvironmentResistance; var oldVacuum = StatDefOf.VacuumResistance;
        var patch = new Harmony("PFL.Tests.EnvironmentRetryInterval");
        patch.Patch(AccessTools.Method(typeof(CelestialPawnEnvironment), nameof(CelestialPawnEnvironment.Apply)),
            prefix: new HarmonyMethod(typeof(CelestialPlanetRoomTests), nameof(ObserveExposure)));
        try
        {
            StatDefOf.ToxicResistance = new StatDef { defName = "PF_RetryToxic", immutable = true };
            StatDefOf.ToxicEnvironmentResistance = new StatDef { defName = "PF_RetryEnvironment", immutable = true };
            StatDefOf.VacuumResistance = new StatDef { defName = "PF_RetryVacuum", immutable = true };
            StatDefOf.ToxicResistance.Worker.SetCacheability(true); StatDefOf.ToxicEnvironmentResistance.Worker.SetCacheability(true); StatDefOf.VacuumResistance.Worker.SetCacheability(true);
            appliedRadiation.Clear(); failingExposurePawn = second; injectExposureFailure = true;
            f.Native.Game.tickManager.DebugSetTicksGame(60); environment.MapComponentTick();
            Assert.StartsWith("RoomEnvironmentUpdateFailed", environment.BlockedReason);
            Assert.Equal(60, state.LastGameTick); Assert.Equal(60, AccessTools.Field(typeof(MapComponent_CelestialEnvironment), "lastProcessedTick").GetValue(environment));
            Assert.Equal(.001, appliedRadiation[first]); Assert.False(appliedRadiation.ContainsKey(second));
            byte[] committed = state.Serialize(); var manager = f.Native.Game.GetComponent<CelestialBodyManager>();
            Assert.True(manager.CelestialRestorePending);
            injectExposureFailure = false;
            Assert.True(manager.RetryCelestialRestore().Success);
            Assert.False(manager.CelestialRestorePending); Assert.Empty(manager.CelestialRestoreErrors); Assert.Null(environment.BlockedReason);
            Assert.Equal(committed, state.Serialize()); Assert.Equal(raw, payload.GetValue(environment));
            environment.MapComponentTick(); Assert.Equal(.001, appliedRadiation[first]); Assert.False(appliedRadiation.ContainsKey(second));
            f.Native.Game.tickManager.DebugSetTicksGame(120); environment.MapComponentTick();
            Assert.Equal(.002, appliedRadiation[first]); Assert.Equal(.001, appliedRadiation[second]); Assert.Equal(120, state.LastGameTick);
        }
        finally
        {
            patch.UnpatchAll(patch.Id); StatDefOf.ToxicResistance = oldToxic; StatDefOf.ToxicEnvironmentResistance = oldEnvironment; StatDefOf.VacuumResistance = oldVacuum;
            appliedRadiation.Clear(); failingExposurePawn = null; injectExposureFailure = false;
        }
    }
    private static bool ObserveExposure(Pawn pawn, CelestialExposureDose dose)
    {
        if (injectExposureFailure && pawn == failingExposurePawn) throw new InvalidOperationException("Transient native consumer dependency");
        appliedRadiation[pawn] = appliedRadiation.GetValueOrDefault(pawn) + dose.Radiation;
        return false;
    }
    [Fact]
    public void NativeRoomMembershipRoofDamageAndMapSavePreserveOrBlockState()
    {
        using var diagnostics = new CelestialExpectedRestoreErrorScope();
        using var native = new CelestialNativeWorldTestScope();
        native.AddDefs(new RoomStatDef { defName = "PF_TestRoomStat" });
        var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
        map.info = new MapInfo { Size = new IntVec3(6, 1, 6) };
        native.Game.Maps.Add(map);
        map.cellIndices = new CellIndices(map); map.regionGrid = new RegionGrid(map); map.roofGrid = new RoofGrid(map);
        map.edificeGrid = new EdificeGrid(map); map.listerThings = new ListerThings(ListerThingsUse.Global);
        map.regionAndRoomUpdater = new RegionAndRoomUpdater(map);
        map.regionDirtyer = new RegionDirtyer(map);
        map.regionAndRoomUpdater.Enabled = false;
        var region = (Region)Activator.CreateInstance(typeof(Region), true);
        region.mapIndex = 0; region.type = RegionType.Normal; region.extentsClose = new CellRect(1, 1, 2, 2); region.valid = true;
        var district = new District { mapIndex = 0 };
        region.District = district;
        map.mapTemperature = new MapTemperature(map);
        var temperature = new Harmony("PFL.Tests.RoomTopologyTemperatureSetup");
        temperature.Patch(AccessTools.PropertyGetter(typeof(MapTemperature), nameof(MapTemperature.OutdoorTemp)), prefix:
            new HarmonyMethod(typeof(CelestialPlanetRoomTests), nameof(SetupOutdoorTemperature)));
        Room room;
        try { room = Room.MakeNew(map); } finally { temperature.UnpatchAll(temperature.Id); }
        district.Room = room;
        foreach (var cell in region.extentsClose.Cells) map.regionGrid.SetRegionAt(cell, region);
        var roofs = (RoofDef[])AccessTools.Field(typeof(RoofGrid), "roofGrid").GetValue(map.roofGrid);
        foreach (var cell in region.extentsClose.Cells) roofs[map.cellIndices.CellToIndex(cell)] = new RoofDef { defName = "testRoof" };
        var captured = Assert.Single(MapComponent_CelestialEnvironment.CaptureTopology(map));
        Assert.False(captured.OpenToOutside); Assert.Equal(4, captured.Cells.Length); Assert.Equal(0, captured.ConditioningPerSecond);
        var otherRegion = (Region)Activator.CreateInstance(typeof(Region), true);
        otherRegion.mapIndex = 0; otherRegion.type = RegionType.Normal; otherRegion.extentsClose = new CellRect(4, 1, 1, 2); otherRegion.valid = true;
        var otherDistrict = new District { mapIndex = 0 }; otherRegion.District = otherDistrict;
        temperature.Patch(AccessTools.PropertyGetter(typeof(MapTemperature), nameof(MapTemperature.OutdoorTemp)), prefix:
            new HarmonyMethod(typeof(CelestialPlanetRoomTests), nameof(SetupOutdoorTemperature)));
        try { otherDistrict.Room = Room.MakeNew(map); } finally { temperature.UnpatchAll(temperature.Id); }
        foreach (var cell in otherRegion.extentsClose.Cells) map.regionGrid.SetRegionAt(cell, otherRegion);
        var doorDef = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef)); doorDef.defName = "PF_DoorProbe"; doorDef.size = new IntVec2(1, 1);
        var door = new Building_Door { def = doorDef, Position = new IntVec3(3, 0, 1) };
        map.edificeGrid.Register(door);
        Assert.Equal(2, MapComponent_CelestialEnvironment.CaptureTopology(map).Count);
        AccessTools.Field(typeof(Building_Door), "openInt").SetValue(door, true);
        var connected = Assert.Single(MapComponent_CelestialEnvironment.CaptureTopology(map));
        Assert.True(connected.OpenToOutside); Assert.Equal(6, connected.Cells.Length);
        AccessTools.Field(typeof(Building_Door), "openInt").SetValue(door, false);
        Assert.Equal(2, MapComponent_CelestialEnvironment.CaptureTopology(map).Count);
        // Native room reassignment is how wall demolition/room merging presents to consumers.
        otherDistrict.Room = room;
        Assert.Equal(6, Assert.Single(MapComponent_CelestialEnvironment.CaptureTopology(map)).Cells.Length);
        temperature.Patch(AccessTools.PropertyGetter(typeof(MapTemperature), nameof(MapTemperature.OutdoorTemp)), prefix:
            new HarmonyMethod(typeof(CelestialPlanetRoomTests), nameof(SetupOutdoorTemperature)));
        try { otherDistrict.Room = Room.MakeNew(map); } finally { temperature.UnpatchAll(temperature.Id); }
        Assert.Equal(2, MapComponent_CelestialEnvironment.CaptureTopology(map).Count);
        otherDistrict.Room = null;
        foreach (var cell in otherRegion.extentsClose.Cells) map.regionGrid.SetRegionAt(cell, null);
        map.edificeGrid.DeRegister(door);
        var buildingDef = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef)); buildingDef.defName = "PF_OxygenProbe";
        var building = new Building { def = buildingDef, Position = new IntVec3(1, 0, 1) };
        AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(building, (sbyte)0);
        var power = new CompPowerTrader { parent = building }; power.Initialize(new CompProperties_Power());
        var support = new CompCelestialLifeSupport { parent = building }; support.Initialize(new CompProperties_CelestialLifeSupport {
            suppliesOxygen = true, filtersToxins = false, regulatesPressure = false } );
        var flick = new CompFlickable { parent = building }; flick.Initialize(new CompProperties_Flickable());
        var breakdown = new CompBreakdownable { parent = building }; breakdown.Initialize(new CompProperties_Breakdownable());
        AccessTools.Field(typeof(ThingWithComps), "comps").SetValue(building, new List<ThingComp> { power, support, flick, breakdown });
        map.listerThings.AllThings.Add(building);
        AccessTools.Field(typeof(CompPowerTrader), "powerOnInt").SetValue(power, true);
        var powered = Assert.Single(MapComponent_CelestialEnvironment.CaptureTopology(map));
        Assert.True(powered.OxygenConditioningPerSecond > 0); Assert.Equal(0, powered.PressureConditioningPerSecond); Assert.Equal(0, powered.ToxinConditioningPerSecond);
        AccessTools.Field(typeof(CompFlickable), "switchOnInt").SetValue(flick, false);
        Assert.Equal(0, Assert.Single(MapComponent_CelestialEnvironment.CaptureTopology(map)).ConditioningPerSecond);
        AccessTools.Field(typeof(CompFlickable), "switchOnInt").SetValue(flick, true);
        AccessTools.Field(typeof(CompBreakdownable), "brokenDownInt").SetValue(breakdown, true);
        Assert.Equal(0, Assert.Single(MapComponent_CelestialEnvironment.CaptureTopology(map)).ConditioningPerSecond);
        AccessTools.Field(typeof(CompBreakdownable), "brokenDownInt").SetValue(breakdown, false);
        AccessTools.Field(typeof(CompPowerTrader), "powerOnInt").SetValue(power, false);
        Assert.Equal(0, Assert.Single(MapComponent_CelestialEnvironment.CaptureTopology(map)).ConditioningPerSecond);
        var protectedState = new CelestialRoomEnvironmentState("planet:roof-probe");
        var outside = CelestialPlanetExposureTests.Sample(500000, 0);
        var protectedTopology = captured with { ConditioningPerSecond = 1, SuppliesOxygen = true, RegulatesPressure = true,
            OxygenConditioningPerSecond = 1, PressureConditioningPerSecond = 1 };
        protectedState.Advance(0, new[] { protectedTopology }, new(500000, 0, 1, 1));
        protectedState.Advance(600, new[] { protectedTopology }, new(500000, 0, 1, 1));
        var protectedComponent = new MapComponent_CelestialEnvironment(map);
        AccessTools.Field(typeof(MapComponent_CelestialEnvironment), "state").SetValue(protectedComponent, protectedState);
        AccessTools.Method(typeof(MapComponent_CelestialEnvironment), "Reindex").Invoke(protectedComponent, null);
        Assert.True(protectedComponent.Effective(outside, new(1, 0, 1)).PressurePascals < 300000);
        roofs[map.cellIndices.CellToIndex(new(1, 0, 1))] = null;
        district.Notify_RoofChanged();
        Assert.True(Assert.Single(MapComponent_CelestialEnvironment.CaptureTopology(map)).OpenToOutside);
        Assert.Equal(500000, protectedComponent.Effective(outside, new(1, 0, 1)).PressurePascals);

        var state = new CelestialRoomEnvironmentState("planet:saved");
        state.Advance(120, new[] { captured }, new(500000, 0, 1, 0));
        var component = new MapComponent_CelestialEnvironment(map);
        AccessTools.Field(typeof(MapComponent_CelestialEnvironment), "state").SetValue(component, state);
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
        try
        {
            Scribe.saver.InitSaving(file, "map-environment"); component.ExposeData(); Scribe.saver.FinalizeSaving();
            string saved = File.ReadAllText(file);
            Assert.Contains("planet:saved", saved); Assert.Contains("LastGameTick", saved);
            var loaded = new MapComponent_CelestialEnvironment(map);
            Scribe.loader.InitLoading(file); loaded.ExposeData(); Scribe.loader.FinalizeLoading();
            Assert.False(loaded.EnsureRestored()); Assert.Equal("RoomEnvironmentDependencyUnavailable", loaded.BlockedReason);
            Assert.True(native.Game.GetComponent<CelestialBodyManager>().CelestialRestorePending);
            Assert.Contains(native.Game.GetComponent<CelestialBodyManager>().CelestialRestoreErrors,
                error => error.Code == "RoomEnvironmentDependencyUnavailable");
            map.components = new() { loaded };
            var seekingPawn = CelestialPlanetExposureTests.BiologicalPawn();
            AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(seekingPawn, (sbyte)0);
            var destination = new IntVec3(1, 0, 1); bool allowed = true;
            CelestialPlanetEnvironmentHooks.SafeTemperatureDestination(seekingPawn, ref destination, ref allowed);
            Assert.False(allowed); Assert.Equal(IntVec3.Invalid, destination);
            Assert.Equal(System.Text.Encoding.UTF8.GetString(state.Serialize()), AccessTools.Field(typeof(MapComponent_CelestialEnvironment), "savedPayload").GetValue(loaded));
        }
        finally { Scribe.ForceStop(); if (File.Exists(file)) File.Delete(file); }

        // Exterior rooms reach map edges; native BorderCells includes coordinates outside the map.
        foreach (var cell in region.extentsClose.Cells) map.regionGrid.SetRegionAt(cell, null);
        region.extentsClose = new CellRect(0, 0, 2, 2);
        foreach (var cell in region.extentsClose.Cells) map.regionGrid.SetRegionAt(cell, region);
        district.Notify_RoofChanged();
        Assert.True(Assert.Single(MapComponent_CelestialEnvironment.CaptureTopology(map)).OpenToOutside);
    }
    private static bool SetupOutdoorTemperature(ref float __result) { __result = 20; return false; }
    [Fact]
    public void PoweredSealedRoomConditionsAndOpenDoorOrPowerLossReverts()
    {
        var outdoor = new CelestialRoomAir(500000, 0, 1, .5);
        var sealedRoom = new CelestialRoomTopology(new[] { 1, 2, 3, 4 }, false, 1, true, true, true);
        var state = new CelestialRoomEnvironmentState("planet:a");
        state.Advance(0, new[] { sealedRoom }, outdoor);
        state.Advance(600, new[] { sealedRoom }, outdoor);
        Assert.True(state.Rooms[0].Air.OxygenPascals > 20000);
        Assert.True(state.Rooms[0].Air.PressurePascals < 110000);
        var safe = state.Serialize();
        state.Advance(600, new[] { sealedRoom }, outdoor);
        Assert.Equal(safe, state.Serialize());
        var restored = CelestialRoomEnvironmentState.Deserialize(safe, "planet:a").Value;
        restored.Advance(1200, new[] { sealedRoom with { OpenToOutside = true } }, outdoor);
        Assert.True(restored.Rooms[0].Air.OxygenPascals < 1000);
        state.Advance(7800, new[] { sealedRoom with { ConditioningPerSecond = 0 } }, outdoor);
        Assert.True(state.Rooms[0].Air.OxygenPascals < 10000);
        Assert.False(CelestialRoomEnvironmentState.Deserialize(safe, "planet:b").Success);
    }
    [Fact]
    public void SplitAndMergeConserveMixtureAndRestorePreservesMembership()
    {
        var outdoor = new CelestialRoomAir(500000, 0, 1, 0);
        var state = new CelestialRoomEnvironmentState("planet:a");
        var rooms = new[] { new CelestialRoomTopology(new[] { 1, 2 }, false, 1, true, true, true),
            new CelestialRoomTopology(new[] { 3, 4 }, false, 0, false, false, false) };
        state.Advance(0, rooms, outdoor); state.Advance(600, rooms, outdoor);
        double before = state.Rooms[0].Air.OxygenPascals!.Value / 2;
        state.Advance(601, new[] { new CelestialRoomTopology(new[] { 1, 2, 3, 4 }, false, 0, false, false, false) }, outdoor);
        Assert.InRange(state.Rooms[0].Air.OxygenPascals!.Value, before * .999, before);
        var restored = CelestialRoomEnvironmentState.Deserialize(state.Serialize(), "planet:a").Value;
        restored.Advance(602, rooms, outdoor);
        Assert.Equal(4, restored.Rooms[0].Cells.Length + restored.Rooms[1].Cells.Length);
        Assert.Equal(state.Serialize(), CelestialRoomEnvironmentState.Deserialize(state.Serialize(), "planet:a").Value.Serialize());
    }
    [Fact]
    public void SupplementalRoomStateHasAVersionedCodecIndependentOfNativeVacuum()
    {
        var type = typeof(CelestialEnvironmentProfile).Assembly.GetType("PioneerFramework.Celestial.CelestialRoomEnvironmentState");
        Assert.NotNull(type);
        Assert.NotNull(type!.GetMethod("Serialize"));
        Assert.NotNull(type.GetMethod("Deserialize"));
        Assert.Null(type.GetProperty("Vacuum"));
    }
}
