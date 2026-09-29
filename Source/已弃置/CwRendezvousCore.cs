// ⚠ **已归档（批5.B，2026-09-28）** —— 本文件**不参与编译**。
// 原因：CW（Hill）两冲量闭式解是**旧律的终端"温柔解"**，而旧律已作废（用户 2026-09-22 裁定）；
//       实测它**零生产调用者**，只被随旧律一起退役的用例调用（`已弃置/README.md` 排队中 §2 早已登记）。
// 替代品：终端由 **MJ 靶执行器**（`BurnStep` 的活差额 + 地板/反向收手）接管。
// 取回代价：搬回 `Source/PioneerFramework/Base/Orbital/` + 恢复两个 csproj 的 `<Compile Include>`。

using System;

namespace PioneerFramework
{
    /// <summary>
    /// CW（Hill / Clohessy–Wiltshire）相对运动的两冲量交会**闭式解**（2026-09-17）。
    ///
    /// 定位：交会终端的**温柔解**。设计稿
    /// <c>DesignNotes/交会物理本质与闭环提法-20260917.md</c> §14.3 的 E1。
    ///
    /// ── 为什么需要它（与"滚动 Lambert 到点"的对比）──────────────────────
    /// 滚动 Lambert 到点是**拦截**解：它让接近率由几何决定（离得越远、剩余时间越短，接近率越高）。
    /// 实测同轨 GEO、4,380 km 处接管，tof 受上限所限 ⇒ 接近率 730 m/s ⇒ 总 Δv **1011 m/s**。
    /// 而交会的温柔解应当是**相位式**：小幅改半长轴、让时间把相位补齐，Δv 只需 **~50 m/s**。
    ///
    /// 本类是后者的解析形式。它的两个优点：
    ///   · **同时**满足位置与速度（这正是交会与拦截的区别）；
    ///   · **解析、无分支**——避开了 <c>Gooding.Solve</c> 单圈解在 $T\approx T_{orb}$ 处落到
    ///     退化支的问题（实测：把拦截的 tof 提到 0.9 个轨道周期时全发散）。
    ///
    /// ── 解的口径 ──────────────────────────────────────────────────────
    /// 相对状态 $x=(r,v)$，分量在 **RTN 系**（x 径向向外、y 沿迹顺行、z 面外 = 轨道法向）。
    /// 记 $B=[0;I_3]$（冲量只改速度），两次点火 $\Delta v_1$（现在）与 $\Delta v_2$（$T$ 后）要求 $x(T)=0$：
    /// $$ \Phi(T)\,(x_0+B\,\Delta v_1)+B\,\Delta v_2 \;=\; 0 $$
    /// 拆开：
    /// $$ \Phi_{rv}\Delta v_1 = -\big(\Phi_{rr}r_0+\Phi_{rv}v_0\big),\qquad
    ///    \Delta v_2 = -\big(\Phi_{vr}r_0+\Phi_{vv}(v_0+\Delta v_1)\big) $$
    /// 第一个是 3×3 线性方程（解出 $\Delta v_1$），第二个直接给出 $\Delta v_2$。
    ///
    /// STM（$c=\cos nT,\ s=\sin nT,\ \theta=nT$）：
    /// $$ \Phi_{rr}=\begin{bmatrix}4-3c&0&0\\6(s-\theta)&1&0\\0&0&c\end{bmatrix},\quad
    ///    \Phi_{rv}=\frac1n\begin{bmatrix}s&2(1-c)&0\\-2(1-c)&4s-3\theta&0\\0&0&s\end{bmatrix} $$
    /// $$ \Phi_{vr}=n\begin{bmatrix}3s&0&0\\6(c-1)&0&0\\0&0&-s\end{bmatrix},\quad
    ///    \Phi_{vv}=\begin{bmatrix}c&2s&0\\-2s&4c-3&0\\0&0&c\end{bmatrix} $$
    ///
    /// **适用界**：$|r_{rel}| \ll r$（线性化）。同轨 GEO 下 4,380 km 是轨道半径的 16.6%，
    /// 已接近上限；实践上应在 **5% 以内**接手（远场律负责把相对状态压到这个量级）。
    /// 面外（z）通道与面内解耦，$\Delta i$ 的修正量是 $2v\sin(\Delta i/2)$ 的线性化形式。
    /// </summary>
    public static class CwRendezvousCore
    {
        /// <summary>一次两冲量解的结果。</summary>
        public struct TwoImpulsePlan
        {
            /// <summary>现在施加的冲量（RTN 分量，m/s）。</summary>
            public DVector3 Dv1;
            /// <summary>$T$ 后施加的冲量（RTN 分量，m/s）。</summary>
            public DVector3 Dv2;
            /// <summary>转移时长（s）。</summary>
            public double ToF;
            /// <summary>总 Δv = |Δv₁| + |Δv₂|（m/s）——选优判据。</summary>
            public double TotalDv;
        }

