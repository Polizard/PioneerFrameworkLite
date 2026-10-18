using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Megastructures
{
    public sealed class ConnectionCapabilityData
    {
        public double MaximumDistanceMeters { get; set; }=1_000_000;
        public long Sequence { get; set; }
        public List<ResourceLeg> PendingLegs { get; set; }=new();
        public string Status { get; set; }="";
    }
    public static class StructureConnectionService
    {
        public static List<ResourceLeg> BuildLegs(string instanceId,IEnumerable<NodeConnection> connections,StructureResourceService resources,long tick,Func<string,string,bool> canConnect)
        {
            var result=new List<ResourceLeg>();var available=new Dictionary<string,decimal>(StringComparer.Ordinal);var room=new Dictionary<string,decimal>(StringComparer.Ordinal);
            foreach(var connection in connections.Where(c=>c.Enabled).OrderBy(c=>c.Id,StringComparer.Ordinal))
            {
                if(!canConnect(connection.SourceNode,connection.TargetNode))continue;
                string source=resources.FindStore(new(instanceId,connection.SourceNode),connection.ResourceId)??"";
                string target=resources.FindStore(new(instanceId,connection.TargetNode),connection.ResourceId)??"";
                if(source==""||target==""||source==target)continue;
                var from=resources.Query(source,connection.ResourceId);var to=resources.Query(target,connection.ResourceId);
                if(from.IsItem||to.IsItem||from.Unit!=to.Unit)continue;
                string sourceKey=source+"/"+connection.ResourceId;string targetKey=target+"/"+connection.ResourceId;
                if(!available.ContainsKey(sourceKey))available[sourceKey]=from.Available;if(!room.ContainsKey(targetKey))room[targetKey]=to.Capacity-to.Amount;
                decimal amount=Math.Min((decimal)connection.CapacityPerStep,Math.Min(available[sourceKey],room[targetKey]));if(amount<=0)continue;
                available[sourceKey]-=amount;room[targetKey]-=amount;
                result.Add(new ResourceLeg {ProviderId=source,ResourceId=connection.ResourceId,Amount=amount});
                result.Add(new ResourceLeg {ProviderId=target,ResourceId=connection.ResourceId,Amount=amount,Incoming=true,AvailableAtTick=tick+250});
            }
            return result;
        }
    }
    internal sealed class ConnectionsCapability:IStructureCapability
    {
        public FrameworkResult Validate(string payload)
        {var data=MegastructureCodec.Read<ConnectionCapabilityData>(payload);return double.IsFinite(data.MaximumDistanceMeters)&&data.MaximumDistanceMeters>0?FrameworkResult.Ok():FrameworkResult.Reject("InvalidConnectionRange");}
        public string Advance(CapabilityContext context,string payload,long elapsedTicks)
        {
            var resources=(StructureResourceService?)context.Services?.GetService(typeof(StructureResourceService));var spatial=(StructureSpatialService?)context.Services?.GetService(typeof(StructureSpatialService));
            var data=MegastructureCodec.Read<ConnectionCapabilityData>(payload);if(resources==null||spatial==null){data.Status="ConnectionServiceMissing";return MegastructureCodec.Encode(data);}
            resources.Tick=context.Tick;
            if(data.PendingLegs.Count==0)
            {
                bool WithinRange(string source,string target)
                {
                    var a=spatial.Evaluate(new(context.Address.InstanceId,source),spatial.CurrentTime);var b=spatial.Evaluate(new(context.Address.InstanceId,target),spatial.CurrentTime);
                    return a.Success&&b.Success&&a.State.FrameId==b.State.FrameId&&(a.State.PositionSystem-b.State.PositionSystem).magnitude<=data.MaximumDistanceMeters;
                }
                data.PendingLegs=StructureConnectionService.BuildLegs(context.Address.InstanceId,context.GetSnapshot()!.Connections,resources,context.Tick,WithinRange);
            }
            if(data.PendingLegs.Count>0)
            {
                var result=resources.Commit(context.Address.InstanceId+"/"+context.CapabilityId+"/network/"+data.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),data.PendingLegs);data.Status=result.Code;
                if(result.Success){data.Sequence++;data.PendingLegs.Clear();}
            }
            return MegastructureCodec.Encode(data);
        }
    }
}
