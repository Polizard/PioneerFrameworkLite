using System;

namespace PioneerFramework.RingWorld
{
    /// <summary>Follows a rotating floor while keeping the native camera direction in world coordinates.</summary>
    public sealed class RingCameraFollow
    {
        private readonly Megastructures.SurfaceCameraFollow follow=new();
        private static readonly Megastructures.PeriodicMotion motion=new(new DVector3(0,-1,0),60000);
        public void Reset()=>follow.Reset();
        public DVector3 Update(int layerId,bool inner,DVector3 worldDirection,double tick)
        {
            return follow.Update(layerId,inner,worldDirection,tick,motion);
        }
    }
}
