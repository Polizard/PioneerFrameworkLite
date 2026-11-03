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
    public enum CelestialBiomeGenerationMode { LatitudeBands, VanillaSurface, Parameterized }
    public sealed record CelestialBiomeSelection(string BiomeId, int Hilliness);
    public sealed class CelestialGenerationProfile
    {
        [JsonPropertyName("Id"), JsonPropertyOrder(1)]
        public string Id { get; }
        [JsonPropertyName("Mode"), JsonPropertyOrder(2)]
        public CelestialBiomeGenerationMode Mode { get; }
        [JsonPropertyName("PolarBiomeId"), JsonPropertyOrder(3)]
        public string? PolarBiomeId { get; }
        [JsonPropertyName("PrimaryBiomeId"), JsonPropertyOrder(4)]
        public string? PrimaryBiomeId { get; }
        [JsonPropertyName("SecondaryBiomeId"), JsonPropertyOrder(5)]
        public string? SecondaryBiomeId { get; }
        [JsonPropertyName("PolarLatitude"), JsonPropertyOrder(6)]
        public double PolarLatitude { get; }
        [JsonPropertyName("SecondaryChance"), JsonPropertyOrder(7)]
        public double SecondaryChance { get; }
        [JsonPropertyName("PolarHilliness"), JsonPropertyOrder(8)]
        public int PolarHilliness { get; }
        [JsonPropertyName("PrimaryHilliness"), JsonPropertyOrder(9)]
        public int PrimaryHilliness { get; }
        [JsonPropertyName("SecondaryHilliness"), JsonPropertyOrder(10)]
        public int SecondaryHilliness { get; }
        [JsonPropertyName("Temperature"), JsonPropertyOrder(11)]
        public float Temperature { get; }
        [JsonPropertyName("Rainfall"), JsonPropertyOrder(12)]
        public float Rainfall { get; }
        [JsonPropertyName("Swampiness"), JsonPropertyOrder(13)]
        public float Swampiness { get; }
        [JsonPropertyName("WorldGenSteps"), JsonPropertyOrder(14)]
        public IReadOnlyList<string> WorldGenSteps { get; }
        [JsonPropertyName("RootOnlySteps"), JsonPropertyOrder(15)]
        public IReadOnlyList<string> RootOnlySteps { get; }
        [JsonPropertyName("Terrain"), JsonPropertyOrder(16), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CelestialPlanetTerrainConfiguration? Terrain { get; }
        [JsonConstructor]
        public CelestialGenerationProfile(string id, CelestialBiomeGenerationMode mode, string? polarBiomeId, string? primaryBiomeId,
            string? secondaryBiomeId, double polarLatitude, double secondaryChance, int polarHilliness, int primaryHilliness,
            int secondaryHilliness, float temperature, float rainfall, float swampiness, IReadOnlyList<string> worldGenSteps, IReadOnlyList<string> rootOnlySteps,
            CelestialPlanetTerrainConfiguration? terrain = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            if (!double.IsFinite(polarLatitude) || polarLatitude < 0 || polarLatitude > 90) throw new ArgumentOutOfRangeException(nameof(polarLatitude));
            if (!double.IsFinite(secondaryChance) || secondaryChance < 0 || secondaryChance > 1) throw new ArgumentOutOfRangeException(nameof(secondaryChance));
            if (!float.IsFinite(temperature) || !float.IsFinite(rainfall) || rainfall < 0 || !float.IsFinite(swampiness) || swampiness < 0 || swampiness > 1)
                throw new ArgumentOutOfRangeException(nameof(temperature));
            if (mode == CelestialBiomeGenerationMode.LatitudeBands && new[] { polarBiomeId, primaryBiomeId, secondaryBiomeId }.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Latitude profile requires explicit biome references.");
            if (worldGenSteps == null || rootOnlySteps == null) throw new ArgumentNullException(nameof(worldGenSteps));
            var all = worldGenSteps.Concat(rootOnlySteps).ToArray();
            if (all.Any(string.IsNullOrWhiteSpace) || all.Distinct(StringComparer.Ordinal).Count() != all.Length) throw new ArgumentException("Invalid world-generation step references.");
            if (mode == CelestialBiomeGenerationMode.VanillaSurface && worldGenSteps.Count == 0) throw new ArgumentException("Missing native world-generation steps.");
            if ((mode == CelestialBiomeGenerationMode.Parameterized) != (terrain != null) || terrain != null && terrain.BiomeRules.Count == 0)
                throw new ArgumentException("Parameterized mode requires explicit terrain and biome rules.");
            if (terrain != null && (temperature < -273.15 || temperature > 10000 || rainfall > 1000000 / 1.5 ||
                terrain.AllowRivers && terrain.Climate == null && (temperature < -100 || temperature > 100)))
                throw new ArgumentException("Parameterized surface exceeds the native field domain.");
            Id = id;
            Mode = mode;
            PolarBiomeId = polarBiomeId;
            PrimaryBiomeId = primaryBiomeId;
            SecondaryBiomeId = secondaryBiomeId;
            PolarLatitude = polarLatitude;
            SecondaryChance = secondaryChance;
            PolarHilliness = polarHilliness;
            PrimaryHilliness = primaryHilliness;
            SecondaryHilliness = secondaryHilliness;
            Temperature = temperature;
            Rainfall = rainfall;
            Swampiness = swampiness;
            WorldGenSteps = Array.AsReadOnly(worldGenSteps.ToArray());
            RootOnlySteps = Array.AsReadOnly(rootOnlySteps.ToArray());
            Terrain = terrain;
        }
        public CelestialBiomeSelection SelectBiome(double latitudeDegrees, bool secondary)
        {
            if (Mode != CelestialBiomeGenerationMode.LatitudeBands) throw new InvalidOperationException("Profile uses native generation.");
            if (!double.IsFinite(latitudeDegrees) || Math.Abs(latitudeDegrees) > 90) throw new ArgumentOutOfRangeException(nameof(latitudeDegrees));
            return Math.Abs(latitudeDegrees) >= PolarLatitude ? new(PolarBiomeId!, PolarHilliness) : secondary ? new(SecondaryBiomeId!, SecondaryHilliness) : new(PrimaryBiomeId!, PrimaryHilliness);
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
