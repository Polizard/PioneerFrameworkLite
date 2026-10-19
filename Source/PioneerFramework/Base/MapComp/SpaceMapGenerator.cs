using System.Collections.Generic;
using RimWorld.Planet;
using Verse;
using UnityEngine;
using RimWorld;
using PioneerFramework.Combat;
using PioneerFramework.Components;

namespace PioneerFramework
{
    public static class SpaceMapGenerator
    {
        /// <summary>
        /// 生成一个口袋地图（空间站内部地图）。
        /// 关键：使用 PocketMapUtility，所有空间站共享同一个sourceMap的tile，不占用额外tile
        /// </summary>
        /// <param name="width">地图宽度（格数）</param>
        /// <param name="height">地图高度（格数）</param>
        /// <param name="mapName">地图名称（仅日志）</param>
        /// <returns>生成的 Map 对象，失败返回 null</returns>
        public static Map? GeneratePocketMap(int width, int height, string? mapName = null)
        {
            var anchorMap = GetAnchorMap();
            return GeneratePocketMap(width, height, anchorMap, "PF_SpacePocket", mapName);
        }

        /// <summary>
        /// 生成一个口袋地图（空间站内部地图）。
        /// 关键：使用 PocketMapUtility，所有空间站共享同一个sourceMap的tile，不占用额外tile
        /// </summary>
        /// <param name="width">地图宽度（格数）</param>
        /// <param name="height">地图高度（格数）</param>
        /// <param name="sourceMap">锚点地图（所有空间站共享同一个）</param>
        /// <param name="mapName">地图名称（仅日志）</param>
        /// <returns>生成的 Map 对象，失败返回 null</returns>
        public static Map? GeneratePocketMap(int width, int height, Map sourceMap, string? mapName = null)
        {
            return GeneratePocketMap(width, height, sourceMap, "PF_SpacePocket", mapName);
        }

        /// <summary>
        /// 生成一个口袋地图（指定生成器）。
        /// </summary>
        public static Map? GeneratePocketMap(int width, int height, Map? sourceMap, string generatorDefName, string? mapName = null)
        {
            // 1. 检查sourceMap有效性
            if (sourceMap == null)
            {
                Log.Error("[PioneerFramework] sourceMap cannot be null. Use GetAnchorMap() to get a valid anchor map.");
                return null;
            }

            // 2. 获取口袋地图生成器（使用mod自定义的生成器）
            // 兜底：如果自定义生成器不存在，尝试SpacePocket(Odyssey)或Encounter
            var generatorDef = DefDatabase<MapGeneratorDef>.GetNamedSilentFail(generatorDefName);
            if (generatorDef == null)
            {
                generatorDef = DefDatabase<MapGeneratorDef>.GetNamedSilentFail("SpacePocket");
                if (generatorDef == null)
                {
                    Log.Warning("[PioneerFramework] " + generatorDefName + " not found, fallback to SpacePocket.");
                    generatorDef = MapGeneratorDefOf.Encounter;
                }
            }

            // 检查generator是否有pocketMapProperties（必须要有，否则会NPE）
            if (generatorDef.pocketMapProperties == null)
            {
                Log.Error("[PioneerFramework] MapGenerator " + generatorDef.defName + " has no pocketMapProperties defined. Pocket map cannot be generated.");
                return null;
            }

            // 3. 使用自定义 SpaceStationMapParent 生成口袋地图
            // 避免使用 PocketMapUtility（它会创建普通的 PocketMapParent，tile=-1
            // 会导致相机定位时崩溃）
            var stationDef = DefDatabase<WorldObjectDef>.GetNamedSilentFail("PF_SpaceStation");
            if (stationDef == null)
            {
                Log.Error("[PioneerFramework] PF_SpaceStation WorldObjectDef not found.");
                return null;
            }
            var parent = WorldObjectMaker.MakeWorldObject(stationDef) as SpaceStationMapParent;
            if (parent == null)
            {
                Log.Error("[PioneerFramework] Failed to create SpaceStationMapParent.");
                return null;
            }
            parent.sourceMap = sourceMap;
            parent.mapGenerator = generatorDef;

            var map = MapGenerator.GenerateMap(
                new IntVec3(width, 1, height),
                parent,
                generatorDef,
                null,
                null,
                true,      // isPocketMap = true
                false
            );

            if (map != null)
                Find.World.pocketMaps.Add(parent);

            if (map == null)
            {
                Log.Error($"[PioneerFramework] Failed to generate pocket map: {(mapName ?? "Unnamed")}");
                return null;
            }

            // 4. 添加 SpaceMapComp（用于后续建筑管理、持久化等）
            var mapComp = map.GetComponent<SpaceMapComp>();
            if (mapComp == null)
            {
                mapComp = new SpaceMapComp(map);
                map.components.Add(mapComp);
            }

            // 5. 注册到 SpaceGameComp，便于后续查找
            var spaceGame = Current.Game.GetComponent<SpaceGameComp>();
            if (spaceGame != null)
                spaceGame.RegisterMapComponent(map.uniqueID, mapComp);

            Log.Message($"[PioneerFramework] Generated pocket map '{(mapName ?? "SpaceMap")}' with ID {map.uniqueID}, size {width}x{height}, anchored to tile {sourceMap.Tile}");
            return map;
        }

