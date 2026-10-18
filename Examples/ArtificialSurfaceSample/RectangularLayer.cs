using PioneerFramework;
using PioneerFramework.Megastructures;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PFL.ArtificialSurfaceSample
{
    public sealed class RectangularLayer:SurfaceLayer,IArtificialSurfaceLayer,IArtificialSurfaceSaveHeader
    {
        private string stableId="rectangle";
        private int parentBodyId,columns=3,rows=2;
        public ArtificialSurfaceDescriptor Surface {get;private set;}=null!;
        public RectangularLayer()=>Configure("rectangle",0,3,2);
        public RectangularLayer(int layerId,PlanetLayerDef def,float radius,Vector3 origin,Vector3 viewCenter,float viewAngle,int subdivisions,float extraCameraAltitude,float backgroundWorldCameraOffset,float backgroundWorldCameraParallaxDistance)
            :base(layerId,def,radius,origin,viewCenter,viewAngle,subdivisions,extraCameraAltitude,backgroundWorldCameraOffset,backgroundWorldCameraParallaxDistance)=>Configure("rectangle",0,3,2);
        public void Configure(string id,int parent,int width,int height)
        {
            stableId=id;parentBodyId=parent;columns=width;rows=height;
            Surface=new(){Backend=new(RectangularBackend.ProviderId,id),ParentBodyId=parent,Geometry=new RectangularGeometry(width,height),Motion=new(new(0,-1,0),60000),Environment=new(),View=new RectangularView(),ImportRequestFactory=(actor,instance)=>RectangularBackend.Request(id,parent,instance,actor,true)};
        }
        public void GenerateHabitatTiles()
        {
            tiles.Clear();var biome=DefDatabase<BiomeDef>.GetNamed("TemperateForest");
            for(int i=0;i<Surface.Geometry.CellCount;i++)tiles.Add(SurfaceEnvironmentBridge.CreateInitialTile(new(i,this),biome,Surface.Environment!));
        }
        public void ExposeSurfaceHeader()
        {
            Scribe_Values.Look(ref stableId,"sampleSurfaceId","rectangle");Scribe_Values.Look(ref parentBodyId,"sampleParent",0);
            Scribe_Values.Look(ref columns,"sampleColumns",3);Scribe_Values.Look(ref rows,"sampleRows",2);
            if(Scribe.mode==LoadSaveMode.LoadingVars)Configure(stableId,parentBodyId,columns,rows);
        }
    }
    public sealed class RectangularView:ISurfaceViewGeometry
    {
        public float Radius=>20;
        public float Bound=>20;
        public float InnerAltitude=>300;
        public DVector3 Project(DVector3 direction)=>new(System.Math.Clamp(direction.x,-3,3),0,System.Math.Clamp(direction.z,-2,2));
        public DVector3 NormalAt(DVector3 local)=>new(0,1,0);
        public Vector3 PresetDirection=>new(0,1,-1);
        public float PresetAltitude(float aspect)=>600;
    }
    public sealed class RectangularDrawLayer:WorldDrawLayer
    {
        public override System.Collections.IEnumerable Regenerate()
        {
            foreach(var item in base.Regenerate())yield return item;
            if(planetLayer is not IArtificialSurfaceLayer provider)yield break;
            var shape=provider.Surface.Geometry;
            var material=MaterialPool.MatFrom(BaseContent.WhiteTex,ShaderDatabase.WorldOverlayTransparent,Color.gray,3500);
            for(int cell=0;cell<shape.CellCount;cell++)
            {
                var mesh=GetSubMesh(material,out int meshIndex);int start=mesh.verts.Count;
                SurfaceMeshParts.AddVertex(mesh,shape.EvaluateLocal(cell).PositionMeters*SpaceConstants.UnitsPerMeter,new(.5f,.5f));
                var corners=shape.GetPolygon(cell);
                foreach(var p in corners)SurfaceMeshParts.AddVertex(mesh,p*SpaceConstants.UnitsPerMeter,new((float)p.x,(float)p.z));
                int captured=cell;SurfaceMeshParts.AddCellFan(mesh,start,corners.Count,SurfaceMeshRole.Habitat,()=>AppendRaycastableTriangle(meshIndex,captured));
                if(cell%SurfaceMeshParts.BatchSize==0)yield return null;
            }
            FinalizeMesh(MeshParts.All);
            foreach(var item in RegenerateWorldMeshColliders())yield return item;
        }
    }
}
