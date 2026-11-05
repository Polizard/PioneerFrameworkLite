using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed partial class CelestialCreationPipeline
    {
        internal long FaultRevision { get; private set; }
        internal void HandleComponentFault(CelestialBodyInstance instance, CelestialBodyComp failed, string code, Exception exception)
        {
            FaultRevision++;
            var error = new CelestialError(code, instance.Id, failed.Descriptor.Key, exception.Message);
            failed.RecordFailure(error);
            instance.ComponentFaults[failed.Descriptor.Key] = new(failed.Descriptor.Key, false, error);
            var components = GetComponents(instance.Id);
            var affected = new HashSet<CelestialBodyComp> { failed };
            bool changed;
            do
            {
                changed = false;
                foreach (var component in components.Where(component => !component.IsDisabled && !affected.Contains(component)))
                    if (component.Descriptor.Requires.Any(required => components.Any(provider => affected.Contains(provider) && provider.Descriptor.Provides.Contains(required)) &&
                        !components.Any(provider => !affected.Contains(provider) && !provider.IsDisabled && provider.Descriptor.Provides.Contains(required))))
                        changed |= affected.Add(component);
            } while (changed);
            instance.Errors = instance.Errors.Append(error).Distinct().ToArray();
            bool Critical(CelestialBodyComp component) => component.Descriptor.FailurePolicy == CelestialCompFailurePolicy.BlockBody ||
                component.Descriptor.Provides.Any(capability => capability is "geometry" or "physics.binding" or "surface");
            if (affected.Any(Critical))
            {
                instance.RestoreSuspension = null;
                instance.Lifecycle = CelestialLifecycle.Blocked;
                return;
            }
            foreach (var component in components.Reverse().Where(affected.Contains))
            {
                var failure = component == failed ? error : new CelestialError("ComponentDependencyUnavailable", instance.Id, component.Descriptor.Key, failed.Descriptor.Key);
                instance.ComponentFaults[component.Descriptor.Key] = new(component.Descriptor.Key, true, failure);
                try { component.Disable(failure); }
                catch (Exception release)
                {
                    instance.RestoreSuspension = null; instance.Lifecycle = CelestialLifecycle.Blocked;
                    instance.Errors = instance.Errors.Append(new CelestialError("ComponentReleaseFailed", instance.Id, component.Descriptor.Key, release.Message)).ToArray();
                }
                if (component != failed) instance.Errors = instance.Errors.Append(failure).ToArray();
            }
        }
    }
}