        /// <summary>
        /// 生成航天器独立地图（非口袋地图语义）。
        /// 使用 SpacecraftMapParent，不加入 Find.World.pocketMaps。
        /// </summary>
        public static Map? GenerateSpacecraftMap(int width, int height, Map sourceMap, string spacecraftId, string generatorDefName)
        {
            if (sourceMap == null)
                return null;

            var generatorDef = DefDatabase<MapGeneratorDef>.GetNamedSilentFail(generatorDefName);
            if (generatorDef == null || generatorDef.pocketMapProperties == null)
            {
                Log.Error("[PioneerFramework] Spacecraft map generator missing or has no pocketMapProperties: " + generatorDefName);
                return null;
            }

            var spacecraftDef = DefDatabase<WorldObjectDef>.GetNamedSilentFail("PF_SpacecraftMap");
            if (spacecraftDef == null)
            {
                Log.Error("[PioneerFramework] PF_SpacecraftMap WorldObjectDef not found.");
                return null;
            }

            var parent = WorldObjectMaker.MakeWorldObject(spacecraftDef) as SpacecraftMapParent;
            if (parent == null)
            {
                Log.Error("[PioneerFramework] Failed to create SpacecraftMapParent.");
                return null;
            }

            parent.sourceMap = sourceMap;
            parent.mapGenerator = generatorDef;
            parent.spacecraftId = spacecraftId;
            parent.state = SpacecraftState.Ground;

            // 注意：isPocketMap 必须为 true。这是 RimWorld 引擎对 Tile=-1 地图的技术要求，
            // 用于使用 pocketTileInfo 而不是访问 Find.WorldGrid[-1]。
            // 架构上我们已使用 SpacecraftMapParent + SpacecraftMapRegistry，不依赖口袋地图语义。
            var map = MapGenerator.GenerateMap(
                new IntVec3(width, 1, height),
                parent,
                generatorDef,
                null,
                null,
                true,
                false
            );

            if (map == null)
            {
                Log.Error("[PioneerFramework] Failed to generate spacecraft map.");
                return null;
            }

            var mapComp = map.GetComponent<SpaceMapComp>();
            if (mapComp == null)
            {
                mapComp = new SpaceMapComp(map);
                map.components.Add(mapComp);
            }

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame != null)
                spaceGame.RegisterMapComponent(map.uniqueID, mapComp);

            Log.Message($"[PioneerFramework] Generated spacecraft map ID {map.uniqueID} for spacecraft {spacecraftId}, {width}x{height}");
            return map;
        }

