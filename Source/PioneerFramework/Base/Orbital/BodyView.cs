using MechJebLib.Primitives;

namespace PioneerFramework
{
    /// <summary>
    /// 中心天体的**数据记录**（《P-1适配层决策》§3.2）。它不是服务——由 `CelestialBodyDef` 填一次就够。
    ///
    /// 与 MJ 的对应：MJ 的 `Orbit.referenceBody` 是一个能把 KSP `CelestialBody` 的所有东西都问出来的对象；
    /// 我们把它压成一张不会变的表。**需要"问"的只有三个**（P-1 §1.2(v)：地形、biome、`timeWarpAltitudeLimits`），
    /// 而它们都不在 15 个 Operation 的热路径上 ⇒ 真要时用委托，别把服务塞进记录。
    /// </summary>
    public readonly struct BodyView
    {
        public readonly int bodyId;

        /// <summary>引力常数 GM (m³/s²)。</summary>
        public readonly double mu;

        /// <summary>平均半径 (m)。</summary>
        public readonly double radius;

        /// <summary>大气层厚度 (m)。0 = 无大气。用于"缩轨受大气上界限制"这类判断（§24.2 的 ≈20°/圈）。</summary>
        public readonly double atmosphereThickness;

        /// <summary>
        /// **赤道轴**（自转轴 / 北极方向，单位矢量）。决定 `*Equatorial*` 族谓词。
        /// 直接进 `OrbitView.equatorialAxis`。
        /// </summary>
        public readonly V3 equatorialAxis;

        /// <summary>自转角速度 (rad/s)。经度/地表几何需要。</summary>
        public readonly double angularVelocity;

        /// <summary>自转在 `epoch` 时刻的转角 (rad)。</summary>
        public readonly double rotationAngleT0;

        /// <summary>父天体 id；<c>-1</c> = 根（绕恒星/系统中心）。</summary>
        public readonly int parentBodyId;

        /// <summary>
        /// ⚠ **SOI 半径是"按 Hill 半径近似"的，不是真值**（P-1 §6-R3）。
        /// 我们**没有** `sphereOfInfluence` 字段，也不需要它做参考系判定——
        /// 参考系是**显式跟踪的**（`CelestialBodyId` + `RegisterGroupToBody`）。
        /// 它只用于少数健全性界；**禁止用它做参考系判定**。
        /// </summary>
        public readonly double soiRadiusApprox;

        public BodyView(int bodyId, double mu, double radius, double atmosphereThickness,
            V3 equatorialAxis, double angularVelocity, double rotationAngleT0,
            int parentBodyId, double soiRadiusApprox)
        {
            this.bodyId = bodyId;
            this.mu = mu;
            this.radius = radius;
            this.atmosphereThickness = atmosphereThickness;
            this.equatorialAxis = equatorialAxis;
            this.angularVelocity = angularVelocity;
            this.rotationAngleT0 = rotationAngleT0;
            this.parentBodyId = parentBodyId;
            this.soiRadiusApprox = soiRadiusApprox;
        }

        /// <summary>大气上界半径 (m)。缩轨/圆化的硬约束（§24.2）。</summary>
        public double AtmoOuterRadius => radius + atmosphereThickness;
    }
}
