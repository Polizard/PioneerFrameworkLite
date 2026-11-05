using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
namespace PioneerFramework.RingWorld
{
    public abstract class WorldDrawLayer_RingHighlight:Megastructures.WorldDrawLayer_SurfaceHighlight { }
    public sealed class WorldDrawLayer_RingSelected:WorldDrawLayer_RingHighlight
    {
        protected override PlanetTile Target=>Find.WorldSelector.SelectedTile;
        protected override Material HighlightMaterial=>WorldMaterials.SelectedTile;
    }
    public sealed class WorldDrawLayer_RingMouse:WorldDrawLayer_RingHighlight
    {
        protected override PlanetTile Target=>GenWorld.MouseTile();
        protected override Material HighlightMaterial=>WorldMaterials.MouseTile;
    }
}
