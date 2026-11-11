using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PioneerFramework.Celestial
{
    public interface ICelestialBiomeGenerationCapability
    {
        CelestialGenerationProfile GetProfile();
    }
    // 显式数值固定：旧存档/旧载荷里的 VanillaSurface=1、Parameterized=2 保持不变；LatitudeBands 已退役（2026-11-03）。
    public enum CelestialBiomeGenerationMode { VanillaSurface = 1, Parameterized = 2 }
    public sealed class CelestialGenerationProfile
    {
        [JsonPropertyName("Id"), JsonPropertyOrder(1)]
        public string Id { get; }
        [JsonPropertyName("Mode"), JsonPropertyOrder(2)]
        public CelestialBiomeGenerationMode Mode { get; }
        [JsonPropertyName("Temperature"), JsonPropertyOrder(3)]
        public float Temperature { get; }
        [JsonPropertyName("Rainfall"), JsonPropertyOrder(4)]
        public float Rainfall { get; }
        [JsonPropertyName("Swampiness"), JsonPropertyOrder(5)]
        public float Swampiness { get; }
        [JsonPropertyName("WorldGenSteps"), JsonPropertyOrder(6)]
        public IReadOnlyList<string> WorldGenSteps { get; }
        [JsonPropertyName("RootOnlySteps"), JsonPropertyOrder(7)]
        public IReadOnlyList<string> RootOnlySteps { get; }
        [JsonPropertyName("Terrain"), JsonPropertyOrder(8), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CelestialPlanetTerrainConfiguration? Terrain { get; }
        [JsonConstructor]
        public CelestialGenerationProfile(string id, CelestialBiomeGenerationMode mode, float temperature, float rainfall, float swampiness,
            IReadOnlyList<string> worldGenSteps, IReadOnlyList<string> rootOnlySteps, CelestialPlanetTerrainConfiguration? terrain = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            if (!float.IsFinite(temperature) || !float.IsFinite(rainfall) || rainfall < 0 || !float.IsFinite(swampiness) || swampiness < 0 || swampiness > 1)
                throw new ArgumentOutOfRangeException(nameof(temperature));
            if (worldGenSteps == null || rootOnlySteps == null) throw new ArgumentNullException(nameof(worldGenSteps));
            var all = worldGenSteps.Concat(rootOnlySteps).ToArray();
            if (all.Any(string.IsNullOrWhiteSpace) || all.Distinct(StringComparer.Ordinal).Count() != all.Length) throw new ArgumentException("Invalid world-generation step references.");
            if (mode == CelestialBiomeGenerationMode.VanillaSurface && worldGenSteps.Count == 0) throw new ArgumentException("Missing native world-generation steps.");
            if ((mode == CelestialBiomeGenerationMode.Parameterized) != (terrain != null) || terrain != null && terrain.BiomeRules.Count == 0)
                throw new ArgumentException("Parameterized mode requires explicit terrain and biome rules.");
            if (terrain != null)
            {
                // 单通道（2026-11-03）：Parameterized 的原生步骤只允许来自 worldGenSteps。
                // 原生 Terrain 会重选 biome 覆盖参数化结果；Rivers/Lakes 步骤必须与 terrain 开关注定成对，
                // 防止"开了 flag 却没跑步骤/跑了步骤却没做域校验"。
                if (worldGenSteps.Contains("Terrain", StringComparer.Ordinal))
                    throw new ArgumentException("ParameterizedNativeTerrainForbidden: Parameterized world generation must not run the native Terrain step.");
                if (terrain.AllowRivers != worldGenSteps.Contains("Rivers", StringComparer.Ordinal))
                    throw new ArgumentException("RiverStepMismatch: terrain.allowRivers must match the presence of the Rivers world-generation step.");
                if (terrain.AllowLakes != worldGenSteps.Contains("Lakes", StringComparer.Ordinal))
                    throw new ArgumentException("LakeStepMismatch: terrain.allowLakes must match the presence of the Lakes world-generation step.");
            }
            if (terrain != null && (temperature < -273.15 || temperature > 10000 || rainfall > 1000000 / 1.5 ||
                terrain.AllowRivers && terrain.Climate == null && (temperature < -100 || temperature > 100)))
                throw new ArgumentException("Parameterized surface exceeds the native field domain.");
            Id = id;
            Mode = mode;
            Temperature = temperature;
            Rainfall = rainfall;
            Swampiness = swampiness;
            WorldGenSteps = Array.AsReadOnly(worldGenSteps.ToArray());
            RootOnlySteps = Array.AsReadOnly(rootOnlySteps.ToArray());
            Terrain = terrain;
        }
    }
    public sealed class BiomeGenerationResolvedProperties : ICelestialResolvedProperties, ICelestialResolvedConfigurationValidator
    {
        public const string Handler = "PFL.Biomes";
        public string Key { get; }
        public string HandlerId => Handler;
        public int Version => Profile.Terrain == null ? 1 : 2;
        public CelestialGenerationProfile Profile { get; }
        public BiomeGenerationResolvedProperties(string key, CelestialGenerationProfile profile)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            Key = key;
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }
        public ReadOnlyMemory<byte> Serialize() => JsonSerializer.SerializeToUtf8Bytes(Profile);
        public CelestialResult<bool> Validate(IReadOnlyList<ICelestialResolvedProperties> components)
        {
            if (Profile.Terrain == null) return CelestialResult.Ok(true);
            var climates = components.OfType<ExistingEnvironmentResolvedProperties>().Select(p => p.Profile.Climate).Where(c => c != null).ToArray();
            var source = Profile.Terrain.Climate;
            bool matches = source == null ? climates.Length == 0 : climates.Length == 1 &&
                JsonSerializer.SerializeToUtf8Bytes(source).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(climates[0]));
            return matches ? CelestialResult.Ok(true) : CelestialResult.Reject<bool>(new CelestialError("TerrainClimateSourceMismatch", CompKey: Key,
                Detail: "Parameterized terrain must bind the same frozen environment climate using useEnvironmentClimate."));
        }
        public static CelestialResult<BiomeGenerationResolvedProperties> Deserialize(string key, int version, ReadOnlyMemory<byte> payload)
        {
            if (version is not (1 or 2)) return CelestialResult.Reject<BiomeGenerationResolvedProperties>(new CelestialError("UnsupportedConfigurationVersion", CompKey: key));
            try
            {
                var profile = CelestialResolvedJson.Read<CelestialGenerationProfile>(payload);
                if ((version == 2) != (profile.Terrain != null)) throw new ArgumentException("Generation version mismatch.");
                return CelestialResult.Ok(new BiomeGenerationResolvedProperties(key, profile!));
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException)
            { return CelestialResult.Reject<BiomeGenerationResolvedProperties>(new CelestialError("InvalidConfigurationPayload", CompKey: key, Detail: exception.Message)); }
        }
    }
}
