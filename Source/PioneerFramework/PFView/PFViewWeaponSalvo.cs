using System;
using System.Collections.Generic;
using PioneerFramework.Combat;
using PioneerFramework.SpaceAI;

namespace PioneerFramework
{
    /// <summary>Fire-control preview and dispatch order, independent of window and render resources.</summary>
    internal static class PFViewWeaponSalvo
    {
        public static string WeaponState(WeaponInstance weapon)
        {
            if (weapon.isDisabled) return "PFV_CockpitWeaponDisabled";
            if (weapon.isOverheated) return "PFV_Overheated";
            if (weapon.cooldownRemaining > 0) return "PFV_CockpitWeaponCooling";
            if (weapon.ammo == 0) return "PFV_CockpitWeaponEmpty";
            if (weapon.def == null || !weapon.TryGetSelectedShot(out _)) return "PFV_CockpitWeaponInvalidShot";
            return "PFV_Ready";
        }

        public static string? WeaponBlockReason(CombatManager? combat, SpaceEntityGroup source,
            WeaponInstance weapon, in Contact contact, double tick)
        {
            string state = WeaponState(weapon);
            if (state != "PFV_Ready") return state;
            if (combat == null || !combat.TryGetMapWeaponMount(source.GroupId, weapon, out _, out var building)
                || building == null || !AiShipAdapter.Operational(building))
                return "PFV_CockpitWeaponMount";
            double distance = (ContactMemory.CoastedPosition(contact, tick) - combat.GetGroupPos(source)).magnitude;
            return distance > ContactTargetService.EffectiveRange(weapon) ? "PFV_CockpitWeaponRange" : null;
        }

        public static string? BlockReason(SpaceEntityGroup source, CombatManager? combat,
            IReadOnlyList<WeaponInstance> weapons, in Contact contact, IReadOnlySet<WeaponInstance>? selection)
        {
            if (selection?.Count == 0) return "PFV_CockpitSelectWeapon";
            if (weapons.Count == 0) return "PFV_CockpitNoWeapons";
            double tick = TimeScaleManager.GetPhysicsTickSafe();
            foreach (var weapon in weapons)
            {
                if (selection != null && !selection.Contains(weapon)) continue;
                string? blocked = WeaponBlockReason(combat, source, weapon, contact, tick);
                if (blocked == null || selection?.Count == 1) return blocked;
            }
            return "PFV_CockpitNoReadyWeapons";
        }

        public static (int Fired, int Attempted) Fire(IReadOnlyList<WeaponInstance> weapons,
            IReadOnlySet<WeaponInstance>? selection, Func<WeaponInstance, bool> tryFire)
        {
            // The combat manager can reuse its live list while a shot is being submitted.
            // Pin only the requested mounts, in host order. Each still needs live validation.
            var requested = new List<WeaponInstance>(selection?.Count ?? weapons.Count);
            foreach (var weapon in weapons)
                if (selection == null || selection.Contains(weapon)) requested.Add(weapon);
            int fired = 0;
            foreach (var weapon in requested)
            {
                // The host reports cooldown changes. A zero-cooldown shot still increments the heat counter.
                int heatBefore = weapon.overheatCounter;
                if (tryFire(weapon) || weapon.overheatCounter != heatBefore) fired++;
            }
            return (fired, requested.Count);
        }
    }
}
