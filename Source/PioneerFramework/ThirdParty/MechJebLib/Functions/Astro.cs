/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 */

using System;
using System.Runtime.CompilerServices;
using MechJebLib.Primitives;
using MechJebLib.Rootfinding;
using MechJebLib.TwoBody;
using MechJebLib.Utils;
using static MechJebLib.Utils.Statics;
using static System.Math;

namespace MechJebLib.Functions
{
    public static class Astro
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double VmagFromVisViva(double mu, double sma, double r) => Sqrt(mu * (2 / r - 1 / sma));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double HmagFromKeplerian(double mu, double sma, double ecc) => Sqrt(mu * sma * (1 - ecc * ecc));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // FIXME: busted with hyperbolic and NANs.
        public static double HmagFromApsides(double mu, double peR, double apR)
        {
            (double sma, double ecc) = SmaEccFromApsides(peR, apR);
            return HmagFromKeplerian(mu, sma, ecc);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V3 HunitFromKeplerian(double inc, double lan) => new V3(Sin(lan) * Sin(inc), Cos(inc), Cos(lan) * Sin(inc));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V3 HvecFromKeplerian(double mu, double sma, double ecc, double inc, double lan) =>
            HunitFromKeplerian(inc, lan) * HmagFromKeplerian(mu, sma, ecc);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V3 HvecFromFlightPathAngle(double r, double v, double gamma, double inc, double lan) =>
            HunitFromKeplerian(inc, lan) * HmagFromFlightPathAngle(r, v, gamma);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double HmagFromFlightPathAngle(double r, double v, double gamma) => r * v * Cos(gamma);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V3 EvecFromKeplerian(double ecc, double inc, double lan, double argP) =>
            new V3(Cos(argP) * Cos(lan) - Cos(inc) * Sin(argP) * Sin(lan),
                Sin(argP) * Sin(inc),
                -(Cos(argP) * Sin(lan) + Cos(inc) * Cos(lan) * Sin(argP))) * ecc;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double SmaFromApsides(double peR, double apR) => (peR + apR) / 2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double EccFromApsides(double peR, double apR) => (apR - peR) / (apR + peR);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (double sma, double ecc) SmaEccFromApsides(double peR, double apR)
        {
            // remap nonsense to circular orbits
            if (apR > 0 && apR < peR)
                apR = peR;
            double sma = SmaFromApsides(peR, apR);
            double ecc = EccFromApsides(peR, apR);
            return (sma, ecc);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double FlightPathAngleFromAngularVelocity(double h, double r, double v) => SafeAcos(h / (r * v));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (double vT, double gammaT) FPATargetFromApsides(double peR, double apR, double attR, double mu)
        {
            if (attR < peR)
                attR = peR;
            if (apR > peR && attR > apR)
                attR = apR;

            (double smaT, double eccT) = SmaEccFromApsides(peR, apR);
            double h = HmagFromKeplerian(mu, smaT, eccT);
            double vT = VmagFromVisViva(mu, smaT, attR);
            double gammaT = FlightPathAngleFromAngularVelocity(h, attR, vT);

            return (vT, gammaT);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (double vT, double gammaT) FPATargetFromKeplerian(double smaT, double eccT, double attR, double mu)
        {
            double h = HmagFromKeplerian(mu, smaT, eccT);
            double vT = VmagFromVisViva(mu, smaT, attR);
            double gammaT = FlightPathAngleFromAngularVelocity(h, attR, vT);

            return (vT, gammaT);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double EscapeVelocity(double mu, double r) => Sqrt(2 * mu / r);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double CircularVelocity(double mu, double r) => Sqrt(mu / r);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double PeriapsisFromKeplerian(double sma, double ecc) => sma * (1 - ecc);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Dual PeriapsisFromKeplerian(Dual sma, Dual ecc) => sma * (1 - ecc);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double ApoapsisFromKeplerian(double sma, double ecc) => sma * (1 + ecc);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Dual ApoapsisFromKeplerian(Dual sma, Dual ecc) => sma * (1 + ecc);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double PeriapsisFromStateVectors(double mu, V3 r, V3 v)
        {
            double sma, ecc;
            (sma, ecc) = SmaEccFromStateVectors(mu, r, v);
            return PeriapsisFromKeplerian(sma, ecc);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Dual PeriapsisFromStateVectors(Dual mu, DualV3 r, DualV3 v)
        {
            Dual sma, ecc;
            (sma, ecc) = SmaEccFromStateVectors(mu, r, v);
            return PeriapsisFromKeplerian(sma, ecc);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double ApoapsisFromStateVectors(double mu, V3 r, V3 v)
        {
            double sma, ecc;
            (sma, ecc) = SmaEccFromStateVectors(mu, r, v);
            return ApoapsisFromKeplerian(sma, ecc);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Dual ApoapsisFromStateVectors(Dual mu, DualV3 r, DualV3 v)
        {
            Dual sma, ecc;
            (sma, ecc) = SmaEccFromStateVectors(mu, r, v);
            return ApoapsisFromKeplerian(sma, ecc);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double EccFromStateVectors(double mu, V3 r, V3 v) => EccVecFromStateVectors(mu, r, v).magnitude;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Dual EccFromStateVectors(Dual mu, DualV3 r, DualV3 v)
        {
            DualV3 eccvec = DualV3.Cross(v / mu, DualV3.Cross(r, v)) - r.normalized;
            return eccvec.magnitude;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V3 EccVecFromStateVectors(double mu, V3 r, V3 v) => V3.Cross(v / mu, V3.Cross(r, v)) - r.normalized;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (double sma, double ecc) SmaEccFromStateVectors(double mu, V3 r, V3 v)
        {
            var h = V3.Cross(r, v);
            double sma = SmaFromStateVectors(mu, r, v);
            return (sma, Sqrt(Max(1 - h.sqrMagnitude / (sma * mu), 0)));
        }

        public static double SlrFromStateVectors(double mu, V3 r, V3 v)
        {
            var h = V3.Cross(r, v);
            return h.sqrMagnitude / mu;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (double l, double ecc) SlrEccFromStateVectors(double mu, V3 r, V3 v)
        {
            double l = SlrFromStateVectors(mu, r, v);
            double ecc = EccVecFromStateVectors(mu, r, v).magnitude;
            return (l, ecc);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (Dual sma, Dual ecc) SmaEccFromStateVectors(Dual mu, DualV3 r, DualV3 v)
        {
            var h = DualV3.Cross(r, v);
            Dual sma = SmaFromStateVectors(mu, r, v);
            var ecc = Dual.Sqrt(Dual.Max(1 - h.sqrMagnitude / (sma * mu), 0));
            return (sma, ecc);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double SmaFromStateVectors(double mu, V3 r, V3 v) => mu / (2.0 * mu / r.magnitude - V3.Dot(v, v));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Dual SmaFromStateVectors(Dual mu, DualV3 r, DualV3 v) => mu / (2.0 * mu / r.magnitude - DualV3.Dot(v, v));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double IncFromStateVectors(V3 r, V3 v)
        {
            V3 hhat = V3.Cross(r, v).normalized;
            return Acos(hhat[1]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double PeriodFromStateVectors(double mu, V3 r, V3 v)
        {
            double sma = SmaFromStateVectors(mu, r, v);
            if (sma < 0)
                throw new Exception("cannot find period of hyperbolic orbit, sma = " + sma);
            return TAU * Sqrt(sma * sma * sma / mu);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double RadiusFromTrueAnomaly(double sma, double ecc, double nu) => sma * (1 - ecc * ecc) / (1 + ecc * Cos(nu));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double RadiusFromTrueAnomaly(double mu, V3 r, V3 v, double nu)
        {
            // FIXME: buggy at ecc = 1.0
            (double sma, double ecc) = SmaEccFromStateVectors(mu, r, v);

            return RadiusFromTrueAnomaly(sma, ecc, nu);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double TrueAnomalyFromRadius(double l, double ecc, double radius) => SafeAcos((l / radius - 1) / ecc);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double TrueAnomalyFromRadius(double mu, V3 r, V3 v, double radius)
        {
            (double l, double ecc) = SlrEccFromStateVectors(mu, r, v);

            return TrueAnomalyFromRadius(l, ecc, radius);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double TrueAnomalyFromStateVectors(double mu, V3 r, V3 v)
        {
            // a custom routine handling edge cases (circular, equatorial) would cost almost as much
            (_, _, _, _, _, double nu, _) = KeplerianFromStateVectors(mu, r, v);
            return nu;
        }

        /// <summary>
        ///     True Anomaly from the Eccentric Anomaly.
        /// </summary>
        /// <param name="mu">Gravitational parameter</param>
        /// <param name="ecc">Eccentricity</param>
        /// <param name="eanom">Eccentric Anomaly</param>
        /// <returns>True Anomaly</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double TrueAnomalyFromEccentricAnomaly(double ecc, double eanom)
        {
            if (ecc < 1)
                return Clamp2Pi(2.0 * Atan(Sqrt((1 + ecc) / (1 - ecc)) * Tan(eanom / 2.0)));
            if (ecc > 1)
                return Clamp2Pi(2.0 * Atan(Sqrt((ecc + 1) / (ecc - 1)) * Tanh(eanom / 2.0)));

            return Clamp2Pi(2 * Atan(eanom));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V3 RhatFromLatLng(double lat, double lng) => new V3(Cos(lat) * Cos(lng), Sin(lat), -Cos(lat) * Sin(lng));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double PitchAngle(V3 v, V3 up) => PI * 0.5 - V3.Angle(v, up);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double FlightPathAngle(V3 r, V3 v) => SafeAsin(V3.Dot(r.normalized, v.normalized));

        // r is the ECI reference point, v is the vector in ECI to be converted to ENU
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V3 ENUToECI(V3 pos, V3 vec)
        {
            double lat = LatitudeFromBCI(pos); // should be geodetic, but we don't care for now
            double lng = LongitudeFromBCI(pos);

            double slat = Sin(lat);
            double slng = Sin(lng);
            double clat = Cos(lat);
            double clng = Cos(lng);

            // §13 极轴 +Y 的 ENU 基：Ê=(−sinλ, 0, −cosλ) · N̂=(−sinφcosλ, cosφ, sinφsinλ) · Û=r̂。
            // 本函数取该基的**转置**（行 = 三个基向量的 x/y/z 分量）⇒ 名实相符的 ENU→ECI。
            // ⚠ 它与 `ECIToENU` 里那份互为转置，P1 换极轴时两者被写反了槽位：往返 M·Mᵀ = I 所以
            //    不会炸（这就解释了为什么游戏里没暴雷），只有中间 ENU 读数全错 —— 由 7 条移植测试同时变红暴露。
            var m = new M3(
                -slng, -slat * clng, clat * clng,
                0, clat, slat,
                -clng, slat * slng, -clat * slng
            );

            return m * vec;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double HeadingForVelocity(V3 r, V3 v)
        {
            V3 venu = ECIToENU(r, v);
            return Clamp2Pi(Atan2(venu[0], venu[1]));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V3 VelocityForHeading(V3 r, V3 v, double newHeading)
        {
            V3 venu = ECIToENU(r, v);
            double hmag = new V3(venu.x, venu.y).magnitude;
            venu[0] = hmag * Sin(newHeading);
            venu[1] = hmag * Cos(newHeading);
            return ENUToECI(r, venu);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V3 ENUHeadingForInclination(double inc, V3 r) => ENUHeadingForInclination(inc, LatitudeFromBCI(r));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V3 ENUHeadingForInclination(double inc, double lat)
        {
            double angle = AngleForInclination(inc, lat);

            return new V3(Cos(angle), Sin(angle), 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double HeadingForInclination(double inc, V3 r) => HeadingForInclination(inc, LatitudeFromBCI(r));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double HeadingForInclination(double inc, double lat)
        {
            double angle = AngleForInclination(inc, lat);

            return Clamp2Pi(Deg2Rad(90) - angle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double AngleForInclination(double inc, double lat)
        {
            double cosAngle = Cos(inc) / Cos(lat);

            if (Abs(cosAngle) > 1.0)
                // for impossible inclinations return due east or west
                return Abs(ClampPi(inc)) < PI * 0.5 ? 0 : Deg2Rad(180);

            // angle is from east, with 90 degrees due north
            double angle = Acos(cosAngle);

            // negative inclinations are conventionally south-going
            if (inc < 0) angle *= -1;

            return angle;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double LatitudeFromBCI(V3 r) => SafeAsin(r.y / r.magnitude);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>B3（§13 极轴统一）：方位基准随极轴从 `+ŷ` 变成 `−ẑ`（**不是换一个分量**，是换基准方向）。</summary>
        public static double LongitudeFromBCI(V3 r) => Atan2(-r.z, r.x);

        // r is the ECI reference point, v is the vector in ENU to be converted to ECI
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V3 ECIToENU(V3 r, V3 v)
        {
            double lat = LatitudeFromBCI(r);
            double lng = LongitudeFromBCI(r);

            double slat = Sin(lat);
            double slng = Sin(lng);
            double clat = Cos(lat);
            double clng = Cos(lng);

            // §13 极轴 +Y：行 = (Ê, N̂, Û) ⇒ 名实相符的 ECI→ENU（与 `ENUToECI` 里那份互为转置）。
            // 判据自证：把 Û 行与 r̂ 比 —— Û=(cosφcosλ, sinφ, −cosφsinλ) = RhatFromLatLng ✓。
            var m = new M3(
                -slng, 0, -clng,
                -slat * clng, clat, slat * slng,
                clat * clng, slat, -clat * slng
            );

            return m * v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V3 EscapeVelocityForInclination(double mu, V3 r, double newInc)
        {
            V3 vf = ENUHeadingForInclination(newInc, r) * EscapeVelocity(mu, r.magnitude);
            vf = ENUToECI(r, vf);
            return vf;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V3 VelocityForInclination(V3 r, V3 v, double newInc)
        {
            V3 v0 = ECIToENU(r, v);
            double horizMag = new V3(v0.x, v0.y).magnitude;
            V3 vf = ENUHeadingForInclination(newInc, r) * horizMag;
            vf.z = v0.z;
            vf = ENUToECI(r, vf);
            return vf;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V3 VelocityForInclination(V3 r, double vmag, double newInc)
        {
            V3 vf = ENUHeadingForInclination(newInc, r) * vmag;
            return ENUToECI(r, vf);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double VelocityFromRadiusSMA(double mu, double rmag, double sma) => Sqrt(mu * (2.0 / rmag - 1 / sma));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Dual VelocityFromRadiusSMA(Dual mu, Dual rmag, Dual sma) => Dual.Sqrt(mu * (2.0 / rmag - 1 / sma));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double VelocityFromRadiusSMA(double mu, V3 r, double sma) => Sqrt(mu * (2.0 / r.magnitude - 1 / sma));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V3 VelocityForFPA(V3 r, V3 v, double newFPA)
        {
            V3 v0 = ECIToENU(r, v);
            double vmag = v0.magnitude;
            V3 vf = new V3(v0.x, v0.y).normalized * Cos(newFPA) * vmag;
            vf.z = Sin(newFPA) * vmag;
            vf = ENUToECI(r, vf);
            return vf;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V3 CircularVelocityFromHvec(double mu, V3 r, V3 h) => V3.Cross(h, r).normalized * CircularVelocity(mu, r.magnitude);

        // r is the ECI reference point, v is the vector in ECI to be converted to pitch, heading angles
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (double pitch, double heading) ECIToPitchHeading(V3 r, V3 v)
        {
            V3 enu = ECIToENU(r, v).normalized;
            return (Asin(enu.z), Clamp2Pi(Atan2(enu.x, enu.y)));
        }

        /// <summary>
        ///     Find the time to a target plane defined by the LAN and inc for a rocket on the ground.  Wrapper which handles
        ///     picking the soonest of the northgoing and southgoing ground tracks.
        /// </summary>
        /// <param name="rotationPeriod">Rotation period of the central body (seconds).</param>
        /// <param name="latitude">Latitude of the launch site (degrees).</param>
        /// <param name="celestialLongitude">Celestial longitude of the current position of the launch site.</param>
        /// <param name="lan">Longitude of the Ascending Node of the target plane (degrees).</param>
        /// <param name="inc">Inclination of the target plane (degrees).</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (double time, double inclination) MinimumTimeToPlane(double rotationPeriod, double latitude, double celestialLongitude,
            double lan, double inc)
        {
            double north = TimeToPlane(rotationPeriod, latitude, celestialLongitude, lan, Abs(inc));
            double south = TimeToPlane(rotationPeriod, latitude, celestialLongitude, lan, -Abs(inc));
            return north < south ? (north, Abs(inc)) : (south, -Abs(inc));
        }

        /// <summary>
        ///     Find the time to a target plane defined by the LAN and inc for a rocket on the ground.
        /// </summary>
        /// <param name="rotationPeriod">Rotation period of the central body (seconds).</param>
        /// <param name="latitude">Latitude of the launch site (degrees).</param>
        /// <param name="celestialLongitude">Celestial longitude of the current position of the launch site (degrees).</param>
        /// <param name="lan">Longitude of the Ascending Node of the target plane (degrees).</param>
        /// <param name="inc">Inclination of the target plane (degrees).</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double TimeToPlane(double rotationPeriod, double latitude, double celestialLongitude, double lan, double inc)
        {
            latitude = Deg2Rad(latitude);
            celestialLongitude = Deg2Rad(celestialLongitude);
            lan = Deg2Rad(lan);
            inc = Deg2Rad(inc);

            // handle singularities at the poles where tan(lat) is infinite
            if (Abs(Abs(latitude) - PI / 2) < EPS)
                return 0;

            // handle equatorial orbits where longitude doesn't matter
            if (Abs(inc) < EPS || Abs(Abs(inc) - PI) < EPS)
                return 0;

            // Napier's rules for spherical trig
            // the clamped Asin produces correct results for abs(inc) < abs(lat)
            double angleEastOfAN = SafeAsin(Tan(latitude) / Tan(Abs(inc)));

            // handle south going trajectories (and the other two quadrants that Asin doesn't cover).
            // if you are launching to the north your AN is always going to be [-90,90] relative to
            // the zero of the launch site.  or facing the launch site your AN is always going to be
            // in "front" of the planet.  but launching south the AN is [90,270] and the AN is always
            // "behind" the planet.
            if (inc < 0)
                angleEastOfAN = PI - angleEastOfAN;

            double lanNow = celestialLongitude - angleEastOfAN;

            double lanDiff = lan - lanNow;

            // handle planets that rotate backwards
            if (rotationPeriod < 0)
                lanDiff = -lanDiff;

            return Clamp2Pi(lanDiff) / TAU * Abs(rotationPeriod);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double MeanMotion(double mu, double sma) => Sqrt(Abs(mu / (sma * sma * sma)));

        // FIXME: hyperbolic and circular orbits
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double TimeToNextApoapsis(double mu, V3 r, V3 v)
        {
            (double sma, double ecc, _, _, _, double nu, _) = KeplerianFromStateVectors(mu, r, v);

            double meanMotion = MeanMotion(mu, sma);

            double manom = Angles.MFromNu(nu, ecc);

            return Clamp2Pi(PI - manom) / meanMotion;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double TimeToNextPeriapsis(double mu, V3 r, V3 v)
        {
            (double sma, double ecc, _, _, _, double nu, _) = KeplerianFromStateVectors(mu, r, v);

            double meanMotion = MeanMotion(mu, sma);

            double manom = Angles.MFromNu(nu, ecc);

            return Clamp2Pi(-manom) / meanMotion;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double TimeToNextTrueAnomaly(double mu, double sma, double ecc, double nu1, double nu2)
        {
            double meanMotion = MeanMotion(mu, sma);

            double manom1 = Angles.MFromNu(nu1, ecc);
            double manom2 = Angles.MFromNu(nu2, ecc);

            if (ecc < 1)
                return Clamp2Pi(manom2 - manom1) / meanMotion;

            return (manom2 - manom1) / meanMotion;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double TimeToNextTrueAnomaly(double mu, V3 r, V3 v, double nu2)
        {
            (double sma, double ecc, _, _, _, double nu1, _) = KeplerianFromStateVectors(mu, r, v);

            return TimeToNextTrueAnomaly(mu, sma, ecc, nu1, nu2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double TimeToNextRadius(double mu, V3 r, V3 v, double radius)
        {
            double nu1 = TrueAnomalyFromRadius(mu, r, v, radius);
            double nu2 = -nu1;
            double time1 = TimeToNextTrueAnomaly(mu, r, v, nu1);
            double time2 = TimeToNextTrueAnomaly(mu, r, v, nu2);
            if (time1 >= 0 && time2 >= 0)
                return Min(time1, time2);
            if (time1 < 0 && time2 < 0)
                return Max(time1, time2);
            return time1 >= 0 ? time1 : time2;
        }

        // Propagate the orbit forward from (r0, v0) until the straight-line chord distance from the
        // starting point equals 'distance', returning the state vectors there.
        public static (V3 r, V3 v) StateVectorsAtDistance(double mu, V3 r0, V3 v0, double distance)
        {
            Check.PositiveFinite(mu);
            Check.Finite(r0);
            Check.Finite(v0);
            Check.PositiveFinite(distance);

            double hi = distance / v0.magnitude;
            double sma = SmaFromStateVectors(mu, r0, v0);
            double cap = sma > 0 ? 0.5 * PeriodFromStateVectors(mu, r0, v0) : double.PositiveInfinity;

            // doubling to find the bracket
            while (Chord(hi, null) < 0)
            {
                hi *= 2;
                if (hi > cap)
                {
                    if (Chord(cap, null) >= 0)
                        hi = cap;
                    else
                        throw new Exception($"StateVectorsAtDistance: distance {distance} is not reachable on this orbit");
                }
            }

            double tof = BrentRoot.Solve(Chord, 0, hi, null);
            return Shepperd.Solve(mu, tof, r0, v0);

            double Chord(double dt, object? o)
            {
                (V3 r, V3 _) = Shepperd.Solve(mu, dt, r0, v0);
                return (r - r0).magnitude - distance;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double TimeToPrevTrueAnomaly(double mu, double sma, double ecc, double nu1, double nu2)
        {
            double meanMotion = MeanMotion(mu, sma);

            double manom1 = Angles.MFromNu(nu1, ecc);
            double manom2 = Angles.MFromNu(nu2, ecc);

            if (ecc < 1)
                return -Clamp2Pi(manom1 - manom2) / meanMotion;

            return (manom2 - manom1) / meanMotion;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double TimeToPrevTrueAnomaly(double mu, V3 r, V3 v, double nu2)
        {
            (double sma, double ecc, _, _, _, double nu1, _) = KeplerianFromStateVectors(mu, r, v);

            return TimeToPrevTrueAnomaly(mu, sma, ecc, nu1, nu2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double TimeToLastRadius(double mu, V3 r, V3 v, double radius)
        {
            double nu1 = TrueAnomalyFromRadius(mu, r, v, radius);
            double nu2 = -nu1;
            double time1 = TimeToPrevTrueAnomaly(mu, r, v, nu1);
            double time2 = TimeToPrevTrueAnomaly(mu, r, v, nu2);
            if (time1 <= 0 && time2 <= 0)
                return Max(time1, time2);
            if (time1 > 0 && time2 > 0)
                return Min(time1, time2);
            return time1 <= 0 ? time1 : time2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double SynodicPeriod(double mu, V3 r1, V3 v1, V3 r2, V3 v2)
        {
            double t1 = PeriodFromStateVectors(mu, r1, v1);
            double t2 = PeriodFromStateVectors(mu, r2, v2);
            int sign = Sign(V3.Dot(V3.Cross(r1, v1), V3.Cross(r2, v2)));
            return Abs(1.0 / (1.0 / t1 - sign / t2));
        }

        /// <summary>
        ///     Kepler's Equation for time since periapsis from the Eccentric Anomaly.
        /// </summary>
        /// <param name="mu">Gravitational parameter</param>
        /// <param name="sma">Semimajor axis</param>
        /// <param name="ecc">Eccentricity</param>
        /// <param name="eanom">Eccentric Anomaly</param>
        /// <returns>Time of flight</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double TimeSincePeriapsisFromEccentricAnomaly(double mu, double sma, double ecc, double eanom)
        {
            double k = Sqrt(Abs(mu / (sma * sma * sma)));
            if (ecc < 1)
                return (eanom - ecc * Sin(eanom)) / k;
            if (ecc > 1)
                return (ecc * Sinh(eanom) - eanom) / k;
            return Sqrt(2) * (eanom + eanom * eanom * eanom / 3.0) / k;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>**B2（§13 极轴统一 P3，2026-09-28）**：本函数返回"近焦点基 → 目标系"的旋转，
        /// 其**列就是 `(P,Q,W)` 在新框架（+Y 极轴）里的分量** —— 这是本函数的**定义**，所以直接**按基矢构造**，
        /// 不写任何"乘积 + 修正因子"（那种写法在换极轴后**差一个符号**：实测把文档的"新轴 3-1-3 再右乘 `M⁻¹`"
        /// 打进去，第一列恰好取负 ⇒ 已弃用该形态，改由几何构造）。
        /// 构造法（纯几何，不猜符号）：① 把 `ẑ` 转到 `ĥ`（`FromToRotation`，绕轴自由度任意）；
        /// ② 再把此刻的 `x̂` 绕 `ĥ` 旋到 `ê` ⇒ 于是 `ẑ→ĥ`、`x̂→ê` ⇒ `ŷ` 自动落到 `q̂ = ĥ × ê` ✓。
        /// 判据 = `PoleFlipDiscriminatorTests.PerifocalMatrix_ColumnsMatchHandDerivedBasis`
        /// （用初等几何手推 `ĥ/ê/q̂` 再比三列；**不碰本文件任何公式**）+ 那 501 条要素往返。</summary>
        public static Q3 PerifocalToECIMatrix(double inc, double argp, double lan)
        {
            V3 hhat = HunitFromKeplerian(inc, lan);                       // 已按 P1 换元 ⇒ 新框架里的轨道法线
            V3 node = new V3(Cos(lan), 0.0, -Sin(lan));                   // 节点线方向（赤道面 = x–z）
            V3 ehat = RotateAboutAxis(node, hhat, argp);                  // 近地点方向（argp=0 时即节点线）
            Q3 r1 = Q3.FromToRotation(V3.zaxis, hhat);
            V3 xNow = r1 * V3.xaxis;
            double signed = Atan2(V3.Dot(V3.Cross(xNow, ehat), hhat), V3.Dot(xNow, ehat));
            return Q3.AngleAxis(signed, hhat) * r1;
        }

        /// <summary>`v` 绕单位轴 `k` 转 `θ`（Rodrigues）—— B2 构造用；纯几何、无约定假设。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V3 RotateAboutAxis(V3 v, V3 k, double theta) =>
            v * Cos(theta) + V3.Cross(k, v) * Sin(theta) + k * (V3.Dot(k, v) * (1.0 - Cos(theta)));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (V3 p, V3 q, Q3 rot) PerifocalFromStateVectors(double mu, V3 r, V3 v)
        {
            (_, double ecc, double inc, double lan, double argp, double nu, double l) = KeplerianFromStateVectors(mu, r, v);
            Q3 rot = PerifocalToECIMatrix(inc, argp, lan);
            (V3 p, V3 q) = PerifocalFromElements(mu, l, ecc, nu);

            return (p, q, rot);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (V3 p, V3 q) PerifocalFromElements(double mu, double l, double ecc, double nu)
        {
            double cnu = Cos(nu);
            double snu = Sin(nu);

            var one = new V3(cnu, snu, 0);
            var two = new V3(-snu, ecc + cnu, 0);

            return (one * l / (1 + ecc * cnu), two * Sqrt(mu / l));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (V3 r, V3 v) StateVectorsAtTrueAnomaly(double mu, V3 r, V3 v, double nu)
        {
            // TODO? there may be a more efficient way to do this
            (double _, double ecc, double inc, double lan, double argp, double _, double l) = KeplerianFromStateVectors(mu, r, v);
            return StateVectorsFromKeplerian(mu, l, ecc, inc, lan, argp, nu);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (V3 r, V3 v) StateVectorsFromKeplerian(double mu, double l, double ecc, double inc, double lan, double argp, double nu)
        {
            (V3 p, V3 q) = PerifocalFromElements(mu, l, ecc, nu);
            Q3 rot = PerifocalToECIMatrix(inc, argp, lan);

            return (rot * p, rot * q);
        }

        /// <summary>
        /// **B1（§13 极轴统一 P2，2026-09-28）**：本函数里的"春分点块"是极轴翻转**最危险的一处**——
        /// 裸把 `[2]→[1]` **能编译、数看着也对、实际错**（文档给的判别例：`Ω=0, i=30°` 会给出 `lan=π` 而不是 `0`）。
        /// 正确改法 = ①`i`/`d` 的极轴分量 2→1；②`q` **变号且换分量**（`-hhat[1] → +hhat[2]`）；
        /// ③`fhat`/`ghat` 按 `(F0,F1,F2)→(F0,F2,−F1)` 重排（与向量换元 `(x,y,z)→(x,z,−y)` 同构）。
        /// 判据：`KeplerianFromStateVectorsTests` 里**手算期望值**的那几个 `[Fact]`（不是那 501 条往返——
        /// 往返对"配对错误"是盲的，实测 P1 后它照样绿）。
        /// </summary>
        public static (double sma, double ecc, double inc, double lan, double argp, double nu, double l) KeplerianFromStateVectors(double mu, V3 r, V3 v)
        {
            double rmag = r.magnitude;
            double vmag = v.magnitude;
            V3 rhat = r.normalized;
            var hv = V3.Cross(r, v);
            V3 hhat = hv.normalized;
            V3 vtmp = v / mu;
            V3 eccvec = V3.Cross(vtmp, hv) - rhat;

            double sma = 1.0 / (2.0 / rmag - vmag * vmag / mu);
            double l = hv.sqrMagnitude / mu;

            int i = hhat[1] >= 0.0 ? +1 : -1;

            double d = 1.0 + i * hhat[1];
            if (d == 0.0)
                throw new ArgumentException("[MechJebLib] KeplerianFromStateVectors(): orbit is at the retrograde singularity somehow");

            double p = hhat[0] / d;
            double q = hhat[2] / d;

            double a = 1.0 / (1.0 + p * p + q * q);

            var fhat = new V3(
                a * (1.0 - p * p + q * q),
                -a * 2.0 * i * p,
                -a * 2.0 * p * q
            );

            var ghat = new V3(
                a * 2.0 * i * p * q,
                a * 2.0 * q,
                -a * i * (1.0 + p * p - q * q)
            );

            double h = V3.Dot(eccvec, ghat);
            double xk = V3.Dot(eccvec, fhat);
            double x1 = V3.Dot(r, fhat);
            double y1 = V3.Dot(r, ghat);
            double xlambdot = Atan2(y1, x1);

            double s = Sqrt(p * p + q * q);
            double ecc = Sqrt(h * h + xk * xk);
            double inc = i == 1 ? 2.0 * Atan(s) : PI - 2.0 * Atan(s);

            double lan = Clamp2Pi(s > EPS ? Atan2(p, q) : 0.0);

            double sum = ecc > EPS ? Atan2(h, xk) : i * lan;
            double argp = Clamp2Pi(ecc > EPS ? sum - i * lan : 0.0);
            double nu = Clamp2Pi(xlambdot - sum);

            return (sma, ecc, inc, lan, argp, nu, l);
        }

        // Danby's method
        public static (double eanom, double nu) AnomaliesFromMean(double manom, double ecc)
        {
            double xma = manom - TAU * Truncate(manom / TAU);
            double eanom, nu;

            if (ecc == 0)
            {
                eanom = nu = xma;
                return (eanom, nu);
            }

            if (ecc < 1) // elliptic initial guess
                eanom = xma + 0.85 * Sign(Sin(xma)) * ecc;
            else // hyperbolic initial guess
                eanom = Log(2 * xma / ecc + 1.8);

            int n = 0;

            while (true)
            {
                double s, c, f, fp, fpp, fppp;

                if (ecc < 1)
                {
                    // elliptic orbit
                    s = ecc * Sin(eanom);
                    c = ecc * Cos(eanom);
                    f = eanom - s - xma;
                    fp = 1 - c;
                    fpp = s;
                    fppp = c;
                }
                else
                {
                    // hyperbolic orbit
                    s = ecc * Sinh(eanom);
                    c = ecc * Cosh(eanom);
                    f = s - eanom - xma;
                    fp = c - 1;
                    fpp = s;
                    fppp = c;
                }

                if (Abs(f) <= EPS || ++n > 20)
                    break;

                // update eccentric anomaly
                double delta = -f / fp;
                double deltastar = -f / (fp + 0.5 * delta * fpp);
                double deltak = -f / (fp + 0.5 * deltastar * fpp + deltastar * deltastar * fppp / 6);
                eanom += deltak;
            }

            // compute true anomaly
            double sta, cta;

            if (ecc < 1)
            {
                // elliptic
                sta = Sqrt(1 - ecc * ecc) * Sin(eanom);
                cta = Cos(eanom) - ecc;
            }
            else
            {
                // hyperbolic
                sta = Sqrt(ecc * ecc - 1) * Sinh(eanom);
                cta = ecc - Cosh(eanom);
            }

            nu = Atan2(sta, cta);

            return (eanom, nu);
        }

        public static (V3 vNeg, V3 vPos, V3 r, double dt) SingleImpulseHyperbolicBurn(double mu, V3 r0, V3 v0, V3 vInf, bool debug = false) =>
            Functions.SingleImpulseHyperbolicBurn.Solve(mu, r0, v0, vInf, debug);

        public static (double dv1, double dv2, double tt, double alpha) HohmannTransferParameters(double mu, V3 r1, V3 r2)
        {
            const double C = 0.35355339059327373;
            double r1M = r1.magnitude;
            double r2M = r2.magnitude;
            double rsum = r1M + r2M;
            double c1 = Sqrt(2.0 * r2M / rsum);
            double c2 = Sqrt(2.0 * r1M / rsum);
            double dv1 = Sqrt(mu / r1M) * (c1 - 1);
            double dv2 = Sqrt(mu / r2M) * (1 - c2);
            double tt = PI * Sqrt(Powi(rsum, 3) / (8 * mu));
            double c3 = r1M / r2M + 1;
            double alpha = PI * (1 - C * Sqrt(Powi(c3, 3)));
            return (dv1, dv2, tt, alpha);
        }

        public static double IspFromMassesThrustBurntime(double m0, double mf, double thrust, double bt)
        {
            double mdot = (m0 - mf) / bt;
            return thrust / (mdot * G0);
        }

        public static double ThrustFromMassesIspBurntime(double m0, double mf, double isp, double bt)
        {
            double mdot = (m0 - mf) / bt;
            return mdot * isp * G0;
        }

        public static double DeltaVFromMassThrustIspBurntime(double m0, double thrust, double isp, double bt)
        {
            Check.PositiveFinite(m0);
            Check.PositiveFinite(thrust);
            Check.PositiveFinite(isp);
            Check.Finite(bt);
            double mdot = thrust / (isp * G0);
            double mf = m0 - mdot * bt;
            Check.PositiveFinite(mf);
            return isp * G0 * Log(m0 / mf);
        }

        public static double MassFromMassThrustIspBurntime(double m0, double thrust, double isp, double bt)
        {
            Check.PositiveFinite(m0);
            Check.PositiveFinite(thrust);
            Check.PositiveFinite(isp);
            Check.Finite(bt);
            double mdot = thrust / (isp * G0);
            return m0 - mdot * bt;
        }
    }
}
