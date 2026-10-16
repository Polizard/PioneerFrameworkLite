using PioneerFramework;
using PioneerFramework.RingWorld;
using Xunit;

public class RingWorldTests
{
    [Theory]
    [InlineData(64,12,768,128)]
    [InlineData(256,48,12288,512)]
    public void PeriodicGraphHasSymmetricNeighborsAndBoundedEdges(int columns,int rows,int count,int edges)
    {
        var graph=new RingTopology(columns,rows);
        Assert.Equal(count,graph.TileCount);
        int edgeCount=0;
        for(int tile=0;tile<count;tile++)
        {
            var neighbors=graph.Neighbors(tile);
            Assert.Equal(neighbors.Length,neighbors.Distinct().Count());
            if(neighbors.Length==4) edgeCount++; else Assert.Equal(6,neighbors.Length);
            foreach(int other in neighbors) Assert.Contains(tile,graph.Neighbors(other));
        }
        Assert.Equal(edges,edgeCount);
        Assert.Contains(graph.TileId(columns-1,3),graph.Neighbors(graph.TileId(0,3)));
    }

    [Fact]
    public void SeamPathTakesThreeStepsAndEntireGraphIsReachable()
    {
        var graph=new RingTopology(256,48);
        var path=RingPathSearch.Find(graph,graph.TileId(1,20),graph.TileId(254,20));
        Assert.Equal(3,path.Cost);
        Assert.Equal(4,path.Nodes.Count);
        var seen=new HashSet<int>{0}; var queue=new Queue<int>(); queue.Enqueue(0);
        while(queue.TryDequeue(out int tile)) foreach(int n in graph.Neighbors(tile)) if(seen.Add(n)) queue.Enqueue(n);
        Assert.Equal(12288,seen.Count);
    }

    [Fact]
    public void PathAvoidsExpensiveEdgeAndHonorsBlockedGoalAndTerminator()
    {
        var graph=new RingTopology(8,3);
        var path=RingPathSearch.Find(graph,8,9,(a,b)=>a==8&&b==9?100:1);
        Assert.True(path.Found); Assert.Equal(2,path.Cost); Assert.Equal(3,path.Nodes.Count);
        Assert.False(RingPathSearch.Find(graph,8,9,passable:t=>t!=9).Found);
        Assert.False(RingPathSearch.Find(graph,8,12,terminator:c=>c>1).Found);
        Assert.Throws<ArgumentOutOfRangeException>(()=>RingPathSearch.Find(graph,8,9,(a,b)=>double.NaN));
    }

    [Fact]
    public void DistanceUsesPeriodicAxialCoordinatesEvenForTallNarrowRing()
    {
        var graph=new RingTopology(4,100);
        Assert.Equal(99,graph.Distance(graph.TileId(0,0),graph.TileId(0,99)));
        Assert.Equal(1,graph.Distance(graph.TileId(0,5),graph.TileId(3,5)));
    }

    [Fact]
    public void AdjacentTilesShareTwoCornersAcrossSeamAndFaceInwards()
    {
        var geometry=new RingGeometry(new RingTopology(64,12),200);
        var a=geometry.Vertices(64*5+63); var b=geometry.Vertices(64*5);
        Assert.Equal(2,a.Count(v=>b.Any(w=>(v-w).magnitude<1e-9)));
        var center=geometry.Center(0); var normal=geometry.Normal(0);
        Assert.InRange(DVector3.Dot(normal,new DVector3(center.x,0,center.z)),-201,-199);
        var verts=geometry.Vertices(0);
        Assert.True(DVector3.Dot(DVector3.Cross(verts[1]-verts[0],verts[2]-verts[0]),normal)>0);
        Assert.InRange(Math.Abs(normal.magnitude-1),0,1e-12);
    }

    [Fact]
    public void HeadingAndClosestTileRespectCylinderWidth()
    {
        var geometry=new RingGeometry(new RingTopology(64,12),200);
        for(int i=0;i<768;i++) Assert.Equal(i,geometry.ClosestTile(geometry.Center(i)));
        Assert.Equal(90,geometry.Heading(64*5,64*5+1),8);
        Assert.Equal(270,geometry.Heading(64*5,64*5+63),8);
    }

