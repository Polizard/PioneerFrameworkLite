using System;
using UnityEngine;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// **并联布局**（2026-10-29，用户令："并联的部件按照推力对称的方式放置"）。
    ///
    /// 轴向堆叠（<see cref="DesignPartAssembler"/> 沿 −Z 一件接一件）只能表达"一件叠一件"，
    /// 做不出一级 4/6 台引擎并排。并联 = **同一个轴向站上放 N 件同型件**，绕主推轴（船体 Z 轴）
    /// 在 X-Y 平面里均匀分布。
    ///
    /// 为什么"均匀分布 + 等半径"是硬约束而不是美术偏好：
    /// 每件推力沿 +Z、作用点在 (x, y, z)，绕质心的合力矩 `τ = Σ r×F` 只有在这种对称下才为零
    /// （<see cref="SpaceEntity.GetEngineTorqueAboutCom"/> 读的正是这个力矩，非零就要动量轮去顶）。
    /// 环上均分 + 等半径 ⇒ Σx = Σy = 0（正多边形顶点和）⇒ 合力矩 = 0，质心落在轴上。
    ///
    /// **1…7 的简洁答案**（环半径 `R = w / (2·sin(π/k))`，w = 单件宽度 ⇒ 相邻两件恰好相切）：
    ///
    /// | N | 摆法 | R | 横向跨度 2R+w |
    /// |---|---|---|---|
    /// | 1 | 轴上 1 件 | 0 | w |
    /// | 2 | 环 k=2（±X 相切于轴） | 0.5w | 2w |
    /// | 3 | 环 k=3 | 0.577w | 2.155w |
    /// | 4 | 环 k=4（十字） | 0.707w | 2.414w |
    /// | 5 | 环 k=5 | 0.851w | 2.701w |
    /// | 6 | 环 k=6（六方） | w | 3w |
    /// | 7 | **中心 1 + 环 k=6** | w | 3w |
    ///
    /// 7 用"六方 + 中心"而不是七边形环：半径小 13%、中心件与环件恰好相切，
    /// 而且与"芯级 + 六台环绕"的火箭直觉一致（土星五号/F-9 那套）。
    ///
    /// 偶数环**成对镜像生成**（后半环 = 前半环取反）⇒ Σoffset **逐位**为 0，力矩对消是精确的；
    /// 奇数环（3/5）用解析均分，Σ 只剩 1e-16 量级的浮点残差（单测按容差判）。
    /// </summary>
    public static class ParallelLayout
    {
        /// <summary>最少并联台数（= 不并联）。</summary>
        public const int MinCount = 1;

        /// <summary>最多并联台数（1…7 都有解析答案；见类注释的表）。</summary>
        public const int MaxCount = 7;

        public static int Clamp(int count) => Mathf.Clamp(count, MinCount, MaxCount);

        /// <summary>中心件数：1 件（就在轴上）与 7 件（六方 + 中心）有，其余为 0。</summary>
        public static int CenterCount(int count)
        {
            int n = Clamp(count);
            return n == 1 || n == MaxCount ? 1 : 0;
        }

        /// <summary>环上的件数（3/4/5/6 环；7 = 6 环 + 1 心）。</summary>
        public static int RingCount(int count) => Clamp(count) - CenterCount(count);

        /// <summary>
        /// 环半径（米）：`R = w / (2·sin(π/k))`，即**相邻两件恰好相切**的最小值。
        /// k = 0（1 件）⇒ 0。
        /// </summary>
        public static float RadiusM(float partWidthM, int count)
        {
            int k = RingCount(count);
            if (k <= 0) return 0f;
            float w = Mathf.Max(0.01f, partWidthM);
            return w / (2f * Mathf.Sin(Mathf.PI / k));
        }

        /// <summary>整个并联站的横向跨度（米）：`2R + w`；1 件 ⇒ `w`。</summary>
        public static float SpanM(float partWidthM, int count)
        {
            float w = Mathf.Max(0.01f, partWidthM);
            if (Clamp(count) <= 1) return w;
            return 2f * RadiusM(w, count) + w;
        }

        /// <summary>
        /// 环的起始角：**奇数环从 +Y 起**（正上方有件，侧视图/正视都对称）；偶数环从 +X 起。
        /// 只影响朝向，不影响对称性（正多边形转过任意角，顶点和仍为 0）。
        /// </summary>
        public static float StartAngleRad(int count)
        {
            int k = RingCount(count);
            if (k <= 0) return 0f;
            return k % 2 == 1 ? Mathf.PI * 0.5f : 0f;
        }

        /// <summary>
        /// 把 <paramref name="count"/> 件的横向偏移（船体系 X/Y，米）填进两个数组。
        /// **顺序 = 中心件在前，环按角度递增**（确定性：同一份设计每次装配的 part 顺序一致）。
        /// 数组长度必须 ≥ count。
        /// </summary>
        public static void Offsets(int count, float partWidthM, float[] xs, float[] ys)
        {
            if (xs == null || ys == null) return;
            int n = Clamp(count);
            if (xs.Length < n || ys.Length < n) return;

            int ring = RingCount(n);
            float radius = RadiusM(partWidthM, n);

            int idx = 0;
            if (CenterCount(n) > 0)
            {
                xs[0] = 0f;
                ys[0] = 0f;
                idx = 1;
            }
            if (ring <= 0) return;

            float start = StartAngleRad(n);
            if (ring % 2 == 0)
            {
                // 偶数环：只算半圈，另一半取反 ⇒ 逐位对消
                for (int j = 0; j < ring / 2; j++)
                {
                    float a = start + 2f * Mathf.PI * j / ring;
                    float x = radius * Mathf.Cos(a);
                    float y = radius * Mathf.Sin(a);
                    xs[idx] = x;
                    ys[idx] = y;
                    idx++;
                    xs[idx] = -x;
                    ys[idx] = -y;
                    idx++;
                }
            }
            else
            {
                // 奇数环：解析均分（3/5 件，Σ 只剩浮点残差）
                for (int j = 0; j < ring; j++)
                {
                    float a = start + 2f * Mathf.PI * j / ring;
                    xs[idx] = radius * Mathf.Cos(a);
                    ys[idx] = radius * Mathf.Sin(a);
                    idx++;
                }
            }
        }

        /// <summary>读数用的一句话说明（窗口 tooltip / 日志）。</summary>
        public static string Describe(int count, float partWidthM)
        {
            int n = Clamp(count);
            if (n <= 1) return "单件（在轴上）";
            float r = RadiusM(partWidthM, n);
            return CenterCount(n) > 0
                ? $"中心 1 + 环 {RingCount(n)}（R={r:0.##} m，跨度 {SpanM(partWidthM, n):0.##} m）"
                : $"环 {RingCount(n)}（R={r:0.##} m，跨度 {SpanM(partWidthM, n):0.##} m）";
        }
    }
}
