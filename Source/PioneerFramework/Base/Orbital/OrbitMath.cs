using System;
using System.Collections.Generic;
using MechJebLib.Functions;        // Astro：要素/传播/周期的移植权威（V1/V2 收敛目标）
using MechJebLib.Primitives;       // `V3` 是别名（批5 U2/U3 起 = PioneerFramework.DVector3）
using MechJebLib.Utils;            // Statics

namespace PioneerFramework
{
    /// <summary>
    /// 开普勒轨道力学工具。
    /// 所有位置/速度使用 DVector3（双精度），标量使用 double。
    /// </summary>
    public static class OrbitMath
    {
        /// <summary>引力参数 GM (m³/s²)，读取 SpaceConstants 中由 CelestialBodyManager 同步的主天体参数。</summary>
        public static double GM => SpaceConstants.PrimaryGravitationalParameter;

        private static double ResolveGM(double mu) => double.IsNaN(mu) ? GM : mu;

        #region 轨道要素计算

        /// <summary>
        /// 计算轨道能量 (m²/s²)，负值 = 束缚轨道。
        /// </summary>
        public static double SpecificEnergy(DVector3 posMeters, DVector3 velMeters, double mu)
        {
            double gm = ResolveGM(mu);
            double v2 = velMeters.sqrMagnitude;
            double r = posMeters.magnitude;
            if (r < 0.001) return double.MaxValue;
            return 0.5 * v2 - gm / r;
        }

        /// <summary>
        /// 计算半长轴 (m)。正数 = 椭圆，负数 = 双曲线。
        /// </summary>
        public static double SemiMajorAxis(DVector3 posMeters, DVector3 velMeters, double mu)
        {
            // 【V2 已收敛 2026-09-18】算式改为移植权威 `Astro.SmaFromStateVectors`（与本式代数逐字相同）。
            // **抛物线守卫保留**：`Astro` 那边分母趋零会给 ±∞，而旧契约是 `double.MaxValue`。
            double gm = ResolveGM(mu);
            double eps = SpecificEnergy(posMeters, velMeters, gm);
            if (Math.Abs(eps) < 1e-10) return double.MaxValue; // 抛物线
            return Astro.SmaFromStateVectors(gm, posMeters, velMeters);
        }

        /// <summary>
        /// 计算离心率向量和大小。
        /// </summary>
        public static void GetEccentricity(DVector3 posMeters, DVector3 velMeters, out DVector3 eVec, out double e, double mu)
        {
            // 【V2 已收敛 2026-09-18】改为移植权威 `Astro.EccVecFromStateVectors`
            // （`cross(v/μ, r×v) − r̂`，与本式代数逐字相同）。**`r < 0.001` 守卫保留**。
            double gm = ResolveGM(mu);
            double r = posMeters.magnitude;
            if (r < 0.001) { eVec = DVector3.zero; e = 0.0; return; }

            V3 vec = Astro.EccVecFromStateVectors(gm, posMeters, velMeters);
            eVec = vec;
            e = eVec.magnitude;
        }

        // 【V2 已删 2026-09-18】本段原先有 `GetOrbitNormal` 与 `GetArgumentOfPeriapsis`：
        //   · `GetOrbitNormal` 零调用点（等价式 `DVector3.Cross(r, v).normalized` 就够，
        //     而"轨道法向"的权威是 `OrbitView.OrbitNormal()`）；
        //   · `GetArgumentOfPeriapsis` 零调用点，且它**只给象限不满的解**（对 `cos` 反余弦、丢掉符号
        //     ⇒ 第二/三象限的 ω 被折叠到第一象限）—— 正是《大修计划》§11.2 列为 PARTIAL 的那条。
        //     移植权威是 `Astro.KeplerianFromStateVectors(...).argp`（`OrbitView.argumentOfPeriapsis` 走它）。
        #endregion

        #region 轨道点生成

        // 【V1 已删 2026-09-18】`GetTrueAnomaly(DVector3, DVector3, double)` 零调用点 ⇒ 删。**
        // ⚠ 它正是那个**带已知缺陷**的副本：`cos ν = (e⃗·r⃗)/(e·r)` 在圆轨上分子分母同为舍入噪声
        //   ⇒ 返回任意角，并曾使圆轨传播被整体旋转一个常数角（记录见
        //   `MechJebLibPortTests.CircularOrbitPropagation_IsExact_AndHyperbolicIsNoLongerANoOp`）。
        //   移植权威是 `Astro.TrueAnomalyFromStateVectors` / `OrbitView.trueAnomaly`。

