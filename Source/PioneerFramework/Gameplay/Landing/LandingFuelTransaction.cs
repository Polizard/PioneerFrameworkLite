using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace PioneerFramework.Landing
{
    public static class LandingFuelTransaction
    {
        public static bool TryRun(Action action, Action rollback, out string error)
        {
            try { action(); error = ""; return true; }
            catch (Exception e)
            {
                error = e.Message;
                try { rollback(); } catch (Exception restore) { error += "; " + restore.Message; }
                return false;
            }
        }
        public static LandingFuelReceipt Capture(SpaceGameComp sg, SpaceEntityGroup group)
        {
            var receipt = new LandingFuelReceipt(); var mapIds = new HashSet<int>();
            foreach (int id in group.GetEntityIds().OrderBy(x => x))
            {
                var e = sg.GetEntityById(id);
                if (e == null) throw new InvalidOperationException("Missing fuel source.");
                if (e.MapId >= 0)
                {
                    if (!mapIds.Add(e.MapId)) continue;
                    var map = LandingSourceMapService.FindMap(e.MapId) ?? throw new InvalidOperationException("Missing fuel map.");
                    var mc = map.GetComponent<SpaceMapComp>();
                    if (mc == null) continue;
                    foreach (var pool in mc.PropellantSnapshot.ToList())
                    {
                        var debit = new LandingFuelDebit { MapId = map.uniqueID, PropellantDefName = pool.Key.defName, Before = pool.Value };
                        foreach (var tank in map.listerThings.AllThings.Select(t => t.TryGetComp<CompPropellantTank>()).Where(t => t != null && t.PropellantDef == pool.Key))
                        { debit.TankIds.Add(tank.parent.ThingID); debit.TankMasses.Add(tank.LandingStoredMass); }
                        receipt.Debits.Add(debit);
                    }
                }
                else for (int i = 0; i < e.propulsion.Count; i++)
                    receipt.Debits.Add(new LandingFuelDebit { EntityId = id, EntryIndex = i, PropellantDefName = e.propulsion[i].propellant.defName, Before = e.propulsion[i].mass });
            }
            return receipt;
        }
        public static bool TryDebit(SpaceGameComp sg, SpaceEntityGroup group, double deltaV, out LandingFuelReceipt receipt, out LandingReport report)
        {
            receipt = Capture(sg, group); receipt.QuotedDeltaV = deltaV;
            var saved = receipt;
            bool ok = TryRun(() =>
            {
                double required = sg.LandingRequiredFuel(group, deltaV);
                saved.ConsumedKg = sg.ConsumeLandingDeltaV(group, deltaV);
                if (saved.ConsumedKg + Math.Max(1e-4, required * 1e-6) < required) throw new InvalidOperationException("Insufficient actual propellant debit.");
                foreach (var d in saved.Debits)
                    d.After = d.MapId >= 0 ? LandingSourceMapService.FindMap(d.MapId)!.GetComponent<SpaceMapComp>().GetPropellantMass(DefDatabase<PropellantDef>.GetNamed(d.PropellantDefName)) : sg.GetEntityById(d.EntityId).propulsion[d.EntryIndex].mass;
            }, () => { var result = Rollback(saved); if (!result.Success) throw new InvalidOperationException(result.Text); }, out string error);
            report = ok ? LandingReport.Ok() : LandingReport.Reject("PFL_LandingExecutionError", error); return ok;
        }
        public static LandingReport Rollback(LandingFuelReceipt receipt) => Rollback(receipt, Current.Game?.GetComponent<SpaceGameComp>());
        public static LandingReport Rollback(LandingFuelReceipt receipt, SpaceGameComp? sg)
        {
            try
            {
                if (sg == null) throw new InvalidOperationException("Missing space game.");
                foreach (var d in receipt.Debits)
                {
                    if (d.MapId >= 0)
                    {
                        var map = LandingSourceMapService.FindMap(d.MapId) ?? throw new InvalidOperationException("Missing fuel map.");
                        var mc = map.GetComponent<SpaceMapComp>(); var def = DefDatabase<PropellantDef>.GetNamed(d.PropellantDefName);
                        mc.AddPropellantMass(def, d.Before - mc.GetPropellantMass(def));
                        for (int i = 0; i < d.TankIds.Count; i++)
                        {
                            var tank = map.listerThings.AllThings.FirstOrDefault(t => t.ThingID == d.TankIds[i])?.TryGetComp<CompPropellantTank>();
                            if (tank != null) tank.LandingStoredMass = d.TankMasses[i];
                        }
                    }
                    else
                    {
                        var entity = sg.GetEntityById(d.EntityId) ?? throw new InvalidOperationException("Missing fuel entity.");
                        entity.propulsion[d.EntryIndex].mass = d.Before; entity.InvalidatePropulsion();
                    }
                }
                receipt.Committed = false; return LandingReport.Ok();
            }
            catch (Exception e) { return LandingReport.Reject("PFL_LandingExecutionError", e.Message); }
        }
    }
}
