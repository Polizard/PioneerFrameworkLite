using System;
using System.Linq;
using PioneerFramework.RingWorld;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    public class RingWorldModuleCompatibilityTests
    {
        [Theory]
        [InlineData(256,3,768)]
        [InlineData(1024,12,12288)]
        public void NativeIdentityAndPhysicalClockRemainStable(int columns,int rows,int cells)
        {
            var p=new RingParameters("legacy/ring:原始",1,194914,columns,rows,14000000);
            Assert.Equal(cells,new RingTopology(columns,rows).TileCount);
            Assert.Equal(60000,RingRotation.PeriodTicks);
            foreach(double tick in new[]{-15000d,0,15000,30000,60000})
            {
                var g=new RingGeometry(new RingTopology(columns,rows),p.RadiusMeters);
                var frame=RingSurfaceFrame.At(p,cells/2,.00001,tick);
                Assert.Equal(SurfaceFrame.FixedToEci(g.Center(cells/2),.00001+Math.Tau/60000,tick),frame.PositionMeters);
                Assert.Equal(SurfaceFrame.SurfaceVelocity(frame.PositionMeters,.00001+Math.Tau/60000),frame.VelocityMetersPerSecond);
            }
            Assert.Equal("legacy/ring:原始",p.RingId);
        }

        [Fact]
        public void DisplayClockAndSeamHaveFrozenResults()
        {
            Assert.Equal(new[]{0d,.25,.5,.75,0},new[]{0d,15000,30000,45000,60000}.Select(RingClimate.DayPercent));
            Assert.Equal(0,RingClimate.SunGlow(0));
            Assert.Equal(1,RingClimate.SunGlow(30000));
            var path=RingPathSearch.Find(new RingTopology(256,3),256,511);
            Assert.Equal(new[]{256,511},path.Nodes);
            Assert.Equal(1,path.Cost);
        }
    }
}
