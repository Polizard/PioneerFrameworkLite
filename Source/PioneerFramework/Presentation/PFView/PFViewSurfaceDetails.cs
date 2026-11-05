using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
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
            bool simd = Avx2.IsSupported;
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
                    float dy2 = dy * dy;
                    int x = x0;
                    if (simd && x1 - x0 + 1 >= 8)
                    {
                        // ── 向量快路（2026-10-14，M3）：x 侧 `dx` 是仿射的 ⇒ 8 lane 一次；
                        //    越界（d2 ≥ r2）用 **ConditionalSelect 保留原值**（不是"加 0"——那会把 −0 抹成 +0）。
                        //    `w·t·t`、`1 − d2/r2` 的逐 lane 次序与标量逐字相同 ⇒ 逐位同结果。──
                        Vector256<float> dy2V = Vector256.Create(dy2);
                        Vector256<float> r2V = Vector256.Create(r2);
                        Vector256<float> wV = Vector256.Create(s.weight);
                        Vector256<float> sxV = Vector256.Create(s.x);
                        for (; x + 7 <= x1; x += 8)                        {
                            Vector256<float> dx = (Vector256.Create((float)x) + LaneOffsets) + HalfVec;
                            Vector256<float> dxv = dx - sxV;
                            Vector256<float> d2 = dxv * dxv + dy2V;
                            Vector256<float> t = OneVec - d2 / r2V;
                            Vector256<float> add = wV * t * t;
                            var old = Vector256.LoadUnsafe(ref field[row + x]);
                            Vector256<float> mask = Vector256.LessThan(d2, r2V);
                            Vector256.StoreUnsafe(Vector256.ConditionalSelect(mask, old + add, old), ref field[row + x]);
                        }
                    }
                    for (; x <= x1; x++)
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

        /// <summary>lane 偏移 `[0..7]` 与 `0.5` 广播（`AccumulateHills` 向量快路用；两处都只读）。</summary>
        private static readonly Vector256<float> LaneOffsets =
            Vector256.Create(0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f);
        private static readonly Vector256<float> HalfVec = Vector256.Create(0.5f);
        private static readonly Vector256<float> OneVec = Vector256.Create(1f);

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
            // ── 竖直半轴向量快路（2026-10-14，M3）：lane = 8 个**相邻列**（内存连续 ⇒ 无需 gather）。
            //    每条 lane 沿行方向走自己那条**递推链**：`(sum − out) + in` 的减/加次序与标量逐字相同，
            //    预填也逐列按 `k = −r..r` 的顺序做 ⇒ 每 lane 与标量逐位同值。
            //    **横向半轴仍是原标量**：滑动窗沿列方向有串行依赖，lane 只能取"行"（内存上跨 size ⇒ 要转置
            //    机器才能 SIMD）——复杂度换 ~10 ms 级收益（烘焙有盘缓存 ⇒ 稀发）⇒ 暂缓，见计划稿 M3。
            //    ⚠ 夹紧（`b±r` 越界）是**每步一个标量行号**、对 8 条 lane 完全相同 ⇒ 向量路里没有 lane 分支。
            if (!horizontal && Avx2.IsSupported && size >= 8)
            {
                BlurAxisVerticalSimd(src, dst, size, radius);
                return;
            }

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

        private static void BlurAxisVerticalSimd(float[] src, float[] dst, int size, int radius)
        {
            float norm = 1f / (2 * radius + 1);
            Span<float> pre = stackalloc float[8];
            int c0 = 0;
            for (; c0 + 8 <= size; c0 += 8)
            {
                // 每列自己的预填（与标量同序：k = −radius..radius 逐个加）
                for (int k = 0; k < 8; k++) pre[k] = 0f;
                for (int j = -radius; j <= radius; j++)
                {
                    int c = j < 0 ? 0 : (j >= size ? size - 1 : j);
                    int baseIdx = c * size + c0;
                    for (int k = 0; k < 8; k++) pre[k] += src[baseIdx + k];
                }
                Vector256<float> sums = Vector256.Create(pre[0], pre[1], pre[2], pre[3], pre[4], pre[5], pre[6], pre[7]);
                for (int b = 0; b < size; b++)
                {
                    Vector256.StoreUnsafe(sums * norm, ref dst[b * size + c0]);
                    int cOut = b - radius < 0 ? 0 : b - radius;
                    int cIn = b + radius + 1 >= size ? size - 1 : b + radius + 1;
                    Vector256<float> vOut = Vector256.LoadUnsafe(ref src[cOut * size + c0]);
                    Vector256<float> vIn = Vector256.LoadUnsafe(ref src[cIn * size + c0]);
                    sums = sums - vOut + vIn;   // ⚠ 次序 = 标量：先减后加（`(sum − out) + in`）
                }
            }
            // 尾部列（不足 8 列）：逐列走原标量式
            for (; c0 < size; c0++)
            {
                float sum = 0f;
                for (int k = -radius; k <= radius; k++)
                    sum += Sample(src, size, c0, k, false);
                for (int b = 0; b < size; b++)
                {
                    dst[b * size + c0] = sum * norm;
                    sum -= Sample(src, size, c0, b - radius, false);
                    sum += Sample(src, size, c0, b + radius + 1, false);
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

            // ── `map == 0.5` 且 AVX2 可用 ⇒ 向量快路（2026-10-14，M3 数学核 SIMD 首批）──
            // 生产口径恒为 `fieldSize = size/2`（`CompositeDetails` 的 `max(64, size/2)`，size 偶数）⇒ 快路恒走；
            // 其余比例走下面的标量路（与改动前逐字相同）。
            if (fieldSize * 2 == size && Avx2.IsSupported)
            {
                ApplyReliefHalfSimd(px, size, field, low, fieldSize, k, lo, hi);
                return;
            }

            for (int y = 0; y < size; y++)
            {
                int row = y * size;
                float fy = (y + 0.5f) * map - 0.5f;
                for (int x = 0; x < size; x++)
                    ApplyReliefPixel(px, row, x, field, low, fieldSize, map, fy, k, lo, hi);
            }
        }

        /// <summary>单像素原式（标量）：`shade = clamp(1 + k·(场−低通), lo, hi)` 乘到三通道。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplyReliefPixel(Color32[] px, int row, int x, float[] field, float[] low, int fieldSize,
            float map, float fy, float k, float lo, float hi)
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

        /// <summary>
        /// **`map == 0.5` 的向量快路**：每 8 个输出像素一次算完"场−低通"的明暗系数。
        ///
        /// **为什么可以逐位同结果**：map = 0.5 时 x 侧模式**完全周期化**——
        /// `fx = 0.5x − 0.25` 在 float 里是精确数（x ≤ 2²⁴，×0.5 与 −0.25 都不丢位）⇒
        /// 奇 x：`x0 = (x−1)/2`、`tx = 0.25`；偶 x（≥2）：`x0 = (x−2)/2 = (x−1)/2`、`tx = 0.75`。
        /// 于是 8 个输出像素（x 从奇数起）的采样下标只有 4 个连续场元素 + 固定置换模式
        /// （lane k 取 `f[j + k/2]` 与 `f[j + k/2 + 1]` ⇒ `[0,0,1,1,2,2,3,3]`，两次 128 位加载 + permute，
        /// 不需要 gather）。Lerp 的求值顺序（`a + (b−a)·t`）、Clamp（输入有限 ⇒ min/max 同解）、乘加次序
        /// 与标量逐字相同 ⇒ 每 lane 的 IEEE 结果与标量逐位一致（台架 §7 以逐位相等断言钉住）。
        /// x = 0（tx = 0 特例）与尾部（含 x = size−1 的 x0 夹紧特例）走 `ApplyReliefPixel` 原式。
        /// </summary>
        private static void ApplyReliefHalfSimd(Color32[] px, int size, float[] field, float[] low, int fieldSize,
            float k, float lo, float hi)
        {
            const float map = 0.5f;
            Vector256<float> txPattern = Vector256.Create(0.25f, 0.75f, 0.25f, 0.75f, 0.25f, 0.75f, 0.25f, 0.75f);
            Vector256<int> pairIdx = Vector256.Create(0, 0, 1, 1, 2, 2, 3, 3);
            Vector256<float> kVec = Vector256.Create(k);
            Vector256<float> oneVec = Vector256.Create(1f);
            Vector256<float> loVec = Vector256.Create(lo);
            Vector256<float> hiVec = Vector256.Create(hi);

            Span<float> shade = stackalloc float[8];   // 明暗系数出向量、逐像素原式应用（位 = Color32 的打包留待实测决定）
            for (int y = 0; y < size; y++)
            {
                int row = y * size;
                float fy = (y + 0.5f) * map - 0.5f;
                int y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, fieldSize - 2);
                float ty = Mathf.Clamp01(fy - y0);
                int r0 = y0 * fieldSize, r1 = r0 + fieldSize;
                Vector256<float> tyVec = Vector256.Create(ty);

                ApplyReliefPixel(px, row, 0, field, low, fieldSize, map, fy, k, lo, hi);   // x=0 特例

                int x = 1;
                for (; x + 7 <= size - 2; x += 8)
                {
                    int j = (x - 1) >> 1;
                    Vector256<float> a0 = PairWiden(field, r0 + j, pairIdx);
                    Vector256<float> a1 = PairWiden(field, r0 + j + 1, pairIdx);
                    Vector256<float> b0 = PairWiden(field, r1 + j, pairIdx);
                    Vector256<float> b1 = PairWiden(field, r1 + j + 1, pairIdx);
                    Vector256<float> la0 = PairWiden(low, r0 + j, pairIdx);
                    Vector256<float> la1 = PairWiden(low, r0 + j + 1, pairIdx);
                    Vector256<float> lb0 = PairWiden(low, r1 + j, pairIdx);
                    Vector256<float> lb1 = PairWiden(low, r1 + j + 1, pairIdx);

                    Vector256<float> valF = LerpV(LerpV(a0, a1, txPattern), LerpV(b0, b1, txPattern), tyVec);
                    Vector256<float> valL = LerpV(LerpV(la0, la1, txPattern), LerpV(lb0, lb1, txPattern), tyVec);
                    Vector256<float> relief = valF - valL;
                    Vector256<float> shadeV = Vector256.Min(Vector256.Max(oneVec + kVec * relief, loVec), hiVec);
                    shadeV.CopyTo(shade);

                    for (int i = 0; i < 8; i++)
                    {
                        Color32 c = px[row + x + i];
                        float s = shade[i];
                        px[row + x + i] = new Color32(
                            (byte)Mathf.Clamp(c.r * s, 0f, 255f),
                            (byte)Mathf.Clamp(c.g * s, 0f, 255f),
                            (byte)Mathf.Clamp(c.b * s, 0f, 255f),
                            c.a);
                    }
                }
                for (; x < size; x++)
                    ApplyReliefPixel(px, row, x, field, low, fieldSize, map, fy, k, lo, hi);   // 尾部（含 size−1 特例）
            }
        }

        /// <summary>`f[at..at+3]` → `[f0,f0,f1,f1,f2,f2,f3,f3]`（128 位加载 + 8 宽置换；`pairIdx` 只用低 4 lane）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<float> PairWiden(float[] f, int at, Vector256<int> pairIdx)
        {
            Vector128<float> v = Vector128.LoadUnsafe(ref f[at]);
            return Avx2.PermuteVar8x32(Vector256.Create(v, v).AsInt32(), pairIdx).AsSingle();
        }

        /// <summary>`a + (b − a)·t`（与 `Mathf.Lerp` 同式；t 进向量前已按原口径 Clamp01 ⇒ 这里不再夹）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<float> LerpV(Vector256<float> a, Vector256<float> b, Vector256<float> t)
            => a + (b - a) * t;

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
