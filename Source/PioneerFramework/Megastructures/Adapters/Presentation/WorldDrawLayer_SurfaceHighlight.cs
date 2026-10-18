using System.Collections;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.Megastructures
{
    public abstract class WorldDrawLayer_SurfaceHighlight : WorldDrawLayer
    {
        private PlanetTile lastTile = PlanetTile.Invalid;
        protected abstract PlanetTile Target { get; }
        protected abstract Material HighlightMaterial { get; }
        public override bool VisibleWhenLayerNotSelected => false;
        public override bool ShouldRegenerate => base.ShouldRegenerate || lastTile != Target;
        public override IEnumerable Regenerate()
        {
            foreach (object item in base.Regenerate()) yield return item;
            PlanetTile tile = Target; lastTile = tile;
            if (!ArtificialSurfaceLayers.TryDescribe(planetLayer,out var surface)||!tile.Valid||tile.Layer!=planetLayer)yield break;
            var local=surface.Geometry.EvaluateLocal(tile.tileId);var offset=local.Normal*.04;
            var mesh=GetSubMesh(HighlightMaterial);int start=mesh.verts.Count;
            SurfaceMeshParts.AddVertex(mesh,local.PositionMeters*SpaceConstants.UnitsPerMeter+offset,new Vector3(.5f,.5f,0));
            var polygon=surface.Geometry.GetPolygon(tile.tileId);
            foreach(var point in polygon)SurfaceMeshParts.AddVertex(mesh,point*SpaceConstants.UnitsPerMeter+offset,Vector3.zero);
            SurfaceMeshParts.AddCellFan(mesh,start,polygon.Count,SurfaceMeshRole.Decoration,()=>{});
            FinalizeMesh(MeshParts.All);
        }
    }
    public sealed class WorldDrawLayer_SurfaceSelected : WorldDrawLayer_SurfaceHighlight
    {
        protected override PlanetTile Target => Find.WorldSelector.SelectedTile;
        protected override Material HighlightMaterial => WorldMaterials.SelectedTile;
    }
    public sealed class WorldDrawLayer_SurfaceMouse : WorldDrawLayer_SurfaceHighlight
    {
        protected override PlanetTile Target => GenWorld.MouseTile();
        protected override Material HighlightMaterial => WorldMaterials.MouseTile;
    }
}
