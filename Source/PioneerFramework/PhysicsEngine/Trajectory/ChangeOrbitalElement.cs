using System;
using MechJebLib.Functions;
using MechJebLib.Minimization;
using MechJebLib.Primitives;
using MechJebLib.Rootfinding;
using MechJebLib.Utils;
using PioneerFramework;   // 批7（2026-09-30）：统一调度原语 `ParallelOps`（θ 粗扫并行 + 唯一归约器）

namespace MechJebLib.Maneuvers
{
    /// <summary>
    /// "把一个轨道要素改成指定值，最少要多少 Δv"——MJ 的 `MechJebLib/Maneuvers/ChangeOrbitalElement.cs` 的**重写**。
    ///
    /// ## ⚠ 为什么它不在 `ThirdParty/MechJebLib/` 里
    /// 上游这个文件**依赖 vendored `alglib`**（`minnlc`：先用增广拉格朗日、再用 SQP）。
    /// 而 `ThirdParty/` 的那条唯一不变式是"**与上游同名文件的 diff 必须为空**"——
    /// 一旦要换掉 alglib 就必然产生 diff ⇒ **它不能住在那儿**。
    /// 所以：**类名与命名空间保持上游原样**（J1：调用方代码不用改、上游 diff 可比对），
    /// 但**文件位于我们自己的树里**（`Base/Orbital/`）⇒ "上游 vs 我们的"在文件系统层面依然机械可判。
    ///
    /// ## 上游那个"NLP"其实是一维族问题（本次改写的依据）
    /// 上游 `DeltaV` 的优化问题：**变量只有 `(dvx, dvy)` 两个**（在**近焦点系**里，`dvz ≡ 0`），
    /// **目标** `min |Δv|²`，**等式约束 1 条** `element(p, q + Δv) = value`。
    /// ⇒ 可行集是 2 维平面上的**一条曲线**，问题 = "在这条曲线上找离原点最近的点"。
    /// **`alglib` 的通用 NLP 求解器对这个规模是杀鸡用牛刀**，而它的存在正是本项目"每引入一族求解器就多一片手工护栏"的来源。
    ///
    /// ## 替换方案：两层一维问题（计划稿 §11.7 说的"网格扫描 + Brent"）
    /// 把曲线按**方向角** θ 参数化：`û(θ) = (cosθ, sinθ, 0)`，内层解
    /// `r(θ) =` "沿 û(θ) 需要多大的 Δv 才能满足约束"（**`BrentRoot` 求根**）。
    /// 于是 **`|Δv| = r(θ)`** ⇒ **外层最小化 `r(θ)` 就是在最小化 `|Δv|`**（θ 网格扫描 + **`BrentMin`** 细化）。
    /// 两层都是一维 ⇒ 用**已被上游 5000 条用例验过的** `BrentRoot`/`BrentMin`，不再引入任何自造求解器。
    ///
    /// ## 与上游的语义保持一致的地方（逐条）
    /// · **同一个无量纲化**（`Scale.Create(mu, |r|)`，元素函数用 `mu = 1.0`）；
    /// · **同一个要素变换**：`APOAPSIS`/`SMA` 用**倒数**（`1/x`）做约束，因为在那两个量上 `alglib` 的数值条件更好
    ///   （上游注释如此）⇒ 一并照搬，免得换个提法就把数值条件换坏；
    /// · **同一个返回**：`rot * (dvx, dvy, 0) * VelocityScale`（近焦点系 → ECI）。
    ///
    /// ## 与上游的语义**故意不同**的地方
    /// · 上游在求解失败时抛 `Exception`（SQP `terminationtype < 0`）；这里在**找不到可行方向**时抛
    ///   `ArgumentException`，并说明是"该目标不可达"——因为一维求根失败的含义比"求解器崩了"更具体；
    /// · 上游的 `optguard` 参数（alglib 的梯度/光滑性自检）在本实现里不需要——它本来就是 alglib 的调试设施。
    ///   **保留该形参**以维持签名可比，但实现为**自检**：验证解确实满足约束（见 `Verify`）
    ///   —— 这比 alglib 的 optguard 更贴合我们的用法，且不引入新依赖。
    /// </summary>
    public static class ChangeOrbitalElement
    {
        private enum Type { PERIAPSIS, APOAPSIS, SMA, ECC }

        /// <summary>θ 网格划分（外层扫描）。2° 步长 ⇒ 最少 180 个方向；之后用 `BrentMin` 细化。</summary>
        private const int THETA_STEPS = 180;

        /// <summary>
        /// 无量纲 Δv 的搜索上限（× 速度尺度）。取 `2·v_esc` = `2√2 ≈ 2.83`：
        /// 超过逃逸速度两倍的单次脉冲在这个问题里没有物理意义。
        /// </summary>
        private const double DV_CAP = 2.83;

