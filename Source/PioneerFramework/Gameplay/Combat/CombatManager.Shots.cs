using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.Combat
{
    public partial class CombatManager
    {
        internal static IntVec3 MapImpactCell(CellRect occupied, Vector3 point) => new(
            Mathf.Clamp(Mathf.FloorToInt(point.x), occupied.minX, occupied.maxX), 0,
            Mathf.Clamp(Mathf.FloorToInt(point.z), occupied.minZ, occupied.maxZ));
        private sealed class ShotHit
        {
            public double T;
            public SpaceEntityGroup Group = null!;
            public SpaceEntity Entity = null!;
            public SpacePart? Part;
            public Thing? Thing;
            public Map? Map;
            public DVector3 PointSystem;
            public DVector3 PointShip;
            public Vector3 PointMap;
        }

        private void CheckConfiguredProjectile(ProjectileInstance projectile, SpaceGameComp spaceGame)
        {
            var grid = GetCombatGrid();
            if (grid == null || projectile.shot == null) return;
            var startCell = CombatGrid.WorldToFineCell(projectile.prevPosition.v);
            grid.GetShipsNearProjectile(projectile.systemBodyId, startCell, projectile.prevPosition.v, projectile.position.v,
                out var nextCell, projectileQueryResults);
            ResolveConfiguredSegment(projectile, projectileQueryResults, true);
            if (projectile.shot.Stopped)
            {
                grid.RemoveProjectile(projectile);
                spaceGame.UnregisterProjectileFromGrid(projectile);
                activeProjectiles.Remove(projectile);
            }
            projectile.fineCx = nextCell.cx;
            projectile.fineCy = nextCell.cy;
            projectile.fineCz = nextCell.cz;
        }

        private void FireConfiguredRay(SpaceEntityGroup group, WeaponInstance weapon, ShotRuntime shot,
            DVector3 start, DVector3 direction)
        {
            int frame = GetSpaceGameComp()!.ResolveReferenceFrameId(group.CelestialBodyId);
            double length = Math.Min(shot.Profile.RangeMeters, weapon.def.maxRange);
            var end = start + direction * length;
            var projectile = new ProjectileInstance
            {
                shot = shot, weaponDef = weapon.def, sourceGroupId = group.GroupId, factionId = group.FactionId,
                prevPosition = (SystemVec3)start, position = (SystemVec3)end, systemBodyId = frame,
                spawnPosition = (SystemVec3)start
            };
            var candidates = new HashSet<SpaceEntityGroup>();
            GetCombatGrid()?.GetShipsNearSegment(frame, start, end, candidates);
            DVector3 visualEnd = ResolveConfiguredSegment(projectile, candidates, false) ?? end;
            int ticks = Math.Max(1, (int)Math.Ceiling(shot.Profile.VisualSeconds * 60));
            activeLaserBeams.Add(new LaserBeamInstance
            {
                startPos = start, endPos = visualEnd, systemBodyId = frame, totalTicks = ticks, remainingTicks = ticks,
                color = shot.Profile.Color, widthMeters = shot.Profile.WidthMeters,
                minimumViewWidth = shot.Profile.MinimumViewWidth
            });
        }

        /// <summary>
        /// Sort all physical contacts before dispatch. Group/part enumeration order cannot select a farther hit.
        /// Map contacts use the actual part transform and only the current swept interval inside that part.
        /// No visual Thing tick participates in damage. Third-party components receive the same event context.
        /// </summary>
        private DVector3? ResolveConfiguredSegment(ProjectileInstance projectile, IEnumerable<SpaceEntityGroup> candidates, bool moving)
        {
            var shot = projectile.shot!;
            var space = GetSpaceGameComp();
            if (space == null) return null;
            var hits = new List<ShotHit>();
            double dt = moving ? TimeScaleManager.GetPhysicsTickSafe() - projectile.prevUpdatePhysicsTick : 0;
            foreach (var group in candidates)
            {
                if (!group.IsActive || !group.IsCollidable || group.GroupId == projectile.sourceGroupId) continue;
                if (moving && group.physicsBirthTick >= TimeScaleManager.GetPhysicsTickSafe()) continue;
                var groupPosition = GetGroupPos(group);
                var previous = !moving ? groupPosition : group.IsCircularOrbit
                    ? group.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe() - dt)
                    : group.CurrentOrbit.positionSystem.v - group.CurrentOrbit.velocitySystem.v * dt;
                foreach (int id in group.GetEntityIds())
                {
                    var entity = space.GetEntityById(id);
                    if (entity == null || !entity.IsCollidable) continue;
                    var inverse = entity.RotationInverse();
                    var a = DVector3.Rotate(projectile.prevPosition.v - previous - entity.Position, inverse);
                    var b = DVector3.Rotate(projectile.position.v - groupPosition - entity.Position, inverse);
                    if ((b - a).magnitude < 0.5) continue;
                    if (!CombatGrid.RaySlabIntersection(a, b, -entity.AabbHalfSize, entity.AabbHalfSize, out _, out _, out _)) continue;
                    foreach (var part in entity.Parts)
                    {
                        if (part == null) continue;
                        var partInverse = DQuaternion.Inverse(part.rotationShip);
                        var pa = DVector3.Rotate(a - part.positionShip, partInverse);
                        var pb = DVector3.Rotate(b - part.positionShip, partInverse);
                        if (!CombatGrid.RaySlabIntersection(pa, pb, -part.boundsSizeShip * 0.5, part.boundsSizeShip * 0.5,
                            out double entry, out double exit, out _)) continue;
                        entry = Math.Max(0, entry);
                        exit = Math.Min(1, exit);
                        var map = part.linkedMapId >= 0 ? SpaceMapGenerator.FindPocketMap(part.linkedMapId) : null;
                        if (map != null)
                        {
                            var ma = MapCoordMapper.ShipToMap(map, (Vector3)(a + (b - a) * entry));
                            var mb = MapCoordMapper.ShipToMap(map, (Vector3)(a + (b - a) * exit));
                            ma.y = mb.y = 0;
                            foreach (var thing in map.spawnedThings)
                            {
                                if (thing.Destroyed || shot.HitThings.Contains(thing) ||
                                    !(thing is Pawn) && thing.def.category != ThingCategory.Building) continue;
                                var rect = thing.OccupiedRect();
                                double localT;
                                if ((mb - ma).sqrMagnitude < 1e-10f)
                                {
                                    if (!rect.Contains(ma.ToIntVec3())) continue;
                                    localT = 0;
                                }
                                else if (!CombatGrid.RaySlabIntersection((DVector3)ma, (DVector3)mb,
                                    new DVector3(rect.minX, -1, rect.minZ), new DVector3(rect.maxX + 1, 1, rect.maxZ + 1),
                                    out localT, out _, out _)) continue;
                                double t = entry + (exit - entry) * Math.Max(0, localT);
                                hits.Add(new ShotHit { T = t, Group = group, Entity = entity, Part = part, Thing = thing, Map = map,
                                    PointMap = Vector3.Lerp(ma, mb, (float)Math.Max(0, localT)),
                                    PointSystem = projectile.prevPosition.v + (projectile.position.v - projectile.prevPosition.v) * t,
                                    PointShip = a + (b - a) * t });
                            }
                        }
                        // Map carriers expose their internal structures; ordinary parts retain component damage callbacks.
                        if (!part.isIndestructible || map == null)
                            hits.Add(new ShotHit { T = entry, Group = group, Entity = entity, Part = part,
                                PointSystem = projectile.prevPosition.v + (projectile.position.v - projectile.prevPosition.v) * entry,
                                PointShip = a + (b - a) * entry });
                    }
                    if (entity.Parts.Count == 0)
                        if (CombatGrid.RaySlabIntersection(a, b, -entity.AabbHalfSize, entity.AabbHalfSize, out double t, out _, out _))
                            hits.Add(new ShotHit { T = Math.Max(0, t), Group = group, Entity = entity,
                                PointSystem = projectile.prevPosition.v + (projectile.position.v - projectile.prevPosition.v) * Math.Max(0, t),
                                PointShip = a + (b - a) * Math.Max(0, t) });
                }
            }
            hits.Sort((a, b) =>
            {
                int order = a.T.CompareTo(b.T);
                if (order != 0) return order;
                order = a.Entity.EntityId.CompareTo(b.Entity.EntityId);
                if (order != 0) return order;
                return a.Thing != null && b.Thing != null ? a.Thing.thingIDNumber.CompareTo(b.Thing.thingIDNumber) :
                    (a.Part?.partId ?? -1).CompareTo(b.Part?.partId ?? -1);
            });
            foreach (var hit in hits)
            {
                if (space.GetEntityById(hit.Entity.EntityId) != hit.Entity) continue;
                if (hit.Thing != null)
                {
                    if (hit.Thing.Destroyed || !hit.Thing.Spawned || !shot.HitThings.Add(hit.Thing)) continue;
                }
                else if (hit.Part != null && !shot.HitParts.Add(hit.Part)) continue;
                // Preserve ballistic interception: remove only the struck missile before detonating its warhead.
                var avm = moving ? GetAvm() : null;
                var missile = avm?.GetRecord(hit.Group.GroupId);
                bool intercepted = missile != null;
                if (intercepted)
                {
                    bool empty = avm!.RemoveMissileEntity(missile!, hit.Entity.EntityId, "拦截");
                    ExplodeMissileEntityAt(hit.Group, missile!, GetGroupPos(hit.Group) + hit.Entity.Position, space,
                        hit.Group.GroupId, hit.Entity.EntityId);
                    if (empty)
                    {
                        avm.UnregisterGroup(hit.Group.GroupId);
                        space.DestroyGroup(hit.Group.GroupId, "战斗中损毁（射弹/接触）");
                    }
                }
                var context = new ShotHitContext
                {
                    Shot = shot,
                    DistanceMeters = (hit.PointSystem - projectile.spawnPosition.v).magnitude,
                    ApplyDirect = (damageDef, partDamage, mapDamage, ap) =>
                    {
                        if (intercepted) return;
                        if (hit.Thing != null)
                        {
                            if (mapDamage > 0 && damageDef != null && !hit.Thing.Destroyed)
                                hit.Thing.TakeDamage(new DamageInfo(damageDef, mapDamage, ap, instigator: shot.Instigator, weapon: shot.WeaponThingDef));
                        }
                        else if (hit.Part != null)
                        {
                            hit.Part.ApplyDamage(partDamage);
                            if (hit.Part.isDestroyed) hit.Entity.MarkPartDestroyed(hit.Part.partId);
                        }
                    },
                    ApplyExplosion = (properties, scale) => ApplyShotExplosion(projectile, hit, properties, scale)
                };
                shot.Contact(context);
                RecordProjectileContact(projectile, hit.Group, hit.Entity, hit.PointSystem, hit.PointShip);
                lastHitPosition = hit.PointSystem;
                lastHitGroupId = hit.Group.GroupId;
                lastHitTick = Find.TickManager.TicksGame;
                lastHitWeaponType = shot.Profile.Instant ? WeaponType.Ray : WeaponType.Projectile;
                if (hit.Map != null && shot.Profile.MapVisualProjectile?.thingClass != null &&
                    typeof(PFProjectile3D).IsAssignableFrom(shot.Profile.MapVisualProjectile.thingClass))
                {
                    var visual = (PFProjectile3D)ThingMaker.MakeThing(shot.Profile.MapVisualProjectile);
                    var cell = hit.Thing != null ? MapImpactCell(hit.Thing.OccupiedRect(), hit.PointMap) : hit.PointMap.ToIntVec3();
                    if (cell.InBounds(hit.Map))
                    {
                        GenSpawn.Spawn(visual, cell, hit.Map);
                        visual.Launch3D(hit.PointMap, hit.PointMap, cell, visualOnly: true);
                    }
                }
                CleanupShotMissile(hit.Entity);
                if (intercepted || !shot.TryPenetrate(hit.Thing, hit.Part))
                {
                    shot.Stop(context);
                    return hit.PointSystem;
                }
            }
            return null;
        }

        private void CleanupShotMissile(SpaceEntity entity)
        {
            var space = GetSpaceGameComp();
            if (space?.GetEntityById(entity.EntityId) != entity || entity.Parts.Count == 0) return;
            var avm = GetAvm();
            var record = avm?.GetRecord(entity.GroupId);
            if (record == null) return;
            foreach (var part in entity.Parts)
                if (part != null && !part.isDestroyed) return;
            int groupId = entity.GroupId;
            if (avm!.RemoveMissileEntity(record, entity.EntityId, "发射物伤害击毁"))
            {
                avm.UnregisterGroup(groupId);
                space!.DestroyGroup(groupId, "战斗中损毁（发射物伤害）");
            }
        }

        private void ApplyShotExplosion(ProjectileInstance projectile, ShotHit hit, ShotCompProperties_Explosion properties, float scale)
        {
            if (hit.Map != null && properties.mapRadiusCells > 0 && properties.mapCenterDamage > 0)
            {
                var map = hit.Map;
                var center = hit.Thing != null ? MapImpactCell(hit.Thing.OccupiedRect(), hit.PointMap) : hit.PointMap.ToIntVec3();
                if (center.InBounds(map) && !IsSpaceFloor(map.terrainGrid.TerrainAt(center)))
                {
                    var targets = new Dictionary<Thing, float>();
                    int radius = (int)Math.Ceiling(properties.mapRadiusCells);
                    for (int x = Math.Max(0, center.x - radius); x <= Math.Min(map.Size.x - 1, center.x + radius); x++)
                        for (int z = Math.Max(0, center.z - radius); z <= Math.Min(map.Size.z - 1, center.z + radius); z++)
                        {
                            var cell = new IntVec3(x, 0, z);
                            if (IsSpaceFloor(map.terrainGrid.TerrainAt(cell))) continue;
                            float damage = properties.mapCenterDamage * scale * ShotMath.BlastFalloff((cell - center).LengthHorizontal,
                                properties.mapRadiusCells, properties.falloffExponent);
                            if (damage <= 0) continue;
                            foreach (var target in map.thingGrid.ThingsListAt(cell))
                            {
                                if (target is Pawn ? !properties.affectPawns : !properties.affectBuildings || target.def.category != ThingCategory.Building) continue;
                                if (!targets.TryGetValue(target, out float previous) || damage > previous) targets[target] = damage;
                            }
                        }
                    foreach (var target in targets)
                        if (!target.Key.Destroyed && properties.damageDef != null)
                            target.Key.TakeDamage(new DamageInfo(properties.damageDef, target.Value, properties.armorPenetration,
                                instigator: projectile.shot!.Instigator, weapon: projectile.shot.WeaponThingDef));
                    SpawnMapBlastFlecks(map, center, radius);
                }
            }
            if (properties.spaceRadiusMeters > 0 && properties.partCenterDamage > 0)
            {
                var groups = new HashSet<SpaceEntityGroup>();
                GetCombatGrid()?.CollectShipsInCube(projectile.systemBodyId, hit.PointSystem, properties.spaceRadiusMeters, groups);
                var victims = new List<(SpaceEntity entity, SpacePart part, float damage)>();
                var space = GetSpaceGameComp();
                foreach (var group in groups)
                {
                    if (!group.IsActive || !group.IsCollidable) continue;
                    foreach (int id in group.GetEntityIds())
                    {
                        var entity = space?.GetEntityById(id);
                        if (entity == null) continue;
                        foreach (var part in entity.Parts)
                        {
                            if (part.isDestroyed || part.isIndestructible) continue;
                            var position = GetGroupPos(group) + entity.Position + DVector3.Rotate(part.positionShip, entity.Rotation);
                            float damage = properties.partCenterDamage * scale * ShotMath.BlastFalloff((position - hit.PointSystem).magnitude,
                                properties.spaceRadiusMeters, properties.falloffExponent);
                            if (damage > 0) victims.Add((entity, part, damage));
                        }
                    }
                }
                foreach (var victim in victims)
                { victim.part.ApplyDamage(victim.damage); if (victim.part.isDestroyed) victim.entity.MarkPartDestroyed(victim.part.partId); }
                foreach (var victim in victims) CleanupShotMissile(victim.entity);
                activeExplosions.Add(new ExplosionInstance { position = hit.PointSystem, maxRadius = properties.spaceRadiusMeters,
                    remainingTicks = 60, totalTicks = 60, systemBodyId = projectile.systemBodyId,
                    HasImpactEvent = RecordDetonation(projectile.shot?.Definition, projectile.sourceGroupId, -1,
                        projectile.id, projectile.systemBodyId, hit.PointSystem, properties.spaceRadiusMeters,
                        hit.Group.GroupId, hit.Entity.EntityId, projectile.shot?.Profile.Instant == true) });
            }
        }
    }
}
