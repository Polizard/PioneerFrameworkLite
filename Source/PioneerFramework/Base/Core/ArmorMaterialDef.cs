using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **装甲材料**（2026-10-22 部件功能化 §11.1）。
    ///
    /// 设计裁定（用户原话）：「我们的 part 不是实心的，所以密度对它没啥意义。我们说的甲就是外面一层。」
    /// ⇒ 装甲**只影响伤害计算**：不参与碰撞几何、不参与命中判定、**不参与质量**（质量与密度解耦）。
    ///
    /// 数据形状：材料只给"每毫米防护值"（无量纲），实例厚度在 `SpacePart.armorThicknessMm`：
    /// `装甲值 = 厚度(mm) × protectionPerMm`。伤害公式见 `ArmorMath.StructuralFactor`。
    /// 将来若要"加厚变重"，给 `armorMassKg` 一个**显式**字段——**不要**回头走密度（§11.1 原文）。
    /// </summary>
    public class ArmorMaterialDef : Def
    {
        /// <summary>每毫米厚度的防护值（无量纲，&gt;0）。装甲值 = 厚度(mm) × 本值。</summary>
        public float protectionPerMm = 1f;
    }
}