        /// <summary>要素函数（**无量纲，mu = 1**）。`APOAPSIS`/`SMA` 取倒数 —— 与上游一致。</summary>
        private static double ElementOf(Type type, V3 p, V3 q)
        {
            switch (type)
            {
                case Type.PERIAPSIS: return Astro.PeriapsisFromStateVectors(1.0, p, q);
                case Type.APOAPSIS: return 1.0 / Astro.ApoapsisFromStateVectors(1.0, p, q);
                case Type.SMA: return 1.0 / Astro.SmaFromStateVectors(1.0, p, q);
                case Type.ECC: return Astro.EccFromStateVectors(1.0, p, q);
                default: throw new ArgumentException("unknown Type");
            }
        }

        /// <summary>目标值按同样的口径变换（倒数 / 原值），见类注释。</summary>
        private static double TargetOf(Type type, double value) =>
            type == Type.ECC ? value : (type == Type.PERIAPSIS ? value : 1.0 / value);

        private struct Probe
        {
            public V3 P;
            public V3 Q;
            public double Target;
            public Type Type;
            public V3 Dir;
        }

        /// <summary>约束残差（无量纲）。**任何非有限结果都返回 NaN**，由调用方当作"这个方向不行"。</summary>
        private static double Residual(double r, Probe probe)
        {
            V3 q = probe.Q + probe.Dir * r;
            if (!q.IsFinite() || q.sqrMagnitude <= 0.0) return double.NaN;
            double v;
            try
            {
                v = ElementOf(probe.Type, probe.P, q);
            }
            catch (Exception)
            {
                return double.NaN;
            }

            double res = v - probe.Target;
            return Statics.IsFinite(res) ? res : double.NaN;
        }

        /// <summary>
        /// 沿 `û(θ)` 找到满足约束的最小 `r ≥ 0`（无量纲）。找不到（该方向不可达）返回 `NaN`。
        ///
        /// 做法：**几何阶梯找括号 + `BrentRoot`**。阶梯而不是等距，是因为"需要的 Δv"自然落在一个很宽的量级范围里
        /// （圆化只要几个百分点，改偏心率可能要吃下大半逃逸速度）。
        /// </summary>
        private static double SolveRadiusAlong(V3 dir, Probe probe)
        {
            probe.Dir = dir;

            double f0 = Residual(0.0, probe);
            if (double.IsNaN(f0)) return double.NaN;
            if (Math.Abs(f0) < 1e-14) return 0.0; // 当前就满足 ⇒ 不需要 Δv

            double lo = 0.0, flo = f0;
            double r = 0.01;
            while (r <= DV_CAP)
            {
                double fr = Residual(r, probe);
                if (!double.IsNaN(fr))
                {
                    if (Math.Abs(fr) < 1e-14) return r;
                    if (flo * fr < 0.0)
                    {
                        // 括号成立 ⇒ Brent 求根（有界重载）
                        try
                        {
                            return BrentRoot.Solve((x, o) => Residual(x, (Probe)o!), lo, r, probe);
                        }
                        catch (ArgumentException)
                        {
                            return double.NaN;
                        }
                    }

                    lo = r;
                    flo = fr;
                }

                r *= 1.35;
            }

            return double.NaN;
        }

        private static V3 DeltaV(double mu, V3 r, V3 v, double value, Type type, bool optguard)
        {
            if (!Statics.IsFinite(mu))
                throw new ArgumentException("bad mu in ChangeOrbitalElement");
            if (!r.IsFinite())
                throw new ArgumentException("bad r in ChangeOrbitalElement");
            if (!v.IsFinite())
                throw new ArgumentException("bad v in ChangeOrbitalElement");

            Scale scale = Scale.Create(mu, r.magnitude);

            (V3 p0, V3 q0, Q3 rot) = Astro.PerifocalFromStateVectors(mu, r, v);

            var probe = new Probe
            {
                P = p0 / scale.LengthScale,
                Q = q0 / scale.VelocityScale,
                Target = TargetOf(type, type == Type.ECC ? value : value / scale.LengthScale),
                Type = type,
            };

            // ── 外层：θ 扫描（粗）→ BrentMin（细）──
            // 批7 #4（2026-09-30）：180 个方向**彼此独立** ⇒ 并行。独立性有据（逐行读过）：
            //   · `Probe` 是 **struct** 且 `SolveRadiusAlong(V3, Probe)` **按值收** ⇒ 里面那句
            //     `probe.Dir = dir` 改的是**副本**；`Residual` 同样按值收；`BrentRoot` **零静态可变状态**。
            //   · 归约走 `ParallelOps.MinIndex`（跳过 NaN、**并列取小下标**）——与原来串行那句
            //     `if (!NaN(rr) && rr < bestR)`（严格 `<` ⇒ 保留先到的）**同语义、逐位相同**。
            // ⚠ 其后的 `BrentMin` 细化**必须顺序**（它喂粗扫的 argmin；计划稿 §17.4 #4 的告警）。
            var radii = new double[THETA_STEPS];
            ParallelOps.For(THETA_STEPS, 5e-6, i =>
            {
                double theta = 2.0 * Math.PI * i / THETA_STEPS;
                radii[i] = SolveRadiusAlong(new V3(Math.Cos(theta), Math.Sin(theta), 0.0), probe);
            });

            int bestIndex = ParallelOps.MinIndex(radii);
            double bestR = bestIndex >= 0 ? radii[bestIndex] : double.PositiveInfinity;

            if (bestIndex < 0)
                throw new ArgumentException(
                    $"ChangeOrbitalElement: 目标不可达（{THETA_STEPS} 个方向上都找不到满足 {type}={value} 的脉冲）");

            double dTheta = 2.0 * Math.PI / THETA_STEPS;
            double thetaBest = 2.0 * Math.PI * bestIndex / THETA_STEPS;
            double thetaLo = thetaBest - dTheta;
            double thetaHi = thetaBest + dTheta;

            double rBest = bestR;
            double thetaSolved = thetaBest;

            try
            {
                (double x, double fx) = BrentMin.Solve(
                    (theta, o) =>
                    {
                        double rr = SolveRadiusAlong(new V3(Math.Cos(theta), Math.Sin(theta), 0.0), probe);
                        return double.IsNaN(rr) ? double.MaxValue : rr; // 不可达的方向给大值（BrentMin 会绕开它）
                    },
                    thetaLo, thetaHi, probe);

                if (Statics.IsFinite(fx) && fx < rBest)
                {
                    rBest = fx;
                    thetaSolved = x;
                }
            }
            catch (TimeoutException)
            {
                // 细化失败就用粗扫描的结果（它已经是可行解，只是未必最优）
            }

            var dv = new V3(Math.Cos(thetaSolved), Math.Sin(thetaSolved), 0.0) * rBest;

            if (optguard)
                Verify(probe, dv, type, value, scale);

            return rot * dv * scale.VelocityScale;
        }