        /// <summary>
        /// 获取锚点地图（用于所有空间站共享tile）
        /// 优先返回玩家殖民地地图，如果没有则返回当前地图
        /// </summary>
        public static Map? GetAnchorMap()
        {
            // 优先找玩家的殖民地地图
            foreach (var map in Find.Maps)
            {
                if (map.IsPlayerHome)
                    return map;
            }
            // 其次返回当前地图
            if (Current.Game.CurrentMap != null)
                return Current.Game.CurrentMap;
            // 最后返回第一个加载的地图
            if (Find.Maps.Count > 0)
                return Find.Maps[0];
            
            Log.Error("[PioneerFramework] No valid anchor map found.");
            return null;
        }

        /// <summary>
        /// 销毁指定的口袋地图（正确清理所有引用）
        /// 使用 PocketMapUtility.DestroyPocketMap 确保正确清理
        /// </summary>
        public static void DestroyPocketMap(Map map)
        {
            if (map == null) return;
            
            // 取消注册到 SpaceGameComp
            var spaceGame = Current.Game.GetComponent<SpaceGameComp>();
            if (spaceGame != null)
                spaceGame.UnregisterMapComponent(map.uniqueID);
            
            // 使用 PocketMapUtility 正确销毁地图
            PocketMapUtility.DestroyPocketMap(map);
            Log.Message($"[PioneerFramework] Destroyed pocket map with ID {map.uniqueID}");
        }

        internal static void DestroyShipMap(Map map)
        {
            if (map == null) return;
            if (map.Parent is PocketMapParent) DestroyPocketMap(map);
            else
            {
                Current.Game?.GetComponent<SpaceGameComp>()?.UnregisterMapComponent(map.uniqueID);
                Current.Game.DeinitAndRemoveMap(map, notifyPlayer: true);
            }
        }

        /// <summary>
        /// 生成一个空白口袋地图（仅框架，无建筑无地形），用于取档恢复。
        /// 使用相同的 SpaceStationMapParent + PF_SpacePocket 生成器。
        /// </summary>
        public static Map? GenerateEmptyPocketMap(int width, int height)
        {
            var anchorMap = GetAnchorMap();
            if (anchorMap == null) return null;

            var generatorDef = DefDatabase<MapGeneratorDef>.GetNamedSilentFail("PF_SpacePocket")
                ?? DefDatabase<MapGeneratorDef>.GetNamedSilentFail("SpacePocket")
                ?? MapGeneratorDefOf.Encounter;

            if (generatorDef.pocketMapProperties == null)
            {
                Log.Error("[PioneerFramework] MapGenerator has no pocketMapProperties.");
                return null;
            }

            var stationDef = DefDatabase<WorldObjectDef>.GetNamedSilentFail("PF_SpaceStation");
            if (stationDef == null)
            {
                Log.Error("[PioneerFramework] PF_SpaceStation WorldObjectDef not found.");
                return null;
            }

            var parent = WorldObjectMaker.MakeWorldObject(stationDef) as SpaceStationMapParent;
            if (parent == null)
            {
                Log.Error("[PioneerFramework] Failed to create SpaceStationMapParent.");
                return null;
            }
            parent.sourceMap = anchorMap;
            parent.mapGenerator = generatorDef;

            // 注意：isPocketMap 必须为 true。这是 RimWorld 引擎对 Tile=-1 地图的技术要求，
            // 用于使用 pocketTileInfo 而不是访问 Find.WorldGrid[-1]。
            // 架构上我们已使用 SpacecraftMapParent + SpacecraftMapRegistry，不依赖口袋地图语义。
            var map = MapGenerator.GenerateMap(
                new IntVec3(width, 1, height),
                parent,
                generatorDef,
                null,
                null,
                true,
                false
            );

            if (map != null)
                Find.World.pocketMaps.Add(parent);

            if (map == null)
            {
                Log.Error("[PioneerFramework] Failed to generate empty pocket map for restore.");
                return null;
            }

            // 添加 MapComponent
            var mapComp = map.GetComponent<SpaceMapComp>();
            if (mapComp == null)
            {
                mapComp = new SpaceMapComp(map);
                map.components.Add(mapComp);
            }

            // 注册
            var spaceGame = Current.Game.GetComponent<SpaceGameComp>();
            if (spaceGame != null)
                spaceGame.RegisterMapComponent(map.uniqueID, mapComp);

            Log.Message($"[PioneerFramework] Generated empty pocket map ID {map.uniqueID}, size {width}x{height} for archive restore");
            return map;
        }

