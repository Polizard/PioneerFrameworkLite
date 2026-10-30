using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Tests;

internal sealed class CelestialNativeWorldTestScope : IDisposable
{
    private readonly Game previousGame = Current.Game;
    private readonly World previousWorld = Current.CreatingWorld;
    private readonly CelestialXmlTestScope preferences = new();
    private readonly CelestialNativeLayerProbeTests.ProbeSavePath mods = new();
    private readonly List<Action> restore = new();
    public Game Game { get; }
    public World World { get; }
    public WorldGrid Grid { get; } = new();
    public CelestialNativeWorldTestScope()
    {
        Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        Game.components = new List<GameComponent> { new CelestialBodyManager(Game) };
        AccessTools.Field(typeof(Game), "maps").SetValue(Game, new List<Map>());
        World = new World { grid = Grid, worldObjects = new WorldObjectsHolder() };
        World.info.planetCoverage = 1;
        World.info.seedString = "celestial-world-probe";
        AccessTools.Field(typeof(Game), "worldInt").SetValue(Game, World);
        Current.Game = Game;
        Current.CreatingWorld = World;
        Rand.PushState(42);
    }
    public void AddDefs<T>(params T[] values) where T : Def, new()
    {
        var previous = DefDatabase<T>.AllDefsListForReading.ToArray();
        var field = AccessTools.Field(typeof(DefDatabase<T>), "defsByShortHash");
        var hashes = (Dictionary<ushort, T>)field.GetValue(null);
        var previousHashes = new Dictionary<ushort, T>(hashes);
        restore.Add(() => {
            DefDatabase<T>.Clear();
            foreach (var definition in previous) DefDatabase<T>.Add(definition);
            hashes = (Dictionary<ushort, T>)field.GetValue(null);
            hashes.Clear();
            foreach (var pair in previousHashes) hashes.Add(pair.Key, pair.Value);
        });
        foreach (var definition in values)
        {
            DefDatabase<T>.Add(definition);
            if (definition.shortHash != 0) hashes[definition.shortHash] = definition;
        }
    }
    public void SetRoot(PlanetLayerDef definition)
    {
        var scenario = new Scenario();
        AccessTools.Field(typeof(Scenario), "surfaceLayer").SetValue(scenario, new ScenPart_PlanetLayer {
            layer = definition, tag = "root", settingsDef = new PlanetLayerSettingsDef {
                settings = new PlanetLayerSettings { radius = 100, subdivisions = 0, useSurfaceViewAngle = true } } });
        Game.Scenario = scenario;
    }
    public void Dispose()
    {
        Grid.Dispose();
        foreach (var action in restore.AsEnumerable().Reverse()) action();
        Current.CreatingWorld = previousWorld;
        Current.Game = previousGame;
        Rand.PopState();
        mods.Dispose();
        preferences.Dispose();
    }
}
