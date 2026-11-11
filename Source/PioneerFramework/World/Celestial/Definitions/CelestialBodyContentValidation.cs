using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;

namespace PioneerFramework.Celestial
{
    /// <summary>
    /// P4 真空声明跨组件校验（2026-11-03）：Parameterized terrain 能生成的 biome 必须与
    /// environment.vacuumBiomes 的声明一致——inVacuum=true 的生成群落必须被声明，
    /// inVacuum=false 的生成群落不得被声明（声明集合可以多出未生成的群落，供跨天体复用）。
    /// 这是 CelestialBodyDef 层面的静态内容校验：只有 XML Def 能读到原生 BiomeDef.inVacuum，
    /// 纯 API 的 ICelestialResolvedConfigurationValidator 看不到该字段，所以放在这里而不是 Resolve 阶段。
    /// 前提不足（无 terrain / 无 env / 多个同类 comp / 空引用）一律跳过返回空，不抛异常。
    /// </summary>
    internal static class CelestialBodyContentValidation
    {
        internal static IEnumerable<CelestialError> Validate(IReadOnlyList<CelestialCompProperties> comps)
        {
            if (comps == null) return Array.Empty<CelestialError>();
            var biomeComps = comps.OfType<CompProperties_BiomeGeneration>().ToArray();
            var environmentComps = comps.OfType<CompProperties_ExistingEnvironment>().ToArray();
            if (biomeComps.Length != 1 || environmentComps.Length != 1) return Array.Empty<CelestialError>();
            var biomeComp = biomeComps[0];
            var environmentComp = environmentComps[0];
            var terrain = biomeComp.profile?.terrain;
            var environment = environmentComp.profile;
            if (terrain == null || environment == null || terrain.biomeRules == null || environment.vacuumBiomes == null)
                return Array.Empty<CelestialError>();
            var declared = new HashSet<string>(environment.vacuumBiomes
                .Where(biome => biome != null && !string.IsNullOrWhiteSpace(biome.defName))
                .Select(biome => biome.defName), StringComparer.Ordinal);
            var generated = terrain.biomeRules
                .Where(rule => rule?.biome != null && !string.IsNullOrWhiteSpace(rule.biome.defName))
                .Select(rule => rule.biome)
                .Concat(terrain.oceanBiome != null && !string.IsNullOrWhiteSpace(terrain.oceanBiome.defName)
                    ? new[] { terrain.oceanBiome } : Array.Empty<BiomeDef>())
                .GroupBy(biome => biome.defName, StringComparer.Ordinal)
                .Select(group => group.First());
            var errors = new List<CelestialError>();
            foreach (var biome in generated)
            {
                if (biome.inVacuum == declared.Contains(biome.defName)) continue;
                // 真空群落漏声明 ⇒ 应补 environment.vacuumBiomes；非真空群落误声明 ⇒ 应删 environment 声明。
                errors.Add(new CelestialError("VacuumContractMismatch",
                    CompKey: biome.inVacuum ? biomeComp.key : environmentComp.key,
                    Detail: biome.defName + " inVacuum=" + biome.inVacuum));
            }
            return errors;
        }
    }
}
