using System;
using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// **[A8] 点到点解析搬运 —— 纯核心**（2026-10-07）。
    ///
    /// §2.7 那张表里唯一还空着的一格：**解析档的"搬运/巡航"**（到达时刻可算 + 按等效 dv 速率扣料）。
    /// 已有先例是"付费瞬时降落"（选 tile、付 Δv、瞬间到）—— 那是**退化的搬运**（T→0）。
    /// 本件把 T 还原出来：给定起终点与这次机动要付的 Δv 总量，**时长由 Δv/可用加速度定**，
    /// 位置沿**解析路径**推进，燃料按 `m·(dv/T)·dt/ve` 逐步入账（=== 同一条 `m·dv/ve` 分账口径）。
    ///
    /// ## 路径形状（为什么是"大圆 + 半径线性"）
    /// 解析档**不模拟受力** ⇒ 形状是**表现**，账才是内容。但形状也不能乱来：
    /// · 方向：沿两个位置的**大圆**（球面线性插值）—— 天然不穿过天体中心，也不会像直线插值那样切进行星；
    /// · 半径：起点半径到终点半径**线性**。
    /// 这样"从低轨搬到高轨"看起来就是"抬升 + 沿弧线转"，且**任何 t 都不会穿到地心**（有判据钉住）。
    ///
    /// ## 与"两档账单一致"的关系
    /// 解析档在 T 内总共扣掉的质量对应的等效 Δv **恰等于** `dvTotal`（判据 `AnalyticTransfer_TwoTier`）——
    /// 物理档真飞一次也是同样的 `dvTotal`（火箭方程的口径）⇒ 换档不逃费也不多收费。
    ///
    /// 【边界】本文件**纯**（只 using System）⇒ 链入 `Pure.Tests`，可单测。
    /// </summary>
    public static class AnalyticTransferCore
    {
        /// <summary>默认搬运时长下限（物理秒）：太短的搬运不是"搬运"而是瞬移（那属付费降落那套）。</summary>
        public const double MinDurationSeconds = 10.0;

        /// <summary>默认时长上限（物理秒，1 小时）：再远的搬运也该比这快，否则解析档就没意义。</summary>
        public const double MaxDurationSeconds = 3600.0;

        /// <summary>
        /// **搬运时长**（s）= `dvTotal / aAvg`（把这次机动的 Δv 摊在可用加速度上烧）。
        /// 夹在 [<see cref="MinDurationSeconds"/>, <see cref="MaxDurationSeconds"/>] 内；
        /// 输入非正 ⇒ 返回下限（宁可"很快"也不返回 0：T=0 是瞬移，不属本件）。
        /// </summary>
        public static double Duration(double dvTotal, double aAvgMps2)
        {
            if (!(dvTotal > 0.0) || !(aAvgMps2 > 0.0)) return MinDurationSeconds;
            double t = dvTotal / aAvgMps2;
            if (t < MinDurationSeconds) return MinDurationSeconds;
            return t > MaxDurationSeconds ? MaxDurationSeconds : t;
        }

        /// <summary>
        /// **路径位置**（系统系）：方向走**大圆**（球面线性插值，`slerp`），半径**线性**。
        /// `u` = 进度（0 = 起点、1 = 终点）。退化输入（零向量/重合方向）⇒ 退化为**线性插值**（不抛）。
        /// </summary>
        public static DVector3 PositionAt(DVector3 from, DVector3 to, double u)
        {
            if (u <= 0.0) return from;
            if (u >= 1.0) return to;

            double r0 = from.magnitude, r1 = to.magnitude;
            double r = r0 + (r1 - r0) * u;

            if (!(r0 > 1e-9) || !(r1 > 1e-9)) return from + (to - from) * u;   // 退化：线性

            DVector3 d0 = from * (1.0 / r0), d1 = to * (1.0 / r1);
            double dot = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(d0, d1)));
            double ang = Math.Acos(dot);
            if (!(ang > 1e-9)) return d0 * r;                                  // 同向：只改半径
            if (ang > Math.PI - 1e-9) return from + (to - from) * u;            // 反向：线性（大圆不唯一）

            double s = Math.Sin(ang);
            DVector3 dir = d0 * (Math.Sin((1.0 - u) * ang) / s) + d1 * (Math.Sin(u * ang) / s);
            return dir * r;
        }

        /// <summary>**路径速度**（系统系）：位置对时间的**有限差分**（解析档的速度是表现量，账不依赖它）。</summary>
        public static DVector3 VelocityAt(DVector3 from, DVector3 to, double u, double durationSeconds)
        {
            if (!(durationSeconds > 0.0)) return DVector3.zero;
            double h = 1e-4;
            DVector3 a = PositionAt(from, to, Math.Max(0.0, u - h));
            DVector3 b = PositionAt(from, to, Math.Min(1.0, u + h));
            double dt = (Math.Min(1.0, u + h) - Math.Max(0.0, u - h)) * durationSeconds;
            return dt > 0.0 ? (b - a) * (1.0 / dt) : DVector3.zero;
        }

        /// <summary>**本步该记的等效 Δv**（m/s）= `dvTotal · dt / T`（摊平到时长上）。</summary>
        public static double DvThisStep(double dvTotal, double durationSeconds, double dtSeconds)
        {
            if (!(dvTotal > 0.0) || !(durationSeconds > 0.0) || !(dtSeconds > 0.0)) return 0.0;
            return dvTotal * (dtSeconds / durationSeconds);
        }

        /// <summary>
        /// **本步该扣的工质**（kg）= `m · dv / ve`（**与燃烧/轨道保持/悬浮解析档同一条口径**）。
        /// `ve ≤ 0` ⇒ 0（没引擎 ⇒ 不扣也不推，由调用方判"付不起"）。
        /// </summary>
        public static double PropellantThisStep(double massKg, double dvThisStep, double ve)
            => ve > 0.0 && massKg > 0.0 && dvThisStep > 0.0 ? massKg * dvThisStep / ve : 0.0;

        /// <summary>
        /// **进度**：从起搬时刻走过的物理秒 → `u ∈ [0,1]`（T ≤ 0 ⇒ 直接 1 = 已到达）。
        /// </summary>
        public static double Progress(double elapsedSeconds, double durationSeconds)
        {
            if (!(durationSeconds > 0.0)) return 1.0;
            if (!(elapsedSeconds > 0.0)) return 0.0;
            double u = elapsedSeconds / durationSeconds;
            return u >= 1.0 ? 1.0 : u;
        }
    }
}
