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
    }
}
