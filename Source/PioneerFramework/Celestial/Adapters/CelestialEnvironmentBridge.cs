using System;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Celestial
{
    /// <summary>
    /// 行星环境采样的唯一宿主桥。读取入口**不得**向原版每 tick/每帧热路径抛异常：
    /// 任何采样失败都按 fail-open 返回 false，让原版逻辑继续（2026-11-07 P1-2）。
    /// 失败按「异常类型 + 消息」做一次 <see cref="Log.ErrorOnce"/>，不会每帧刷日志。
    /// </summary>
    public static class CelestialEnvironmentBridge
    {
        public static bool TrySample(PlanetLayer layer, CelestialSurfaceEnvironmentQuery query, out CelestialSurfaceEnvironmentSample sample)
        {
            sample = null!;
            if (layer is not CelestialSurfaceLayer surface) return false;
            try
            {
                if (CelestialBodyManager.GetCached()?.TryGetCelestialRuntime(out var runtime) != true) return false;
                var body = runtime.Query(surface.Header.OwnerId);
                if (!body.Success || body.Value.Lifecycle != CelestialLifecycle.Ready) return false;
                var capabilities = runtime.GetCapabilities<ICelestialSurfaceEnvironmentCapability>(body.Value.Id);
                if (capabilities.Count != 1) return false;
                sample = capabilities[0].SampleSurface(query);
                return true;
            }
            catch (Exception exception) { Report("EnvironmentSampleFailed", exception); sample = null!; return false; }
        }
        public static bool TryEvaluate(PlanetLayer layer, CelestialEnvironmentQuery query, out CelestialEnvironmentSample sample)
        {
            sample = null!;
            if (layer is not CelestialSurfaceLayer surface) return false;
            try
            {
                if (CelestialBodyManager.GetCached()?.TryGetCelestialRuntime(out var runtime) != true) return false;
                var body = runtime.Query(surface.Header.OwnerId);
                if (!body.Success || body.Value.Lifecycle != CelestialLifecycle.Ready) return false;
                var capabilities = runtime.GetCapabilities<ICelestialEnvironmentCapability>(body.Value.Id);
                if (capabilities.Count != 1) return false;
                sample = capabilities[0].Evaluate(query);
                return true;
            }
            catch (Exception exception) { Report("EnvironmentEvaluateFailed", exception); sample = null!; return false; }
        }
        private static void Report(string code, Exception exception) =>
            Log.ErrorOnce("[PFL Celestial] " + code + " (read falls back to vanilla): " + exception.GetType().Name + ": " + exception.Message,
                (code + ":" + exception.GetType().Name + ":" + exception.Message).GetHashCode());
    }
}
