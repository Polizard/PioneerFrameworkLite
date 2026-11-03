using System.Collections.Generic;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// **设计规格里的一条 part 引用**（2026-10-17：普适零件目录 `ShipPartDef` 的消费者）。
    /// Part 不是地面实体，也不是口袋地图上的可视建筑，它以数据形式存在于 RocketDesignSpec 中，
    /// 施工/发射/入轨时由机制层消费。**零件定义本体是普适的 `ShipPartDef`，本类只是"选用哪一件、几件"**。
    /// </summary>
    public class DesignPartSpec : IExposable
    {
        /// <summary>Part 的 DefName（**普适目录** `ShipPartDef`；DMS:NF 型号也可填自己的 Def，但共用同一套零件词表）。</summary>
        public string defName = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        /// <summary>Part 类别（词表）：thruster / tank / stage / payload-mount / sensor / weapon…</summary>
        public string category = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        /// <summary>数量 = **轴向站数**（沿堆叠方向摆几次）。框架施工代表这些 Part 被一起建造。</summary>
        public int quantity = 1;

        /// <summary>
        /// **并联台数**（2026-10-29）：同一个轴向站上并排放几件（1 = 单件；2…7 = 绕主推轴成环）。
        /// 实际件数 = `quantity × parallelCount`；几何见 <see cref="ParallelLayout"/>。
        /// 默认 1 ⇒ 老存档/老设计逐位零变化。
        /// </summary>
        public int parallelCount = 1;

        /// <summary>安装位置描述。首发只做保存和透传，后续可用于自定义布局。</summary>
        public string installPosition = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        // ── B3：显式布局/姿态/形状/分级预留（全有默认值，旧设计零变化）──
        /// <summary>分级预留（只透传到 part；分级行为后置）。</summary>
        public int stageId;
        /// <summary>true = 用 <see cref="localPosition"/>/<see cref="localRotation"/>；false = 沿 −Z 堆叠。</summary>
        public bool useExplicitPose;
        /// <summary>显式体坐标（相对实体原点，米）。</summary>
        public DVector3 localPosition;
        /// <summary>显式船体系姿态（默认 identity）。</summary>
        public DQuaternion localRotation = DQuaternion.identity;
        /// <summary>挂接父条目下标（spec 顺序，−1 = 根；预留）。</summary>
        public int parentIndex = -1;
        /// <summary>true = 用 <see cref="shapeOverride"/> 覆盖目录形状。</summary>
        public bool useShapeOverride;
        public InertiaTensor.Shape shapeOverride = InertiaTensor.Shape.Box;

        public void ExposeData()
        {
            Scribe_Values.Look(ref defName, "defName");
            Scribe_Values.Look(ref category, "category");
            Scribe_Values.Look(ref quantity, "quantity", 1);
            Scribe_Values.Look(ref parallelCount, "parallelCount", 1);
            parallelCount = ParallelLayout.Clamp(parallelCount);
            Scribe_Values.Look(ref installPosition, "installPosition");
            Scribe_Values.Look(ref stageId, "stageId", 0);
            Scribe_Values.Look(ref useExplicitPose, "useExplicitPose", false);
            DVector3.Scribe("localPosition", ref localPosition);
            DQuaternion.Scribe("localRotation", ref localRotation);
            Scribe_Values.Look(ref parentIndex, "parentIndex", -1);
            Scribe_Values.Look(ref useShapeOverride, "useShapeOverride", false);
            Scribe_Values.Look(ref shapeOverride, "shapeOverride", InertiaTensor.Shape.Box);
        }
    }
}
