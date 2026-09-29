using MechJebLib.Functions;
using MechJebLib.ODE;
using MechJebLib.Primitives;
using MechJebLib.TwoBody;
using MechJebLib.Utils;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 第三方移植面 ↔ 我们自己的代码 的**跨求解器互证台**（《MechJeb参考大修计划》§12.3 的 L3 层）。
    ///
    /// ⚠ 本文件**是本项目的代码，不是移植物**。它从 `Source/PioneerFramework.Tests/` 搬进
    /// `Source/MechJebLibTest/`（原因为了让"移植面及其验收"集中在一个桶里）；
    /// 本工程为此额外链接了我们的纯算术源码（`DVector3` / `SpaceConstants` / `OrbitMath`，见该工程 csproj）。
    ///
    /// **它存在的理由**（《P-1适配层决策》§6 的 R1）：移植最大的风险不是抄错（那有逐字核验兜着），
    /// 而是**约定/手性对不上**——一旦参考系的旋转方向与两体公式的假设相反，
    /// **每一个移植物都会给出"看起来合理"的错数**，而且不会有任何东西报警。
    ///
    /// 三层互证（同一 `(r,v,μ)` 下多方必须给同一个答案）：
    ///   ① 移植的 `Astro`/`Shepperd` ↔ 我们的 `OrbitMath` ↔ **上游自己的 `DP5` 自适应积分器**（数值第三方）
    ///   ② ⭐ **旋转方向（R1 的本体）**：`r(τ)` 必须绕 `h = r×v` 按**右手正向**转
    ///
    /// **关于数值第三方**：这里**不再自己写积分器**。先前版本是一段手搓 RK4 —— 那与项目方法论冲突
    /// （§11.3：§23–§29 七连败的根因正是"每个求解器都现推、靠手工护栏兜着"），
    /// 当时只是因为上游 `ODE/` 还没移植。现在 `ODE/` 已逐字移植（`ThirdParty/MechJebLib/ODE/`），
    /// 且**上游 `TwoBodyTests` 已在同一工程里跑起来**（用同一个 `DP5` 做参照）⇒ 手搓那段已删除。
    ///
    /// ⚠ **本文件只在 net48 可编译**（与 `ThirdParty/MechJebLib` 同因：`Angles.cs` 靠 net48 没有
    /// `Math.Asinh/Acosh/Atanh` 才写得通）。见 `ThirdParty/MechJebLib/README.md`。
    /// </summary>
    public class MechJebLibPortTests
    {
        private static readonly double Mu = OrbitMath.GM;

        /// <summary>近圆工作轨道半径（米）。取在行星半径 5000km 之上。</summary>
        private const double R0 = 7_000_000.0;

        // 批5 U4（2026-09-28）：本地那两个"搬运三个分量"的恒等转换助手已删
        // （类型统一后移植面那个类型就是 `DVector3`）。

        /// <summary>`r = (R,0,0)`、`v = (0, vc, 0)` ⇒ `h = r×v` 指向 **+z**。</summary>
        private static void CircularState(double radius, out V3 r, out V3 v)
        {
            r = new V3(radius, 0, 0);
            v = new V3(0, System.Math.Sqrt(Mu / radius), 0);
        }

        /// <summary>偏心率可控的共面轨道：`e = v²R/μ − 1`（因 `r·v = 0`）。</summary>
        private static void RadialPerpendicularState(double radius, double speed, out V3 r, out V3 v)
        {
            r = new V3(radius, 0, 0);
            v = new V3(0, speed, 0);
        }

        // ── 仪器：上游的数值积分器（第三方，独立于两方实现） ──────────────────────

        /// <summary>
        /// 二体问题右端项。**镜像上游 <c>ShepperdTests.KeplerODE</c> 的形状**（同样的 6 维状态、同样的 `Vec` 池化接口），
        /// 差别只在于上游那里 μ=1 是硬编码的，这里因为走游戏量级所以放到无量纲化里处理。
        /// </summary>
        private sealed class KeplerOde
        {
            public const int N = 6;

            public void Rhs(Vec yin, double x, Vec dyout)
            {
                var r = new V3(yin[0], yin[1], yin[2]);
                var v = new V3(yin[3], yin[4], yin[5]);

                double rm2 = r.sqrMagnitude;
                double rm = System.Math.Sqrt(rm2);
                double rm3 = rm2 * rm;

                dyout.Set(0, v);
                dyout.Set(3, r * (-1.0 / rm3));
            }
        }

        /// <summary>
        /// 用**上游的 `DP5`** 传播二体问题，作为互证里的数值第三方。
        ///
        /// 先把状态无量纲化（长度 ÷ R₀、速度 ÷ √(μ/R₀)、时间 ÷ R₀/√(μ/R₀)），使 μ=1、`|r|` 为 O(1)
        /// ——**与上游测试同一量级 regime**。这一步不是装饰：上游的 `Hmin = EPS` 是绝对量，
        /// 直接把 μ=2e14、r=7e6 的状态丢进去会让求解器工作在完全不同的尺度上。
        /// </summary>
        private static (V3 rf, V3 vf) IntegrateTwoBody(V3 r0, V3 v0, double tau)
        {
            double vs = System.Math.Sqrt(Mu / R0); // 速度尺度
            double ts = R0 / vs;                   // 时间尺度

            var solver = new DP5 { Rtol = 1e-9, Hmin = Statics.EPS, ThrowOnMaxIter = true, Maxiter = 5000 };
            var ode = new KeplerOde();

            using (var y0 = Vec.Rent(KeplerOde.N))
            using (var yf = Vec.Rent(KeplerOde.N))
            {
                y0.Set(0, r0 * (1.0 / R0));
                y0.Set(3, v0 * (1.0 / vs));

                solver.Solve(ode.Rhs, y0, yf, 0, tau / ts);

                return (yf.Get(0) * R0, yf.Get(3) * vs);
            }
        }

        private static void AssertVecRel(DVector3 actual, DVector3 expected, double relTol, string what)
        {
            double scale = System.Math.Max(expected.magnitude, 1.0);
            double err = (actual - expected).magnitude / scale;
            Assert.True(err <= relTol,
                $"{what}: 相对误差 {err:E3} 超出容差 {relTol:E3}\n  actual   = ({actual.x:R},{actual.y:R},{actual.z:R})\n  expected = ({expected.x:R},{expected.y:R},{expected.z:R})");
        }

        // 批5 U3（2026-09-28）：原先这里还有一个 `AssertVecRel(V3, V3, …)` 转发重载 ——
        // 类型统一后 `V3` 就是 `DVector3`，两个重载签名相同（CS0111）⇒ 删掉转发那份。
        // ── ② ⭐ R1 本体：旋转方向 ────────────────────────────────────────────────

        /// <summary>
        /// ⭐ **本互证台存在的理由**。两体公式全部隐含"位置绕角动量矢量按右手正向进动"这一约定。
        /// 若参考系手性、或某个移植的符号写反，后面每一个移植物都会静默给出反向的轨道。
        ///
        /// 判据用可直接心算的形态：圆轨 `r=(R,0,0)`、`v=(0,vc,0)` ⇒ `h=+z`；
        /// 则 **`r(T/4)` 必须是 `(0,R,0)`**（绕 +z 逆时针 90°），而不是 `(0,-R,0)`。
        /// </summary>
        [Fact]
        public void R1_RotationSense_IsRightHanded_ForPortedCode()
        {
            CircularState(R0, out V3 r, out V3 v);

            // 前置：角动量确实指向 +z（这一步若红，说明约定在更外层就错了）
            V3 h = V3.Cross(r, v);
            Assert.True(h.z > 0, $"预期 h 指向 +z，实得 h=({h.x:R},{h.y:R},{h.z:R})");

            double period = Astro.PeriodFromStateVectors(Mu, r, v);
            double quarter = period * 0.25;

            // ① 移植的 Shepperd —— 这是 R1 真正要钉的东西：移植物必须按"右手绕 h 正向"进动
            (V3 rf, V3 vf) = Shepperd.Solve(Mu, quarter, r, v);
            AssertVecRel(rf, new DVector3(0, R0, 0), 1e-9, "Shepperd r(T/4)（应为 +z 旋转 90°）");
            AssertVecRel(vf, new DVector3(-System.Math.Sqrt(Mu / R0), 0, 0), 1e-9, "Shepperd v(T/4)");

            // ② 反向对照：T/4 **反**着走必须落在 (0,-R0,0) —— 钉死"不是碰巧落在 +y"
            (V3 rb, V3 _) = Shepperd.Solve(Mu, -quarter, r, v);
            AssertVecRel(rb, new DVector3(0, -R0, 0), 1e-9, "Shepperd r(-T/4)（应为 -y）");

            // ③ 上游数值积分器独立复核同一条约定（它不共享 Shepperd 的代码）
            (V3 rn, V3 _) = IntegrateTwoBody(r, v, quarter);
            AssertVecRel(rn, new V3(0, R0, 0), 1e-6, "DP5 r(T/4)（应为 +z 旋转 90°）");

            // ④ 我们自己的传播：**已知缺陷已修**（`Shepperd` 取代，见 §14.3『传播』行），
            //    所以在这里也断言同一件事 —— 两条路径必须给出同一个 +z 旋转 90°。
            AssertVecRel(OrbitMath.GetPositionAtTimeDelta(r, v, quarter, Mu),
                new DVector3(0, R0, 0), 1e-6, "我们的传播 r(T/4)（应为 +z 旋转 90°）");
        }

        /// <summary>
        /// ⭐ **圆轨传播的回归守卫（2026-09-18 由 pin 翻正）**。
        ///
        /// **它替代的那条 pin 曾经红过——而那次红正是它的任务。** 原始记录：
        /// `OrbitMath.GetTrueAnomaly` 用 `cos ν = (e⃗·r⃗)/(e·r)` 求真近点角，圆轨上 `e` 与 `e⃗` 同为舍入噪声
        /// ⇒ 比值是任意数；而 `GetPositionAtTimeDelta` 的 `e &lt; 0.001` 分支里轨道基 `eDir` 是以**当前点**
        /// 为 ν=0 锚定的，却把那个噪声值当 `currentNu` 加进相位 ⇒ **输出被整体旋转一个任意常数角**。
        /// 最干净的复现是 **τ=0 不返回输入**。
        ///
        /// **1996-09-18 的处置**：§14.3『传播』行裁定由 `Shepperd` **取代**（不是就地打补丁）。
        /// 两个方法已改成薄转发 ⇒ **椭圆专用分支与 Q11 本身都已删除**，唯一实现是普适变量法。
        /// 于是本条从"钉住缺陷"翻成"守住修复后的不变式"：
        ///
        /// ① **τ=0 是恒等**（最朴素、也最不可能骗人的判据）；
        /// ② **旋转方向正确**：`r(T/4)` 必须是 `(0,+R₀,0)`（绕 `h = +z` 右手正向转 90°）；
        /// ③ **近圆/圆轨上良定**：与移植的 `Shepperd` 逐位一致；
        /// ④ **Q11 已根治**：双曲状态**真的会被传播**，不再是"静默原样返回输入"。
        /// </summary>
        [Fact]
        public void CircularOrbitPropagation_IsExact_AndHyperbolicIsNoLongerANoOp()
        {
            CircularState(R0, out V3 r, out V3 v);
            DVector3 rv = r, vv = v;

            // ① τ = 0 必须是恒等
            AssertVecRel(OrbitMath.GetPositionAtTimeDelta(rv, vv, 0.0, Mu), rv, 1e-12, "τ=0 位置");
            AssertVecRel(OrbitMath.GetVelocityAtTimeDelta(rv, vv, 0.0, Mu), vv, 1e-12, "τ=0 速度");

            // ② 旋转方向：r(T/4) 必须是 (0, +R₀, 0)，而不是 (0, -R₀, 0)（旧缺陷给出的正是后者）
            double period = Astro.PeriodFromStateVectors(Mu, r, v);
            AssertVecRel(OrbitMath.GetPositionAtTimeDelta(rv, vv, period * 0.25, Mu),
                new DVector3(0, R0, 0), 1e-6, "圆轨 r(T/4)（应为 +z 旋转 90°）");

            // ③ 与移植的 Shepperd 逐位一致（同一份实现 ⇒ 这条其实是"转发没转错"）
            foreach (double frac in new[] { 0.1, 0.25, 0.5, 1.0, -0.35 })
            {
                (V3 sp, V3 sv) = Shepperd.Solve(Mu, period * frac, r, v);
                AssertVecRel(OrbitMath.GetPositionAtTimeDelta(rv, vv, period * frac, Mu), sp, 1e-9,
                    $"与 Shepperd 位置一致（{frac} 圈）");
                AssertVecRel(OrbitMath.GetVelocityAtTimeDelta(rv, vv, period * frac, Mu), sv, 1e-9,
                    $"与 Shepperd 速度一致（{frac} 圈）");
            }

            // ④ Q11：双曲状态**必须真的被传播**。旧实现在 `a<=0 || e>=1` 时原样返回输入（no-op），
            //    所以"位置变了"本身就是这条的判据（用解析可算的位移量核对，不只和 Shepperd 自洽）。
            double vEsc = System.Math.Sqrt(2.0 * Mu / R0);
            var hypR = new V3(R0, 0, 0);
            var hypV = new V3(0, vEsc * 1.4, 0);
            const double tau = 1000.0;
            DVector3 hypAfter = OrbitMath.GetPositionAtTimeDelta(hypR, hypV, tau, Mu);
            double moved = (hypAfter - hypR).magnitude;
            Assert.True(moved > 1e6,
                $"双曲状态传播 {tau}s 后位移应远大于 0（旧实现在这里是 no-op），实得 {moved:E3} m");
            (V3 np, V3 _) = IntegrateTwoBody(hypR, hypV, tau);
            AssertVecRel(hypAfter, np, 1e-6, "双曲传播 vs DP5（Q11 根治）");
        }

        // ── ① 周期：移植 ↔ 我们 ↔ 数值 ───────────────────────────────────────────

        /// <summary>
        /// 周期三方一致：`Astro.PeriodFromStateVectors`（移植） ↔ `2π√(a³/μ)`（我们的 `SemiMajorAxis` 给出 a）
        /// ↔ **数值**：走满一个周期必须回到出发点，走半个周期必须到对跖点。
        /// 第三方（数值）出手是为了排除"两方用同一条错算式"这种共模错误。
        /// </summary>
        [Fact]
        public void Period_AgreesAcrossPorted_Ours_AndNumerical()
        {
            CircularState(R0, out V3 r, out V3 v);

            double portedPeriod = Astro.PeriodFromStateVectors(Mu, r, v);

            double a = OrbitMath.SemiMajorAxis(r, v, Mu);
            double analytic = 2.0 * System.Math.PI * System.Math.Sqrt(a * a * a / Mu);

            Assert.True(System.Math.Abs(portedPeriod - analytic) / analytic < 1e-12,
                $"移植周期 {portedPeriod:R} vs 解析 {analytic:R}");

            // 数值：走满一圈 ⇒ 回到出发点
            (V3 full, V3 _) = IntegrateTwoBody(r, v, portedPeriod);
            AssertVecRel(full, r, 1e-6, "DP5 走满一个周期应回到起点");

            // 数值：走半圈 ⇒ 对跖点
            (V3 half, V3 _) = IntegrateTwoBody(r, v, portedPeriod * 0.5);
            AssertVecRel(half, new V3(-R0, 0, 0), 1e-6, "DP5 走半个周期应到对跖点");

            // 移植侧同样走满一圈（比数值参照更紧：这是它自己的自洽性）
            (V3 fullS, V3 _) = Shepperd.Solve(Mu, portedPeriod, r, v);
            AssertVecRel(fullS, r, 1e-8, "Shepperd 走满一个周期应回到起点");
        }

        // ── ② 传播：移植 ↔ 我们 ───────────────────────────────────────────────────

        /// <summary>
        /// 椭圆轨道上的逐点互证。**这是"两个独立实现给同一个答案"的直接形式**：
        /// `Shepperd`（上游的普适变量法）与 `OrbitMath.GetPositionAtTimeDelta`（我们自研的椭圆专用解）
        /// 在同一 `(r,v,τ)` 下必须一致，含跨周期采样（τ &gt; T）与负 τ（反推）。
        /// </summary>
        [Theory]
        [InlineData(0.1)]
        [InlineData(0.25)]
        [InlineData(0.5)]
        [InlineData(1.0)]
        [InlineData(2.5)]
        [InlineData(-0.35)]
        public void Propagation_AgreesBetweenPortedAndOurs(double periodFraction)
        {
            // 偏心率 0.3 的椭圆（在 r=(R,0,0)、r·v=0 的族里：e = v²R/μ − 1 = 0.3 ⇒ v² = 1.3μ/R）
            RadialPerpendicularState(R0, System.Math.Sqrt(1.3 * Mu / R0), out V3 r, out V3 v);

            double period = Astro.PeriodFromStateVectors(Mu, r, v);
            double tau = period * periodFraction;

            (V3 rf, V3 vf) = Shepperd.Solve(Mu, tau, r, v);
            DVector3 ourR = OrbitMath.GetPositionAtTimeDelta(r, v, tau, Mu);
            DVector3 ourV = OrbitMath.GetVelocityAtTimeDelta(r, v, tau, Mu);

            // 相对容差按 r 的量级取；τ 越大累积差越大，2.5 圈仍应在 1e-6 内
            AssertVecRel(ourR, rf, 1e-6, $"位置 τ={tau:R}s（{periodFraction} 圈）");
            AssertVecRel(ourV, vf, 1e-6, $"速度 τ={tau:R}s（{periodFraction} 圈）");
        }

        /// <summary>
        /// 数值第三方独立复核（不依赖我们与上游任何一方）：`Shepperd` 与上游 `DP5` 在同一椭圆轨道上必须一致。
        /// </summary>
        [Fact]
        public void Propagation_AgreesWithNumericalIntegration()
        {
            RadialPerpendicularState(R0, System.Math.Sqrt(1.3 * Mu / R0), out V3 r, out V3 v);
            double tau = Astro.PeriodFromStateVectors(Mu, r, v) * 0.4;

            (V3 rf, V3 vf) = Shepperd.Solve(Mu, tau, r, v);
            (V3 nr, V3 nv) = IntegrateTwoBody(r, v, tau);

            AssertVecRel(rf, nr, 1e-6, "Shepperd vs DP5 位置");
            AssertVecRel(vf, nv, 1e-6, "Shepperd vs DP5 速度");
        }

        // ── Q11：双曲上的传播（移植的直接收益） ────────────────────────────────────

        /// <summary>
        /// 上游 `Shepperd` 用**普适变量**，天然覆盖双曲/抛物；我们自研的 `OrbitMath.GetPositionAtTimeDelta`
        /// 只有椭圆解（`GetOrbitFrame` 在 `a&lt;=0 || e&gt;=1` 时**原样返回输入**，静默 no-op —— 登记为 Q11）。
        ///
        /// 本用例只断言**移植这一侧是对的**（与 `DP5` 互证 + 守恒量），
        /// 不把 Q11 的错误行为写成断言 —— 那样 Q11 被修好时这条会莫名其妙变红。
        /// </summary>
        [Fact]
        public void PortedPropagation_IsValidOnHyperbolicOrbits_Q11Benefit()
        {
            double vEsc = System.Math.Sqrt(2.0 * Mu / R0);
            RadialPerpendicularState(R0, vEsc * 1.4, out V3 r, out V3 v);

            // 前置：确认这条轨道确实是双曲的（e = v²R/μ − 1 = 2.92）
            double e = Astro.EccFromStateVectors(Mu, r, v);
            Assert.True(e > 1.0, $"前置失败：期望双曲轨道，实得 e={e:R}");

            const double tau = 1000.0;
            (V3 rf, V3 vf) = Shepperd.Solve(Mu, tau, r, v);

            // 与第三方数值传播互证
            (V3 nr, V3 nv) = IntegrateTwoBody(r, v, tau);
            AssertVecRel(rf, nr, 1e-6, "Shepperd vs DP5 位置（双曲）");
            AssertVecRel(vf, nv, 1e-6, "Shepperd vs DP5 速度（双曲）");

            // 守恒量：比机械能与角动量必须逐位保住
            double energyBefore = v.sqrMagnitude * 0.5 - Mu / r.magnitude;
            double energyAfter = vf.sqrMagnitude * 0.5 - Mu / rf.magnitude;
            Assert.True(System.Math.Abs(energyAfter - energyBefore) / System.Math.Abs(energyBefore) < 1e-12,
                $"比机械能不守恒：{energyBefore:R} → {energyAfter:R}");

            V3 hBefore = V3.Cross(r, v);
            V3 hAfter = V3.Cross(rf, vf);
            AssertVecRel(hAfter, hBefore, 1e-12, "角动量不守恒（双曲）");
        }

        // ── Angles：我们只有椭圆子集，移植版是全集 ────────────────────────────────

        /// <summary>
        /// 移植的 `Angles` 覆盖椭圆（E）、双曲（F）、近抛物（D）三支；
        /// 我们原来的 `OrbitMath` 只有椭圆子集（`TrueToEccentricAnomaly` / `EccentricToTrueAnomaly`）。
        /// 判据 = **每个换算的往返必须闭合**（这是可被违反的：任一支的符号或定义域写错都会让它红）。
        /// </summary>
        [Fact]
        public void Angles_RoundTripsClose_OnAllThreeFamilies()
        {
            // 椭圆：ν → E → ν
            foreach (double ecc in new[] { 0.0, 0.3, 0.9 })
            {
                foreach (double nu in new[] { -2.0, -0.5, 0.0, 0.7, 2.5 })
                {
                    double E = Angles.EFromNu(nu, ecc);
                    double back = Angles.NuFromE(E, ecc);
                    Assert.True(System.Math.Abs(back - nu) < 1e-11, $"椭圆 ν→E→ν 不闭合: e={ecc} ν={nu} → {back}");
                }
            }

            // 椭圆：M → E → M
            foreach (double ecc in new[] { 0.0, 0.3, 0.9 })
            {
                foreach (double M in new[] { 0.1, 1.0, 3.0, 6.0 })
                {
                    double E = Angles.EFromM(M, ecc);
                    double back = Angles.MFromE(E, ecc);
                    Assert.True(System.Math.Abs(back - M) < 1e-11, $"椭圆 M→E→M 不闭合: e={ecc} M={M} → {back}");
                }
            }

            // 双曲：ν → F → ν
            foreach (double ecc in new[] { 1.2, 2.0, 3.0 })
            {
                foreach (double nu in new[] { -1.2, -0.4, 0.0, 0.4, 1.2 })
                {
                    double F = Angles.FFromNu(nu, ecc);
                    double back = Angles.NuFromF(F, ecc);
                    Assert.True(System.Math.Abs(back - nu) < 1e-10, $"双曲 ν→F→ν 不闭合: e={ecc} ν={nu} → {back}");
                }
            }

            // 双曲：M → F → M
            foreach (double ecc in new[] { 1.2, 2.0, 3.0 })
            {
                foreach (double M in new[] { -2.0, -0.3, 0.0, 0.5, 2.0 })
                {
                    double F = Angles.FFromM(M, ecc);
                    double back = Angles.MFromF(F, ecc);
                    Assert.True(System.Math.Abs(back - M) < 1e-10, $"双曲 M→F→M 不闭合: e={ecc} M={M} → {back}");
                }
            }

            // 近抛物：ν → D → ν
            foreach (double nu in new[] { -2.5, -0.8, 0.0, 0.9, 2.2 })
            {
                double D = Angles.DFromNu(nu);
                double back = Angles.NuFromD(D);
                Assert.True(System.Math.Abs(back - nu) < 1e-11, $"近抛物 ν→D→ν 不闭合: ν={nu} → {back}");
            }
        }

        /// <summary>
        /// `Farnocchia`（近抛物专用求解器）给出的状态必须与 `Shepperd` 一致 ——
        /// 两者是**独立算法**，在近抛物区（e≈1）最容易分道扬镳，互证价值最高。
        /// </summary>
        [Fact]
        public void Farnocchia_AgreesWithShepperd_NearParabolic()
        {
            // e = 1.001：几乎抛物，是最难的区制。
            // ⚠ 速度必须取 sqrt(1.001) · v_esc ⇒ v²R/μ = 2.001 ⇒ e = v²R/μ − 1 = 1.001。
            //   （原先写成 v_c·1.0005 是错的：那给出 v²R/μ = 1.001 ⇒ e = 0.001，还在椭圆侧。）
            double vEsc = System.Math.Sqrt(2.0 * Mu / R0);
            RadialPerpendicularState(R0, vEsc * System.Math.Sqrt(1.001), out V3 r, out V3 v);
            Assert.True(Astro.EccFromStateVectors(Mu, r, v) > 1.0, "前置失败：期望双曲轨道");

            (V3 rfS, V3 vfS) = Shepperd.Solve(Mu, 500.0, r, v);
            (V3 rfF, V3 vfF) = Farnocchia.Solve(Mu, 500.0, r, v);

            AssertVecRel(rfF, rfS, 1e-6, "Farnocchia vs Shepperd 位置（近抛物）");
            AssertVecRel(vfF, vfS, 1e-6, "Farnocchia vs Shepperd 速度（近抛物）");
        }

        // ── Scale / Statics：移植面的基本自洽 ────────────────────────────────────

        /// <summary>
        /// `Scale`（无量纲化）是本项目最缺的那一族之一：它的存在理由是"绝对阈值跨量级复用"这个病
        /// （本项目 §24.1 的 60000s、§27 的死区都是这个病的症状）。
        /// 判据 = 它自报的派生尺度与定义一致（可被违反：任一时/力/密度尺度写错就红）。
        /// </summary>
        [Fact]
        public void Scale_DerivedScalesMatchDefinition()
        {
            var s = Scale.Create(Mu, R0);

            Assert.Equal(R0, s.LengthScale, 6);
            Assert.Equal(System.Math.Sqrt(Mu / R0), s.VelocityScale, 6);
            Assert.Equal(1.0, s.MassScale, 12);

            Assert.Equal(s.LengthScale / s.VelocityScale, s.TimeScale, 6);
            Assert.Equal(s.VelocityScale / s.TimeScale, s.AccelScale, 6);
            Assert.Equal(s.MassScale * s.AccelScale, s.ForceScale, 6);
            Assert.Equal(s.MassScale / s.TimeScale, s.MdotScale, 6);
            Assert.Equal(s.LengthScale * s.LengthScale, s.AreaScale, 6);
            Assert.Equal(s.ForceScale / s.AreaScale, s.PressureScale, 6);
        }

        /// <summary>
        /// 移植的 `Statics` 必须在**极端与边界输入**上与公式一致。
        /// 这一条同时锁住一个已登记的事实：我们原来手抄的 `MJStatics` 与上游**已经漂移**
        /// （它的 `Asinh/Atanh/Acosh` 是朴素一行式，小量上会退化为 0），
        /// 所以这里是"唯一权威"落地后应有的行为。
        /// </summary>
        [Fact]
        public void Statics_MatchesFormulasAtEdges()
        {
            // 小量：朴素写法 Log(x + Sqrt(x²+1)) 在 x=1e-9 会因舍入返回 0，正确的应 ≈ x
            Assert.True(System.Math.Abs(Statics.Asinh(1e-9) - 1e-9) < 1e-15,
                $"Asinh(1e-9) 应为 ≈1e-9，实得 {Statics.Asinh(1e-9):R}");

            Assert.Equal(0.0, Statics.Asinh(0.0), 12);
            Assert.Equal(0.0, Statics.Atanh(0.0), 12);
            Assert.Equal(0.0, Statics.Acosh(1.0), 12);

            // 边界：Atanh(±1) 是 ±∞（不是抛异常）
            Assert.True(double.IsPositiveInfinity(Statics.Atanh(1.0)), "Atanh(1) 应为 +∞");
            Assert.True(double.IsNegativeInfinity(Statics.Atanh(-1.0)), "Atanh(-1) 应为 -∞");
            Assert.True(double.IsNaN(Statics.Atanh(1.5)), "Atanh(1.5) 应为 NaN");

            // SafeAcos/SafeAsin 对越界与 NaN 必须钳位/传 NaN，而不是抛
            Assert.Equal(0.0, Statics.SafeAcos(1.0), 12);
            Assert.Equal(System.Math.PI, Statics.SafeAcos(-1.0), 12);
            Assert.True(double.IsNaN(Statics.SafeAcos(double.NaN)), "SafeAcos(NaN) 应为 NaN");

            // ClampPi 值域 (-π, π]
            foreach (double x in new[] { -10.0, -System.Math.PI, 0.0, System.Math.PI, 10.0, 100.0 })
            {
                double y = Statics.ClampPi(x);
                Assert.True(y > -System.Math.PI - 1e-12 && y <= System.Math.PI + 1e-12, $"ClampPi({x}) = {y} 越界");
            }

            // Powi 与整数幂一致（含负指数）
            Assert.Equal(System.Math.Pow(3.0, 5), Statics.Powi(3.0, 5), 9);
            Assert.Equal(System.Math.Pow(3.0, -4), Statics.Powi(3.0, -4), 12);
        }
    }
}
