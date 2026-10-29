using System.Linq;
using LudeonTK;
using PioneerFramework;
using PioneerFramework.Celestial;
using Verse;

namespace PFL.CelestialCompSample
{
    public static class SampleCounterDebugActions
    {
        [DebugAction("PFL Celestial Sample", "Increment first counter", allowedGameStates = AllowedGameStates.Playing)]
        public static void IncrementFirst()
        {
            var services = CelestialWorldServices.TryGetCurrent();
            if (services == null) return;
            var runtime = services.Query;
            foreach (var body in runtime.CaptureCatalog().Bodies)
            {
                var counter = runtime.GetCapabilities<SampleCounterComp>(body.Id).FirstOrDefault();
                if (counter == null) continue;
                var result = services.Bodies.EnqueueEvent(runtime.WorldSessionId, body.Id, new(SampleCounterComp.IncrementEvent), counter.Descriptor.Key);
                Log.Message(result.Success ? "PF_CounterSample_Queued".Translate(body.Id.Value).ToString() :
                    "PF_CounterSample_Error".Translate(result.Errors[0].Code).ToString());
                return;
            }
            Log.Message("PF_CounterSample_NoInstances".Translate());
        }
        [DebugAction("PFL Celestial Sample", "Report counters", allowedGameStates = AllowedGameStates.Playing)]
        public static void ReportCounters()
        {
            var runtime = CelestialWorldServices.TryGetCurrent()?.Query;
            if (runtime == null) return;
            int count = 0;
            foreach (var body in runtime.CaptureCatalog().Bodies)
                foreach (var counter in runtime.GetCapabilities<SampleCounterComp>(body.Id))
                {
                    Log.Message("PF_CounterSample_Value".Translate(body.Id.Value, counter.Descriptor.Key, counter.Counter));
                    count++;
                }
            if (count == 0) Log.Message("PF_CounterSample_NoInstances".Translate());
        }
    }
}
