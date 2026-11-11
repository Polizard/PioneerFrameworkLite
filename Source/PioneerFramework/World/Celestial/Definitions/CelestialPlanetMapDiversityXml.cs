using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace PioneerFramework.Celestial
{
    public abstract class CelestialPlanetReferenceXml
    {
        public double weight = 1;
        public List<CelestialEnvironmentParameterXml> parameters = new();
        protected abstract Def? Reference { get; }
        internal CelestialWeightedReference Resolve(CelestialGenerationContext? context, string group)
        {
            var s = new CelestialParameterSamplerXml(parameters, context, "diversity." + group + "." + Reference?.defName + ".");
            var result = new CelestialWeightedReference(Reference?.defName ?? "", s.Get(nameof(weight), weight, double.Epsilon, 1000000)); s.Complete(); return result;
        }
    }
    public sealed class CelestialPlanetThingReferenceXml : CelestialPlanetReferenceXml
    { public ThingDef def = null!; protected override Def? Reference => def; }
    public sealed class CelestialPlanetSoilReferenceXml : CelestialPlanetReferenceXml
    { public TerrainDef def = null!; protected override Def? Reference => def; }
    public sealed class CelestialPlanetAnimalReferenceXml : CelestialPlanetReferenceXml
    { public PawnKindDef def = null!; protected override Def? Reference => def; }
    public sealed class CelestialPlanetWeatherReferenceXml : CelestialPlanetReferenceXml
    { public WeatherDef def = null!; protected override Def? Reference => def; }
    public sealed class CelestialPlanetConditionReferenceXml : CelestialPlanetReferenceXml
    { public GameConditionDef def = null!; protected override Def? Reference => def; }
    public sealed class CelestialPlanetIncidentReferenceXml : CelestialPlanetReferenceXml
    { public IncidentDef def = null!; protected override Def? Reference => def; }
    public sealed class CelestialPlanetMapDiversityXml
    {
        public List<CelestialEnvironmentParameterXml> parameters = new();
        public List<CelestialPlanetThingReferenceXml> rocks = new();
        public List<CelestialPlanetThingReferenceXml> minerals = new();
        public List<CelestialPlanetThingReferenceXml> plants = new();
        public List<CelestialPlanetSoilReferenceXml> soils = new();
        public List<CelestialPlanetAnimalReferenceXml> animals = new();
        public List<CelestialPlanetWeatherReferenceXml> weather = new();
        public List<CelestialPlanetConditionReferenceXml> conditions = new();
        public List<CelestialPlanetIncidentReferenceXml> incidents = new();
        public double mineralAbundance = 1;
        public double soilFertility = 1;
        public double plantDensity = 1;
        public double animalDensity = 1;
        public double geologyVariation = 0;
        public bool allowCaves = true;
        public CelestialPlanetMapDiversity Resolve(CelestialGenerationContext? context = null)
        {
            if (rocks.Any(r => r.def == null || !r.def.IsNonResourceNaturalRock) || minerals.Any(r => r.def?.building?.mineableThing == null || r.def.category != ThingCategory.Building) ||
                plants.Any(r => r.def?.plant == null || r.def.category != ThingCategory.Plant) || animals.Any(r => r.def?.race?.race?.Animal != true) ||
                soils.Any(r => r.def == null || r.def.IsWater || r.def.IsRock || !float.IsFinite(r.def.fertility) || r.def.fertility < 0))
                throw new ArgumentException("Invalid native geology or ecology reference.");
            var s = new CelestialParameterSamplerXml(parameters, context, "diversity.");
            CelestialWeightedReference[] Refs<T>(List<T> values, string name) where T : CelestialPlanetReferenceXml => values.Select(r => r.Resolve(context, name)).ToArray();
            var result = new CelestialPlanetMapDiversity(Refs(rocks, nameof(rocks)), Refs(minerals, nameof(minerals)), Refs(soils, nameof(soils)),
                Refs(plants, nameof(plants)), Refs(animals, nameof(animals)), Refs(weather, nameof(weather)), Refs(conditions, nameof(conditions)), Refs(incidents, nameof(incidents)),
                s.Get(nameof(mineralAbundance), mineralAbundance, 0, 10), s.Get(nameof(soilFertility), soilFertility, 0, 10),
                s.Get(nameof(plantDensity), plantDensity, 0, 10), s.Get(nameof(animalDensity), animalDensity, 0, 10),
                s.Get(nameof(geologyVariation), geologyVariation, 0, 1), allowCaves);
            s.Complete(); return result;
        }
    }
}
