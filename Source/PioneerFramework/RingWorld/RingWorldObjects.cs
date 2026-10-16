using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    internal static class RingWorldObjects
    {
        internal static bool DrawPath(WorldPath __instance,Caravan pathingCaravan)
        {
            if(__instance.Layer is not RingSurfaceLayer ring) return true;
            if(!__instance.Found || __instance.NodesLeftCount<=0) return false;
            Vector3 Position(int index)
            {
                int tile=__instance.Peek(index).tileId;
                return RingWorldMotion.Point(ring,ring.Geometry.Center(tile)+ring.Geometry.Normal(tile)*.08);
            }
            void Line(Vector3 a,Vector3 b)
            {
                Vector3 delta=b-a;if(delta.sqrMagnitude<1e-6f) return;
                Vector3 center=(a+b)*.5f;
                Vector3 relative=center-ring.Origin;
                Vector3 normal=new Vector3(-relative.x,0,-relative.z).normalized;
                var matrix=Matrix4x4.TRS(center,Quaternion.LookRotation(delta,normal),new Vector3(ring.AverageTileSize*.1f,1,delta.magnitude));
                var material=MaterialPool.MatFrom(BaseContent.WhiteTex,ShaderDatabase.WorldOverlayTransparent,Color.white,3600);
                Graphics.DrawMesh(MeshPool.plane10,matrix,material,WorldCameraManager.WorldLayer);
            }
            for(int i=0;i<__instance.NodesLeftCount-1;i++) Line(Position(i),Position(i+1));
            if(pathingCaravan!=null) Line(pathingCaravan.DrawPos+RingWorldMotion.Direction(ring.Geometry.Normal(pathingCaravan.Tile.tileId))*.08f,Position(0));
            return false;
        }
        internal static bool Print(WorldObject __instance, LayerSubMesh subMesh)
        {
            if (__instance.Tile.Layer is not RingSurfaceLayer ring) return true;
            int tile=__instance.Tile.tileId;
            Vector3 center=(Vector3)ring.Geometry.Center(tile)+(Vector3)ring.Geometry.Normal(tile)*.06f;
            Vector3 right=(Vector3)ring.Geometry.Tangent(tile)*ring.AverageTileSize*.35f;
            Vector3 up=Vector3.up*ring.AverageTileSize*.35f;
            int start=subMesh.verts.Count;
            subMesh.verts.Add(center-right-up);subMesh.verts.Add(center+right-up);
            subMesh.verts.Add(center+right+up);subMesh.verts.Add(center-right+up);
            subMesh.uvs.Add(new Vector2(0,0));subMesh.uvs.Add(new Vector2(1,0));
            subMesh.uvs.Add(new Vector2(1,1));subMesh.uvs.Add(new Vector2(0,1));
            foreach(int index in new[]{0,1,2,0,2,3}) subMesh.tris.Add(start+index);
            return false;
        }
        internal static bool Draw(WorldObject __instance)
        {
            if (__instance.Tile.Layer is not RingSurfaceLayer ring) return true;
            if (__instance.Material==null || !ring.IsSelected) return false;
            Vector3 normal=RingWorldMotion.Direction(ring.Geometry.Normal(__instance.Tile.tileId));
            Matrix4x4 transform=Matrix4x4.TRS(__instance.DrawPos+normal*.06f,
                Quaternion.LookRotation(Vector3.up,normal),Vector3.one*(ring.AverageTileSize*.7f));
            Graphics.DrawMesh(MeshPool.plane10,transform,__instance.Material,WorldCameraManager.WorldLayer);
            return false;
        }
    }
}
