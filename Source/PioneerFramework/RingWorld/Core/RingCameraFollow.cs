using System;

namespace PioneerFramework.RingWorld
{
    /// <summary>Follows a rotating floor while keeping the native camera direction in world coordinates.</summary>
    public sealed class RingCameraFollow
    {
        private int? layer;
        private double previousTick;
        public void Reset() { layer=null; }
        public DVector3 Update(int layerId,bool inner,DVector3 worldDirection,double tick)
        {
            if(!double.IsFinite(tick)) throw new ArgumentOutOfRangeException(nameof(tick));
            DVector3 result=inner && layer==layerId ? RingRotation.ToView(worldDirection,tick-previousTick) : worldDirection;
            layer=inner?layerId:null;
            previousTick=tick;
            return result;
        }
    }
}
