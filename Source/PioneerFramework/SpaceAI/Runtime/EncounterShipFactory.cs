using System;
using System.Collections.Generic;
using PioneerFramework.Components;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.SpaceAI
{
    /// <summary>Creates real ship maps and ordinary equipment. Never invokes debug spawners.</summary>
    public static class EncounterShipFactory
    {
        public static SpaceEntityGroup Create(SpaceShipTemplateDef template, Faction faction, Map source,
            DVector3 position, DVector3 velocity, int bodyId)
        {
            if (!template.TryValidate(out string reason)) throw new ArgumentException(reason, nameof(template));
            var occupied = new HashSet<IntVec3>();
            foreach (var item in template.buildings)
            {
                var def = DefDatabase<ThingDef>.GetNamed(item.defName);
                foreach (var cell in GenAdj.OccupiedRect(item.cell, item.rotation, def.size))
                    if (cell.x < 3 || cell.z < 3 || cell.x >= template.width - 3 || cell.z >= template.height - 3 || !occupied.Add(cell))
                        throw new ArgumentException("Ship equipment overlaps or crosses hull boundary: " + item.defName);
            }
            var game = Current.Game.GetComponent<SpaceGameComp>();
            int gid = game.AllocGroupId(), eid = game.AllocEntityId();
            var group = new SpaceEntityGroup(gid, faction.loadID)
            {
                CelestialBodyId = bodyId, IsStealth = true, IsMovable = true, IsCollidable = true,
                CurrentOrbit = new OrbitState {positionSystem = (SystemVec3)position, velocitySystem = (SystemVec3)velocity, rotation = DQuaternion.identity}
            };
            Map? map = null;
            bool registeredEntity = false;
            try
            {
                game.RegisterGroup(group);
                if (!ReferenceEquals(game.GetGroupById(gid),group))
                    throw new InvalidOperationException("Encounter group registration rejected");
                map = SpaceMapGenerator.GeneratePocketMap(template.width, template.height, source, "PF_SpacePocket", template.label);
                if (map == null) throw new InvalidOperationException("Encounter ship map generation failed");
                map.Parent.SetFaction(faction);
                var entity = new SpaceEntity(eid, faction.loadID) {GroupId = gid, HasMap = true, IsMovable = true, IsAffectedByGravity = true, CanInteract = true};
                var hull = new SpacePart {partId = 1, defName = "PF_ShipPart_Cube", boundsSizeShip = new DVector3(template.width,8,template.height),
                    hp = 100, isIndestructible = true};
                hull.AddComponent(new HullMassComponent {mass = template.dryMass});
                entity.SetParts(new List<SpacePart> {hull});
                entity.SetRotation(Quaternion.identity); entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
                SpaceMapGenerator.LinkEntityToMap(entity, map); game.RegisterEntity(entity);
                if (!ReferenceEquals(game.GetEntityById(eid),entity))
                    throw new InvalidOperationException("Encounter entity registration rejected");
                registeredEntity = true;
                var floor = DefDatabase<TerrainDef>.GetNamed("MetalTile");
                var area = new CellRect(2,2,template.width-4,template.height-4);
                foreach (var cell in area)
                {
                    map.terrainGrid.SetTerrain(cell,floor); map.roofGrid.SetRoof(cell,RoofDefOf.RoofConstructed);
                    if (cell.x == 2 || cell.z == 2 || cell.x == template.width-3 || cell.z == template.height-3)
                    {
                        var wall = ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.Steel); wall.SetFaction(faction);
                        GenSpawn.Spawn(wall,cell,map);
                    }
                }
                foreach (var item in template.buildings)
                {
                    var def = DefDatabase<ThingDef>.GetNamed(item.defName);
                    var thing = ThingMaker.MakeThing(def,def.MadeFromStuff ? ThingDefOf.Steel : null); thing.SetFaction(faction);
                    GenSpawn.Spawn(thing,item.cell,map,item.rotation);
                    var tank = thing.TryGetComp<CompPropellantTank>();
                    if (tank?.PropellantDef != null)
                    {
                        double additional = tank.Capacity-tank.StoredMass;
                        tank.LandingStoredMass = tank.Capacity;
                        map.GetComponent<SpaceMapComp>().AddPropellantMass(tank.PropellantDef,additional);
                    }
                    var weapon = thing.TryGetComp<CompMissileLauncher>()?.Weapon ?? thing.TryGetComp<CompMapTurret>()?.Mount?.weapon;
                    if (weapon != null) weapon.ammo = template.ammunition;
                }
                map.fogGrid.ClearAllFog();
                return group;
            }
            catch
            {
                RollbackCreation(game, group, map, registeredEntity);
                throw;
            }
        }

        internal static void RollbackCreation(SpaceGameComp game, SpaceEntityGroup group, Map? map, bool registeredEntity)
        {
            if (ReferenceEquals(game.GetGroupById(group.GroupId),group))
                game.DestroyGroup(group.GroupId,"Encounter generation rollback");
            if (!registeredEntity && map != null && SpaceMapGenerator.FindPocketMap(map.uniqueID) != null)
                SpaceMapGenerator.DestroyPocketMap(map);
        }
    }
}
