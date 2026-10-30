using System;
using RimWorld.Planet;

namespace PioneerFramework.Celestial
{
    public static class CelestialEnvironmentBridge
    {
        public static bool TryEvaluate(PlanetLayer layer, CelestialEnvironmentQuery query, out CelestialEnvironmentSample sample)
        {
            sample = null!;
            if (layer is not CelestialSurfaceLayer surface || CelestialBodyManager.GetCached()?.TryGetCelestialRuntime(out var runtime) != true) return false;
            var body = runtime.Query(surface.Header.OwnerId);
            if (!body.Success || body.Value.Lifecycle != CelestialLifecycle.Ready) return false;
            var capabilities = runtime.GetCapabilities<ICelestialEnvironmentCapability>(body.Value.Id);
            if (capabilities.Count != 1) return false;
            sample = capabilities[0].Evaluate(query);
            return true;
        }
    }
}
