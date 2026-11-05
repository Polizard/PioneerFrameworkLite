using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Megastructures
{
    public sealed class ResourceSpecification
    {
        public string Id { get; set; }="";public string Unit { get; set; }="";public bool IsItem { get; set; }
        public decimal Capacity { get; set; }public decimal Initial { get; set; }
    }
    public sealed class StorageCapabilityData {public List<ResourceSpecification> Resources { get; set; }=new();}
    public sealed class RecipeAmount {public string ResourceId { get; set; }="";public decimal Amount { get; set; }}
    public sealed class ProductionCapabilityData
    {
        public string StoreCapabilityId { get; set; }="storage";
        public long PeriodTicks { get; set; }=60000;public long ProgressTicks { get; set; }public long Cycles { get; set; }
        public List<RecipeAmount> Inputs { get; set; }=new();public List<RecipeAmount> Outputs { get; set; }=new();
        public List<ResourceLeg> PendingLegs { get; set; }=new();public string Status { get; set; }="";
    }
    public sealed class ConstructionStage
    {
        public string Id { get; set; }="";public List<string> Requires { get; set; }=new();
        public decimal WorkRequired { get; set; }public decimal WorkDone { get; set; }public bool Completed { get; set; }
        public List<ResourceLeg> Costs { get; set; }=new();public List<StructureCommand> Actions { get; set; }=new();
        public bool MaterialsCommitted { get; set; }public string Status { get; set; }="";
    }
    public sealed class ConstructionCapabilityData
    {
        public decimal AvailableWork { get; set; }public decimal AutomaticWorkPerTick { get; set; }
        public bool Paused { get; set; }public bool Cancelled { get; set; }
        public List<ConstructionStage> Stages { get; set; }=new();
    }
    public sealed class StructureConstructionService
    {
        private readonly StructureResourceService resources;
        public StructureConstructionService(StructureResourceService resources){this.resources=resources;}
        public static FrameworkResult Validate(ConstructionCapabilityData data)
        {
            if(data.AvailableWork<0||data.AutomaticWorkPerTick<0||data.Stages.Any(s=>string.IsNullOrWhiteSpace(s.Id)||s.WorkRequired<0||s.WorkDone<0||s.Costs.Any(c=>c.Incoming||c.Amount<=0))||data.Stages.Select(s=>s.Id).Distinct().Count()!=data.Stages.Count)return FrameworkResult.Reject("InvalidProject");
            var complete=new HashSet<string>();var visiting=new HashSet<string>();
            bool Visit(ConstructionStage stage)
            {
                if(complete.Contains(stage.Id))return true;if(!visiting.Add(stage.Id))return false;
                foreach(string id in stage.Requires){var previous=data.Stages.FirstOrDefault(s=>s.Id==id);if(previous==null||!Visit(previous))return false;}
                visiting.Remove(stage.Id);complete.Add(stage.Id);return true;
            }
            return data.Stages.All(Visit)?FrameworkResult.Ok():FrameworkResult.Reject("ProjectCycle");
        }
        public bool Advance(string projectId,ConstructionCapabilityData data,Func<StructureCommand,FrameworkResult>? execute=null,Func<string,FrameworkResult?>? receipt=null,Func<string,ConstructionStage,FrameworkResult>? commitStage=null)
        {
            if(data.Paused||data.Cancelled)return false;
            foreach(var stage in data.Stages.Where(s=>!s.Completed).OrderBy(s=>s.Id,StringComparer.Ordinal))
            {
                if(stage.Requires.Any(id=>!data.Stages.First(s=>s.Id==id).Completed))continue;
                decimal work=Math.Min(data.AvailableWork,Math.Max(0,stage.WorkRequired-stage.WorkDone));stage.WorkDone+=work;data.AvailableWork-=work;
                if(stage.WorkDone<stage.WorkRequired){stage.Status="WorkRequired";continue;}
                if(commitStage!=null)
                {
                    var result=commitStage(projectId+"/"+stage.Id,stage);stage.Status=result.Code;
                    if(result.Success&&result.Code!="Queued"){stage.MaterialsCommitted=true;stage.Completed=true;stage.Status="Completed";}
                    continue;
                }
                if(stage.Actions.Count>0){stage.Status="AtomicStageExecutorRequired";continue;}
                if(!stage.MaterialsCommitted)
                {
                    var paid=stage.Costs.Count==0?FrameworkResult.Ok():resources.Commit(projectId+"/"+stage.Id,stage.Costs);
                    if(!paid.Success){stage.Status=paid.Code;continue;}stage.MaterialsCommitted=true;
                }
                bool actionsCompleted=true;
                for(int index=0;index<stage.Actions.Count;index++)
                {
                    var command=MegastructureCodec.Clone(stage.Actions[index]);command.OperationId=projectId+"/"+stage.Id+"/action/"+index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if(receipt?.Invoke(command.OperationId)?.Success==true)continue;
                    var result=execute?.Invoke(command)??FrameworkResult.Reject("ActionExecutorMissing");
                    if(!result.Success||result.Code=="Queued"){actionsCompleted=false;stage.Status=result.Code;}
                }
                if(actionsCompleted){stage.Completed=true;stage.Status="Completed";}
            }
            return data.Stages.All(s=>s.Completed);
        }
    }
    public static class StandardCapabilities
    {
        private static readonly string[] StorageDependency={"pfl.resource.storage"};
        public static void Register(MegastructureCapabilityRegistry registry)
        {
            registry.Register(new("pfl.resource.storage",()=>new StorageCapability()));
            registry.Register(new("pfl.production",()=>new ProductionCapability(),requires:StorageDependency));
            registry.Register(new("pfl.upkeep",()=>new ProductionCapability(),requires:StorageDependency));
            registry.Register(new("pfl.construction",()=>new ConstructionCapability()));
            registry.Register(new("pfl.resource.connections",()=>new ConnectionsCapability()){AllowNode=false});
            foreach(string id in new[]{"pfl.maneuver","pfl.map","pfl.transport","pfl.combat","pfl.presentation"})registry.Register(new(id,()=>new PassiveCapability(id)){IntervalTicks=int.MaxValue});
        }
        private sealed class PassiveCapability:IStructureCapability
        {
            private readonly string id;
            public PassiveCapability(string id){this.id=id;}
            public FrameworkResult Validate(string payload)
            {
                try
                {
                    using var json=System.Text.Json.JsonDocument.Parse(payload);if(json.RootElement.ValueKind!=System.Text.Json.JsonValueKind.Object)return FrameworkResult.Reject("InvalidPayload");
                    if(id=="pfl.map")
                    {
                        var data=MegastructureCodec.Read<MapCapabilityData>(payload);
                        if(data.MapIds==null||data.EntityIds==null||data.MapIds.Any(m=>m<0)||data.MapIds.Distinct().Count()!=data.MapIds.Count||data.EntityIds.Any(e=>e.Key<0||e.Key>=data.MapIds.Count||e.Value<0)||data.Width<50||data.Height<50||data.Width>400||data.Height>400)return FrameworkResult.Reject("InvalidMapConfiguration");
                    }
                    if(id=="pfl.transport")
                    {
                        var data=MegastructureCodec.Read<TransportCapabilityData>(payload);
                        if(data.Endpoints==null||data.Endpoints.Any(e=>e==null||string.IsNullOrWhiteSpace(e.Id)||e.Capacity<1||!e.Offset.IsFinite()||!e.Direction.IsFinite())||data.Endpoints.Select(e=>e.Id).Distinct().Count()!=data.Endpoints.Count)return FrameworkResult.Reject("InvalidEndpoint");
                    }
                    return FrameworkResult.Ok();
                }
                catch(Exception){return FrameworkResult.Reject("InvalidPayload");}
            }
            public string Advance(CapabilityContext context,string payload,long elapsedTicks)=>payload;
        }
        private sealed class StorageCapability:IStructureCapability
        {
            public FrameworkResult Validate(string payload)
            {
                var data=MegastructureCodec.Read<StorageCapabilityData>(payload);
                if(data.Resources.Select(r=>r.IsItem).Distinct().Count()>1)return FrameworkResult.Reject("SeparateItemAndScalarStoresRequired");
                return data.Resources.Select(r=>r.Id).Distinct().Count()!=data.Resources.Count||data.Resources.Any(r=>string.IsNullOrWhiteSpace(r.Id)||string.IsNullOrWhiteSpace(r.Unit)||r.Capacity<0||r.Initial<0||r.Initial>r.Capacity||(r.IsItem&&(decimal.Truncate(r.Capacity)!=r.Capacity||decimal.Truncate(r.Initial)!=r.Initial)))?FrameworkResult.Reject("InvalidStore"):FrameworkResult.Ok();
            }
            public string Advance(CapabilityContext context,string payload,long elapsedTicks)
            {
                var resources=(StructureResourceService?)context.Services?.GetService(typeof(StructureResourceService))??throw new InvalidOperationException("Resource service missing.");resources.Tick=context.Tick;
                string id=context.Address.InstanceId+"/"+context.Address.NodeId+"/"+context.CapabilityId;
                foreach(var spec in MegastructureCodec.Read<StorageCapabilityData>(payload).Resources)
                {
                    var factory=context.Services?.GetService(typeof(IResourceStoreFactory)) as IResourceStoreFactory;
                    var result=factory?.EnsureStore(id,context.Address,spec)??resources.AddStore(id,context.Address,spec.Id,spec.Unit,spec.Capacity,spec.Initial,spec.IsItem);
                    if(!result.Success)throw new InvalidOperationException(result.Code);
                }
                return payload;
            }
        }
        private sealed class ProductionCapability:IStructureCapability
        {
            public FrameworkResult Validate(string payload)
            {var data=MegastructureCodec.Read<ProductionCapabilityData>(payload);return data.PeriodTicks<=0||data.ProgressTicks<0||data.Cycles<0||data.Inputs.Concat(data.Outputs).Any(r=>string.IsNullOrWhiteSpace(r.ResourceId)||r.Amount<=0)?FrameworkResult.Reject("InvalidRecipe"):FrameworkResult.Ok();}
            public string Advance(CapabilityContext context,string payload,long elapsedTicks)
            {
                var resources=(StructureResourceService?)context.Services?.GetService(typeof(StructureResourceService))??throw new InvalidOperationException("Resource service missing.");resources.Tick=context.Tick;
                var data=MegastructureCodec.Read<ProductionCapabilityData>(payload);data.ProgressTicks=checked(data.ProgressTicks+elapsedTicks);
                if(data.ProgressTicks<data.PeriodTicks)return MegastructureCodec.Encode(data);
                string store=context.Address.InstanceId+"/"+context.Address.NodeId+"/"+data.StoreCapabilityId;
                int budget=256;
                while(data.ProgressTicks>=data.PeriodTicks&&budget-->0)
                {
                if(data.PendingLegs.Count==0)
                {
                    foreach(var input in data.Inputs)data.PendingLegs.Add(new ResourceLeg {ProviderId=store,ResourceId=input.ResourceId,Amount=input.Amount});
                    foreach(var output in data.Outputs)data.PendingLegs.Add(new ResourceLeg {ProviderId=store,ResourceId=output.ResourceId,Amount=output.Amount,Incoming=true,AvailableAtTick=context.Tick+250});
                }
                string operation=context.Address.InstanceId+"/"+context.Address.NodeId+"/"+context.CapabilityId+"/cycle/"+data.Cycles.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var result=data.PendingLegs.Count==0?FrameworkResult.Ok():resources.Commit(operation,data.PendingLegs);data.Status=result.Code;
                if(result.Success){data.Cycles++;data.ProgressTicks-=data.PeriodTicks;data.PendingLegs.Clear();}
                else {data.ProgressTicks=Math.Min(data.ProgressTicks,data.PeriodTicks);break;}
                }
                return MegastructureCodec.Encode(data);
            }
        }
        private sealed class ConstructionCapability:IStructureCapability
        {
            public FrameworkResult Validate(string payload)=>StructureConstructionService.Validate(MegastructureCodec.Read<ConstructionCapabilityData>(payload));
            public string Advance(CapabilityContext context,string payload,long elapsedTicks)
            {
                var resources=(StructureResourceService?)context.Services?.GetService(typeof(StructureResourceService))??throw new InvalidOperationException("Resource service missing.");resources.Tick=context.Tick;
                var data=MegastructureCodec.Read<ConstructionCapabilityData>(payload);
                if(!data.Paused&&!data.Cancelled)data.AvailableWork+=data.AutomaticWorkPerTick*elapsedTicks;
                var construction=new StructureConstructionService(resources);
                construction.Advance(context.Address.InstanceId+"/"+context.Address.NodeId+"/"+context.CapabilityId,data,commitStage:(id,stage)=>context.CommitStage(id,stage,resources));
                return MegastructureCodec.Encode(data);
            }
        }
    }
}
