using System;
using System.Buffers.Binary;
using PioneerFramework.MapGeneration;
using Verse;

namespace PFL.CelestialCompSample
{
    // RimWorld constructs Mod instances before loading and validating Def XML.
    public sealed class CelestialCompSampleMod : Mod
    {
        public CelestialCompSampleMod(ModContentPack content) : base(content) =>
            SampleLandingModule.Register(LocalMapGenerationModules.Registry);
    }

    public sealed class SampleLandingProperties : LocalMapModuleProperties
    {
        public int eastOfCenter;

        public override ReadOnlyMemory<byte> FreezeConfiguration()
        {
            var bytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, eastOfCenter);
            return bytes;
        }
    }

    public sealed class SampleLandingModule : IMapGenerationModule
    {
        public const string HandlerId = "PFL.CelestialCompSample.LandingCell";
        public const string WorkingDataKey = "PFL.CelestialCompSample.landingCell";
        private readonly int eastOfCenter;

        private SampleLandingModule(int eastOfCenter) => this.eastOfCenter = eastOfCenter;

        public static void Register(MapGenerationRegistry registry) =>
            registry.Register(new(HandlerId, 1, new[] { 1 },
                bytes => _ = Decode(bytes), bytes => new SampleLandingModule(Decode(bytes))));

        private static int Decode(ReadOnlyMemory<byte> bytes)
        {
            if (bytes.Length != sizeof(int)) throw new ArgumentException("Invalid sample landing configuration.");
            int offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.Span);
            if (offset is < -32 or > 32) throw new ArgumentOutOfRangeException(nameof(bytes), "Landing offset must be in [-32, 32].");
            return offset;
        }

        public void Execute(MapGenerationContext context)
        {
            var map = context.RequireService<INativeMapGenerationServices>().Map;
            var cell = map.Center + new IntVec3(eastOfCenter, 0, 0);
            if (!cell.InBounds(map)) throw new InvalidOperationException("Sample landing cell is outside the generated map.");
            MapGenerator.SetVar(WorkingDataKey, cell);
        }
    }
}
