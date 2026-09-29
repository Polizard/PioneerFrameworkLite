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
        /// <summary>
        /// 最大安全着陆速度（米/秒）：**接地速度**超过它 = 坠毁，不超过 = 着陆
        /// （2026-09-22 用户裁定："着陆就是以一个安全的速度撞击天体"，暂取 100，
        ///  原话"有点荒谬但也是游戏性处理"）。
        ///
        /// ⚠ **口径 = 相对地表**（`SurfaceFrame.GroundSpeed`：已扣天体平动与地表共转），
        /// **不是惯性系总速度** —— 地球赤道地表共转 = ω·R = 523.6 m/s（ω=2π/60000、R=5000 km），
        /// 用惯性口径时"跟着地面一起垂直下降"也读 524 m/s ⇒ 阈值取多少都永远判坠毁。
        /// </summary>
        public const double MaxSafeLandingSpeedMps = 100.0;

        /// <summary>
        /// **"着陆还是坠毁"的唯一判据**（把 `CheckPlanetCollision` 里那句内联比较收成一件可测的纯函数）。
        /// ⚠ 入参是**接地速度**（`SurfaceFrame.GroundSpeed`：相对地表、已扣共转），**不是惯性系速度**
        /// —— 地球赤道共转 523.6 m/s，用惯性速度则任何着陆都判坠毁（见上一节的更正记录）。
        /// ⚠ **边界含在内**：`speed == 阈值` ⇒ 着陆（与 `CheckPlanetCollision` 原来的 `>` 一致）。
        /// </summary>
        public static bool IsSafeLandingSpeed(double groundSpeed) => groundSpeed <= MaxSafeLandingSpeedMps;

        // ── 选 tile 主动降落（2026-09-22 用户裁定："选一个 tile、消耗一定 dV、瞬间完成降落"）──
        /// <summary>落点距**星下点**的地表大圆距离上限（米）= 1000 km，与既有"附近"口径同量级。</summary>
        public const double MaxLandingSurfaceDistanceMeters = 1000000.0;
        /// <summary>
        /// 距离门随天体半径缩的比例：小天体必须用它，否则"1000 km"在半径 1500 km 的月球上
        /// 等于把大半个球面都算成"邻近"。实际门 = `min(绝对上限, 本值 × 天体半径)`，
        /// **规则的唯一实现在 `LandingQuote.MaxSurfaceDistance`**（地球 1000 km / 月球 525 km）。
        /// </summary>
        public const double MaxLandingDistanceRadiiFraction = 0.35;

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
        public static void SyncWithWorldGrid()
        {
            // WorldGrid.PlanetRadius 是 RimWorld 的编译时常量 100f
            PlanetRadiusMeters = WorldGrid.PlanetRadius * MetersPerUnit;
        }
#endif
    }
}
