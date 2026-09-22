using System;
using System.Collections.Generic;
using MechJebLib.Functions;
using MechJebLib.Maneuvers;
using MechJebLib.Primitives;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 地表朝向那一族的**纯三角件 + `OperationLan`**（`LAN`；`Longitude`/`Stationary` 待做）。
    /// </summary>
    public class SurfaceOperationTests
    {
        private static readonly double Mu = OrbitMath.GM;
        private const double R0 = 7_000_000.0;
        private static readonly V3 Pole = new V3(0, 0, 1);
        private const double BodyR = 5_000_000.0;

        /// <summary>倾角 30° 的圆轨（**必须非赤道**，否则"赤道交点"不存在、`DeltaVToShiftLAN` 会抛）。</summary>
        private static OrbitView Inclined30()
        {
            double vc = System.Math.Sqrt(Mu / R0);
            double inc = System.Math.PI / 6;
            return OrbitView.FromStateVectors(new V3(R0, 0, 0),
                new V3(0, vc * System.Math.Cos(inc), vc * System.Math.Sin(inc)), Mu, 0.0, Pole, BodyR);
        }

        /// <summary>轨道的**升交点惯性系经度（度）**——与 `DeltaVToShiftLAN` 的目标同口径。</summary>
        private static double LanDeg(OrbitView o, double ut, double omega = 0.0)
        {
            V3 an = o.AscendingNodePositionEquatorial(ut);
            DVector3 g = OrbitView.ToD(OrbitView.AdapterToGameWorld(an));
            double lonFixed = SurfaceFrame.EciToLongLat(g, omega, ut).lonRad;
            double deg = (lonFixed + omega * ut) * 180.0 / System.Math.PI;
            double m = deg % 360.0;
            return m < 0.0 ? m + 360.0 : m;
        }

        // ── 纯三角件（可以直接心算） ──────────────────────────────────────────────

        [Fact]
        public void HeadingAndDistance_AreCorrectForCardinalDirections()
        {
            // 赤道上从 (0,0) 到 (0,90)：正东 ⇒ 航向 90°、角距 90°
            Assert.Equal(90.0, OrbitalManeuverCalculator.Heading(0, 0, 0, 90), 9);
            Assert.Equal(90.0, OrbitalManeuverCalculator.Distance(0, 0, 0, 90), 9);

            // 从 (0,0) 到 (10,0)：正北 ⇒ 航向 0°、角距 10°
            Assert.Equal(0.0, OrbitalManeuverCalculator.Heading(0, 0, 10, 0), 9);
            Assert.Equal(10.0, OrbitalManeuverCalculator.Distance(0, 0, 10, 0), 9);

            // 对跖点：角距 180°
            Assert.Equal(180.0, OrbitalManeuverCalculator.Distance(0, 0, 0, 180), 9);
        }

        // ── 守卫与告警 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 赤道轨**没有**赤道交点 ⇒ 必须明确抛（**上游同款**：它抛 `ArgumentException`；我们抛
        /// `OperationException`，为的是让 `Operation.MakeNodes` 能把这句话原样交给玩家，见该函数的注释）。
        /// </summary>
        [Fact]
        public void ShiftLan_OnEquatorialOrbit_Throws()
        {
            var o = OrbitView.FromStateVectors(new V3(R0, 0, 0), new V3(0, System.Math.Sqrt(Mu / R0), 0), Mu, 0.0, Pole, BodyR);
            Assert.False(o.AscendingNodeEquatorialExists());
            Assert.Throws<OperationException>(() => OrbitalManeuverCalculator.DeltaVToShiftLAN(o, 0.0, 45.0));
        }

        /// <summary>低倾角告警：**非致命**（节点照常产出，只是带一句 `ErrorMessage`）。判据见 `OperationLan` 的类注释。</summary>
        [Fact]
        public void OperationLan_WarnsOnLowInclination_ButStillProducesNode()
        {
            double vc = System.Math.Sqrt(Mu / R0);
            double inc = 5.0 * System.Math.PI / 180.0; // 5° < 10° 门限
            var o = OrbitView.FromStateVectors(new V3(R0, 0, 0),
                new V3(0, vc * System.Math.Cos(inc), vc * System.Math.Sin(inc)), Mu, 0.0, Pole, BodyR);

            var op = new OperationLan();
            op.TimeSelector._currentTimeRef = 2; // X_FROM_NOW
            // ⚠ 点火时刻要离开赤道面：本轨道在 t=0 时**正好**在赤道（升交点）上，
            // 而那属于"解不存在"的另一个守卫（见 OperationLan 的类注释）——用 0 提前量会让本用例
            // 测的变成那个守卫、而不是"告警非致命"。
            op.TimeSelector.LeadTime.Val = OffEquatorBurnTime(o);
            op.NewLAN.Val = 45.0;

            List<PlanNode>? nodes = op.MakeNodes(o, 0.0, new FixedTargetView(o));
            Assert.NotNull(nodes);                                  // 非致命 ⇒ 仍然产出
            Assert.False(string.IsNullOrEmpty(op.GetErrorMessage())); // 但带着告警
        }

        // ── L2 oracle：请求的 LAN 要真的达成 ──────────────────────────────────────

        // ── L2 oracle：请求的 LAN 要真的达成 ──────────────────────────────────────

        /// <summary>离赤道最远的点火时刻（1/4 周期处；轨道在 t=0 过升交点）——本操作的解**只在这个域里存在**。</summary>
        private static double OffEquatorBurnTime(OrbitView o) => o.period / 4.0;

        /// <summary>`X_FROM_NOW` + 提前量（秒）。`OperationLan` 的候选表 = {APOAPSIS, PERIAPSIS, X_FROM_NOW} ⇒ 下标 2。</summary>
        private static OperationLan LanOpAt(double leadSeconds, double wantLan)
        {
            var op = new OperationLan();
            op.TimeSelector._currentTimeRef = 2; // X_FROM_NOW
            op.TimeSelector.LeadTime.Val = leadSeconds;
            op.NewLAN.Val = wantLan;
            return op;
        }

        /// <summary>两个角度（度）之差，取 `[0,180]`。</summary>
        private static double AngDiff(double a, double b)
        {
            double d = (a - b) % 360.0;
            if (d > 180.0) d -= 360.0;
            if (d < -180.0) d += 360.0;
            return System.Math.Abs(d);
        }

        /// <summary>
        /// **上游那个"地面导航"算式，在测试里独立重写一遍**（`MechJeb2/OrbitalManeuverCalculator.cs:338`）：
        /// 把烧点与"赤道上目标经度的那个点"喂进大圆初航向，让水平速度朝那个方位飞。
        /// 用途：证明新实现在**上游有效的域内**与它逐位等价（没偷偷改物理）。
        /// </summary>
        private static V3 UpstreamGroundPointDv(OrbitView o, double ut, double newLanDeg)
        {
            const double d2r = System.Math.PI / 180.0;
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(ut);

            var latLon = SurfaceFrame.EciToLongLat(OrbitView.ToD(OrbitView.AdapterToGameWorld(r)), o.angularVelocity, ut);
            double burnLat = latLon.latRad / d2r;
            double burnLon = (latLon.lonRad + o.angularVelocity * ut) / d2r; // 惯性系经度 = 上游的 `+ rotationAngle`

            double targetLon = o.TimeOfDescendingNodeEquatorial(ut) < o.TimeOfAscendingNodeEquatorial(ut)
                ? ((newLanDeg + 180.0) % 360.0 + 360.0) % 360.0
                : ((newLanDeg % 360.0) + 360.0) % 360.0;

            double heading = OrbitalManeuverCalculator.Heading(burnLat, burnLon, 0.0, targetLon);

            V3 up = o.Up(ut);
            V3 vOrb = o.RightHandedOrbitalVelocityAtUT(ut);
            V3 vh = vOrb - V3.Dot(up, vOrb) * up;
            V3 desired = (o.East(ut) * System.Math.Sin(heading * d2r)
                          + o.North(ut) * System.Math.Cos(heading * d2r)) * vh.magnitude;
            return desired - vh;
        }

        /// <summary>
        /// **请求一个 LAN ⇒ 施加 Δv 后轨道的升交点经度要精确等于它。**
        ///
        /// ⚠ **仪器修正（2026-09-19）**：本用例原先用默认的 `APOAPSIS` 时刻，而测试轨道是**圆轨**
        /// ⇒ 拱点任意 ⇒ 那个时刻正好落在**赤道**（交点）上 ⇒ 把被测件推进了本操作的**奇点**（解不存在），
        /// 于是"红字"其实是我的仪器造成的。现在把点火时刻显式放在**离赤道最远**处（1/4 周期，纬度 = 倾角）。
        /// 该域内的解是**代数精确**的 ⇒ 容差 1e-5°，实测误差 0.00°。
        /// 奇点域另有专门用例（`OperationLan_AtEquatorialBurnPoint_RefusesInsteadOfBurning`）钉住。
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(45.0)]
        [InlineData(120.0)]
        [InlineData(300.0)]
        public void OperationLan_AchievesRequestedLan(double wantLan)
        {
            var o = Inclined30();
            OperationLan op = LanOpAt(OffEquatorBurnTime(o), wantLan);

            List<PlanNode>? nodes = op.MakeNodes(o, 0.0, new FixedTargetView(o));
            Assert.NotNull(nodes);
            PlanNode node = nodes![0];

            (V3 r0, V3 v0) = o.RightHandedStateVectorsAtUT(node.ut);
            var after = OrbitView.FromStateVectors(r0, v0 + node.dV, Mu, node.ut, Pole, BodyR);

            double got = LanDeg(after, node.ut);
            double err = AngDiff(got, wantLan);

            Assert.True(err < 1e-5,
                $"请求 LAN={wantLan}°，实得 {got:F6}°（误差 {err:E3}°）｜|Δv|={node.dV.magnitude:F3} m/s");
        }

        /// <summary>
        /// **在"上游有效的域内"与上游地面导航模型逐位等价。**
        /// 实测（2026-09-19）：LAN=45° 两边都 2456.16 m/s、LAN=120° 都 1497.26 m/s。
        /// 这条同时是"我们没换物理、只换了求解方式"的证据。
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(45.0)]
        [InlineData(120.0)]
        [InlineData(300.0)]
        public void ShiftLan_MatchesUpstreamGroundPointModelOffEquator(double wantLan)
        {
            var o = Inclined30();
            double ut = OffEquatorBurnTime(o);

            V3 mine = OrbitalManeuverCalculator.DeltaVToShiftLAN(o, ut, wantLan);
            V3 theirs = UpstreamGroundPointDv(o, ut, wantLan);

            Assert.True(mine.magnitude > 1.0, "这个轨道上改 LAN 不该是零 Δv——否则用例没在测东西");
            Assert.True((mine - theirs).magnitude <= 1e-6 * mine.magnitude,
                $"LAN={wantLan}°：新解 {mine.magnitude:F6} vs 上游模型 {theirs.magnitude:F6} m/s" +
                $"（矢量差 {(mine - theirs).magnitude:E3}）");
        }

        /// <summary>
        /// **契约：纯平面变更**——改 LAN 只许动平面：`|v|`（⇒ 能量）与径向分量（⇒ 拱线几何）必须守恒。
        /// 这正是"最小 Δv"的可判定表述：若解顺带改了能量，那它就多烧了。
        /// </summary>
        [Fact]
        public void ShiftLan_ChangesOnlyThePlane()
        {
            var o = Inclined30();
            double ut = OffEquatorBurnTime(o);
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(ut);

            V3 v2 = v + OrbitalManeuverCalculator.DeltaVToShiftLAN(o, ut, 45.0);

            // 三条**相对**判据（绝对容差在 7e6 m 的轨道上没有意义）。
            // 它们就是"最小 Δv"的定义：只许动平面，别的都不许动。
            double dvRel = System.Math.Abs(v2.magnitude - v.magnitude) / v.magnitude;
            double vrRel = System.Math.Abs(V3.Dot(v2, r.normalized) - V3.Dot(v, r.normalized)) / v.magnitude;
            double hRel = System.Math.Abs(V3.Cross(r, v2).magnitude - V3.Cross(r, v).magnitude) / V3.Cross(r, v).magnitude;

            Assert.True(dvRel < 1e-12, $"|v| 必须守恒（相对差 {dvRel:E3}）");
            Assert.True(vrRel < 1e-12, $"径向分量必须守恒（相对差 {vrRel:E3}）");
            Assert.True(hRel < 1e-12, $"|h| 必须守恒（相对差 {hRel:E3}）");

            var after = OrbitView.FromStateVectors(r, v2, Mu, ut, Pole, BodyR);
            double aRel = System.Math.Abs(after.semiMajorAxis - o.semiMajorAxis) / o.semiMajorAxis;
            Assert.True(aRel < 1e-9,
                $"SMA 必须守恒（相对差 {aRel:E3}；o={o.semiMajorAxis:R} after={after.semiMajorAxis:R}）");

            double peRel = System.Math.Abs(Astro.PeriapsisFromStateVectors(Mu, r, v2)
                                           - Astro.PeriapsisFromStateVectors(Mu, r, v)) / o.semiMajorAxis;
            // ⚠ 这条容差**不能再紧**，而且原因要说清楚：`近心点半径 = a(1−e)`，而移植来的
            // `Astro.EccFromStateVectors` 在**近圆**轨道上的噪声地板是 ~1e-8（本轮实测 1.49e-8，正是 √ε 量级，
            // 属 F2/R6 那一族"近圆噪声"）。**精确的不变量是上面的 `|h|`/能量**，peR 只是它们的派生读数
            // ⇒ 这里同时把"被测轨道确实是近圆的"断言出来，否则这条容差就成了掩盖问题的橡皮尺。
            Assert.True(Astro.EccFromStateVectors(Mu, r, v) < 1e-6,
                $"前提：本用例的轨道是近圆的（实测 e={Astro.EccFromStateVectors(Mu, r, v):E3}）⇒ peR 的读数受噪声地板限制");
            Assert.True(peRel < 1e-6, $"近心点半径必须守恒（相对差 {peRel:E3}）");

            // 反例护栏：倾角**本就该变**（把节点挪到指定经度通常要连倾角一起变）——若它没变，用例没在测"挪节点"
            Assert.True(System.Math.Abs(after.inclination - o.inclination) > 1e-9,
                "这个例子里的倾角本就该变；没变说明没在测'挪节点'");
        }

        /// <summary>
        /// ⚠ **这就是 2026-09-18 那条红字的复现路径**：`APOAPSIS` + 圆轨 ⇒ 点火点落在赤道（交点）上。
        /// 那时它静默烧了 2766.89 m/s 去**改倾角**、请求的 LAN 完全没被满足；
        /// 现在必须**明确拒绝且不产出节点**，并把"可达到的经度"告诉玩家。
        /// </summary>
        [Fact]
        public void OperationLan_AtEquatorialBurnPoint_RefusesInsteadOfBurning()
        {
            var o = Inclined30();
            var op = new OperationLan();
            op.TimeSelector._currentTimeRef = 0; // APOAPSIS —— 圆轨上拱点任意，这个布局下正好落在交点
            op.NewLAN.Val = 45.0;

            // 前提自检：这个时刻**确实**在赤道面内。否则本用例测的就不是它自己声称的东西。
            double ut = op.TimeSelector.ComputeManeuverTime(o, 0.0, new FixedTargetView(o));
            (V3 r, V3 _) = o.RightHandedStateVectorsAtUT(ut);
            Assert.True(System.Math.Abs(V3.Dot(r.normalized, Pole)) < 1e-9,
                $"前提不成立：这个点火时刻不在赤道面内（|r̂·极轴| = {System.Math.Abs(V3.Dot(r.normalized, Pole)):E3}）");

            List<PlanNode>? nodes = op.MakeNodes(o, 0.0, new FixedTargetView(o));
            Assert.Null(nodes);

            string msg = op.GetErrorMessage() ?? "";
            Assert.Contains("赤道面内", msg);
            Assert.Contains("请另选点火时刻", msg);
        }

        /// <summary>退化支：请求的经度**就是**当前升交点经度 ⇒ 本来就已满足 ⇒ Δv 必须是零（而不是抛）。</summary>
        [Fact]
        public void ShiftLan_AtNodeLine_RequestingCurrentLanIsAlreadySatisfied()
        {
            var o = Inclined30();
            double ut = o.TimeOfAscendingNodeEquatorial(0.0); // 正好在升交点，且在赤道面内
            double lanNow = LanDeg(o, ut);

            V3 dv = OrbitalManeuverCalculator.DeltaVToShiftLAN(o, ut, lanNow);
            Assert.True(dv.magnitude < 1e-6, $"已满足的请求不该要 Δv，实得 {dv.magnitude:E3} m/s");
        }

        /// <summary>退化支：请求的是**对面**（升交点已在对面 ⇒ 要翻面）⇒ 本计划不做 ⇒ 明确拒绝。</summary>
        [Fact]
        public void ShiftLan_AtNodeLine_RequestingOppositeLanRefuses()
        {
            var o = Inclined30();
            double ut = o.TimeOfAscendingNodeEquatorial(0.0);
            double lanNow = LanDeg(o, ut);

            Assert.Throws<OperationException>(() =>
                OrbitalManeuverCalculator.DeltaVToShiftLAN(o, ut, lanNow + 180.0));
        }

        // ── 改拱点地面经度（OperationLongitude）：改周期、不改位置 ─────────────────

        /// <summary>1 游戏天 = 60000 s（与 `OrbitAdapter.AngularVelocity` 同一口径）。</summary>
        private const double Omega = 2.0 * System.Math.PI / 60000.0;

        private static OrbitView EquatorialRotating()
        {
            double vc = System.Math.Sqrt(Mu / R0);
            return OrbitView.FromStateVectors(new V3(R0, 0, 0), new V3(0, vc, 0), Mu, 0.0,
                Pole, BodyR, double.NaN, Omega);
        }

        private static OrbitView Inclined30Rotating()
        {
            double vc = System.Math.Sqrt(Mu / R0);
            double inc = System.Math.PI / 6;
            return OrbitView.FromStateVectors(new V3(R0, 0, 0),
                new V3(0, vc * System.Math.Cos(inc), vc * System.Math.Sin(inc)), Mu, 0.0,
                Pole, BodyR, double.NaN, Omega);
        }

        /// <summary>`pos`（**惯性**矢量）在 `t` 时刻**正下方**那条经线的经度（度）。</summary>
        private static double FixedLonDeg(V3 pos, double t)
        {
            DVector3 g = OrbitView.ToD(OrbitView.AdapterToGameWorld(pos));
            double deg = SurfaceFrame.EciToLongLat(g, Omega, t).lonRad * 180.0 / System.Math.PI;
            double m = deg % 360.0;
            return m < 0.0 ? m + 360.0 : m;
        }

        /// <summary>
        /// **一圈之后，拱点必须正好压到目标经线上。**
        /// 判据直接量"那个时刻这一点正下方是哪条经线"（把 `ut + 新周期` 喂给 `SurfaceFrame`），
        /// 而不是相信我推的 `固连 = 惯性 − ωt` 那套符号——**仪器自己站得住**才算数。
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(90.0)]
        [InlineData(200.0)]
        [InlineData(359.0)]
        public void OperationLongitude_PutsApsisOverTargetLongitudeOneOrbitLater(double want)
        {
            var o = EquatorialRotating();
            double ut = OffEquatorBurnTime(o); // 圆轨上"拱点"任意，取 1/4 周期（与 LAN 用例同一习惯）

            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(ut);
            V3 v2 = v + OrbitalManeuverCalculator.DeltaVToShiftNodeLongitude(o, ut, want);
            var after = OrbitView.FromStateVectors(r, v2, Mu, ut, Pole, BodyR, double.NaN, Omega);

            double lonAtReturn = FixedLonDeg(r, ut + after.period);
            double err = AngDiff(lonAtReturn, want);
            // 容差也是量出来的：把门限逼到 1e-12° 仍然全绿（这条关系在代数上是精确的，
            // 残差只来自 `周期↔SMA↔状态矢量` 的浮点往返）⇒ 定在 1e-9°，留三个数量级余量。
            Assert.True(err < 1e-9,
                $"请求地面经度 {want}°，一圈后实得 {lonAtReturn:F6}°（差 {err:E3}°）" +
                $"｜新周期 {after.period:F1}s｜|Δv|={MechJebLib.Primitives.V3.Distance(v2, v):F3} m/s");
        }

        /// <summary>
        /// **契约：只改周期**——在拱点做的切向燃烧只动 SMA：平面、拱点位置都不许动，
        /// 且"对面那个拱点"必须留在天体之外（`N` 循环存在的唯一理由）。
        /// </summary>
        [Fact]
        public void ShiftNodeLongitude_ChangesOnlyThePeriodAndKeepsPlane()
        {
            var o = Inclined30Rotating();
            double ut = OffEquatorBurnTime(o);
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(ut);

            V3 dv = OrbitalManeuverCalculator.DeltaVToShiftNodeLongitude(o, ut, 120.0);
            V3 v2 = v + dv;

            // ① 切向：Δv 与速度平行（否则会顺带改偏心率/拱线方向）
            Assert.True(MechJebLib.Primitives.V3.Cross(dv, v.normalized).magnitude < 1e-6 * dv.magnitude,
                $"拱点燃烧必须是切向的（横向分量 {MechJebLib.Primitives.V3.Cross(dv, v.normalized).magnitude:E3}）");

            // ② 点火点仍是新轨道的一个拱点
            double pe = Astro.PeriapsisFromStateVectors(Mu, r, v2);
            double ap = Astro.ApoapsisFromStateVectors(Mu, r, v2);
            Assert.True(System.Math.Abs(pe - r.magnitude) < 1e-5 * R0 || System.Math.Abs(ap - r.magnitude) < 1e-5 * R0,
                $"点火点必须仍是拱点（pe={pe:F1} ap={ap:F1} r={r.magnitude:F1}）");

            // ③ 平面不变（倾角与升交点经度都不许动）
            var after = OrbitView.FromStateVectors(r, v2, Mu, ut, Pole, BodyR, double.NaN, Omega);
            Assert.True(System.Math.Abs(after.inclination - o.inclination) < 1e-9,
                $"平面不许变：倾角 {o.inclination:E12} → {after.inclination:E12}");

            // ④ 对面那个拱点必须在天体之外（N 循环的用途）
            double outer = System.Math.Max(pe, ap);
            Assert.True(outer > BodyR, $"对面拱点必须在天体之外（外拱点 {outer:F0} m，天体半径 {BodyR:F0} m）");
        }

        /// <summary>没有自转数据 ⇒ 明说做不到，而不是拿 ω=0 去算出一个假周期。</summary>
        [Fact]
        public void ShiftNodeLongitude_WithoutRotationData_Throws()
        {
            var o = Inclined30(); // ω = 0
            Assert.Equal(0.0, o.angularVelocity);
            Assert.Throws<OperationException>(() =>
                OrbitalManeuverCalculator.DeltaVToShiftNodeLongitude(o, 0.0, 90.0));
        }
    }
}
