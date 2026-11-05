using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Celestial
{
    internal static class CelestialWorldGenSessionRegistry
    {
        private static readonly object gate = new();
        private static readonly ConditionalWeakTable<WorldGrid, CelestialWorldBootstrapScope> sessions = new();
        private static readonly ConditionalWeakTable<PlanetLayer, CelestialWorldBootstrapScope> roots = new();
        internal static CelestialWorldBootstrapScope? Current
        {
            get
            {
                var game = Verse.Current.Game; var world = Verse.Current.CreatingWorld ?? game?.World;
                return game == null || world?.grid == null ? null : Find(world.grid, game, world);
            }
        }
        internal static CelestialWorldBootstrapScope? Find(WorldGrid grid, Game game, World world)
        {
            lock (gate) return sessions.TryGetValue(grid, out var scope) && ReferenceEquals(scope.OwnerGame, game) && ReferenceEquals(scope.OwnerWorld, world) ? scope : null;
        }
        internal static void Register(CelestialWorldBootstrapScope scope)
        {
            lock (gate)
            {
                if (sessions.TryGetValue(scope.Grid, out _)) throw new InvalidOperationException("BootstrapAlreadyActive");
                sessions.Add(scope.Grid, scope);
            }
        }
        internal static void NoteRoot(PlanetLayer root, CelestialWorldBootstrapScope scope)
        { lock (gate) { roots.Add(root, scope); scope.RegisteredRoots.Add(root); } }
        internal static CelestialWorldBootstrapScope? FindRoot(PlanetLayer root)
        { lock (gate) return roots.TryGetValue(root, out var scope) ? scope : null; }
        internal static void Remove(CelestialWorldBootstrapScope scope)
        {
            lock (gate)
            {
                if (!sessions.TryGetValue(scope.Grid, out var current) || !ReferenceEquals(current, scope)) throw new InvalidOperationException("BootstrapScopeMismatch");
                sessions.Remove(scope.Grid);
                foreach (var root in scope.RegisteredRoots) roots.Remove(root);
                scope.RegisteredRoots.Clear();
            }
        }
    }
}