        #endregion

        #region 远/近心点 (Pe/Ap)

        /// <summary>
        /// 计算近心点距离 (m)。Pe = a·(1-e)
        /// </summary>
        public static double GetPeriapsisDistance(double semiMajorAxis, double eccentricity)
        {
            if (semiMajorAxis <= 0.0) return -1.0; // 旧契约：非椭圆给 -1（移植的 Astro 没有这个守卫）
            return Astro.PeriapsisFromKeplerian(semiMajorAxis, eccentricity);
        }

        /// <summary>
        /// 计算远心点距离 (m)。Ap = a·(1+e)
        /// </summary>
        public static double GetApoapsisDistance(double semiMajorAxis, double eccentricity)
        {
            if (semiMajorAxis <= 0.0) return -1.0; // 同上
            return Astro.ApoapsisFromKeplerian(semiMajorAxis, eccentricity);
        }

        /// <summary>
        /// 检查轨道是否安全（不撞击行星表面或大气层）。
        /// 椭圆轨判断近地点 ≥ 安全边界；双曲轨返回 true（逃逸轨道不做撞击检查）。
        /// </summary>
        /// <param name="posMeters">位置 (m)，需为天体中心坐标系</param>
        /// <param name="velMeters">速度 (m/s)，需为天体中心坐标系</param>
        /// <param name="safeRadiusMeters">安全边界半径</param>
        /// <param name="mu">引力参数 GM (m³/s²)，默认使用 OrbitMath.GM</param>
        /// <returns>安全返回 true，会撞击返回 false</returns>
        public static bool IsOrbitSafe(DVector3 posMeters, DVector3 velMeters, double safeRadiusMeters, double mu)
        {
            double gm = double.IsNaN(mu) ? GM : mu;
            // 直接计算 SMA 和 e，避免修改现有方法
            double r = posMeters.magnitude;
            double v2 = velMeters.sqrMagnitude;
            double eps = 0.5 * v2 - gm / r;
            if (Math.Abs(eps) < 1e-10) return true; // 抛物线安全
            double sma = -gm / (2.0 * eps);
            if (sma <= 0.0) return true; // 双曲安全

            DVector3 h = DVector3.Cross(posMeters, velMeters);
            DVector3 eVec = DVector3.Cross(velMeters, h) / gm - posMeters / r;
            double e = eVec.magnitude;
            double pe = GetPeriapsisDistance(sma, e);
            return pe >= safeRadiusMeters;
        }

        /// <summary>
        /// 计算从当前位置到近心点所需时间 (秒)。
        ///
        /// ⚠ **2026-09-18（V1，§14.3『近点角』行）：已收敛到 <see cref="OrbitView.NextPeriapsisTime" />。**
        /// 原先这里自算 `GetTrueAnomaly` + `TrueToEccentricAnomaly` + 手工平均近点角差，
        /// 只在椭圆上成立（`GetOrbitFrame` 拒绝 `a<=0 || e>=1` ⇒ 双曲返回 0.0）。
        /// **本步是"收敛"不是"改行为"，所以双曲仍然返回 0.0**（`OrbitView` 会给出真实值，
        /// 但那是另一个提升，不夹带在这一步里——见下方 `GetTimeToApoapsis` 的同款处理）。
        /// </summary>
        public static double GetTimeToPeriapsis(DVector3 posMeters, DVector3 velMeters)
        {
            OrbitView ov = OrbitView.FromStateVectors(posMeters, velMeters, GM, 0.0);
            if (!(ov.eccentricity < 1.0)) return 0.0; // 旧契约：非椭圆给 0（双曲没有远近心点的椭圆口径）
            return ov.NextPeriapsisTime(0.0);
        }

        /// <summary>
        /// 计算从当前位置到远心点所需时间 (秒)。**同 <see cref="GetTimeToPeriapsis" />：已收敛，双曲仍返回 0.0。**
        /// ⚠ `OrbitView.NextApoapsisTime` 对双曲**抛 `ArgumentException`** ⇒ 这里的守卫是**功能性的**，
        /// 不是装饰（去掉它会把 `SpaceGameComp` 的候选时刻计算变成抛异常）。
        /// </summary>
        public static double GetTimeToApoapsis(DVector3 posMeters, DVector3 velMeters)
        {
            OrbitView ov = OrbitView.FromStateVectors(posMeters, velMeters, GM, 0.0);
            if (!(ov.eccentricity < 1.0)) return 0.0;
            return ov.NextApoapsisTime(0.0);
        }

        #endregion

