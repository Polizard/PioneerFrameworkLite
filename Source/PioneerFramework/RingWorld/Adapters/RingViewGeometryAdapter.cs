using PioneerFramework.Megastructures;
using UnityEngine;

namespace PioneerFramework.RingWorld
{
    internal sealed class RingViewGeometryAdapter:ISurfaceViewGeometry
    {
        private readonly RingGeometry geometry;
        public RingViewGeometryAdapter(RingGeometry geometry)=>this.geometry=geometry;
        public float Radius=>(float)geometry.Radius;
        public float Bound=>Mathf.Sqrt(Radius*Radius+(float)geometry.HalfWidth*(float)geometry.HalfWidth);
        public float InnerAltitude=>300;
        public DVector3 Project(DVector3 localDirection)=>geometry.ProjectDirectionToSurface(localDirection);
        public DVector3 NormalAt(DVector3 localPosition)=>new(-localPosition.x/Radius,0,-localPosition.z/Radius);
        public Vector3 PresetDirection=>(Vector3)RingViewFraming.PresetViewDirection();
        public float PresetAltitude(float aspect)=>RingViewFraming.PresetAltitude(geometry,aspect);
    }
}
