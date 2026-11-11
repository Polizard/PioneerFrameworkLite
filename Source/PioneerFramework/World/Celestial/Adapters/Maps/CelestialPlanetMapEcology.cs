using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace PioneerFramework.Celestial
{
    public static partial class CelestialPlanetMapHooks
    {
        private static void InstallEcology(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(WildPlantSpawner), "GetCommonalityOfPlant"), prefix: new(typeof(CelestialPlanetMapHooks), nameof(PlantCommonality)));
            h.Patch(AccessTools.Method(typeof(WildPlantSpawner), "CalculatePlantsWhichCanGrowAt"),
                transpiler: new(typeof(CelestialPlanetMapHooks), nameof(PlantCandidateInput)), postfix: new(typeof(CelestialPlanetMapHooks), nameof(FilterPlants)));
            h.Patch(AccessTools.Method(typeof(WildAnimalSpawner), nameof(WildAnimalSpawner.SpawnRandomWildAnimalAt)),
                prefix: new(typeof(CelestialPlanetMapHooks), nameof(BeginAnimals)), finalizer: new(typeof(CelestialPlanetMapHooks), nameof(EndAnimals)));
            h.Patch(AccessTools.PropertyGetter(typeof(WildAnimalSpawner), "DesiredAnimalDensity"),
                prefix: new(typeof(CelestialPlanetMapHooks), nameof(BeginAnimals)), finalizer: new(typeof(CelestialPlanetMapHooks), nameof(EndAnimals)));
            foreach (string method in new[] { nameof(BiomeDef.CommonalityOfAnimal), nameof(BiomeDef.CommonalityOfPollutionAnimal) })
                h.Patch(AccessTools.Method(typeof(BiomeDef), method), prefix: new(typeof(CelestialPlanetMapHooks), nameof(AnimalCommonality)));
            h.Patch(AccessTools.Method(typeof(BiomeDef), nameof(BiomeDef.CommonalityOfCoastalAnimal)), prefix: new(typeof(CelestialPlanetMapHooks), nameof(CoastalAnimalCommonality)));
        }
        public static bool PlantCommonality(Map ___map, ThingDef plant, ref float __result)
        {
            if (!GenerationDiversity(___map, out var d) || d.Plants.Count == 0) return true;
            __result = (float)CelestialPlanetMapDiversity.Weight(d.Plants, plant.defName);
            foreach (var mutator in ___map.TileInfo.Mutators) if (mutator.Worker != null) __result *= mutator.Worker.PlantCommonalityFactorFor(plant, ___map.Tile);
            return false;
        }
        private static List<ThingDef> PlantCandidates(BiomeDef biome, Map map)
        {
            if (!GenerationDiversity(map, out var d) || d.Plants.Count == 0) return biome.AllWildPlants;
            return plantInputs.GetValue(d, static p => new(p)).Plants;
        }
        private sealed class PlantInputs
        {
            public readonly List<ThingDef> Plants;
            public readonly float LowestOrder;
            public PlantInputs(CelestialPlanetMapDiversity profile)
            { Plants = profile.Plants.Select(r => Definition<ThingDef>(r.Id)).ToList(); LowestOrder = Plants.Min(p => p.plant.wildOrder); }
        }
        private static readonly ConditionalWeakTable<CelestialPlanetMapDiversity, PlantInputs> plantInputs = new();
        private static float LowestPlantOrder(BiomeDef biome, Map map) =>
            GenerationDiversity(map, out var d) && d.Plants.Count != 0 ? plantInputs.GetValue(d, static p => new(p)).LowestOrder : biome.LowestWildAndCavePlantOrder;
        public static IEnumerable<CodeInstruction> PlantCandidateInput(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0, orderReplaced = 0;
            var getter = AccessTools.PropertyGetter(typeof(BiomeDef), nameof(BiomeDef.AllWildPlants));
            var order = AccessTools.PropertyGetter(typeof(BiomeDef), nameof(BiomeDef.LowestWildAndCavePlantOrder));
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(getter))
                {
                    replaced++;
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(WildPlantSpawner), "map"));
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CelestialPlanetMapHooks), nameof(PlantCandidates)));
                }
                else if (instruction.Calls(order))
                {
                    orderReplaced++;
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(WildPlantSpawner), "map"));
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CelestialPlanetMapHooks), nameof(LowestPlantOrder)));
                }
                else yield return instruction;
            }
            if (replaced != 1 || orderReplaced == 0) throw new InvalidOperationException("NativePlantCandidateContractMissing");
        }
        public static void FilterPlants(Map ___map, List<ThingDef> outPlants)
        {
            if (GenerationDiversity(___map, out var d) && d.Plants.Count != 0)
                outPlants.RemoveAll(p => CelestialPlanetMapDiversity.Weight(d.Plants, p.defName) == 0);
        }
        [ThreadStatic] private static Map? animalMap;
        public static void BeginAnimals(Map ___map, out Map? __state) { __state = animalMap; animalMap = ___map; }
        public static Exception? EndAnimals(Exception? __exception, Map? __state) { animalMap = __state; return __exception; }
        public static bool AnimalCommonality(PawnKindDef animalDef, ref float __result)
        {
            if (animalMap == null || !GenerationDiversity(animalMap, out var d) || d.Animals.Count == 0) return true;
            __result = (float)CelestialPlanetMapDiversity.Weight(d.Animals, animalDef.defName); return false;
        }
        public static bool CoastalAnimalCommonality(PawnKindDef animalDef, ref float __result)
        {
            if (animalMap == null || !GenerationDiversity(animalMap, out var d) || d.Animals.Count == 0) return true;
            // Allowed coastal candidates retain the native DLC gate and biome weight.
            if (CelestialPlanetMapDiversity.Weight(d.Animals, animalDef.defName) > 0) return true;
            __result = 0; return false;
        }
    }
}
