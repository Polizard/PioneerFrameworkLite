using System;
using System.Collections.Generic;

namespace PioneerFramework.Celestial
{
    /// <summary>Frozen native map inputs. Empty candidate lists retain the native candidates; no shared Def is edited.</summary>
    public sealed class CelestialPlanetMapDiversity
    {
        public IReadOnlyList<CelestialWeightedReference> Rocks { get; }
        public IReadOnlyList<CelestialWeightedReference> Minerals { get; }
        public IReadOnlyList<CelestialWeightedReference> Soils { get; }
        public IReadOnlyList<CelestialWeightedReference> Plants { get; }
        public IReadOnlyList<CelestialWeightedReference> Animals { get; }
        public IReadOnlyList<CelestialWeightedReference> Weather { get; }
        public IReadOnlyList<CelestialWeightedReference> Conditions { get; }
        public IReadOnlyList<CelestialWeightedReference> Incidents { get; }
        public double MineralAbundance { get; }
        public double SoilFertility { get; }
        public double PlantDensity { get; }
        public double AnimalDensity { get; }
        public double GeologyVariation { get; }
        public bool AllowCaves { get; }
        public CelestialPlanetMapDiversity(IReadOnlyList<CelestialWeightedReference>? rocks = null,
            IReadOnlyList<CelestialWeightedReference>? minerals = null, IReadOnlyList<CelestialWeightedReference>? soils = null,
            IReadOnlyList<CelestialWeightedReference>? plants = null, IReadOnlyList<CelestialWeightedReference>? animals = null,
            IReadOnlyList<CelestialWeightedReference>? weather = null, IReadOnlyList<CelestialWeightedReference>? conditions = null,
            IReadOnlyList<CelestialWeightedReference>? incidents = null, double mineralAbundance = 1, double soilFertility = 1,
            double plantDensity = 1, double animalDensity = 1, double geologyVariation = 0, bool allowCaves = true)
        {
            Rocks = CelestialWeightedReference.Copy(rocks); Minerals = CelestialWeightedReference.Copy(minerals); Soils = CelestialWeightedReference.Copy(soils);
            Plants = CelestialWeightedReference.Copy(plants); Animals = CelestialWeightedReference.Copy(animals); Weather = CelestialWeightedReference.Copy(weather);
            Conditions = CelestialWeightedReference.Copy(conditions); Incidents = CelestialWeightedReference.Copy(incidents);
            CelestialPlanetTerrainConfiguration.Domain(mineralAbundance, 0, 10, nameof(mineralAbundance));
            CelestialPlanetTerrainConfiguration.Domain(soilFertility, 0, 10, nameof(soilFertility));
            CelestialPlanetTerrainConfiguration.Domain(plantDensity, 0, 10, nameof(plantDensity));
            CelestialPlanetTerrainConfiguration.Domain(animalDensity, 0, 10, nameof(animalDensity));
            CelestialPlanetTerrainConfiguration.Domain(geologyVariation, 0, 1, nameof(geologyVariation));
            MineralAbundance = mineralAbundance; SoilFertility = soilFertility; PlantDensity = plantDensity; AnimalDensity = animalDensity;
            GeologyVariation = geologyVariation; AllowCaves = allowCaves;
        }
        public static double Weight(IReadOnlyList<CelestialWeightedReference> references, string id, double native = 1)
        {
            if (references.Count == 0) return native;
            foreach (var item in references) if (item.Id == id) return item.Weight;
            return 0;
        }
    }
}
