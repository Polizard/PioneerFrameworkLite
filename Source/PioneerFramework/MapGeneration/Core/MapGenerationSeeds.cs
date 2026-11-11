using System;
using System.Globalization;
using PioneerFramework.Celestial;

namespace PioneerFramework.MapGeneration
{
    public static class MapGenerationSeeds
    {
        public static ulong Root(MapSourceContext source)
        {
            ArgumentNullException.ThrowIfNull(source);
            ulong owner = CelestialSeed.Derive(source.GenerationSeed, source.SourceId, "local-map", "root.v1");
            ulong tile = CelestialSeed.DeriveTile(owner, source.TileId, "local-map", "tile.v1");
            return CelestialSeed.Derive(tile, source.SizeX.ToString(CultureInfo.InvariantCulture), source.SizeZ.ToString(CultureInfo.InvariantCulture), "size.v1");
        }
        public static ulong ForStep(MapSourceContext source, MapGenerationPurpose purpose, MapGenerationStepSpec step)
        {
            ArgumentNullException.ThrowIfNull(step);
            if (!Enum.IsDefined(purpose)) throw new ArgumentOutOfRangeException(nameof(purpose));
            string domain = step.Domain == MapGenerationDomain.Natural ? "natural" : "purpose/" + ((int)purpose).ToString(CultureInfo.InvariantCulture);
            return CelestialSeed.Derive(Root(source), domain, step.Key, step.AlgorithmVersion.ToString(CultureInfo.InvariantCulture));
        }
    }
}
