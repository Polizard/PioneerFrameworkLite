using PioneerFramework.Megastructures;
using PFL.MegastructureExtensionSample;
using Xunit;

namespace PioneerFramework.Tests
{
    public sealed class MegastructureExtensionTests
    {
        [Fact]
        public void SeparateAssemblyCapabilityRunsAndRestoresUsingPublicContractsOnly()
        {
            var registry=new MegastructureCapabilityRegistry();
            Assert.True(registry.Register(new CapabilityDescriptor("sample.counter",()=>new CounterCapability())).Success);
            var runtime=new MegastructureRuntime();
            runtime.RegisterDefinition(new StructureDefinition {Id="external",Nodes={new NodeTemplate {Id="hub",Capabilities={new CapabilityState {Id="counter",HandlerId="sample.counter"}}}}});
            runtime.Create(new CreateStructureRequest {OperationId="create",InstanceId="external-1",DefinitionId="external"});
            runtime.Execute(new StructureCommand {OperationId="active",InstanceId="external-1",Kind=StructureCommandKind.Activate});
            new CapabilityScheduler(runtime,registry).Tick(1000);
            var saved=MegastructureCodec.Decode(MegastructureCodec.Encode(runtime.Export()));
            var restored=new MegastructureRuntime();Assert.True(restored.Restore(saved).Success);new CapabilityScheduler(restored,registry).Tick(1250);
            var state=MegastructureCodec.Read<CounterState>(restored.GetSnapshot("external-1").Nodes[0].Capabilities[0].Payload);
            Assert.Equal(1250,state.SimulatedTicks);
            Assert.NotEqual(typeof(MegastructureRuntime).Assembly,typeof(CounterCapability).Assembly);
        }
    }
}
