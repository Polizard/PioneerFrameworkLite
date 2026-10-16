using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Landing
{
    public static class LandingRecoveryService
    {
        public static LandingRecoveryDef? SelectConfiguration(LandingEntityPayload payload, IEnumerable<LandingRecoveryDef> configurations)
        {
            var configs = configurations.ToList();
            return configs.Where(d => d.partDefNames.Count > 0 && payload.Entity != null &&
                    d.partDefNames.All(n => payload.Entity.Parts.Any(p => !p.isDestroyed && p.defName == n)))
                .OrderByDescending(d => d.partDefNames.Count).ThenBy(d => d.defName, StringComparer.Ordinal).FirstOrDefault()
                ?? configs.FirstOrDefault(d => d.defName == "PF_GenericLandingRecovery");
        }
        public static LandingRecoveryDef? ConfigurationFor(LandingEntityPayload? payload) => payload == null ? null :
            SelectConfiguration(payload, DefDatabase<LandingRecoveryDef>.AllDefsListForReading);
        public static bool TryCreateSite(LandingTaskData task, out LandedGroupWorldObject? site, out LandingReport report)
        {
            site = Find.WorldObjects.AllWorldObjects.OfType<LandedGroupWorldObject>().FirstOrDefault(s => s.TaskId == task.TaskId);
            if (task.MaplessEntityIds.Count == 0) { report = LandingReport.Ok(); return true; }
            try
            {
                if (site == null)
                {
                    site = (LandedGroupWorldObject)WorldObjectMaker.MakeWorldObject(DefDatabase<WorldObjectDef>.GetNamed("PF_LandedGroup"));
                    site.TaskId = task.TaskId; site.Tile = task.TargetTile; site.SetFaction(Faction.OfPlayer);
                    Find.WorldObjects.Add(site);
                }
                task.RecoverySiteId = site.ID;
                var sg = Current.Game.GetComponent<SpaceGameComp>();
                foreach (int id in task.MaplessEntityIds)
                {
                    string entryId = task.TaskId + ":entity:" + id;
                    var entry = site.Entries.FirstOrDefault(e => e.EntryId == entryId);
                    if (entry?.TransferredFromSpace == true) continue;
                    var entity = sg.GetEntityById(id) ?? throw new InvalidOperationException("Missing recovery entity " + id);
                    if (entry == null) { entry = new LandingRecoveryEntry { EntryId = entryId, EntityId = id }; site.Entries.Add(entry); }
                    entry.Payload = new LandingEntityPayload { EntryId = entryId, EntityId = id, Entity = entity, DryMass = entity.TotalMass, FuelMass = entity.TotalPropellantMass };
                    try { sg.UnregisterEntity(entity); }
                    finally { entry.TransferredFromSpace = sg.GetEntityById(id) == null; }
                }
                report = LandingReport.Ok(); return true;
            }
            catch (Exception e) { report = LandingReport.Reject("PFL_LandingExecutionError", e.Message); return false; }
        }
        private static bool HasEntry(Caravan caravan, string id) => CaravanInventoryUtility.AllInventoryItems(caravan).OfType<RecoveredSpacecraft>().Any(t => t.Payload?.EntryId == id);
        public static bool TryRecover(LandedGroupWorldObject site, Caravan caravan, out LandingRecoveryReport report)
        {
            report = new LandingRecoveryReport();
            if (caravan.Tile != site.Tile || caravan.Faction != Faction.OfPlayer)
            { report.Result = LandingReport.Reject("PFL_LandingRecoveryUnavailable"); return false; }
            report.Result = Reconcile(site);
            if (!report.Result.Success) return false;
            foreach (var entry in site.Entries.Where(e => e.TransferredFromSpace && e.State != LandingHandoffState.Delivered).ToList())
            {
                try
                {
                    if (HasEntry(caravan, entry.EntryId)) { entry.HandOff(id => HasEntry(caravan, id), _ => { }); continue; }
                    if (entry.Payload == null || caravan.MassUsage + entry.Payload.Mass > caravan.MassCapacity) continue;
                    entry.ReceiverCaravanId = caravan.ID;
                    if (entry.HandOff(id => HasEntry(caravan, id), payload =>
                    {
                        var config = ConfigurationFor(payload);
                        var def = config?.packagedThingDef ?? DefDatabase<ThingDef>.GetNamed("PF_RecoveredSpacecraft");
                        if (def.stackLimit != 1) throw new InvalidOperationException("Recovery ThingDef must have stackLimit 1.");
                        var item = ThingMaker.MakeThing(def) as RecoveredSpacecraft ?? throw new InvalidOperationException("Recovery ThingDef must use RecoveredSpacecraft.");
                        item.Payload = payload;
                        var pawn = caravan.PawnsListForReading.FirstOrDefault(p => p.inventory != null && !p.Dead);
                        if (pawn == null || !pawn.inventory.innerContainer.TryAdd(item, false)) throw new InvalidOperationException("No receiving inventory.");
                    })) report.TransferredEntryIds.Add(entry.EntryId);
                }
                catch (Exception e)
                {
                    if (HasEntry(caravan, entry.EntryId)) entry.HandOff(id => HasEntry(caravan, id), _ => { });
                    report.Result = LandingReport.Reject("PFL_LandingExecutionError", e.Message); break;
                }
            }
            caravan.RecacheInventory();
            report.RemainingCount = site.Entries.Count(e => e.State != LandingHandoffState.Delivered);
            if (report.RemainingCount == 0) site.Destroy();
            else Messages.Message("PFL_LandingRecoveryRemaining".Translate(report.RemainingCount), MessageTypeDefOf.NeutralEvent, false);
            return report.Result.Success;
        }
        public static LandingReport Reconcile(LandedGroupWorldObject site)
        {
            return Reconcile(site.Entries, (caravanId, entryId) =>
            {
                var caravan = Find.WorldObjects.Caravans.FirstOrDefault(c => c.ID == caravanId);
                return caravan == null ? (bool?)null : HasEntry(caravan, entryId);
            });
        }
        public static LandingReport Reconcile(IEnumerable<LandingRecoveryEntry> entries, Func<int, string, bool?> receiverHas)
        {
            foreach (var entry in entries.Where(e => e.State == LandingHandoffState.HandingOff))
            {
                bool? has = receiverHas(entry.ReceiverCaravanId, entry.EntryId);
                if (!has.HasValue) return LandingReport.Reject("PFL_LandingRecoveryUnavailable");
                if (has.Value) entry.HandOff(_ => true, _ => { });
                else { entry.State = LandingHandoffState.Available; entry.ReceiverCaravanId = -1; }
            }
            return LandingReport.Ok();
        }
    }
}
