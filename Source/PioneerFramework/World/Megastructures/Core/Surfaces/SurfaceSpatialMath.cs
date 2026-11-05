namespace PioneerFramework.Megastructures
{
    public static class SurfaceSpatialMath
    {
        public static SurfaceCellState Evaluate(SurfaceLocalFrame local,SpatialState anchor,PeriodicMotion? motion,double physicsTick)
        {
            var offset=anchor.Rotation.Rotate(motion?.Rotate(local.PositionMeters,physicsTick)??local.PositionMeters);
            var angular=anchor.AngularVelocity+anchor.Rotation.Rotate(motion==null?DVector3.zero:motion.Axis*motion.AngularRate);
            var pose=new SpatialState(anchor.FrameId,anchor.PositionSystem.v+offset,anchor.VelocitySystem.v+DVector3.Cross(angular,offset),anchor.Rotation*(motion?.RotationAt(physicsTick)??RotationData.Identity),angular,physicsTick);
            return new(pose,anchor.Rotation.Rotate(motion?.Rotate(local.Normal,physicsTick)??local.Normal),anchor.Rotation.Rotate(motion?.Rotate(local.Tangent,physicsTick)??local.Tangent));
        }
        public static SurfaceCellState EvaluateParentAxial(SurfaceLocalFrame local,SpatialState parent,double localAngularRate,double physicsTick)
        {
            double rate=-parent.AngularVelocity.y+localAngularRate;
            var frame=RotateAxial(local,rate,physicsTick);
            return new(new SpatialState(parent.FrameId,parent.PositionSystem.v+frame.PositionMeters,parent.VelocitySystem.v+SurfaceFrame.SurfaceVelocity(frame.PositionMeters,rate),RotationData.AxisAngle(new(0,-1,0),rate*physicsTick),new(0,-rate,0),physicsTick),frame.Normal,frame.Tangent);
        }
        // Preserve the established surface-frame floating point operation order for axial surfaces.
        public static SurfaceLocalFrame RotateAxial(SurfaceLocalFrame local,double angularRate,double physicsTick)=>new(
            SurfaceFrame.FixedToEci(local.PositionMeters,angularRate,physicsTick),
            SurfaceFrame.FixedToEci(local.Normal,angularRate,physicsTick),
            SurfaceFrame.FixedToEci(local.Tangent,angularRate,physicsTick));
    }
}
