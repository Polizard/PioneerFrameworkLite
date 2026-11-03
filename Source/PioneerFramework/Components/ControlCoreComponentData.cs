using System.IO;

namespace PioneerFramework.Components
{
    /// <summary>
    /// **控制核心**（2026-10-26；用户口径："坎巴拉里那种"命令舱/探测器核心）。
    ///
    /// 语义（v1，刻意最小）：
    /// · **控制权来源**：舰上有未损毁的控制核心 ⇒ 可以点火/操舵；**装过**控制核心的舰一旦失去它 ⇒
    ///   永久失控（`RequiresControlCore` 粘住，不因残骸消失而"复活"）；
    /// · **没装过控制核心的舰不受影响**（EVA/导弹/无人机/调试船）——这是"不砸既有链"的唯一保证；
    /// · 本组件**没有数据字段**：v1 不区分载人/无人，也不耗电（KSP 的探测器核心要 EC；我们等载人/无人
    ///   分开时再加 `powerDrawWatts`，不先占一个没人读的槽）。
    ///
    /// 失效：走 `PartRole.Control` 的角色表 → 实体侧 `HandleControlCoreLost()`（置失控 + 走关键件失能）。
    /// </summary>
    public class ControlCoreComponentData : PartComponentData
    {
        static ControlCoreComponentData()
        {
            ComponentRegistry.Register<ControlCoreComponentData>();
        }

        public override PartRole Role => PartRole.Control;

        public override void ExposeData() { }
        public override void Write(BinaryWriter w) { }
        public override void Read(BinaryReader r) { }
    }
}
