using RimWorld.Planet;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace PioneerFramework
{
    public class WorldGenStep_MoonTiles : WorldGenStep
    {
            internal enum MoonBiomeCategory
            {
                WaterIce,
                Basalt,
                Regolith
            }

            private const float PolarWaterIceLatitude = 75f;

            public override int SeedPart
            {
                get
                {
                    return 58474782;
                }
            }

            public override void GenerateFresh(string seed, PlanetLayer layer)
            {
                layer.Tiles.Clear();
                for (int i = 0; i < layer.TilesCount; i++)
                {
                    Tile item = this.GenerateTileFor(new PlanetTile(i, layer), layer);
                    layer.Tiles.Add(item);
                }
            }

            protected virtual Tile GenerateTileFor(PlanetTile tile, PlanetLayer layer)
            {
                BiomeDef primaryBiome = SelectBiomeFor(tile);
                return new SurfaceTile(tile)
                {
                    PrimaryBiome = primaryBiome,
                    elevation = tile.Layer.Radius,
                    hilliness = SelectHillinessForBiomeDefName(primaryBiome.defName),
                    temperature = 0f,
                    rainfall = 0f,
                    swampiness = 0f
                };
            }

            protected virtual BiomeDef SelectBiomeFor(PlanetTile tile)
            {
                if (tile.LayerDef.defName != "Moon")
                {
                    return tile.LayerDef.DefaultBiome;
                }

                float latitude = Find.WorldGrid.LongLatOf(tile).y;
                MoonBiomeCategory category =
                    SelectMoonBiomeCategory(latitude, Rand.Chance(0.5f));
                if (category == MoonBiomeCategory.WaterIce)
                {
                    return PFDefs.PF_MoonBiomeWater;
                }

                return category == MoonBiomeCategory.Basalt
                    ? PFDefs.PF_MoonBiomeBasalt
                    : PFDefs.PF_MoonBiomeRegolith;
            }

            internal static bool IsWaterIceLatitude(float latitude)
            {
                return Math.Abs(latitude) >= PolarWaterIceLatitude;
            }

            internal static MoonBiomeCategory SelectMoonBiomeCategory(
                float latitude, bool selectBasalt)
            {
                if (IsWaterIceLatitude(latitude))
                {
                    return MoonBiomeCategory.WaterIce;
                }

                return selectBasalt
                    ? MoonBiomeCategory.Basalt
                    : MoonBiomeCategory.Regolith;
            }

            internal static Hilliness SelectHillinessForBiomeDefName(
                string biomeDefName)
            {
                return biomeDefName == "PF_MoonBiomeBasalt"
                    ? Hilliness.SmallHills
                    : Hilliness.Flat;
            }
        }
    }
