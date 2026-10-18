using System;
using PioneerFramework.Megastructures;
using Verse;

namespace PFL.MegastructureExtensionSample
{
    public sealed class CounterState {public long SimulatedTicks { get; set; }}
    public sealed class CounterCapability:IStructureCapability
    {
        public FrameworkResult Validate(string payload)
        {
            try{return MegastructureCodec.Read<CounterState>(payload).SimulatedTicks>=0?FrameworkResult.Ok():FrameworkResult.Reject("NegativeCounter");}
            catch(Exception){return FrameworkResult.Reject("InvalidCounterPayload");}
        }
        public string Advance(CapabilityContext context,string payload,long elapsedTicks)
        {
            var state=MegastructureCodec.Read<CounterState>(payload);state.SimulatedTicks=checked(state.SimulatedTicks+elapsedTicks);return MegastructureCodec.Encode(state);
        }
    }
    [StaticConstructorOnStartup]
    public static class SampleRegistration
    {
        static SampleRegistration()
        {
            var result=MegastructureRegistry.RegisterCapability(new CapabilityDescriptor("sample.counter",()=>new CounterCapability()));
            if(!result.Success)Log.Error("[PFL example] "+result.Code);
        }
    }
}
