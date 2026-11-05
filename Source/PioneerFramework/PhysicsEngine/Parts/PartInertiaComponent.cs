using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>
    /// **部件惯量形状**（批 0 底座，2026-10-17）：给"质量分布 → 自惯量"一个数据形状。
    ///
    /// - **质量不由本组件提供**：质量仍由统计组件（`HullMassComponent` / `RoomComponentData` …）给出，
    ///   与既有口径一致；本组件只决定"这份质量按什么形状摊开"。
    /// - 形状预设：长方体 / 圆柱（对称轴 = 本体系 **Z**）/ 球；尺寸来自 `dims`，
    ///   **为 0 的轴回落到 `part.boundsSizeShip`**。
    /// - **自定义张量**（6 分量，绕部件质心、体轴）——给非简单几何体；启用后忽略 shape/dims。
    /// - **无本组件 ⇒ 默认长方体（boundsSizeShip）**：单 part 船的读数因此 = 盒体解析值
    ///   （批 0 "零行为变化 + 读数可手算对照"的自证口径）。
    ///
    /// 序列化：Scribe（存档）+ Write/Read（`PartComponentData.Clone` 的 `.part` 二进制通道）。
    /// </summary>
    public class PartInertiaComponent : PartComponentData
    {
        /// <summary>形状预设（无自定义张量时生效）。</summary>
        public InertiaTensor.Shape shape = InertiaTensor.Shape.Box;

        /// <summary>形体尺寸（米；0 = 用 `part.boundsSizeShip`）。圆柱：X = 直径、Z = 高；球：X = 直径。</summary>
        public DVector3 dims;

        /// <summary>启用自定义张量（绕部件质心、体轴 6 分量）；为 true 时忽略 shape/dims。</summary>
        public bool useCustomTensor;

        public double cIxx, cIyy, cIzz, cIxy, cIxz, cIyz;

        static PartInertiaComponent()
        {
            ComponentRegistry.Register<PartInertiaComponent>();
        }

        public override PartRole Role => PartRole.Inertia;

        /// <summary>本组件**刻意不写** ShipStats（与 `IntakeComponentData` 的"刻意不进"同款）：质量由别的组件给。</summary>
        public override void AccumulateStats(ref ShipStats s, float efficiency) { }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref shape, "inertiaShape", InertiaTensor.Shape.Box);
            DVector3.Scribe("inertiaDims", ref dims);
            Scribe_Values.Look(ref useCustomTensor, "inertiaUseCustom", false);
            Scribe_Values.Look(ref cIxx, "inertiaIxx", 0.0);
            Scribe_Values.Look(ref cIyy, "inertiaIyy", 0.0);
            Scribe_Values.Look(ref cIzz, "inertiaIzz", 0.0);
            Scribe_Values.Look(ref cIxy, "inertiaIxy", 0.0);
            Scribe_Values.Look(ref cIxz, "inertiaIxz", 0.0);
            Scribe_Values.Look(ref cIyz, "inertiaIyz", 0.0);
        }

        public override void Write(BinaryWriter w)
        {
            w.Write((byte)shape);
            w.Write(dims.x);
            w.Write(dims.y);
            w.Write(dims.z);
            w.Write(useCustomTensor);
            w.Write(cIxx);
            w.Write(cIyy);
            w.Write(cIzz);
            w.Write(cIxy);
            w.Write(cIxz);
            w.Write(cIyz);
        }

        public override void Read(BinaryReader r)
        {
            shape = (InertiaTensor.Shape)r.ReadByte();
            dims = new DVector3(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
            useCustomTensor = r.ReadBoolean();
            cIxx = r.ReadDouble();
            cIyy = r.ReadDouble();
            cIzz = r.ReadDouble();
            cIxy = r.ReadDouble();
            cIxz = r.ReadDouble();
            cIyz = r.ReadDouble();
        }
    }
}
