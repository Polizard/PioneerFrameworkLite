using System.Collections.Generic;
using System.IO;
using Verse;
using PioneerFramework.Combat;
using PioneerFramework.Components;
using UnityEngine;

namespace PioneerFramework
{
    /// <summary>
    /// 飞船装配数据。包含所有已放置的部件实例，
    /// 每个实例引用一个 ShipPartTemplate（按名称）。
    /// 二进制序列化 v3 格式。
    /// </summary>
    public class Ship3D
    {
        public string shipName = "未命名飞船";
        public List<ShipPartInstance> parts = new List<ShipPartInstance>();

        /// <summary>Unity 单位到物理米的换算：1 Unity unit = 10 米。</summary>
        public const double UNITY_TO_METERS = 10.0;

        private static int nextSpacePartId = 1;

        /// <summary>蓝图文件存储目录。</summary>
        public static string BlueprintsFolder =>
            Path.Combine(GenFilePaths.ConfigFolderPath, "ShipBlueprints");

        public static void EnsureBlueprintsFolder()
        {
            if (!Directory.Exists(BlueprintsFolder))
                Directory.CreateDirectory(BlueprintsFolder);
        }

        public static List<string> GetSavedBlueprints()
        {
            EnsureBlueprintsFolder();
            var result = new List<string>();
            foreach (var f in Directory.GetFiles(BlueprintsFolder, "*.ship"))
                result.Add(Path.GetFileNameWithoutExtension(f));
            return result;
        }

        /// <summary>
        /// 将装配的部件实例列表转换为运行时 SpacePart 列表。
        /// 坐标从 Unity 单位缩放到物理米。
        /// 模板组件数据深拷贝进运行时实例（设计层参数真实进入物理）。
        /// 武器部件会创建关联的 WeaponInstance。
        /// </summary>
        public List<SpacePart> ToSpaceParts()
        {
            var result = new List<SpacePart>(parts.Count);
            foreach (var sp in parts)
            {
                Vector3 effectiveBounds = sp.EffectiveBounds;
                var part = new SpacePart
                {
                    partId = nextSpacePartId++,
                    defName = ShipPartDefs.GetDefaultDefName(sp.ShapeType),
                    localPosition = new DVector3(
                        sp.position.x * UNITY_TO_METERS,
                        sp.position.y * UNITY_TO_METERS,
                        sp.position.z * UNITY_TO_METERS),
                    localBoundsSize = new DVector3(
                        effectiveBounds.x * UNITY_TO_METERS,
                        effectiveBounds.y * UNITY_TO_METERS,
                        effectiveBounds.z * UNITY_TO_METERS),
                    hp = sp.hp,
                };

                // 组件拷贝：模板快照 → 运行时实例（深拷贝）
                var tmplComps = sp.Components;
                if (tmplComps != null)
                {
                    for (int i = 0; i < tmplComps.Count; i++)
                    {
                        var cloned = tmplComps[i].Clone();
                        if (cloned != null)
                        {
                            part.components.Add(cloned);
                            cloned.OnAttached();
                        }
                    }
                }

                // 舱室尺寸：1格=1米，由组件承载（未配置 Room 组件则无舱室）
                var room = part.GetComponent<RoomComponentData>();
                if (room != null)
                {
                    room.mapWidth = Mathf.Max(8, Mathf.RoundToInt(effectiveBounds.x * (float)UNITY_TO_METERS));
                    room.mapHeight = Mathf.Max(8, Mathf.RoundToInt(effectiveBounds.z * (float)UNITY_TO_METERS));
                }

                part.ResolveReferences();
                part.InvalidateStats();
                if (part.GetComponent<WeaponMountComponentData>() != null)
                    part.CreateWeaponInstance();
                result.Add(part);
            }
            return result;
        }

        // ══════════════════════════════════════════════════
        //  二进制序列化（格式版本 v3）
        // ══════════════════════════════════════════════════

        private const byte FORMAT_VERSION = 3;

        public void WriteBinary(BinaryWriter w)
        {
            w.Write(FORMAT_VERSION);
            w.Write(shipName ?? "");
            w.Write(parts.Count);

            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                w.Write(p.templateName ?? "");     // string: 模板名
                w.Write(p.position.x);
                w.Write(p.position.y);
                w.Write(p.position.z);
                w.Write(p.rotation.x);
                w.Write(p.rotation.y);
                w.Write(p.rotation.z);
                w.Write(p.rotation.w);
                w.Write(p.hp);
                w.Write(p.customBounds.x);
                w.Write(p.customBounds.y);
                w.Write(p.customBounds.z);
            }
        }

        public static Ship3D ReadBinary(BinaryReader r)
        {
            // 版本检测：只支持当前版本（旧格式已弃用）
            byte version = r.ReadByte();
            if (version != FORMAT_VERSION)
            {
                Log.Warning("[Ship3D] 不支持的蓝图格式版本 " + version
                    + "（当前版本 " + FORMAT_VERSION + "，旧蓝图已弃用）。");
                return new Ship3D();
            }

            var ship = new Ship3D { shipName = r.ReadString() };
            int partCount = r.ReadInt32();

            for (int i = 0; i < partCount; i++)
            {
                string nameField = r.ReadString();
                var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                float hp = r.ReadSingle();
                var customBounds = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

                ship.parts.Add(new ShipPartInstance
                {
                    templateName = nameField,
                    position = pos,
                    rotation = rot,
                    hp = hp,
                    customBounds = customBounds
                });
            }

            return ship;
        }

        // ══════════════════════════════════════════════════
        //  命名保存/读取/删除
        // ══════════════════════════════════════════════════

        public void SaveToFile(string designName)
        {
            EnsureBlueprintsFolder();
            string path = Path.Combine(BlueprintsFolder, designName + ".ship");
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                WriteBinary(bw);
            }
        }

        public static Ship3D LoadFromFile(string designName)
        {
            EnsureBlueprintsFolder();
            string path = Path.Combine(BlueprintsFolder, designName + ".ship");
            if (!File.Exists(path))
                return null;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                return ReadBinary(br);
            }
        }

        public static void DeleteFile(string designName)
        {
            EnsureBlueprintsFolder();
            string path = Path.Combine(BlueprintsFolder, designName + ".ship");
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
