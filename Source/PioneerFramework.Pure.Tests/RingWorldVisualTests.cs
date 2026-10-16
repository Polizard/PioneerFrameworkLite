using PioneerFramework;
using PioneerFramework.RingWorld;
using Xunit;

public class RingWorldVisualTests
{
    [Theory]
    [InlineData(128,6)]
    [InlineData(512,24)]
    [InlineData(256,3)]
    [InlineData(1024,12)]
    [InlineData(64,12)]
    [InlineData(4,2)]
    public void StructuralFacesRemainFiniteAndNonDegenerate(int columns,int rows)
    {
        var geometry = new RingGeometry(new RingTopology(columns,rows),280);
        var quads = RingIndustrialGeometry.Build(geometry).ToArray();
        Assert.NotEmpty(quads);
        Assert.Contains(quads,q=>q.Material==RingVisualMaterial.Pipe);
        Assert.Contains(quads,q=>q.Material==RingVisualMaterial.WarningLight);
        foreach (var q in quads)
        {
            foreach (var p in new[]{q.A,q.B,q.C,q.D})
            {
                Assert.True(double.IsFinite(p.Position.x) && double.IsFinite(p.Position.y) && double.IsFinite(p.Position.z));
                Assert.True(double.IsFinite(p.U) && double.IsFinite(p.V));
            }
            var n1=DVector3.Cross(q.B.Position-q.A.Position,q.C.Position-q.A.Position);
            var n2=DVector3.Cross(q.C.Position-q.A.Position,q.D.Position-q.A.Position);
            Assert.True(n1.magnitude>1e-9);
            Assert.True(n2.magnitude>1e-9);
            Assert.True(DVector3.Dot(n1,n2)>0);
        }
    }

    [Fact]
    public void BackHullClosesExactlyAtSeamAndFacesOutward()
    {
        var geometry=new RingGeometry(new RingTopology(128,6),280);
        var hull=RingIndustrialGeometry.Build(geometry).Where(q=>q.Material==RingVisualMaterial.Hull).ToArray();
        Assert.NotEmpty(hull);
        Assert.Equal(hull[0].A.Position,hull[^1].D.Position);
        Assert.Equal(hull[0].B.Position,hull[^1].C.Position);
        foreach(var q in hull)
        {
            var normal=DVector3.Cross(q.B.Position-q.A.Position,q.C.Position-q.A.Position);
            var center=(q.A.Position+q.B.Position+q.C.Position+q.D.Position)/4;
            Assert.True(DVector3.Dot(normal,new DVector3(center.x,0,center.z))>0);
            Assert.True(Math.Sqrt(center.x*center.x+center.z*center.z)>geometry.Radius);
        }
    }

    [Fact]
    public void HabitatUVIsContinuousAcrossTilesIncludingPeriodicSeam()
    {
        var graph=new RingTopology(128,6);
        var geometry=new RingGeometry(graph,280);
        for(int tile=0;tile<graph.TileCount;tile++)
        {
            var corners=geometry.Vertices(tile);
            var uvs=corners.Select(p=>RingIndustrialGeometry.SurfaceUV(geometry,tile,p)).ToArray();
            Assert.True(uvs.Max(p=>p.U)-uvs.Min(p=>p.U)<.2);
            foreach(int other in graph.Neighbors(tile))
            foreach(var a in corners)
            foreach(var b in geometry.Vertices(other))
                if((a-b).magnitude<1e-9)
                {
                    var uvA=RingIndustrialGeometry.SurfaceUV(geometry,tile,a);
                    var uvB=RingIndustrialGeometry.SurfaceUV(geometry,other,b);
                    Assert.Equal(uvA.V,uvB.V,10);
                    double repeats=(uvA.U-uvB.U)/2;
                    Assert.InRange(Math.Abs(repeats-Math.Round(repeats)),0,1e-10);
                }
        }
    }

    [Fact]
    public void DecorativeDetailHasFixedBudgetForLargeTileGrids()
    {
        var geometry=new RingGeometry(new RingTopology(4096,32),280);
        Assert.InRange(RingIndustrialGeometry.Build(geometry).Count(),1,24000);
        Assert.Equal(131072,geometry.Topology.TileCount);
    }

    [Fact]
    public void GapAboveZigzagHabitatEdgeHasInwardFacingBacking()
    {
        var geometry=new RingGeometry(new RingTopology(128,6),280);
        double h=geometry.RowSpacing*5/2+geometry.Spacing/Math.Sqrt(3);
        // Upper-row centers sit at q+2.5. At q+3 the floor boundary dips below its bounding width.
        double angle=3.0/128*Math.Tau;
        var direction=new DVector3(Math.Cos(angle),0,Math.Sin(angle));
        var origin=new DVector3(0,h-geometry.Spacing/(4*Math.Sqrt(3)),0);
        Assert.Contains(RingIndustrialGeometry.Build(geometry),q=>
        {
            var a=q.A.Position; var b=q.B.Position; var c=q.C.Position; var d=q.D.Position;
            if(DVector3.Dot(DVector3.Cross(b-a,c-a),direction)>=0) return false;
            return HitsTriangle(origin,direction,a,b,c,geometry.Radius,geometry.Radius+geometry.Spacing)
                || HitsTriangle(origin,direction,a,c,d,geometry.Radius,geometry.Radius+geometry.Spacing);
        });
    }

    [Fact]
    public void SideCapTextureCoordinatesJoinAcrossAngularSegments()
    {
        var geometry=new RingGeometry(new RingTopology(128,6),280);
        double radius=geometry.Radius;
        var caps=RingIndustrialGeometry.Build(geometry).Where(q=>
            q.A.Position.y>0 && q.A.Position.y==q.B.Position.y && q.A.Position.y==q.C.Position.y &&
            Math.Abs(Math.Sqrt(q.A.Position.x*q.A.Position.x+q.A.Position.z*q.A.Position.z)-radius)<1e-9 &&
            DVector3.Cross(q.B.Position-q.A.Position,q.C.Position-q.A.Position).y>0).Take(2).ToArray();
        Assert.Equal(2,caps.Length);
        Assert.Equal(caps[0].B.Position,caps[1].A.Position);
        Assert.Equal(caps[0].B.U,caps[1].A.U,10);
        Assert.Equal(caps[0].B.V,caps[1].A.V,10);
    }

    private static bool HitsTriangle(DVector3 origin,DVector3 direction,DVector3 a,DVector3 b,DVector3 c,double min,double max)
    {
        var e1=b-a; var e2=c-a; var p=DVector3.Cross(direction,e2);
        double determinant=DVector3.Dot(e1,p);
        if(Math.Abs(determinant)<1e-12) return false;
        var offset=origin-a;
        double u=DVector3.Dot(offset,p)/determinant;
        var q=DVector3.Cross(offset,e1);
        double v=DVector3.Dot(direction,q)/determinant;
        double distance=DVector3.Dot(e2,q)/determinant;
        return u>=0 && v>=0 && u+v<=1 && distance>min && distance<max;
    }
}
