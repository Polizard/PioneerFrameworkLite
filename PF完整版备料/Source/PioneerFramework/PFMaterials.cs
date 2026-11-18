using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    [StaticConstructorOnStartup]
    public class PFMaterials
    {
        public static Material GetMoonPlanetUngeneratedParts()
        {
            if (MoonPlanetUngeneratedPlanetParts != null)
            {
                return MoonPlanetUngeneratedPlanetParts;
            }
            else
            {
                Texture2D texture = ContentFinder<Texture2D>.Get("World/Biomes/IceSheetOcean");
                MoonPlanetUngeneratedPlanetParts = new Material(ShaderDatabase.WorldTerrain);
                MoonPlanetUngeneratedPlanetParts.mainTexture = texture;
            }
            return MoonPlanetUngeneratedPlanetParts;
        }
        public static Material MoonPlanetUngeneratedPlanetParts;
    }
}
