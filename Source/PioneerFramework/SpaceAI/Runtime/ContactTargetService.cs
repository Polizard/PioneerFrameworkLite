using System;
using System.Collections.Generic;
using PioneerFramework.Combat;
using UnityEngine;
using Verse;

namespace PioneerFramework.SpaceAI
{
    /// <summary>Runtime-only identity. Never pass this type into AI decision code.</summary>
    public readonly record struct ContactTarget(int GroupId, int DecoyId);

    public sealed class AiTrackRegistry
    {
        private readonly Dictionary<(int faction, ContactTarget target), int> keys = new();
        private readonly Dictionary<int, (int faction, ContactTarget target)> targets = new();
        private readonly Dictionary<int,double> lastSeen = new();
        private readonly List<int> expired = new();
        private int next = 1;
        public int Remember(int faction, in Contact contact,double now = 0)
        {
            var identity = (faction, new ContactTarget(contact.targetGroupId, contact.decoyId));
            if (keys.TryGetValue(identity, out int key)) { lastSeen[key] = now; return key; }
            key = next++;
            keys.Add(identity, key);
            targets.Add(key, identity);
            lastSeen.Add(key,now);
            return key;
        }
        public bool TryGet(int faction, int key, out ContactTarget target)
        {
            target = default;
            if (!targets.TryGetValue(key, out var entry) || entry.faction != faction) return false;
            target = entry.target;
            return true;
        }
        public void Prune(double now,double retention)
        {
            expired.Clear();
            foreach (var entry in lastSeen) if (now-entry.Value > retention) expired.Add(entry.Key);
            foreach (int key in expired)
            { keys.Remove(targets[key]); targets.Remove(key); lastSeen.Remove(key); }
        }
        public void Clear() { keys.Clear(); targets.Clear(); lastSeen.Clear(); expired.Clear(); next = 1; }
    }

    /// <summary>Faction-correct contact resolution and common physical firing entry.</summary>
    public static class ContactTargetService
    {
        public static AiContact Project(in Contact c, int key, bool hostile, bool threatening) =>
            new(key, c.position, c.velocity, c.lastUpdatePhysics, c.positionSigma, c.apparentRadius,
                true, hostile, c.isRadiationSource, c.isDecoy, threatening);

        public static ContactTarget CopyTarget(AutonomousVehicleManager.VehicleRecord record) =>
            new(record.targetGroupId, record.targetDecoyId);

        public static bool TryResolve(SensorManager? sensor, SpaceGameComp? game, int faction,
            ContactTarget target, double now, out Contact contact)
        {
            contact = default;
            if (sensor == null || game == null) return false;
            if (target.DecoyId >= 0) return sensor.TryGetDecoyContactAt(faction, target.DecoyId, now, out contact);
            var group = game.GetGroupById(target.GroupId);
            return group != null && sensor.TryGetKnownState(faction, group, out contact);
        }

        public static double EffectiveRange(WeaponInstance weapon)
        {
            if (weapon?.def == null) return 0;
            double range = weapon.def.orbitalEngageRange;
            if (weapon.def.UsesShotComponents)
            {
                if (!weapon.TryCreateShot(out var shot, out _) || shot == null) return 0;
                double shotRange = shot.Profile.Instant ? shot.Profile.RangeMeters : shot.Profile.Speed * shot.Profile.LifetimeSeconds;
                return Math.Max(0, Math.Min(range, Math.Min(weapon.def.maxRange, shotRange)));
            }
            if (weapon.def.weaponType == WeaponType.Ray) range = Math.Min(range, weapon.def.rayMaxLength);
            if (weapon.def.weaponType == WeaponType.Projectile)
                range = Math.Min(range, weapon.def.projectileSpeed * weapon.def.projectileLifetime / 60.0);
            return Math.Max(0, range);
        }

        public static bool TryFire(SpaceGameComp game, SensorManager sensor, CombatManager combat,
            SpaceEntityGroup shooter, SpaceEntity entity, Thing building, WeaponInstance weapon,
            ContactTarget target, int count = 1)
        {
            if (!shooter.IsActive || !AiShipAdapter.Operational(building) ||
                entity.IsMapArchived || weapon == null || !weapon.CanFire()) return false;
            double now = TimeScaleManager.GetPhysicsTickSafe();
            if (!TryResolve(sensor, game, shooter.FactionId, target, now, out var contact) || contact.isDecoy) return false;
            DVector3 origin = combat.GetGroupPos(shooter);
            DVector3 aim = ContactMemory.CoastedPosition(contact, now);
            if ((aim - origin).magnitude > EffectiveRange(weapon)) return false;
            Vector3 muzzle = MapCoordMapper.MapCellToShip(building.Map, building.Position);
            Vector3 direction = MapCoordMapper.SystemToShip(entity, origin, aim) - muzzle;
            if (direction.sqrMagnitude < 1) return false;
            direction.Normalize();
            Vector3 half = (Vector3)entity.AabbHalfSize;
            Vector3 exit = MapCoordMapper.RayExitAabb(muzzle, direction, -half, half, out _);
            DVector3 spawn = MapCoordMapper.ShipToSystem(entity, origin, exit);
            int before = weapon.cooldownRemaining;
            combat.FireContactWeapon(shooter.GroupId, weapon, contact, spawn, count);
            return weapon.cooldownRemaining != before;
        }
    }
}
