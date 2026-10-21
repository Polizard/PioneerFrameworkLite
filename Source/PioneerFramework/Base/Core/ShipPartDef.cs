using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **普适零件目录**（2026-10-17，用户裁定："不能定义专门的火箭零件，而是应该定义普适的 part"）。
    ///
    /// 这是**所有船/站/实体**共用的零件定义，不是火箭专用：
    /// · 类别（`category`）只是**词表**（thruster / tank / stage / hull / cabin / sensor / weapon / misc…），
    ///   新增类别不需要新 Def 类型；
    /// · 几何（`bounds` / `stackLengthMeters`）+ 质量（`massKg`）是任何 part 都需要的；
    /// · 推进元数据（ve / ṁ / 容量）是**可选**的——推进器/储罐填，其他件留 0/空。
    ///
    /// 刻意**不是 ThingDef**：目录条目只被装配器读来派生 `SpacePart`，不需要建造/贴图/生成/进建造菜单。
    /// 运行时的功能（引擎/储罐/武器）仍由组件与实体/地图级聚合承载（M2 甲），不把功能语义塞进 Def。
    /// </summary>
    public class ShipPartDef : Def
    {
        /// <summary>零件类别（词表，不是类型）：thruster / tank / stage / hull / cabin / sensor / weapon / misc…</summary>
        public string category = "misc";

        /// <summary>结构质量（kg）。≤0 ⇒ 装配器按类别用兜底值。</summary>
        public float massKg = 100f;

        public float boundsX = 2f;
        public float boundsY = 2f;
        public float boundsZ = 2f;

        /// <summary>沿堆叠主轴的安装长度（m）。0 ⇒ 用 `boundsY`。</summary>
        public float stackLengthMeters;

        /// <summary>B3：形状预设（Box/Cylinder/Sphere）；只影响惯量/渲染/OBB，不影响质量口径。</summary>
        public InertiaTensor.Shape shape = InertiaTensor.Shape.Box;

        // ── 可选推进元数据（0/空 = 不适用于本零件）──
        public float exhaustVelocity;
        public float massFlowRate;
        public bool isVectoredRcs;
        public string propellantDefName = "";
        public float tankCapacityKg;
    }
}
