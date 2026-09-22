using System;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **闭环微调**（`TrimBurn`，B2.2-b）的判据（2026-09-20）。
    ///
    /// 判据的顺序刻意是"先证它算得对，再证它用得上"：
    ///   ① **定义判据**：按构造，打完之后 `(a, h)` 必须等于目标 —— 这是函数的**定义**，
    ///      不是近似（用 vis-viva 与角动量各自反算，不用函数内部那条路径）；
    ///   ② **近圆收口**：给一个 `e=1.3e-3` 的实测残差轨道 + 圆目标 ⇒ `e` 必须掉到 ~1e-12 量级，
    ///      且 Δv 量级 ≈ `v·e/2`（~3 m/s，能对上"实测反推需要的 3.4 m/s"）；
    ///   ③ **半径不匹配时的已知代价**：`a` 落在**本点半径** `r` 而不是 `a_target` ⇒
    ///      残差 = `|r − a_t|`（这条是**登记的局限**，写成判据免得日后被当成 bug 追）。
    ///   ④ 退化输入必须**拒绝并给理由**（不是返回一个零 Δv 让人以为"修好了"）。
    /// </summary>
    public class TrimBurnTests
    {
        private const double Mu = 2.0e14;          // 与游戏一致（OrbitMath.GM）
        private const double R0 = 7_054_650.0;     // 游戏那轮的目标半径（a_expected = 7054.65 km）

        /// <summary>由状态算 (a, |h|) —— 用 vis-viva 与叉积**独立**算，不复用被测函数内部那条路径。</summary>
        private static (double a, double h) Elements(DVector3 r, DVector3 v, double mu)
        {
            double rm = r.magnitude;
            double a = 1.0 / (2.0 / rm - v.sqrMagnitude / mu);
            return (a, DVector3.Cross(r, v).magnitude);
        }

        /// <summary>① 定义判据：打完 (a, h) 就是目标（随机态 × 随机共面目标）。</summary>
        [Fact]
        public void Plan_LandsOnTargetConic()
        {
            var rnd = new Random(20260920);
            int checkedCases = 0;
            for (int i = 0; i < 300; i++)
            {
                double rm = 6.6e6 + rnd.NextDouble() * 2.0e6;
                // 随机椭圆态（保证 a > 0 且在本点可达）
                double a0 = rm * (0.8 + rnd.NextDouble() * 0.4);
                double h0 = rm * Math.Sqrt(Mu * (2.0 / rm - 1.0 / a0)) * (0.3 + 0.6 * rnd.NextDouble());
                if (!(a0 > 0.0) || !(h0 > 0.0)) continue;

                // 本点速度：给一个带径向分量的方向（γ 由 h 自洽）
                double vr = (rnd.NextDouble() * 2 - 1) * 0.3 * Math.Sqrt(Mu / rm);

                if (!TrimBurn.TryPlan(new DVector3(rm, 0, 0), new DVector3(vr, 0.9 * Math.Sqrt(Mu / rm), 0.0),
                        Mu, a0, h0, out DVector3 dv, out _, out double aUsed))
                    continue;

                var vNew = new DVector3(vr, 0.9 * Math.Sqrt(Mu / rm), 0.0) + dv;
                (double aNew, double hNew) = Elements(new DVector3(rm, 0, 0), vNew, Mu);

                // 精确支：目标曲线**容纳**本点 ⇒ aUsed = a0，且 (a, h) 逐位命中
                Assert.True(Math.Abs(aUsed - a0) / a0 < 1e-12,
                    $"本点在目标曲线内 ⇒ 必须走精确支：aUsed={aUsed:F3} vs 目标 {a0:F3}（第 {i} 例）");
                Assert.True(Math.Abs(aNew - a0) / a0 < 1e-9,
                    $"a 应落在目标上：a={aNew:F3} vs 目标 {a0:F3}（第 {i} 例）");
                Assert.True(Math.Abs(hNew - h0) / h0 < 1e-9,
                    $"|h| 应落在目标上：h={hNew:F3} vs 目标 {h0:F3}（第 {i} 例）");
                checkedCases++;
            }
            Assert.True(checkedCases > 100, $"有效算例太少（{checkedCases}）⇒ 判据没被真正跑到");
        }

        /// <summary>② 近圆收口：实测残差轨道（e=1.3e-3）⇒ e 归零，Δv ≈ v·e/2。</summary>
        [Fact]
        public void Plan_NearCircularResidual_IsNulled()
        {
            // 游戏 G4 实测的终态口径：a=7062 km、e=1.28e-3（近圆、本点接近近心点）
            double aNow = 7_062_000.0;
            double eNow = 0.00128;
            double rNow = aNow * (1.0 - eNow);                    // 近心点附近
            double vt = Math.Sqrt(Mu * (2.0 / rNow - 1.0 / aNow));
            double vr = Math.Sqrt(Mu / aNow) * eNow * 0.9;        // 近心点附近径向分量很小

            var r = new DVector3(rNow, 0, 0);
            var v = new DVector3(vr, vt, 0);

            // 目标 = 圆轨（h_t = r·v_c、a_t = 本点半径）⇒ 这就是"在当前点圆化"
            double vc = Math.Sqrt(Mu / rNow);
            Assert.True(TrimBurn.TryPlan(r, v, Mu, rNow, rNow * vc, out DVector3 dv, out string why, out _), why);

            Assert.InRange(dv.magnitude, 0.5, 10.0);              // 量级：v·e/2 ≈ 3.4 m/s
            var vNew = v + dv;
            double eNew = Math.Abs(1.0 - DVector3.Cross(r, vNew).magnitude * DVector3.Cross(r, vNew).magnitude / (Mu * rNow));
            Assert.True(eNew < 1e-10, $"e 应归零，实测 {eNew:E3}");
            Assert.True(Math.Abs(vNew.magnitude - vc) / vc < 1e-12, "圆化后速度必须是本点圆轨速度");
        }

        /// <summary>③ 登记的局限：**不修轨道面** —— 面外偏差原样留下（这条写成判据免得日后被当成 bug 追）。</summary>
        [Fact]
        public void Plan_DoesNotFixOrbitalPlane()
        {
            double rNow = 7_053_000.0;
            double vc = Math.Sqrt(Mu / rNow);
            var r = new DVector3(rNow, 0, 0);
            // 速度带 1° 面外分量（倾角误差）
            double iDeg = 1.0;
            var v = new DVector3(0.5, vc * 1.001 * Math.Cos(iDeg * Math.PI / 180.0), vc * 1.001 * Math.Sin(iDeg * Math.PI / 180.0));
            double iBefore = Inclination(r, v);

            Assert.True(TrimBurn.TryPlan(r, v, Mu, rNow, rNow * vc, out DVector3 dv, out string why, out _), why);
            double iAfter = Inclination(r, v + dv);

            Assert.True(Math.Abs(iAfter - iBefore) < 1e-9,
                $"微调**不该**动轨道面：i {iBefore:F6}° → {iAfter:F6}°");
        }

        private static double Inclination(DVector3 r, DVector3 v)
        {
            DVector3 h = DVector3.Cross(r, v);
            return Math.Acos(Math.Max(-1.0, Math.Min(1.0, h.z / h.magnitude))) * 180.0 / Math.PI;
        }

        /// <summary>④ 退化输入必须被拒绝并给理由（不能返回零 Δv 冒充"已经修好"）。</summary>
        [Fact]
        public void Plan_RejectsDegenerateInputs()
        {
            var r = new DVector3(R0, 0, 0);
            var v = new DVector3(0, Math.Sqrt(Mu / R0), 0);

            Assert.False(TrimBurn.TryPlan(r, v, Mu, -1.0, 1e10, out _, out string why1, out _));
            Assert.False(string.IsNullOrEmpty(why1));
            Assert.False(TrimBurn.TryPlan(r, v, Mu, R0, 0.0, out _, out string why2, out _));
            Assert.False(string.IsNullOrEmpty(why2));
            Assert.False(TrimBurn.TryPlan(DVector3.zero, v, Mu, R0, 1e10, out _, out string why4, out _));
            Assert.False(string.IsNullOrEmpty(why4));
            // 双曲意图（h² > μa）⇒ 拒绝（微调不接逃逸轨道；见 `TrimBurn` 的守卫注释）
            Assert.False(TrimBurn.TryPlan(r, v, Mu, R0 / 2.0, 1e13, out _, out string why5, out _));
            Assert.False(string.IsNullOrEmpty(why5));
            // ⚠ **登记**：任何**椭圆**形状 + 任意本点都会给出解（退化支会给出很野的解，如上例 a 落到 9.5e5 km）
            //   ⇒ "合理不合理"由**调用方的 Δv 合理带**兜（`SpaceGameComp.TrimDvMin/Max`），不在这里判 ✓。
        }

        /// <summary>⑥ **退化支**（游戏首轮实测踩到的正是这一支）：目标是**圆**（e_t=0）而本点半径 ≠ a_t。
        /// 圆只有一个半径 ⇒ 精确解不存在；必须**保形状（e_t）、把尺度锚到本点** ⇒ 本点圆化、a 落在 r。
        /// ⚠ 这条判据同时钉住"守卫不再误拒"（首轮失败形态：`目标角动量与本点几何不相容`）。</summary>
        [Fact]
        public void Plan_CircularTargetOffRadius_DegradesToCircularizeAtCurrentRadius()
        {
            double aTarget = 7_054_650.0;
            double hTarget = Math.Sqrt(Mu * aTarget);              // 意图 = 半径 a_t 的圆
            double rNow = 7_061_970.0;                             // 本点在外（游戏实测 a = 7061.97 km 同量级）
            double vc = Math.Sqrt(Mu / rNow);
            var r = new DVector3(rNow, 0, 0);
            var v = new DVector3(2.1, vc * 1.0004, 0);             // 略欠圆（残差 ~1e-3 量级）

            Assert.True(TrimBurn.TryPlan(r, v, Mu, aTarget, hTarget, out DVector3 dv, out string why, out double aUsed), why);
            Assert.True(Math.Abs(aUsed - rNow) < 1.0, $"退化支应把尺度锚到本点半径 {rNow:F0}，实测 aUsed={aUsed:F1}");
            Assert.True(Math.Abs(aUsed - aTarget) > 1.0, "这一例本该是退化支（本点半径与 a_t 差公里级）");

            var vNew = v + dv;
            (double aNew, double hNew) = Elements(r, vNew, Mu);
            double eNew = Math.Sqrt(Math.Max(0.0, 1.0 - hNew * hNew / (Mu * aNew)));
            // 下限是**目标自身的浮点离心率**（圆目标的 `h²/(μa)` 在浮点上 ≈1 ⇒ e_t ~1e-8）
            // ⇒ 微调把意图形状复现到机器精度（相对基线 1.28e-3 好 5 个量级），这就是这一支的全部承诺。
            Assert.True(eNew < 1e-7, $"退化支修的是 e ⇒ 应到目标形状的机器精度，实测 {eNew:E3}");
            Assert.True(Math.Abs(aNew - rNow) < 1.0, $"a 落在本点半径，实测 {aNew:F1}");
            Assert.InRange(dv.magnitude, 0.05, 20.0);              // 量级合理（能进执行侧的合理带）
        }

        /// <summary>⑤ 已经在目标轨道上 ⇒ Δv ≈ 0（幂等；不然微调会自我激发）。</summary>
        [Fact]
        public void Plan_AlreadyOnTarget_IsNoOp()
        {
            double rm = R0;
            double vc = Math.Sqrt(Mu / rm);
            Assert.True(TrimBurn.TryPlan(new DVector3(rm, 0, 0), new DVector3(0, vc, 0),
                Mu, rm, rm * vc, out DVector3 dv, out string why, out _), why);
            Assert.True(dv.magnitude < 1e-9, $"已达标时 Δv 应为零，实测 {dv.magnitude:E3}");
        }
    }
}
