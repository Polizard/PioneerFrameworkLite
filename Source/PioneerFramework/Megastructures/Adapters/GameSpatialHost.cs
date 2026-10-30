using System;
using Verse;

namespace PioneerFramework.Megastructures
{
    public sealed class GameStructureSpatialHost:IStructureSpatialHost
    {
        private readonly GameComponentRef<CelestialBodyManager> bodies=new();
        private readonly GameComponentRef<SpaceGameComp> space=new();
        public bool TryGetBody(int id,double tick,out SpatialState state)
        {
            state=default;var mgr=bodies.Get();var body=mgr?.GetBodyById(id);if(body==null)return false;
            int frame=mgr!.GetReferenceFrameId(id);
            var position=ViewFrame.BodySystemPosition(id,tick);
            var velocity=id==frame?DVector3.zero:mgr.GetBodyVelocityAbs(id,tick)-mgr.GetBodyVelocityAbs(frame,tick);
            double omega=Launch.GetSurfaceState.RotationOmega(mgr,id);
            state=new(frame,position,velocity,RotationData.AxisAngle(new DVector3(0,1,0),-omega*tick),new DVector3(0,-omega,0),tick);return true;
        }
        public bool TryGetGroup(int id,double tick,out SpatialState state)
        {
            state=default;var group=space.Get()?.GetGroupById(id);if(group==null||!group.IsActive)return false;
            var q=group.CurrentOrbit.rotation;
            state=new(bodies.Get()?.GetReferenceFrameId(group.CelestialBodyId)??-1,group.CurrentOrbit.positionSystem.v,group.CurrentOrbit.velocitySystem.v,new(q.x,q.y,q.z,q.w),group.angularVelocity,TimeScaleManager.GetPhysicsTickSafe());return true;
        }
        public FrameworkResult Maneuver(int groupId,DVector3 deltaV)
        {
            var simulation=space.Get();var group=simulation?.GetGroupById(groupId);
            if(group==null||!group.IsActive||!group.IsMovable||group.IsSurfaceAnchor)return FrameworkResult.Reject("GroupUnavailable");
            if(group.HasActiveBurn||!string.IsNullOrEmpty(group.LandingTaskId))return FrameworkResult.Reject("GroupBusy");
            float budget=simulation!.GetGroupDeltaV(group).remainingDv;
            if(!float.IsFinite(budget)||budget<deltaV.magnitude)return FrameworkResult.Reject("InsufficientPropellant");
            group.BeginBurn(new ManeuverPlan { dv = deltaV, networks = ManeuverNetworkMode.All });return FrameworkResult.Ok();
        }
    }
}