        /// <summary>
        /// 进入指定的口袋地图。
        /// </summary>
        public static void EnterPocketMap(Map map)
        {
            if (map != null)
            {
                Current.Game.CurrentMap = map;
                CameraJumper.TryHideWorld();
            }
            else
            {
                Messages.Message("Map not found or already unloaded.", MessageTypeDefOf.RejectInput);
            }
        }

        /// <summary>
        /// 通过地图ID查找口袋地图。
        /// </summary>
        public static Map FindPocketMap(int mapId)
        {
            return Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
        }

        /// <summary>
        /// 创建空间站实体并关联到地图。
        /// 同时创建一个 Part（尺寸 = mapWidth × 50m × mapHeight）代表空间站物理体积。
        /// 1 格 = 1 米，Part 厚度固定 50m。
        /// </summary>
        public static SpaceEntity CreateStationEntity(int entityId, int factionId, DVector3 initialPosition,
            int mapWidth, int mapHeight)
        {
            var entity = new SpaceEntity(entityId, factionId)
            {
                CanInteract = true,
                IsSelectable = true,
                HasMap = true,
                IsMovable = true,
                IsAffectedByGravity = true
            };
            // entity.Position/Velocity 为相对 group 原点的局部偏移（实体在 group 原点 → zero）
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            // 姿态必须显式置位：不设就是 (0,0,0,0)，物理端（Rotate/AABB 展开式）恰好等价单位姿态，
            // 但它不是合法四元数——表现层（Matrix4x4.TRS）拿到它会把网格压成一个点（不可见）。
            entity.SetRotation(Quaternion.identity);

            // 创建空间站主体 Part：宽=mapWidth, 高=50m(厚度), 深=mapHeight
            var part = new SpacePart
            {
                partId = 1, // 空间站只有一个 Part，固定 ID
                defName = "PF_ShipPart_Cube",
                positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(mapWidth, 50.0, mapHeight),
                hp = 100f,
                // 空间站是地图载体：无 Part 血条，存亡由 Map 内建筑决定（射弹命中不扣 Part HP）
                isIndestructible = true,
            };
            // 站体质量（2026-09-06 补：此前无质量组件 → 1kg 兜底 → 任何爆炸冲量（弹头 AoE / 船-船）
            // 把空间站以 ~1.6e4 m/s 打飞离轨——弹头命中后目标成逃逸体、后续导弹全部追丢。
            // 口径：part 体积 mapW×50×mapH m³ × 90kg/m³（低密度站壳，内部空旷）
            // ≈ 60×60 站 1.6e4 t → 弹头冲量对站速影响 <1m/s（站保持轨道，受击不漂移）。
            part.AddComponent(new HullMassComponent { mass = mapWidth * 50.0f * mapHeight * 90.0f });
            // [V7] 站载传感器阵列：空间站是固定大平台 ⇒ 灵敏度最高的一档（2000 km）+ 一点抗性
            part.AddComponent(new SensorArrayComponentData
            {
                passiveRangeMeters = 2_000_000f, ecmResist = 1.0f, eccmResist = 1.0f
            });
            entity.SetParts(new List<SpacePart> { part });

            return entity;
        }

