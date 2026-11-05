using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 舱室类型 Def。定义舱室的用途分类与默认属性。
    /// 舱室内部地图尺寸由部件物理尺寸决定（1 格 = 1 米），不由此 Def 控制。
    /// （Lite 裁剪：从 Ships3D 迁出，SpacePart/PFView 引用舱室类型）
    /// </summary>
    public class RoomTypeDef : Def
    {
        /// <summary>舱室内部地图使用的生成器 Def 名（默认统一使用 PF_RoomPocket：地板+边界墙）。</summary>
        public string mapGeneratorDefName = "PF_RoomPocket";
    }
}
