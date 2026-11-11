using System;
using RimWorld;
using Verse;

namespace PioneerFramework.Celestial
{
    /// <summary>Extension filter and protection contract for third-party species and equipment. Independent resistance channels.</summary>
    public sealed class CelestialEnvironmentProtectionExtension : DefModExtension
    {
        public bool ignoresBiologicalExposure;
        public double oxygenSupply;
        public double highPressureResistance;
        public double radiationResistance;
        public bool gravityAlreadyAdapted;
    }
    public static class CelestialPawnEnvironment
    {
        public static CelestialExposureProtection Protection(Pawn pawn)
        {
            var race = pawn.def.GetModExtension<CelestialEnvironmentProtectionExtension>();
            bool biological = pawn.RaceProps.IsFlesh && !pawn.RaceProps.IsMechanoid && race?.ignoresBiologicalExposure != true;
            double oxygen = race?.oxygenSupply ?? 0, pressure = race?.highPressureResistance ?? 0, radiation = race?.radiationResistance ?? 0;
            if (pawn.apparel != null)
                foreach (var apparel in pawn.apparel.WornApparel)
                {
                    var extension = apparel.def.GetModExtension<CelestialEnvironmentProtectionExtension>();
                    if (extension == null) continue;
                    oxygen = Math.Max(oxygen, extension.oxygenSupply); pressure = Math.Max(pressure, extension.highPressureResistance);
                    radiation = Math.Max(radiation, extension.radiationResistance);
                }
            if (pawn.genes != null)
                foreach (var gene in pawn.genes.GenesListForReading)
                {
                    if (!gene.Active) continue;
                    var extension = gene.def.GetModExtension<CelestialEnvironmentProtectionExtension>();
                    if (extension == null) continue;
                    biological &= !extension.ignoresBiologicalExposure;
                    oxygen = Math.Max(oxygen, extension.oxygenSupply); pressure = Math.Max(pressure, extension.highPressureResistance);
                    radiation = Math.Max(radiation, extension.radiationResistance);
                }
            double vacuum = ModsConfig.OdysseyActive ? pawn.GetStatValue(StatDefOf.VacuumResistance) : 0;
            // Native vacuum resistance supplies only the low-pressure channel. Oxygen equipment must opt in explicitly.
            return new(biological, oxygen, vacuum, pressure, pawn.GetStatValue(StatDefOf.ToxicResistance),
                pawn.GetStatValue(StatDefOf.ToxicEnvironmentResistance), radiation);
        }
        public static bool GravityAlreadyAdapted(Pawn pawn)
        {
            if (pawn.def.GetModExtension<CelestialEnvironmentProtectionExtension>()?.gravityAlreadyAdapted == true) return true;
            if (pawn.genes != null) foreach (var gene in pawn.genes.GenesListForReading)
                if (gene.Active && gene.def.GetModExtension<CelestialEnvironmentProtectionExtension>()?.gravityAlreadyAdapted == true) return true;
            return false;
        }
        public static void Apply(Pawn pawn, CelestialExposureDose dose)
        {
            void Add(string name, double severity)
            {
                if (severity <= 0) return;
                var definition = DefDatabase<HediffDef>.GetNamedSilentFail(name) ?? throw new InvalidOperationException("EnvironmentHediffMissing: " + name);
                HealthUtility.AdjustSeverity(pawn, definition, (float)severity);
            }
            Add("PF_CelestialHypoxia", dose.Hypoxia); Add("PF_CelestialPressureStress", dose.Pressure);
            if (dose.Toxicity > 0) HealthUtility.AdjustSeverity(pawn, HediffDefOf.ToxicBuildup, (float)dose.Toxicity);
            Add("PF_CelestialRadiationExposure", dose.Radiation);
        }
    }
    public sealed class StatPart_CelestialGravity : StatPart
    {
        public bool carrying;
        public override void TransformValue(StatRequest req, ref float val)
        {
            if (req.Thing is not Pawn pawn || pawn.MapHeld == null || CelestialPawnEnvironment.GravityAlreadyAdapted(pawn) ||
                !CelestialPlanetEnvironmentHooks.TrySample(pawn.MapHeld.Tile, out var sample) || sample.Exposure?.GravityStats != true || !sample.GravityMetersPerSecondSquared.HasValue) return;
            val *= (float)CelestialExposureModel.GravityFactor(sample.GravityMetersPerSecondSquared.Value, carrying);
        }
        public override string? ExplanationPart(StatRequest req) => req.Thing is Pawn pawn && pawn.MapHeld != null &&
            CelestialPlanetEnvironmentHooks.TrySample(pawn.MapHeld.Tile, out var sample) && sample.Exposure?.GravityStats == true ?
                "PF_CelestialGravityEffect".Translate().ToString() : null;
    }
}
