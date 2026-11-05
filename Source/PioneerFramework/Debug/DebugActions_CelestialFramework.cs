using System;
using System.Linq;
using LudeonTK;
using PioneerFramework.Celestial;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public static class DebugActions_CelestialFramework
    {
        [DebugAction("PFL Celestial", "Inspect instances and requests", allowedGameStates = AllowedGameStates.Playing)]
        public static void Inspect() => Find.WindowStack.Add(new Window_CelestialFrameworkDebug());
        [DebugAction("PFL Celestial", "Retry suspended restoration", allowedGameStates = AllowedGameStates.Playing)]
        public static void RetryRestore() => Current.Game?.GetComponent<CelestialBodyManager>()?.RetryCelestialRestore();
    }

    internal sealed class Window_CelestialFrameworkDebug : Window
    {
        private readonly Game game = Current.Game;
        private readonly CelestialBodyManager? manager = Current.Game?.GetComponent<CelestialBodyManager>();
        private CelestialRuntime? runtime;
        private Vector2 scroll;
        private string status = "";
        private string? lastOperation;
        public override Vector2 InitialSize => new(1000, 720);
        internal Window_CelestialFrameworkDebug() { doCloseX = true; draggable = true; absorbInputAroundWindow = true; }
        private static string NewOperation() => "debug/" + Guid.NewGuid().ToString("N");
        private CelestialOperationContext Context() => new(NewOperation(), runtime!.WorldSessionId, runtime.CaptureCatalog().Revision);
        public override void DoWindowContents(Rect rect)
        {
            if (!ReferenceEquals(Current.Game, game)) { Close(); return; }
            Text.Font = GameFont.Medium;
            Widgets.Label(new(0, 0, rect.width, 34), "PF_Celestial_Title".Translate());
            Text.Font = GameFont.Small;
            if (manager?.TryGetCelestialRuntime(out var current) != true)
            {
                Widgets.Label(new(0, 42, rect.width, 100), "PF_Celestial_TargetUnavailable".Translate() + "\n" +
                    string.Join(", ", manager?.CelestialRestoreErrors.Select(error => error.Code) ?? Array.Empty<string>()));
                return;
            }
            if (runtime != null && !ReferenceEquals(runtime, current)) { Close(); return; }
            runtime = current;
            if (Widgets.ButtonText(new(0, 42, 270, 30), "PF_Celestial_Create".Translate()))
                Find.WindowStack.Add(new FloatMenu(DefDatabase<CelestialBodyDef>.AllDefsListForReading.OrderBy(def => def.defName, StringComparer.Ordinal)
                    .Select(def => new FloatMenuOption(def.LabelCap + " (" + def.defName + ")", () => Create(def))).ToList()));
            if (Widgets.ButtonText(new(280, 42, 250, 30), "PF_Celestial_ApplyBoundary".Translate()))
            {
                var stamp = runtime.CurrentStamp;
                var result = runtime.Advance(new(runtime.WorldSessionId, stamp.TopologyRevision, stamp.PhysicalSeconds, stamp.PhysicalSeconds));
                status = result.Success ? "PF_Celestial_BoundaryApplied".Translate() : string.Join(", ", result.Errors.Select(error => error.Code));
            }
            var stampNow = runtime.CurrentStamp;
            Widgets.Label(new(0, 82, rect.width, 24), $"session={runtime.WorldSessionId:N}  t={stampNow.PhysicalSeconds:F6}  topology={stampNow.TopologyRevision}  state={stampNow.StateRevision}");
            if (lastOperation != null)
            {
                var operation = runtime.GetOperation(lastOperation);
                status = "PF_Celestial_Operation".Translate(operation.OperationId, operation.Status.ToString()) + " " + string.Join(", ", operation.Errors.Select(error => error.Code));
            }
            string diagnostics = string.Join(", ", manager.CelestialRestoreErrors.Concat(game.GetComponent<SpaceGameComp>().CelestialPhysicsErrors).Select(error => error.Code));
            Widgets.Label(new(0, 108, rect.width, 52), status + (diagnostics.Length == 0 ? "" : "\n" + diagnostics));
            var bodies = runtime.Catalog.CaptureInstances().OrderBy(body => body.Description.Handle).ToArray();
            var view = new Rect(0, 0, rect.width - 20, bodies.Length * 116);
            Widgets.BeginScrollView(new(0, 166, rect.width, rect.height - 174), ref scroll, view);
            float y = 0;
            foreach (var body in bodies)
            {
                var description = body.Description;
                Widgets.Label(new(0, y, view.width - 200, 24), $"{description.Handle}  {body.Id.Value}");
                Widgets.Label(new(0, y + 24, view.width - 200, 24), $"{description.DefinitionId}  {body.Lifecycle}  r={description.RadiusMeters:R} m  GM={description.GravitationalParameter:R}");
                Widgets.Label(new(0, y + 48, view.width - 200, 42), string.Join(", ", body.Errors.Select(error => error.Code)));
                if (Widgets.ButtonText(new(view.width - 190, y, 185, 28), "PF_Celestial_RequestSurface".Translate())) Surface(body.Id, description.Handle);
                if (Widgets.ButtonText(new(view.width - 190, y + 32, 185, 28), "PF_Celestial_RequestMap".Translate())) Map(body.Id, description.Handle);
                if (Widgets.ButtonText(new(view.width - 190, y + 64, 185, 28), "PF_Celestial_Remove".Translate()))
                {
                    var operation = runtime.Remove(new(Context(), body.Id));
                    lastOperation = operation.OperationId;
                }
                y += 116;
            }
            Widgets.EndScrollView();
        }
        private bool CurrentSession() => ReferenceEquals(Current.Game, game) && manager?.TryGetCelestialRuntime(out var current) == true && ReferenceEquals(runtime, current);
        private void Create(CelestialBodyDef definition)
        {
            if (!CurrentSession()) return;
            var template = definition.ToTemplate();
            if (!template.Success) { status = string.Join(", ", template.Errors.Select(error => error.Code)); return; }
            var context = Context();
            var id = CelestialInstanceId.Parse(context.OperationId);
            var components = template.Value.Resolve(new(runtime!.WorldSeed, id.Value));
            if (!components.Success) { status = string.Join(", ", components.Errors.Select(error => error.Code)); return; }
            var root = runtime.Creation.RootSurfaceId;
            var references = new System.Collections.Generic.Dictionary<string, CelestialInstanceId>();
            if (root.HasValue)
            {
                references["frameOrigin"] = root.Value;
                if (runtime.TryGetInitializationReference(root.Value, "primaryLight", out var light)) references["primaryLight"] = light;
            }
            var item = new CelestialSpawnItem(id, id.Value, 0, id.Value, template.Value, components.Value, root, references);
            lastOperation = runtime.CreateBatch(new(context, new("debug", runtime.WorldSeed, null, new[] { item }))).OperationId;
        }
        private void Surface(CelestialInstanceId id, int handle)
        {
            var token = manager!.CaptureTarget(handle);
            if (!token.Success) { status = string.Join(", ", token.Errors.Select(error => error.Code)); return; }
            LongEventHandler.QueueLongEvent(() => {
                if (!CurrentSession() || !manager.ValidateTarget(token.Value).Success) return;
                var result = runtime!.RequestSurface(new(Context(), id));
                lastOperation = result.Operation.OperationId;
                if (result.Handle != null && manager.ValidateTarget(token.Value).Success && Find.WorldGrid.PlanetLayers.TryGetValue(result.Handle.LayerId, out var layer))
                    PlanetLayer.Selected = layer;
            }, "PF_Celestial_RequestSurface", false, null);
        }
        private void Map(CelestialInstanceId id, int handle)
        {
            var token = manager!.CaptureTarget(handle);
            var selected = Find.WorldSelector.SelectedTile;
            if (!token.Success || selected.tileId < 0 || selected.Layer is not CelestialSurfaceLayer owned || owned.Header.OwnerId != id)
            { status = "PF_Celestial_SelectOwnedTile".Translate(); return; }
            var address = new CelestialMapAddress(id, owned.LayerID, selected.tileId);
            LongEventHandler.QueueLongEvent(() => {
                if (!CurrentSession() || !manager.ValidateTarget(token.Value).Success ||
                    !Find.WorldGrid.PlanetLayers.TryGetValue(owned.LayerID, out var layer) || !ReferenceEquals(layer, owned)) return;
                var result = runtime!.RequestMap(new(Context(), address, CelestialMapAction.Generate, 250, 250));
                lastOperation = result.Operation.OperationId;
                if (result.Handle != null && manager.ValidateTarget(token.Value).Success && runtime.Maps!.TryGetNativeMap(result.Handle.MapId, out var map))
                    Current.Game.CurrentMap = map;
            }, "PF_Celestial_RequestMap", false, null);
        }
    }
}
