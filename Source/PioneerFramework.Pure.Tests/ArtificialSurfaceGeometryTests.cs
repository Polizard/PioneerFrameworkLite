using System;
using System.Collections.Generic;
using PioneerFramework.Megastructures;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    public sealed class ArtificialSurfaceGeometryTests
    {
        internal sealed class Rectangle:IArtificialSurfaceGeometry
        {
            public int CellCount=>6;
            public double AverageCellSizeMeters=>10;
            public void GetNeighbors(int cell,List<int> result)
            {Check(cell);result.Clear();if(cell%3>0)result.Add(cell-1);if(cell%3<2)result.Add(cell+1);if(cell>=3)result.Add(cell-3);if(cell<3)result.Add(cell+3);}
            public IReadOnlyList<DVector3> GetPolygon(int cell)
            {var p=EvaluateLocal(cell).PositionMeters;return new[]{p+new DVector3(-5,0,-5),p+new DVector3(5,0,-5),p+new DVector3(5,0,5),p+new DVector3(-5,0,5)};}
            public SurfaceLocalFrame EvaluateLocal(int cell){Check(cell);return new(new(cell%3*10,0,cell/3*10),new(0,1,0),new(1,0,0));}
            public double Distance(int a,int b){Check(a);Check(b);return Math.Abs(a%3-b%3)+Math.Abs(a/3-b/3);}
            private void Check(int c){if(c<0||c>=CellCount)throw new ArgumentOutOfRangeException(nameof(c));}
        }
        [Fact]
        public void GridBufferHasVariablePolygonOffsets()
        {
            var data=SurfaceGridData.Build(new Rectangle());
            Assert.Equal(24,data.Vertices.Length);Assert.Equal(new[]{0,4,8,12,16,20},data.VertexOffsets);
            Assert.Equal(new[]{1,3},data.Neighbors.AsSpan(0,2).ToArray());
            Assert.Throws<ArgumentOutOfRangeException>(()=>new Rectangle().EvaluateLocal(6));
        }
        [Theory]
        [InlineData(-15000)] [InlineData(0)] [InlineData(15000)] [InlineData(60000)]
        public void MotionUsesAbsoluteTimeAndProducesVelocity(double tick)
        {
            var motion=new PeriodicMotion(new(0,-1,0),60000);
            var local=new SurfaceLocalFrame(new(100,0,0),new(-1,0,0),new(0,0,1));
            var pose=SurfaceSpatialMath.Evaluate(local,new SpatialState(7,DVector3.zero,DVector3.zero,RotationData.Identity,DVector3.zero,tick),motion,tick);
            var legacy=RingWorld.RingRotation.ToView(local.PositionMeters,tick);
            Assert.InRange((legacy-pose.Pose.PositionSystem.v).magnitude,0,1e-12);
            Assert.Equal(7,pose.Pose.FrameId);
            Assert.InRange((pose.Pose.VelocitySystem.v-DVector3.Cross(new DVector3(0,-Math.Tau/60000,0),legacy)).magnitude,0,1e-12);
        }
        [Fact]
        public void RectangularSurfaceReusesDeterministicPathsAndEnvironment()
        {
            var shape=new Rectangle();
            var path=SurfacePathSearch.Find(shape,0,5);
            Assert.Equal(new[]{0,1,2,5},path.Nodes);Assert.Equal(3,path.Cost);
            Assert.Equal(new[]{0,1,3},SurfaceQueryService.SelectByDistance(shape,0,0,1));
            Assert.False(SurfacePathSearch.Find(shape,0,5,passable:c=>c!=1&&c!=3).Found);
            Assert.Equal(1,ArtificialCycle.SunGlow(30000,60000));
            Assert.Equal(.75,ArtificialCycle.DayPercent(-15000,60000));
        }
        [Theory]
        [InlineData(true)] [InlineData(false)]
        public void ResultFactoryRunsOnlyForTheReturnedPath(bool reachable)
        {
            int calls=0;
            var result=SurfacePathSearch.Find(new Rectangle(),0,5,(nodes,cost)=>{calls++;return(nodes,cost);},passable:c=>reachable||c!=1&&c!=3);
            Assert.Equal(1,calls);Assert.Equal(reachable,result.nodes.Count>0);
            Assert.Equal(reachable?3:double.PositiveInfinity,result.cost);
        }
        [Theory]
        [InlineData(-15000)] [InlineData(0)] [InlineData(15000)] [InlineData(60000)]
        public void ParentAxialFrameMatchesLegacyRingOperationOrderExactly(double tick)
        {
            var parameters=new RingWorld.RingParameters("fixture",1,123,256,3,14000000);
            var geometry=new RingWorld.RingGeometry(new RingWorld.RingTopology(256,3),parameters.RadiusMeters);
            const double parentOmega=.00001;int cell=345;
            var old=RingWorld.RingSurfaceFrame.At(parameters,cell,parentOmega,tick);
            var current=SurfaceSpatialMath.EvaluateParentAxial(new(geometry.Center(cell),geometry.Normal(cell),geometry.Tangent(cell)),new SpatialState(7,DVector3.zero,DVector3.zero,RotationData.Identity,new(0,-parentOmega,0),tick),Math.Tau/60000,tick);
            Assert.Equal(old.PositionMeters,current.Pose.PositionSystem.v);Assert.Equal(old.VelocityMetersPerSecond,current.Pose.VelocitySystem.v);
            Assert.Equal(old.Normal,current.Normal);Assert.Equal(old.Tangent,current.Tangent);
            var captured=new SurfacePoseTransform(new SpatialState(7,DVector3.zero,DVector3.zero,RotationData.Identity,new(0,-parentOmega,0),tick),new PeriodicMotion(new(0,-1,0),60000),tick,true);
            var cached=captured.Evaluate(new(geometry.Center(cell),geometry.Normal(cell),geometry.Tangent(cell)));
            Assert.Equal(old.PositionMeters,cached.Pose.PositionSystem.v);Assert.Equal(old.VelocityMetersPerSecond,cached.Pose.VelocitySystem.v);
            Assert.Equal(old.Normal,cached.Normal);Assert.Equal(old.Tangent,cached.Tangent);
        }
    }
}
