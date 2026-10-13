using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 细节点缀（山 / 河 / 路）的**纯核心**钉子。用户 2026-10-10："先整山、河、路，用我们自己的方式。"
    ///
    /// 这几条不变量是"画对了"的可离线部分：混合端点、带的走向与端部、软边覆盖、高度场的峰/外零、
    /// 模糊保质量、**平场不动像素**、以及**起伏明暗不表态光向**（径向对称场 ⇒ 四方向同亮）。
    /// 真正"好不好看"只能进游戏看（那是用户侧的事，本文件不声称）。
    /// </summary>
    public class PFViewSurfaceDetailsTests
    {
        static Color32[] Buffer(int size, byte v = 0)
        {
            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(v, v, v, 255);
            return px;
        }

        [Fact]
        public void Blend_HitsEndpoints()
        {
            var dst = new Color32(10, 20, 30, 255);
            var src = new Color32(200, 100, 50, 255);

            Assert.Equal(dst, PFViewSurfaceDetails.Blend(dst, src, 0f));
            Assert.Equal(src, PFViewSurfaceDetails.Blend(dst, src, 1f));
            Assert.Equal(new Color32(105, 60, 40, 255), PFViewSurfaceDetails.Blend(dst, src, 0.5f));
        }

        [Fact]
        public void DrawSegment_PaintsAlongAxis_AndLeavesFarPixelsAlone()
        {
            const int n = 64;
            var px = Buffer(n);
            var color = new Color32(255, 255, 255, 255);

            PFViewSurfaceDetails.DrawSegment(px, n, new PFViewSurfaceDetails.LineStamp
            { ax = 10.5f, ay = 32.5f, bx = 40.5f, by = 32.5f, halfWidth = 2f }, color, 1f);

            // 轴心一行整段都被盖住
            for (int x = 12; x < 40; x++)
                Assert.Equal(255, px[32 * n + x].r);
            // 段外的像素一个都不许碰
            Assert.Equal(0, px[32 * n + 2].r);
            Assert.Equal(0, px[10 * n + 20].r);
            Assert.Equal(0, px[5 * n + 5].r);
        }

        [Fact]
        public void DrawSegment_EdgeIsSoft_AndOutsideIsEmpty()
        {
            const int n = 64;
            var px = Buffer(n);
            PFViewSurfaceDetails.DrawSegment(px, n, new PFViewSurfaceDetails.LineStamp
            { ax = 10.5f, ay = 32.5f, bx = 40.5f, by = 32.5f, halfWidth = 2f },
                new Color32(255, 255, 255, 255), 1f);

            // halfWidth 2 ⇒ 中心行全覆盖(2.5−0)、±2 行半覆盖(2.5−2)、±3 行零（软边 = 抗锯齿的来源）
            Assert.Equal(255, px[32 * n + 20].r);
            Assert.Equal(127, px[34 * n + 20].r);
            Assert.Equal(0, px[35 * n + 20].r);
        }

        /// <summary>与 `DrawSegment` 同一条覆盖度算式（这里是**规格**，不是实现复制：覆盖度是这条带的契约）。</summary>
        static float Coverage(PFViewSurfaceDetails.LineStamp s, int x, int y)
        {
            float dx = s.bx - s.ax, dy = s.by - s.ay;
            float len2 = dx * dx + dy * dy;
            float pxx = x + 0.5f - s.ax, py = y + 0.5f - s.ay;
            float t = len2 < 1e-6f ? 0f : Mathf.Clamp01((pxx * dx + py * dy) / len2);
            float cx = pxx - dx * t, cy = py - dy * t;
            return Mathf.Clamp01(s.halfWidth + 0.5f - Mathf.Sqrt(cx * cx + cy * cy));
        }

        /// <summary>数"被芯**完全**覆盖、但颜色不是芯色"的像素 —— 这个数必须恒为 0，
        /// 否则带子就是一段深一段浅（用户实测的"一节一节"）。</summary>
        static int CountCoreCoveredButNotCore(Color32[] px, int n, PFViewSurfaceDetails.LineStamp[] cores, Color32 core)
        {
            int bad = 0;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    bool covered = false;
                    for (int i = 0; i < cores.Length && !covered; i++)
                        covered = Coverage(cores[i], x, y) > 0.99f;
                    if (!covered) continue;
                    var c = px[y * n + x];
                    if (c.r != core.r || c.g != core.g || c.b != core.b) bad++;
                }
            }
            return bad;
        }

        [Fact]
        public void TwoPassLayerOrder_CoreWinsAtJoints_InterleavingBreaksIt()
        {
            // 用户 2026-10-10 实测报回来的"一节一节"：两段相接的带、岸比芯宽（出厂参数 bank = 1.8×核 + 0.6 px）。
            // · **按层两遍**（岸、岸、芯、芯）⇒ 芯覆盖到的地方恒为芯色 ⇒ 带子连续 ✓
            // · **逐段两层**（岸、芯、岸、芯）⇒ 后一段的岸压在前一段的芯上 ⇒ 相接处一段深一段浅 ✗
            // 生产里 `DrawRivers/DrawRoads` 走的是前一种（按层两遍）。
            const int n = 64;
            var bank = new Color32(46, 62, 78, 255);
            var core = new Color32(108, 146, 176, 255);
            float coreHalf = 1.6f, bankHalf = coreHalf * 1.8f + 0.6f;

            var s1b = new PFViewSurfaceDetails.LineStamp { ax = 10f, ay = 32.5f, bx = 32.5f, by = 32.5f, halfWidth = bankHalf };
            var s1c = new PFViewSurfaceDetails.LineStamp { ax = 10f, ay = 32.5f, bx = 32.5f, by = 32.5f, halfWidth = coreHalf };
            var s2b = new PFViewSurfaceDetails.LineStamp { ax = 32.5f, ay = 32.5f, bx = 32.5f, by = 11f, halfWidth = bankHalf };
            var s2c = new PFViewSurfaceDetails.LineStamp { ax = 32.5f, ay = 32.5f, bx = 32.5f, by = 11f, halfWidth = coreHalf };
            var cores = new[] { s1c, s2c };

            var layered = Buffer(n, 10);
            PFViewSurfaceDetails.DrawSegment(layered, n, s1b, bank, 1f);
            PFViewSurfaceDetails.DrawSegment(layered, n, s2b, bank, 1f);
            PFViewSurfaceDetails.DrawSegment(layered, n, s1c, core, 1f);
            PFViewSurfaceDetails.DrawSegment(layered, n, s2c, core, 1f);

            var interleaved = Buffer(n, 10);
            PFViewSurfaceDetails.DrawSegment(interleaved, n, s1b, bank, 1f);
            PFViewSurfaceDetails.DrawSegment(interleaved, n, s1c, core, 1f);
            PFViewSurfaceDetails.DrawSegment(interleaved, n, s2b, bank, 1f);
            PFViewSurfaceDetails.DrawSegment(interleaved, n, s2c, core, 1f);

            Assert.Equal(0, CountCoreCoveredButNotCore(layered, n, cores, core));
            Assert.True(CountCoreCoveredButNotCore(interleaved, n, cores, core) > 0,
                "逐段两层画法必须在拐角处留下「芯被暗岸压住」的像素（这条用例是那个症状的复刻）");
        }

        [Fact]
        public void DrawSegment_OpaqueInterior_IsIdempotent()
        {
            // 不透明的"内部"（全覆盖）重叠是幂等的 —— 相邻段在格心处重叠多少次，颜色都不变。
            // （软边像素不在此列：那是 coverage<1 的混合，会把边越叠越实，不会成结。）
            const int n = 64;
            var once = Buffer(n, 40);
            var twice = Buffer(n, 40);
            var color = new Color32(200, 180, 60, 255);
            var s = new PFViewSurfaceDetails.LineStamp { ax = 12f, ay = 32.5f, bx = 40f, by = 32.5f, halfWidth = 2.5f };

            PFViewSurfaceDetails.DrawSegment(once, n, s, color, 1f);
            PFViewSurfaceDetails.DrawSegment(twice, n, s, color, 1f);
            PFViewSurfaceDetails.DrawSegment(twice, n, s, color, 1f);

            for (int x = 14; x < 38; x++)
                Assert.Equal(once[32 * n + x], twice[32 * n + x]);
        }

        [Fact]
        public void DrawSegment_ZeroLength_BecomesDisc()
        {
            const int n = 48;
            var px = Buffer(n);
            PFViewSurfaceDetails.DrawSegment(px, n, new PFViewSurfaceDetails.LineStamp
            { ax = 24.5f, ay = 24.5f, bx = 24.5f, by = 24.5f, halfWidth = 2f },
                new Color32(200, 200, 200, 255), 1f);

            Assert.True(px[24 * n + 24].r > 0, "零长度段的端点必须画出来（否则河源/孤立节点会消失）");
            Assert.Equal(0, px[10 * n + 10].r);
        }

        [Fact]
        public void AccumulateHills_PeaksAtStamp_AndIsZeroOutside()
        {
            const int n = 32;
            var field = new float[n * n];
            var stamps = new List<PFViewSurfaceDetails.HillStamp>
            { new PFViewSurfaceDetails.HillStamp { x = 16f, y = 16f, radius = 6f, weight = 1f } };

            PFViewSurfaceDetails.AccumulateHills(field, n, stamps);

            Assert.True(field[16 * n + 16] > 0.9f, "穹顶中心应接近权重峰值：" + field[16 * n + 16]);
            Assert.Equal(0f, field[2 * n + 2]);              // 半径外
            Assert.Equal(0f, field[30 * n + 30]);
        }

        [Fact]
        public void AccumulateHills_WeightsAddUp()
        {
            const int n = 32;
            var one = new float[n * n];
            var two = new float[n * n];
            var s = new PFViewSurfaceDetails.HillStamp { x = 16f, y = 16f, radius = 6f, weight = 0.7f };
            PFViewSurfaceDetails.AccumulateHills(one, n, new List<PFViewSurfaceDetails.HillStamp> { s });
            PFViewSurfaceDetails.AccumulateHills(two, n, new List<PFViewSurfaceDetails.HillStamp> { s, s });

            Assert.True(Mathf.Abs(two[16 * n + 16] - 2f * one[16 * n + 16]) < 1e-5f,
                "同点重叠必须线性相加（山脉就是靠相邻格子的穹顶叠出来的）");
        }

        [Fact]
        public void BoxBlur_SpreadsDelta_AndRoughlyPreservesMass()
        {
            const int n = 64;
            var src = new float[n * n];
            var dst = new float[n * n];
            var tmp = new float[n * n];
            src[32 * n + 32] = 1f;
            float sumIn = 1f;

            PFViewSurfaceDetails.BoxBlur(src, dst, tmp, n, 3, 2);

            Assert.True(dst[32 * n + 32] < 0.1f, "模糊之后中心不该还是原来的尖峰：" + dst[32 * n + 32]);
            Assert.True(dst[32 * n + 33] > 0f, "模糊必须把值摊到邻域");
            double sumOut = 0.0;
            for (int i = 0; i < dst.Length; i++) sumOut += dst[i];
            Assert.True(Mathf.Abs((float)(sumOut - sumIn)) < 0.05f, "模糊后总量应大致守恒：" + sumOut);
        }

        [Fact]
        public void ApplyRelief_FlatField_LeavesPixelsUntouched()
        {
            const int n = 32;
            var px = Buffer(n, 120);
            var field = new float[16 * 16];
            var low = new float[16 * 16];
            for (int i = 0; i < field.Length; i++) { field[i] = 1f; low[i] = 1f; }

            PFViewSurfaceDetails.ApplyRelief(px, n, field, low, 16, 0.5f, 0.4f);

            for (int i = 0; i < px.Length; i++)
                Assert.Equal(new Color32(120, 120, 120, 255), px[i]);
        }

        [Fact]
        public void ApplyRelief_DirectionFree_IsRadiallySymmetric()
        {
            // 中心高、四周低的径向对称场：同一半径的四方向采样必须一样亮 ——
            // 这就是"我们**不**往贴图里烤一个假光向"的可测形式（否则会有一侧明显更亮）。
            // 对称轴落在 (31.5, 31.5)（半分辨率场中心 15.5 ⇔ 全分辨率 31.5）⇒ 取 (31,31∓r) 与 (31∓r,31) 四向同半径。
            const int n = 64;
            const int f = 32;
            var field = new float[f * f];
            var low = new float[f * f];
            var px = Buffer(n, 120);

            for (int y = 0; y < f; y++)
            {
                for (int x = 0; x < f; x++)
                {
                    float dx = x - 15.5f, dy = y - 15.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    field[y * f + x] = Mathf.Max(0f, 1f - d / 8f);      // 一个圆丘
                    low[y * f + x] = 0.5f;                              // 常数低通 ⇒ 高通 = 圆丘 − 0.5
                }
            }

            PFViewSurfaceDetails.ApplyRelief(px, n, field, low, f, 0.3f, 0.4f);

            byte up = px[23 * n + 31].r;
            byte down = px[40 * n + 31].r;
            byte left = px[31 * n + 23].r;
            byte right = px[31 * n + 40].r;
            Assert.True(Mathf.Abs(up - down) <= 1 && Mathf.Abs(left - right) <= 1 && Mathf.Abs(up - left) <= 1,
                string.Format("无方向起伏必须径向对称：上{0} 下{1} 左{2} 右{3}", up, down, left, right));

            Assert.True(px[31 * n + 31].r > up, "丘顶应比丘腰亮（高通的符号方向）");
            Assert.True(px[31 * n + 31].r > 120, "丘顶应比平坦处（120）亮");
        }

        [Fact]
        public void Hash01_IsInRange_AndDeterministic_AndVaries()
        {
            var seen = new HashSet<float>();
            for (int i = 0; i < 512; i++)
            {
                float v = PFViewSurfaceDetails.Hash01(i, 11);
                Assert.True(v >= 0f && v < 1f, "哈希必须落在 [0,1)：" + v);
                Assert.Equal(v, PFViewSurfaceDetails.Hash01(i, 11));      // 确定性（同一格每次一样）
                seen.Add(v);
            }
            Assert.True(seen.Count > 480, "512 次里应该有足够多的不同值（抖动才像抖动）：" + seen.Count);
        }
    }
}
