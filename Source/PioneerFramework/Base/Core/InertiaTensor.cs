using System;
using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// 刚体惯量（**批 0 底座**，2026-10-17）：形状预设、张量合成、主轴分解、`InertiaType` 三档分类。
    /// **纯数学（只 using System / System.Collections.Generic）⇒ 链入 `PioneerFramework.Pure.Tests` 钉解析样本。**
    ///
    /// ## 口径（计划稿 §6 批 0；待裁项的默认值已按推荐执行，改动点见计划稿）
    /// · **枢轴**：同时产出「绕质心」（主轴系，降序）与「绕群原点」（平行轴、体轴分量）两套张量——
    ///   批 0 只做读数，枢轴选择留给批 1/2 的裁定 ⇒ 改口零返工。
    /// · **合成完整式**：`I = Σ_i [ R_i·I_i·R_iᵀ + m_i(|d_i|²E − d_i d_iᵀ) ]`；v1 的 `R_i = I`
    ///   （`SpacePart` 暂无姿态字段），但接口按"逐件自惯量（绕自身质心）+ 位置"写，**不写标量对角累加**。
    /// · **形状**：长方体 / 圆柱 / 球（质量 + 尺寸导出）；一般几何走**自定义张量**（6 分量，绕部件质心）。
    /// · **分类**：等惯 / 轴对称 / 一般。判据 = **主轴惯量的严格相等（几何判定）**；"近简并带"
    ///   （相对间隙 ≤ 1e-9 但非严格相等）**不**并入等惯/轴对称——归 `Asymmetric`，
    ///   由消费者（姿态传播候迁件的分发器）按"数值子步"处理（KSA 在近简并处会退化/NaN，我们不吃那个亏）。
    ///   另提供 <see cref="IsNearDegenerate"/> 供读数与未来控制律使用。
    /// </summary>
    public static class InertiaTensor
    {
        public enum InertiaType { Isotropic, Axisymmetric, Asymmetric }

        public enum Shape { Box, Cylinder, Sphere }

        /// <summary>近简并带阈值（相对间隙；与姿态传播候迁件 `DegenerateTolerance` 同口径）。</summary>
        public const double DegenerateTolerance = 1e-9;

        // ── 魔法档限速预算（批 1，2026-10-17）──
        // 口径：ω_max = clamp(τ_ref / I_轴, 地板, 天花板)。
        // · **锚点船 = 默认无Map船**（`DebugShipSpawner.CreateNoMapShipGroup`：结构干质量 5000 kg、
        //   盒体 40×20×80 m）——B1 推荐的默认（"现有 5°/s 手感对应的船型"；**待用户裁定可改**）。
        //   锚点取它的**最慢主轴**：该轴保持 5°/s；其余轴 = 5° × I_最慢/I_轴（更快，受天花板钳制）。
        // · 船越大越慢（16,200 t 地图站 ≈ 0.002°/s ⇒ 地板 0.2°/s），小船封顶 30°/s。
        // · `maxAngularRate > 0` 的 def 覆盖（导弹 20°/s）**优先于本公式**，不进这里。
        public const double AnchorRateDegPerSec = 5.0;
        public const double MinRateDegPerSec = 0.2;
        public const double MaxRateDegPerSec = 30.0;
        public const double AnchorMass = 5000.0;
        public const double AnchorSizeX = 40.0;
        public const double AnchorSizeY = 20.0;
        public const double AnchorSizeZ = 80.0;

        /// <summary>锚点船的最慢主轴惯量（= 三主惯量最大值；kg·m²）。</summary>
        public static double AnchorSlowestAxisMoment()
        {
            BoxInertia(AnchorMass, AnchorSizeX, AnchorSizeY, AnchorSizeZ,
                out double ixx, out double iyy, out double izz);
            return Math.Max(ixx, Math.Max(iyy, izz));
        }

        /// <summary>魔法力矩预算 τ_ref（N·m·s）：锚点船以 5°/s 绕最慢轴转身所需的恒力矩 × 单位时间。</summary>
        public static double TorqueBudget =>
            AnchorRateDegPerSec * (Math.PI / 180.0) * AnchorSlowestAxisMoment();

        /// <summary>控制权限（KSA `RcsTorqueAuthority` 语义）：τ / I ⇒ 角加速度 (rad/s²)。</summary>
        public static double TorqueAuthority(double torque, double moment) =>
            moment > 0.0 ? torque / moment : 0.0;

        /// <summary>最小冲量位（KSA `RateBit` 语义）：Δh / I ⇒ 一个最小脉冲带来的 Δω (rad/s)。批 2 消费。</summary>
        public static double RateBit(double impulse, double moment) =>
            moment > 0.0 ? impulse / moment : 0.0;

        /// <summary>
        /// 角冲量响应：`Δω = I⁻¹·h`（h = 角冲量，**本体系**；返回本体系）。
        /// 主轴框架下逐轴除以 p，再转回体系（与 <see cref="InertiaResult.MomentAboutBodyAxis"/> 同一份方向矩阵）。
        /// 无数据/退化 ⇒ 零（调用方跳过角响应，线速度响应仍生效）。
        /// </summary>
        public static DVector3 InverseInertiaMultiply(in InertiaResult r, double hx, double hy, double hz)
        {
            if (!r.HasData || !(r.p1 > 0.0) || !(r.p2 > 0.0) || !(r.p3 > 0.0)) return DVector3.zero;
            double a = r.r00 * hx + r.r10 * hy + r.r20 * hz;
            double b = r.r01 * hx + r.r11 * hy + r.r21 * hz;
            double c = r.r02 * hx + r.r12 * hy + r.r22 * hz;
            double da = a / r.p1, db = b / r.p2, dc = c / r.p3;
            return new DVector3(
                r.r00 * da + r.r01 * db + r.r02 * dc,
                r.r10 * da + r.r11 * db + r.r12 * dc,
                r.r20 * da + r.r21 * db + r.r22 * dc);
        }

        /// <summary>
        /// 魔法档限速（rad/s）：`clamp(τ_ref / I_轴, 0.2°/s, 30°/s)`。
        /// **不含 def 覆盖**——调用方（`SpaceEntityGroup`）先看自己的 `maxAngularRate`。
        /// </summary>
        public static double RateForMoment(double moment)
        {
            double lo = MinRateDegPerSec * (Math.PI / 180.0);
            double hi = MaxRateDegPerSec * (Math.PI / 180.0);
            if (!(moment > 0.0)) return hi;
            double r = TorqueAuthority(TorqueBudget, moment);
            return Math.Min(hi, Math.Max(lo, r));
        }

        // ── 形状预设（主惯量绕部件质心；质量 kg、尺寸 m）──

        /// <summary>长方体（尺寸 = **全长**，非半长）。</summary>
        public static void BoxInertia(double mass, double sx, double sy, double sz,
            out double ixx, out double iyy, out double izz)
        {
            ixx = mass * (sy * sy + sz * sz) / 12.0;
            iyy = mass * (sx * sx + sz * sz) / 12.0;
            izz = mass * (sx * sx + sy * sy) / 12.0;
        }

        /// <summary>圆柱，对称轴 = 本体系 **Y**；`diameter` = 直径、`height` = 高。</summary>
        public static void CylinderInertia(double mass, double diameter, double height,
            out double ixx, out double iyy, out double izz)
        {
            double r = diameter * 0.5;
            double axial = 0.5 * mass * r * r;
            double radial = mass * (3.0 * r * r + height * height) / 12.0;
            ixx = radial;
            iyy = axial;
            izz = radial;
        }

        /// <summary>球（直径）。</summary>
        public static void SphereInertia(double mass, double diameter,
            out double ixx, out double iyy, out double izz)
        {
            double r = diameter * 0.5;
            double i = 0.4 * mass * r * r;
            ixx = i;
            iyy = i;
            izz = i;
        }

        /// <summary>部件自惯量（绕自身质心、体轴）。`useCustom` ⇒ 直接用 6 分量；否则按形状 + 已解析尺寸。</summary>
        public static void PartSelfInertia(Shape shape, double mass,
            double dimX, double dimY, double dimZ,
            bool useCustom, double cxx, double cyy, double czz, double cxy, double cxz, double cyz,
            out double ixx, out double iyy, out double izz, out double ixy, out double ixz, out double iyz)
        {
            if (!(mass > 0.0))
            {
                ixx = 0.0; iyy = 0.0; izz = 0.0; ixy = 0.0; ixz = 0.0; iyz = 0.0;
                return;
            }
            if (useCustom)
            {
                ixx = cxx; iyy = cyy; izz = czz; ixy = cxy; ixz = cxz; iyz = cyz;
                return;
            }
            // 尺寸下限防零（退化盒/未配 bounds）：1e-9 m —— 惯量近零而非 NaN。
            double sx = dimX > 1e-9 ? dimX : 1e-9;
            double sy = dimY > 1e-9 ? dimY : 1e-9;
            double sz = dimZ > 1e-9 ? dimZ : 1e-9;
            switch (shape)
            {
                case Shape.Cylinder:
                    CylinderInertia(mass, sx, sy, out ixx, out iyy, out izz);
                    break;
                case Shape.Sphere:
                    SphereInertia(mass, sx, out ixx, out iyy, out izz);
                    break;
                default:
                    BoxInertia(mass, sx, sy, sz, out ixx, out iyy, out izz);
                    break;
            }
            ixy = 0.0; ixz = 0.0; iyz = 0.0;
        }

        // ── 合成 ──

        /// <summary>一个部件的贡献：质量 + 质心相对**群原点**的位置 + 绕自身质心的自惯量（体轴 6 分量）。</summary>
        public struct PartContribution
        {
            public double mass;
            public double px, py, pz;
            public double cxx, cyy, czz, cxy, cxz, cyz;
            /// <summary>B3：零件自身姿态（相对实体体轴）；默认 identity ⇒ 与旧结果逐位一致。</summary>
            public DQuaternion rotation;
        }

        /// <summary>合成结果。绕质心给**主轴降序 + 主轴方向（列）**；绕原点给体轴 6 分量（平行轴）。</summary>
        public struct InertiaResult
        {
            public bool HasData;
            public double mass;
            public double comX, comY, comZ;                 // 质心（群体系，相对群原点）
            public double p1, p2, p3;                       // 绕质心：主轴惯量（p1 ≥ p2 ≥ p3）
            public double r00, r01, r02, r10, r11, r12, r20, r21, r22; // 主轴方向（列 = 主轴，体系→主轴系）
            public InertiaType type;
            public double oIxx, oIyy, oIzz, oIxy, oIxz, oIyz; // 绕群原点（体轴分量；平行轴）
            public double Trace => p1 + p2 + p3;

            /// <summary>
            /// 绕**本体系**给定方向（不必单位；如船的滚转/偏航/俯仰轴）的转动惯量：`nᵀ·I_com·n`。
            /// 用「主轴惯量 + 主轴方向矩阵」重建（列 = 主轴在体系中的方向）⇒ 与张量分量同一份数据。
            /// </summary>
            public double MomentAboutBodyAxis(double x, double y, double z)
            {
                double len2 = x * x + y * y + z * z;
                if (!(len2 > 0.0)) return 0.0;
                double inv = 1.0 / Math.Sqrt(len2);
                x *= inv; y *= inv; z *= inv;
                // n 在主轴系的坐标 = Rᵀ·n（R 的列 = 主轴）
                double a = r00 * x + r10 * y + r20 * z;
                double b = r01 * x + r11 * y + r21 * z;
                double c = r02 * x + r12 * y + r22 * z;
                return p1 * a * a + p2 * b * b + p3 * c * c;
            }
        }

        /// <summary>对称张量按四元数旋转：I' = R·I·Rᵀ（体轴 6 分量进/出）。</summary>
        private static void RotateTensor(DQuaternion rotation,
            ref double cxx, ref double cyy, ref double czz, ref double cxy, ref double cxz, ref double cyz)
        {
            double x = rotation.x, y = rotation.y, z = rotation.z, w = rotation.w;
            double r00 = 1 - 2 * (y * y + z * z), r01 = 2 * (x * y - z * w), r02 = 2 * (x * z + y * w);
            double r10 = 2 * (x * y + z * w), r11 = 1 - 2 * (x * x + z * z), r12 = 2 * (y * z - x * w);
            double r20 = 2 * (x * z - y * w), r21 = 2 * (y * z + x * w), r22 = 1 - 2 * (x * x + y * y);

            // temp = R · I
            double t00 = r00 * cxx + r01 * cxy + r02 * cxz;
            double t01 = r00 * cxy + r01 * cyy + r02 * cyz;
            double t02 = r00 * cxz + r01 * cyz + r02 * czz;
            double t10 = r10 * cxx + r11 * cxy + r12 * cxz;
            double t11 = r10 * cxy + r11 * cyy + r12 * cyz;
            double t12 = r10 * cxz + r11 * cyz + r12 * czz;
            double t20 = r20 * cxx + r21 * cxy + r22 * cxz;
            double t21 = r20 * cxy + r21 * cyy + r22 * cyz;
            double t22 = r20 * cxz + r21 * cyz + r22 * czz;

            // I' = temp · Rᵀ
            cxx = t00 * r00 + t01 * r01 + t02 * r02;
            cxy = t00 * r10 + t01 * r11 + t02 * r12;
            cxz = t00 * r20 + t01 * r21 + t02 * r22;
            cyy = t10 * r10 + t11 * r11 + t12 * r12;
            cyz = t10 * r20 + t11 * r21 + t12 * r22;
            czz = t20 * r20 + t21 * r21 + t22 * r22;
        }

        /// <summary>合成（**无缓存**；调用方负责事件驱动缓存与失效——见 `SpaceEntityGroup.Inertia`）。</summary>
        public static InertiaResult Compose(IReadOnlyList<PartContribution> parts)
        {
            var r = new InertiaResult();
            if (parts == null || parts.Count == 0) return r;

            double mass = 0.0;
            double mx = 0.0, my = 0.0, mz = 0.0;
            double oxx = 0.0, oyy = 0.0, ozz = 0.0, oxy = 0.0, oxz = 0.0, oyz = 0.0;
            for (int i = 0; i < parts.Count; i++)
            {
                var q = parts[i];
                if (!(q.mass > 0.0)) continue;
                mass += q.mass;
                mx += q.mass * q.px;
                my += q.mass * q.py;
                mz += q.mass * q.pz;
                double d2 = q.px * q.px + q.py * q.py + q.pz * q.pz;
                // B3：零件自身姿态 ⇒ 先把自惯量从零件体轴转到实体体轴（R I Rᵀ）。
                double cxx = q.cxx, cyy = q.cyy, czz = q.czz, cxy = q.cxy, cxz = q.cxz, cyz = q.cyz;
                bool zeroQ = q.rotation.x == 0.0 && q.rotation.y == 0.0 && q.rotation.z == 0.0 && q.rotation.w == 0.0;
                if (!zeroQ && q.rotation != DQuaternion.identity)
                    RotateTensor(q.rotation, ref cxx, ref cyy, ref czz, ref cxy, ref cxz, ref cyz);
                // 平行轴（关于原点累加）：I_O = I_com + m(|d|²E − d dᵀ)
                oxx += cxx + q.mass * (d2 - q.px * q.px);
                oyy += cyy + q.mass * (d2 - q.py * q.py);
                ozz += czz + q.mass * (d2 - q.pz * q.pz);
                oxy += cxy - q.mass * q.px * q.py;
                oxz += cxz - q.mass * q.px * q.pz;
                oyz += cyz - q.mass * q.py * q.pz;
            }
            if (!(mass > 0.0)) return r;

            double cx = mx / mass, cy = my / mass, cz = mz / mass;
            r.HasData = true;
            r.mass = mass;
            r.comX = cx; r.comY = cy; r.comZ = cz;
            r.oIxx = oxx; r.oIyy = oyy; r.oIzz = ozz;
            r.oIxy = oxy; r.oIxz = oxz; r.oIyz = oyz;

            // 绕质心（平行轴反向）：I_com = I_O − m(c²E − c cᵀ)
            double c2 = cx * cx + cy * cy + cz * cz;
            double ixx = oxx - mass * (c2 - cx * cx);
            double iyy = oyy - mass * (c2 - cy * cy);
            double izz = ozz - mass * (c2 - cz * cz);
            double ixy = oxy + mass * cx * cy;
            double ixz = oxz + mass * cx * cz;
            double iyz = oyz + mass * cy * cz;

            PrincipalAxes(ixx, iyy, izz, ixy, ixz, iyz,
                out r.p1, out r.p2, out r.p3,
                out r.r00, out r.r01, out r.r02,
                out r.r10, out r.r11, out r.r12,
                out r.r20, out r.r21, out r.r22);
            r.type = Classify(r.p1, r.p2, r.p3);
            return r;
        }

        // ── 主轴分解（Jacobi）──

        /// <summary>
        /// 对称 3×3 的主轴分解（Jacobi 旋转，double）：输出**降序**主惯量（p1 ≥ p2 ≥ p3）与主轴方向
        /// （r 矩阵的**列** = 主轴，表示"体系 → 主轴系"）。
        /// 纯函数；仅在缓存重建/测试里调用（非热路径）。对称输入下 32 轮 sweep 必收敛。
        /// </summary>
        public static void PrincipalAxes(double ixx, double iyy, double izz, double ixy, double ixz, double iyz,
            out double p1, out double p2, out double p3,
            out double r00, out double r01, out double r02,
            out double r10, out double r11, out double r12,
            out double r20, out double r21, out double r22)
        {
            // 对称矩阵（行优先 3×3）
            var a = new double[9];
            a[0] = ixx; a[1] = ixy; a[2] = ixz;
            a[3] = ixy; a[4] = iyy; a[5] = iyz;
            a[6] = ixz; a[7] = iyz; a[8] = izz;
            // 特征向量矩阵（列 = 向量）
            var v = new double[9] { 1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0 };

            for (int sweep = 0; sweep < 32; sweep++)
            {
                double off = a[1] * a[1] + a[2] * a[2] + a[5] * a[5];
                double diag = a[0] * a[0] + a[4] * a[4] + a[8] * a[8];
                if (off <= 1e-28 * (diag + 1e-300)) break;
                for (int p = 0; p < 2; p++)
                {
                    for (int q = p + 1; q < 3; q++)
                    {
                        double apq = a[p * 3 + q];
                        double scale = Math.Abs(a[p * 3 + p]) + Math.Abs(a[q * 3 + q]) + 1e-300;
                        if (Math.Abs(apq) <= 1e-18 * scale) continue;
                        double app = a[p * 3 + p];
                        double aqq = a[q * 3 + q];
                        // τ = (a_qq − a_pp)/(2 a_pq)；t = sign(τ)/(|τ| + √(τ²+1))（τ=0 ⇒ t=1）
                        double tau = (aqq - app) / (2.0 * apq);
                        double t = tau >= 0.0
                            ? 1.0 / (tau + Math.Sqrt(1.0 + tau * tau))
                            : -1.0 / (-tau + Math.Sqrt(1.0 + tau * tau));
                        double c = 1.0 / Math.Sqrt(1.0 + t * t);
                        double s = t * c;
                        // A ← Jᵀ A J（先列、后行）
                        for (int k = 0; k < 3; k++)
                        {
                            double akp = a[k * 3 + p], akq = a[k * 3 + q];
                            a[k * 3 + p] = c * akp - s * akq;
                            a[k * 3 + q] = s * akp + c * akq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double apk = a[p * 3 + k], aqk = a[q * 3 + k];
                            a[p * 3 + k] = c * apk - s * aqk;
                            a[q * 3 + k] = s * apk + c * aqk;
                        }
                        // V ← V J（特征向量同步）
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = v[k * 3 + p], vkq = v[k * 3 + q];
                            v[k * 3 + p] = c * vkp - s * vkq;
                            v[k * 3 + q] = s * vkp + c * vkq;
                        }
                    }
                }
            }

            // 降序排序（三元素；同步交换向量列）
            var d = new double[3] { a[0], a[4], a[8] };
            var idx = new int[3] { 0, 1, 2 };
            if (d[idx[0]] < d[idx[1]]) { var tmp = idx[0]; idx[0] = idx[1]; idx[1] = tmp; }
            if (d[idx[1]] < d[idx[2]]) { var tmp = idx[1]; idx[1] = idx[2]; idx[2] = tmp; }
            if (d[idx[0]] < d[idx[1]]) { var tmp = idx[0]; idx[0] = idx[1]; idx[1] = tmp; }

            p1 = d[idx[0]]; p2 = d[idx[1]]; p3 = d[idx[2]];
            r00 = v[idx[0]]; r10 = v[3 + idx[0]]; r20 = v[6 + idx[0]];
            r01 = v[idx[1]]; r11 = v[3 + idx[1]]; r21 = v[6 + idx[1]];
            r02 = v[idx[2]]; r12 = v[3 + idx[2]]; r22 = v[6 + idx[2]];
        }

        // ── 分类 ──

        /// <summary>
        /// 三档分类（主轴降序输入）。**只认严格相等**（几何判定）：
        /// 三轴全等 ⇒ 等惯；恰两轴相等 ⇒ 轴对称；其余（含"近简并带"）⇒ 一般。
        /// 近简并带交给消费者的数值路径——**不要**在这里放宽成"近似相等"（那正是 KSA 的三条悬崖之一）。
        /// </summary>
        public static InertiaType Classify(double p1, double p2, double p3)
        {
            if (!(p1 > 0.0)) return InertiaType.Isotropic;            // 退化（点质量/无数据）
            if (p1 == p2 && p2 == p3) return InertiaType.Isotropic;
            if (p1 == p2 || p2 == p3) return InertiaType.Axisymmetric;
            return InertiaType.Asymmetric;
        }

        /// <summary>
        /// 近简并带判定：相对间隙 ≤ <see cref="DegenerateTolerance"/> **且非严格相等** ⇒ true。
        /// （读数："一般(近简并带)"；未来控制律可据此选数值子步。）
        /// </summary>
        public static bool IsNearDegenerate(double p1, double p2, double p3, out double gap12, out double gap23)
        {
            double denom = p1 > 0.0 ? p1 : 1.0;
            gap12 = (p1 - p2) / denom;
            gap23 = (p2 - p3) / denom;
            return (gap12 <= DegenerateTolerance && p1 != p2)
                || (gap23 <= DegenerateTolerance && p2 != p3);
        }
    }
}
