using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.Celestial
{
    /// <summary>Native calls carry their own cleanup state; sessions are indexed by the exact world and grid.</summary>
    internal static class CelestialWorldBootstrapHooks
    {
        [ThreadStatic] private static Invocation? invocation;
        private static bool installed, constructionInstalled;
        private static readonly object installGate = new();
        internal sealed class Invocation(Invocation? parent, Game? game)
        {
            internal readonly Invocation? Parent = parent;
            internal readonly Game? Game = game;
            internal readonly List<CelestialWorldBootstrapScope> Owned = new();
        }
        internal sealed record Construction(CelestialSurfaceCreationScope Header, CelestialWorldBootstrapScope Root);
        internal static void InstallConstruction()
        {
            lock (installGate)
            {
            if (constructionInstalled) return;
            var harmony = new Harmony("PioneerFramework.Celestial.WorldBootstrap.Construction");
            harmony.Patch(CelestialNativeCompatibility.RequireMethod("WorldGrid.RegisterPlanetLayer.Settings"),
                prefix: new HarmonyMethod(typeof(CelestialWorldBootstrapHooks), nameof(BeginRootConstruction)),
                finalizer: new HarmonyMethod(typeof(CelestialWorldBootstrapHooks), nameof(EndConstruction)));
            harmony.Patch(
                CelestialNativeCompatibility.RequireMethod("WorldGrid.RegisterPlanetLayer"),
                prefix: new HarmonyMethod(typeof(CelestialWorldBootstrapHooks), nameof(BeginConstruction)),
                finalizer: new HarmonyMethod(typeof(CelestialWorldBootstrapHooks), nameof(EndConstruction)));
            CelestialNativeCompatibility.RequireInstalledHooks("WorldGrid.RegisterPlanetLayer.prefix.BeginConstruction", "WorldGrid.RegisterPlanetLayer.finalizer.EndConstruction",
                "WorldGrid.RegisterPlanetLayer.Settings.prefix.BeginRootConstruction", "WorldGrid.RegisterPlanetLayer.Settings.finalizer.EndConstruction");
            constructionInstalled = true;
            }
        }
        internal static void Install()
        {
            lock (installGate)
            {
            if (installed) return;
            InstallConstruction();
            var harmony = new Harmony("PioneerFramework.Celestial.WorldBootstrap");
            harmony.Patch(CelestialNativeCompatibility.RequireMethod("WorldGrid.GenerateFreshWorld"),
                prefix: new HarmonyMethod(typeof(CelestialWorldBootstrapHooks), nameof(BeginFreshWorld)));
            harmony.Patch(CelestialNativeCompatibility.RequireMethod("WorldGenerator.GeneratePlanetLayer"),
                postfix: new HarmonyMethod(typeof(CelestialWorldBootstrapHooks), nameof(PlanetLayerGenerated)));
            harmony.Patch(CelestialNativeCompatibility.RequireMethod("WorldGenerator.GenerateWorld"),
                prefix: new HarmonyMethod(typeof(CelestialWorldBootstrapHooks), nameof(BeginInvocation)),
                finalizer: new HarmonyMethod(typeof(CelestialWorldBootstrapHooks), nameof(FinalizeWorld)));
            CelestialNativeCompatibility.RequireInstalledHooks("WorldGrid.GenerateFreshWorld.prefix.BeginFreshWorld", "WorldGenerator.GeneratePlanetLayer.postfix.PlanetLayerGenerated",
                "WorldGenerator.GenerateWorld.prefix.BeginInvocation", "WorldGenerator.GenerateWorld.finalizer.FinalizeWorld");
            installed = true;
            }
        }
        internal static void BeginRootConstruction(WorldGrid __instance, PlanetLayerDef __0, PlanetLayerSettings __1, Vector3? __4, float? __5, out Construction? __state)
        {
            __state = null;
            if (CelestialSurfaceCreationScope.CurrentHeader != null || !__4.HasValue || !__5.HasValue || !typeof(CelestialSurfaceLayer).IsAssignableFrom(__0.layerType)) return;
            var game = Current.Game; var world = Current.CreatingWorld ?? game?.World;
            if (game == null || world == null || !ReferenceEquals(world.grid, __instance)) throw new InvalidOperationException("SurfaceConstructionWorldMismatch");
            var part = CelestialNativeCompatibility.ResolveRootPart(Find.Scenario);
            var scope = CelestialWorldGenSessionRegistry.Find(__instance, game, world);
            if (!part.Success || scope?.MatchesRoot(__0) != true || !ReferenceEquals(part.Value.layer, __0) || !ReferenceEquals(part.Value.Settings, __1)) return;
            __state = new(new CelestialSurfaceCreationScope(scope.RootHeader, __instance, scope.ConstructRootHeader), scope);
        }
        internal static void BeginConstruction(WorldGrid __instance, PlanetLayerDef __0, out Construction? __state)
        {
            __state = null;
            if (!typeof(CelestialSurfaceLayer).IsAssignableFrom(__0.layerType)) return;
            CelestialSurfaceCreationScope.ValidateGrid(__instance);
            var game = Current.Game; var world = Current.CreatingWorld ?? game?.World;
            if (game == null || world == null || !ReferenceEquals(world.grid, __instance)) throw new InvalidOperationException("SurfaceConstructionWorldMismatch");
            if (CelestialSurfaceCreationScope.CurrentHeader == null) throw new InvalidOperationException("MissingCelestialConstructionSession");
        }
        internal static Exception? EndConstruction(Exception? __exception, Construction? __state, PlanetLayer? __result)
        {
            if (__state == null) return __exception;
            try
            {
                if (__exception == null && __result != null) CelestialWorldGenSessionRegistry.NoteRoot(__result, __state.Root);
            }
            catch (Exception cleanup)
            {
                if (__exception == null) __exception = cleanup;
                else __exception.Data["PFL.Celestial.RootRegistration"] = cleanup;
            }
            try { __state.Header.Dispose(); }
            catch (Exception cleanup)
            {
                if (__exception == null) __exception = cleanup;
                else __exception.Data["PFL.Celestial.HeaderCleanup"] = cleanup;
            }
            return __exception;
        }
        internal static void BeginInvocation(out Invocation __state)
        { __state = new(invocation, Current.Game); invocation = __state; }
        internal static void BeginFreshWorld(WorldGrid __instance)
        {
            var game = Current.Game; var world = Current.CreatingWorld ?? game?.World;
            if (game == null || world == null || !ReferenceEquals(world.grid, __instance)) throw new InvalidOperationException("CelestialBootstrap: NativeWorldMismatch");
            var existing = CelestialWorldGenSessionRegistry.Find(__instance, game, world);
            if (existing != null) { existing.Runtime.HostWork.BindHostThread(); return; }
            CelestialBuiltinCompRegistry.Global.Seal();
            CelestialCapabilityRegistry.Global.Seal();
            ulong seed = unchecked((uint)GenText.StableStringHash(world.info.seedString));
            var started = CelestialWorldBootstrap.Begin(CelestialWorldBootstrap.DefaultSystem, seed);
            if (!started.Success)
            {
                // 2026-11-17：天体内容 Def（系统/天体/档案/视觉）已迁到附属包 NFexp。PFL 单独启用时
                // 「系统定义不存在」是合法配置（无天体框架），不应把原版建世界整个打断；
                // 其余任何失败仍按原样抛。
                if (started.Errors.Count == 1 && started.Errors[0].Code == "SystemDefinitionMissing")
                {
                    Log.Warning("[PFL] 未找到天体系统定义 " + CelestialWorldBootstrap.DefaultSystem
                        + "（内容包 NFexp 未启用？）—— 本世界不启用天体框架。");
                    return;
                }
                throw Failure(started.Errors);
            }
            var scope = started.Value; scope.ManagedByNativeHooks = true;
            if (invocation != null && ReferenceEquals(invocation.Game, game)) invocation.Owned.Add(scope);
        }
        internal static void PlanetLayerGenerated(PlanetLayer __0)
        {
            if (!__0.IsRootSurface) return;
            var scope = CelestialWorldGenSessionRegistry.FindRoot(__0);
            if (scope == null || !scope.ManagedByNativeHooks) return;
            if (!ReferenceEquals(Current.Game, scope.OwnerGame) || !ReferenceEquals(Current.CreatingWorld ?? Current.Game?.World, scope.OwnerWorld) ||
                !ReferenceEquals(scope.OwnerWorld.grid, scope.Grid)) throw new InvalidOperationException("CelestialBootstrap: WorldChanged");
            if (__0 is not CelestialSurfaceLayer root) throw new InvalidOperationException("CelestialBootstrap: RootLayerAdapterMissing");
            var bound = scope.BindRoot(root);
            if (!bound.Success) throw Failure(bound.Errors);
            var complete = scope.Complete();
            if (!complete.Success) throw Failure(complete.Errors);
            scope.Dispose();
        }
        internal static Exception? FinalizeWorld(Exception? __exception, Invocation? __state = null)
        {
            var current = CelestialWorldBootstrap.Current;
            var owned = __state?.Owned.ToArray() ?? (current?.ManagedByNativeHooks == true ? new[] { current } : Array.Empty<CelestialWorldBootstrapScope>());
            if (__exception == null && owned.Any(scope => !scope.IsCompleted)) __exception = new InvalidOperationException("CelestialBootstrap: RootGenerationIncomplete");
            foreach (var scope in owned.Reverse())
            {
                try { scope.Dispose(); }
                catch (Exception cleanup)
                {
                    if (__exception == null) __exception = cleanup;
                    else __exception.Data["PFL.Celestial.Cleanup." + scope.Runtime.WorldSessionId] = cleanup;
                }
            }
            if (__state != null && ReferenceEquals(invocation, __state)) invocation = __state.Parent;
            return __exception;
        }
        private static InvalidOperationException Failure(IReadOnlyList<CelestialError> errors)
            => new("CelestialBootstrap: " + string.Join("; ", errors.Select(error => error.Code + " " + error.Detail)));
    }
}
