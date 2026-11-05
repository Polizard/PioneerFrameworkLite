using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 太阳叶子数学的**就地移植**（2026-10-15，"反射清理"批）：保真照抄 1.7 `GenCelestial` 的
    /// 四个 **private static** 叶子函数与两条私有曲线：
    /// `SurfaceNormal` / `SunPositionUnmodified` / `SunPosition` / `CelestialSunGlowPercent`
    /// + `SunOffsetFractionFromLatitudeCurve` / `SunPeekAroundDegreesFactorCurve`。
    ///
    /// ## 为什么不调公开 API（2026-10-15 核对结论）
    /// 公开重载 `GenCelestial.CelestialSunGlow(Map/PlanetTile, ticksAbs)` 内部**再入
    /// `GenDate.DayOfYear/DayPercent`**——而那两个正是我们打了"时间刻度替换"补丁的方法
    /// （`CalendarAndSunPatch`）⇒ 走公开 API 会被二次替换、算出错值。
    /// 本类出现前，代码用**反射**（`AccessTools.Method` + `Delegate.CreateDelegate`）拿私有叶子来绕过；
    /// 现改为就地移植 ⇒ **太阳路径零反射**。
    ///
    /// ## 移植基准与漂移风险（照"仪器必须被验证"的纪律写明）
    /// · 基准 = `D:/tmp/rw17/src/Assembly-CSharp/RimWorld/GenCelestial.cs`
    ///   （叶子 :125-177；曲线 :245-255 与 :258-268，2026-10-15 逐行核对）；
    /// · 若 vanilla 之后改太阳模型，本文件必须同步——**判据 = 与消费方（殖民地图光照/世界图太阳盘）
    ///   的观感对照**，以及 `Vector3.RotateTowards`/`Mathf.InverseLerp` 等 UnityStubs 实现未变。
    /// </summary>
    public static class PF_SunMath
    {
        public readonly record struct CelestialDayCycle(float DayPercent, int DayOfYear, float DayOfYearFraction);
        public static CelestialDayCycle EvaluateCycle(double displayTicksAbs, float dayLengthTicks, float longitudeDegrees)
        {
            if (!System.Double.IsFinite(displayTicksAbs) || !float.IsFinite(dayLengthTicks) || dayLengthTicks <= 0 || !float.IsFinite(longitudeDegrees))
                throw new System.ArgumentOutOfRangeException(nameof(displayTicksAbs));
            long ticks = (long)displayTicksAbs;
            if (System.Math.Abs(dayLengthTicks - 60000f) > 10f) ticks = (long)(ticks * (60000.0 / dayLengthTicks));
            double days = (ticks + (double)(longitudeDegrees / 360f * 60000f)) / 60000.0;
            return new((float)(((days % 1.0) + 1.0) % 1.0), (int)(((System.Math.Floor(days) % 60.0) + 60.0) % 60.0),
                (float)(((days % 60.0) + 60.0) % 60.0));
        }
        /// <summary>照抄 `GenCelestial.SunOffsetFractionFromLatitudeCurve`（:258-268）。</summary>
        private static readonly SimpleCurve SunOffsetFractionFromLatitudeCurve = new()
        {
            { new CurvePoint(70f, 0.2f), true },
            { new CurvePoint(75f, 1.5f), true },
        };

        /// <summary>照抄 `GenCelestial.SunPeekAroundDegreesFactorCurve`（:245-255）。</summary>
        private static readonly SimpleCurve SunPeekAroundDegreesFactorCurve = new()
        {
            { new CurvePoint(70f, 1f), true },
            { new CurvePoint(75f, 0.05f), true },
        };

        /// <summary>照抄 `GenCelestial.SurfaceNormal`（:170-177）。</summary>
        public static Vector3 SurfaceNormal(float latitude)
        {
            Vector3 vector = new(1f, 0f, 0f);
            return Quaternion.AngleAxis(latitude, new Vector3(0f, 0f, 1f)) * vector;
        }

        /// <summary>照抄 `GenCelestial.SunPositionUnmodified`（:140-147）。</summary>
        public static Vector3 SunPositionUnmodified(float dayOfYear, float dayPercent, Vector3 initialSunPos, float latitude = 0f)
        {
            Vector3 vector = initialSunPos * 100f;
            float num = -Mathf.Cos(dayOfYear / 60f * 3.1415927f * 2f);
            vector.y += num * 100f * SunOffsetFractionFromLatitudeCurve.Evaluate(latitude);
            vector = Quaternion.AngleAxis((dayPercent - 0.5f) * 360f, Vector3.up) * vector;
            return vector.normalized;
        }

        /// <summary>照抄 `GenCelestial.SunPosition`（:125-138）。</summary>
        public static Vector3 SunPosition(float latitude, int dayOfYear, float dayPercent)
        {
            Vector3 vector = SurfaceNormal(latitude);
            Vector3 vector2 = SunPositionUnmodified(dayOfYear, dayPercent, new Vector3(1f, 0f, 0f), latitude);
            float num = SunPeekAroundDegreesFactorCurve.Evaluate(latitude);
            vector2 = Vector3.RotateTowards(vector2, vector, 0.33161256f * num, 9999999f);
            float num2 = Mathf.InverseLerp(60f, 0f, Mathf.Abs(latitude));
            if (num2 > 0f)
            {
                vector2 = Vector3.RotateTowards(vector2, vector, 6.2831855f * (17f * num2 / 360f), 9999999f);
            }
            return vector2.normalized;
        }

        /// <summary>照抄 `GenCelestial.CelestialSunGlowPercent`（:150-156）。</summary>
        public static float SunGlowPercent(float latitude, int dayOfYear, float dayPercent)
        {
            Vector3 vector = SurfaceNormal(latitude);
            Vector3 vector2 = SunPosition(latitude, dayOfYear, dayPercent);
            float num = Vector3.Dot(vector.normalized, vector2);
            return Mathf.Clamp01(Mathf.InverseLerp(0f, 0.7f, num));
        }
    }
}
