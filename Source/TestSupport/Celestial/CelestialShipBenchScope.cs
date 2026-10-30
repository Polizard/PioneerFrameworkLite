#if !PURE_MATH
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PioneerFramework;
using PioneerFramework.Celestial;
using PioneerFramework.Combat;
using PioneerFramework.Components;
using PioneerFramework.SpaceAI;
using PioneerFramework.TestSupport.Celestial;
using Verse;

internal sealed class CelestialShipBenchScope : IDisposable
{
    private readonly Game previous = Current.Game;
    private readonly Harmony patches = new("PFL.CelestialBenchmark");
    private readonly int previousFrame = RealTime.frameCount;
    private readonly float previousDelta = RealTime.deltaTime;
    private readonly FieldInfo tickField = AccessTools.Field(typeof(TickManager), "ticksGameInt");
    internal readonly CelestialRuntime Runtime;
    internal readonly SpaceGameComp Space;
    private int tick;
    private TimeScaleLevel speed = TimeScaleLevel.Normal;
    internal void SetSpeed(TimeScaleLevel level)
    { speed = level; Current.Game.GetComponent<TimeScaleManager>().SetTimeScale(level); }
    internal CelestialShipBenchScope(int ships, int maps)
    {
        patches.Patch(AccessTools.Method(typeof(Log), nameof(Log.Message), new[] { typeof(string) }),
            prefix: new HarmonyMethod(typeof(CelestialShipBenchScope), nameof(Skip)));
        foreach (var method in typeof(Messages).GetMethods().Where(m => m.Name == nameof(Messages.Message)))
            patches.Patch(method, prefix: new HarmonyMethod(typeof(CelestialShipBenchScope), nameof(Skip)));
        var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        AccessTools.Field(typeof(Game), "tickManager").SetValue(game, RuntimeHelpers.GetUninitializedObject(typeof(TickManager)));
        Space = new SpaceGameComp(game);
        var manager = new CelestialBodyManager(game);
        var clock = new TimeScaleManager(game);
        game.components = new() { Space, manager, clock, new CombatManager(game), new SpaceAiManager(game), new CombatGrid(game) };
        Current.Game = game;
        AccessTools.Field(typeof(TickManager), "curTimeSpeed").SetValue(Find.TickManager, TimeSpeed.Normal);
        RealTime.frameCount = 10000; RealTime.deltaTime = 1f / 240f;
        clock.GetPhysicsTick();
        Runtime = CelestialBenchFactory.Create(3);
        CelestialBenchFactory.Require(manager.AttachCelestialRuntime(Runtime).Success, "Host attachment failed.");
        var fuel = new PropellantDef { defName = "PF_BenchFuel" };
        for (int i = 0; i < ships; i++)
        {
            var group = new SpaceEntityGroup(100 + i, 2) { CelestialBodyId = 1, IsActive = true, IsMovable = true,
                exemptPlanetCollision = true, CurrentOrbit = new() { positionSystem = (SystemVec3)new DVector3(1e7 + i * 1000, 0, 0),
                    velocitySystem = (SystemVec3)new DVector3(0, 0, 100), rotation = DQuaternion.identity } };
            Space.RegisterGroup(group);
            var entity = new SpaceEntity(1000 + i, 2) { GroupId = group.GroupId, IsAffectedByGravity = false };
            entity.AddPropulsion(new PropulsionData { propellant = fuel, ve = 1000, massFlow = 1,
                mass = 1e6, capacity = 1e6, isVectoredRcs = true });
            Space.RegisterEntity(entity); group.GetEntityIds().Add(entity.EntityId); group.StartBurn(new DVector3(1e8, 0, 0));
        }
        for (int i = 0; i < maps; i++)
        {
            var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map)); map.uniqueID = 2000 + i;
            var component = new SpaceMapComp(map);
            component.AddPropellantMass(fuel, 1000);
            Space.RegisterMapComponent(map.uniqueID, component);
        }
    }
    internal void Advance()
    {
        double expected = Runtime.CurrentStamp.PhysicalSeconds + 2 * (speed == TimeScaleLevel.Fast ? 8 : 1);
        tick += 2; tickField.SetValue(Find.TickManager, tick);
        Space.AdvancePhysics(tick);
        CelestialBenchFactory.Require(Space.CelestialPhysicsErrors.Count == 0 && Runtime.CurrentStamp.PhysicalSeconds == expected,
            "Ship preparation failed: " + string.Join(",", Space.CelestialPhysicsErrors.Select(e => e.Code + ":" + e.Detail)));
    }
    private static bool Skip() => false;
    public void Dispose()
    { Current.Game = previous; RealTime.frameCount = previousFrame; RealTime.deltaTime = previousDelta; patches.UnpatchAll(patches.Id); }
}

#endif
