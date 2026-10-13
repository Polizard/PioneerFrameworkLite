using System.Collections.Generic;
using UnityEngine;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 地表补丁上的**细节点缀**（2026-10-10 用户裁定："先整山、河、路，**用我们自己的方式**而不是 RW 原版的方式整"）。
    ///
    /// ## 三条与原版口径分开的地方（都做成常量/单点，改起来只动一处）
    ///
    /// 1. **走 CPU 合成，不走 GPU 材质**：细节直接画进烘焙回读出来的像素缓冲（`Color32[]`）。理由：
    ///    ① 原版那批世界材质（`WorldMaterials.Rivers/Roads/VertexColor…`）是给世界相机那条渲染路造的，
    ///    在"离屏相机 + 命令缓冲"里是否成立**不可离线验证**（本轮不想赌）；② CPU 画法 = 纯 C# ⇒
    ///    全部可 headless 测（本文件每一个函数都有用例）；③ 我们要的是"我们的样子"。
    ///
    /// 2. **山 = 高度场 + 无方向高通**（不是原版"每格一块山图标 sprite"）：把有起伏的格撒成平滑穹顶得到
    ///    高度场，再做高通（`h − 低通(h)`）⇒ 隆起处亮、凹陷处暗。**为什么"无方向"**：
    ///    PFView 的昼夜由太阳方向光给（终止线是真的），再往贴图里烘一个"假的固定光向"会与它打架；
    ///    无方向的高通只表达"这里在隆起/这里在凹陷"，不表态光从哪来 —— 与太阳不冲突。
    ///
    /// 3. **河/路 = 我们自己画的双层带**（岸/描边 + 芯），宽度由数据换算成补丁像素后**夹在可见区间**
    ///    （数据里一条 0.2 格的 creek 在 ~8 px/格 的图上不足 1 px —— 不夹就是"什么都没画"）。
    ///    颜色口径：**数据里有的用数据**（路的 `RoadWorldLayerDef.color`）、**原版写死在代码里的用我们的**（河色，
    ///    原版是 `WorldDrawLayer_Rivers.riverColor` 里硬编码的 (73,82,100)）。
    ///
    /// 全部函数都是纯算术（只用 `Mathf` 与 `Color32` 这类值类型）⇒ xunit 可测，不需要 ECall、不需要渲染器。
    /// </summary>
    internal static class PFViewSurfaceDetails
    {
        // ════════════════════════════════════════════════════
        //  数据载体（都是"补丁图像素坐标"里的纯数值）
        // ════════════════════════════════════════════════════

        /// <summary>一座"山"（穹顶）：落点 + 半径 + 权重（权重由 hilliness 档位与高程给）。</summary>
        internal struct HillStamp
        {
            public float x;
            public float y;
            public float radius;
            public float weight;
        }

        /// <summary>一条带（河/路）的一段：两端点 + 半宽（px）。</summary>
        internal struct LineStamp
        {
            public float ax;
            public float ay;
            public float bx;
            public float by;
            public float halfWidth;
        }

        // ════════════════════════════════════════════════════
        //  像素混合 / 带
        // ════════════════════════════════════════════════════

        /// <summary>把 src 按 alpha 叠到 dst 上（补丁图是不透明的 ⇒ 输出 alpha 恒 255）。</summary>
        internal static Color32 Blend(Color32 dst, Color32 src, float alpha)
        {
            alpha = Mathf.Clamp01(alpha);
            return new Color32(
                (byte)(dst.r + (src.r - dst.r) * alpha),
                (byte)(dst.g + (src.g - dst.g) * alpha),
                (byte)(dst.b + (src.b - dst.b) * alpha),
                255);
        }

        /// <summary>
        /// 画一段带（河/路）：沿线做"点到线段距离"的覆盖度，边沿 1 px 软过渡（不用超采样就有抗锯齿）。
        /// 零长度段退化成圆点（河源的端点、孤立节点）。
        /// </summary>
        internal static void DrawSegment(Color32[] px, int size, LineStamp s, Color32 color, float alpha)
        {
            float dx = s.bx - s.ax, dy = s.by - s.ay;
            float len2 = dx * dx + dy * dy;
            if (len2 < 1e-6f)
            {
                DrawDisc(px, size, s.ax, s.ay, s.halfWidth, color, alpha);
                return;
            }

            float reach = s.halfWidth + 1f;
            int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(s.ax, s.bx) - reach));
            int x1 = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(s.ax, s.bx) + reach));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(s.ay, s.by) - reach));
            int y1 = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(s.ay, s.by) + reach));

            for (int y = y0; y <= y1; y++)
            {
                int row = y * size;
                float py = y + 0.5f - s.ay;
                for (int x = x0; x <= x1; x++)
                {
                    float pxx = x + 0.5f - s.ax;
                    float t = Mathf.Clamp01((pxx * dx + py * dy) / len2);
                    float cx = pxx - dx * t, cy = py - dy * t;
                    float dist = Mathf.Sqrt(cx * cx + cy * cy);
                    float cov = Mathf.Clamp01(s.halfWidth + 0.5f - dist);
                    if (cov <= 0f) continue;
                    px[row + x] = Blend(px[row + x], color, alpha * cov);
                }
            }
        }

        /// <summary>画一个实心软边圆（零长度带的退化情形；也用于端点补圆头）。</summary>
        internal static void DrawDisc(Color32[] px, int size, float cx, float cy, float radius, Color32 color, float alpha)
        {
            int x0 = Mathf.Max(0, Mathf.FloorToInt(cx - radius - 1f));
            int x1 = Mathf.Min(size - 1, Mathf.CeilToInt(cx + radius + 1f));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(cy - radius - 1f));
            int y1 = Mathf.Min(size - 1, Mathf.CeilToInt(cy + radius + 1f));
            for (int y = y0; y <= y1; y++)
            {
                int row = y * size;
                float dy = y + 0.5f - cy;
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - cx;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float cov = Mathf.Clamp01(radius + 0.5f - dist);
                    if (cov <= 0f) continue;
                    px[row + x] = Blend(px[row + x], color, alpha * cov);
                }
            }
        }

        // ════════════════════════════════════════════════════
        //  山：高度场 → 低通 → 无方向高通
        // ════════════════════════════════════════════════════

        /// <summary>
        /// 把每座山撒成一个穹顶累加进高度场：核 = `(1 − d²/r²)²`（核内平滑、边界一阶连续 ⇒ 相加后不出现格子边）。
        /// `stamps` 的坐标/半径用**目标缓冲的像素坐标**（调用方按需要的分辨率换算）。
        /// </summary>
        internal static void AccumulateHills(float[] field, int size, IList<HillStamp> stamps)
        {
            for (int i = 0; i < stamps.Count; i++)
            {
                HillStamp s = stamps[i];
                if (s.radius <= 0.05f || s.weight <= 0f) continue;
                float r2 = s.radius * s.radius;
                int x0 = Mathf.Max(0, Mathf.FloorToInt(s.x - s.radius));
                int x1 = Mathf.Min(size - 1, Mathf.CeilToInt(s.x + s.radius));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(s.y - s.radius));
                int y1 = Mathf.Min(size - 1, Mathf.CeilToInt(s.y + s.radius));
                for (int y = y0; y <= y1; y++)
                {
                    int row = y * size;
                    float dy = y + 0.5f - s.y;
                    for (int x = x0; x <= x1; x++)
                    {
                        float dx = x + 0.5f - s.x;
                        float d2 = dx * dx + dy * dy;
                        if (d2 >= r2) continue;
                        float t = 1f - d2 / r2;
                        field[row + x] += s.weight * t * t;
                    }
                }
            }
        }

        /// <summary>分离式箱式模糊（`passes` 次 ⇒ 近似高斯；`tmp` 是等长暂存）。</summary>
        internal static void BoxBlur(float[] src, float[] dst, float[] tmp, int size, int radius, int passes)
        {
            if (radius < 1 || size <= 1) return;
            for (int p = 0; p < passes; p++)
            {
                BlurAxis(src, tmp, size, radius, true);
                BlurAxis(tmp, dst, size, radius, false);
                if (p + 1 < passes) System.Array.Copy(dst, src, size * size);
            }
        }

        private static void BlurAxis(float[] src, float[] dst, int size, int radius, bool horizontal)
        {
            float norm = 1f / (2 * radius + 1);
            for (int a = 0; a < size; a++)            // a = 被沿线扫过的那一维的索引（横向时是行号）
            {
                float sum = 0f;
                // 预填窗口（越界按边界值夹住 ⇒ 边缘不塌陷）
                for (int k = -radius; k <= radius; k++)
                    sum += Sample(src, size, a, k, horizontal);
                for (int b = 0; b < size; b++)
                {
                    Write(dst, size, a, b, sum * norm, horizontal);
                    sum -= Sample(src, size, a, b - radius, horizontal);
                    sum += Sample(src, size, a, b + radius + 1, horizontal);
                }
            }
        }

        private static float Sample(float[] f, int size, int a, int b, bool horizontal)
        {
            int c = b < 0 ? 0 : (b >= size ? size - 1 : b);
            return horizontal ? f[a * size + c] : f[c * size + a];
        }

        private static void Write(float[] f, int size, int a, int b, float v, bool horizontal)
        {
            if (horizontal) f[a * size + b] = v;
            else f[b * size + a] = v;
        }

        /// <summary>
        /// 自动标定高通幅度：在"有起伏的地方"（低通 &gt; 阈值）取 `|h − 低通(h)|` 的均值。
        /// 有了它，"起伏明暗"的幅度不用拍常数 —— 山脉密集的天体和零星几座山看起来一样明显。
        /// </summary>
        internal static float MeanAbsRelief(float[] field, float[] low, int size, float lowThreshold)
        {
            double sum = 0.0;
            int n = 0;
            for (int i = 0; i < field.Length; i++)
            {
                if (low[i] <= lowThreshold) continue;
                sum += System.Math.Abs(field[i] - low[i]);
                n++;
            }
            return n > 0 ? (float)(sum / n) : 0f;
        }

        /// <summary>
        /// 把起伏明暗乘到像素上：`shade = clamp(1 + strength·(h − 低通)/scale, lo, hi)`。
        /// `field`/`low` 可以是更低分辨率的（本调用做双线性采样）—— 起伏本来就是低频量，省内存也省时间。
        /// </summary>
        internal static void ApplyRelief(Color32[] px, int size, float[] field, float[] low, int fieldSize,
            float reliefScale, float strength, float lo = 0.60f, float hi = 1.35f)
        {
            if (reliefScale <= 1e-6f || strength <= 0f || fieldSize < 2) return;
            float k = strength / reliefScale;
            float map = (float)fieldSize / size;
            for (int y = 0; y < size; y++)
            {
                int row = y * size;
                float fy = (y + 0.5f) * map - 0.5f;
                for (int x = 0; x < size; x++)
                {
                    float fx = (x + 0.5f) * map - 0.5f;
                    float relief = SampleField(field, fieldSize, fx, fy) - SampleField(low, fieldSize, fx, fy);
                    float shade = Mathf.Clamp(1f + k * relief, lo, hi);
                    Color32 c = px[row + x];
                    px[row + x] = new Color32(
                        (byte)Mathf.Clamp(c.r * shade, 0f, 255f),
                        (byte)Mathf.Clamp(c.g * shade, 0f, 255f),
                        (byte)Mathf.Clamp(c.b * shade, 0f, 255f),
                        c.a);
                }
            }
        }

        private static float SampleField(float[] f, int size, float fx, float fy)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, size - 2);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, size - 2);
            float tx = Mathf.Clamp01(fx - x0);
            float ty = Mathf.Clamp01(fy - y0);
            int r0 = y0 * size, r1 = r0 + size;
            float a = Mathf.Lerp(f[x0 + r0], f[x0 + 1 + r0], tx);
            float b = Mathf.Lerp(f[x0 + r1], f[x0 + 1 + r1], tx);
            return Mathf.Lerp(a, b, ty);
        }

        /// <summary>
        /// 确定性哈希（[0,1)）：给"每格山的落点抖动"这类地方用。
        /// **不用 `Rand`** —— 那会动游戏自己的随机流（可复现性是这个仓库的红线之一）。
        /// </summary>
        internal static float Hash01(int a, int salt)
        {
            unchecked
            {
                uint h = (uint)(a * 73856093) ^ (uint)(salt * 19349663);
                h ^= h >> 13;
                h *= 0x85ebca6b;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }
    }
}
