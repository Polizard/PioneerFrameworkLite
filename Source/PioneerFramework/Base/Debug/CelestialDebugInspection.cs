using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PioneerFramework.Celestial;
using Verse;

namespace PioneerFramework
{
    /// <summary>Reads frozen instance records and existing ready capabilities. Never requests layers or maps.</summary>
    internal static class CelestialDebugInspection
    {
        internal static string Describe(CelestialRuntime runtime, CelestialBodyInstance body)
        {
            string unknown = "PF_PlanetKind_Unknown".Translate();
            string N(double? value) => value?.ToString("G6", CultureInfo.InvariantCulture) ?? unknown;
            var d = body.Description; double? gm = d.GravitationalParameter; double r = d.RadiusMeters;
            var rows = new List<string> { "PF_PlanetKind_Resolved".Translate() +
                $" r={N(r)} m; density={N(gm / (6.67430e-11 * (4d / 3) * Math.PI * r * r * r))} kg/m3; GM={N(gm)} m3/s2; g={N(gm / (r * r))} m/s2; escape={N(gm.HasValue ? Math.Sqrt(2 * gm.Value / r) : null)} m/s" };
            if (runtime.Physics is ExistingCelestialEphemerisBridge bridge && bridge.TryGetParameters(body.Id, out var motion))
                rows.Add($"motion: orbit={N(motion.OrbitRadius)} m / {N(motion.OrbitPeriod)} s; rotation={N(motion.DayLengthTicks)} s; axis={motion.RotationAxis}; retrograde={motion.Retrograde}; synchronous={motion.SynchronousTargetReference}");
            var air = runtime.GetCapabilities<ICelestialAtmosphereCompositionCapability>(body.Id).SingleOrDefault();
            rows.Add(air == null ? "air pressure/composition: " + unknown :
                $"air @ altitude=0m: p={N(air.Sample(0).PressurePascals)} Pa; O2={N(air.Sample(0).OxygenPartialPressurePascals)} Pa; CO2={N(air.Sample(0).CarbonDioxidePartialPressurePascals)} Pa");
            foreach (var record in body.CompStateRecords.Values)
            {
                if (record.HandlerId == AtmosphereResolvedProperties.Handler && record.ConfigurationVersion == 2)
                {
                    var decoded = AtmosphereResolvedProperties.Deserialize(record.Key, record.ConfigurationVersion, record.ConfigurationPayload);
                    if (decoded.Success) rows.Add("composition: " + string.Join(",", decoded.Value.Composition.Select(g => g.Gas + "=" + N(g.MoleFraction))));
                }
                if (record.HandlerId == ExistingEnvironmentResolvedProperties.Handler)
                {
                    var decoded = ExistingEnvironmentResolvedProperties.Deserialize(record.Key, record.ConfigurationVersion, record.ConfigurationPayload);
                    var c = decoded.Success ? decoded.Value.Profile.Climate : null;
                    if (c != null) rows.Add($"climate: {c.Mode} mean={N(c.MeanTemperatureCelsius)}C dayAmplitude={N(c.DiurnalAmplitudeCelsius)}C seasonAmplitude={N(c.SeasonalAmplitudeCelsius)}C year={N(c.YearLengthTicks)} ticks response={N(c.ThermalResponseSeconds)}s");
                }
            }
            var terrain = runtime.GetCapabilities<ICelestialBiomeGenerationCapability>(body.Id).SingleOrDefault()?.GetProfile().Terrain;
            var maps = runtime.GetCapabilities<ICelestialMapGenerationCapability>(body.Id).SingleOrDefault()?.GetProfile().Diversity;
            rows.Add(terrain == null ? "terrain: " + unknown : $"terrain: scale={N(terrain.ContinentScale)} roughness={N(terrain.Roughness)} water={N(terrain.OceanCoverage)}; biomes=" + string.Join(",", terrain.BiomeRules.Select(b => b.BiomeId)));
            rows.Add(maps == null ? "map: " + unknown : $"map: minerals={N(maps.MineralAbundance)} soils={N(maps.SoilFertility)} plants={N(maps.PlantDensity)} animals={N(maps.AnimalDensity)} weather=" + string.Join(",", maps.Weather.Select(w => w.Id)));
            var environment = runtime.GetCapabilities<ICelestialSurfaceEnvironmentCapability>(body.Id).SingleOrDefault();
            string? biome = terrain?.BiomeRules.SingleOrDefault(b => b.Fallback)?.BiomeId;
            if (environment != null && body.Lifecycle == CelestialLifecycle.Ready)
            {
                try {
                    var q = new CelestialSurfaceEnvironmentQuery(Find.TickManager?.TicksAbs ?? 0, 0, 0, 0, biome);
                    var sample = environment.SampleSurface(q);
                    var dose = CelestialExposureModel.Dose(sample, new(true), 60000, false);
                    rows.Add($"query: lon=0 lat=0 altitude=0m biome={biome ?? unknown} displayTicks={N(q.DisplayTicksAbs)} physicalSeconds={N(sample.PhysicalSeconds)}; T={N(sample.TemperatureCelsius)} C; light={N(sample.SunGlow)} wind={N(sample.WindSpeed)} rain={N(sample.RainfallMillimeters)}; unprotected/day hypo={N(dose.Hypoxia)} pressure={N(dose.Pressure)} toxic={N(dose.Toxicity)} radiation={N(dose.Radiation)}");
                } catch (Exception exception) { rows.Add("query: " + unknown + " (" + exception.Message + ")"); }
            }
            else rows.Add("climate/exposure query: " + unknown);
            rows.Add("visual: " + string.Join("; ", runtime.GetCapabilities<ICelestialVisualCapability>(body.Id).Select(c => {
                var v = c.GetVisualDescription(); return $"{v.Kind}={v.Profile.Id} style={v.Profile.Appearance?.Style} coverage={N(v.Profile.Appearance?.CloudCoverage)}"; })));
            var definition = DefDatabase<CelestialBodyDef>.GetNamedSilentFail(d.DefinitionId);
            var authored = definition?.comps.OfType<CompProperties_SphericalBody>().SingleOrDefault()?.radiusMeters;
            rows.Add("PF_PlanetKind_AuthoringReference".Translate() + (authored == null ? unknown : $" radius=[{N(authored.value ?? authored.min)},{N(authored.value ?? authored.max)}] m"));
            return string.Join("\n", rows);
        }
    }
}