        /// <summary>
        /// 给定相对状态与时长，解出两冲量。返回 false = 时长退化（$\theta\to0$ 使 $\Phi_{rv}$ 奇异）
        /// 或数值失败。
        /// </summary>
        public static bool Solve(DVector3 r0Rel, DVector3 v0Rel, double n, double T,
            out DVector3 dv1, out DVector3 dv2)
        {
            dv1 = DVector3.zero;
            dv2 = DVector3.zero;
            if (!(n > 0.0) || !(T > 0.0)) return false;

            double th = n * T, c = Math.Cos(th), sn = Math.Sin(th);

            // ── 奇点守卫：面外（z）通道的 Φ_rv 系数是 sin(nT)/n，Φ_rr 的面外项是 cos(nT) ──
            //   ⇒ **nT 接近 π 的整数倍（即 T 接近半个轨道周期的整数倍）时整块矩阵奇异**。
            //   实测（2026-09-17）：接力点恰落在扫描网格的 T=0.5·T_orb 上时，解出来的 Δv1 是
            //   数值垃圾却不报错（枢轴 1e-12 > 1e-14 的阈值漏过），船于是带着 r·Δi 量级的
            //   交叉轨迹偏移停在原地——表现为"dist ≈ 末面外 z，而 rel≈0"。加守卫跳过该邻域，
            //   代价扫描自然落到非奇异的 T 上。
            if (Math.Abs(sn) < 0.05) return false;

            // Φ_rv（按 [行,列] 存）
            var m = new double[3, 3];
            m[0, 0] = sn / n;                  m[0, 1] = 2.0 * (1.0 - c) / n;        m[0, 2] = 0.0;
            m[1, 0] = -2.0 * (1.0 - c) / n;    m[1, 1] = (4.0 * sn - 3.0 * th) / n;  m[1, 2] = 0.0;
            m[2, 0] = 0.0;                     m[2, 1] = 0.0;                        m[2, 2] = sn / n;

            // 右端 b = −(Φ_rr·r0 + Φ_rv·v0)
            double rr00 = 4.0 - 3.0 * c;
            double rr10 = 6.0 * (sn - th);
            double rr11 = 1.0;
            double rr22 = c;
            var b = new double[3];
            b[0] = -(rr00 * r0Rel.x + m[0, 0] * v0Rel.x + m[0, 1] * v0Rel.y);
            b[1] = -(rr10 * r0Rel.x + rr11 * r0Rel.y + m[1, 0] * v0Rel.x + m[1, 1] * v0Rel.y);
            b[2] = -(rr22 * r0Rel.z + m[2, 2] * v0Rel.z);

            // 3×3 高斯消元（部分主元）
            var a = (double[,])m.Clone();
            for (int col = 0; col < 3; col++)
            {
                int piv = col;
                for (int r = col + 1; r < 3; r++)
                    if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
                if (Math.Abs(a[piv, col]) < 1e-14) return false;
                if (piv != col)
                {
                    for (int k2 = 0; k2 < 3; k2++) { double t2 = a[col, k2]; a[col, k2] = a[piv, k2]; a[piv, k2] = t2; }
                    double tb = b[col]; b[col] = b[piv]; b[piv] = tb;
                }
                for (int r = col + 1; r < 3; r++)
                {
                    double f = a[r, col] / a[col, col];
                    for (int k2 = col; k2 < 3; k2++) a[r, k2] -= f * a[col, k2];
                    b[r] -= f * b[col];
                }
            }
            double x2 = b[2] / a[2, 2];
            double x1 = (b[1] - a[1, 2] * x2) / a[1, 1];
            double x0 = (b[0] - a[0, 1] * x1 - a[0, 2] * x2) / a[0, 0];
            dv1 = new DVector3(x0, x1, x2);

            // Δv2 = −(Φ_vr·r0 + Φ_vv·(v0+Δv1))
            double vx = v0Rel.x + dv1.x, vy = v0Rel.y + dv1.y, vz = v0Rel.z + dv1.z;
            double vr0 = 3.0 * n * sn * r0Rel.x;
            double vr1 = 6.0 * n * (c - 1.0) * r0Rel.x;
            double vr2 = -n * sn * r0Rel.z;
            double vv0 = c * vx + 2.0 * sn * vy;
            double vv1 = -2.0 * sn * vx + (4.0 * c - 3.0) * vy;
            double vv2 = c * vz;
            dv2 = new DVector3(-(vr0 + vv0), -(vr1 + vv1), -(vr2 + vv2));
            return true;
        }

