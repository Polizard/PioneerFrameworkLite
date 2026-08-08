using System;
using System.Collections.Generic;

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
        public static double SpecificEnergy(DVector3 posMeters, DVector3 velMeters, double mu = double.NaN)
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
        public static double SemiMajorAxis(DVector3 posMeters, DVector3 velMeters, double mu = double.NaN)
        {
            double gm = ResolveGM(mu);
            double eps = SpecificEnergy(posMeters, velMeters, gm);
            if (Math.Abs(eps) < 1e-10) return double.MaxValue; // 抛物线
            return -gm / (2.0 * eps);
        }

        /// <summary>
        /// 计算离心率向量和大小。
        /// </summary>
        public static void GetEccentricity(DVector3 posMeters, DVector3 velMeters, out DVector3 eVec, out double e, double mu = double.NaN)
        {
            double gm = ResolveGM(mu);
            double r = posMeters.magnitude;
            if (r < 0.001) { eVec = DVector3.zero; e = 0.0; return; }

            // e = (v × h) / μ - r̂, 其中 h = r × v
            DVector3 h = DVector3.Cross(posMeters, velMeters);
            eVec = DVector3.Cross(velMeters, h) / gm - posMeters / r;
            e = eVec.magnitude;
        }

        /// <summary>
        /// 获取轨道平面法线 (角动量方向)。
        /// </summary>
        public static DVector3 GetOrbitNormal(DVector3 posMeters, DVector3 velMeters)
        {
            return DVector3.Cross(posMeters, velMeters).normalized;
        }

        /// <summary>
        /// 计算近心点幅角 (Argument of Periapsis)，即偏心矢量方向与 ascending node 的夹角。
        /// 简化版：直接返回偏心矢量在轨道平面内的方向角。
        /// </summary>
        public static double GetArgumentOfPeriapsis(DVector3 posMeters, DVector3 velMeters, DVector3 eVec, double e)
        {
            if (e < 0.001) return 0.0;

            DVector3 h = DVector3.Cross(posMeters, velMeters);
            DVector3 node = DVector3.Cross(new DVector3(0, 1, 0), h);
            double nodeMag = node.magnitude;
            if (nodeMag < 0.001) return 0.0;

            double cosOmega = DVector3.Dot(node, eVec) / (nodeMag * e);
            return Math.Acos(Math.Max(-1.0, Math.Min(1.0, cosOmega)));
        }

        #endregion

        #region 轨道点生成

        /// <summary>
        /// 计算给定位置/速度下的真近点角。
        /// </summary>
        public static double GetTrueAnomaly(DVector3 posMeters, DVector3 velMeters, double mu = double.NaN)
        {
            DVector3 eVec;
            double e;
            GetEccentricity(posMeters, velMeters, out eVec, out e, mu);

            double r = posMeters.magnitude;
            double cosNu = DVector3.Dot(eVec, posMeters) / (e * r);
            cosNu = Math.Max(-1.0, Math.Min(1.0, cosNu));

            // 如果径向速度为正 → 远离中心 → 0 < ν < π
            double radVel = DVector3.Dot(posMeters, velMeters);
            double nu = Math.Acos(cosNu);
            if (radVel < 0.0)
                nu = 2.0 * Math.PI - nu;

            return nu;
        }

        #endregion

        #region 远/近心点 (Pe/Ap)

        /// <summary>
        /// 计算近心点距离 (m)。Pe = a·(1-e)
        /// </summary>
        public static double GetPeriapsisDistance(double semiMajorAxis, double eccentricity)
        {
            if (semiMajorAxis <= 0.0) return -1.0;
            return semiMajorAxis * (1.0 - eccentricity);
        }

        /// <summary>
        /// 计算远心点距离 (m)。Ap = a·(1+e)
        /// </summary>
        public static double GetApoapsisDistance(double semiMajorAxis, double eccentricity)
        {
            if (semiMajorAxis <= 0.0) return -1.0;
            return semiMajorAxis * (1.0 + eccentricity);
        }

        /// <summary>
        /// 获取近心点位置矢量 (世界空间, m)。
        /// Pe 方向 = 偏心矢量方向, 距离 = a·(1-e)。
        /// </summary>
        public static DVector3 GetPeriapsisPosition(DVector3 posMeters, DVector3 velMeters)
        {
            double a, e, p;
            DVector3 eDir, yDir;
            if (!GetOrbitFrame(posMeters, velMeters, out a, out e, out p, out eDir, out yDir))
                return posMeters;
            double peDist = GetPeriapsisDistance(a, e);
            if (e < 0.001) return eDir * a; // 圆轨：任意方向
            return eDir * peDist;
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
        public static bool IsOrbitSafe(DVector3 posMeters, DVector3 velMeters, double safeRadiusMeters, double mu = double.NaN)
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
        /// 获取远心点位置矢量 (世界空间, m)。
        /// Ap 方向 = -偏心矢量方向, 距离 = a·(1+e)。
        /// </summary>
        public static DVector3 GetApoapsisPosition(DVector3 posMeters, DVector3 velMeters)
        {
            double a, e, p;
            DVector3 eDir, yDir;
            if (!GetOrbitFrame(posMeters, velMeters, out a, out e, out p, out eDir, out yDir))
                return posMeters;
            double apDist = GetApoapsisDistance(a, e);
            if (e < 0.001) return -eDir * a; // 圆轨：与 Pe 相反
            return -eDir * apDist;
        }

        /// <summary>
        /// 计算从当前位置到近心点所需时间 (秒)。
        /// </summary>
        public static double GetTimeToPeriapsis(DVector3 posMeters, DVector3 velMeters)
        {
            double a, e, p;
            DVector3 eDir, yDir;
            if (!GetOrbitFrame(posMeters, velMeters, out a, out e, out p, out eDir, out yDir))
                return 0.0;

            double currentNu = GetTrueAnomaly(posMeters, velMeters);
            double dNu = -currentNu;
            if (dNu < 0.0) dNu += 2.0 * Math.PI;

            double n = MeanMotion(a);
            if (n <= 0.0) return 0.0;

            if (e < 0.001)
            {
                return dNu / n;
            }

            double currentE = TrueToEccentricAnomaly(currentNu, e);
            double peE = TrueToEccentricAnomaly(0.0, e);
            double dM = (peE - e * Math.Sin(peE)) - (currentE - e * Math.Sin(currentE));
            if (dM < 0.0) dM += 2.0 * Math.PI;
            return dM / n;
        }

        /// <summary>
        /// 计算从当前位置到远心点所需时间 (秒)。
        /// </summary>
        public static double GetTimeToApoapsis(DVector3 posMeters, DVector3 velMeters)
        {
            double a, e, p;
            DVector3 eDir, yDir;
            if (!GetOrbitFrame(posMeters, velMeters, out a, out e, out p, out eDir, out yDir))
                return 0.0;

            double currentNu = GetTrueAnomaly(posMeters, velMeters);
            double dNu = Math.PI - currentNu;
            if (dNu < 0.0) dNu += 2.0 * Math.PI;

            double n = MeanMotion(a);
            if (n <= 0.0) return 0.0;

            if (e < 0.001)
            {
                return dNu / n;
            }

            double currentE = TrueToEccentricAnomaly(currentNu, e);
            double apE = TrueToEccentricAnomaly(Math.PI, e);
            double dM = (apE - e * Math.Sin(apE)) - (currentE - e * Math.Sin(currentE));
            if (dM < 0.0) dM += 2.0 * Math.PI;
            return dM / n;
        }

        #endregion

        #region 升/降交点 (AN/DN) 与轨道倾角

        /// <summary>
        /// 计算轨道倾角 (弧度)。
        /// </summary>
        public static double GetInclination(DVector3 posMeters, DVector3 velMeters, DVector3 refNormal = default)
        {
            if (refNormal == default) refNormal = DVector3.up;
            DVector3 h = DVector3.Cross(posMeters, velMeters);
            if (h.sqrMagnitude < 0.0001) return 0.0;
            return Math.Acos(Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(h.normalized, refNormal))));
        }

        /// <summary>
        /// 计算升/降交点位置。
        /// </summary>
        public static (DVector3 an, DVector3 dn) GetNodePositions(DVector3 posMeters, DVector3 velMeters, DVector3 refNormal = default)
        {
            if (refNormal == default) refNormal = DVector3.up;

            double a, e, p;
            DVector3 eDir, yDir;
            if (!GetOrbitFrame(posMeters, velMeters, out a, out e, out p, out eDir, out yDir))
                return (DVector3.zero, DVector3.zero);

            double dotE = DVector3.Dot(eDir, refNormal);
            double dotY = DVector3.Dot(yDir, refNormal);

            if (Math.Abs(dotE) < 1e-10 && Math.Abs(dotY) < 1e-10)
                return (DVector3.zero, DVector3.zero);

            double nu0 = Math.Atan2(-dotE, dotY);
            double nu1 = nu0 + Math.PI;

            DVector3 GetPosAtNu(double nu)
            {
                double cosNu = Math.Cos(nu);
                double r = p / (1.0 + e * cosNu);
                if (r < 0.0) r = -r;
                return eDir * (r * cosNu) + yDir * (r * Math.Sin(nu));
            }

            DVector3 pos0 = GetPosAtNu(nu0);
            DVector3 pos1 = GetPosAtNu(nu1);

            DVector3 vel0 = GetVelocityAtFraction(posMeters, velMeters, nu0 / (2.0 * Math.PI));
            // 用 DVector3.Dot 判断升/降
            if (DVector3.Dot(vel0, refNormal) > 0.0)
                return (pos0, pos1);
            else
                return (pos1, pos0);
        }

        /// <summary>
        /// 获取升交点经度 (弧度)。
        /// </summary>
        public static double GetLongitudeOfAscendingNode(DVector3 posMeters, DVector3 velMeters, DVector3 refNormal = default)
        {
            if (refNormal == default) refNormal = DVector3.up;

            var (anPos, _) = GetNodePositions(posMeters, velMeters, refNormal);
            if (anPos.sqrMagnitude < 0.001) return 0.0;

            DVector3 proj = anPos - refNormal * DVector3.Dot(anPos, refNormal);
            if (proj.sqrMagnitude < 0.001) return 0.0;

            // 与参考方向夹角（默认 Z 轴正方向）
            DVector3 refDir = new DVector3(0, 0, 1);
            double lan = Math.Acos(Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(proj.normalized, refDir))));
            DVector3 crossDir = DVector3.Cross(refDir, proj.normalized);
            if (DVector3.Dot(crossDir, refNormal) > 0.0)
                lan = 2.0 * Math.PI - lan;
            return lan;
        }

        #endregion

        #region 轨道点生成

        /// <summary>
        /// 生成轨道路径点 (世界空间，单位：米)。
        /// </summary>
        public static List<DVector3> GetOrbitPoints(DVector3 posMeters, DVector3 velMeters, int numPoints = 60, double mu = double.NaN)
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

        /// <summary>
        /// 获取轨道上指定比例位置处的点。
        /// </summary>
        public static DVector3 GetPositionAtFraction(DVector3 posMeters, DVector3 velMeters, double fraction)
        {
            double a, e, p;
            DVector3 eDir, yDir;
            if (!GetOrbitFrame(posMeters, velMeters, out a, out e, out p, out eDir, out yDir))
                return posMeters;

            double nu = 2.0 * Math.PI * fraction;
            double cosNu = Math.Cos(nu);
            double r = p / (1.0 + e * cosNu);
            if (r < 0.0) r = -r;

            return eDir * (r * cosNu) + yDir * (r * Math.Sin(nu));
        }

        /// <summary>
        /// 获取轨道上指定比例位置处的速度向量。
        /// </summary>
        public static DVector3 GetVelocityAtFraction(DVector3 posMeters, DVector3 velMeters, double fraction)
        {
            double a, e, p;
            DVector3 eDir, yDir;
            if (!GetOrbitFrame(posMeters, velMeters, out a, out e, out p, out eDir, out yDir))
                return velMeters;

            double nu = 2.0 * Math.PI * fraction;
            double cosNu = Math.Cos(nu);
            double sinNu = Math.Sin(nu);

            double sqrtMuOverP = Math.Sqrt(GM / p);
            double vRadial = sqrtMuOverP * e * sinNu;
            double vTangential = sqrtMuOverP * (1.0 + e * cosNu);

            return eDir * (vRadial * cosNu - vTangential * sinNu)
                 + yDir * (vRadial * sinNu + vTangential * cosNu);
        }

        /// <summary>
        /// 提取轨道平面坐标系和要素。
        /// </summary>
        public static bool GetOrbitFrame(DVector3 posMeters, DVector3 velMeters,
            out double a, out double e, out double p,
            out DVector3 eDir, out DVector3 yDir, double mu = double.NaN)
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

        /// <summary>
        /// 平均角速度: n = √(μ/a³) (rad/s)
        /// </summary>
        public static double MeanMotion(double semiMajorAxis, double mu = double.NaN)
        {
            if (semiMajorAxis <= 0.0) return 0.0;
            double gm = double.IsNaN(mu) ? GM : mu;
            return Math.Sqrt(gm / (semiMajorAxis * semiMajorAxis * semiMajorAxis));
        }

        /// <summary>
        /// 从真近点角 ν 计算偏近点角 E。
        /// </summary>
        public static double TrueToEccentricAnomaly(double trueAnomaly, double e)
        {
            double tanHalfNu = Math.Tan(trueAnomaly / 2.0);
            double tanHalfE = Math.Sqrt((1.0 - e) / (1.0 + e)) * tanHalfNu;
            return 2.0 * Math.Atan(tanHalfE);
        }

        /// <summary>
        /// 从偏近点角 E 计算真近点角 ν。
        /// </summary>
        public static double EccentricToTrueAnomaly(double eccentricAnomaly, double e)
        {
            double tanHalfE = Math.Tan(eccentricAnomaly / 2.0);
            double tanHalfNu = Math.Sqrt((1.0 + e) / (1.0 - e)) * tanHalfE;
            return 2.0 * Math.Atan(tanHalfNu);
        }

        /// <summary>
        /// 解 Kepler 方程 E - e·sin(E) = M (牛顿迭代法)。
        /// </summary>
        public static double SolveKepler(double meanAnomaly, double e)
        {
            double E = meanAnomaly;
            for (int i = 0; i < 20; i++)
            {
                double dE = (E - e * Math.Sin(E) - meanAnomaly) / (1.0 - e * Math.Cos(E));
                E -= dE;
                if (Math.Abs(dE) < 1e-12) break;
            }
            return E;
        }

        /// <summary>
        /// 获取指定时间 Δt 后的轨道位置。
        /// </summary>
        public static DVector3 GetPositionAtTimeDelta(DVector3 posMeters, DVector3 velMeters, double deltaSeconds, double mu = double.NaN)
        {
            double gm = ResolveGM(mu);
            double a, e, p;
            DVector3 eDir, yDir;
            if (!GetOrbitFrame(posMeters, velMeters, out a, out e, out p, out eDir, out yDir, mu))
                return posMeters;

            double currentNu = GetTrueAnomaly(posMeters, velMeters, mu);

            if (e < 0.001)
            {
                double nCirc = MeanMotion(a, gm);
                double nu = currentNu + nCirc * deltaSeconds;
                double r = a;
                double cosNu = Math.Cos(nu);
                double sinNu = Math.Sin(nu);
                return eDir * (r * cosNu) + yDir * (r * sinNu);
            }

            double currentE = TrueToEccentricAnomaly(currentNu, e);
            double currentM = currentE - e * Math.Sin(currentE);
            double nEll = MeanMotion(a, gm);
            double newM = currentM + nEll * deltaSeconds;
            double newE = SolveKepler(newM, e);
            double newNu = EccentricToTrueAnomaly(newE, e);

            double rNew = p / (1.0 + e * Math.Cos(newNu));
            if (rNew < 0.0) rNew = -rNew;

            double cosNuNew = Math.Cos(newNu);
            double sinNuNew = Math.Sin(newNu);
            return eDir * (rNew * cosNuNew) + yDir * (rNew * sinNuNew);
        }

        /// <summary>
        /// 获取指定时间 Δt 后的轨道速度。
        /// </summary>
        public static DVector3 GetVelocityAtTimeDelta(DVector3 posMeters, DVector3 velMeters, double deltaSeconds, double mu = double.NaN)
        {
            double gm = ResolveGM(mu);
            double a, e, p;
            DVector3 eDir, yDir;
            if (!GetOrbitFrame(posMeters, velMeters, out a, out e, out p, out eDir, out yDir, mu))
                return velMeters;

            double currentNu = GetTrueAnomaly(posMeters, velMeters, mu);
            double newNu;

            if (e < 0.001)
            {
                double n = MeanMotion(a, gm);
                newNu = currentNu + n * deltaSeconds;
            }
            else
            {
                double currentE = TrueToEccentricAnomaly(currentNu, e);
                double currentM = currentE - e * Math.Sin(currentE);
                double n = MeanMotion(a, gm);
                double newM = currentM + n * deltaSeconds;
                double newE = SolveKepler(newM, e);
                newNu = EccentricToTrueAnomaly(newE, e);
            }

            double cosNu = Math.Cos(newNu);
            double sinNu = Math.Sin(newNu);
            double sqrtMuOverP = Math.Sqrt(gm / p);
            double vRadial = sqrtMuOverP * e * sinNu;
            double vTangential = sqrtMuOverP * (1.0 + e * cosNu);

            return eDir * (vRadial * cosNu - vTangential * sinNu)
                 + yDir * (vRadial * sinNu + vTangential * cosNu);
        }

        #endregion

        #region 机动计算

        /// <summary>
        /// 分解 dV 到世界空间向量。
        /// </summary>
        public static DVector3 GetWorldDeltaV(DVector3 posMeters, DVector3 velMeters,
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
        public static void WorldDeltaVToComponents(DVector3 posMeters, DVector3 velMeters,
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
            DVector3 dV, int numPoints = 60, double mu = double.NaN)
        {
            DVector3 newVel = velAtNodeMeters + dV;
            return GetOrbitPoints(nodePosMeters, newVel, numPoints, mu);
        }

        #endregion
    }
}