        /// <summary>
        /// 将空间站实体与地图双向关联。map 可为 null（生成失败时调用方直接跳过；实现本就早退）。
        /// </summary>
        public static void LinkEntityToMap(SpaceEntity entity, Map? map)
        {
            if (entity == null || map == null) return;
            entity.MapId = map.uniqueID;
            entity.HasMap = true;

            // 双向映射：Map 侧
            var mapComp = map.GetComponent<SpaceMapComp>();
            if (mapComp != null)
            {
                mapComp.ownerEntityId = entity.EntityId;
                // 取第一个 Part 作为地图对应的部件（空间站只有一个 Part）
                foreach (var part in entity.Parts)
                {
                    if (!part.isDestroyed)
                    {
                        part.linkedMapId = map.uniqueID;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 获取空间站实体关联的地图。
        /// </summary>
        public static Map? GetEntityMap(SpaceEntity entity)
        {
            if (entity == null || entity.MapId < 0) return null;
            return FindPocketMap(entity.MapId);
        }

        /// <summary>
        /// 从空间站实体进入其内部地图。
        /// </summary>
        public static bool EnterStation(SpaceEntity stationEntity)
        {
            if (stationEntity == null || !stationEntity.HasMap)
            {
                Messages.Message("Station has no internal map.", MessageTypeDefOf.RejectInput);
                return false;
            }
            
            var map = GetEntityMap(stationEntity);
            if (map != null)
            {
                EnterPocketMap(map);
                return true;
            }
            else
            {
                Messages.Message("Station map not found.", MessageTypeDefOf.RejectInput);
                return false;
            }
        }

        /// <summary>
        /// 为舱室部件生成内部地图（懒生成）：首次调用时创建口袋地图并双向关联。
        /// 已生成过（linkedMapId 指向存在的地图）时直接返回现有地图，幂等。
        /// 地图尺寸 = 部件水平面物理尺寸（1 格 = 1 米）。
        /// </summary>
        public static Map? GenerateRoomMap(SpaceEntity entity, SpacePart part)
        {
            if (part == null || part.isDestroyed || !part.IsRoomModulePart)
                return null;

            if (part.linkedMapId >= 0)
            {
                var existing = FindPocketMap(part.linkedMapId);
                if (existing != null) return existing;
            }

            // 生成器：优先取舱室类型指定，回退默认房间生成器
            var roomComp = part.GetComponent<RoomComponentData>();
            string genName = (part.RoomTypeDef != null && !string.IsNullOrEmpty(part.RoomTypeDef.mapGeneratorDefName))
                ? part.RoomTypeDef.mapGeneratorDefName
                : "PF_RoomPocket";

            int w = Mathf.Max(8, roomComp != null ? roomComp.mapWidth : 8);
            int h = Mathf.Max(8, roomComp != null ? roomComp.mapHeight : 8);
            var map = GeneratePocketMap(w, h, GetAnchorMap(), genName, "RoomMap");
            if (map == null)
            {
                Log.Error($"[RoomMap] 舱室地图生成失败: part {part.partId} ({w}x{h})");
                return null;
            }

            var mapComp = map.GetComponent<SpaceMapComp>();
            if (mapComp != null)
            {
                mapComp.ownerEntityId = entity.EntityId;
            }
            part.linkedMapId = map.uniqueID;

            Log.Message($"[RoomMap] 舱室地图已生成: part {part.partId}, map {map.uniqueID}, {w}x{h}");
            return map;
        }

        /// <summary>
        /// 销毁舱室地图：先杀死地图内所有 pawns（舱室被毁，无人生还），再销毁地图。
        /// </summary>
        public static void DestroyRoomMap(int mapId)
        {
            var map = FindPocketMap(mapId);
            if (map == null) return;

            var pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = pawns.Count - 1; i >= 0; i--)
            {
                var p = pawns[i];
                if (p != null && !p.Dead && !p.Destroyed)
                    p.Kill(new DamageInfo(DamageDefOf.SurgicalCut, 99999f));
            }

            DestroyPocketMap(map);
            Log.Message($"[RoomMap] 舱室地图 {mapId} 已销毁（内部 pawns 已清除）");
        }
    }
}
