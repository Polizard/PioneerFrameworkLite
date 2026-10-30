using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace PioneerFramework.Combat
{
    /// <summary>
    /// XML shot contract. Definitions are read-only configuration; every launch owns fresh ShotComp state.
    /// Exactly one propagation provider is required. Extensions implement CreateComp without name dispatch.
    /// </summary>
    public class ShotDef : Def
    {
        public List<ShotCompProperties> comps = new();

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (comps == null || comps.Count == 0) { yield return "comps must contain one propagation component"; yield break; }
            if (comps.Count(c => c != null && c.ProvidesPropagation) != 1)
                yield return "exactly one propagation component is required";
            foreach (var slot in comps.Where(c => c?.ExclusiveSlot != null).GroupBy(c => c.ExclusiveSlot))
                if (slot.Count() > 1) yield return $"duplicate exclusive shot component slot: {slot.Key}";
            foreach (var properties in comps)
            {
                if (properties == null) { yield return "null shot component"; continue; }
                foreach (string error in properties.ConfigErrors()) yield return error;
            }
        }

        public bool TryCreateRuntime(float weaponFactor, float mountFactor, out ShotRuntime? shot, out string error)
        {
            shot = null;
            error = string.Join("; ", ConfigErrors());
            if (error.Length != 0) return false;
            try
            {
                var created = new ShotRuntime(this);
                foreach (var properties in comps)
                {
                    var component = properties.CreateComp();
                    if (component == null) { error = "ShotComp factory returned null"; return false; }
                    created.Add(component);
                }
                created.Profile.Speed = created.Profile.Instant ? 0 :
                    ShotMath.MuzzleSpeed(created.Profile.Speed, weaponFactor, mountFactor);
                if (!created.Profile.Instant && !ShotMath.Positive(created.Profile.Speed))
                { error = "invalid muzzle velocity factors"; return false; }
                if (created.Profile.Instant ? !ShotMath.Positive(created.Profile.RangeMeters) :
                    !ShotMath.Positive(created.Profile.LifetimeSeconds) || !ShotMath.Positive(created.Profile.MassKg))
                { error = "invalid propagation profile"; return false; }
                shot = created;
                return true;
            }
            catch (Exception ex)
            {
                error = $"ShotComp factory failed: {ex.Message}";
                return false;
            }
        }
    }

    public abstract class ShotCompProperties
    {
        public virtual bool ProvidesPropagation => false;
        public virtual string? ExclusiveSlot => ProvidesPropagation ? "propagation" : null;
        public abstract ShotComp CreateComp();
        public virtual IEnumerable<string> ConfigErrors() { yield break; }
    }
}
