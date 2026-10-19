using System;
using System.Collections.Generic;
using PioneerFramework.Combat;
using RimWorld;
using Verse;

namespace PioneerFramework.SpaceAI
{
    /// <summary>Own-ship equipment adapter. All reads and writes occur on the game thread.</summary>
    internal sealed class AiShipAdapter
    {
        internal readonly List<(SpaceEntity entity, Thing building, WeaponInstance weapon)> weapons = new();
        internal readonly List<Thing> devices = new();
        internal bool Ready;
        internal AiShipState State;

        internal static bool Operational(Thing thing)
        {
            if (thing == null || thing.Destroyed || !thing.Spawned || thing.HitPoints <= 0) return false;
            if (thing.TryGetComp<CompPowerTrader>() is { PowerOn: false }) return false;
            return thing.TryGetComp<CompFlickable>() is not { SwitchIsOn: false };
        }

        internal void Refresh(SpaceGameComp game, CombatManager combat, SpaceEntityGroup group, AiShipRecord record)
        {
            weapons.Clear(); devices.Clear(); Ready = false;
            double health = 0, maximum = 0, range = 0;
            bool radar = false, jammer = false;
            double ammunition = 0; int weaponCount = 0;
            foreach (int id in group.GetEntityIds())
            {
                var entity = game.GetEntityById(id);
                if (entity == null || entity.IsMapArchived || entity.MapId < 0) continue;
                var map = SpaceMapGenerator.FindPocketMap(entity.MapId);
                if (map == null) continue;
                Ready = true;
                foreach (var thing in map.listerThings.AllThings)
                {
                    if (!(thing is Building)) continue;
                    health += thing.HitPoints; maximum += thing.MaxHitPoints;
                    if (!Operational(thing)) continue;
                    var missile = thing.TryGetComp<CompMissileLauncher>();
                    var turret = thing.TryGetComp<CompMapTurret>();
                    var weapon = missile?.Weapon ?? turret?.Mount?.weapon;
                    if (weapon?.def != null)
                    {
                        weapons.Add((entity, thing, weapon));
                        if (!weapon.isDisabled && weapon.def.weaponType != WeaponType.Drone)
                        {
                            if (weapon.ammo != 0) range = Math.Max(range, ContactTargetService.EffectiveRange(weapon));
                            ammunition += weapon.ammo == 0 ? 0 : 1; weaponCount++;
                        }
                    }
                    if (thing.TryGetComp<CompActiveRadar>() != null || thing.TryGetComp<CompJammer>() != null || thing.TryGetComp<CompDecoyEmitter>() != null)
                        devices.Add(thing);
                    radar |= thing.TryGetComp<CompActiveRadar>() != null;
                    jammer |= thing.TryGetComp<CompJammer>() != null;
                }
            }
            var dv = game.GetGroupDeltaV(group);
            State = new(group.GroupId, combat.GetGroupPos(group), combat.GetGroupVelocity(group), range,
                game.EstimateMaxAcceleration(group), dv.remainingDv, record.reserveDeltaV,
                maximum > 0 ? health / maximum : 0, weaponCount > 0 ? ammunition / weaponCount : 1,
                record.role, weaponCount > 0, radar && group.RadarCap.beamRangeMeters > 0, jammer && group.JamStrength > 0);
        }

        internal int Fire(SpaceGameComp game, SensorManager sensor, CombatManager combat,
            SpaceEntityGroup group, ContactTarget target, int salvo,bool missileWindowOpen)
        {
            int fired = 0;
            for (int i = 0; i < weapons.Count; i++)
            {
                var entry = weapons[i];
                if (!Operational(entry.building)) continue;
                if (entry.weapon.def.weaponType == WeaponType.Drone) continue;
                if (entry.weapon.def.weaponType == WeaponType.Missile && !missileWindowOpen) continue;
                int count = entry.weapon.ammo >= 0 ? Math.Min(salvo, entry.weapon.ammo) : salvo;
                if (count < 1) continue;
                if (ContactTargetService.TryFire(game,sensor,combat,group,entry.entity,entry.building,entry.weapon,target,count))
                    fired += entry.weapon.def.weaponType == WeaponType.Missile ? count : 1;
            }
            return fired;
        }

        internal void EmitDecoy()
        {
            for (int i = 0; i < devices.Count; i++)
                if (Operational(devices[i]) && devices[i].TryGetComp<CompDecoyEmitter>()?.TryEmit() == true) return;
        }
    }
}
