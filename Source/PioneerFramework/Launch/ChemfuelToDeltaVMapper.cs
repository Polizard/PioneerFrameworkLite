namespace PioneerFramework.Launch
{
    /// <summary>
    /// 化合燃料（Chemfuel）数量 → dV 的定量换算通道。
    /// 系数目前只是可配置占位，正式定额由 DMS:NF 确认后覆盖。
    /// </summary>
    public static class ChemfuelToDeltaVMapper
    {
        /// <summary>
        /// 每个 dV（m/s）需要多少单位化合燃料。
        /// 初稿：1 格 = 2 单位，仅供参考；正式指标待 DMS:NF 确定。
        /// </summary>
        public static float ChemfuelUnitsPerDeltaV = 2f;

        /// <summary>把化合燃料数量换算为 dV。</summary>
        public static float ToDeltaV(float chemfuelUnits)
        {
            if (chemfuelUnits <= 0f || ChemfuelUnitsPerDeltaV <= 0f)
                return 0f;
            return chemfuelUnits / ChemfuelUnitsPerDeltaV;
        }
    }
}
