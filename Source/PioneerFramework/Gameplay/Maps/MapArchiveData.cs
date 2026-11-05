using System.Collections.Generic;
using System.IO;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 封存建筑的完整数据记录（非压缩物）。
    /// </summary>
    public class BuildingRecord
    {
        public ushort defShortHash;
        public short posX;
        public short posZ;
        public byte rotation;
        public ushort stuffShortHash; // 0 = 无 stuff
        public int factionId;
        public int hitPoints;
        public List<CompRecord> comps = new();
    }

    /// <summary>
    /// 单个 ThingComp 的序列化数据。
    /// </summary>
    public class CompRecord
    {
        public string compTypeName = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        public byte[] xmlData = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
    }

    /// <summary>
    /// 地形格子数据。
    /// </summary>
    public class TerrainRecord
    {
        public short x;
        public short z;
        public string terrainDefName = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
    }

    /// <summary>
    /// .pfmap 文件的格式常量与序列化/反序列化。
    /// 文件结构：
    ///   [4B] Magic "PFMA" (0x414D4650)
    ///   [4B] Version
    ///   [4B] MapWidth  [4B] MapHeight
    ///   [4B] CompressedDataLen  [N] CompressedData     (ushort[]，每格2B)
    ///   [4B] NonCompressedCount
    ///     [BuildingRecord...]
    ///   [4B] TerrainCount
    ///     [TerrainRecord...]
    /// </summary>
    public static class MapArchiveFormat
    {
        public const uint MAGIC = 0x414D4650; // "PFMA" little-endian
        public const int VERSION = 1;

        // ── 写 ──

        public static void Write(string filePath, int mapWidth, int mapHeight,
            byte[] compressedData, List<BuildingRecord> buildings, List<TerrainRecord> terrains)
        {
            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs))
            {
                // Header
                bw.Write(MAGIC);
                bw.Write(VERSION);
                bw.Write(mapWidth);
                bw.Write(mapHeight);

                // Compressed data
                bw.Write(compressedData.Length);
                bw.Write(compressedData);

                // Non-compressed buildings
                bw.Write(buildings.Count);
                foreach (var b in buildings)
                    WriteBuildingRecord(bw, b);

                // Terrain
                bw.Write(terrains.Count);
                foreach (var t in terrains)
                    WriteTerrainRecord(bw, t);
            }
        }

        private static void WriteBuildingRecord(BinaryWriter bw, BuildingRecord b)
        {
            bw.Write(b.defShortHash);
            bw.Write(b.posX);
            bw.Write(b.posZ);
            bw.Write(b.rotation);
            bw.Write(b.stuffShortHash);
            bw.Write(b.factionId);
            bw.Write(b.hitPoints);
            bw.Write((byte)b.comps.Count);
            foreach (var c in b.comps)
            {
                bw.Write(c.compTypeName);
                bw.Write(c.xmlData.Length);
                bw.Write(c.xmlData);
            }
        }

        private static void WriteTerrainRecord(BinaryWriter bw, TerrainRecord t)
        {
            bw.Write(t.x);
            bw.Write(t.z);
            bw.Write(t.terrainDefName);
        }

        // ── 读 ──

        public static bool TryRead(string filePath,
            out int mapWidth, out int mapHeight,
            out byte[] compressedData, out List<BuildingRecord> buildings, out List<TerrainRecord> terrains)
        {
            mapWidth = 0;
            mapHeight = 0;
            compressedData = null;
            buildings = null;
            terrains = null;

            if (!File.Exists(filePath))
                return false;

            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var br = new BinaryReader(fs))
                {
                    uint magic = br.ReadUInt32();
                    if (magic != MAGIC)
                    {
                        Log.Error("[MapArchive] Invalid magic number in " + filePath);
                        return false;
                    }

                    int version = br.ReadInt32();
                    if (version > VERSION)
                    {
                        Log.Error($"[MapArchive] Unsupported version {version} in {filePath}");
                        return false;
                    }

                    mapWidth = br.ReadInt32();
                    mapHeight = br.ReadInt32();

                    // Compressed data
                    int compressedLen = br.ReadInt32();
                    compressedData = br.ReadBytes(compressedLen);

                    // Non-compressed buildings
                    int buildingCount = br.ReadInt32();
                    buildings = new List<BuildingRecord>(buildingCount);
                    for (int i = 0; i < buildingCount; i++)
                        buildings.Add(ReadBuildingRecord(br));

                    // Terrain
                    int terrainCount = br.ReadInt32();
                    terrains = new List<TerrainRecord>(terrainCount);
                    for (int i = 0; i < terrainCount; i++)
                        terrains.Add(ReadTerrainRecord(br));
                }
                return true;
            }
            catch (System.Exception ex)
            {
                Log.Error($"[MapArchive] Failed to read {filePath}: {ex.Message}");
                return false;
            }
        }

        private static BuildingRecord ReadBuildingRecord(BinaryReader br)
        {
            var b = new BuildingRecord
            {
                defShortHash = br.ReadUInt16(),
                posX = br.ReadInt16(),
                posZ = br.ReadInt16(),
                rotation = br.ReadByte(),
                stuffShortHash = br.ReadUInt16(),
                factionId = br.ReadInt32(),
                hitPoints = br.ReadInt32()
            };

            byte compCount = br.ReadByte();
            for (int i = 0; i < compCount; i++)
            {
                var c = new CompRecord
                {
                    compTypeName = br.ReadString(),
                };
                int dataLen = br.ReadInt32();
                c.xmlData = br.ReadBytes(dataLen);
                b.comps.Add(c);
            }

            return b;
        }

        private static TerrainRecord ReadTerrainRecord(BinaryReader br)
        {
            return new TerrainRecord
            {
                x = br.ReadInt16(),
                z = br.ReadInt16(),
                terrainDefName = br.ReadString()
            };
        }
    }
}
