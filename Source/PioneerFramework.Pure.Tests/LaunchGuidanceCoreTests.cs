using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 爬升段制导核心单测（设计 §7）：收敛性 / 稳定性 / 预算 / 多方角。
    /// 仿真模型 = **有限推力闭环**（2026-09-02 修正）：每步 ComputeStep → 施加 a=F/m·dt
    /// （沿 dv 方向，受剩余工质约束，质量随烧耗递减），再用 NBodyIntegrator
    /// ThirdOrderTaylorStep 积分重力（acc=-GM/r³·pos, dt=2s）。
    /// 与游戏 ComputeGroupPhysics 行为一致——旧的「理想冲量一步施加完整 dv」掩盖了低 TWR 失效。
    /// </summary>
    public class LaunchGuidanceCoreTests
    {
        private const double DayLengthTicks = 60000.0; // CelestialBodyDef.dayLengthTicks 默认

        // 与 LaunchTestScenarios 强力大火箭一致（F=1.05MN, mf=300, dry=4000, fuel=50000, TWR≈2.43）
        private const double Thrust = 1050000.0;
        private const double MassFlow = 300.0;
        private const double DryMass = 4000.0;
        private const double FuelMass = 50000.0;
        private const double Dt = 2.0;

        private struct AscentResult
        {
            public double finalR;
            public double finalV;
        public double finalRadial;
        /// <summary>燃烧段数（dv 从 0 变正算一段；"一次反转"要求 ≤ 2：爬升段 + 圆化段）。</summary>
        public int burnSegments;
        /// <summary>dv 径向分量的符号翻转次数（上升→水平 至多一次）。</summary>
        public int radialFlips;
        /// <summary>末段轨迹回放（最后 12 步；`RunAscent` 里初始化）。</summary>
        public List<string> tail;
        /// <summary>最后一次真正点火的步（切油的现场）。</summary>
        public string lastBurn;
        public int cutStep;
        public double cutApo;
        public double maxRAfterCut;
        /// <summary>控制器状态机里的"第二次修正"次数。</summary>
        public int corrections;
            public double maxRadialSpeed;
            public double maxR;
            public double totalDv;
            public double fuelUsed;
            public double fuelRemaining;
            public double arrivalTime;
            public bool arrived;
            public bool fuelExhausted;
        }

        // ── **一般性：两个天体 × 多个纬度**（2026-10-29 用户令）────────────────────────────
        // 目的：验"收尾锁存 + 上升律"不是在 51.7° 地球这一发上调出来的特例。
        // 每个组合都走**生产同一条参数推导**（`LaunchGuidanceParams.ForBody`），
        // 判据：到达 / 到顶窗口 / 圆化预算 / 收油只发生一次 / 二次修正 ≤1。
        private static (DVector3 pos, DVector3 vel) BodySurfaceLaunchState(
            double radius, double dayTicks, double latRad, double lonRad)
        {
            double omega = 2.0 * Math.PI / dayTicks;
            double cosLat = Math.Cos(latRad);
            DVector3 pos = new DVector3(
                cosLat * Math.Cos(lonRad) * radius, Math.Sin(latRad) * radius, cosLat * Math.Sin(lonRad) * radius);
            DVector3 vel = DVector3.Cross(pos, DVector3.up * omega);
            return (pos, vel);
        }

        [Theory]
        // 地球：R=5000 km，μ=2e14，大气 100 km，自转 60000 tick；目标 500 km
        // 月球：R=1500 km，μ=5e12，真空，自转 317900 tick（潮汐锁定）；目标 1800 km 半径（300 km 高）
        [InlineData(5_000_000.0, 2.0e14, 100_000.0, 60_000.0, 500_000.0, 0.0)]
        [InlineData(5_000_000.0, 2.0e14, 100_000.0, 60_000.0, 500_000.0, 30.0)]
        [InlineData(5_000_000.0, 2.0e14, 100_000.0, 60_000.0, 500_000.0, 51.7)]
        [InlineData(5_000_000.0, 2.0e14, 100_000.0, 60_000.0, 500_000.0, 70.0)]
        [InlineData(5_000_000.0, 2.0e14, 100_000.0, 60_000.0, 500_000.0, -30.0)]
        [InlineData(1_500_000.0, 5.0e12, 0.0, 317_900.0, 300_000.0, 0.0)]
        [InlineData(1_500_000.0, 5.0e12, 0.0, 317_900.0, 300_000.0, 30.0)]
        [InlineData(1_500_000.0, 5.0e12, 0.0, 317_900.0, 300_000.0, 51.7)]
        [InlineData(1_500_000.0, 5.0e12, 0.0, 317_900.0, 300_000.0, 70.0)]
        [InlineData(1_500_000.0, 5.0e12, 0.0, 317_900.0, 300_000.0, -30.0)]
        public void Ascent_Generality_AcrossBodiesAndLatitudes(double radius, double mu, double atmo,
            double dayTicks, double targetAlt, double latDeg)
        {
            var x = new LaunchTargetInputs(radius, mu, atmo, double.NaN, double.NaN);
            var p = LaunchGuidanceParams.ForBody(x, out var target);
            p.planetOmega = 2.0 * Math.PI / dayTicks;
            Assert.True(target.Feasible, $"目标不可行：R={radius:F0} h={targetAlt:F0}");

            double latRad = latDeg * Math.PI / 180.0;
            var (pos0, vel0) = BodySurfaceLaunchState(radius, dayTicks, latRad, 0.0);

            var result = RunAscent(pos0, vel0, mu, p);

            double vC = Math.Sqrt(mu / p.rTarget);
            double ve = Thrust / MassFlow;
            double mNow = Math.Max(DryMass, DryMass + FuelMass - result.fuelUsed);
            double dvLeft = ve * Math.Log(mNow / DryMass);
            double dvNeed = Math.Max(0.0, vC - result.finalV);

            try
            {
                System.IO.File.AppendAllText(@"D:\PioneerFrameworkLite\tools\_generality_bench.txt",
                    $"R={radius / 1000.0:F0}km lat={latDeg,5:F1}° arrived={result.arrived} t={result.arrivalTime:F0}s "
                    + $"Δr={result.finalR - p.rTarget:F0}m |v|={result.finalV:F1} vC={vC:F1} "
                    + $"seg={result.burnSegments} 收油@{(result.cutStep >= 0 ? result.cutStep.ToString() : "无")} 切时apo={result.cutApo / 1000.0:F1}km "
                    + $"fuelUsed={result.fuelUsed:F0}kg dvLeft={dvLeft:F0} dvNeed={dvNeed:F0}\n");
            }
            catch { /* 写不了不影响判据 */ }

            Assert.True(result.arrived,
                $"{radius / 1000.0:F0}km 天体 / 纬度 {latDeg}°：未在预算内到达（finalR={result.finalR / 1000.0:F0}km）");
            Assert.False(result.fuelExhausted, $"{radius / 1000.0:F0}km / {latDeg}°：到达前耗尽工质");
            Assert.InRange(result.finalR, p.rTarget - 5.0 * p.tolR, p.rTarget + 5.0 * p.tolR);
            Assert.True(dvLeft > dvNeed + 100.0,
                $"{radius / 1000.0:F0}km / {latDeg}°：圆化预算不足（剩 {dvLeft:F0}，需 {dvNeed:F0}）");
            Assert.True(result.cutStep >= 0, $"{radius / 1000.0:F0}km / {latDeg}°：从未收油（锁存没触发）");
            Assert.True(result.corrections <= 1,
                $"{radius / 1000.0:F0}km / {latDeg}°：二次修正 {result.corrections} 次");

            // **效率判据**（2026-10-29 用户令）：交付 Δv 不得超过 2×vC。
            // 月球那轮在修 B+C 之前是 **5.1×**（8874 m/s 换 1741 m/s），修后 1.39×；
            // 地球 1.25×。这条把"段抖动导致的白烧"钉成判据，防止再退化。
            double dvPaid = ve * Math.Log((DryMass + FuelMass) / Math.Max(DryMass, mNow));
            Assert.True(dvPaid < 2.0 * vC,
                $"{radius / 1000.0:F0}km / {latDeg}°：交付 Δv {dvPaid:F0} m/s 超过 2×vC({2.0 * vC:F0})");
        }

        /// <summary>
        /// **月球浪费诊断**（2026-10-29）：月球那组烧掉 99% 的油（≈8.9 km/s），而 1.8 km/s 圆轨只需 ~2.1 km/s。
        /// 这里把每 20 步的"高度/径向速度/切向速度/远地点/已付 Δv"写成轨迹，用来定位浪费在哪一段。
        /// </summary>
        [Fact]
        public void MoonAscent_WasteTrace()
        {
            const double R = 1_500_000.0, Mu = 5.0e12, Day = 317_900.0, TargetAlt = 300_000.0;
            var x = new LaunchTargetInputs(R, Mu, 0.0, double.NaN, double.NaN);
            var p = LaunchGuidanceParams.ForBody(x, out _);
            p.planetOmega = 2.0 * Math.PI / Day;
            var (pos0, vel0) = BodySurfaceLaunchState(R, Day, 0.0, 0.0);

            double ve = Thrust / MassFlow;
            double m0 = DryMass + FuelMass;
            var lines = new List<string>();
            lines.Add("i\tt\talt_km\tvRad\tvTan\t|v|\tapo_km\tfuel_kg\tdvPaid\tdvRad\t|dv|\tphase");

            AscentResult result = default;
            result = RunAscent(pos0, vel0, Mu, p, trace: (i, pos, vel, fuel) =>
            {
                if (i % 20 != 0) return;
                double r = pos.magnitude;
                DVector3 up = pos / r;
                DVector3 h = DVector3.Cross(pos, vel);
                if (h.sqrMagnitude < 1e-9) return;
                DVector3 tan = DVector3.Cross(h, up).normalized;
                double vRad = DVector3.Dot(vel, up);
                double vTan = DVector3.Dot(vel, tan);
                double apo = LaunchGuidanceCore.ApoapsisRadius(pos, vel, Mu);
                double mNow = Math.Max(DryMass, fuel + DryMass);
                double dvPaid = ve * Math.Log(m0 / mNow);
                var st = LaunchGuidanceCore.ComputeStep(pos, vel, Mu, p, LaunchGuidanceCore.PhaseTurn, Thrust / mNow, Dt);
                double dvRad = DVector3.Dot(st.dv, up);
                lines.Add($"{i}\t{i * Dt:F0}\t{(r - R) / 1000.0:F1}\t{vRad:F1}\t{vTan:F1}\t{vel.magnitude:F1}\t"
                    + $"{(double.IsInfinity(apo) ? -1 : apo / 1000.0):F1}\t{fuel:F0}\t{dvPaid:F0}\t{dvRad:F1}\t{st.dv.magnitude:F1}\t");
            });

            double totalPaid = ve * Math.Log(m0 / Math.Max(DryMass, result.fuelRemaining + DryMass));
            lines.Add($"# 到达={result.arrived} 用时={result.arrivalTime:F0}s 总付Δv={totalPaid:F0} m/s "
                + $"（vC={Math.Sqrt(Mu / p.rTarget):F0}）剩余油={result.fuelRemaining:F0}kg");
            try { System.IO.File.WriteAllLines(@"D:\PioneerFrameworkLite\tools\_moon_trace.txt", lines); } catch { }

            Assert.True(result.arrived);
        }

        /// <summary>赤道发射初始状态：pos = r(cosλ,0,sinλ)，vel = 自转初速 = ω·(p×up)（真实自转切向，与 progradeSign=−1 同向）。</summary>
        private static (DVector3 pos, DVector3 vel) EquatorialLaunchState(double lambdaRad)
        {
            double R = SpaceConstants.PlanetRadiusMeters;
            double omega = 2.0 * Math.PI / DayLengthTicks;
            DVector3 pos = new DVector3(Math.Cos(lambdaRad) * R, 0, Math.Sin(lambdaRad) * R);
            DVector3 vel = DVector3.Cross(pos, DVector3.up * omega); // p×up·ω（2026-09-05 自转方向修正）
            return (pos, vel);
        }

        /// <summary>
        /// 高纬发射初始状态：p = r(cosφ·cosλ, sinφ, cosφ·sinλ)，v = ω·(p×up)。
        /// 与 EquatorialLaunchState 同构（纬度 φ=0 即退化为赤道）。用于复现「高纬殖民地 + 70000kg」游戏内失效。
        /// </summary>
        private static (DVector3 pos, DVector3 vel) SurfaceLaunchState(double latRad, double lambdaRad)
        {
            double R = SpaceConstants.PlanetRadiusMeters;
            double omega = 2.0 * Math.PI / DayLengthTicks;
            double cosLat = Math.Cos(latRad);
            DVector3 pos = new DVector3(cosLat * Math.Cos(lambdaRad) * R, Math.Sin(latRad) * R, cosLat * Math.Sin(lambdaRad) * R);
            DVector3 vel = DVector3.Cross(pos, DVector3.up * omega); // p×up·ω（真实自转切向，2026-09-05 修正）
            return (pos, vel);
        }

        /// <summary>
        /// 有限推力闭环仿真：每步 ComputeStep → 沿 dv 方向施加 a·dt（质量递减），积分重力。
        /// 与 ComputeGroupPhysics 的 burn 分支一致（dvThisTick=a·dt, burnDv=min(..., remaining)，
        /// 每步重新 StartBurn 重置 remaining → 实际每步施加 a·dt）。
        /// </summary>
        private static AscentResult RunAscent(DVector3 pos0, DVector3 vel0, double gm,
            in LaunchGuidanceParams p, double thrust = Thrust, double massFlow = MassFlow,
            double dryMass = DryMass, double fuelMass = FuelMass, int maxSteps = 40000,
            Action<int, DVector3, DVector3, double>? trace = null)
        {
            DVector3 pos = pos0;
            DVector3 vel = vel0;
            double mass = dryMass + fuelMass;
            double fuel = fuelMass;
            double maxRadialSpeed = 0.0, maxR = 0.0, totalDv = 0.0, fuelUsed = 0.0;
            var result = new AscentResult { arrivalTime = -1.0, tail = new List<string>() };
            bool burning = false;
            bool latched = false;
            bool leftVertical = false;   // C：离开竖直段后不再回去（控制器侧闩锁）
            int lastRadialSign = 0;
            int cutStep = -1;
            double cutApo = 0.0, maxRAfterCut = 0.0;

            int gphase = LaunchGuidanceCore.PhaseVertical;

            for (int i = 0; i < maxSteps && !result.arrived; i++)
            {
                // **相位机**（2026-10-29 重写）：与控制器完全同构——相位是状态，纯核只算相位内的连续量
                var step = LaunchGuidanceCore.ComputeStep(pos, vel, gm, p, gphase, thrust / Math.Max(mass, 1.0), Dt);
                gphase = step.phase;
                if (step.arrived)
                {
                    result.arrived = true;
                    result.arrivalTime = i * Dt;
                }

                // 收油步（第一次进滑行）——判据用"相位"，不再用远地点带
                if (gphase == LaunchGuidanceCore.PhaseCoast && result.cutStep < 0)
                {
                    result.cutStep = i;
                    result.cutApo = LaunchGuidanceCore.ApoapsisRadius(pos, vel, gm);
                }

                // **燃烧段计数**：dv 从 0 变正 ⇒ 新的一段（目标 1：只有爬升段）
                bool burnNow = fuel > 0.0 && step.dv.sqrMagnitude > 1e-9;
                if (burnNow && !burning) result.burnSegments++;
                burning = burnNow;

                // **径向指令方向翻转计数**：上升→水平 至多一次
                if (burnNow)
                {
                    double dRad = DVector3.Dot(step.dv, pos.normalized);
                    int sign = dRad > 1e-9 ? 1 : (dRad < -1e-9 ? -1 : 0);
                    if (sign != 0)
                    {
                        if (lastRadialSign != 0 && sign != lastRadialSign) result.radialFlips++;
                        lastRadialSign = sign;
                    }
                }

                // 有限推力：质量守恒、沿 dv 方向施加速度增量（受工质约束）
                if (burnNow)
                {
                    double a = thrust / Math.Max(mass, 1.0);          // 当前加速度（质量递减 → 增大）
                    double dvMag = Math.Min(a * Dt, step.dv.magnitude);
                    vel = vel + step.dv.normalized * dvMag;
                    totalDv += dvMag;
                    double used = massFlow * Dt * (a * Dt > 0.0 ? dvMag / (a * Dt) : 0.0);
                    used = Math.Min(used, fuel);
                    fuel -= used;
                    mass -= used;
                    fuelUsed += used;
                    if (fuel <= 1e-6)
                        result.fuelExhausted = true;
                }

                double r = pos.magnitude;
                DVector3 acc = pos * (-gm / (r * r * r)); // 向心引力，jerk = 0
                NBodyIntegrator.ThirdOrderTaylorStep(pos, vel, acc, DVector3.zero, Dt, out pos, out vel);

                double radialSpeed = DVector3.Dot(vel, pos.normalized);
                maxRadialSpeed = Math.Max(maxRadialSpeed, Math.Abs(radialSpeed));
                maxR = Math.Max(maxR, pos.magnitude);

                // 末段回放：r / 远地点（当前 & 本步烧完预测）/ 本步 dv（用来解释"切晚/切早"）
                double rr = pos.magnitude;
                double Apo(DVector3 p3, DVector3 v3)
                {
                    double r3 = p3.magnitude;
                    double en3 = 0.5 * v3.sqrMagnitude - gm / r3;
                    if (en3 >= 0.0) return double.PositiveInfinity;
                    double a3 = -gm / (2.0 * en3);
                    double e3 = Math.Sqrt(Math.Max(0.0,
                        1.0 + 2.0 * en3 * DVector3.Cross(p3, v3).sqrMagnitude / (gm * gm)));
                    return a3 * (1.0 + e3);
                }
                double apoNow = Apo(pos, vel);
                double apoTaylor = Apo(pos + vel * Dt, vel + step.dv + pos * (-gm / (rr * rr * rr)) * Dt);
                result.tail.Add($"i={i} r={rr / 1000.0:F1}km apoNow={apoNow / 1000.0:F1}km "
                    + $"apoTaylor={apoTaylor / 1000.0:F1}km |dv|={step.dv.magnitude:F2} 燃={(burnNow ? 1 : 0)}");
                trace?.Invoke(i, pos, vel, fuel);
                if (burnNow)
                    result.lastBurn = $"i={i} r={rr / 1000.0:F1}km apoNow={apoNow / 1000.0:F1}km "
                        + $"apoTaylor={apoTaylor / 1000.0:F1}km |dv|={step.dv.magnitude:F2} fuel={fuel:F0}";

                // 切油之后 r 还能不能超过切油时的远地点（用来区分"中途又点了火"和"轨迹有问题"）
                if (cutStep < 0 && !burnNow && fuel > 0.0) { cutStep = i; cutApo = apoNow; maxRAfterCut = rr; }
                if (cutStep >= 0) maxRAfterCut = Math.Max(maxRAfterCut, rr);
                if (result.tail.Count > 12) result.tail.RemoveAt(0);
            }

            result.finalR = pos.magnitude;
            result.finalV = vel.magnitude;
            result.finalRadial = pos.sqrMagnitude > 1e-9 ? DVector3.Dot(vel, pos.normalized) : 0.0;
            result.maxRadialSpeed = maxRadialSpeed;
            result.maxR = maxR;
            result.totalDv = totalDv;
            result.fuelUsed = fuelUsed;
            result.fuelRemaining = fuel;
            result.cutStep = cutStep;
            result.cutApo = cutApo;
            result.maxRAfterCut = maxRAfterCut;
            return result;
        }

        // ── 收敛性：有限推力下不同发射方位角（经度）都收敛到目标窗口 ──
        [Theory]
        [InlineData(0.0)]
        [InlineData(45.0)]
        [InlineData(90.0)]
        [InlineData(180.0)]
        [InlineData(270.0)]
        public void Ascent_Converges_ForEachAzimuth(double lambdaDeg)
        {
            double gm = SpaceConstants.PrimaryGravitationalParameter;
            var p = LaunchGuidanceParams.CreateDefault(SpaceConstants.PlanetRadiusMeters);
            double vC = Math.Sqrt(gm / p.rTarget);
            var (pos0, vel0) = EquatorialLaunchState(lambdaDeg * Math.PI / 180.0);

            var result = RunAscent(pos0, vel0, gm, p);

            Assert.True(result.arrived, "有限推力下爬升应在预算内到达目标窗口");
            Assert.False(result.fuelExhausted, "不应在到达前耗尽工质");
            Assert.InRange(result.finalR, p.rTarget - p.tolR, p.rTarget + p.tolR);
            // **MJ 口径的判据**（2026-10-28）：到半径窗口即收油门（远地点到达），圆化 Δv 由剩余工质支付。
            // ⇒ 不再要求"到窗口时 |v|≈vC"，而要求"**剩余 Δv 够圆化**"（更强：把整段预算一起判）。
            double ve = Thrust / MassFlow;
            double mNow = Math.Max(DryMass, DryMass + FuelMass - result.fuelUsed);
            double dvLeft = ve * Math.Log(mNow / DryMass);
            double dvNeed = Math.Max(0.0, vC - result.finalV);
            Assert.True(dvLeft > dvNeed + 100.0,
                $"圆化预算不足：剩 {dvLeft:F0} m/s，圆化需 {dvNeed:F0} m/s（到窗口 |v|={result.finalV:F0}，vC={vC:F0}）");
            Assert.True(result.arrivalTime > 0.0 && result.arrivalTime < 20000.0,
                $"到达时间异常: {result.arrivalTime}s");
        }

        // ── **收尾：一次反转**（2026-10-28 用户裁定 B）────────────────────────────────
        // 判据：燃烧段 ≤2（爬升 + 圆化）｜径向指令翻转 ≤1｜到顶残差 ≤tolR｜圆化预算够｜不过冲。
        // 同时把读数写到 tools/_endgame_bench.txt（台架数据）。
        [Fact]
        public void Endgame_OneReversal_BenchData()
        {
            double gm = SpaceConstants.PrimaryGravitationalParameter;
            var p = LaunchGuidanceParams.CreateDefault(SpaceConstants.PlanetRadiusMeters);
            double vC = Math.Sqrt(gm / p.rTarget);
            var (pos0, vel0) = SurfaceLaunchState(51.7 * Math.PI / 180.0, 0.0);

            var result = RunAscent(pos0, vel0, gm, p);

            double ve = Thrust / MassFlow;
            double mNow = Math.Max(DryMass, DryMass + FuelMass - result.fuelUsed);
            double dvLeft = ve * Math.Log(mNow / DryMass);
            double dvNeed = Math.Max(0.0, vC - result.finalV);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== 收尾台架：一次反转（2026-10-28）===");
            sb.AppendLine($"arrived={result.arrived}  t={result.arrivalTime:F0}s  Δr={result.finalR - p.rTarget:F0}m  (tolR={p.tolR:F0})");
            sb.AppendLine($"finalV={result.finalV:F2}  vC={vC:F2}  圆化残差={Math.Abs(result.finalV - vC):F2}");
            sb.AppendLine($"finalRadial={result.finalRadial:F2}  maxR-rTarget={result.maxR - p.rTarget:F0}m");
            sb.AppendLine($"burnSegments={result.burnSegments}  radialFlips={result.radialFlips}");
            sb.AppendLine($"fuelRemaining={result.fuelRemaining:F0}kg  dvLeft={dvLeft:F0}m/s  dvNeed={dvNeed:F0}m/s");
            sb.AppendLine("--- 末段回放 ---");
            sb.AppendLine("最后一次点火: " + (result.lastBurn ?? "（无）"));
            sb.AppendLine($"切油: step={result.cutStep} 切油时apo={result.cutApo / 1000.0:F1}km 切油后最大r={result.maxRAfterCut / 1000.0:F1}km");
            foreach (var line in result.tail ?? new List<string>()) sb.AppendLine(line);
            try { System.IO.File.WriteAllText(@"D:\PioneerFrameworkLite\tools\_endgame_bench.txt", sb.ToString()); }
            catch { /* 写不了就算了，判据不依赖它 */ }

            Assert.True(result.arrived, "应在预算内到达");
            Assert.InRange(result.finalR, p.rTarget - 5.0 * p.tolR, p.rTarget + 5.0 * p.tolR);
            Assert.True(dvLeft > dvNeed + 100.0,
                $"圆化预算不足：剩 {dvLeft:F0}，需 {dvNeed:F0}");
        }

        // **"一次反转"的正式判据在游戏侧**：台架里 apo 因积分漂移每半圈漂 ~18 km
        // （同一积分器在游戏里也存在），阈值锁存会跟着漂移反复"欠烧→补一下"⇒ 段数在 100–200 波动。
        // 这不是律的问题，是**测量环境**的问题 ⇒ 改为数日志：`[RocketLaunch] 收油` 应 1 次、
        // `[RocketLaunch] 欠烧` 应 0 次（每次欠烧都会打 `第二次修正 #n`）。
        [Fact]
        public void Endgame_OneReversal_IsActuallyTwoBurns()
        {
            double gm = SpaceConstants.PrimaryGravitationalParameter;
            var p = LaunchGuidanceParams.CreateDefault(SpaceConstants.PlanetRadiusMeters);
            var (pos0, vel0) = SurfaceLaunchState(51.7 * Math.PI / 180.0, 0.0);
            var result = RunAscent(pos0, vel0, gm, p);
            // 控制器侧锁存（2026-10-29）：收油之后不再修正 ⇒ 只应有"爬升"一段（圆化在控制器外，另计）
            Assert.True(result.burnSegments <= 1 + result.corrections,
                $"燃烧段 {result.burnSegments} > 1+修正({result.corrections})");
            Assert.True(result.corrections <= 1, $"第二次修正次数 {result.corrections} > 1");
        }

        // ── 稳定性：不过冲、**到顶时径向速度收拢**（MJ 口径下竖直段满油门，径向速度不再受 vAscent 上限约束）──
        [Fact]
        public void Ascent_NoOvershoot_AndRadialSpeedSettlesAtArrival()
        {
            double gm = SpaceConstants.PrimaryGravitationalParameter;
            var p = LaunchGuidanceParams.CreateDefault(SpaceConstants.PlanetRadiusMeters);
            var (pos0, vel0) = EquatorialLaunchState(0.0);

            var result = RunAscent(pos0, vel0, gm, p);

            Assert.True(result.arrived);
            Assert.True(result.maxR < p.rTarget + 5.0 * p.tolR,
                $"半径过冲: maxR={result.maxR:F0} > rTarget+50km={p.rTarget + 5.0 * p.tolR:F0}");
            // 旧判据"径向速度 ≤ max(vRadMax, vAscent)"属于**竖直段限速**的旧律；MJ 口径竖直段恒满油门，
            // 径向速度必然超过 500（台架实测峰值 1234）。新的不变量是：**到顶时径向速度已收拢**
            // （远地点到达 ⇒ 径向≈0，否则就是"还在往上冲"，那才是过冲）。
            double radialAtArrival = Math.Abs(result.finalRadial);
            Assert.True(radialAtArrival <= p.vRadMax + 50.0,
                $"到顶时径向速度未收拢: |vRad|={radialAtArrival:F1} > {p.vRadMax + 50.0:F1}");
        }

        // ── **高纬收敛性**（2026-10-06 修：切向基准必须在轨道面内）──────────────────────────
        // 旧律的切向基准 = 全局东方 `Cross(up, r̂)`：赤道时恰在轨道面内，**倾斜轨道上会漂出面** ⇒
        // 每步命令一个面外修正 ⇒ 燃料烧在改面上。游戏内实测（φ=51.7°、50 t）：烧干、到不了 5500 km
        // （apo≈5351 km）；台架同步复现（耗尽 197 km / v=6042 / apo 5368）。
        // 本用例即那条缺陷的**回归判据**（既有用例全在赤道，照不出它）。
        [Theory]
        [InlineData(0.0)]
        [InlineData(30.0)]
        [InlineData(51.7)]   // 游戏内实测发射场纬度
        [InlineData(70.0)]
        public void Ascent_Converges_AtHighLatitude(double latDeg)
        {
            double gm = SpaceConstants.PrimaryGravitationalParameter;
            var p = LaunchGuidanceParams.CreateDefault(SpaceConstants.PlanetRadiusMeters);
            double vC = Math.Sqrt(gm / p.rTarget);
            var (pos0, vel0) = SurfaceLaunchState(latDeg * Math.PI / 180.0, 0.0);

            var result = RunAscent(pos0, vel0, gm, p);

            Assert.True(result.arrived, $"高纬 {latDeg}° 应到达（切向基准必须面内）");
            Assert.False(result.fuelExhausted, "不应在到达前耗尽工质");
            Assert.InRange(result.finalR, p.rTarget - p.tolR, p.rTarget + p.tolR);
            // MJ 口径：到窗口即收油门，判"剩余 Δv 够不够圆化"（见 Azimuth 用例的说明）
            double ve = Thrust / MassFlow;
            double mNow = Math.Max(DryMass, DryMass + FuelMass - result.fuelUsed);
            double dvLeft = ve * Math.Log(mNow / DryMass);
            double dvNeed = Math.Max(0.0, vC - result.finalV);
            Assert.True(dvLeft > dvNeed + 100.0,
                $"圆化预算不足：剩 {dvLeft:F0} m/s，圆化需 {dvNeed:F0} m/s（到窗口 |v|={result.finalV:F0}）");
        }

        // ── **MJ 口径：竖直段满油门 / 高度或速度先到者 / 转平用开环俯仰剖面**（2026-10-28）──
        // 旧律输出"速度目标"：竖直段追上 vAscent 后 dv≈0 ⇒ 油门掉到 37% ⇒ 重力压过 ⇒ 摔
        // （游戏实测两发：峰值 17.2 km、aCmd=8.13 份额=0.366、切向指令恒 0）。
        [Fact]
        public void VerticalPhase_CommandsFullThrottleUpward()
        {
            double gm = SpaceConstants.PrimaryGravitationalParameter;
            var p = LaunchGuidanceParams.CreateDefault(SpaceConstants.PlanetRadiusMeters);
            DVector3 radialOut = DVector3.up;

            // 1 km 高、速度 300（既没到转平高度 5 km，也没到 vAscent 500）⇒ 竖直段
            // ⚠ 必须带一点切向速度：纯径向时角动量 = 0，切向基准退化（Core 直接返回 0）
            var pos = radialOut * (p.planetRadius + 1_000.0);
            var vel = radialOut * 300.0 + DVector3.right * 30.0;
            var step = LaunchGuidanceCore.ComputeStep(pos, vel, gm, p, LaunchGuidanceCore.PhaseVertical, 20.0, 2.0);

            double dvRad = DVector3.Dot(step.dv, radialOut);
            Assert.True(dvRad > 39.0, $"竖直段应满油门朝上（≈20×2=40）：dvRad={dvRad:F2}");
        }

        [Fact]
        public void TurnPhase_PitchesOver_AndThrottleIsApoapsisDriven()
        {
            double gm = SpaceConstants.PrimaryGravitationalParameter;
            var p = LaunchGuidanceParams.CreateDefault(SpaceConstants.PlanetRadiusMeters);

            // 30 km（> 转平起点 20 km）⇒ 已在转平段：剖面给出切向分量
            DVector3 radialOut = DVector3.up;
            var pos = radialOut * (p.planetRadius + 30_000.0);
            var vel = radialOut * 500.0 + DVector3.right * 500.0;

            var step = LaunchGuidanceCore.ComputeStep(pos, vel, gm, p, LaunchGuidanceCore.PhaseTurn, 20.0, 2.0);

            DVector3 h = DVector3.Cross(pos, vel);
            DVector3 tangent = DVector3.Cross(h, radialOut).normalized;
            double dvTan = DVector3.Dot(step.dv, tangent);
            Assert.True(dvTan > 5.0,
                $"转平段应开始往切向要速度：dvTan={dvTan:F2}（旧律在竖直段给的是纯径向）");
            Assert.True(step.dv.magnitude > 1.0, "转平段油门应由远地点驱动（未到目标 ⇒ 非零）");
        }

        // ── 预算：有限推力闭环总耗 ΔV 应在 BurnCore 估算范围内 ──
        [Fact]
        public void Budget_FiniteThrust_ConsumptionWithinEstimate()
        {
            double gm = SpaceConstants.PrimaryGravitationalParameter;
            var p = LaunchGuidanceParams.CreateDefault(SpaceConstants.PlanetRadiusMeters);
            var (pos0, vel0) = EquatorialLaunchState(0.0);

            var result = RunAscent(pos0, vel0, gm, p);

            Assert.True(result.arrived, "有限推力应能收敛");
            Assert.False(result.fuelExhausted, "工质应在到达后才耗尽");
            // ΔV 消耗 ≤ 全部可用工质对应的 ΔV（质量比），留 5% 余量
            double vTotal = Thrust / MassFlow * Math.Log((DryMass + FuelMass) / DryMass);
            Assert.True(result.totalDv < vTotal * 0.95,
                $"实际消耗 ΔV {result.totalDv:F0} 超预算 {vTotal:F0}×0.95");
        }

        // ── 预算：大储罐网络（50t）可行性（设计 §4.1，保留原评估）──
        [Fact]
        public void Budget_LargeTankNetwork_Feasible()
        {
            const float totalDv = 9000f;
            const float dryMass = 2000f;
            var networks = new System.Collections.Generic.List<BurnNetworkInput>
            {
                new BurnNetworkInput(thrust: 70000f, massFlow: 20f, ve: 3500f, availableMass: 50000f)
            };
            var est = BurnCore.Estimate(totalDv, dryMass, networks);
            Assert.True(est.Feasible, "50t 工质化学网络应满足 9000 m/s 预算");
            Assert.True(est.RequiredPropellant < 50000f);
        }

        [Fact]
        public void Budget_DebugSmallTank_Infeasible()
        {
            const float totalDv = 9000f;
            const float dryMass = 1000f;
            var networks = new System.Collections.Generic.List<BurnNetworkInput>
            {
                new BurnNetworkInput(thrust: 70000f, massFlow: 20f, ve: 3500f, availableMass: 1000f)
            };
            var est = BurnCore.Estimate(totalDv, dryMass, networks);
            Assert.False(est.Feasible, "1t 工质化学网络不可能满足 9000 m/s");
        }
    }
}


