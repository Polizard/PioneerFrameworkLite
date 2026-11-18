using System.Collections.Generic;
using RimWorld;
using PioneerFramework.Components;
using UnityEngine;

namespace PioneerFramework
{
    /// <summary>
    /// 飞船部件的装配实例（布局数据）。
    /// 通过 templateName 引用已保存的 ShipPartTemplate，
    /// 几何（形状/尺寸）从模板派生；功能参数由模板组件集承载。
    /// 支持可选的 customBounds 覆盖模板尺寸。
    /// </summary>
    public class ShipPartInstance
    {
        /// <summary>引用的部件模板名称（即 .part 文件名不含扩展名）。</summary>
        public string templateName;

        /// <summary>部件位置（相对飞船原点，Unity 单位）。</summary>
        public Vector3 position;

        /// <summary>部件朝向。</summary>
        public Quaternion rotation;

        /// <summary>当前耐久。</summary>
        public float hp = 100f;

        /// <summary>自定义尺寸覆盖（非零时覆盖模板 bounds）。</summary>
        public Vector3 customBounds = Vector3.zero;

        // ════════════════════════════════════════════
        //  运行时缓存（不参与序列化）
        // ════════════════════════════════════════════

        private ShipPartTemplate _cachedTemplate;
        private bool _templateResolved;

        /// <summary>解析并获取关联的模板。如果模板文件不存在，返回 null。</summary>
        public ShipPartTemplate ResolveTemplate()
        {
            if (!_templateResolved)
            {
                _cachedTemplate = ShipPartTemplate.LoadFromFile(templateName);
                _templateResolved = true;
            }
            return _cachedTemplate;
        }

        /// <summary>获取几何形状类型（从模板中读取）。</summary>
        public ShipPartShape ShapeType
        {
            get
            {
                var tmpl = ResolveTemplate();
                return tmpl != null ? tmpl.shapeType : ShipPartShape.Cube;
            }
        }

        /// <summary>获取实际尺寸：优先 customBounds，否则模板 bounds。</summary>
        public Vector3 EffectiveBounds
        {
            get
            {
                if (customBounds != Vector3.zero)
                    return customBounds;
                var tmpl = ResolveTemplate();
                return tmpl != null ? tmpl.bounds : Vector3.one;
            }
        }

        /// <summary>预览颜色（从模板获取）。</summary>
        public Color PreviewColor
        {
            get
            {
                var tmpl = ResolveTemplate();
                return tmpl != null ? tmpl.previewColor : Color.gray;
            }
        }

        /// <summary>
        /// 组件集（从模板获取；模板缺失时回退到同名 Def 预置组件的深拷贝，
        /// 保证引用 Def 名（如 PF_ShipPart_Engine）的部件不退化，且不共享 Def 预置数据）。
        /// </summary>
        public IReadOnlyList<PartComponentData> Components
        {
            get
            {
                var tmpl = ResolveTemplate();
                if (tmpl != null && tmpl.components != null)
                    return tmpl.components;

                // 兜底：templateName 即 Def 名时，深拷贝 Def 预置组件（避免污染全局预置数据）
                if (!_defComponentsResolved)
                {
                    _defComponentsResolved = true;
                    var def = ShipPartDefs.GetDef(templateName);
                    if (def != null && def.presetComponents != null)
                    {
                        var list = new List<PartComponentData>(def.presetComponents.Count);
                        for (int i = 0; i < def.presetComponents.Count; i++)
                        {
                            var cloned = def.presetComponents[i].Clone();
                            if (cloned != null)
                                list.Add(cloned);
                        }
                        _cachedDefComponents = list;
                    }
                }

                return _cachedDefComponents ?? s_emptyComponents;
            }
        }

        private IReadOnlyList<PartComponentData> _cachedDefComponents;
        private bool _defComponentsResolved;

        private static readonly List<PartComponentData> s_emptyComponents = new List<PartComponentData>();

        /// <summary>获取指定类型组件；不存在返回 null。</summary>
        public T GetComponent<T>() where T : PartComponentData
        {
            var comps = Components;
            for (int i = 0; i < comps.Count; i++)
            {
                if (comps[i] is T typed)
                    return typed;
            }
            return null;
        }
    }
}