    [Fact]
    public void InnerViewProjectionStaysOnCylinderAtBothPolesAndAtWidthLimits()
    {
        var geometry = new RingGeometry(new RingTopology(64,12),280);
        var directions = new[]
        {
            new DVector3(0,1,0), new DVector3(0,-1,0),
            new DVector3(1e-6,1,1e-6), new DVector3(1e-6,-1,1e-6),
            new DVector3(0,0,0), new DVector3(1,100,0), new DVector3(1,-100,0)
        };
        double halfHeight = geometry.RowSpacing * (geometry.Topology.Rows - 1) / 2;
        foreach (var direction in directions)
        {
            DVector3 target = geometry.ProjectDirectionToSurface(direction);
            Assert.InRange(Math.Abs(Math.Sqrt(target.x*target.x+target.z*target.z)-geometry.Radius),0,1e-9);
            Assert.InRange(target.y,-halfHeight,halfHeight);
            DVector3 normal = new DVector3(-target.x/geometry.Radius,0,-target.z/geometry.Radius);
            Assert.InRange(Math.Abs(normal.magnitude-1),0,1e-12);
        }
    }

    [Theory]
    [InlineData(3,12)]
    [InlineData(64,1)]
    [InlineData(int.MaxValue,48)]
    public void InvalidDimensionsAreRejected(int columns,int rows)=>Assert.Throws<ArgumentOutOfRangeException>(()=>new RingTopology(columns,rows));

    [Fact]
    public void ParametersRejectNonFiniteRadiusAndUnknownSaveFormat()
    {
        Assert.Throws<ArgumentOutOfRangeException>(()=>new RingParameters("test",1,1,64,12,double.NaN));
        Assert.Throws<ArgumentException>(()=>new RingParameters(" ",1,1,64,12,200));
        Assert.Throws<NotSupportedException>(()=>new RingParameters("test",1,1,64,12,200,2));
    }

    [Fact]
    public void ArtificialDayUsesOneCycleForLightAndDateIncludingNegativeTicks()
    {
        Assert.Equal(0,RingClimate.DayPercent(60000));
        Assert.Equal(.5,RingClimate.DayPercent(30000));
        Assert.Equal(.75,RingClimate.DayPercent(-15000));
        Assert.InRange(RingClimate.SunGlow(30000),.99,1);
        Assert.InRange(RingClimate.SunGlow(0),0,.1);
    }

    [Fact]
    public void RingOcclusionFindsNearWallAndDoesNotOccludeSurfaceEndpoint()
    {
        var geometry=new RingGeometry(new RingTopology(64,12),200);
        Assert.True(geometry.IntersectsSegment(new DVector3(300,0,0),new DVector3(-200,0,0)));
        Assert.False(geometry.IntersectsSegment(new DVector3(100,0,0),new DVector3(200,0,0)));
        Assert.False(geometry.IntersectsSegment(new DVector3(300,1000,0),new DVector3(-200,1000,0)));
    }

    [Fact]
    public void SurfaceFrameUsesRingRadiusAndParentRotationWithoutCollapsingWidth()
    {
        var p=new RingParameters("frame",1,0,64,12,1000);
        var frame=RingSurfaceFrame.At(p,0,Math.PI/30000,15000);
        Assert.Equal(-1000,frame.PositionMeters.x,8);
        Assert.InRange(Math.Abs(frame.PositionMeters.z),0,1e-9);
        Assert.True(frame.PositionMeters.y<0);
        Assert.Equal(1,frame.Normal.x,8);
        Assert.Equal(-Math.PI/15,frame.VelocityMetersPerSecond.z,8);
        Assert.InRange(Math.Abs(DVector3.Dot(frame.Normal,frame.VelocityMetersPerSecond)),0,1e-9);
    }
}
