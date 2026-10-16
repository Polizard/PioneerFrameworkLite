using PioneerFramework;
using PioneerFramework.RingWorld;
using Xunit;

public class RingWorldRotationTests
{
    [Theory]
    [InlineData(0,280,0)]
    [InlineData(15000,0,280)]
    [InlineData(30000,-280,0)]
    [InlineData(60000,280,0)]
    [InlineData(-15000,0,-280)]
    public void OneGameDayCompletesARevolutionAndInverseRecoversTile(double tick,double x,double z)
    {
        var local=new DVector3(280,17,0);
        var world=RingRotation.ToView(local,tick);
        Assert.Equal(x,world.x,8); Assert.Equal(17,world.y,8); Assert.Equal(z,world.z,8);
        Assert.InRange((RingRotation.ToLocal(world,tick)-local).magnitude,0,1e-9);
    }

    [Fact]
    public void LargeSavedTickRetainsTheSamePhaseAndInvalidTicksAreRejected()
    {
        var point=new DVector3(280,-3,0);
        Assert.InRange((RingRotation.ToView(point,60000000015000)-new DVector3(0,-3,280)).magnitude,0,1e-8);
        Assert.Throws<ArgumentOutOfRangeException>(()=>RingRotation.ToView(point,double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(()=>RingRotation.ToView(point,double.PositiveInfinity));
    }

    [Fact]
    public void PhysicalSurfaceFrameIncludesRelativeRingSpin()
    {
        var p=new RingParameters("rotating",1,1,256,3,1000);
        var frame=RingSurfaceFrame.At(p,0,0,15000);
        Assert.Equal(0,frame.PositionMeters.x,8); Assert.Equal(1000,frame.PositionMeters.z,8);
        Assert.Equal(-1,frame.Normal.z,8);
        Assert.Equal(-Math.PI/30,frame.VelocityMetersPerSecond.x,8);
        Assert.InRange(Math.Abs(frame.VelocityMetersPerSecond.z),0,1e-10);
    }

    [Fact]
    public void InnerCameraFollowsInWorldSpaceAndExplicitJumpIsNotRotatedAgain()
    {
        var follow=new RingCameraFollow();
        var direction=new DVector3(1,0,0);
        Assert.Equal(direction,follow.Update(7,true,direction,0));
        direction=follow.Update(7,true,direction,15000);
        Assert.InRange((direction-new DVector3(0,0,1)).magnitude,0,1e-10);
        follow.Reset();
        var earthTarget=new DVector3(0,0,-1);
        Assert.Equal(earthTarget,follow.Update(0,false,earthTarget,15000));
    }

    [Fact]
    public void WorldAxisCameraInputRemainsWorldSpaceAtQuarterTurn()
    {
        var follow=new RingCameraFollow();
        follow.Update(7,true,new DVector3(1,0,0),0);
        follow.Update(7,true,new DVector3(1,0,0),15000);
        var verticalDrag=new DVector3(0,.1,Math.Sqrt(.99));
        Assert.Equal(verticalDrag,follow.Update(7,true,verticalDrag,15000));
        Assert.Equal(verticalDrag,follow.Update(8,true,verticalDrag,15000));
    }
}
