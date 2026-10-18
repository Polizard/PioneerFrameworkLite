using System;

namespace PioneerFramework.Megastructures
{
    public sealed class SurfaceCameraFollow
    {
        private int? layer;
        private double previousTick;
        public void Reset()=>layer=null;
        public DVector3 Update(int layerId,bool inner,DVector3 worldDirection,double tick,PeriodicMotion? motion)
        {
            if(!double.IsFinite(tick))throw new ArgumentOutOfRangeException(nameof(tick));
            DVector3 result=inner&&layer==layerId&&motion!=null?motion.RotateDelta(worldDirection,tick-previousTick):worldDirection;
            layer=inner?layerId:null;previousTick=tick;return result;
        }
    }
}