        /// <summary>
        /// 自检：把求出的 Δv 带回去，确认它**真的**满足约束（残差按无量纲口径算）。
        /// 这是 `optguard` 形参在本实现里的语义 —— 上游那个是 alglib 的梯度/光滑性自检，
        /// 而"解对不对"才是我们真正关心的（且不需要任何新依赖）。
        /// </summary>
        private static void Verify(Probe probe, V3 dv, Type type, double value, Scale scale)
        {
            double res = Residual(dv.magnitude, new Probe { P = probe.P, Q = probe.Q, Target = probe.Target, Type = type, Dir = dv.normalized });
            if (double.IsNaN(res) || Math.Abs(res) > 1e-9)
                throw new ArgumentException(
                    $"ChangeOrbitalElement 自检失败：Δv={dv.magnitude * scale.VelocityScale:F3} m/s 带回去后 {type}={value} 未满足（残差 {res}）");
        }

        // ── 与上游同名的五个入口（签名逐字保持） ──────────────────────────────────
        // ⚠ 批6 S3-3b 判定：`optguard` **不许**改成必需 —— 它是**上游签名**里的自检开关（不是值哨兵：
        //   漏传只是不跑自检，不会静默换一个值）；改必需 = 偏离上游签名。生产路径一直用默认 false，只有测试显式打开。

        public static V3 ChangePeriapsis(double mu, V3 r, V3 v, double peR, bool optguard = false)
        {
            if (peR < 0 || (peR > r.magnitude) | !Statics.IsFinite(peR))
                throw new ArgumentException($"Bad periapsis in ChangeOrbitalElement = {peR}");

            return DeltaV(mu, r, v, peR, Type.PERIAPSIS, optguard);
        }

        public static V3 ChangeApoapsis(double mu, V3 r, V3 v, double apR, bool optguard = false)
        {
            if (!Statics.IsFinite(apR) || (apR > 0 && apR < r.magnitude))
                throw new ArgumentException($"Bad apoapsis in ChangeOrbitalElement = {apR}");

            return DeltaV(mu, r, v, apR, Type.APOAPSIS, optguard);
        }

        public static V3 ChangeApsis(double mu, V3 r, V3 v, double valueR, bool optguard = false)
        {
            if (!Statics.IsFinite(valueR))
                throw new ArgumentException($"Bad apoapsis in ChangeOrbitalElement = {valueR}");

            return valueR <= r.magnitude ? DeltaV(mu, r, v, valueR, Type.PERIAPSIS, optguard) : DeltaV(mu, r, v, valueR, Type.APOAPSIS, optguard);
        }

        public static V3 ChangeSMA(double mu, V3 r, V3 v, double sma, bool optguard = false)
        {
            if (!Statics.IsFinite(sma))
                throw new ArgumentException($"Bad SMA in ChangeOrbitalElement = {sma}");

            return DeltaV(mu, r, v, sma, Type.SMA, optguard);
        }

        public static V3 ChangeECC(double mu, V3 r, V3 v, double ecc, bool optguard = false)
        {
            if (!Statics.IsFinite(ecc) || ecc < 0)
                throw new ArgumentException($"Bad Ecc in ChangeOrbitalElement = {ecc}");

            return DeltaV(mu, r, v, ecc, Type.ECC, optguard);
        }
    }
}
