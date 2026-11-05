using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.Megastructures
{
    public static class SurfaceWorldObjectBridge
    {
        public static Vector3 CellPoint(PlanetLayer layer,int cell,float offset=0)
        {
            if(!ArtificialSurfaceLayers.TryDescribe(layer,out var surface))return layer.GetTileCenter(cell);
            var local=surface.Geometry.EvaluateLocal(cell);
            return SurfaceMotionBridge.Point(layer,local.PositionMeters*SpaceConstants.UnitsPerMeter+local.Normal*offset);
        }
        internal static bool DrawPath(WorldPath __instance,Caravan pathingCaravan)
        {
            var layer=__instance.Layer;if(!ArtificialSurfaceLayers.TryDescribe(layer,out var surface))return true;
            if(!__instance.Found||__instance.NodesLeftCount<=0)return false;
            Vector3 Position(int index)=>CellPoint(layer,__instance.Peek(index).tileId,.08f);
            void Line(Vector3 a,Vector3 b,DVector3 localNormal)
            {
                var delta=b-a;if(delta.sqrMagnitude<1e-6f)return;
                var normal=SurfaceMotionBridge.Direction(layer,localNormal).normalized;
                var matrix=Matrix4x4.TRS((a+b)*.5f,Quaternion.LookRotation(delta,normal),new(layer.AverageTileSize*.1f,1,delta.magnitude));
                var material=MaterialPool.MatFrom(BaseContent.WhiteTex,ShaderDatabase.WorldOverlayTransparent,Color.white,3600);
                Graphics.DrawMesh(MeshPool.plane10,matrix,material,WorldCameraManager.WorldLayer);
            }
            for(int i=0;i<__instance.NodesLeftCount-1;i++)
                Line(Position(i),Position(i+1),surface.Geometry.EvaluateLocal(__instance.Peek(i).tileId).Normal+surface.Geometry.EvaluateLocal(__instance.Peek(i+1).tileId).Normal);
            if(pathingCaravan!=null)
            {
                var normal=surface.Geometry.EvaluateLocal(pathingCaravan.Tile.tileId).Normal;
                Line(pathingCaravan.DrawPos+SurfaceMotionBridge.Direction(layer,normal)*.08f,Position(0),normal);
            }
            return false;
        }
        internal static bool Print(WorldObject __instance,LayerSubMesh subMesh)
        {
            var layer=__instance.Tile.Layer;if(!ArtificialSurfaceLayers.TryDescribe(layer,out var surface))return true;
            var local=surface.Geometry.EvaluateLocal(__instance.Tile.tileId);
            var center=(Vector3)(local.PositionMeters*SpaceConstants.UnitsPerMeter+local.Normal*.06);
            var right=(Vector3)local.Tangent*layer.AverageTileSize*.35f;
            var up=(Vector3)DVector3.Cross(local.Normal,local.Tangent)*layer.AverageTileSize*.35f;
            int start=subMesh.verts.Count;
            subMesh.verts.Add(center-right-up);subMesh.verts.Add(center+right-up);subMesh.verts.Add(center+right+up);subMesh.verts.Add(center-right+up);
            subMesh.uvs.Add(new(0,0));subMesh.uvs.Add(new(1,0));subMesh.uvs.Add(new(1,1));subMesh.uvs.Add(new(0,1));
            SurfaceMeshParts.AddQuadTriangles(subMesh,start);return false;
        }
        internal static bool Draw(WorldObject __instance)
        {
            var layer=__instance.Tile.Layer;if(!ArtificialSurfaceLayers.TryDescribe(layer,out var surface))return true;
            if(__instance.Material==null||!layer.IsSelected)return false;
            var local=surface.Geometry.EvaluateLocal(__instance.Tile.tileId);
            var normal=SurfaceMotionBridge.Direction(layer,local.Normal);
            var up=SurfaceMotionBridge.Direction(layer,DVector3.Cross(local.Normal,local.Tangent));
            var transform=Matrix4x4.TRS(__instance.DrawPos+normal*.06f,Quaternion.LookRotation(up,normal),Vector3.one*(layer.AverageTileSize*.7f));
            Graphics.DrawMesh(MeshPool.plane10,transform,__instance.Material,WorldCameraManager.WorldLayer);return false;
        }
    }
}
