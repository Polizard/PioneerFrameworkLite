using System.Linq;
using System;
using PioneerFramework.Landing;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public class Window_LandingTarget : Window
    {
        private readonly int groupId;
        private readonly LandingTargetSession session;
        private PlanetTile observedTile;
        private bool collapsed, preparing;
        private Vector2 scroll;
        private string error = "";
        private readonly System.Collections.Generic.List<EntitySnapshot> entityBuf = new();

        public Window_LandingTarget(int targetGroupId)
        {
            groupId = targetGroupId;
            var mgr = Current.Game.GetComponent<CelestialBodyManager>();
            int body;
            if (PhysicsApi.TryGetSpacecraft(groupId, out var craft) && mgr.GetBodyById(craft.CelestialBodyId)?.hasSurface == true)
                body = craft.CelestialBodyId;
            else
                body = mgr.GetAllBodies().First(b => b.hasSurface).bodyId;
            session = new LandingTargetSession(body);
            forcePause = true; closeOnAccept = false; closeOnCancel = true; closeOnClickedOutside = false;
            absorbInputAroundWindow = false; preventCameraMotion = false; draggable = true; doCloseX = true;
            observedTile = Find.WorldSelector.SelectedTile;
            SwitchSurfaceView(body);
            if (observedTile.Valid && observedTile.Layer == Launch.GetSurfaceState.GetLayerForBody(mgr, body)) session.SelectTile(observedTile);
        }
        public override Vector2 InitialSize => new Vector2(Math.Min(340, UI.screenWidth - 24), Math.Min(440, UI.screenHeight - 100));
        protected override void SetInitialSizeAndPosition()
        { base.SetInitialSizeAndPosition(); windowRect.x = UI.screenWidth - windowRect.width - 14; windowRect.y = 80; }
        public override void PreClose() { session.Close(); base.PreClose(); }
        private static void SwitchSurfaceView(int bodyId)
        {
            var layer = Launch.GetSurfaceState.GetLayerForBody(Current.Game.GetComponent<CelestialBodyManager>(), bodyId);
            if (layer != null) { CameraJumper.TryShowWorld(); PlanetLayer.Selected = layer; }
        }
        public override void DoWindowContents(Rect inRect)
        {
            if (!PhysicsApi.TryGetSpacecraft(groupId, out var craft) || !craft.IsActive) { Close(); return; }
            var mgr = Current.Game.GetComponent<CelestialBodyManager>();
            var selected = Find.WorldSelector.SelectedTile;
            if (selected != observedTile)
            {
                observedTile = selected;
                var layer = Launch.GetSurfaceState.GetLayerForBody(mgr, session.BodyId);
                session.SelectTile(selected.Valid && selected.Layer == layer ? selected : PlanetTile.Invalid);
            }
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0, 0, inRect.width - 80, 28), "PFL_LandingTitle".Translate(groupId));
            if (Widgets.ButtonText(new Rect(inRect.width - 78, 0, 50, 25), collapsed ? "+" : "−"))
            { collapsed = !collapsed; windowRect.height = collapsed ? 90 : Math.Min(440, UI.screenHeight - 100); }
            if (collapsed) return;
            entityBuf.Clear();
            PhysicsApi.CopyEntities(groupId, entityBuf);
            bool requiresPreparation = entityBuf.Exists(e => e.IsMapArchived);
            float width = inRect.width - 18;
            Widgets.BeginScrollView(new Rect(0, 34, inRect.width, inRect.height - 35), ref scroll, new Rect(0, 0, width, 390));
            float y = 0;
            Widgets.Label(new Rect(0, y, width, 48), "PFL_LandingGuide".Translate()); y += 50;
            if (Widgets.ButtonText(new Rect(0, y, width, 28), mgr.GetBodyName(session.BodyId)))
                Find.WindowStack.Add(new FloatMenu(mgr.GetAllBodies().Where(b => b.hasSurface).Select(b => new FloatMenuOption(b.label ?? b.defName, () =>
                { session.SelectBody(b.bodyId); observedTile = Find.WorldSelector.SelectedTile; SwitchSurfaceView(b.bodyId); })).ToList()));
            y += 34;
            bool quoted = false;
            if (requiresPreparation)
            {
                Widgets.Label(new Rect(0, y, width, 100), "PFL_LandingPrepareSourceGuide".Translate()); y += 115;
            }
            else if (session.HasSelection && PhysicsQueries.TryQuoteLanding(groupId, session.BodyId, session.Tile,
                         out double deltaV, out float requiredPropellant, out float remainingDv,
                         out string bodyName, out string spot, out _))
            {
                Widgets.Label(new Rect(0, y, width, 48), "PFL_LandingSelectedTile".Translate(bodyName, spot, session.Tile.tileId)); y += 50;
                Widgets.Label(new Rect(0, y, width, 65), "PFL_LandingCost".Translate(deltaV.ToString("F0"), requiredPropellant.ToString("F0"), remainingDv.ToString("F0"))); y += 66;
                var allowed = LandingDestinationService.Check(session.Tile, session.BodyId, new IntVec2(250, 250));
                quoted = allowed.Success;
                Widgets.Label(new Rect(0, y, width, 48), allowed.Success ? "PFL_LandingDuration".Translate(180).ToString() : allowed.Text); y += 50;
            }
            else
            {
                string? reason = "PFL_LandingSelectTile".Translate();
                if (session.HasSelection) PhysicsQueries.TryQuoteLanding(groupId, session.BodyId, session.Tile,
                    out _, out _, out _, out _, out _, out reason);
                Widgets.Label(new Rect(0, y, width, 100), reason); y += 115;
            }
            var action = session.GetAction(requiresPreparation, quoted);
            bool oldEnabled = GUI.enabled; GUI.enabled = action != LandingTargetAction.None && !preparing;
            string buttonKey = preparing ? "PFL_LandingPreparing" : action == LandingTargetAction.PrepareMaps ? "PFL_LandingPrepareSource" : "PFL_LandingStart";
            if (Widgets.ButtonText(new Rect(0, y, width, 34), buttonKey.Translate())) Start(action == LandingTargetAction.BeginDescent);
            GUI.enabled = oldEnabled; y += 38;
            Widgets.Label(new Rect(0, y, width, 70), error);
            Widgets.EndScrollView();
        }
        private void Start(bool startDescent)
        {
            if (preparing) return;
            preparing = true; int token = session.Token;
            int bodyId = session.BodyId; PlanetTile tile = session.Tile;
            PhysicsCommands.BeginLandingPrepare(groupId, () =>
            {
                preparing = false;
                if (!session.Accepts(token) || !startDescent) return;
                if (PhysicsCommands.LandAtTile(groupId, bodyId, tile, out string? reason)) Close();
                else error = reason ?? "";
            }, report => { preparing = false; if (session.Accepts(token)) error = report; });
        }
    }
}
