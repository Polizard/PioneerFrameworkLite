using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PioneerFramework.Celestial;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.MapGeneration
{
    internal sealed class NativeLocalMapSession : IServiceProvider, IMapGenerationExecutionScopeFactory
    {
        private static NativeLocalMapSession? active;
        internal static NativeLocalMapSession? Active => Volatile.Read(ref active);
        internal readonly FrozenLocalMapContext Frozen;
        internal readonly MapGenerationPurpose Purpose;
        internal readonly MapParent Parent;
        private readonly Game? game;
        private readonly World? world;
        private readonly CelestialMapService? maps;
        private readonly PlanetTile initialTile;
        private readonly CelestialSurfaceLayer? initialLayer;
        private readonly CelestialMapAddress? address;
        internal Map? Map;
        internal NativeMapGenerationServices? Native;
        internal MapGenerationExecutor? Executor;
        internal string Stage = "Preflight";
        internal string? Error;
        internal bool Logging;
        private int ownerThread;
        private bool contentFinished, postFinished;
        // P1-2: only PFL-out-sourced native callbacks may turn a Log.Error into session failure.
        // EnterHost(...) covers Run(stage) host callbacks, EnterErrorWindow() covers ConstructComponents,
        // and Enter(...) adds the wrapped native GenStep callbacks (handler PFL.LocalMap.NativeGenStep).
        // Plain module code and all log traffic outside those windows are ignored.
        private int errorWindowDepth;
        internal NativeLocalMapSession(FrozenLocalMapContext frozen, MapGenerationPurpose purpose, MapParent parent)
        {
            Frozen = frozen; Purpose = purpose; Parent = parent;
            game = Current.Game;
            world = Find.World;
            initialTile = parent.Tile;
            maps = (CelestialWorldServices.TryGetCurrent()?.Query as CelestialRuntime)?.Maps;
            if (maps != null && initialTile.Layer is CelestialSurfaceLayer layer)
            {
                initialLayer = layer;
                address = new(layer.Header.OwnerId, layer.LayerID, initialTile.tileId, parent.ID);
            }
        }
        internal void Activate()
        {
            if (Interlocked.CompareExchange(ref active, this, null) != null) throw new InvalidOperationException("NestedNativeMapGeneration");
            ownerThread = Environment.CurrentManagedThreadId;
        }
        internal void Release() => Interlocked.CompareExchange(ref active, null, this);
        internal void Check()
        { if (Error != null) throw new InvalidOperationException("LocalMapGenerationFailed: " + Stage + ": " + Error); }
        internal void ObserveError(string text)
        {
            // 只观察 PFL 外包给原生回调的窗口（见 errorWindowDepth 注释）。Logging 只包失败摘要的
            // Log.Message，因此 !Logging 当前恒真；保留它是为了将来摘要若改回 Log.Error 时不回灌自身。
            if (Environment.CurrentManagedThreadId == ownerThread && Volatile.Read(ref errorWindowDepth) > 0 && !Logging) Error ??= text;
        }
        internal static int Fold(ulong seed) => unchecked((int)(seed ^ (seed >> 32)));
        internal IDisposable EnterHost(string stage)
        {
            Stage = stage; Check();
            return EnterSeed(CelestialSeed.Derive(MapGenerationSeeds.Root(Frozen.Source), "local-map", "native-host", stage), errorWindow: true);
        }
        internal IDisposable EnterPreparation(MapGenerationStepSpec step)
        {
            Stage = "Plan:" + step.Key; Check();
            return EnterSeed(MapGenerationSeeds.ForStep(Frozen.Source, Purpose, step), errorWindow: false);
        }
        private IDisposable EnterSeed(ulong seed, bool errorWindow)
        {
            Rand.PushState(Fold(seed));
            if (errorWindow) errorWindowDepth++;
            return new Scope(this, errorWindow);
        }
        /// <summary>Error-attribution window without a Rand scope (ConstructComponents keeps the early root stream).</summary>
        internal IDisposable EnterErrorWindow()
        {
            errorWindowDepth++;
            return new ErrorWindow(this);
        }
        private sealed class ErrorWindow(NativeLocalMapSession owner) : IDisposable
        {
            // No Check() here: Construct() checks right after the window so an exception thrown by
            // ConstructComponents itself is never replaced by the logged-error boundary exception.
            public void Dispose() => owner.errorWindowDepth--;
        }
        private sealed class Scope(NativeLocalMapSession owner, bool errorWindow) : IDisposable
        {
            public void Dispose()
            {
                try
                {
                    if (owner.Map?.pathing?.IncrementalDirtyingDisabled == true)
                    {
                        owner.Map.pathing.ReEnableIncrementalDirtying();
                        owner.Error ??= "Native path incremental dirtying left disabled.";
                    }
                }
                finally
                {
                    Rand.PopState();
                    if (errorWindow) owner.errorWindowDepth--;
                }
                owner.Check();
            }
        }
        public IDisposable Enter(MapGenerationContext context, bool postInitialize)
        {
            Stage = (postInitialize ? "PostInitialize:" : "Generate:") + context.Step.Key;
            Check();
            // The native GenStep wrapper is the other PFL-out-sourced native callback surface: a Log.Error
            // raised by the wrapped GenStep must still fail the session (native catch-and-continue is not used).
            return EnterSeed(context.Seed, context.Step.HandlerId == LocalMapBuiltinConfigurations.Native);
        }
        public object? GetService(Type type) => type == typeof(INativeMapGenerationServices) ? Native : type == typeof(IMapGenerationExecutionScopeFactory) ? this : null;
        internal void Generate(IEnumerable<GenStepWithParams> candidates, Map map)
        {
            if (Map != null && !ReferenceEquals(Map, map)) throw new InvalidOperationException("LocalMapIdentityChanged");
            Map = map; Stage = "Plan"; Check();
            var materialized = candidates.ToArray();
            NativeLocalMapHooks.ClearWorkingData();
            var gravship = materialized.FirstOrDefault(step => step.def == GenStepDefOf.GravshipMarker).parms.gravship;
            NativeLocalMapHooks.SetGravship(gravship);
            if (gravship != null)
                for (int i = 0; i < materialized.Length; i++)
                { var parms = materialized[i].parms; parms.gravship = gravship; materialized[i] = new(materialized[i].def, parms); }
            var plan = NativeLocalMapPlan.Build(Frozen, Purpose, materialized, LocalMapGenerationModules.Registry, EnterPreparation, Parent is Site);
            Native = new(map, Frozen.Source, plan.NativeSteps);
            foreach (var input in plan.InitialInputs) Native.SetInitialOverrides(input);
            Executor = new(plan.Plan, Frozen.Source, this, preparedModules: plan.Modules);
            Exception? contentError = null;
            try
            {
                using (EnterHost("RockNoises.Init")) RockNoises.Init(map);
                if (Executor.Execute().State != MapGenerationState.Generated)
                    throw new InvalidOperationException(Executor.Report.Steps.Last().Error);
                contentFinished = true;
            }
            catch (Exception exception) { contentError = exception; throw; }
            finally
            {
                try { RockNoises.Reset(); }
                catch (Exception cleanup)
                {
                    Error = (contentError?.ToString() ?? Error ?? "") + "\nRockNoises.Reset: " + cleanup;
                    if (contentError == null) throw;
                }
            }
        }
        internal void PostInitialize(Map map)
        {
            Check();
            if (!ReferenceEquals(Map, map) || !contentFinished || Executor == null) throw new InvalidOperationException("LocalMapContentIncomplete");
            if (Executor.PostInitialize().State != MapGenerationState.Ready) throw new InvalidOperationException(Executor.Report.Steps.Last(s => s.Error != null).Error);
            postFinished = true;
        }
        internal void Finish(Map? result, Exception? exception)
        {
            if (exception != null) Error ??= exception.ToString();
            if (Error == null && (!contentFinished || !postFinished || !ReferenceEquals(result, Map))) Error = "Native lifecycle did not complete.";
            if (Error == null && (result == null || !ReferenceEquals(Current.Game, game) || !ReferenceEquals(Find.World, world) ||
                game?.Maps == null || !game.Maps.Contains(result) ||
                world?.worldObjects == null || !world.worldObjects.MapParents.Contains(Parent) ||
                !ReferenceEquals(Parent.Map, result) || !ReferenceEquals(result.Parent, Parent) ||
                Parent.Tile != initialTile || Parent.Tile != result.Tile || Parent.Tile.Layer is not CelestialSurfaceLayer layer ||
                !ReferenceEquals(layer, initialLayer) || Parent.ID != address?.MapParentId ||
                layer.Header?.OwnerId.Value != Frozen.Source.SourceId || Parent.Tile.tileId != Frozen.Source.TileId))
                Error = "Native map identity changed before publication.";
            // P1-1: the native Map was registered by Game.AddMap before content/FinalizeInit could fail.
            // Remove it before Capture/RecordNativeGeneration so the published provenance keeps MapId=null;
            // CelestialNativeMapLifecycle then sees a site without MapId and RetireMap early-returns,
            // preserving the failure/Generation/operation history.
            if (Error != null) RemoveRegisteredMap();
            var state = Error == null ? MapGenerationState.Ready : MapGenerationState.Failed;
            var record = LocalMapGenerationRecord.Capture(new(Frozen.Source, Frozen.Specification, Purpose), state, Stage, Error, Executor?.Report);
            // PlanetTile resolves through the current world. Publication uses the captured world service and address.
            if (address != null)
                maps!.RecordNativeGeneration(address, Map != null && game?.Maps?.Contains(Map) == true ? Map.uniqueID : null, record);
            if (Error != null)
            {
                Logging = true;
                try { Log.Message($"[PFL LocalMap] Failed source={Frozen.Source.SourceId} tile={Frozen.Source.TileId} parent={Parent.ID} map={Map?.uniqueID} purpose={Purpose} recipe={record.RecipeHash} stage={Stage} seed={record.RootSeed} steps={System.Text.Json.JsonSerializer.Serialize(record.Steps)} exception={Error}"); }
                finally { Logging = false; }
            }
        }
        /// <summary>Best-effort removal of this session's partially generated Map; never touches other maps.</summary>
        private void RemoveRegisteredMap()
        {
            var map = Map;
            if (map == null) return;
            var owner = game;
            if (!ReferenceEquals(Current.Game, owner))
            {
                Error += "\nMapCleanup: current game changed; the registered map was not removed.";
                return;
            }
            if (owner?.Maps == null || !owner.Maps.Contains(map)) return;
            try
            {
                // Best effort: a partially initialized map can throw inside native deinit. Error already holds the
                // original failure as its prefix, so a cleanup diagnostic is appended instead of replacing it.
                owner.DeinitAndRemoveMap(map, false);
            }
            catch (Exception cleanup) { Error += "\nMapCleanup: " + cleanup; }
            if (owner.Maps.Contains(map)) Error += "\nMapCleanup: the registered map is still present after DeinitAndRemoveMap.";
        }
        internal static bool TryInputs(Map map, out CelestialPlanetMapDiversity diversity, out MapSourceContext source)
        {
            var session = Active;
            if (session?.Native?.InitialOverrides is not { } input || !ReferenceEquals(map, session.Map) || !ReferenceEquals(MapGenerator.mapBeingGenerated, map))
            { diversity = null!; source = null!; return false; }
            source = session.Frozen.Source;
            diversity = session.inputProjection ??= new(input.Rocks, input.Minerals, input.Soils, input.Plants, input.Animals,
                mineralAbundance: input.MineralAbundance, soilFertility: input.SoilFertility, plantDensity: input.PlantDensity,
                animalDensity: input.AnimalDensity, geologyVariation: input.GeologyVariation, allowCaves: input.AllowCaves);
            return true;
        }
        private CelestialPlanetMapDiversity? inputProjection;
        internal static bool TryInputs(PlanetTile tile, out CelestialPlanetMapDiversity diversity, out MapSourceContext source)
        {
            var map = Active?.Map;
            if (map != null && map.Tile == tile) return TryInputs(map, out diversity, out source);
            diversity = null!; source = null!; return false;
        }
    }
}
