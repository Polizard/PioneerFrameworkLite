using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PioneerFramework.Celestial
{
    public sealed class CompProperties_CelestialLifeSupport : CompProperties
    {
        public double conditioningPerSecondPerHundredCells = .5;
        public bool suppliesOxygen = true;
        public bool filtersToxins;
        public bool regulatesPressure;
        public bool requiresPower = true;
        public CompProperties_CelestialLifeSupport() => compClass = typeof(CompCelestialLifeSupport);
        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (var error in base.ConfigErrors(parentDef)) yield return error;
            if (!double.IsFinite(conditioningPerSecondPerHundredCells) || conditioningPerSecondPerHundredCells <= 0)
                yield return "Invalid life support conditioning rate.";
        }
    }
    /// <summary>Only facilities explicitly carrying this Comp provide supplementary conditioning.</summary>
    public sealed class CompCelestialLifeSupport : ThingComp
    {
        public CompProperties_CelestialLifeSupport Settings => (CompProperties_CelestialLifeSupport)props;
        public bool Operational => parent.Spawned && (!Settings.requiresPower || parent.GetComp<CompPowerTrader>()?.PowerOn == true) &&
            parent.GetComp<CompFlickable>()?.SwitchIsOn != false &&
            parent.GetComp<CompBreakdownable>()?.BrokenDown != true;
        public override string CompInspectStringExtra() => parent.Map != null && CelestialPlanetEnvironmentHooks.TrySample(parent.Map.Tile, out var sample) && sample.Exposure != null ?
            (Operational ? "PF_CelestialLifeSupportActive" : "PF_CelestialLifeSupportOffline").Translate().ToString() : string.Empty;
    }
}
