using System;

namespace PioneerFramework
{
    /// <summary>
    /// **分离解算的"半体"累加器**（2026-10-30，纯核）。质量口径由调用方给（= 结构质量 + 罐内燃料），
    /// 位置是**体轴系、相对当前组原点**的部件质心位置。
    /// 累加用 `Σm` 与 `Σm·p`（而不是逐件质心）⇒ 数值更稳、一次遍历即可。
    /// </summary>
    public struct SeparationMass
    {
        /// <summary>总质量（kg）。</summary>
        public double mass;

        /// <summary>Σ m·p（kg·m，体轴系、相对当前组原点）。</summary>
        public DVector3 moment;

        /// <summary>质量加权质心（体轴系、相对当前组原点）；质量 ≤ 0 ⇒ 零向量。</summary>
        public DVector3 Center => mass > 0.0 ? moment / mass : DVector3.zero;

        public void Reset()
        {
            mass = 0.0;
            moment = DVector3.zero;
        }
    }

    /// <summary>分离解算结果（纯数据）。世界系 = 系统系（本核只做平移/旋转，不做天体换算）。</summary>
    public struct SeparationSolution
    {
        /// <summary>解算是否成立；false 时 <see cref="reason"/> 给机器可读原因。</summary>
        public bool ok;

        /// <summary>失败原因（`zero-mass` / `coincident-centers` / `non-finite` / 空串 = 成功）。</summary>
        public string reason;

        public double massA, massB;

        /// <summary>两半质心（体轴、相对**分离前**的组原点）。</summary>
        public DVector3 centerA, centerB;

        /// <summary>分离方向（体轴单位向量，A→B；= normalize(c_B − c_A)，与级号/结构轴都脱钩）。</summary>
        public DVector3 directionBody;

        /// <summary>冲量大小（N·s）；`relativeSpeed = 0` 时为 0。</summary>
        public double impulseJ;

        /// <summary>实际施加的相对分离速度（m/s，沿方向）。</summary>
        public double relativeSpeed;

        /// <summary>重定后的两个组原点与速度（世界系；原点 = 各自质心）。</summary>
        public DVector3 originA, velocityA, originB, velocityB;
    }

    /// <summary>
    /// **抛级分离的刚体解算（纯核）**（2026-10-30 用户方案："两组原点都取质心，速度按分离时的
    /// 角速度/速度与两半质心位置严格算"）。
    ///
    /// 模型：分离**瞬间**整船是一个刚体 —— 原点 `P` 平动 `V`、刚体角速度 `ω`（**世界系**；本仓
    /// `SpaceEntityGroup.angularVelocity` 就是世界系）、姿态 `R`（体→世界）。于是任一点 `x` 的速度
    /// `v(x) = V + ω × (x − P)`。把船分成 A/B 两半、各自重定到自己的质心：
    ///
    /// ```
    /// r_i   = R·c_i                                  （c_i = 该半质心，体轴、相对旧原点）
    /// P_i   = P + r_i ;  V_i = V + ω × r_i           （新原点 = 该半质心，速度 = 该点的刚体速度）
    /// ω_i   = ω                                      （刚性分裂，两边瞬时角速度相同）
    /// ```
    ///
    /// **守恒（可证，注意口径）**：`V` 是**原点那一点**的速度 ⇒ 整船动量 = `m·(V + ω×(R·c_whole))`
    /// （`c_whole` = 整船质心相对**当前原点**；只有当原点恰好是质心时才等于 `m·V`）。
    /// 分裂后 `Σ m_i V_i = mV + ω×(R·Σ m_i c_i) = m(V + ω×(R·c_whole))` —— 与分裂前**同值**。
    /// 角动量 `Σ[I_i ω + m_i r_i×V_i] = (I_A+I_B)ω + 平行轴项 = I_whole ω`（绕整船质心）。
    /// ⇒ **原点在不在质心都不影响正确性**；C2（装配即质心原点）只是让"存的速度 = 质心速度"这个口径统一。
    ///
    /// **推开（可选）**：沿 `u = normalize(c_B − c_A)`（**几何方向**，不是级号/−Z）施加一对等大反向的**中心冲量**
    /// `±J`：`J = v_rel / (1/m_A + 1/m_B)` ⇒ `Δv_A = −J/m_A`、`Δv_B = +J/m_B`。
    /// 因为 `J ∥ (c_B − c_A)`，这一对是**中心冲量** ⇒ 相对质心无力矩 ⇒ 两边角速度不变，
    /// 且总动量/总角动量精确守恒（`ΔL = (r_A − r_B) × J = 0`）。
    /// 没有这一项时，`ω ≈ 0` 的火箭两半**相对速度为 0**（仍然贴着飞）——这就是"物理上必须给分离机构一个冲量"。
    ///
    /// 本核只做算术，不碰任何 `Verse`/实体/存档；测试直接构造部件质量/位置即可。
    /// </summary>
    public static class StageSeparationCore
    {
        /// <summary>两半质心距离的下限（m）：小于它就说明两半几何重合/穿插 ⇒ 不是合法的刚体分裂。</summary>
        public const double MinCenterDistanceMeters = 1e-6;

