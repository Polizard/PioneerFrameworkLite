using MechJebLib.Functions;
using MechJebLib.Primitives;
using Xunit;
using static System.Math;

namespace MechJebLibTest.FunctionsTests
{
    /// <summary>
    /// **P2（§13 极轴统一 +Y）的判别金丝雀** —— 2026-09-28 加。
    ///
    /// ## 为什么不能用现成的测试当判据
    /// · `KeplerianFromStateVectorsTests.RandomOrbitalElementsForwardAndBack`（501 条）是**往返**
    ///   （`elements → state → elements`）⇒ **对"配对错误"是盲的**（实测：P1 之后它照样绿 ✗）。
    /// · 同文件的 `[Fact]`（`…CircularEquatorial*`）虽手算，但取的是**赤道轨道** ⇒ `lan`/`argp` 在
    ///   `i = 0/180°` 上**本来就没有定义**（0、π/2、π 都只是约定）⇒ 判不了"极轴翻没翻对" ✗。
    ///
    /// ## 本测试取的判别例（§13.2 B1 点名的那个）
    /// `lan = 0, inc = 30°, argp = 0` —— **非退化**：节点线有定义（= +x）⇒ `lan = 0` 无歧义。
    /// 文档给的签名：**裸把 `[2]→[1]` 会让它给出 `lan = π` 而不是 `0`** ⇒ 本测试正盯这一条。
    ///
    /// ## 几何是**手推的常量**（不调用任何未修的帮手）
    /// 新框架（+Y 极轴）下：
    ///   `ĥ(inc=30°, lan=0) = (0, cos30°, sin30°) = (0, √3/2, 1/2)`
    ///   `ê(argp=0, lan=0)  = (1, 0, 0)`（近地点沿 +x）
    ///   `q̂ = ĥ × ê`
    /// ⇒ `r = r·(cos nu·ê + sin nu·q̂)`，`v = √(μ/p)·(−sin nu·ê + (e+cos nu)·q̂)`
    /// ⚠ **不调用** `PerifocalToECIMatrix`/`EvecFromKeplerian`（B2 尚未修）⇒ 与实现相互独立
    ///   （本库纪律：自检不得与实现同错）。这条独立性正是本测试能当"金丝雀"的原因。
    /// </summary>
    public class PoleFlipDiscriminatorTests
    {
        private const double Mu = 1.0, Ecc = 0.5, P = 0.75;   // p = a(1−e²)，a = 1

        /// <summary>由"手搓的基矢 + 真近点角"造状态矢量（新框架）。</summary>
        private static (V3 r, V3 v) StateFromBasis(V3 ehat, V3 qhat, double nu)
        {
            double rmag = P / (1.0 + Ecc * Cos(nu));
            double vmag = Sqrt(Mu / P);
            V3 r = rmag * (Cos(nu) * ehat + Sin(nu) * qhat);
            V3 v = vmag * (-Sin(nu) * ehat + (Ecc + Cos(nu)) * qhat);
            return (r, v);
        }

        /// <summary>★ 判别位：`lan = 0` 必须是 **0**，不是 π。</summary>
        [Fact]
        public void LanZeroAtInclination30_IsZeroNotPi()
        {
            const double nu = PI / 6.0;
            var hhat = new V3(0.0, Sqrt(3.0) / 2.0, 0.5);        // inc=30°, lan=0
            var ehat = new V3(1.0, 0.0, 0.0);                    // argp=0, lan=0
            var qhat = V3.Cross(hhat, ehat).normalized;

            (V3 r, V3 v) = StateFromBasis(ehat, qhat, nu);
            (double _, double ecc, double inc, double lan, double argp, double nuOut, double _) =
                Astro.KeplerianFromStateVectors(Mu, r, v);

            Assert.Equal(Ecc, ecc, 9);
            Assert.Equal(30.0 * PI / 180.0, inc, 9);   // 与极轴约定无关 ⇒ 改前改后都必须对
            Assert.Equal(0.0, argp, 9);
            Assert.Equal(nu, nuOut, 9);
            Assert.Equal(0.0, lan, 9);                 // ★ 裸换分量 ⇒ 这里会得到 π ✗
        }

