using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace PioneerFramework.Celestial
{
    /// <summary>Optional procedural population for a new system using the existing ephemeris provider.</summary>
    public sealed class CelestialPopulationPolicyDef : Def
    {
        public int minimumPlanets = 4;
        public int maximumPlanets = 8;
        public int minimumDwarfs = 1;
        public int maximumDwarfs = 4;
        public int attemptsPerSlot = 32;
        public double referenceIrradiance = 1;
        public double minimumOrbitSeparation = .06;
        public double hillFraction = .35;
        public double satelliteMassFraction = .05;
        public List<CelestialPopulationDistanceBand> distanceBands = new();
        public List<CelestialPopulationBodyRule> bodyRules = new();

        internal CelestialResult<IReadOnlyList<CelestialPopulationRule>> ResolveRules()
        {
            if (minimumPlanets < 0 || minimumPlanets > maximumPlanets || maximumPlanets > 8 ||
                minimumDwarfs < 0 || minimumDwarfs > maximumDwarfs || maximumDwarfs > 4 || attemptsPerSlot is < 1 or > 32 ||
                !Positive(referenceIrradiance) || referenceIrradiance > 1e6 || !Fraction(minimumOrbitSeparation) ||
                !Positive(hillFraction) || hillFraction > .49 || !Positive(satelliteMassFraction) || satelliteMassFraction > .05)
                return Reject("InvalidPopulationLimits");
            if (distanceBands == null || distanceBands.Count == 0 || distanceBands.Any(band => band == null ||
                !Positive(band.minimum) || !Positive(band.maximum) || band.maximum <= band.minimum ||
                !Weight(band.planetWeight) || !Weight(band.dwarfWeight)))
                return Reject("InvalidPopulationDistanceBands");
            var bands = distanceBands.OrderBy(band => band.minimum).ToArray();
            if (bands.Zip(bands.Skip(1)).Any(pair => pair.First.maximum > pair.Second.minimum) ||
                maximumPlanets > 0 && !bands.Any(band => band.planetWeight > 0) ||
                maximumDwarfs > 0 && !bands.Any(band => band.dwarfWeight > 0))
                return Reject("InvalidPopulationDistanceBands");
            if (bodyRules == null || bodyRules.Count == 0 || bodyRules.Any(rule => rule == null || rule.body == null ||
                !Enum.IsDefined(rule.role) || !Positive(rule.weight) || !Weight(rule.weight) ||
                !double.IsFinite(rule.minimumDistance) || rule.minimumDistance < 0 || !Positive(rule.maximumDistance) ||
                rule.maximumDistance <= rule.minimumDistance || !Positive(rule.minimumRadiusMeters) ||
                !Positive(rule.maximumRadiusMeters) || rule.maximumRadiusMeters < rule.minimumRadiusMeters ||
                !Positive(rule.minimumDensity) || !Positive(rule.maximumDensity) || rule.maximumDensity < rule.minimumDensity ||
                !Positive(rule.minimumSpinSeconds) || !Positive(rule.maximumSpinSeconds) || rule.maximumSpinSeconds < rule.minimumSpinSeconds ||
                !Weight(rule.temperatureResponseKelvin) || rule.moonCountWeights == null || rule.moonCountWeights.Count > 5 ||
                rule.moonCountWeights.Any(weight => !Weight(weight)) || rule.moonCountWeights.Count > 0 && rule.moonCountWeights.All(weight => weight == 0) ||
                rule.role == CelestialPopulationRole.Moon && rule.moonCountWeights.Count != 0))
                return Reject("InvalidPopulationBodyRule");
            if (bodyRules.GroupBy(rule => (rule.role, rule.body.defName)).Any(group => group.Count() > 1))
                return Reject("DuplicatePopulationBodyRule");

            var resolved = new List<CelestialPopulationRule>();
            foreach (var rule in bodyRules.OrderBy(rule => rule.body.defName, StringComparer.Ordinal).ThenBy(rule => rule.role))
            {
                var template = rule.body.ToTemplate();
                if (!template.Success) return CelestialResult.Reject<IReadOnlyList<CelestialPopulationRule>>(template.Errors.ToArray());
                var shape = rule.body.comps.OfType<CompProperties_SphericalBody>().SingleOrDefault();
                var motion = rule.body.comps.OfType<CompProperties_ExternalPhysicsBinding>().SingleOrDefault();
                var surface = rule.body.comps.OfType<CompProperties_Surface>().SingleOrDefault();
                if (shape == null || motion?.providerId != ExistingCelestialEphemerisBridge.Id ||
                    template.Value.HasCapability("surface") && surface?.generationMode != CelestialSurfaceGenerationMode.OnDemand)
                    return Reject("UnsupportedPopulationTemplate", rule.body.defName);
                var environment = rule.body.comps.OfType<CompProperties_ExistingEnvironment>().SingleOrDefault();
                if (environment?.profile.primaryLightReference == "frameOrigin")
                    return Reject("ConflictingPopulationLightReference", rule.body.defName);
                var atmosphere = rule.body.comps.OfType<CompProperties_Atmosphere>().SingleOrDefault();
                var climate = environment?.profile.ToProfile().Value.Climate;
                resolved.Add(new(rule, template.Value, shape.key, motion.key, environment?.profile.primaryLightReference,
                    climate == null ? null : new(environment!.key, "climate.meanTemperatureCelsius", climate.MeanTemperatureCelsius + 273.15),
                    atmosphere?.temperatureKelvin == null ? null : new(atmosphere.key, "temperatureKelvin", atmosphere.temperatureKelvin.ToSpec().Value.Minimum)));
            }
            return CelestialResult.Ok<IReadOnlyList<CelestialPopulationRule>>(resolved.AsReadOnly());
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            foreach (var error in ResolveRules().Errors) yield return error.Code + ": " + error.Detail;
        }

        private static bool Positive(double value) => double.IsFinite(value) && value > 0;
        private static bool Weight(double value) => double.IsFinite(value) && value >= 0 && value <= 1e6;
        private static bool Fraction(double value) => Weight(value) && value < 1;
        private static CelestialResult<IReadOnlyList<CelestialPopulationRule>> Reject(string code, string detail = "")
            => CelestialResult.Reject<IReadOnlyList<CelestialPopulationRule>>(new CelestialError(code, Detail: detail));
    }

    public sealed class CelestialPopulationDistanceBand
    {
        public double minimum;
        public double maximum;
        public double planetWeight;
        public double dwarfWeight;
    }

    public enum CelestialPopulationRole { Planet, DwarfPlanet, Moon }

    public sealed class CelestialPopulationBodyRule
    {
        public CelestialBodyDef body = null!;
        public CelestialPopulationRole role;
        public double weight = 1;
        public double minimumDistance;
        public double maximumDistance = 60;
        public double minimumRadiusMeters;
        public double maximumRadiusMeters;
        public double minimumDensity;
        public double maximumDensity;
        public double minimumSpinSeconds = 20000;
        public double maximumSpinSeconds = 150000;
        public double temperatureResponseKelvin = 65;
        public List<double> moonCountWeights = new();
    }

    internal sealed record CelestialPopulationRule(CelestialPopulationBodyRule Settings, CelestialBodyTemplate Template,
        string GeometryKey, string MotionKey, string? LightReference, CelestialPopulationTemperature? Climate, CelestialPopulationTemperature? Atmosphere);
    internal sealed record CelestialPopulationTemperature(string CompKey, string ParameterKey, double BaselineKelvin);
}
