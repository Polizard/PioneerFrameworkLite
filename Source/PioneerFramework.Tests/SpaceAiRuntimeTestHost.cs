using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PioneerFramework.Combat;
using PioneerFramework.SpaceAI;
using Verse;

namespace PioneerFramework.Tests
{
    internal sealed class SpaceAiRuntimeTestHost : IDisposable
    {
        private readonly Game previous = Current.Game;
        private readonly Harmony bootstrap = new("PFL.SpaceAI.RuntimeHost");
        internal readonly Game Game;
        internal readonly SpaceGameComp Space;
        internal readonly SpaceAiManager Ai;
        internal readonly CombatGrid Grid;
        internal SpaceAiRuntimeTestHost()
        {
            bootstrap.Patch(AccessTools.Method(typeof(Log), nameof(Log.Message), new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(SpaceAiRuntimeTestHost), nameof(SkipLog)));
            Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            AccessTools.Field(typeof(Game), "tickManager").SetValue(Game,
                RuntimeHelpers.GetUninitializedObject(typeof(TickManager)));
            Space = new SpaceGameComp(Game); Ai = new SpaceAiManager(Game); Grid = new CombatGrid(Game);
            var bodies = new CelestialBodyManager(Game);
            AccessTools.Field(typeof(CelestialBodyManager), "initialized").SetValue(bodies, true);
            AccessTools.Field(typeof(CelestialBodyManager), "bodiesById").SetValue(bodies,
                new Dictionary<int,CelestialBodyInfo> { [0] = new CelestialBodyInfo { bodyId = 0, bodyType = CelestialBodyType.Planet } });
            Game.components = new List<GameComponent> { Space, Ai, Grid, bodies, new CombatManager(Game) };
            Current.Game = Game;
        }
        private static bool SkipLog() => false;
        internal SpaceEntityGroup AddShip()
        {
            var group = new SpaceEntityGroup(10, 2) { CelestialBodyId = 0, IsCollidable = true };
            Space.RegisterGroup(group); Ai.TryRegisterFleet(2, new[] { group.GroupId }, out _);
            return group;
        }
        public void Dispose() { Current.Game = previous; bootstrap.UnpatchAll(bootstrap.Id); }
    }
}
