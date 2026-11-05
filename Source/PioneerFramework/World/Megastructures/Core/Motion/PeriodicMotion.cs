using System;

namespace PioneerFramework.Megastructures
{
    /// <summary>Absolute-time rotation. Physics ticks are supplied explicitly; no scheduler integration.</summary>
    public sealed class PeriodicMotion
    {
        public DVector3 Axis { get; }
        public double PeriodTicks { get; }
        public double Epoch { get; }
        public double ReferencePhase { get; }
        public double AngularRate=>Math.Tau/PeriodTicks;
        public PeriodicMotion(DVector3 axis,double periodTicks,double epoch=0,double referencePhase=0)
        {
            if(!axis.IsFinite()||axis.sqrMagnitude==0||!double.IsFinite(periodTicks)||periodTicks<=0||!double.IsFinite(epoch)||!double.IsFinite(referencePhase))throw new ArgumentException("Invalid periodic motion.");
            Axis=axis.normalized;PeriodTicks=periodTicks;Epoch=epoch;ReferencePhase=referencePhase;
        }
        public double Phase(double physicsTick)
        {
            if(!double.IsFinite(physicsTick))throw new ArgumentOutOfRangeException(nameof(physicsTick));
            double tick=(physicsTick-Epoch)%PeriodTicks;if(tick<0)tick+=PeriodTicks;
            return ReferencePhase+tick*AngularRate;
        }
        public RotationData RotationAt(double tick)=>RotationData.AxisAngle(Axis,Phase(tick));
        public PeriodicMotionSample SampleAt(double tick)=>new(Axis,Phase(tick),tick);
        public DVector3 RotateDelta(DVector3 value,double deltaTicks)
        {
            if(!double.IsFinite(deltaTicks))throw new ArgumentOutOfRangeException(nameof(deltaTicks));
            double ticks=deltaTicks%PeriodTicks;if(ticks<0)ticks+=PeriodTicks;
            double phase=ticks*AngularRate;
            return Axis==new DVector3(0,-1,0)?SurfaceFrame.FixedToEci(value,1,phase):RotationData.AxisAngle(Axis,phase).Rotate(value);
        }
        public DVector3 Rotate(DVector3 local,double tick)
        {
            if(Axis==new DVector3(0,-1,0))return SurfaceFrame.FixedToEci(local,1,Phase(tick));
            return RotationAt(tick).Rotate(local);
        }
        public DVector3 InverseRotate(DVector3 value,double tick)
        {
            if(Axis==new DVector3(0,-1,0))return SurfaceFrame.EciToFixed(value,1,Phase(tick));
            return RotationData.AxisAngle(Axis,-Phase(tick)).Rotate(value);
        }
    }
    public sealed class PeriodicMotionSample
    {
        private readonly bool axial;
        private readonly double cosine,sine;
        public double Tick {get;}
        public RotationData Rotation {get;}
        internal PeriodicMotionSample(DVector3 axis,double phase,double tick)
        {
            Tick=tick;axial=axis==new DVector3(0,-1,0);cosine=Math.Cos(phase);sine=Math.Sin(phase);
            Rotation=RotationData.AxisAngle(axis,phase);
        }
        public static PeriodicMotionSample Identity(double tick)=>new(new(0,-1,0),0,tick);
        public DVector3 Rotate(DVector3 point)=>axial?new(point.x*cosine-point.z*sine,point.y,point.x*sine+point.z*cosine):Rotation.Rotate(point);
        public DVector3 InverseRotate(DVector3 point)=>axial?new(point.x*cosine+point.z*sine,point.y,-point.x*sine+point.z*cosine):new RotationData(-Rotation.X,-Rotation.Y,-Rotation.Z,Rotation.W).Rotate(point);
    }
}
