#if !PURE_MATH
using RimWorld.Planet;
#endif

namespace PioneerFramework
{
    public static class SpaceConstants
    {
        public const double MetersPerUnit = 50000.0;          // 1 Unity 单位 = 50 km
        public const double UnitsPerMeter = 1.0 / 50000.0;
        public const double CellSizeMeters = 1000000.0;        // 区块边长 1,000 km

        // ── 行星物理参数（可在运行时由 CelestialBodyManager 同步）──
        /// <summary>行星半径 (米)，默认 5000 km (= 100 tiles × 50 km)</summary>
        public static double PlanetRadiusMeters = 5000000.0;
        /// <summary>行星半径 (Unity 单位)</summary>
        public static double PlanetRadiusUnity => PlanetRadiusMeters * UnitsPerMeter;
        /// <summary>大气层厚度 (米)</summary>
        public static double AtmosphereThicknessMeters = 100000.0;
        /// <summary>大气层外缘距行星中心距离 (米)</summary>
        public static double AtmoOuterRadiusMeters => PlanetRadiusMeters + AtmosphereThicknessMeters;

        // ── 着陆/碰撞参数 ──
        /// <summary>最大安全着陆速度 (米/秒)，超过则视为坠毁</summary>
        public const double MaxSafeLandingSpeedMps = 10.0;

        // ── 派生常量（高性能场景下可直接引用） ──
        /// <summary>行星半径平方，用于距离比较优化</summary>
        public static double PlanetRadiusSqr => PlanetRadiusMeters * PlanetRadiusMeters;
        /// <summary>大气层外缘半径平方</summary>
        public static double AtmoOuterRadiusSqr => AtmoOuterRadiusMeters * AtmoOuterRadiusMeters;

        // ── 主天体引力参数 ──
        /// <summary>
        /// 主天体（地球）的引力参数 GM (m³/s²)。
        /// 硬编码默认值：距中心 5,000,000 m 处 g = 8 m/s² → GM = 8 × 5,000,000² = 2e14。
        /// CelestialBodyManager 初始化时若 XML 定义了天体，会覆盖此值。
        /// </summary>
        public static double PrimaryGravitationalParameter = 8.0 * (PlanetRadiusMeters * PlanetRadiusMeters);

#if !PURE_MATH
        /// <summary>
        /// 与 RimWorld 世界同步行星参数。
        /// RimWorld 的世界半径恒为 WorldGrid.PlanetRadius (100 tiles)，
        /// 因此行星物理半径 = 100 × MetersPerUnit = 5,000,000 m。
        /// 如果后续支持不同大小的世界，此处会自动适配。
        /// </summary>
        public static void SyncWithWorld()
        {
            // WorldGrid.PlanetRadius 是 RimWorld 的编译时常量 100f
            PlanetRadiusMeters = WorldGrid.PlanetRadius * MetersPerUnit;
        }
#endif
    }
}
