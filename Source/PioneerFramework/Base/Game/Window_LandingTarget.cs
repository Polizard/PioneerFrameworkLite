using System;
using System.Linq;
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
        public Window_LandingTarget(SpaceGameComp sg, SpaceEntityGroup group)
        {
            groupId = group.GroupId;
            var mgr = Current.Game.GetComponent<CelestialBodyManager>();
            int body = mgr.GetBodyById(group.CelestialBodyId)?.hasSurface == true ? group.CelestialBodyId : mgr.GetAllBodies().First(b => b.hasSurface).bodyId;
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
            var sg = Current.Game.GetComponent<SpaceGameComp>(); var group = sg.GetGroupById(groupId);
            if (group == null || !group.IsActive) { Close(); return; }
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0, 0, inRect.width - 80, 28), "PFL_LandingTitle".Translate(groupId));
            if (Widgets.ButtonText(new Rect(inRect.width - 78, 0, 50, 25), collapsed ? "+" : "−"))
            { collapsed = !collapsed; windowRect.height = collapsed ? 90 : Math.Min(440, UI.screenHeight - 100); }
            if (collapsed) return;
            var mgr = Current.Game.GetComponent<CelestialBodyManager>();
            var selected = Find.WorldSelector.SelectedTile;
            if (selected != observedTile)
            {
                observedTile = selected;
                var layer = Launch.GetSurfaceState.GetLayerForBody(mgr, session.BodyId);
                if (selected.Valid && selected.Layer == layer) session.SelectTile(selected);
            }
            float width = inRect.width - 18;
            Widgets.BeginScrollView(new Rect(0, 34, inRect.width, inRect.height - 35), ref scroll, new Rect(0, 0, width, 390));
            float y = 0;
            Widgets.Label(new Rect(0, y, width, 48), "PFL_LandingGuide".Translate()); y += 50;
            if (Widgets.ButtonText(new Rect(0, y, width, 28), mgr.GetBodyName(session.BodyId)))
                Find.WindowStack.Add(new FloatMenu(mgr.GetAllBodies().Where(b => b.hasSurface).Select(b => new FloatMenuOption(b.label ?? b.defName, () =>
                { session.SelectBody(b.bodyId); observedTile = Find.WorldSelector.SelectedTile; SwitchSurfaceView(b.bodyId); })).ToList()));
            y += 34;
            bool quoted = false;
            if (session.HasSelection && sg.TryQuoteLanding(group, session.BodyId, session.Tile, out var q, out var estimate, out var bodyName, out var spot, out _))
            {
                Widgets.Label(new Rect(0, y, width, 48), "PFL_LandingSelectedTile".Translate(bodyName, spot, session.Tile.tileId)); y += 50;
                Widgets.Label(new Rect(0, y, width, 65), "PFL_LandingCost".Translate(q.deltaV.ToString("F0"), estimate.RequiredPropellant.ToString("F0"), sg.GetGroupDeltaV(group).remainingDv.ToString("F0"))); y += 66;
                var allowed = LandingDestinationService.Check(session.Tile, session.BodyId, new IntVec2(250, 250));
                quoted = allowed.Success;
                Widgets.Label(new Rect(0, y, width, 48), allowed.Success ? "PFL_LandingDuration".Translate(180).ToString() : allowed.Text); y += 50;
            }
            else
            {
                string? reason = "PFL_LandingSelectTile".Translate();
                if (session.HasSelection) sg.TryQuoteLanding(group, session.BodyId, session.Tile, out _, out _, out _, out _, out reason);
                Widgets.Label(new Rect(0, y, width, 100), reason); y += 115;
            }
            bool oldEnabled = GUI.enabled; GUI.enabled = quoted && !preparing;
            if (Widgets.ButtonText(new Rect(0, y, width, 34), (preparing ? "PFL_LandingPreparing" : "PFL_LandingStart").Translate())) Start(sg, group);
            GUI.enabled = oldEnabled; y += 38;
            Widgets.Label(new Rect(0, y, width, 70), error);
            Widgets.EndScrollView();
        }
        private void Start(SpaceGameComp sg, SpaceEntityGroup group)
        {
            if (preparing) return;
            preparing = true; int token = session.Token;
            LandingSourceMapService.BeginPrepare(sg, group, () =>
            {
                preparing = false;
                if (!session.Accepts(token)) return;
                if (sg.TryLandAtTile(group, session.BodyId, session.Tile, out string? reason)) Close();
                else error = reason ?? "";
            }, report => { preparing = false; if (session.Accepts(token)) error = report.Text; });
        }
    }
}
