using System;
using PioneerFramework.Celestial;
using Xunit;
using RimWorld;
using Verse;
using HarmonyLib;
using System.Collections.Generic;
namespace PioneerFramework.Tests;
public class CelestialPlanetExposureTests
{
    internal static Pawn BiologicalPawn()
    {
        var definition = (ThingDef)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ThingDef));
        definition.defName = "PFL_ExposureHuman"; definition.race = new RaceProperties();
        definition.race.lifeStageAges = new() { new() { minAge = 0, def = new LifeStageDef { defName = "PFL_Adult", developmentalStage = DevelopmentalStage.Adult } } };
        var pawn = new Pawn { def = definition };
        AccessTools.Field(typeof(RaceProperties), "fleshType").SetValue(pawn.RaceProps, new FleshTypeDef { isOrganic = true });
        pawn.health = new Pawn_HealthTracker(pawn); pawn.ageTracker = new Pawn_AgeTracker(pawn);
        AccessTools.Field(typeof(Pawn_AgeTracker), "cachedLifeStageIndex").SetValue(pawn.ageTracker, 0);
        pawn.apparel = new Pawn_ApparelTracker(pawn);
        return pawn;
    }
    [Fact]
    public void RealPawnEquipmentAndActiveGenesHaveIndependentProtectionChannels()
    {
        using var scope = new CelestialNativeWorldTestScope();
        var toxic = StatDefOf.ToxicResistance; var environment = StatDefOf.ToxicEnvironmentResistance;
        var biotechField = AccessTools.Field(typeof(ModsConfig), "biotechActive"); var previousBiotech = biotechField.GetValue(null);
        biotechField.SetValue(null, true);
        StatDefOf.ToxicResistance = new StatDef { defName = "PFL_ToxicProbe", defaultBaseValue = .25f, immutable = true };
        StatDefOf.ToxicEnvironmentResistance = new StatDef { defName = "PFL_ToxicEnvProbe", defaultBaseValue = .5f, immutable = true };
        StatDefOf.ToxicResistance.Worker.SetCacheability(true);
        StatDefOf.ToxicEnvironmentResistance.Worker.SetCacheability(true);
        try
        {
            var pawn = BiologicalPawn();
            var apparelDef = (ThingDef)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ThingDef));
            apparelDef.defName = "PFL_OxygenHelmet"; apparelDef.apparel = new ApparelProperties();
            apparelDef.modExtensions = new() { new CelestialEnvironmentProtectionExtension { oxygenSupply = 1 } };
            apparelDef.equippedStatOffsets = new() { new() { stat = StatDefOf.ToxicResistance, value = .2f } };
            var apparel = new Apparel { def = apparelDef };
            pawn.apparel.WornApparel.Add(apparel);
            var protection = CelestialPawnEnvironment.Protection(pawn);
            Assert.Equal(1, protection.OxygenSupply); Assert.Equal(0, protection.HighPressureResistance);
            Assert.Equal(.45, protection.ToxicResistance, 6); Assert.Equal(.5, protection.ToxicEnvironmentResistance);
            pawn.genes = new Pawn_GeneTracker(pawn);
            var gene = new Gene { pawn = pawn, def = new GeneDef { defName = "PFL_PressureAdaptation", modExtensions = new() {
                new CelestialEnvironmentProtectionExtension { highPressureResistance = .75, radiationResistance = .5 } },
                statOffsets = new() { new() { stat = StatDefOf.ToxicResistance, value = .1f } } } };
            pawn.genes.Endogenes.Add(gene);
            StatDefOf.ToxicResistance.Worker.SetCacheability(true);
            var adapted = CelestialPawnEnvironment.Protection(pawn);
            Assert.Equal(.75, adapted.HighPressureResistance); Assert.Equal(.5, adapted.RadiationResistance);
            Assert.Equal(1, adapted.OxygenSupply); Assert.Equal(.55, adapted.ToxicResistance, 6);
            var dose = CelestialExposureModel.Dose(Sample(900000, 0), adapted, 60000, false);
            Assert.Equal(0, dose.Hypoxia); Assert.Equal(1, dose.Pressure); Assert.Equal(.225, dose.Toxicity, 6); Assert.Equal(.5, dose.Radiation);
        }
        finally { StatDefOf.ToxicResistance = toxic; StatDefOf.ToxicEnvironmentResistance = environment; biotechField.SetValue(null, previousBiotech); }
    }
    internal static CelestialSurfaceEnvironmentSample Sample(double? pressure = 101325, double? oxygen = 21200) =>
        new(20, 1, 1, 1000, .5, pressure, oxygen, 1, 1, 9.80665, 0, 0, 1, 0, false, new CelestialExposureXml().Resolve());
    [Fact]
    public void UnknownAirIsNotVacuumAndLegacyNeverAddsExposure()
    {
        var unknown = CelestialExposureModel.Dose(Sample(null, null), new(true), 60000, false);
        Assert.Equal(0, unknown.Hypoxia); Assert.Equal(0, unknown.Pressure);
        Assert.False(CelestialExposureModel.Dose(Sample() with { Exposure = null }, new(true), 60000, false).Dangerous);
        Assert.False(CelestialExposureModel.Dose(Sample(0, 0), new(false), 60000, false).Dangerous);
    }
    [Fact]
    public void NativeVacuumOwnsOnlyLowPressureAndHypoxia()
    {
        var dose = CelestialExposureModel.Dose(Sample(900000, 0), new(true, 1, 1), 60000, true);
        Assert.Equal(0, dose.Hypoxia);
        Assert.True(dose.Pressure > 0); Assert.True(dose.Toxicity > 0); Assert.True(dose.Radiation > 0);
        Assert.Equal(0, CelestialExposureModel.Dose(Sample(0, 0), new(true), 60, true).Pressure);
    }
    [Fact]
    public void EquipmentAndGenesComposeOncePerDistinctProtectionChannel()
    {
        var dose = CelestialExposureModel.Dose(Sample(900000, 0), new(true, .5, 1, .25, .5, .5, .75), 60000, false);
        Assert.Equal(1, dose.Hypoxia); Assert.Equal(3, dose.Pressure);
        Assert.Equal(.25, dose.Toxicity); Assert.Equal(.25, dose.Radiation);
        Assert.Equal(dose.Hypoxia / 1000, CelestialExposureModel.Dose(Sample(900000, 0), new(true, .5), 60, false).Hypoxia);
        Assert.False(CelestialExposureModel.Dose(Sample(0, 0), new(true), 0, false).Dangerous);
    }
    [Fact]
    public void ExposureRequiresExplicitOptInAndHasSeparateProtectionChannels()
    {
        var field = typeof(CelestialEnvironmentProfileDef).GetField("exposure");
        Assert.NotNull(field);
        var definition = new CelestialEnvironmentProfileDef { defName = "PF_ExposureProbe" };
        Assert.Null(field!.GetValue(definition));
        var exposure = Activator.CreateInstance(field.FieldType)!;
        field.FieldType.GetField("maximumPressurePascals")!.SetValue(exposure, 150000d);
        field.SetValue(definition, exposure);
        Assert.Equal(2, new ExistingEnvironmentResolvedProperties("environment", definition.ToProfile().Value).Version);
        Assert.NotNull(typeof(CelestialEnvironmentProfile).Assembly.GetType("PioneerFramework.Celestial.CelestialExposureProtection"));
    }
}
