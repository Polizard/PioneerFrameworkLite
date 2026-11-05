using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.Landing
{
    public sealed class LandedGroupWorldObject : WorldObject
    {
        public string TaskId = "";
        public List<LandingRecoveryEntry> Entries = new List<LandingRecoveryEntry>();
        private LandingRecoveryDef? DisplayConfiguration
        {
            get
            {
                var configs = Entries.Where(e => e.State != LandingHandoffState.Delivered && e.Payload != null)
                    .Select(e => LandingRecoveryService.ConfigurationFor(e.Payload)).Distinct().Take(2).ToList();
                return configs.Count == 1 && configs[0]?.partDefNames.Count > 0 ? configs[0] : null;
            }
        }
        public override string Label => DisplayConfiguration is LandingRecoveryDef config && !string.IsNullOrEmpty(config.label) ? config.label : base.Label;
        public override Material Material => DisplayConfiguration is LandingRecoveryDef config && !string.IsNullOrEmpty(config.texturePath)
            ? MaterialPool.MatFrom(config.texturePath, ShaderDatabase.WorldOverlayCutout) : base.Material;
        public override Texture2D ExpandingIcon => DisplayConfiguration == null ? base.ExpandingIcon : (Texture2D)Material.mainTexture;
        public override void ExposeData()
        { base.ExposeData(); Scribe_Values.Look(ref TaskId, "taskId"); Scribe_Collections.Look(ref Entries, "recoveryEntries", LookMode.Deep); }
        public override string GetInspectString() => LandingWorldObjectPresentation.JoinInspectLines(base.GetInspectString(), "PFL_LandingRecoveryRemaining".Translate(Entries.Count(e => e.State != LandingHandoffState.Delivered)));
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;
            var map = LandingDestinationService.FindExisting(Tile);
            if (map != null) yield return new Command_Action { defaultLabel = "PFL_LandingViewSite".Translate(), action = () => CameraJumper.TryJump(map.Center, map) };
            foreach (var caravan in Find.WorldObjects.Caravans.Where(c => c.Tile == Tile && c.Faction == Faction.OfPlayer).ToArray())
            {
                var receiver = caravan;
                yield return new Command_Action { defaultLabel = "PFL_LandingRecover".Translate(receiver.Label), action = () => LandingRecoveryService.TryRecover(this, receiver, out _) };
            }
        }
        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan)
        {
            foreach (var option in base.GetFloatMenuOptions(caravan)) yield return option;
            yield return new FloatMenuOption("PFL_LandingRecover".Translate(Label), () => caravan.pather.StartPath(Tile, new CaravanArrivalAction_RecoverLandedGroup(ID), true));
        }
    }
}
