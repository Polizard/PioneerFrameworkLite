using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

/// <summary>
/// Frozen baseline for the shared seed derivations introduced in the 2026-11-03 P4 pass.
/// In particular `CelestialPlanetMapHooks.SoilTerrain` replaced its hand-written 32-bit hash with
/// <see cref="CelestialSeed.DeriveCell"/>; soil distribution intentionally changes versus older
/// builds and the new values are pinned here instead of being silently re-derived.
/// </summary>
public class CelestialMapDeterminismTests
{
    [Fact]
    public void TileCellAndCreateDerivationsKeepTheirFrozenBytes()
    {
        Assert.Equal(0xF55673D80E4CEE86UL, CelestialSeed.DeriveTile(1, 0, "terrain", "biome"));
        Assert.Equal(0xFFF12F1D194C2CB9UL, CelestialSeed.DeriveTile(42, 1234, "map", "rocks"));
        Assert.Equal(0x6312198BB36A1090UL, CelestialSeed.DeriveTile(ulong.MaxValue, 17, "map", "rocks"));
        Assert.Equal(0x8E18C5C538F01EE0UL, CelestialSeed.DeriveCell(42, 7, 3, -5, "map", "soil"));
        Assert.Equal(0x19F8E78AC4B7FE58UL, CelestialSeed.DeriveCell(0, 0, 0, 0, "map", "soil"));
        Assert.Equal(0x7CB3D0DA6024AA8CUL, CelestialSeed.CreateSeed(99, "operation/abc/x[0]"));
        Assert.Equal(0x75DF7F4883DF204EUL, CelestialSeed.SurfaceSeed(7, "moon[0]", "surface"));
    }

    [Fact]
    public void DerivationIsStableForTheSameInputsAndSeparatedAcrossInputs()
    {
        Assert.Equal(CelestialSeed.DeriveCell(42, 7, 3, -5, "map", "soil"), CelestialSeed.DeriveCell(42, 7, 3, -5, "map", "soil"));
        Assert.NotEqual(CelestialSeed.DeriveCell(42, 7, 3, -5, "map", "soil"), CelestialSeed.DeriveCell(42, 7, 3, -4, "map", "soil"));
        Assert.NotEqual(CelestialSeed.DeriveCell(42, 7, 3, -5, "map", "soil"), CelestialSeed.DeriveCell(42, 7, 4, -5, "map", "soil"));
        Assert.NotEqual(CelestialSeed.DeriveTile(42, 1234, "map", "rocks"), CelestialSeed.DeriveTile(43, 1234, "map", "rocks"));
        Assert.NotEqual(CelestialSeed.DeriveTile(42, 1234, "map", "rocks"), CelestialSeed.DeriveTile(42, 1234, "map", "soil"));
    }
}
