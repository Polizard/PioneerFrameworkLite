using System.Collections.Generic;
using System.IO;
using RimWorld;
using Verse;
using UnityEngine;
using PioneerFramework.Components;

namespace PioneerFramework
{
    /// <summary>
    /// 用户设计的部件模板。命名后以二进制 .part 文件储存。
    /// 包含几何（形状/尺寸/颜色）与组件集（引擎/工质罐/货舱/武器/舱室等）。
    /// 组件覆盖语义：Def 预置 = 新建模板初始值，模板固化后增删/调参为模板自身行为。
    /// </summary>
    public class ShipPartTemplate
    {
        public string templateName = "未命名部件";
        public ShipPartShape shapeType;
        public Vector3 bounds = Vector3.one;

        /// <summary>组件集（用户调整后的快照）。</summary>
        public List<PartComponentData> components = new List<PartComponentData>();

        /// <summary>预览颜色。</summary>
        public Color previewColor = Color.gray;

        // ══════════════════════════════════════════════════
        //  静态：文件管理
        // ══════════════════════════════════════════════════

        public static string TemplatesFolder =>
            Path.Combine(GenFilePaths.ConfigFolderPath, "ShipPartTemplates");

        public static void EnsureFolder()
        {
            if (!Directory.Exists(TemplatesFolder))
                Directory.CreateDirectory(TemplatesFolder);
        }

        /// <summary>
        /// 获取所有已保存的模板文件名（不含扩展名）。
        /// </summary>
        /// <param name="filterShape">可选几何形状过滤，null 返回全部。</param>
        public static List<string> GetSavedTemplates(ShipPartShape? filterShape = null)
        {
            EnsureFolder();
            var result = new List<string>();
            foreach (var f in Directory.GetFiles(TemplatesFolder, "*.part"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (filterShape == null)
                {
                    result.Add(name);
                }
                else
                {
                    // 快速检查：读文件头判断形状类型
                    var tmpl = LoadFromFile(name);
                    if (tmpl != null && tmpl.shapeType == filterShape.Value)
                        result.Add(name);
                }
            }
            return result;
        }

        /// <summary>获取指定形状类型的模板列表（高效版：先全量读取再过滤）。</summary>
        public static List<ShipPartTemplate> GetTemplatesByShape(ShipPartShape shape)
        {
            EnsureFolder();
            var result = new List<ShipPartTemplate>();
            foreach (var f in Directory.GetFiles(TemplatesFolder, "*.part"))
            {
                var tmpl = LoadFromFile(Path.GetFileNameWithoutExtension(f));
                if (tmpl != null && tmpl.shapeType == shape)
                    result.Add(tmpl);
            }
            return result;
        }

        // ══════════════════════════════════════════════════
        //  二进制序列化（格式版本 v4，组件化）
        // ══════════════════════════════════════════════════

        private const byte FORMAT_VERSION = 4;

        public void WriteBinary(BinaryWriter w)
        {
            w.Write(FORMAT_VERSION);
            w.Write(templateName ?? "");
            w.Write((byte)shapeType);
            w.Write(bounds.x);
            w.Write(bounds.y);
            w.Write(bounds.z);
            w.Write(previewColor.r);
            w.Write(previewColor.g);
            w.Write(previewColor.b);
            w.Write(previewColor.a);

            // 组件集：数量 + 每组件 (TypeId + 数据)
            int count = components != null ? components.Count : 0;
            w.Write(count);
            for (int i = 0; i < count; i++)
            {
                var comp = components[i];
                w.Write(comp.TypeId);
                comp.Write(w);
            }
        }

        public static ShipPartTemplate ReadBinary(BinaryReader r)
        {
            byte version = r.ReadByte();
            if (version != FORMAT_VERSION)
            {
                Log.Warning("[ShipPartTemplate] 不支持的模板格式版本 " + version
                    + "（当前版本 " + FORMAT_VERSION + "，旧模板已弃用）。");
                return null;
            }

            var tmpl = new ShipPartTemplate
            {
                templateName = r.ReadString(),
                shapeType = (ShipPartShape)r.ReadByte(),
                bounds = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                previewColor = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle())
            };

            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string typeId = r.ReadString();
                var comp = ComponentRegistry.Create(typeId);
                if (comp == null)
                {
                    Log.Warning("[ShipPartTemplate] 未注册组件 TypeId '" + typeId + "'，跳过。");
                    continue;
                }
                comp.Read(r);
                tmpl.components.Add(comp);
            }

            return tmpl;
        }

        // ══════════════════════════════════════════════════
        //  命名保存/读取/删除
        // ══════════════════════════════════════════════════

        public void SaveToFile(string designName)
        {
            EnsureFolder();
            templateName = designName;
            string path = Path.Combine(TemplatesFolder, designName + ".part");
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                WriteBinary(bw);
            }
        }

        public static ShipPartTemplate LoadFromFile(string designName)
        {
            EnsureFolder();
            string path = Path.Combine(TemplatesFolder, designName + ".part");
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
            EnsureFolder();
            string path = Path.Combine(TemplatesFolder, designName + ".part");
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