        /// <summary>
        /// **在 $[\text{loFrac},\text{hiFrac}]\cdot T_{orb}$ 上扫 <paramref name="steps"/> 格，
        /// 返回总 Δv 最小的解。**
        ///
        /// ⚠ **扫描区间必须避开 $nT=k\pi$**（$T$ 为半个轨道周期的整数倍）：那里 $\Phi_{rv}$ 的面外
        /// 系数 $\sin(nT)/n$ 与 $\Phi_{rr}$ 的面外项 $\cos(nT)$ 同时退化，整块矩阵奇异，解出来是
        /// **数值垃圾却不报错**（实测接力点恰落在 $T=0.5T_{orb}$ 时，船带着 $r\Delta i$ 的
        /// 交叉轨迹偏移停在原地）。
        ///
        /// 默认区间 $[0.15,0.45]\cdot T_{orb}$：$nT\in[0.94,2.83]$，$\sin$ 远离零点 ✓；
        /// 且对应**温柔的接近率**（1,316 km / 27,110 s ≈ 49 m/s）。
        ///
        /// ⚠ **不要在区间之间比"最省"**：滚动执行下每轮终点会前推一个 $T$，
        /// 跨区间比较等于拿不同的"到达时刻"当同一件事比——那不是成本，是错觉。
        /// </summary>
        public static bool SolveBest(DVector3 r0Rel, DVector3 v0Rel, double n,
            out TwoImpulsePlan best, double loFrac = 0.08, double hiFrac = 0.35, int steps = 16)
        {
            best = default(TwoImpulsePlan);
            if (!(n > 0.0) || steps < 2) return false;
            double tOrb = 2.0 * Math.PI / n;
            bool any = false;

            // **视界扫描**：$nT=k\pi$（$T$ 为半周期整数倍）处整块矩阵奇异，故区间须避开。
            // ⚠ 曾经把扫描扩到第二段（更长视界），**实测更贵**（T5 终端 430→1,455 m/s）：
            //   滚动执行下，静态计划的"最省"**不等于**闭环的"最省"——长视界给出的是更温和的方向，
            //   而滚动重解会让船在误差上停留更久 ⇒ 总 Δv 更大。
            //   这与 §14.6 那个"跨原语比成本"是同一类错误的新化身：**选择准则必须对闭环成立**。
            for (int i = 0; i <= steps; i++)
            {
                double frac = loFrac + (hiFrac - loFrac) * i / steps;
                double T = frac * tOrb;
                if (!Solve(r0Rel, v0Rel, n, T, out var d1, out var d2)) continue;
                double tot = d1.magnitude + d2.magnitude;
                if (!any || tot < best.TotalDv)
                {
                    any = true;
                    best.Dv1 = d1; best.Dv2 = d2; best.ToF = T; best.TotalDv = tot;
                }
            }
            return any;
        }

