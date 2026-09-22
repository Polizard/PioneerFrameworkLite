using MechJebLib.Primitives;

namespace PioneerFramework
{
    /// <summary>
    /// 计划的**归属**（《MechJeb参考大修计划》§2.4 的队列/引导互斥规则）。
    ///
    /// 规矩：一条队列里同一时刻只允许一个权威。手动节点与引导不能各写各的。
    /// </summary>
    public enum PlanOwner
    {
        /// <summary>玩家（或我们的 UI）手动排的计划。引导不得覆盖它。</summary>
        Manual = 0,

        /// <summary>自动引导排的计划。手动节点存在时引导让位。</summary>
        Guidance = 1,

        /// <summary>**执行器自己的收口动作**（B2.2-b 闭环微调，`TrimBurn`）：既不是玩家排的，也不是引导排的。
        /// 两条归属规则里它都**不**触发（不夺引导、也不逼引导让位）—— 微调是主燃烧的收尾，不改变归属。
        /// （规则用的是"== Manual"的正向判定 ⇒ 新增档位**按构造**不会影响既有两条规则。）</summary>
        Executor = 2,
    }

    /// <summary>
    /// 一个机动节点的**输出形状**（《P-1适配层决策》§3.4）。
    ///
    /// 与 MJ 的对应：MJ 的 `ManeuverParameters { Vector3d dV; double UT; }`（`MechJeb2/Maneuver/Operation.cs:11-22`）。
    /// 两者的差别只有"多一个归属"——那是我们自己的队列语义需要的。
    ///
    /// ⚠ **向量类型是 `V3`**：与 `OrbitView` 同理（用户 2026-09-18 裁定"暂用 V3、日后换 DV3"）。
    /// 这样 `MakeNodesImpl` 返回 `List<PlanNode>` 时，函数体里构造 `new ManeuverParameters(x, y)` 的地方
    /// 只需把类型名换掉，算术一行不改。
    ///
    /// ⚠ **本类型只定"输出形状"，不定"执行语义"。** 队列条目的**活重解**（MJ 的节点语义）是 **P2**。
    /// </summary>
    public readonly struct PlanNode
    {
        /// <summary>Δv（**天体参考系**，与 MJ 的 `ManeuverParameters.dV` 同义）。</summary>
        public readonly V3 dV;

        /// <summary>施加强度为 <see cref="dV" /> 的绝对时刻。</summary>
        public readonly double ut;

        /// <summary>归属。默认 `Guidance`——自动规划器产出的东西天然是引导的。</summary>
        public readonly PlanOwner owner;

        /// <summary>
        /// **本机动预计的燃烧时长（s）**——B2.2 的"`t_b` 进计划"。
        /// `0` = 未声明（脉冲/未知）⇒ 执行层退回自己估（旧行为）。
        ///
        /// 为什么要它：执行器是**居中点火**（点火 = 节点 − 半程燃烧），
        /// 而"半程"过去由执行器自己估 ⇒ **同一件事两个权威** ✗；更要紧的是
        /// **可用对准时间 = 窗口 − 半程燃烧**（实测：60 s 窗口 − 46 s 半程 = 14 s ⇒ 只能转 70°，
        /// 却需要 177° ⇒ 带 107° 夹角点火）——这件事**在排队时就知道**，不该等到点火才告警。
        /// </summary>
        public readonly double burnSeconds;

        /// <summary>
        /// ★★ **本节点的参考轨道**（2026-09-21）——"这一发的 Δv 是在**哪条轨道上**算的"。
        /// 执行器由它推 `burnTargetV` = `参考轨道@ut 的速度 + Δv`。
        ///
        /// 为什么必须带上（两冲量转移的**第二发**）：
        /// `DeltaVToInterceptAtTime` 的解是**两发**（起飞 + 到达刹车），而执行器原来的靶口径是
        /// **点火前轨道@UT + Δv** —— 对第一发恰好成立（点火前的活轨道就是那条参考），
        /// 对第二发**不成立**（那一刻船在**转移轨道**上）。
        /// ⇒ 不带参考轨道，第二发的靶会算成"从**点火前**轨道推到到达时刻再加 Δv"，方向与幅值**双双是错的**。
        ///
        /// ⚠ 默认（<see cref="hasRefOrbit" /> = false）⇒ 退回旧口径 ⇒ 既有调用点行为**逐位不变**。
        /// ⚠ 字段名 `refOrbit` 而非 `orbit`：本结构里 `PlanNode` 是成员名，避免与类型名撞车。
        /// </summary>
        public readonly OrbitView refOrbit;
        public readonly bool hasRefOrbit;

        public PlanNode(V3 dV, double ut, PlanOwner owner = PlanOwner.Guidance, double burnSeconds = 0.0)
            : this(dV, ut, owner, burnSeconds, default, false) { }

        /// <summary>带**参考轨道**的构造（语义见 <see cref="refOrbit" />）。</summary>
        public PlanNode(V3 dV, double ut, PlanOwner owner, double burnSeconds, OrbitView refOrbit, bool hasRefOrbit)
        {
            this.dV = dV;
            this.ut = ut;
            this.owner = owner;
            this.burnSeconds = burnSeconds;
            this.refOrbit = refOrbit;
            this.hasRefOrbit = hasRefOrbit;
        }

        public override string ToString() =>
            $"PlanNode(ut={ut:F1}, |dV|={dV.magnitude:F2} m/s, dv=({dV.x:F1},{dV.y:F1},{dV.z:F1}), {owner})";
    }
}
