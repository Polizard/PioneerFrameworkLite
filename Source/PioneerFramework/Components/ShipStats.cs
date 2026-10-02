namespace PioneerFramework.Components
{
    /// <summary>
    /// 飞船聚合属性（数值流）。组件经 AccumulateStats 折算贡献，双级缓存聚合：
    /// 部件级 cachedStats + 实体级 shipStats。热路径只读最终 float。
    /// </summary>
    public struct ShipStats
    {
        public float mass;            // kg，结构 + 组件自重 + 载重
        public float thrust;          // N，Σ(ve × ṁ)
        public float powerGen;        // W
        public float powerDraw;       // W（满负荷标称功耗）
        public float heatProduction;  // W
        public float heatDissipation; // W
        public float propellant;      // kg（可消耗推进剂）
        public float powerStorage;    // J
        public float shieldCapacity;  // 点
        public float sensorRange;     // m
        public float cargoCapacity;   // kg

        // ── 电子战三分量的两个抗性槽（[N2]，2026-09-16 随 [V1] 步 2 加）──
        // 设计稿 §3.2 说"8 个空槽 = 量纲基础已躺好"，但那 8 个只覆盖灵敏度（sensorRange）与热/电基数；
        // **抗压制 / 抗欺骗没有对应槽** ⇒ 这是 ShipStats 唯一一次扩容。理由：三分量不能退化成一维比大小（W2）。
        // 两个都是**无量纲**（"抗性倍数"）：抗压制落在压制效力的分母上，抗欺骗与欺骗强度比大小。
        // ⚠ 消费者：[V7] 的传感器组件（`SensorArrayComponentData`）写，[V3]/[E6] 的认知层读。**本步尚无写入者**。
        public float ecmResist;       // 无量纲，抗压制（≥0）
        public float eccmResist;      // 无量纲，抗欺骗（≥0）

        // ── 主动雷达的两个槽（[V7]/[E2]，2026-10-01 随主动雷达建筑一起加）──
        // 部件级组件当初**刻意不声明**它们（"ShipStats 没有对应槽位，声明了没人读的空字段"）——现在有槽了、也有读者了。
        // 语义（都是**加法**，因为 ShipStats 的聚合语义就是求和）：
        //   · `activeGainBonus`：主动增益**加成** ⇒ 有效探测距离的倍率 = `1 + Σ`（0 = 无增益）；
        //   · `activeRadiation`：主动开机时的**信号增量**（S_REF 倍数）——加在 `SignalModel.ActivePingMultiplier` **基准之上**
        //     （基准覆盖"没装设备也能开机"的情形，设备把量级抬上去 ✓ W1：开机即更响，响多少由设备定）。
        public float activeGainBonus; // 无量纲加成（≥0）
        public float activeRadiation; // S_REF 倍数（≥0）

        // 观测者**记忆时长**（s，**物理秒轴**；2026-10-02 用户裁定 ②）：接触在"最后一次刷新"之后还能活多久
        // （[V3] 的记忆窗口 T）。**0 = 未设** ⇒ 用 `DetectionCore.DefaultMemorySeconds`（待定标）。
        // 落在这里的理由：它是**观测者**的属性（"我记多久"），不是目标的属性；且用户的
        // "信息处理速度（以后要改为非无穷）"将来就长在这一格上 —— 一处具名分量，不许散成传感器代码里的常数。
        public float memorySeconds;   // s（≥0；0 = 用默认）

        /// <summary>**干扰效力**（无量纲，≥0）：压制式干扰的强度（[E3]/S4）。
        /// 汇总进群级 `JamStrength`；作用方式见 `DetectionCore.JamEffort`（按"干扰机→观测方"距离平方反比衰减）。
        /// 0 = 无干扰能力。</summary>
        public float jamStrength;
    }
}
