using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PioneerFramework.Megastructures;

const int instances=100,nodesPerInstance=100,samples=40;
var runtime=new MegastructureRuntime();var definition=new StructureDefinition {Id="benchmark"};
for(int node=0;node<nodesPerInstance;node++)definition.Nodes.Add(new NodeTemplate {Id="node-"+node,Spatial=new SpatialBinding {FrameId=1},Capabilities={new CapabilityState {Id="counter",HandlerId="bench.counter",Payload="0"}}});
Check(runtime.RegisterDefinition(definition));
for(int instance=0;instance<instances;instance++)
{
    string id="facility-"+instance;
    Check(runtime.Create(new CreateStructureRequest {OperationId="create-"+id,InstanceId=id,DefinitionId=definition.Id}));
    Check(runtime.Execute(new StructureCommand {OperationId="active-"+id,InstanceId=id,Kind=StructureCommandKind.Activate}));
}
var registry=new MegastructureCapabilityRegistry();registry.Register(new CapabilityDescriptor("bench.counter",()=>new BenchmarkCounter()));
var scheduler=new CapabilityScheduler(runtime,registry);long tick=0;
for(int warm=0;warm<8;warm++)scheduler.Tick(tick+=250,20000);
var timings=new List<double>();long before=GC.GetAllocatedBytesForCurrentThread();
for(int sample=0;sample<samples;sample++)
{
    long started=Stopwatch.GetTimestamp();scheduler.Tick(tick+=250,20000);timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    if(scheduler.LastStepCount!=instances*nodesPerInstance)throw new InvalidOperationException("Benchmark left due work unprocessed.");
}
long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
var verification=runtime.GetSnapshot("facility-0")!;
if(verification.Nodes[0].Capabilities[0].Payload!=tick.ToString(CultureInfo.InvariantCulture))throw new InvalidOperationException("Wrong settled duration.");
timings.Sort();
var report=new {Utc=DateTime.UtcNow,Environment.ProcessorCount,Runtime=Environment.Version.ToString(),Instances=instances,Nodes=instances*nodesPerInstance,Samples=samples,MeanBatchMilliseconds=timings.Average(),P95BatchMilliseconds=timings[(int)(samples*.95)-1],MeanAmortizedMillisecondsPerGameTick=timings.Average()/250,AllocatedBytesPerBatch=allocated/samples,Scope="Managed framework scheduler only; not a game tick performance claim"};
string json=JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true});Console.WriteLine(json);
if(args.Length>0)File.WriteAllText(Path.GetFullPath(args[0]),json);
static void Check(FrameworkResult result){if(!result.Success)throw new InvalidOperationException(result.Code);}
sealed class BenchmarkCounter:IStructureCapability
{
    public FrameworkResult Validate(string payload)=>FrameworkResult.Ok();
    public string Advance(CapabilityContext context,string payload,long elapsedTicks)=>(long.Parse(payload,CultureInfo.InvariantCulture)+elapsedTicks).ToString(CultureInfo.InvariantCulture);
}