        #region 升/降交点 (AN/DN) 与轨道倾角

        // 【V2 已删 2026-09-18】本段原先有 `GetInclination` / `GetNodePositions` / `GetLongitudeOfAscendingNode`
        // —— 三者都是「参考法向由调用方传」的口径（默认 `DVector3.up` = 游戏世界 +Y），
        // 与适配层（+z 极轴）是**两个口径**。它们的调用点已重指到 `OrbitView`：
        //   · `GetInclination`     → `OrbitView.FromGameStateVectors(...).inclination`（ManeuverPlanner 的手册规划器）
        //   · `GetNodePositions`   → `OrbitView.Ascending/DescendingNodePositionEquatorial`（调试标记；`OrbitView` 原先只有交点时刻、没有位置，那处缺口已补）
        //   · `GetLongitudeOfAscendingNode` → 零调用点，直接删（`OrbitView.LAN` 是权威）
        #endregion

        #region 轨道点生成

        /// <summary>
        /// 生成轨道路径点 (世界空间，单位：米)。
        /// </summary>
        public static List<DVector3> GetOrbitPoints(DVector3 posMeters, DVector3 velMeters, int numPoints, double mu)
        {
            double gm = double.IsNaN(mu) ? GM : mu;
            var points = new List<DVector3>(numPoints);

            double r = posMeters.magnitude;
            if (r < 0.001) return points;

            // 用传入的 GM 计算离心率
            DVector3 hVec = DVector3.Cross(posMeters, velMeters);
            DVector3 eVec = DVector3.Cross(velMeters, hVec) / gm - posMeters / r;
            double e = eVec.magnitude;

            // 用传入的 GM 计算半长轴
            double v2 = velMeters.sqrMagnitude;
            double eps = 0.5 * v2 - gm / r;
            double a;
            if (Math.Abs(eps) < 1e-10) return points;
            a = -gm / (2.0 * eps);

            if (a <= 0.0 || double.IsInfinity(a) || e >= 1.0)
                return points;

            DVector3 h = hVec.normalized;
            DVector3 eDir = eVec.magnitude > 0.001 ? eVec.normalized : posMeters.normalized;
            DVector3 yDir = DVector3.Cross(h, eDir).normalized;

            double p = a * (1.0 - e * e);

            for (int i = 0; i < numPoints; i++)
            {
                double nu = 2.0 * Math.PI * i / numPoints;
                double cosNu = Math.Cos(nu);
                double rPt = p / (1.0 + e * cosNu);
                if (rPt < 0.0) rPt = -rPt;

                DVector3 point = eDir * (rPt * cosNu) + yDir * (rPt * Math.Sin(nu));
                points.Add(point);
            }

            return points;
        }

        // 【V2 已删 2026-09-18】`GetPositionAtFraction` 与 `GetVelocityAtFraction` 都零调用点 ⇒ 删。
        // 它们是"按真近点角比例取轨道点"的椭圆专用版（且同样受 `GetOrbitFrame` 的椭圆限制）；
        // 对应能力在 `OrbitView`（`RightHandedStateVectorsAtUT` + `TrueAnomalyAtRadius`/`trueAnomaly`）。
        /// <summary>
        /// 提取轨道平面坐标系和要素。
        /// </summary>
        public static bool GetOrbitFrame(DVector3 posMeters, DVector3 velMeters,
            out double a, out double e, out double p,
            out DVector3 eDir, out DVector3 yDir, double mu)
        {
            a = SemiMajorAxis(posMeters, velMeters, mu);
            DVector3 eVec;
            GetEccentricity(posMeters, velMeters, out eVec, out e, mu);
            eDir = eVec;

            if (a <= 0.0 || double.IsInfinity(a) || e >= 1.0)
            {
                yDir = DVector3.zero;
                p = 0.0;
                return false;
            }

            DVector3 h = DVector3.Cross(posMeters, velMeters).normalized;
            eDir = e > 0.001 ? eVec.normalized : posMeters.normalized;
            yDir = DVector3.Cross(h, eDir).normalized;
            p = a * (1.0 - e * e);
            return true;
        }

        #endregion

        #region 时间传播 (Kepler)

        // 【V1 已删 2026-09-18】`MeanMotion` 零调用点 ⇒ 删；`Astro.MeanMotion(mu, sma)` 是同一算式的移植权威。
        // 它原先挂在『周期』那一族（§14.3）名下 —— 删掉的就是「同一条算式的又一份」。