        /// <summary>把一件累加进某一半（质量 ≤ 0 / 非有限值直接忽略；位置是体轴系、相对当前组原点）。</summary>
        public static void Add(ref SeparationMass body, double mass, in DVector3 bodyPosition)
        {
            if (!(mass > 0.0) || !IsFinite(mass)) return;
            if (!IsFinite(bodyPosition)) return;
            body.mass += mass;
            body.moment += bodyPosition * mass;
        }

        /// <summary>
        /// 解算一次分离。`relativeSpeed ≤ 0` ⇒ 只做刚体分裂（不推开），返回值里 `impulseJ = 0`。
        /// </summary>
        /// <param name="positionOrigin">分离前组原点（世界系）。</param>
        /// <param name="velocityOrigin">分离前组原点速度（世界系）——**是本核约定的"原点那一点的速度"**。</param>
        /// <param name="omegaSystem">刚体角速度（世界系，rad/s）。</param>
        /// <param name="rotation">姿态（体→世界）。</param>
        /// <param name="relativeSpeed">期望相对分离速度（m/s，沿 `c_B − c_A` 方向推开；0 = 不推开）。</param>
        public static bool Solve(in SeparationMass a, in SeparationMass b,
            in DVector3 positionOrigin, in DVector3 velocityOrigin,
            in DVector3 omegaSystem, in DQuaternion rotation,
            double relativeSpeed,
            out SeparationSolution solution)
        {
            solution = default;
            solution.reason = string.Empty;

            if (!(a.mass > 0.0) || !(b.mass > 0.0))
            {
                solution.reason = "zero-mass";
                return false;
            }
            if (!IsFinite(positionOrigin) || !IsFinite(velocityOrigin) || !IsFinite(omegaSystem)
                || !IsFinite(a.moment) || !IsFinite(b.moment) || !IsFinite(relativeSpeed))
            {
                solution.reason = "non-finite";
                return false;
            }

            DVector3 ca = a.Center;
            DVector3 cb = b.Center;
            DVector3 delta = cb - ca;
            double distance = delta.magnitude;
            if (!(distance > MinCenterDistanceMeters))
            {
                solution.reason = "coincident-centers";
                return false;
            }

            DVector3 dirBody = delta / distance;
            DVector3 ra = DVector3.Rotate(ca, rotation);
            DVector3 rb = DVector3.Rotate(cb, rotation);

            solution.ok = true;
            solution.massA = a.mass;
            solution.massB = b.mass;
            solution.centerA = ca;
            solution.centerB = cb;
            solution.directionBody = dirBody;

            // 新原点 = 该半质心；速度 = 该点的刚体速度（= 原点速度 + ω × r）
            solution.originA = positionOrigin + ra;
            solution.velocityA = velocityOrigin + DVector3.Cross(omegaSystem, ra);
            solution.originB = positionOrigin + rb;
            solution.velocityB = velocityOrigin + DVector3.Cross(omegaSystem, rb);

            if (relativeSpeed > 0.0)
            {
                double invA = 1.0 / a.mass;
                double invB = 1.0 / b.mass;
                double j = relativeSpeed / (invA + invB);
                DVector3 dirSystem = DVector3.Rotate(dirBody, rotation);
                solution.impulseJ = j;
                solution.relativeSpeed = j * (invA + invB);
                solution.velocityA -= dirSystem * (j * invA);   // A 沿 −u（离 B 而去）
                solution.velocityB += dirSystem * (j * invB);   // B 沿 +u
            }

            return true;
        }

        private static bool IsFinite(in DVector3 v)
            => double.IsFinite(v.x) && double.IsFinite(v.y) && double.IsFinite(v.z);

        private static bool IsFinite(double d) => double.IsFinite(d);
    }
}
