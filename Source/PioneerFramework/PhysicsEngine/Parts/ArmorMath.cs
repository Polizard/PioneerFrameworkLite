namespace PioneerFramework
{
    /// <summary>
    /// **装甲伤害公式**（2026-10-22 部件功能化 §11.2，纯函数）。
    ///
    /// 两个通道（设计稿原文）：
    /// · **结构伤害**：决定 `hp`；装甲按"穿透 vs 装甲"缩放 ——
    ///   `结构伤害 = dmg × clamp(pen / (pen + armor), 0.05, 1)`；
    /// · **维护伤害**：决定 `condition`；**装甲对它作用有限**（默认不吃装甲，代表震动/烧蚀/过载），
    ///   `维护伤害 = dmg × k_wear`，`k_wear = 0.3`。否则重甲件永远不会磨损（§11.2 的坑）。
    ///
    /// 三条不变量（测试钉住）：
    /// ① **无装甲 ⇒ 系数恰为 1**（`dmg × 1.0f` 精确 ⇒ 现有读数/判据逐位不变）；
    /// ② 系数对穿透**单调不减**、对装甲**单调不增**；
    /// ③ 下界 0.05：再厚的甲也挡不住一点（否则小口径武器变成"完全无效"）。
    ///
    /// 为什么是纯函数：命中事件里只读，不参与碰撞/命中/质量，也不进 `ShipStats` 求和
    /// （不是求和量 ⇒ 不碰 §3 的求和顺序红线）。
    /// </summary>
    public static class ArmorMath
    {
        /// <summary>结构伤害系数下界（§11.2）。</summary>
        public const float MinStructuralFactor = 0.05f;

        /// <summary>`k_wear`（§11.2 默认 0.3，不随装甲变化）。</summary>
        public const float WearFactor = 0.3f;

        /// <summary>
        /// 维护伤害折算：`condition` 是 0–1、`damage` 是 hp 点（0–100 口径）
        /// ⇒ 磨损量 = `damage × k_wear / MaxHp` = `damage × 0.003`。
        /// 例：15 点的命中 ⇒ 每次 0.045 维护值（约 22 次打光）。
        /// </summary>
        public const float WearPerHpPoint = WearFactor / SpacePart.MaxHp;

        /// <summary>
        /// **结构伤害系数**。`armorValue ≤ 0` ⇒ 恰 1（无装甲，旧行为逐位不变）。
        /// `penetration ≤ 0` 且 `armorValue > 0` ⇒ 下界 0.05（挡不住也蹭掉一点）。
        /// </summary>
        public static float StructuralFactor(float penetration, float armorValue)
        {
            if (!(armorValue > 0f)) return 1f;
            float pen = penetration > 0f ? penetration : 0f;
            float f = pen / (pen + armorValue);
            if (!(f > MinStructuralFactor)) return MinStructuralFactor;   // 含 NaN
            return f > 1f ? 1f : f;
        }

        /// <summary>维护伤害（condition 单位）：`damage × k_wear / MaxHp`。</summary>
        public static float WearAmount(float damage)
            => damage > 0f ? damage * WearPerHpPoint : 0f;
    }
}
