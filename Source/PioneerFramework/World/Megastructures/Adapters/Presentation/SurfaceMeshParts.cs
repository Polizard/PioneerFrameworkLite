using System;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.Megastructures
{
    public enum SurfaceMeshRole { Habitat, Decoration, Glow }
    public static class SurfaceMeshParts
    {
        public const int BatchSize=256;
        public static bool CanPick(SurfaceMeshRole role)=>role==SurfaceMeshRole.Habitat;
        public static void AddVertex(LayerSubMesh mesh,DVector3 point,Vector3 uv,DVector3? normal=null,Color32? color=null)
        {
            mesh.verts.Add((Vector3)point);mesh.uvs.Add(uv);
            if(normal.HasValue)mesh.normals.Add((Vector3)normal.Value);
            if(color.HasValue)mesh.colors.Add(color.Value);
        }
        public static void AddQuadTriangles(LayerSubMesh mesh,int start)
        {mesh.tris.Add(start);mesh.tris.Add(start+1);mesh.tris.Add(start+2);mesh.tris.Add(start);mesh.tris.Add(start+2);mesh.tris.Add(start+3);}
        public static void AddCellFan(LayerSubMesh mesh,int start,int corners,SurfaceMeshRole role,Action mapTriangle)
        {
            if(corners<3)throw new ArgumentOutOfRangeException(nameof(corners));
            for(int i=0;i<corners;i++)
            {
                mesh.tris.Add(start);mesh.tris.Add(start+1+i);mesh.tris.Add(start+1+(i+1)%corners);
                if(CanPick(role))mapTriangle();
            }
        }
    }
}
