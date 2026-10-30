using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using Verse;

namespace PioneerFramework.Megastructures
{
    internal static class MegastructureDeferredGuard
    {
        internal static Func<FrameworkResult> Capture(MegastructureManager manager, StructureState state)
        {
            var game = Current.Game;
            string instance = state.Id;
            long revision = state.Revision;
            var bodies = game?.GetComponent<CelestialBodyManager>();
            var targets = new List<CelestialTargetToken>();
            FrameworkResult? captureError = null;
            if (bodies?.TryGetCelestialRuntime(out _) == true)
            {
                var handles = state.Nodes.Select(node => node.Spatial).Where(binding => binding.Mode is SpatialMode.BodyAnchored or SpatialMode.FrameFixed)
                    .Select(binding => binding.Mode == SpatialMode.BodyAnchored ? binding.BodyId : binding.FrameId).Where(handle => handle >= 0).Distinct();
                foreach (int handle in handles)
                {
                    var target = bodies.CaptureTarget(handle);
                    if (!target.Success) { captureError = FrameworkResult.Reject(target.Errors[0].Code); break; }
                    targets.Add(target.Value);
                }
            }
            return () => {
                if (game == null || !ReferenceEquals(game, Current.Game) || !ReferenceEquals(game.GetComponent<MegastructureManager>(), manager))
                    return FrameworkResult.Reject("StaleSession");
                if (captureError != null) return captureError;
                var current = manager.GetSnapshot(instance);
                if (current == null || current.Revision != revision || current.Lifecycle == StructureLifecycle.Retired)
                    return FrameworkResult.Reject("RevisionConflict");
                foreach (var target in targets)
                {
                    var valid = bodies!.ValidateTarget(target);
                    if (!valid.Success) return FrameworkResult.Reject(valid.Errors[0].Code);
                }
                return FrameworkResult.Ok(instance, revision);
            };
        }
    }
}
