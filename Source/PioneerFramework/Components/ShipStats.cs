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
    }
}
