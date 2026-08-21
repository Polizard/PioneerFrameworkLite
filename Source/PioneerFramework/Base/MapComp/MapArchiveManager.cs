using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 地图封存/取出管理器。
    /// 封存：遍历 Map 上所有建筑 → 二进制写入 .pfmap 文件 → 销毁 Map。
    /// 取出：读取 .pfmap → 创建空地图 → 多 tick 分批生成建筑。
    /// 
    /// 性能策略：整个封存/取出操作共用一个临时文件，Comp 序列化时反复覆盖写入，
    /// 避免每个 Comp 创建/删除临时文件的磁盘开销。
    /// </summary>
    public static class MapArchiveManager
    {
        private const string ARCHIVE_FOLDER_NAME = "PioneerFramework/ArchivedMaps";
        private const string ARCHIVE_EXTENSION = ".pfmap";

        // Scribe 反射缓存（懒加载，避免静态初始化时 Assembly 未就绪）
        private static FieldInfo _saverField;
        private static FieldInfo _loaderField;
        private static FieldInfo _curXmlParentField;

        private static FieldInfo SaverField =>
            _saverField ??= typeof(Scribe).GetField("saver",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        private static FieldInfo LoaderField =>
            _loaderField ??= typeof(Scribe).GetField("loader",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        private static FieldInfo CurXmlParentField =>
            _curXmlParentField ??= typeof(ScribeLoader).GetField("curXmlParent",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // Scribe 静态状态：封存/取出会临时替换 Scribe.saver/loader；
        // 结束后必须恢复原始实例，否则后续正常存档/读档会因 null 而 NRE。
        private static ScribeSaver originalScribeSaver;
        private static ScribeLoader originalScribeLoader;

        // ═══════════════════════════════════════════════
        //  公共接口
        // ═══════════════════════════════════════════════

        /// <summary>获取封存文件路径。</summary>
        public static string GetArchivePath(int mapId)
        {
            string folder = Path.Combine(GenFilePaths.SaveDataFolderPath, ARCHIVE_FOLDER_NAME);
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, mapId + ARCHIVE_EXTENSION);
        }

        /// <summary>检查是否存在封存文件。</summary>
        public static bool ArchiveExists(int mapId)
        {
            return File.Exists(GetArchivePath(mapId));
        }

        /// <summary>
        /// 读取封存数据并初始化取档操作（多 tick 异步）。
        /// 返回准备好的 MapRestoreOperation，由 SpaceGameComp.Tick() 驱动。
        /// </summary>
        public static MapRestoreOperation BeginRestoreMap(SpaceEntity entity)
        {
            if (entity == null || entity.MapId < 0)
            {
                Log.Error("[MapArchive] BeginRestoreMap: invalid entity or mapId");
                return null;
            }

            string filePath = GetArchivePath(entity.MapId);
            if (!File.Exists(filePath))
            {
                Log.Error(string.Format("[MapArchive] Archive file not found: {0}", filePath));
                return null;
            }

            // 1. 读取二进制数据
            if (!MapArchiveFormat.TryRead(filePath,
                    out int mapWidth, out int mapHeight,
                    out byte[] compressedData,
                    out List<BuildingRecord> buildingRecords,
                    out List<TerrainRecord> terrainRecords))
            {
                return null;
            }

            // 2. 创建空口袋地图
            var map = SpaceMapGenerator.GenerateEmptyPocketMap(mapWidth, mapHeight);
            if (map == null)
            {
                Log.Error("[MapArchive] Failed to create empty pocket map for restore");
                return null;
            }

            // 3. 创建取档操作（后续由 SpaceGameComp 多 tick 执行）
            var op = new MapRestoreOperation
            {
                TargetEntity = entity,
                NewMap = map,
                BuildingRecords = buildingRecords,
                TerrainRecords = terrainRecords,
                CompressedData = compressedData,
                MapWidth = mapWidth,
                MapHeight = mapHeight,
                CurrentBatchIndex = 0,
                Phase = 0,
                ArchiveFilePath = filePath
            };

            // Phase 0: 立即完成地形 + 压缩物铺设（1 tick）
            ApplyTerrain(map, terrainRecords);
            ApplyCompressedThings(map, compressedData);

            Log.Message(string.Format("[MapArchive] Restore initiated for map {0}: {1} buildings, size {2}x{3}",
                entity.MapId, buildingRecords.Count, mapWidth, mapHeight));

            return op;
        }

        // ═══════════════════════════════════════════════
        //  异步封存（多 tick）
        // ═══════════════════════════════════════════════

        /// <summary>
        /// 开始封存操作（Phase 0：收集建筑 + 压缩物 + 地形，1 tick）。
        /// 返回 MapArchiveOperation，后续由 SpaceGameComp 多 tick 驱动。
        /// </summary>
        public static MapArchiveOperation BeginArchiveMap(Map map, SpaceEntity entity)
        {
            if (map == null || entity == null)
            {
                Log.Error("[MapArchive] BeginArchiveMap: map or entity is null");
                return null;
            }

            if (map.mapPawns.AllPawns.Any())
            {
                Messages.Message("Cannot archive: pawns still present on the station.",
                    MessageTypeDefOf.RejectInput, false);
                return null;
            }

            string filePath = GetArchivePath(map.uniqueID);

            // Phase 0: 收集需要序列化的建筑 + 压缩物 + 地形（纯读操作，快）
            var buildings = new List<Building>();
            foreach (var thing in map.listerThings.AllThings)
            {
                if (!thing.def.isSaveable || thing.def.saveCompressible) continue;
                if (thing.Destroyed) continue;
                var building = thing as Building;
                if (building != null)
                    buildings.Add(building);
            }

            byte[] compressedData = BuildCompressedData(map);
            List<TerrainRecord> terrainRecords = BuildTerrainRecords(map);
            string tmpPath = Path.GetTempFileName();

            var op = new MapArchiveOperation
            {
                TargetEntity = entity,
                SourceMap = map,
                CompressedData = compressedData,
                TerrainRecords = terrainRecords,
                BuildingsToProcess = buildings,
                CurrentBuildingIndex = 0,
                TempFilePath = tmpPath,
                ArchiveFilePath = filePath,
                Phase = 1
            };

            Log.Message(string.Format("[MapArchive] Archive initiated for map {0}: {1} buildings",
                map.uniqueID, buildings.Count));

            return op;
        }

        /// <summary>
        /// 处理一批建筑的 Comp 序列化。返回 true 表示全部完成。
        /// </summary>
        public static bool ProcessArchiveBatch(MapArchiveOperation op, int batchSize)
        {
            if (op.BuildingsToProcess == null || op.BuildingsToProcess.Count == 0)
                return true;

            int processed = 0;
            while (processed < batchSize && op.CurrentBuildingIndex < op.BuildingsToProcess.Count)
            {
                var building = op.BuildingsToProcess[op.CurrentBuildingIndex];
                op.CurrentBuildingIndex++;
                processed++;

                try
                {
                    var record = new BuildingRecord
                    {
                        defShortHash = building.def.shortHash,
                        posX = (short)building.Position.x,
                        posZ = (short)building.Position.z,
                        rotation = (byte)building.Rotation.AsInt,
                        stuffShortHash = building.Stuff?.shortHash ?? (ushort)0,
                        factionId = building.Faction?.loadID ?? -1,
                        hitPoints = building.HitPoints
                    };

                    var thingWithComps = building as ThingWithComps;
                    if (thingWithComps?.AllComps != null)
                    {
                        foreach (var comp in thingWithComps.AllComps)
                        {
                            byte[] xmlData = SerializeCompData(comp, op.TempFilePath);
                            if (xmlData != null && xmlData.Length > 0)
                            {
                                var compRecord = new CompRecord
                                {
                                    compTypeName = comp.GetType().FullName ?? comp.GetType().Name,
                                    xmlData = xmlData
                                };
                                record.comps.Add(compRecord);
                            }
                        }
                    }

                    op.BuildingRecords.Add(record);
                }
                catch (Exception ex)
                {
                    Log.Warning(string.Format("[MapArchive] Failed to serialize building {0}: {1}",
                        building.def.shortHash, ex.Message));
                }
            }

            return op.CurrentBuildingIndex >= op.BuildingsToProcess.Count;
        }

        /// <summary>
        /// 完成封存：写入文件 + 销毁地图 + 更新实体状态。
        /// </summary>
        public static void FinalizeArchive(MapArchiveOperation op)
        {
            // 写入 pfmap 文件
            MapArchiveFormat.Write(op.ArchiveFilePath,
                op.SourceMap.Size.x, op.SourceMap.Size.z,
                op.CompressedData, op.BuildingRecords, op.TerrainRecords);

            // 销毁地图
            int mapId = op.SourceMap.uniqueID;
            SpaceMapGenerator.DestroyPocketMap(op.SourceMap);

            // 标记实体
            op.TargetEntity.MapId = mapId;
            op.TargetEntity.HasMap = false;
            op.TargetEntity.IsMapArchived = true;

            // 清理临时文件
            if (File.Exists(op.TempFilePath))
                File.Delete(op.TempFilePath);

            Log.Message(string.Format("[MapArchive] Archived map {0} to {1} ({2} buildings, {3} terrains)",
                mapId, op.ArchiveFilePath, op.BuildingRecords.Count, op.TerrainRecords.Count));
            Messages.Message("地图已封存。", MessageTypeDefOf.SilentInput, false);
        }

        // ═══════════════════════════════════════════════
        //  Phase 0: 地形 & 压缩物
        // ═══════════════════════════════════════════════

        private static void ApplyTerrain(Map map, List<TerrainRecord> records)
        {
            if (records == null) return;

            foreach (var rec in records)
            {
                var cell = new IntVec3(rec.x, 0, rec.z);
                if (!cell.InBounds(map)) continue;

                var terrainDef = DefDatabase<TerrainDef>.GetNamedSilentFail(rec.terrainDefName);
                if (terrainDef != null)
                    map.terrainGrid.SetTerrain(cell, terrainDef);
            }
        }

        private static void ApplyCompressedThings(Map map, byte[] compressedData)
        {
            if (compressedData == null || compressedData.Length == 0) return;

            // 构建 shortHash → ThingDef 字典
            var hashToDef = new Dictionary<ushort, ThingDef>();
            foreach (var def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def.saveCompressible && !hashToDef.ContainsKey(def.shortHash))
                    hashToDef[def.shortHash] = def;
            }

            // 解析每格数据
            int totalCells = map.Size.x * map.Size.z;
            int count = Math.Min(compressedData.Length / 2, totalCells);

            for (int i = 0; i < count; i++)
            {
                ushort hash = (ushort)(compressedData[i * 2] | (compressedData[i * 2 + 1] << 8));
                if (hash == 0) continue;

                if (!hashToDef.TryGetValue(hash, out var def)) continue;

                try
                {
                    var cell = new IntVec3(i % map.Size.x, 0, i / map.Size.x);
                    var thing = ThingMaker.MakeThing(def, null);
                    thing.SetPositionDirect(cell);
                    GenSpawn.Spawn(thing, cell, map, Rot4.North, WipeMode.Vanish, true);
                }
                catch (Exception ex)
                {
                    Log.Warning(string.Format("[MapArchive] Failed to spawn compressed thing {0}: {1}", def.defName, ex.Message));
                }
            }
        }

        // ═══════════════════════════════════════════════
        //  Phase 1: 非压缩物（每 tick 一批，由 SpaceGameComp 驱动）
        // ═══════════════════════════════════════════════

        /// <summary>处理一批非压缩建筑。返回 true 表示全部完成。</summary>
        public static bool ProcessBuildingBatch(MapRestoreOperation op, int batchSize)
        {
            if (op.BuildingRecords == null || op.BuildingRecords.Count == 0)
                return true;

            // 构建 hash → def 字典（首次调用时）
            if (op._hashToDef == null)
            {
                op._hashToDef = new Dictionary<ushort, ThingDef>();
                foreach (var def in DefDatabase<ThingDef>.AllDefs)
                {
                    if (!op._hashToDef.ContainsKey(def.shortHash))
                        op._hashToDef[def.shortHash] = def;
                }
            }

            // Faction 缓存
            if (op._factionCache == null)
                op._factionCache = new Dictionary<int, Faction>();

            int processed = 0;
            while (processed < batchSize && op.CurrentBatchIndex < op.BuildingRecords.Count)
            {
                var record = op.BuildingRecords[op.CurrentBatchIndex];
                op.CurrentBatchIndex++;
                processed++;

                try
                {
                    SpawnBuildingFromRecord(op.NewMap, record, op._hashToDef, op._factionCache);
                }
                catch (Exception ex)
                {
                    Log.Warning(string.Format("[MapArchive] Failed to spawn building {0}: {1}",
                        record.defShortHash, ex.Message));
                }
            }

            return op.CurrentBatchIndex >= op.BuildingRecords.Count;
        }

        private static void SpawnBuildingFromRecord(Map map, BuildingRecord record,
            Dictionary<ushort, ThingDef> hashToDef, Dictionary<int, Faction> factionCache)
        {
            if (!hashToDef.TryGetValue(record.defShortHash, out var thingDef))
            {
                Log.Warning(string.Format("[MapArchive] Unknown def hash: {0}", record.defShortHash));
                return;
            }

            // Stuff
            ThingDef stuffDef = null;
            if (record.stuffShortHash != 0 && !hashToDef.TryGetValue(record.stuffShortHash, out stuffDef))
            {
                Log.Warning(string.Format("[MapArchive] Unknown stuff hash: {0}", record.stuffShortHash));
            }

            // 创建 Thing
            var thing = ThingMaker.MakeThing(thingDef, stuffDef);
            var cell = new IntVec3(record.posX, 0, record.posZ);
            if (!cell.InBounds(map)) return;

            thing.SetPositionDirect(cell);
            thing.Rotation = new Rot4(record.rotation);
            thing.HitPoints = record.hitPoints;

            // Faction
            if (record.factionId >= 0)
            {
                if (!factionCache.TryGetValue(record.factionId, out var faction))
                {
                    faction = Find.FactionManager?.AllFactions
                        .FirstOrDefault(f => f.loadID == record.factionId);
                    if (faction != null)
                        factionCache[record.factionId] = faction;
                }
                if (faction != null)
                    thing.SetFactionDirect(faction);
            }

            // 直接 Spawn（跳过 GenSpawn.Spawn 对空地图不必要的校验）
            SpawnBuildingDirect(thing, map);

            // 恢复 Comp 数据
            if (record.comps != null && record.comps.Count > 0)
            {
                var thingWithComps = thing as ThingWithComps;
                if (thingWithComps?.AllComps != null)
                {
                    foreach (var compRecord in record.comps)
                    {
                        foreach (var comp in thingWithComps.AllComps)
                        {
                            if (comp.GetType().Name == compRecord.compTypeName ||
                                comp.GetType().FullName == compRecord.compTypeName)
                            {
                                DeserializeCompData(comp, compRecord.xmlData);
                                break;
                            }
                        }
                    }
                }
            }
        }

        // ═══════════════════════════════════════════════
        //  序列化辅助
        // ═══════════════════════════════════════════════

        /// <summary>
        /// 精简版 Spawn：跳过 GenSpawn.Spawn 对空地图不必要的校验
        /// （边界检查、WipeExistingThings、物品数量检查、寻路恢复等），
        /// 直接调用 SpawnSetup(respawningAfterLoad=true)。
        /// </summary>
        private static void SpawnBuildingDirect(Thing newThing, Map map)
        {
            if (newThing.holdingOwner != null)
                newThing.holdingOwner.Remove(newThing);
            newThing.SpawnSetup(map, true);
        }

        /// <summary>构建压缩物数据（ushort 网格）。</summary>
        private static byte[] BuildCompressedData(Map map)
        {
            int totalCells = map.Size.x * map.Size.z;
            var data = new byte[totalCells * 2];

            for (int i = 0; i < totalCells; i++)
            {
                var cell = new IntVec3(i % map.Size.x, 0, i / map.Size.x);
                ushort hash = 0;
                var things = map.thingGrid.ThingsListAtFast(cell);
                foreach (var thing in things)
                {
                    if (thing.def.saveCompressible)
                    {
                        hash = thing.def.shortHash;
                        break;
                    }
                }
                data[i * 2] = (byte)(hash & 0xFF);
                data[i * 2 + 1] = (byte)((hash >> 8) & 0xFF);
            }

            return data;
        }

        /// <summary>构建地形记录（仅保存非默认地形）。</summary>
        private static List<TerrainRecord> BuildTerrainRecords(Map map)
        {
            var records = new List<TerrainRecord>();

            foreach (var cell in map.AllCells)
            {
                var terrainDef = map.terrainGrid.TerrainAt(cell);
                if (terrainDef == null)
                    continue;

                records.Add(new TerrainRecord
                {
                    x = (short)cell.x,
                    z = (short)cell.z,
                    terrainDefName = terrainDef.defName
                });
            }

            return records;
        }

        // ═══════════════════════════════════════════════
        //  Comp 序列化 / 反序列化（共享临时文件）
        // ═══════════════════════════════════════════════

        /// <summary>
        /// 将 Comp 序列化为 XML 字节数组。
        /// tmpPath 由调用方提供并复用，避免每个 Comp 创建/删除临时文件。
        /// </summary>
        private static byte[] SerializeCompData(ThingComp comp, string tmpPath)
        {
            if (comp == null) return Array.Empty<byte>();

            if (Scribe.mode != LoadSaveMode.Inactive)
            {
                Log.Error("[MapArchive] Cannot serialize comp - Scribe is active (save/load in progress)");
                return Array.Empty<byte>();
            }

            try
            {
                PreserveOriginalScribeState();
                var saver = new ScribeSaver();
                SetScribeSaver(saver);
                saver.InitSaving(tmpPath, "CompRoot");
                comp.PostExposeData();
                saver.FinalizeSaving();

                return File.ReadAllBytes(tmpPath);
            }
            catch (Exception ex)
            {
                Log.Warning(string.Format("[MapArchive] Failed to serialize comp {0}: {1}",
                    comp.GetType().Name, ex.Message));
                return Array.Empty<byte>();
            }
            finally
            {
                if (Scribe.mode != LoadSaveMode.Inactive)
                    Scribe.ForceStop();
                RestoreOriginalScribeState();
            }
        }

        /// <summary>
        /// 从 XML 字节数组反序列化 Comp 数据（纯内存，零磁盘 I/O）。
        /// 绕过 ScribeLoader.InitLoading 的文件中转，直接从 byte[] 解析 XML 并注入。
        /// </summary>
        private static void DeserializeCompData(ThingComp comp, byte[] xmlData)
        {
            if (comp == null || xmlData == null || xmlData.Length == 0)
                return;

            if (Scribe.mode != LoadSaveMode.Inactive)
            {
                Log.Error("[MapArchive] Cannot deserialize comp - Scribe is active (save/load in progress)");
                return;
            }

            try
            {
                PreserveOriginalScribeState();
                // 从内存解析 XML（跳过 InitLoading 的文件写→读）
                var doc = new XmlDocument();
                using (var ms = new MemoryStream(xmlData))
                {
                    doc.Load(ms);
                }

                var loader = new ScribeLoader();
                SetScribeLoader(loader);

                // 直接注入解析好的 XML（等效于 InitLoading 的 curXmlParent 赋值）
                // curXmlParent 就是 CompRoot，PostExposeData 直接从这里读子节点
                CurXmlParentField?.SetValue(loader, doc.DocumentElement);
                Scribe.mode = LoadSaveMode.LoadingVars;

                comp.PostExposeData();
                loader.FinalizeLoading();
            }
            catch (Exception ex)
            {
                Log.Warning(string.Format("[MapArchive] Failed to deserialize comp {0}: {1}",
                    comp.GetType().Name, ex.Message));
            }
            finally
            {
                if (Scribe.mode != LoadSaveMode.Inactive)
                    Scribe.ForceStop();
                RestoreOriginalScribeState();
            }
        }

        // ═══════════════════════════════════════════════
        //  Scribe 反射辅助（设置 Scribe.saver / Scribe.loader 静态字段）
        //  这是 Scribe 系统唯一的扩展入口，RimWorld 本身也通过此方式使用
        // ═══════════════════════════════════════════════

        private static void SetScribeSaver(ScribeSaver saver)
        {
            SaverField?.SetValue(null, saver);
        }

        private static void SetScribeLoader(ScribeLoader loader)
        {
            LoaderField?.SetValue(null, loader);
        }

        private static void PreserveOriginalScribeState()
        {
            originalScribeSaver ??= Scribe.saver;
            originalScribeLoader ??= Scribe.loader;
        }

        private static void RestoreOriginalScribeState()
        {
            SetScribeSaver(originalScribeSaver ?? new ScribeSaver());
            SetScribeLoader(originalScribeLoader ?? new ScribeLoader());
            originalScribeSaver = null;
            originalScribeLoader = null;
        }
    }

    /// <summary>
    /// 封存异步操作状态。由 SpaceGameComp 的 GameComponentTick 驱动。
    /// Phase 0: 收集建筑列表 + 压缩物 + 地形（1 tick）
    /// Phase 1: 分批序列化 Comp 数据（多 tick）
    /// Phase 2: 写入文件 + 销毁地图（1 tick）
    /// </summary>
    public class MapArchiveOperation
    {
        public SpaceEntity TargetEntity;
        public Map SourceMap;
        public byte[] CompressedData;
        public List<TerrainRecord> TerrainRecords;
        public List<BuildingRecord> BuildingRecords = new List<BuildingRecord>();
        public List<Building> BuildingsToProcess;
        public int CurrentBuildingIndex;
        public string TempFilePath;
        public string ArchiveFilePath;
        public int Phase; // 0=collect, 1=serialize comps, 2=write+destroy
        public const int BATCH_SIZE = 8;

        public float Progress
        {
            get
            {
                if (BuildingsToProcess == null || BuildingsToProcess.Count == 0)
                    return Phase >= 2 ? 1f : 0.3f;
                return 0.3f + 0.7f * (float)CurrentBuildingIndex / BuildingsToProcess.Count;
            }
        }
    }

    /// <summary>
    /// 取档异步操作状态。由 SpaceGameComp 的 GameComponentTick 驱动。
    /// </summary>
    public class MapRestoreOperation
    {
        public SpaceEntity TargetEntity;
        public Map NewMap;
        public List<BuildingRecord> BuildingRecords;
        public List<TerrainRecord> TerrainRecords;
        public byte[] CompressedData;
        public int MapWidth;
        public int MapHeight;
        public int CurrentBatchIndex;
        public int Phase;
        public const int BATCH_SIZE = 6;
        public string ArchiveFilePath;

        internal Dictionary<ushort, ThingDef> _hashToDef;
        internal Dictionary<int, Faction> _factionCache;

        public float Progress
        {
            get
            {
                if (BuildingRecords == null || BuildingRecords.Count == 0)
                    return Phase >= 1 ? 1f : 0.5f;
                return 0.5f + 0.5f * (float)CurrentBatchIndex / BuildingRecords.Count;
            }
        }
    }
}