        // 【V1 已删 2026-09-18】`TrueToEccentricAnomaly` 零调用点 ⇒ 删
        // （`GetTimeToPeriapsis`/`GetTimeToApoapsis` 已收敛到 `OrbitView.Next*`，不再需要它）。
        // 『近点角』行（§14.3）的移植权威是 `Angles.EFromNu` / `NuFromE`。
        // 同批 `EccentricToTrueAnomaly` 与 `SolveKepler` 也早已零调用点、已删。

        /// <summary>
        /// 获取指定时间 Δt 后的轨道位置。
        ///
        /// ⚠ **2026-09-18（V1，§14.3『传播』行）：本方法已改成薄转发。**
        /// 原先这里是一份**椭圆专用**的解算（`GetOrbitFrame` + `GetTrueAnomaly` + `SolveKepler` 串起来），
        /// 它有两个已实测的毛病：
        ///   · `a<=0 || e>=1` 时**静默原样返回输入** ⇒ 双曲/抛物上"传播"是 no-op（登记为 **Q11**）；
        ///   · `e < 0.001` 分支用的真近点角是**舍入噪声**（`e⃗·r̂/e`）⇒ **输出被整体旋转一个任意常数角**
        ///     （见 `MechJebLibPortTests.KnownDefect_CircularOrbitPropagationIsRotatedByGarbage`）。
        /// 现在**唯一实现是移植来的 `Shepperd`**（普适变量法，椭圆/抛物/双曲全域有效），经 `OrbitView` 转发。
        /// **转发的名字与调用点都保留**（12 个调用点里不少在交会路径上），所以这是一次"就地取代"。
        ///
        /// **参考系无关**：二体传播对参考系的**旋转**不变（输入整体旋转 ⇒ 输出同样旋转），
        /// 所以游戏系 `DVector3` 直接喂进来是对的，**不需要换系**
        /// （换系只服务 `inclination`/`LAN`/`argP`/`ν` 那些**方向**量，传播不读它们）。
        /// `epoch` 取 0 ⇒ 入参 `deltaSeconds` 就是 `Shepperd` 的 `tau`，与原签名语义一致。
        /// </summary>
        public static DVector3 GetPositionAtTimeDelta(DVector3 posMeters, DVector3 velMeters, double deltaSeconds, double mu)
            => OrbitView.Propagate(posMeters, velMeters, ResolveGM(mu), deltaSeconds).pos;

        /// <summary>
        /// 获取指定时间 Δt 后的轨道速度。**同上：薄转发到 `Shepperd`**（见 <see cref="GetPositionAtTimeDelta" /> 的说明）。
        /// </summary>
        public static DVector3 GetVelocityAtTimeDelta(DVector3 posMeters, DVector3 velMeters, double deltaSeconds, double mu)
            => OrbitView.Propagate(posMeters, velMeters, ResolveGM(mu), deltaSeconds).vel;

        #endregion

        #region 机动计算

        /// <summary>
        /// 分解 dV 到世界空间向量。
        /// </summary>
        public static DVector3 GetDeltaVSystem(DVector3 posMeters, DVector3 velMeters,
            double radialDV, double tangentialDV, double normalDV)
        {
            DVector3 radialDir = posMeters.normalized;
            DVector3 h = DVector3.Cross(posMeters, velMeters).normalized;
            DVector3 tangentialDir = DVector3.Cross(h, radialDir).normalized;

            return radialDir * radialDV + tangentialDir * tangentialDV + h * normalDV;
        }

        /// <summary>
        /// 将世界空间 dV 分解为径向、切向、法向分量。
        /// </summary>
        public static void DeltaVSystemToComponents(DVector3 posMeters, DVector3 velMeters,
            DVector3 dV, out double radialDV, out double tangentialDV, out double normalDV)
        {
            DVector3 radialDir = posMeters.normalized;
            DVector3 h = DVector3.Cross(posMeters, velMeters).normalized;
            DVector3 tangentialDir = DVector3.Cross(h, radialDir).normalized;

            radialDV = DVector3.Dot(dV, radialDir);
            tangentialDV = DVector3.Dot(dV, tangentialDir);
            normalDV = DVector3.Dot(dV, h);
        }

        /// <summary>
        /// 计算机动后的轨道路径点。
        /// </summary>
        public static List<DVector3> GetPostManeuverOrbitPoints(DVector3 nodePosMeters, DVector3 velAtNodeMeters,
            DVector3 dV, int numPoints, double mu)
        {
            DVector3 newVel = velAtNodeMeters + dV;
            return GetOrbitPoints(nodePosMeters, newVel, numPoints, mu);
        }

        #endregion
    }
}