        /// <summary>
        /// **打靶修正的灵敏度**：$T$ 时刻的 RTN 位置偏差 $\Delta r$ → $t=0$ 需要的 RTN 速度修正
        /// $\Delta v = -\Phi_{rv}(T)^{-1}\Delta r$。
        ///
        /// 用途：解「**精确两冲量交会**」。CW 是**线性**解，而真实动力学是 Kepler——
        /// 在 1,295 km 分离 + 9,000 s 视界下，线性化误差会被滚动循环反复纠偏，
        /// 使台架终端 = 模型内同策略的 **1.74 倍**（§19.7）。
        /// 修法 = CW 首猜 → **精确传播**测残差 → 按本灵敏度修正 → 迭代 2~3 次。
        ///
        /// ⚠ **不能用 Lambert 到点代替**：那是"拦截"解（只命中位置、不含到达速度匹配），
        /// 实测滚动执行下会发散（T5@192000 飞到 283,062 km）。**必须保住"交会"结构。**
        /// </summary>
        public static bool PositionErrorToVelocityFix(DVector3 dr, double n, double T, out DVector3 dv)
        {
            dv = DVector3.zero;
            if (!(n > 0.0) || !(T > 0.0)) return false;
            double th = n * T, sn = Math.Sin(th);
            if (Math.Abs(sn) < 0.05) return false;                 // 同 Solve：避开奇异带
            double c = Math.Cos(th);
            var m = new double[3, 3];
            m[0, 0] = sn / n;               m[0, 1] = 2.0 * (1.0 - c) / n;        m[0, 2] = 0.0;
            m[1, 0] = -2.0 * (1.0 - c) / n; m[1, 1] = (4.0 * sn - 3.0 * th) / n;  m[1, 2] = 0.0;
            m[2, 0] = 0.0;                  m[2, 1] = 0.0;                        m[2, 2] = sn / n;
            var b = new double[] { -dr.x, -dr.y, -dr.z };
            var a = (double[,])m.Clone();
            for (int col = 0; col < 3; col++)
            {
                int piv = col;
                for (int r2 = col + 1; r2 < 3; r2++)
                    if (Math.Abs(a[r2, col]) > Math.Abs(a[piv, col])) piv = r2;
                if (Math.Abs(a[piv, col]) < 1e-14) return false;
                if (piv != col)
                {
                    for (int k = 0; k < 3; k++) { double t2 = a[col, k]; a[col, k] = a[piv, k]; a[piv, k] = t2; }
                    double tb = b[col]; b[col] = b[piv]; b[piv] = tb;
                }
                for (int r2 = col + 1; r2 < 3; r2++)
                {
                    double f = a[r2, col] / a[col, col];
                    for (int k = col; k < 3; k++) a[r2, k] -= f * a[col, k];
                    b[r2] -= f * b[col];
                }
            }
            double x2 = b[2] / a[2, 2];
            double x1 = (b[1] - a[1, 2] * x2) / a[1, 1];
            double x0 = (b[0] - a[0, 1] * x1 - a[0, 2] * x2) / a[0, 0];
            dv = new DVector3(x0, x1, x2);
            return true;
        }

        /// <summary>
        /// **CW 自由传播** $x(t)=\Phi(t)x(0)$（解析）。
        ///
        /// 用途：把"**策略**本身的代价"与"真实非线性仿真里的杂项"分开核算——
        /// 在 CW 模型内策略的代价可以精确算出来，不受扰动、执行器、节流的影响。
        /// </summary>
        public static void Propagate(DVector3 r0, DVector3 v0, double n, double t,
            out DVector3 r, out DVector3 v)
        {
            if (!(n > 0.0) || !(t > 0.0)) { r = r0; v = v0; return; }
            double th = n * t, c = Math.Cos(th), sn = Math.Sin(th);
            double f01 = sn / n, f02 = 2.0 * (1.0 - c) / n, f12 = (4.0 * sn - 3.0 * th) / n;
            r = new DVector3(
                (4.0 - 3.0 * c) * r0.x + f01 * v0.x + f02 * v0.y,
                6.0 * (sn - th) * r0.x + r0.y - f02 * v0.x + f12 * v0.y,
                c * r0.z + f01 * v0.z);
            v = new DVector3(
                3.0 * n * sn * r0.x + c * v0.x + 2.0 * sn * v0.y,
                6.0 * n * (c - 1.0) * r0.x - 2.0 * sn * v0.x + (4.0 * c - 3.0) * v0.y,
                -n * sn * r0.z + c * v0.z);
        }
    }
}
