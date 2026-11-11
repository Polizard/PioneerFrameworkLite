using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PioneerFramework.Celestial;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.MapGeneration
{
    /// <summary>One scoped native boundary. GenerateMap retains lifecycle ownership; only content lists and exact callback calls are adapted.</summary>
    [StaticConstructorOnStartup]
    public static class NativeLocalMapHooks
    {
        private static bool installed;
        private static readonly object gate = new();
        private static readonly MethodInfo clear = AccessTools.Method(typeof(MapGenerator), "ClearWorkingData", Type.EmptyTypes)
            ?? throw new InvalidOperationException("NativeContractMissing: ClearWorkingData");
        private static readonly FieldInfo gravshipField = AccessTools.Field(typeof(MapGenerator), "gravship")
            ?? throw new InvalidOperationException("NativeContractMissing: gravship");
        static NativeLocalMapHooks() => Install();
        internal static void ClearWorkingData() => clear.Invoke(null, null);
        internal static void SetGravship(Gravship? gravship) => gravshipField.SetValue(null, gravship);
        public static bool IsActive => NativeLocalMapSession.Active != null;
        private static CelestialMapService? CurrentMaps => (CelestialWorldServices.TryGetCurrent()?.Query as CelestialRuntime)?.Maps;
        public static void Install()
        {
            lock (gate)
            {
                if (installed) return;
                if (clear.ReturnType != typeof(void) || !clear.IsStatic || gravshipField.FieldType != typeof(Gravship) || !gravshipField.IsStatic)
                    throw new InvalidOperationException("NativeContractMissing: local map workspace");
                var h = new Harmony("PioneerFramework.LocalMapGeneration");
                var generate = CelestialNativeCompatibility.RequireMethod("MapGenerator.GenerateMap");
                var original = PatchProcessor.GetOriginalInstructions(generate);
                if (original.Count(i => i.Calls(AccessTools.PropertySetter(typeof(Rand), nameof(Rand.Seed)))) != 1)
                    throw new InvalidOperationException("NativeContractMissing: initial Rand.Seed anchor");
                h.Patch(generate, prefix: new(typeof(NativeLocalMapHooks), nameof(Begin)),
                    transpiler: new(typeof(NativeLocalMapHooks), nameof(AdaptLifecycle)), finalizer: new(typeof(NativeLocalMapHooks), nameof(End)));
                h.Patch(CelestialNativeCompatibility.RequireMethod("MapGenerator.GenerateContentsIntoMap"), prefix: new(typeof(NativeLocalMapHooks), nameof(Contents)));
                h.Patch(CelestialNativeCompatibility.RequireMethod("MapGenerator.MapGeneratorPostInit"), prefix: new(typeof(NativeLocalMapHooks), nameof(PostInitialize)));
                h.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(NativeLocalMapHooks), nameof(ObserveError)) { priority = Priority.First });
                var reuse = typeof(GetOrGenerateMapUtility).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "GetOrGenerateMap").ToArray();
                if (reuse.Length == 0) throw new InvalidOperationException("NativeContractMissing: GetOrGenerateMap");
                foreach (var method in reuse) h.Patch(method, prefix: new(typeof(NativeLocalMapHooks), nameof(CheckReuse)));
                installed = true;
            }
        }
        internal static void Begin(IntVec3 mapSize, MapParent parent, MapGeneratorDef mapGenerator, bool isPocketMap, bool stepDebugger, out NativeLocalMapSession? __state)
        {
            __state = null;
            if (IsActive) throw new InvalidOperationException("NestedNativeMapGeneration");
            if (parent == null) return;
            var generator = mapGenerator ?? MapGeneratorDefOf.Encounter;
            var game = Current.Game;
            var world = Find.World;
            var tile = parent.Tile;
            var maps = CurrentMaps;
            CheckFailed(maps?.FindFailedSite(parent, parent.Map), generator, mapSize, isPocketMap);
            var resolved = LocalMapSourceResolver.Resolve(tile, mapSize.x, mapSize.z, generator, isPocketMap);
            if (!resolved.Success)
            {
                if (resolved.Errors.Any(e => e.Code is "LocalMapSourceOutOfScope" or "LocalMapGeneratorOutOfScope")) return;
                throw new InvalidOperationException(string.Join("; ", resolved.Errors.Select(e => e.Code + ":" + e.Detail)));
            }
            if (!ReferenceEquals(game, Current.Game) || !ReferenceEquals(world, Find.World) || !ReferenceEquals(maps, CurrentMaps) || parent.Tile != tile)
                throw new InvalidOperationException("LocalMapIdentityChanged");
            if (stepDebugger) throw new InvalidOperationException("LocalMapStepDebuggerUnsupported");
            if (parent.HasMap) throw new InvalidOperationException("LocalMapAlreadyExists");
            var purpose = ResolvePurpose(parent, generator);
            var session = new NativeLocalMapSession(resolved.Value, purpose, parent);
            session.Activate(); __state = session;
        }
        internal static MapGenerationPurpose ResolvePurpose(MapParent parent, MapGeneratorDef generator)
            => parent is Site ? MapGenerationPurpose.Quest : parent is Landing.LandedShipMapParent ? MapGenerationPurpose.Landing :
                parent is Settlement settlement && settlement.Faction != null && settlement.Faction == Faction.OfPlayer ||
                generator.defName is "Base_Player" or "BasePlayer_SecondArchonexusCycle" or "BasePlayer_ThirdArchonexusCycle"
                    ? MapGenerationPurpose.Colony : MapGenerationPurpose.Encounter;
        internal static void CheckReuse(object[] __args)
        {
            var tile = __args.OfType<PlanetTile>().FirstOrDefault();
            if (!tile.Valid || CurrentMaps is not { } maps) return;
            var map = Current.Game?.FindMap(tile);
            var parent = map?.Parent ?? Find.World?.worldObjects?.MapParentAt(tile);
            if (parent == null) return;
            var known = maps.FindFailedSite(parent, map);
            if (known == null) return;
            var generator = map?.generatorDef ?? (parent.def == null ? MapGeneratorDefOf.Encounter : parent.MapGeneratorDef);
            var size = map?.Size ?? __args.OfType<IntVec3>().FirstOrDefault();
            if (size.x <= 0 || size.z <= 0) size = new IntVec3(250, 1, 250);
            CheckFailed(known, generator, size, map?.IsPocketMap == true);
        }
        private static void CheckFailed(CelestialMapSiteRecord? known, MapGeneratorDef? generator, IntVec3 size, bool isPocketMap)
        {
            if (known == null || generator == null || !Find.WorldGrid.PlanetLayers.TryGetValue(known.Address.LayerId, out var layer)) return;
            // Scope the generator to the recorded source, including when the failed parent now sits elsewhere.
            var resolved = LocalMapSourceResolver.Resolve(new PlanetTile(known.Address.TileId, layer), size.x, size.z, generator, isPocketMap);
            if (!resolved.Success && resolved.Errors.Any(e => e.Code is "LocalMapSourceOutOfScope" or "LocalMapGeneratorOutOfScope")) return;
            throw new InvalidOperationException("MapGenerationIncomplete: " + known.Failure);
        }
        internal static Exception? End(Exception? __exception, Map? __result, NativeLocalMapSession? __state)
        {
            if (__state == null) return __exception;
            Exception? failure = __exception;
            try
            {
                try { RockNoises.Reset(); } catch (Exception e) { failure ??= e; }
                try { ClearWorkingData(); } catch (Exception e) { failure ??= e; }
                try { MapGenerator.mapBeingGenerated = null; SetGravship(null); } catch (Exception e) { failure ??= e; }
                try { __state.Finish(__result, failure); } catch (Exception e) { failure ??= e; }
                return failure ?? (__state.Error == null ? null : new InvalidOperationException("LocalMapGenerationFailed: " + __state.Error));
            }
            finally
            {
                __state.Release();
            }
        }
        internal static bool Contents(IEnumerable<GenStepWithParams> genStepDefs, Map map)
        {
            var session = NativeLocalMapSession.Active;
            if (session == null) return true;
            session.Generate(genStepDefs, map); return false;
        }
        internal static bool PostInitialize(Map map)
        {
            var session = NativeLocalMapSession.Active;
            if (session == null) return true;
            session.PostInitialize(map); return false;
        }
        internal static void ObserveError(string text) => NativeLocalMapSession.Active?.ObserveError(text);
        internal static void ApplyInitialSeed(int original) => Rand.Seed = NativeLocalMapSession.Active is { } s ? NativeLocalMapSession.Fold(MapGenerationSeeds.Root(s.Frozen.Source)) : original;
        internal static void Construct(Map map)
        {
            var s = NativeLocalMapSession.Active;
            if (s == null) { map.ConstructComponents(); return; }
            s.Map = map;
            // Do not reseed here: components and mutator setup consume the early stable root stream.
            s.Stage = "ConstructComponents"; s.Check(); map.ConstructComponents(); s.Check();
        }
        private static void FinalizeMap(Map map) => Run("FinalizeInit", map.FinalizeInit);
        private static void Generated(Map map) => Run("MapComponent.MapGenerated", () => MapComponentUtility.MapGenerated(map));
        private static void ScenarioGenerated(Scenario scenario, Map map) => Run("Scenario.PostMapGenerate", () => scenario.PostMapGenerate(map));
        private static void ParentGenerated(MapParent parent) => Run("MapParent.PostMapGenerate", parent.PostMapGenerate);
        private static void MutatorInit(TileMutatorWorker worker, Map map) => Run("Mutator.Init:" + worker.GetType().FullName, () => worker.Init(map));
        private static void Weather(WeatherDecider decider) => Run("InitialWeather", decider.StartInitialWeather);
        private static void ExtraInit(Action<Map> callback, Map map) => Run("ExtraInit", () => callback(map));
        private static void Run(string stage, Action callback)
        { using (NativeLocalMapSession.Active?.EnterHost(stage)) callback(); }
        internal static IEnumerable<CodeInstruction> AdaptLifecycle(IEnumerable<CodeInstruction> instructions)
        {
            var replacements = new Dictionary<MethodInfo, MethodInfo> {
                [AccessTools.PropertySetter(typeof(Rand), nameof(Rand.Seed))] = AccessTools.Method(typeof(NativeLocalMapHooks), nameof(ApplyInitialSeed)),
                [AccessTools.Method(typeof(Map), nameof(Map.ConstructComponents))] = AccessTools.Method(typeof(NativeLocalMapHooks), nameof(Construct)),
                [AccessTools.Method(typeof(Map), nameof(Map.FinalizeInit))] = AccessTools.Method(typeof(NativeLocalMapHooks), nameof(FinalizeMap)),
                [AccessTools.Method(typeof(MapComponentUtility), nameof(MapComponentUtility.MapGenerated))] = AccessTools.Method(typeof(NativeLocalMapHooks), nameof(Generated)),
                [AccessTools.Method(typeof(Scenario), nameof(Scenario.PostMapGenerate))] = AccessTools.Method(typeof(NativeLocalMapHooks), nameof(ScenarioGenerated)),
                [AccessTools.Method(typeof(MapParent), nameof(MapParent.PostMapGenerate))] = AccessTools.Method(typeof(NativeLocalMapHooks), nameof(ParentGenerated)),
                [AccessTools.Method(typeof(TileMutatorWorker), nameof(TileMutatorWorker.Init))] = AccessTools.Method(typeof(NativeLocalMapHooks), nameof(MutatorInit)),
                [AccessTools.Method(typeof(WeatherDecider), nameof(WeatherDecider.StartInitialWeather))] = AccessTools.Method(typeof(NativeLocalMapHooks), nameof(Weather)),
                [AccessTools.Method(typeof(Action<Map>), "Invoke")] = AccessTools.Method(typeof(NativeLocalMapHooks), nameof(ExtraInit)) };
            var counts = replacements.Keys.ToDictionary(m => m, _ => 0);
            foreach (var instruction in instructions)
            {
                if (instruction.operand is MethodInfo method && (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && replacements.TryGetValue(method, out var replacement))
                {
                    counts[method]++;
                    yield return new CodeInstruction(OpCodes.Call, replacement).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                }
                else yield return instruction;
            }
            if (counts.Any(pair => pair.Value != 1)) throw new InvalidOperationException("NativeContractMissing: lifecycle anchors " + string.Join(",", counts.Where(p => p.Value != 1).Select(p => p.Key.Name + "=" + p.Value)));
        }
    }
}
