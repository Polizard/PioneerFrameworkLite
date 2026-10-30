using System;

namespace PioneerFramework.Components
{
    /// <summary>
    /// **角色 → 失效收口**的唯一表（2026-10-22 部件功能化 §7）。
    ///
    /// 病根（§7 原文）：失效语义原先是"每个组件自己写 `OnDestroyed`"，加新角色容易漏；
    /// 实际落地形态是 `SpaceEntity.MarkPartDestroyed` 里按**组件类型**写死（储罐漏光 / 引擎出局）。
    /// 现在改成**按角色查表**：实体侧只留"表里那一行做什么"，新增角色缺行 ⇒ **启动期**炸（`Validate`）。
    ///
    /// 表的语义：
    /// · `AggregationOnly` = **不需要额外出局动作**。损毁件的功能项本来就被
    ///   `SpaceEntity.RecalculateShipStats` 跳过（`if (p.isDestroyed) continue;`），质量仍在 ⇒
    ///   反应堆/散热器/货舱/传感器/干扰机/进气道/惯量的失效**已经**由聚合承担，不再重复写一遍。
    /// · 需要实体级副作用的只有两行：`Tank`（漏光：镜像+权威池+容量）与 `Thruster`（该台 ve/ṁ 出局）。
    ///
    /// 纪律：**不许把新逻辑塞进 `MarkPartDestroyed` 的 if-else 里**——要加就加一行；缺行会红。
    /// </summary>
    internal static class PartRoleTable
    {
        /// <summary>失效收口签名：实体 + 件 + **具体那个组件实例**（一件上可能挂多个同类组件，逐个处理）。</summary>
        internal delegate void FailureHandler(SpaceEntity entity, SpacePart part, PartComponentData component);

        private static readonly FailureHandler?[] rows;

        static PartRoleTable()
        {
            int count = 0;
            foreach (PartRole role in Enum.GetValues<PartRole>())
            {
                int v = (int)role;
                if (v >= count) count = v + 1;   // 允许显式值留洞 ⇒ 洞会在 Validate 里被抓住
            }
            rows = new FailureHandler?[count];

            rows[(int)PartRole.None] = AggregationOnly;          // 哨兵：未声明角色（注册进 ComponentRegistry 的组件不许留在这里）
            rows[(int)PartRole.Hull] = AggregationOnly;
            rows[(int)PartRole.Cabin] = AggregationOnly;         // 舱室毁 ⇒ 销毁该图：由 MarkPartDestroyed 按 linkedMapId 处理
            rows[(int)PartRole.Thruster] = static (entity, part, component) =>
                entity.DisableEnginePart(part, component as EngineComponentData);
            rows[(int)PartRole.Tank] = static (entity, part, component) =>
                entity.LeakTankPart(part, component as PropellantTankComponentData);
            rows[(int)PartRole.Sensor] = AggregationOnly;
            rows[(int)PartRole.Jammer] = AggregationOnly;
            rows[(int)PartRole.Intake] = AggregationOnly;
            rows[(int)PartRole.Inertia] = AggregationOnly;
            rows[(int)PartRole.Reactor] = AggregationOnly;
            rows[(int)PartRole.Radiator] = AggregationOnly;
            rows[(int)PartRole.Cargo] = AggregationOnly;
            rows[(int)PartRole.Weapon] = AggregationOnly;
            rows[(int)PartRole.Battery] = AggregationOnly;
            // 控制核心：**唯一一个"功能出局之外还有实体级后果"的新角色** —— 失控 + 关键件失能
            rows[(int)PartRole.Control] = static (entity, part, component) => entity.HandleControlCoreLost();
            rows[(int)PartRole.Payload] = AggregationOnly;
        }

        /// <summary>聚合出局（无额外出局动作）——见类头注：功能项已由 `RecalculateShipStats` 跳过损毁件承担。</summary>
        private static void AggregationOnly(SpaceEntity entity, SpacePart part, PartComponentData component) { }

        /// <summary>对一件 part 上的**每个组件**按角色收口（损毁时调一次）。</summary>
        internal static void ApplyFailure(SpaceEntity entity, SpacePart part)
        {
            var components = part.components;
            for (int i = 0; i < components.Count; i++)
            {
                var component = components[i];
                if (component == null) continue;
                int role = (int)component.Role;
                if (role < 0 || role >= rows.Length || rows[role] == null)
                    throw new InvalidOperationException(
                        $"[PartRole] 组件 {component.TypeId} 的角色 {(PartRole)role} 在表里没有收口行");
                rows[role]!(entity, part, component);
            }
        }

        /// <summary>
        /// **启动期自检**（`PioneerFrameworkLiteMod` 构造时调一次）：缺行、注册组件没声明角色 ⇒ 抛异常。
        /// 走的是"宁可开不了游戏，也不要运行时静默半失效"的口径（§7）。
        /// </summary>
        internal static void Validate()
        {
            for (int i = 0; i < rows.Length; i++)
                if (rows[i] == null)
                    throw new InvalidOperationException(
                        $"[PartRole] 角色 {(PartRole)i} 在 PartRoleTable 里没有收口行（§7：缺行必须在启动期报错）");

            ComponentRegistry.ValidateRoles();
        }
    }
}