        /// <summary>第二个判别例：`lan = 60°, inc = 30°, argp = 0`（`lan` 非零 ⇒ 节点线方向也被钉住）。</summary>
        [Fact]
        public void LanSixtyAtInclination30_IsSixty()
        {
            const double nu = PI / 6.0, lan = PI / 3.0, inc = PI / 6.0;
            // 新框架下的 HunitFromKeplerian（P1 已换元）与"节点方向 = 近地点方向（argp=0）"
            var hhat = new V3(Sin(lan) * Sin(inc), Cos(inc), Cos(lan) * Sin(inc));
            var ehat = new V3(Cos(lan), 0.0, -Sin(lan));
            var qhat = V3.Cross(hhat, ehat).normalized;

            (V3 r, V3 v) = StateFromBasis(ehat, qhat, nu);
            (double _, double ecc, double incOut, double lanOut, double argp, double nuOut, double _) =
                Astro.KeplerianFromStateVectors(Mu, r, v);

            Assert.Equal(Ecc, ecc, 9);
            Assert.Equal(inc, incOut, 9);
            Assert.Equal(lan, lanOut, 9);
            Assert.Equal(0.0, argp, 9);
            Assert.Equal(nu, nuOut, 9);
        }

        // ── B2 的独立仪器（2026-09-28）──────────────────────────────────────────────
        /// <summary>
        /// **B2 的判据**：`PerifocalToECIMatrix(inc, argp, lan)` 的三列必须 = 近焦点基 `(P,Q,W)`
        /// **在新框架（+Y 极轴）里的分量**。
        ///
        /// 这里的 P/Q/W **用初等几何手推**（不碰实现里任何公式 —— `EvecFromKeplerian` 等一个都不调）：
        ///   · `ĥ = (sin lan·sin inc, cos inc, cos lan·sin inc)`（球面标准式，新框架极轴 = +Y）
        ///   · `n̂ = (cos lan, 0, −sin lan)`（节点线：赤道面是 x–z 面，方位从 +x 转向 −z）
        ///   · `ê = n̂` 绕 `ĥ` 转 `argp`（Rodrigues 公式，独立写）
        ///   · `q̂ = ĥ × ê`
        /// 于是"矩阵 × 基矢"必须回到基矢本身。**裸换轴名**（少那个右乘 `M⁻¹`）会在这里当场露馅。
        /// </summary>
        [Theory]
        [InlineData(0.5, 0.0, 0.0)]      // inc=30°, argp=0, lan=0
        [InlineData(0.5, 0.3, 0.0)]      // + argp 非零
        [InlineData(0.5, 0.0, PI / 3.0)] // + lan 非零
        [InlineData(0.7, 0.9, 2.1)]      // 一般位置
        public void PerifocalMatrix_ColumnsMatchHandDerivedBasis(double inc, double argp, double lan)
        {
            // ① 手推基（初等几何，与实现无关）
            var hhat = new V3(Sin(lan) * Sin(inc), Cos(inc), Cos(lan) * Sin(inc));
            hhat = hhat.normalized;
            var node = new V3(Cos(lan), 0.0, -Sin(lan));
            V3 ehat = Rodrigues(node, hhat, argp).normalized;
            V3 qhat = V3.Cross(hhat, ehat).normalized;

            // ② 被测物
            Q3 rot = Astro.PerifocalToECIMatrix(inc, argp, lan);

            // ③ 矩阵的列 = 基矢在目标系里的分量 ⇒ rot 应当把 (1,0,0)/(0,1,0)/(0,0,1) 分别送成 P/Q/W
            (rot * new V3(1, 0, 0)).ShouldEqual(ehat, 1e-9);
            (rot * new V3(0, 1, 0)).ShouldEqual(qhat, 1e-9);
            (rot * new V3(0, 0, 1)).ShouldEqual(hhat, 1e-9);
        }

        /// <summary>`v` 绕单位轴 `k` 转 `θ`（Rodrigues，独立于实现）。</summary>
        private static V3 Rodrigues(V3 v, V3 k, double theta) =>
            v * Cos(theta) + V3.Cross(k, v) * Sin(theta) + k * (V3.Dot(k, v) * (1.0 - Cos(theta)));
    }
}
