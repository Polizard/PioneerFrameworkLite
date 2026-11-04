using System;
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
        private byte[] configuration = Array.Empty<byte>();
        private string? serializedConfiguration = "";
        private CelestialSurfaceState state;
        public CelestialInstanceId OwnerId => CelestialInstanceId.Parse(owner ?? throw new InvalidDataException("Missing celestial surface owner."));
        public int LayerId => layerId;
        public double PhysicalRadiusMeters => physicalRadius;
        public float NativeRadius => nativeRadius;
        public int Subdivisions => subdivisions;
        public int GenerationSeed => generationSeed;
        public ReadOnlyMemory<byte> GenerationConfiguration => configuration;
        public CelestialSurfaceState State { get => state; internal set => state = value; }
        public CelestialSurfaceHeader() { }
        public CelestialSurfaceHeader(CelestialInstanceId ownerId, int layerId, double physicalRadiusMeters, float nativeRadius,
            int subdivisions, int generationSeed, ReadOnlyMemory<byte> generationConfiguration)
        {
            owner = ownerId.Value;
            this.layerId = layerId;
            physicalRadius = physicalRadiusMeters;
            this.nativeRadius = nativeRadius;
            this.subdivisions = subdivisions;
            this.generationSeed = generationSeed;
            configuration = generationConfiguration.ToArray();
            Validate();
        }
        internal CelestialSurfaceHeader WithLayerId(int id) => new(OwnerId, id, physicalRadius, nativeRadius, subdivisions, generationSeed, configuration) { State = state };

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
            Scribe_Values.Look(ref physicalRadius, "physicalRadiusMeters", 0d);
            Scribe_Values.Look(ref nativeRadius, "nativeRadius", 0f);
            Scribe_Values.Look(ref subdivisions, "subdivisions", 0);
            Scribe_Values.Look(ref generationSeed, "generationSeed", 0);
            Scribe_Values.Look(ref serializedConfiguration, "generationConfiguration");
            Scribe_Values.Look(ref state, "state", CelestialSurfaceState.Generating);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                configuration = Convert.FromBase64String(serializedConfiguration ?? throw new InvalidDataException("Missing celestial surface configuration."));
                Validate();
            }
        }
        private void Validate()
        {
            if (formatVersion != 1 || string.IsNullOrWhiteSpace(owner) || layerId < -1 || !double.IsFinite(physicalRadius) || physicalRadius <= 0 ||
                !float.IsFinite(nativeRadius) || nativeRadius <= 0 || subdivisions < 0 || subdivisions > SurfaceResolvedProperties.MaxBuiltinSubdivisions ||
                !Enum.IsDefined(state)) throw new InvalidDataException("Invalid celestial surface header; builtin subdivisions must be within 0..10.");
        }
    }
}
