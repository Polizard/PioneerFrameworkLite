using System;
using System.Globalization;
using System.IO;
using Verse;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialSurfaceHeader : IExposable
    {
        private int formatVersion = 1;
        private string? owner = "";
        private int layerId = -1;
        private double physicalRadius;
        private float nativeRadius;
        private int subdivisions;
        private int generationSeed;
        private int terrainAlgorithmVersion = CelestialPlanetTerrain.LegacyVersionWithoutHeaderField;
        private byte[] configuration = Array.Empty<byte>();
        private string? serializedConfiguration = "";
        private CelestialSurfaceState state;
        public CelestialInstanceId OwnerId => CelestialInstanceId.Parse(owner ?? throw new InvalidDataException("Missing celestial surface owner."));
        public int LayerId => layerId;
        public double PhysicalRadiusMeters => physicalRadius;
        public float NativeRadius => nativeRadius;
        public int Subdivisions => subdivisions;
        public int GenerationSeed => generationSeed;
        /// <summary>生成世界层 tile 时使用的地形数值语义版本；运行期采样与地图期地质必须与之一致。</summary>
        public int TerrainAlgorithmVersion => terrainAlgorithmVersion;
        public ReadOnlyMemory<byte> GenerationConfiguration => configuration;
        public CelestialSurfaceState State { get => state; internal set => state = value; }
        public CelestialSurfaceHeader() { }
        public CelestialSurfaceHeader(CelestialInstanceId ownerId, int layerId, double physicalRadiusMeters, float nativeRadius,
            int subdivisions, int generationSeed, ReadOnlyMemory<byte> generationConfiguration,
            int terrainAlgorithmVersion = CelestialPlanetTerrain.AlgorithmVersion)
        {
            owner = ownerId.Value;
            this.layerId = layerId;
            physicalRadius = physicalRadiusMeters;
            this.nativeRadius = nativeRadius;
            this.subdivisions = subdivisions;
            this.generationSeed = generationSeed;
            this.terrainAlgorithmVersion = terrainAlgorithmVersion;
            configuration = generationConfiguration.ToArray();
            Validate();
        }
        internal CelestialSurfaceHeader WithLayerId(int id) => new(OwnerId, id, physicalRadius, nativeRadius, subdivisions, generationSeed,
            configuration, terrainAlgorithmVersion) { State = state };

        internal void RestorePhysicalRadius(double exactRadius) => physicalRadius = exactRadius;

        /// <summary>Shared header construction for both root bootstrap and on-demand surface creation.</summary>
        internal static CelestialSurfaceHeader Create(CelestialBodyInstance instance, SurfaceResolvedProperties properties)
        {
            ArgumentNullException.ThrowIfNull(instance);
            ArgumentNullException.ThrowIfNull(properties);
            ulong seed = CelestialSeed.SurfaceSeed(instance.GenerationOrigin.WorldSeed, instance.GenerationOrigin.GenerationKey, properties.Key);
            return new CelestialSurfaceHeader(instance.Id, -1, instance.Description.RadiusMeters,
                (float)(instance.Description.RadiusMeters / properties.MetersPerLayerUnit), properties.Subdivisions,
                unchecked((int)(seed ^ (seed >> 32))), CelestialGenerationConfiguration.Encode(instance.CompStateRecords.Values));
        }
        public void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) serializedConfiguration = Convert.ToBase64String(configuration);
            Scribe_Values.Look(ref formatVersion, "formatVersion", 1);
            Scribe_Values.Look(ref owner, "owner");
            Scribe_Values.Look(ref layerId, "layerId", -1);
            string? serializedPhysicalRadius = Scribe.mode == LoadSaveMode.Saving
                ? physicalRadius.ToString("G17", CultureInfo.InvariantCulture) : null;
            Scribe_Values.Look(ref serializedPhysicalRadius, "physicalRadiusMeters");
            Scribe_Values.Look(ref nativeRadius, "nativeRadius", 0f);
            Scribe_Values.Look(ref subdivisions, "subdivisions", 0);
            Scribe_Values.Look(ref generationSeed, "generationSeed", 0);
            // 旧 v4 档没有该字段：按"字段引入前"的语义版本读取，再由 Validate 与当前版本比较。
            Scribe_Values.Look(ref terrainAlgorithmVersion, "terrainAlgorithmVersion", CelestialPlanetTerrain.LegacyVersionWithoutHeaderField);
            Scribe_Values.Look(ref serializedConfiguration, "generationConfiguration");
            Scribe_Values.Look(ref state, "state", CelestialSurfaceState.Generating);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                physicalRadius = double.Parse(serializedPhysicalRadius ?? throw new InvalidDataException("Missing celestial surface radius."),
                    CultureInfo.InvariantCulture);
                configuration = Convert.FromBase64String(serializedConfiguration ?? throw new InvalidDataException("Missing celestial surface configuration."));
                Validate();
            }
        }
        private void Validate()
        {
            if (terrainAlgorithmVersion != CelestialPlanetTerrain.AlgorithmVersion)
                throw new InvalidDataException("UnsupportedTerrainAlgorithmVersion: saved=" +
                    terrainAlgorithmVersion.ToString(CultureInfo.InvariantCulture) + " current=" +
                    CelestialPlanetTerrain.AlgorithmVersion.ToString(CultureInfo.InvariantCulture) + ".");
            if (formatVersion != 1 || string.IsNullOrWhiteSpace(owner) || layerId < -1 || !double.IsFinite(physicalRadius) || physicalRadius <= 0 ||
                !float.IsFinite(nativeRadius) || nativeRadius <= 0 || subdivisions < 0 || subdivisions > SurfaceResolvedProperties.MaxBuiltinSubdivisions ||
                !Enum.IsDefined(state)) throw new InvalidDataException("Invalid celestial surface header; builtin subdivisions must be within 0..10.");
        }
    }
}
