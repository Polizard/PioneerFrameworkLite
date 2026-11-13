using System;
using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using PioneerFramework.Celestial;
using RimWorld;
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
        [DebugAction("PFL Celestial", "Planet kind test panel", allowedGameStates = AllowedGameStates.Playing)]
        public static void PlanetKinds() => Find.WindowStack.Add(new Window_CelestialFrameworkDebug(true));
        [DebugAction("PFL Celestial", "Create all six planet test kinds", allowedGameStates = AllowedGameStates.Playing)]
        public static void CreatePlanetKinds() => Find.WindowStack.Add(new Window_CelestialFrameworkDebug(true, true));
    }

    internal sealed class Window_CelestialFrameworkDebug : Window
    {
        private readonly Game game = Current.Game;
        private readonly CelestialBodyManager? manager = Current.Game?.GetComponent<CelestialBodyManager>();
        private CelestialRuntime? runtime;
        private readonly bool planetKindsOnly;
        private bool createAllOnOpen;
        private PlanetKindDebugSession? kindSession;
        private Vector2 scroll;
        private string status = "";
        private string? lastOperation;
        public override Vector2 InitialSize => new(1000, 720);
        internal Window_CelestialFrameworkDebug(bool planetKindsOnly = false, bool createAllOnOpen = false)
        {
            this.planetKindsOnly = planetKindsOnly;
            this.createAllOnOpen = createAllOnOpen;
            doCloseX = true; draggable = true; absorbInputAroundWindow = true;
        }
        private static string NewOperation() => "debug/" + Guid.NewGuid().ToString("N");
        private CelestialOperationContext Context() => new(NewOperation(), runtime!.WorldSessionId, runtime.CaptureCatalog().Revision);
        private CelestialOperationContext? ActionContext()
        {
            if (!planetKindsOnly) return Context();
            var result = kindSession!.Context();
            if (result.Success) return result.Value;
            ReportFailure(result.Errors);
            return null;
        }
        private void Remember(CelestialOperation operation)
        {
            lastOperation = operation.OperationId;
            if (planetKindsOnly) { kindSession!.Track(operation); kindSession.Refresh(); }
            if (operation.Status == CelestialOperationStatus.Rejected) ReportFailure(operation.Errors);
        }
        private static string ErrorText(IEnumerable<CelestialError> errors) => string.Join("; ", errors.Select(error =>
            error.Code + (string.IsNullOrWhiteSpace(error.Detail) ? "" : ": " + error.Detail)));
        internal void ReportFailure(IEnumerable<CelestialError> errors)
        {
            lastOperation = null;
            status = ErrorText(errors);
            Log.Warning("[PFL Celestial Debug] " + status);
        }
        public override void DoWindowContents(Rect rect)
        {
            var font = Text.Font; bool wrap = Text.WordWrap; var anchor = Text.Anchor;
            var color = GUI.color; var contentColor = GUI.contentColor; var backgroundColor = GUI.backgroundColor; bool enabled = GUI.enabled;
            try { Text.WordWrap = true; DrawContents(rect); }
            finally { Text.Font = font; Text.WordWrap = wrap; Text.Anchor = anchor;
                GUI.color = color; GUI.contentColor = contentColor; GUI.backgroundColor = backgroundColor; GUI.enabled = enabled; }
        }
        private void DrawContents(Rect rect)
        {
            if (!Prefs.DevMode || !ReferenceEquals(Current.Game, game)) { Close(); return; }
            Text.Font = GameFont.Medium;
            Widgets.Label(new(0, 0, rect.width, 34), (planetKindsOnly ? "PF_PlanetKind_DebugTitle" : "PF_Celestial_Title").Translate());
            Text.Font = GameFont.Small;
            if (manager?.TryGetCelestialRuntime(out var current) != true)
            {
                Widgets.Label(new(0, 42, rect.width, 100), "PF_Celestial_TargetUnavailable".Translate() + "\n" +
                    string.Join(", ", manager?.CelestialRestoreErrors.Select(error => error.Code) ?? Array.Empty<string>()));
                return;
            }
            if (runtime != null && !ReferenceEquals(runtime, current)) { Close(); return; }
            runtime = current;
            if (planetKindsOnly)
            {
                if (kindSession == null)
                {
                    kindSession = PlanetKindDebugSession.For(runtime);
                    lastOperation = kindSession.LastOperation?.OperationId;
                }
                kindSession.Refresh();
                if (createAllOnOpen) { createAllOnOpen = false; CreateAllKinds(); }
            }
            if (Widgets.ButtonText(new(0, 42, 270, 30), "PF_Celestial_Create".Translate()))
                Find.WindowStack.Add(new FloatMenu(DefDatabase<CelestialBodyDef>.AllDefsListForReading.OrderBy(def => def.defName, StringComparer.Ordinal)
                    .Where(def => !planetKindsOnly || PlanetKindDebugScenario.DefinitionIds.Contains(def.defName, StringComparer.Ordinal))
                    .SelectMany(def => (planetKindsOnly ? PlanetKindDebugScenario.Variants(def.defName) : new[] { "Default" })
                        .Select(variant => new FloatMenuOption(def.LabelCap + " / " + ("PF_PlanetKind_Variant_" + variant).Translate(),
                            () => { if (planetKindsOnly) Remember(kindSession!.Create(new[] { def }, variant)); else Create(def); }))).ToList()));
            if (Widgets.ButtonText(new(280, 42, 250, 30), "PF_Celestial_ApplyBoundary".Translate()))
            {
                var stamp = runtime.CurrentStamp;
                var result = runtime.Advance(new(runtime.WorldSessionId, stamp.TopologyRevision, stamp.PhysicalSeconds, stamp.PhysicalSeconds));
                status = result.Success ? "PF_Celestial_BoundaryApplied".Translate() : string.Join(", ", result.Errors.Select(error => error.Code));
                if (!result.Success) ReportFailure(result.Errors);
                kindSession?.Refresh();
            }
            if (planetKindsOnly)
            {
                if (Widgets.ButtonText(new(540, 42, 200, 30), "PF_PlanetKind_DebugCreateAll".Translate())) CreateAllKinds();
                if (Widgets.ButtonText(new(750, 42, rect.width - 750, 30), "PF_PlanetKind_DebugCleanup".Translate()))
                {
                    var operations = kindSession!.RemoveAll();
                    if (operations.Count > 0) Remember(operations[operations.Count - 1]);
                    else { status = "PF_PlanetKind_DebugEmpty".Translate(); lastOperation = null; }
                }
            }
            var stampNow = runtime.CurrentStamp;
            Widgets.Label(new(0, 82, rect.width, 24), $"session={runtime.WorldSessionId:N}  t={stampNow.PhysicalSeconds:F6}  topology={stampNow.TopologyRevision}  state={stampNow.StateRevision}");
            if (lastOperation != null)
            {
                var operation = planetKindsOnly ? kindSession!.LastOperation! : runtime.GetOperation(lastOperation);
                status = "PF_Celestial_Operation".Translate(operation.OperationId,
                    ("PF_Celestial_Operation_" + operation.Status).Translate()) + " " + ErrorText(operation.Errors);
            }
            string diagnostics = string.Join(", ", manager.CelestialRestoreErrors.Concat(game.GetComponent<SpaceGameComp>()?.CelestialPhysicsErrors ?? Array.Empty<CelestialError>())
                .Concat(kindSession?.Errors ?? Array.Empty<CelestialError>()).Select(error => ErrorText(new[] { error })).Distinct());
            Widgets.Label(new(0, 108, rect.width, 52), status + (diagnostics.Length == 0 ? "" : "\n" + diagnostics));
            float top = planetKindsOnly ? 212 : 166;
            float rowHeight = 485;
            if (planetKindsOnly) Widgets.Label(new(0, 166, rect.width, 42), "PF_PlanetKind_DebugHelp".Translate(kindSession!.PendingCount));
            var bodies = runtime.Catalog.CaptureInstances().Where(body => !planetKindsOnly || PlanetKindDebugScenario.IsTestBody(body.Description))
                .Where(body => !planetKindsOnly || body.Lifecycle != CelestialLifecycle.Removed).OrderBy(body => body.Description.Handle).ToArray();
            var view = new Rect(0, 0, rect.width - 20, Math.Max(44, bodies.Length * rowHeight));
            Widgets.BeginScrollView(new(0, top, rect.width, rect.height - top - 8), ref scroll, view);
            if (planetKindsOnly && bodies.Length == 0) Widgets.Label(new(0, 0, view.width, 44), "PF_PlanetKind_DebugEmpty".Translate());
            float y = 0;
            foreach (var body in bodies)
            {
                var description = body.Description;
                string label = DefDatabase<CelestialBodyDef>.GetNamedSilentFail(description.DefinitionId)?.LabelCap ?? description.DefinitionId;
                var labelRect = new Rect(0, y, view.width - 200, 24);
                Widgets.Label(labelRect, planetKindsOnly ? label + "  #" + description.Handle : $"{description.Handle}  {body.Id.Value}");
                if (planetKindsOnly) TooltipHandler.TipRegion(labelRect, body.Id.Value);
                Widgets.Label(new(0, y + 24, view.width - 200, 24), $"{description.DefinitionId}  {body.Lifecycle}  r={description.RadiusMeters:R} m  GM={description.GravitationalParameter:R}");
                Widgets.Label(new(0, y + 48, view.width - 200, 42), string.Join(", ", body.Errors.Select(error => error.Code)));
                if (planetKindsOnly)
                {
                    var surface = runtime.Surfaces?.Find(body.Id);
                    string surfaceState = surface?.Success == true ? "PF_PlanetKind_DebugLayerReady".Translate(surface.Value.LayerId).ToString() :
                        runtime.GetCapabilities<ICelestialSurfaceCapability>(body.Id).Count == 0 ? "PF_PlanetKind_DebugNoSurface".Translate().ToString() :
                        surface == null ? "PF_Celestial_TargetUnavailable".Translate().ToString() : ErrorText(surface.Errors);
                    Widgets.Label(new(0, y + 86, view.width - 200, 48), "PF_PlanetKind_DebugCapabilities".Translate(
                        (runtime.GetCapabilities<ICelestialSurfaceCapability>(body.Id).Count > 0 ? "PF_PlanetKind_DebugYes" : "PF_PlanetKind_DebugNo").Translate(),
                        (runtime.GetCapabilities<ICelestialMapGenerationCapability>(body.Id).Count > 0 ? "PF_PlanetKind_DebugYes" : "PF_PlanetKind_DebugNo").Translate(),
                        (runtime.GetCapabilities<ICelestialAtmosphereCapability>(body.Id).Count > 0 ? "PF_PlanetKind_DebugYes" : "PF_PlanetKind_DebugNo").Translate()) + "\n" + surfaceState);
                    if (Widgets.ButtonText(new(view.width - 190, y + 96, 185, 28), "PF_PlanetKind_DebugView".Translate())) ViewBody(body.Id, description.Handle);
                }
                bool previousEnabled = GUI.enabled;
                bool canGenerateSurface = !planetKindsOnly || runtime.GetCapabilities<ICelestialSurfaceCapability>(body.Id).Count > 0;
                GUI.enabled = previousEnabled && canGenerateSurface;
                if (Widgets.ButtonText(new(view.width - 190, y, 185, 28), "PF_Celestial_RequestSurface".Translate())) Surface(body.Id, description.Handle);
                if (Widgets.ButtonText(new(view.width - 190, y + 32, 185, 28), "PF_Celestial_RequestMap".Translate())) Map(body.Id, description.Handle);
                GUI.enabled = previousEnabled;
                if (Widgets.ButtonText(new(view.width - 190, y + 64, 185, 28), "PF_Celestial_Remove".Translate()))
                {
                    var context = ActionContext();
                    if (context != null) Remember(runtime.Remove(new(context, body.Id)));
                }
                Widgets.Label(new(0, y + 148, view.width - 20, 325), CelestialDebugInspection.Describe(runtime, body));
                y += rowHeight;
            }
            Widgets.EndScrollView();
        }
        private bool CurrentSession() => Prefs.DevMode && ReferenceEquals(Current.Game, game) && manager?.TryGetCelestialRuntime(out var current) == true && ReferenceEquals(runtime, current);
        public override void PostClose()
        {
            if (CurrentSession()) kindSession?.Refresh();
            base.PostClose();
        }
        private void CreateAllKinds()
        {
            if (!CurrentSession()) return;
            var definitions = PlanetKindDebugScenario.DefinitionIds.Select(DefDatabase<CelestialBodyDef>.GetNamedSilentFail).ToArray();
            if (definitions.Any(definition => definition == null)) { status = "PF_PlanetKind_DebugMissing".Translate(); lastOperation = null; return; }
            Remember(kindSession!.Create(definitions));
        }
        private void ViewBody(CelestialInstanceId id, int handle)
        {
            if (!CurrentSession() || !runtime!.Query(id).Success || manager!.GetBodyById(handle)?.InstanceId != id) return;
            var definition = DefDatabase<MainButtonDef>.GetNamedSilentFail("PF_View");
            if (definition?.TabWindow is not MainTabWindow_PFView view) { status = "PF_Celestial_TargetUnavailable".Translate(); lastOperation = null; return; }
            Find.MainTabsRoot.SetCurrentTab(definition);
            if (planetKindsOnly) view.FocusBodyAtAltitude(handle, 120000.0);
            else view.FocusBody(handle);
            Close();
        }
        private void Create(CelestialBodyDef definition)
        {
            if (!CurrentSession()) return;
            if (planetKindsOnly) { Remember(kindSession!.Create(new[] { definition })); return; }
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
            Remember(runtime.CreateBatch(new(context, new("debug", runtime.WorldSeed, null, new[] { item }))));
        }
        private void Surface(CelestialInstanceId id, int handle)
        {
            if (!CurrentSession()) { ReportFailure(new[] { new CelestialError("StaleSession", id) }); return; }
            var token = manager!.CaptureTarget(handle);
            if (!token.Success) { ReportFailure(token.Errors); return; }
            LongEventHandler.QueueLongEvent(() => {
                if (!CurrentSession()) { ReportFailure(new[] { new CelestialError("StaleSession", id) }); return; }
                var validated = manager.ValidateTarget(token.Value);
                if (!validated.Success) { ReportFailure(validated.Errors); return; }
                var context = ActionContext();
                if (context == null) return;
                var result = runtime!.RequestSurface(new(context, id));
                Remember(result.Operation);
                if (result.Handle != null && manager.ValidateTarget(token.Value).Success && Find.WorldGrid.PlanetLayers.TryGetValue(result.Handle.LayerId, out var layer))
                {
                    Log.Message($"[PFL Celestial Surface] Applied instance={id.Value} PlanetLayer={layer.LayerID}");
                    if (planetKindsOnly) CameraJumper.TryShowWorld();
                    PlanetLayer.Selected = layer;
                    // 2026-11-08：只选层不跳相机 ⇒ 玩家仍停在地球上方，看起来"什么都没发生"。
                    // 跳到该层第 0 格（会顺带切世界视图与当前层）。
                    CameraJumper.TryJump(new PlanetTile(0, layer), default);
                    if (planetKindsOnly) Close();
                }
            }, "PF_Celestial_RequestSurface", false, exception => ReportFailure(new[] {
                new CelestialError("SurfaceGenerationFailed", id, Detail: exception.ToString()) }));
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
                var context = ActionContext();
                if (context == null) return;
                var result = runtime!.RequestMap(new(context, address, CelestialMapAction.Generate, 250, 250));
                Remember(result.Operation);
                if (result.Handle != null && manager.ValidateTarget(token.Value).Success && runtime.Maps!.TryGetNativeMap(result.Handle.MapId, out var map))
                {
                    Current.Game.CurrentMap = map;
                    if (planetKindsOnly) { CameraJumper.TryJump(map.Center, map); Close(); }
                }
            }, "PF_Celestial_RequestMap", false, null);
        }
    }
}
