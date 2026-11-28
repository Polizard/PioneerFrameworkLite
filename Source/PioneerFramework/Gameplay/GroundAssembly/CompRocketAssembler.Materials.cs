using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PioneerFramework.GroundAssembly;

internal readonly record struct AssemblyMaterialRow(string DefName, ThingDef? Def, int Required, int Delivered)
{
    internal int Missing => Math.Max(0, Required - Delivered);
    internal string Label => Def?.LabelCap.ToString() ?? DefName;
}

public partial class CompRocketAssembler
{
    private const int CurrentMaterialOrderSchema = 1;
    private int materialOrderSchema;
    private int buildOrderVersion;
    private bool buildPaused;
    private bool refundPending;
    private string? materialMigrationError;
    private List<MaterialRequirement>? materialRequirements;
    private ThingOwner<Thing>? receivedMaterials;

    IThingHolder? IThingHolder.ParentHolder => parent?.ParentHolder;

    public ThingOwner GetDirectlyHeldThings()
        => receivedMaterials ??= new ThingOwner<Thing>(this, oneStackOnly: false);

    public void GetChildHolders(List<IThingHolder> outChildren)
    {
        if (receivedMaterials != null) ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, receivedMaterials);
    }

    internal int BuildOrderVersion => buildOrderVersion;
    internal bool IsBuildPaused => buildPaused;
    internal bool IsRefundPending => refundPending;
    private bool HasHeldMaterials => receivedMaterials is { Count: > 0 };

    internal bool TryQuoteAssemblyMaterials(out List<ThingDefCountClass> materials, out string reason)
        => RocketAssemblyMaterials.TryQuote(Design, out materials, out reason);

    internal bool UsesDefaultAssemblyCost(string name)
        => DefDatabase<ShipPartDef>.GetNamedSilentFail(name) is { assemblyCost: null };

    internal IEnumerable<AssemblyMaterialRow> MaterialRows()
    {
        if (materialRequirements == null) yield break;
        foreach (MaterialRequirement requirement in materialRequirements)
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(requirement.DefName);
            int delivered = def == null ? 0 : receivedMaterials?.TotalStackCountOfDef(def) ?? 0;
            yield return new AssemblyMaterialRow(requirement.DefName, def, requirement.Count, delivered);
        }
    }

    internal string? MaterialOrderError
    {
        get
        {
            if (materialMigrationError != null) return materialMigrationError;
            if (state != AssemblerState.Building) return null;
            if (materialRequirements is not { Count: > 0 }) return "PF_AssemblyOrderMissing".Translate();
            foreach (MaterialRequirement material in materialRequirements)
            {
                if (material.Count <= 0 || string.IsNullOrEmpty(material.DefName)
                    || DefDatabase<ThingDef>.GetNamedSilentFail(material.DefName) is not
                        { category: ThingCategory.Item, stackLimit: > 0, destroyOnDrop: false })
                    return $"{"PF_AssemblyOrderMissingMaterial".Translate()} {material.DefName}";
            }
            return null;
        }
    }

    internal int MissingMaterial(ThingDef def)
    {
        if (materialRequirements == null) return 0;
        foreach (MaterialRequirement material in materialRequirements)
        {
            if (material.DefName == def.defName)
                return Math.Max(0, material.Count - (receivedMaterials?.TotalStackCountOfDef(def) ?? 0));
        }
        return 0;
    }

    internal bool HasAssemblyMaterials
    {
        get
        {
            if (materialRequirements is not { Count: > 0 } || !HasHeldMaterials || MaterialOrderError != null)
                return false;
            foreach (AssemblyMaterialRow row in MaterialRows())
            {
                if (row.Missing != 0) return false;
            }
            return true;
        }
    }

    internal bool IsOpenBuildOrder(int version)
        => version == buildOrderVersion && state == AssemblerState.Building
            && !buildPaused && !refundPending && MaterialOrderError == null;

    internal bool PauseBuild()
    {
        if (state != AssemblerState.Building || buildPaused || refundPending) return false;
        buildPaused = true;
        AdvanceOrder();
        return true;
    }

    internal bool ResumeBuild()
    {
        if (state != AssemblerState.Building || !buildPaused || refundPending) return false;
        buildPaused = false;
        return true;
    }

    private void AdvanceOrder()
        => buildOrderVersion = buildOrderVersion == int.MaxValue ? 1 : buildOrderVersion + 1;

    private void SetMaterialOrder(List<ThingDefCountClass> quote)
    {
        materialRequirements = quote.ConvertAll(line => new MaterialRequirement
        {
            DefName = line.thingDef.defName,
            Count = line.count,
        });
        materialOrderSchema = CurrentMaterialOrderSchema;
        materialMigrationError = null;
        buildPaused = false;
        refundPending = false;
        AdvanceOrder();
    }

    private void ResetMaterialOrder()
    {
        materialRequirements = null;
        materialMigrationError = null;
        buildPaused = false;
        refundPending = false;
    }

    private void CancelMaterialOrder()
    {
        if (!refundPending) AdvanceOrder();
        refundPending = true;
        TryRefundMaterials();
    }

    private void TryRefundMaterials()
    {
        if (HasHeldMaterials)
        {
            Map? map = parent?.Map;
            if (map == null || !receivedMaterials!.TryDropAll(parent!.Position, map, ThingPlaceMode.Near)) return;
        }
        state = AssemblerState.Designing;
        workDone = 0;
        workRequired = 0;
        ResetMaterialOrder();
    }

    private void ClearSettledMaterials()
    {
        if (!HasHeldMaterials) return;
        try
        {
            // The Complete state is committed before item callbacks. Leftovers can only be destroyed.
            receivedMaterials!.ClearAndDestroyContents();
        }
        catch (Exception exception)
        {
            Log.ErrorOnce($"[PFL] Rocket material cleanup failed on {parent}: {exception}",
                Gen.HashCombineInt(1960457721, parent?.thingIDNumber ?? 0));
        }
    }

    public override void PostDestroy(DestroyMode mode, Map previousMap)
    {
        if (HasHeldMaterials)
        {
            if (state is AssemblerState.Complete or AssemblerState.Launching)
            {
                ClearSettledMaterials();
            }
            else if (previousMap == null || !receivedMaterials!.TryDropAll(parent.Position, previousMap, ThingPlaceMode.Near))
            {
                // A destroyed pad has no holder to retain items that cannot be placed on the map.
                Log.Warning($"[PFL] Some materials from destroyed pad {parent} could not be placed.");
                receivedMaterials!.ClearAndDestroyContents();
            }
        }
        base.PostDestroy(mode, previousMap);
    }

    private void ExposeMaterialOrder()
    {
        Scribe_Values.Look(ref materialOrderSchema, "assemblerMaterialOrderSchema", 0);
        Scribe_Values.Look(ref buildOrderVersion, "assemblerBuildOrderVersion", 0);
        Scribe_Values.Look(ref buildPaused, "assemblerBuildPaused", false);
        Scribe_Values.Look(ref refundPending, "assemblerRefundPending", false);
        Scribe_Values.Look(ref materialMigrationError, "assemblerMaterialOrderError");
        Scribe_Collections.Look(ref materialRequirements, "assemblerMaterialRequirements", LookMode.Deep);
        Scribe_Deep.Look(ref receivedMaterials, "assemblerReceivedMaterials", this);
        if (Scribe.mode != LoadSaveMode.PostLoadInit || state != AssemblerState.Building) return;

        if (materialOrderSchema == 0)
        {
            if (TryQuoteAssemblyMaterials(out var quote, out string reason)) SetMaterialOrder(quote);
            else
            {
                materialOrderSchema = CurrentMaterialOrderSchema;
                materialMigrationError = reason;
            }
        }
        else if (materialOrderSchema != CurrentMaterialOrderSchema)
        {
            materialMigrationError = "PF_AssemblyUnsupportedOrder".Translate();
        }
        else if (materialMigrationError == null)
        {
            foreach (DesignPartSpec entry in Design.parts)
            {
                if (entry == null) continue;
                if (!string.IsNullOrEmpty(entry.defName) && DefDatabase<ShipPartDef>.GetNamedSilentFail(entry.defName) != null)
                    continue;
                materialMigrationError = $"{"PF_AssemblyUnknownPart".Translate()} {entry.defName}";
                break;
            }
        }
    }

    private sealed class MaterialRequirement : IExposable
    {
        public string DefName = string.Empty;
        public int Count;

        public void ExposeData()
        {
            Scribe_Values.Look(ref DefName, "defName", string.Empty);
            Scribe_Values.Look(ref Count, "count", 0);
        }
    }
}
