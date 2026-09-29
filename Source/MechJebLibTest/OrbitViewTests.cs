using MechJebLib.Functions;
using MechJebLib.Primitives;
using MechJebLib.TwoBody;
using MechJebLib.Utils;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// `OrbitView` 的验收（《P-1适配层决策》§7 的 A1/A3）。
    ///
    /// **A2 的现状要说清楚**：A2 原先写"通过 MJ 自己的 `MechJebLibTest` 对应项"——
    /// **上游根本没有 `OrbitExtensions` 的测试**（它在 KSP 层，MJ 只测 `MechJebLib`）。
    /// ⇒ `OrbitView` 这一层的验证**只能靠我们自己的互证**，也就是本文件。
    /// 好消息是这恰好说明适配层足够薄：**真数学全在已被 4989 条上游用例验收的
    /// `Astro`/`Angles`/`Shepperd` 那一层**，本文件只需验证"摆到 MJ 期望的成员名下"这件事没摆错。
    /// </summary>
    public class OrbitViewTests
    {
        private static readonly double Mu = OrbitMath.GM;
        private const double R0 = 7_000_000.0;

        /// <summary>
        /// 极轴 = **+Y**（批4 P4 极轴统一：适配层与游戏世界同系，`GameWorldToAdapter` 已退化为恒等）。
        /// ⚠ 手写的构造向量按 `(x, y, z)_新 = (x, z, −y)_旧` 换元（一个 det=+1 的旋转、同一个物理轨道）
        /// ⇒ **帧不变标量期望（倾角/半径/周期/ν/Δv 幅值）不动**，矢量分量期望才换元。
        /// </summary>
        private static readonly V3 Pole = new V3(0, 1, 0);
        private const double BodyR = 5_000_000.0;

        /// <summary>构造一个**真圆轨**：速度必须与位置相切（`Pole × r̂`，`Pole` = +Y ⇒ 绕 +Y 顺行）。</summary>
        private static OrbitView Circular(double radius, V3 r, bool prograde = true)
        {
            double vc = System.Math.Sqrt(Mu / radius);
            V3 tangent = V3.Cross(Pole, r.normalized).normalized; // ĥ × r̂
            return OrbitView.FromStateVectors(r, tangent * (prograde ? vc : -vc), Mu, 0.0, Pole);
        }

        // ── A1：形状可用 ────────────────────────────────────────────────────────

        /// <summary>
        /// 适配层四件套能构造、且 `ITargetView` 的契约成立（σ/年龄 = 0 的精确目标）。
        /// </summary>
        [Fact]
        public void A1_AdapterTypesConstructAndTargetContractHolds()
        {
            var ov = Circular(R0, new V3(R0, 0, 0));
            var node = new PlanNode(new V3(0, 10, 0), 1234.5);
            var body = new BodyView(1, Mu, 5_000_000.0, 100_000.0, Pole, 7.27e-5, 0.0, -1, double.NaN);

            Assert.Equal(PlanOwner.Guidance, node.owner);
            Assert.Equal(5_100_000.0, body.AtmoOuterRadius, 3);
            Assert.Equal(R0, ov.radius, 6);

            var target = new FixedTargetView(ov, identityGroupId: 42, apparentRadius: 50.0);
            Assert.True(target.TryOrbit(0.0, out var got));
            Assert.Equal(R0, got.radius, 6);
            Assert.Equal(0.0, target.PositionSigma, 12);
            Assert.Equal(0.0, target.DataAgeSeconds, 12);
            Assert.Equal(42, target.IdentityGroupId);
            Assert.Equal(-1, target.IdentityDecoyId); // 真接触的 decoyId 必须是 -1
            Assert.True(target.Confidence.IsUsable);
            Assert.Equal(1.0, target.Confidence.value, 12);
        }

        // ── A3：传播三方互证 + R1 旋转方向 ──────────────────────────────────────

        /// <summary>
        /// `OrbitView` 的传播 ↔ 上游 `Shepperd` ↔ 上游 `DP5` 数值积分。
        /// 由于 `OrbitView.RightHandedStateVectorsAtUT` 只是 `Shepperd` 的转发，
        /// 真正的第三方是 `DP5` —— 它不共享任何一方的代码。
        /// </summary>
        [Theory]
        [InlineData(0.1)]
        [InlineData(0.25)]
        [InlineData(0.5)]
        [InlineData(1.0)]
        [InlineData(-0.35)]
        public void A3_Propagation_AgreesWithUpstreamNumericalReference(double periodFraction)
        {
            var ov = Circular(R0, new V3(R0, 0, 0));
            double tau = ov.period * periodFraction;

            (V3 p, V3 vel) = ov.RightHandedStateVectorsAtUT(tau);
            (V3 np, V3 nv) = IntegrateTwoBody(ov.r, ov.v, tau);

            AssertRel(p, np, 1e-6, $"OrbitView 位置 vs DP5（{periodFraction} 圈）");
            AssertRel(vel, nv, 1e-6, $"OrbitView 速度 vs DP5（{periodFraction} 圈）");
        }

        /// <summary>
        /// ⭐ **R1 的旋转方向判据，这次落在 `OrbitView` 上**：
        /// 圆轨 `r=(R,0,0)`、`v = Pole×r̂·vc = (0,0,−vc)` ⇒ `h = r×v = +Y`；绕 +Y 的右手旋转把
        /// `x̂` 送到 `(0,0,−1)` ⇒ `r(T/4)` 必须是 `(0,0,−R)`。
        /// 若参考系手性反了，这条会红——而它正是"每个移植物都给出看起来合理的错数"的那种坑。
        /// </summary>
        [Fact]
        public void A3_OrbitView_RotationSenseIsRightHanded()
        {
            var ov = Circular(R0, new V3(R0, 0, 0));

            Assert.True(ov.OrbitNormal().y > 0, $"OrbitNormal 应为 +Y，实得 {ov.OrbitNormal()}");

            (V3 p, V3 vel) = ov.RightHandedStateVectorsAtUT(ov.period * 0.25);
            AssertRel(p, new V3(0, 0, -R0), 1e-9, "OrbitView r(T/4)（绕 +Y 转 90°：x̂ → −ẑ）");
            AssertRel(vel, new V3(-System.Math.Sqrt(Mu / R0), 0, 0), 1e-9, "OrbitView v(T/4)");

            // 反向对照：不是碰巧落在 +z
            (V3 pb, V3 _) = ov.RightHandedStateVectorsAtUT(-ov.period * 0.25);
            AssertRel(pb, new V3(0, 0, R0), 1e-9, "OrbitView r(-T/4)（应为 +z）");
        }

        /// <summary>
        /// 要素提取 ↔ 上游 `Astro`（**现在与 `OrbitView` 是同一个参考系**）。
        ///
        /// ⚠ 2026-09-28（批4 P5）：本条原先还带一次**跨系对照**（换到"游戏世界 +Y 极轴"再重算倾角、
        /// 要求两数相等）——**那条判据已随被测量一起消失**：适配层与游戏世界已是同一个系、
        /// `GameWorldToAdapter` 退化为恒等 ⇒ 再"跨系"比一次就是自己跟自己比（恒真判据，故删，
        /// 不留"永远为真"的空判据）。"两系同轴"这一事实现在由
        /// `A3_OrbitView_RotationSenseIsRightHanded` 与移植面 6207 条上游测试共同承担。
        /// </summary>
        [Fact]
        public void A3_Elements_AgreeWithUpstream()
        {
            // 偏心 0.3、倾角 30°（+Y 极轴下）的一般位置
            double v = System.Math.Sqrt(1.3 * Mu / R0);
            var r = new V3(R0, 0, 0);
            // 构造向量按 `(x, y, z)_新 = (x, z, −y)_旧` 换元（倾角 0.5 rad 不变 ⇒ 见最后一条断言）
            var vv = new V3(0, v * System.Math.Sin(0.5), -v * System.Math.Cos(0.5));
            var ov = OrbitView.FromStateVectors(r, vv, Mu, 0.0, Pole);

            Assert.Equal(Astro.SmaFromStateVectors(Mu, r, vv), ov.semiMajorAxis, 6);
            Assert.Equal(Astro.EccFromStateVectors(Mu, r, vv), ov.eccentricity, 12);
            Assert.Equal(Astro.IncFromStateVectors(r, vv), ov.inclination, 12);
            Assert.Equal(0.5, ov.inclination, 9); // 由 v 的构造决定：h ∝ (0, cos0.5, sin0.5) ⇒ acos(ĥ·ŷ) = 0.5
        }

        /// <summary>
        /// `OrbitView.period` 是**唯一权威**（§14.3『周期』行）。
        /// 这里同时钉住：它与 `Astro.PeriodFromStateVectors` 是同一个数，
        /// 以及**双曲时返回 NaN 而不是抛异常**——因为上游 `NextClosestApproachTime` 先读 `period` 再判 `ecc`。
        /// </summary>
        [Fact]
        public void A3_Period_IsSingleAuthority_AndNaNOnHyperbolic()
        {
            var ov = Circular(R0, new V3(R0, 0, 0));
            double expected = Astro.PeriodFromStateVectors(Mu, ov.r, ov.v);
            Assert.Equal(expected, ov.period, 6);
            Assert.Equal(2.0 * System.Math.PI * System.Math.Sqrt(R0 * R0 * R0 / Mu), ov.period, 3);

            // 双曲：period 必须给 NaN（守卫），meanMotion 必须为正（上游拿它算"大块双曲弧"）
            double vEsc = System.Math.Sqrt(2.0 * Mu / R0);
            // 换元 `(x,y,z)_新=(x,z,−y)_旧`（"是不是双曲"是帧不变量 ✓ 期望不动）
            var hyp = OrbitView.FromStateVectors(new V3(R0, 0, 0), new V3(0, 0, -vEsc * 1.4), Mu, 0.0, Pole);
            Assert.True(hyp.eccentricity > 1.0);
            Assert.True(double.IsNaN(hyp.period), $"双曲 period 应为 NaN，实得 {hyp.period}");
            Assert.True(hyp.meanMotion > 0.0);
        }

        // ── ⭐ 与已知缺陷的正对照 ────────────────────────────────────────────────

        /// <summary>
        /// ⭐ **圆轨上 `TrueAnomalyFromVector` 必须良定** —— 这正是我们自家
        /// `OrbitMath.GetTrueAnomaly` 栽跟头的地方（它用 `e⃗·r̂/e` 求真近点角，
        /// 圆轨上分子分母同为舍入噪声 ⇒ 返回任意值，见 `MechJebLibPortTests.KnownDefect_*`）。
        ///
        /// 新路径不用 `e⃗`：走移植的 `Astro.EvecFromKeplerian(ecc, inc, lan, argP)`，由要素按闭式给方向。
        /// 判据用可直接心算的形态：圆轨上**当前点就是 ν=0**，沿运动方向 90° 处必须是 π/2。
        /// </summary>
        [Fact]
        public void CircularOrbit_TrueAnomalyFromVectorIsWellDefined()
        {
            var ov = Circular(R0, new V3(R0, 0, 0));

            // 当前点 → ν = 0
            Assert.Equal(0.0, ov.TrueAnomalyFromVector(ov.r), 9);

            // 沿运动方向 90°（绕 +Y 转 90°：x̂ → −ẑ）→ ν = π/2
            Assert.Equal(System.Math.PI / 2, ov.TrueAnomalyFromVector(new V3(0, 0, -R0)), 9);

            // 反向 90°（+ẑ）→ ν = 3π/2
            Assert.Equal(3 * System.Math.PI / 2, ov.TrueAnomalyFromVector(new V3(0, 0, R0)), 9);

            // 对跖点 → ν = π
            Assert.Equal(System.Math.PI, ov.TrueAnomalyFromVector(new V3(-R0, 0, 0)), 9);

            // 法向分量必须被投影掉（纯 ±极轴方向的矢量投影后是零 ⇒ 走零矢量分支返回 0，且不产生 NaN）
            double fromPole = ov.TrueAnomalyFromVector(new V3(0, R0, 0));
            Assert.False(double.IsNaN(fromPole));
        }

        // ── North / East（地表几何的**纯**那一半） ──────────────────────────────

        /// <summary>
        /// `North`/`East` 的**结构判据**（公式无关，四条）：
        /// ① 三个方向各自是单位矢量；② 两两正交；③ `East = Up × North`（右手性）；
        /// ④ **已知几何**：站在**赤道上**时"朝北极的切向"就是**极轴本身** ⇒ `North ≈ pole`。
        /// 第 ④ 条是最不可能骗人的那种判据——可以直接心算。
        /// </summary>
        [Fact]
        public void NorthEast_AreOrthonormal_AndNorthPointsToPoleAtEquator()
        {
            // 赤道上的点（批4 P4：极轴 +Y ⇒ 赤道面 = x–z 平面）
            var o = Circular(R0, new V3(R0, 0, 0));

            V3 up = o.Up(0.0), north = o.North(0.0), east = o.East(0.0);

            Assert.Equal(1.0, up.magnitude, 12);
            Assert.Equal(1.0, north.magnitude, 12);
            Assert.Equal(1.0, east.magnitude, 12);

            Assert.Equal(0.0, V3.Dot(north, up), 12);
            Assert.Equal(0.0, V3.Dot(east, up), 12);
            Assert.Equal(0.0, V3.Dot(north, east), 12);

            // ③ 右手性：East = Up × North
            V3 cross = V3.Cross(up, north);
            Assert.Equal(cross.x, east.x, 12);
            Assert.Equal(cross.y, east.y, 12);
            Assert.Equal(cross.z, east.z, 12);

            // ④ 赤道上：朝北极的切向 = 极轴本身
            Assert.Equal(1.0, V3.Dot(north, Pole), 9);

            // 沿轨道走一段再验一次（换个位置、同一结论）
            var o2 = Circular(R0, new V3(R0 * System.Math.Cos(1.1), 0, -R0 * System.Math.Sin(1.1))); // 仍是赤道上的点（+Y 口径）
            Assert.Equal(1.0, V3.Dot(o2.North(0.0), Pole), 9);
        }

        /// <summary>
        /// **倾斜轨道在赤道交点处**：朝北极的切向同样应当 ≈ 极轴
        /// （因为那一点就在赤道上，"往北"就是沿极轴方向）✓ 与倾角无关 —— 这条同时说明
        /// `North` 与轨道形状无关、只与"你在地球上的哪一点"有关。
        /// </summary>
        [Fact]
        public void North_AtEquatorCrossing_IsPoleRegardlessOfInclination()
        {
            double vc = System.Math.Sqrt(Mu / R0);
            double inc = System.Math.PI / 6;
            var v = new V3(0, vc * System.Math.Sin(inc), -vc * System.Math.Cos(inc)); // 换元 (x,y,z)_新=(x,z,−y)_旧
            var o = OrbitView.FromStateVectors(new V3(R0, 0, 0), v, Mu, 0.0, Pole, BodyR); // 起点即赤道升交点

            Assert.Equal(1.0, V3.Dot(o.North(0.0), Pole), 9);
        }

        /// <summary>
        /// **退化点**：站在极轴正上方时"朝北极的切向"没有定义 ⇒ 必须给**零矢量**而不是 NaN
        /// （调用方靠判长来区分"无定义"，NaN 会静默污染下游）。
        /// </summary>
        [Fact]
        public void NorthAtPole_IsZeroNotNaN()
        {
            // 一条穿过极点正上方的轨道（r ∥ pole = +Y ⇒ `(0, R0, 0)`；速度垂直于极轴 ⇒ 仍是合法轨道）
            var o = OrbitView.FromStateVectors(new V3(0, R0, 0), new V3(System.Math.Sqrt(Mu / R0), 0, 0), Mu, 0.0, Pole, BodyR);

            V3 north = o.North(0.0);
            V3 east = o.East(0.0);

            Assert.False(double.IsNaN(north.x) || double.IsNaN(north.y) || double.IsNaN(north.z), "North 不该是 NaN");
            Assert.False(double.IsNaN(east.x) || double.IsNaN(east.y) || double.IsNaN(east.z), "East 不该是 NaN");
            Assert.Equal(0.0, north.magnitude, 12);
            Assert.Equal(0.0, east.magnitude, 12);
        }

        // ── ⭐ 顺行/逆向的**约定**（本 mod 与 KSP/MJ 相反） ──────────────────────

        /// <summary>
        /// ⭐ **本 mod 里 `inclination = 180°` 才是顺行**（与 KSP/MJ 的 0° 直觉相反）。
        ///
        /// 依据（**全部来自本 mod 自己的约定**，不是推测）：
        /// · `GetSurfaceState.cs` 文件头：自转轴 = **+Y**、东 = **(−z→+x)**、`p_eci = Ry(−ωt)·p_fix`；
        /// · 同一文件的 `SurfaceVelocity = ω·(−p_z, 0, p_x)`。
        /// ⇒ 在 `p = +x̂` 处**东向**速度 = `ω·(0,0,R) = +ẑ` ⇒ **`h = p×v ∥ −Ŷ`**。
        /// 而 `inclination` 是 `h` 与 `+极轴` 的夹角 ⇒ **顺行赤道轨的 inclination = 180°**。
        ///
        /// ⇒ 任何**面向玩家**的倾角参数都要意识到这一点（否则 `OperationInclination` 输入 30°
        /// 会给出一个近乎**逆行**的轨道）。这条与游戏内探针的判据 C 是同一件事的两半。
        /// </summary>
        [Fact]
        public void ProgradeEquatorialOrbit_HasInclination180_Not0()
        {
            // 在 p = +x̂ 处，游戏侧东向 = +ẑ（`SurfaceFrame.SurfaceState`：v = ωRcosφ(cosλ, 0, sinλ)）。
            // 批4 P4 起适配层与游戏世界同系 ⇒ **就是 `(0,0,1)` 本身**，不再有换系那一步。
            V3 gameEast = new V3(0, 0, 1);
            V3 rAt = new V3(R0, 0, 0);
            double vCirc = System.Math.Sqrt(Mu / R0);

            var prograde = OrbitView.FromStateVectors(rAt, gameEast.normalized * vCirc, Mu, 0.0, Pole, BodyR);
            var retrograde = OrbitView.FromStateVectors(rAt, gameEast.normalized * -vCirc, Mu, 0.0, Pole, BodyR);

            Assert.Equal(System.Math.PI, prograde.inclination, 9);   // 顺行 ⇒ 180°
            Assert.Equal(0.0, retrograde.inclination, 9);            // 逆行 ⇒ 0°

            // 交叉核对：顺行的 h 指向 −极轴（而不是 +极轴）
            Assert.Equal(-1.0, V3.Dot(prograde.OrbitNormal(), Pole), 9);
            Assert.Equal(+1.0, V3.Dot(retrograde.OrbitNormal(), Pole), 9);
        }

        // ── 两体间几何 ──────────────────────────────────────────────────────────

        /// <summary>
        /// `NextClosestApproachTime` 用一个**有解析答案**的场景：
        /// 追赶者顺行圆轨、目标**逆行**圆轨、同半径、初始相差 90°。
        /// 相对角速度是 2n ⇒ 首次重合在 `t = T/8`（且那一刻距离为 0）。
        /// 这条同时验证了搜索的收敛方向没写反。
        /// </summary>
        [Fact]
        public void NextClosestApproach_MatchesClosedForm_ForRetrogradePair()
        {
            var chaser = Circular(R0, new V3(R0, 0, 0));                    // 顺行，h = +Y
            var target = Circular(R0, new V3(0, 0, -R0), prograde: false);  // 目标在 −ẑ（沿追赶者运动方向相差 90°）、逆行，h = −Y

            Assert.True(target.OrbitNormal().y < 0, "目标应为逆行（h = −Y）");

            double t = chaser.NextClosestApproachTime(target, 0.0);
            double expected = chaser.period / 8.0;

            Assert.True(System.Math.Abs(t - expected) / expected < 1e-4,
                $"最近接近时刻应为 T/8 = {expected:F1}s，实得 {t:F1}s");

            double d = chaser.NextClosestApproachDistance(target, 0.0);
            Assert.True(d / R0 < 1e-6, $"那一刻距离应 ≈ 0，实得 {d:E3} m");
        }

        /// <summary>
        /// 同半径共面**同向**圆心轨：距离是常数 `2R·sin(Δθ/2)`（相位差不变）。
        /// 这是一条闭式检查，专门排除"搜索返回了一个看似合理但错的时刻"。
        /// </summary>
        [Fact]
        public void Separation_ConstantForCoplanarCoCircularPair()
        {
            var a = Circular(R0, new V3(R0, 0, 0));
            double theta = System.Math.PI / 3; // 60°
            var b = Circular(R0, new V3(R0 * System.Math.Cos(theta), 0, -R0 * System.Math.Sin(theta))); // 共面（赤道面 x–z）且相位差 θ

            double expected = 2 * R0 * System.Math.Sin(theta / 2);
            foreach (double ut in new[] { 0.0, 100.0, 1000.0, a.period * 0.37 })
                Assert.Equal(expected, a.Separation(b, ut), 3);
        }

        /// <summary>`RelativeInclination` / `SynodicPeriod` 的基本语义。</summary>
        [Fact]
        public void RelativeInclination_And_SynodicPeriod()
        {
            var pro = Circular(R0, new V3(R0, 0, 0));
            var retro = Circular(R0, new V3(R0, 0, 0), prograde: false);

            // 同面反向 ⇒ 相对倾角 180°
            Assert.Equal(System.Math.PI, pro.RelativeInclination(retro), 9);

            // 同向同周期 ⇒ 会合周期趋于无穷（两者的 1/T 相减为 0）
            Assert.True(double.IsInfinity(System.Math.Abs(pro.SynodicPeriod(pro))), "同轨道会合周期应为无穷");

            // 反向同周期 ⇒ 会合周期 = T/2（相对角速度 2n）
            double syn = pro.SynodicPeriod(retro);
            Assert.Equal(pro.period / 2.0, syn, 3);
        }

        // ── 时间 / 平近点角 ─────────────────────────────────────────────────────

        /// <summary>
        /// `TimeOfTrueAnomaly` ↔ 实际传播的往返闭合：
        /// 求出"下次到达 ν 的时刻"，把状态传播到那一刻，再从状态反解真近点角，必须回到 ν。
        ///
        /// ⚠ 这里用**偏心 0.3** 的轨道。近圆轨道上这条**不成立**，原因见下一条
        /// `KnownLimitation_NearCircularTrueAnomalyIsThresholdFlaky` —— 是上游要素提取在退化区的问题，
        /// 不是传播的问题。
        /// </summary>
        [Fact]
        public void TimeOfTrueAnomaly_RoundTripsThroughPropagation()
        {
            // e = 0.3、带倾角的一般椭圆（在 r·v = 0 的族里 e = v²R/μ − 1）
            double speed = System.Math.Sqrt(1.3 * Mu / R0);
            var r = new V3(R0, 0, 0);
            var v = new V3(0, speed * System.Math.Sin(0.4), -speed * System.Math.Cos(0.4)); // 换元 (x,y,z)_新=(x,z,−y)_旧（e = 0.3 是帧不变量）
            var ov = OrbitView.FromStateVectors(r, v, Mu, 0.0, Pole);
            Assert.Equal(0.3, ov.eccentricity, 9);

            foreach (double nu in new[] { 0.1, 1.0, System.Math.PI / 2, 3.0, 5.5 })
            {
                double t = ov.TimeOfTrueAnomaly(nu, 0.0);
                (V3 p, V3 vv) = ov.RightHandedStateVectorsAtUT(t);
                double back = Astro.TrueAnomalyFromStateVectors(Mu, p, vv);

                double diff = System.Math.Abs(Statics.ClampPi(back - nu));
                Assert.True(diff < 1e-8, $"ν={nu} 的往返不闭合：解出 t={t:F1}s 后反解得 ν={back:R}");
            }
        }

        /// <summary>
        /// ⭐ **近圆轨道上真近点角必须良定**（R6 的回归守卫，2026-09-18 修）。
        ///
        /// **修前**：`ecc ≈ 1e-16` 时 `Astro` 的 `sum = ecc > EPS ? Atan2(h,xk) : i*lan`
        /// 会因 `ecc` 落在 `EPS = 2.22e-16` 两侧而翻车——同一条圆轨、只差一次传播，反解 `ν` 从 `1.0` 跳到 `3.68`。
        ///
        /// **修法（用户裁定 (a)）**：`OrbitView` 在 `ecc < 1e-6` 时接管，走**标准约定**——
        /// 近心点置于升交点（`argP = 0`），`ν` 即纬度幅角 `atan2(r̂·(ĥ×n̂), r̂·n̂)`。
        /// **不动移植物**，"与上游 diff 为空"的不变式保住。
        ///
        /// ⚠ 本条**同时**断言 `Astro` 原始接口仍然会翻车（那是上游未处理的边界，不是我们的 bug），
        /// 以及 **`OrbitView` 两条路径都不过它** ——差别就是这次修复的全部内容。
        /// </summary>
        [Fact]
        public void NearCircular_TrueAnomalyIsWellDefined_WhileRawAstroStillFlaky()
        {
            var ov = Circular(R0, new V3(R0, 0, 0)); // 真圆轨，ecc ≈ 1e-16
            Assert.True(ov.eccentricity < Statics.EPS, $"前置：期望 ecc 落在噪声地板下，实得 {ov.eccentricity:E3}");

            // ① OrbitView 自己的要素是良定的：圆轨上当前点即 ν = 0
            Assert.Equal(0.0, ov.trueAnomaly, 9);
            Assert.Equal(0.0, ov.argumentOfPeriapsis, 12); // 标准约定：近心点在升交点
            Assert.Equal(0.0, ov.LAN, 12);                 // 赤道轨道 ⇒ 升交点在 +x

            // ② 往返：求出 t → 传播 → 反解，必须回到 ν
            foreach (double nu in new[] { 0.1, 1.0, System.Math.PI / 2, 3.0, 5.5 })
            {
                double t = ov.TimeOfTrueAnomaly(nu, 0.0);
                (V3 p, V3 v) = ov.RightHandedStateVectorsAtUT(t);

                // 位置确实落在 nu（与反解无关的独立检查）。
                // ⚠ `atan2` 值域是 (-π, π]，而 ν 在 [0, 2π) ⇒ 必须先 Clamp2Pi 再比，否则 5.5 rad 会以 -0.783 出现。
                Assert.Equal(nu, Statics.Clamp2Pi(System.Math.Atan2(-p.z, p.x)), 8);

                // 用 OrbitView 自己的入口重建：ν 必须回到 nu
                var at = OrbitView.FromStateVectors(p, v, Mu, t, Pole);
                Assert.Equal(nu, at.trueAnomaly, 8);
            }

            // ③ 对照：上游原始接口在这条轨道上仍然不可信（这是"我们为什么要接管"的证据本身）
            (V3 pp, V3 vv) = ov.RightHandedStateVectorsAtUT(ov.TimeOfTrueAnomaly(1.0, 0.0));
            double raw = Astro.TrueAnomalyFromStateVectors(Mu, pp, vv);
            Assert.True(System.Math.Abs(Statics.ClampPi(raw - 1.0)) > 1e-3,
                $"上游 `Astro.TrueAnomalyFromStateVectors` 在近圆上已变得良定（反解得 {raw:R}）——" +
                "说明 R6 已在上游侧解决，可以删掉 `OrbitView` 的近圆接管分支了。");
        }

        /// <summary>
        /// 赤道交点：把 `equatorialAxis` 真的用起来了。
        /// 判据不靠解析式，而是**直接采样传播结果**：返回时刻处位置沿**极轴**的分量必须过零且向上。
        /// （批4 P4：极轴 = +Y ⇒ 该分量是 `y`；测试名随之由 `ZCrosses` 改为 `YCrosses`。）
        /// </summary>
        [Fact]
        public void TimeOfAscendingNodeEquatorial_IsWhereYCrossesNorthward()
        {
            // 倾角 30° 的**圆轨**（适配层 +z 极轴）：起点在赤道面内 (+x)、速度按 30° 仰角出发。
            // ⚠ 必须是真圆轨 ⇒ 速度分量要按 cos/sin 分配，|v| = vc；否则得到的是椭圆，倾角也不是 30°。
            double vc = System.Math.Sqrt(Mu / R0);
            double inc = System.Math.PI / 6;
            var r = new V3(R0, 0, 0);
            var v = new V3(0, vc * System.Math.Sin(inc), -vc * System.Math.Cos(inc)); // 换元 (x,y,z)_新=(x,z,−y)_旧
            var ov = OrbitView.FromStateVectors(r, v, Mu, 0.0, Pole);

            Assert.Equal(inc, ov.inclination, 9);
            Assert.Equal(R0, ov.semiMajorAxis, 3); // 圆轨
            Assert.True(ov.AscendingNodeEquatorialExists(), "该轨道应当存在赤道升交点");

            double t = ov.TimeOfAscendingNodeEquatorial(0.0);
            Assert.True(t >= 0.0 && t <= ov.period + 1e-6, $"升交点时刻应落在 [0, T] 内，实得 {t}");

            // 采样：返回时刻处 y 必须 ≈ 0（赤道面 = x–z），且紧邻之后 y > 0（"向北"= 朝 +Y）
            double yAt = ov.RightHandedBCIPositionAtUT(t).y;
            double yAfter = ov.RightHandedBCIPositionAtUT(t + ov.period * 0.01).y;
            Assert.True(System.Math.Abs(yAt) / R0 < 1e-6, $"升交点处 y 应为 0，实得 {yAt:E3}");
            Assert.True(yAfter > 0.0, $"升交点之后 y 应为正（向北穿越），实得 {yAfter:E3}");
        }

        // ── 机动坐标 ────────────────────────────────────────────────────────────

        /// <summary>
        /// `DeltaVToManeuverNodeCoordinates`：纯顺行的 Δv 应当完全落在第三分量（prograde），
        /// 其余两个为 0；纯径向的 Δv 落在第一分量。**法向取负是 MJ 的约定**，用"烧 +z"钉住它。
        /// </summary>
        [Fact]
        public void DeltaVToManeuverNodeCoordinates_DecomposesCorrectly()
        {
            var ov = Circular(R0, new V3(R0, 0, 0));
            const double ut = 0.0;

            var progradeDv = ov.DeltaVToManeuverNodeCoordinates(ut, new V3(0, 0, -100)); // 顺行 = 速度方向 = −ẑ（+Y 口径）
            Assert.Equal(0.0, progradeDv.x, 6);
            Assert.Equal(0.0, progradeDv.y, 6);
            Assert.Equal(100.0, progradeDv.z, 6);

            var radialDv = ov.DeltaVToManeuverNodeCoordinates(ut, new V3(100, 0, 0));
            Assert.Equal(100.0, radialDv.x, 6);
            Assert.Equal(0.0, radialDv.z, 6);

            // 世界系 +Y（= 轨道法向）在 MJ 的节点坐标里是 **-|Δv|**（`-NormalPlus`）
            var normalDv = ov.DeltaVToManeuverNodeCoordinates(ut, new V3(0, 100, 0));
            Assert.Equal(-100.0, normalDv.y, 6);
        }

        // ── A5（纯层那一半）：置信度单调性 ──────────────────────────────────────

        /// <summary>
        /// 《P-1适配层决策》§4.3：**σ 或年龄递增 ⇒ 置信度单调不增**。
        /// 这是 A5 的纯类型那一半（`ContactTargetView` 那一半等它的实现落地时补）。
        /// </summary>
        [Fact]
        public void A5_PlanConfidence_IsMonotoneInSigmaAndAge()
        {
            const double L = R0, T = 600.0;

            double prev = double.PositiveInfinity;
            foreach (double sigma in new[] { 0.0, 1.0, 100.0, 10_000.0, 1e6 })
            {
                double v = PlanConfidence.From(sigma, 0.0, L, T).value;
                Assert.True(v <= prev, $"σ 增大时置信度必须不增：σ={sigma} 得 {v} > 上个 {prev}");
                Assert.True(v > 0.0 && v <= 1.0);
                prev = v;
            }

            prev = double.PositiveInfinity;
            foreach (double age in new[] { 0.0, 1.0, 60.0, 600.0, 36_000.0 })
            {
                double v = PlanConfidence.From(0.0, age, L, T).value;
                Assert.True(v <= prev, $"年龄增大时置信度必须不增：age={age} 得 {v} > 上个 {prev}");
                prev = v;
            }

            Assert.False(PlanConfidence.Unusable.IsUsable);
            Assert.Equal(1.0, PlanConfidence.Exact.value, 12);
        }

        // ── 仪器：上游 DP5（与 MechJebLibPortTests 同法：无量纲化到上游的尺度） ──

        private sealed class KeplerOde
        {
            public const int N = 6;

            public void Rhs(Vec yin, double x, Vec dyout)
            {
                var r = new V3(yin[0], yin[1], yin[2]);
                var v = new V3(yin[3], yin[4], yin[5]);
                double rm2 = r.sqrMagnitude;
                double rm3 = rm2 * System.Math.Sqrt(rm2);

                dyout.Set(0, v);
                dyout.Set(3, r * (-1.0 / rm3));
            }
        }

        private static (V3 rf, V3 vf) IntegrateTwoBody(V3 r0, V3 v0, double tau)
        {
            double vs = System.Math.Sqrt(Mu / R0);
            double ts = R0 / vs;

            var solver = new MechJebLib.ODE.DP5 { Rtol = 1e-9, Hmin = Statics.EPS, ThrowOnMaxIter = true, Maxiter = 5000 };
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

        private static void AssertRel(V3 actual, V3 expected, double relTol, string what)
        {
            double scale = System.Math.Max(expected.magnitude, 1.0);
            double err = (actual - expected).magnitude / scale;
            Assert.True(err <= relTol,
                $"{what}: 相对误差 {err:E3} 超出容差 {relTol:E3}\n  actual   = ({actual.x:R},{actual.y:R},{actual.z:R})\n  expected = ({expected.x:R},{expected.y:R},{expected.z:R})");
        }
    }
}
