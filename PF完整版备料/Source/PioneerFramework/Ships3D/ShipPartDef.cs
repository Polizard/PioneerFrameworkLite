using System.Collections.Generic;
using UnityEngine;
using Verse;
using PioneerFramework.Components;

namespace PioneerFramework
{
    /// <summary>
    /// 飞船部件定义（内容层原型）：纯几何 + 通用属性 + 预置组件集。
    /// 功能（引擎/储罐/货舱/武器/舱室）一律由组件表达，几何与功能彻底分离。
    /// </summary>
    public class ShipPartDef : Def
    {
        /// <summary>几何形状（纯几何，不再兼任功能标记）。</summary>
        public ShipPartShape shapeType;

        public Vector3 bounds;

        /// <summary>结构基础质量 kg（不含组件）。</summary>
        public float massBase = 1f;

        public Color previewColor = Color.gray;

        /// <summary>预置组件集（内容层默认）。运行时实例 = Def 预置 + 模板覆盖。</summary>
        public List<PartComponentData> presetComponents;
    }

    /// <summary>纯几何形状。功能角色由部件组件（PartComponentData）表达。</summary>
    public enum ShipPartShape
    {
        Cube,
        SphericalCorner
    }
}
