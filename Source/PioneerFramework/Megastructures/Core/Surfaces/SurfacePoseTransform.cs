namespace PioneerFramework.Megastructures
{
    /// <summary>Immutable pose captured once for a surface/anchor/time, reusable across cells.</summary>
    public sealed class SurfacePoseTransform
    {
        private readonly SpatialState anchor;
        private readonly PeriodicMotionSample motion;
        private readonly bool parentAxial;
        private readonly double rate,tick;
        private readonly RotationData rotation;
        private readonly DVector3 angular;
        public SurfacePoseTransform(SpatialState anchor,PeriodicMotion? periodic,double tick,bool parentAxial=false)
        {
            this.anchor=anchor;this.tick=tick;this.parentAxial=parentAxial;
            if(parentAxial)
            {
                rate=-anchor.AngularVelocity.y+(periodic?.AngularRate??0);
                motion=new(new(0,-1,0),rate*tick,tick);rotation=motion.Rotation;angular=new(0,-rate,0);
            }
            else
            {
                motion=periodic?.SampleAt(tick)??PeriodicMotionSample.Identity(tick);
                rotation=anchor.Rotation*motion.Rotation;
                angular=anchor.AngularVelocity+anchor.Rotation.Rotate(periodic==null?DVector3.zero:periodic.Axis*periodic.AngularRate);
            }
        }
        public SurfaceCellState Evaluate(SurfaceLocalFrame local)
        {
            var offset=motion.Rotate(local.PositionMeters);var normal=motion.Rotate(local.Normal);var tangent=motion.Rotate(local.Tangent);
            if(!parentAxial){offset=anchor.Rotation.Rotate(offset);normal=anchor.Rotation.Rotate(normal);tangent=anchor.Rotation.Rotate(tangent);}
            var velocity=parentAxial?SurfaceFrame.SurfaceVelocity(offset,rate):DVector3.Cross(angular,offset);
            return new(new SpatialState(anchor.FrameId,anchor.PositionSystem.v+offset,anchor.VelocitySystem.v+velocity,rotation,angular,tick),normal,tangent);
        }
    }
}
