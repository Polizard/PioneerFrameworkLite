namespace PioneerFramework.Components
{
    /// <summary>
    /// **部件角色词表**（2026-10-22 部件功能化 §7：统一失效语义）。
    ///
    /// 角色 ≠ 组件类型：它是"这件部件在船上是干什么的"，失效收口只认角色（见 `PartRoleTable`）。
    /// 新增一个角色 = ① 这里加一行；② `PartRoleTable` 加一行**收口**；③ 至少一个组件在 `Role` 里声明它。
    /// 少任何一步都在**启动期**炸（`PartRoleTable.Validate` / `ComponentRegistry.Register`），不许运行时静默。
    ///
    /// ⚠ 加角色会改枚举值次序：**不要把新角色插在中间**（存档/模板里若落了 `(int)role` 会错位）。
    /// 当前只用于运行时分派，没有落盘的 `(int)role` —— 但仍按"只在末尾追加"的纪律写。
    /// </summary>
    public enum PartRole
    {
        /// <summary>未声明角色（哨兵）。注册进 `ComponentRegistry` 的组件**不许**留在这里。</summary>
        None = 0,

        /// <summary>结构件（只贡献质量/外形，无运转功能）。</summary>
        Hull,

        /// <summary>舱室（承载一张 Map；损毁 ⇒ 销毁该图，见 `MarkPartDestroyed`）。</summary>
        Cabin,

        /// <summary>推进器（损毁 ⇒ 该台 ve/ṁ 从推进聚合出局）。</summary>
        Thruster,

        /// <summary>储罐（损毁 ⇒ 漏光：镜像清零 + 权威池扣减 + 容量出局）。</summary>
        Tank,

        /// <summary>传感器阵列（损毁 ⇒ 灵敏度/抗性从聚合出局）。</summary>
        Sensor,

        /// <summary>干扰机（损毁 ⇒ 干扰效力出局）。</summary>
        Jammer,

        /// <summary>进气道（损毁 ⇒ 该件不再进气）。</summary>
        Intake,

        /// <summary>惯量贡献件（损毁后质量仍在 ⇒ 惯量不变；只登记，无额外出局）。</summary>
        Inertia,

        /// <summary>反应堆（损毁 ⇒ `powerGen`/`heatProduction` 从聚合出局）。</summary>
        Reactor,

        /// <summary>散热器（损毁 ⇒ `heatDissipation` 从聚合出局）。</summary>
        Radiator,

        /// <summary>货舱（损毁 ⇒ `cargoCapacity` 从聚合出局）。</summary>
        Cargo,

        /// <summary>武器挂点（损毁 ⇒ 挂点失效；开火仍走 Map 炮塔桥接，禁止在 part 层长第二套开火逻辑，§6）。</summary>
        Weapon,

        /// <summary>电池（损毁 ⇒ 容量/充放上限从聚合出局；群级存量按新容量夹回）。</summary>
        Battery,

        /// <summary>控制核心（损毁 ⇒ 该舰**永久失控**：不能点火/操舵；没装过的舰不受影响）。</summary>
        Control,

        /// <summary>载荷舱（损毁 ⇒ 载荷放不出来；注册表条目保留）。</summary>
        Payload,
    }
}
