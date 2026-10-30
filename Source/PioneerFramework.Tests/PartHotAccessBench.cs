using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **部件功能化 §2-A 的台架**：热循环取件方式 `IReadOnlyList&lt;SpacePart&gt;` vs `Span&lt;SpacePart&gt;`。
    ///
    /// 口径（照设计稿 §2 的台架纪律）：
    /// · **同一进程、同轮交错**（禁跨轮/跨构建比较）：每个采样里 A/B 各跑一遍，奇数采样颠倒先后；
    /// · 每采样 = 64 遍完整 256 件循环，取 201 个采样的**中位数**；
    /// · 报告 ns/件（= 中位数 ÷ (64×256)）+ 每采样分配字节数（应为 0）。
    ///
    /// 这是**微循环**台架：它只量"取件"这一步，不含 `part盒重组` 的几何运算。
    /// 生产侧读数（`part盒重组` 21.7–22.5 µs）由游戏内 PerfProbe 出，两者不可直接相除。
    /// 判据：span 侧 ns/件 **不高于**接口侧（本台架只出读数，不判 PASS/FAIL；见 `CollisionContactBench` 同例）；
    /// 两个循环的取值校验和必须**逐位相等**（证明确实读的是同一批数）。
    /// </summary>
    public class PartHotAccessBench
    {
        private readonly ITestOutputHelper _out;
        public PartHotAccessBench(ITestOutputHelper output) => _out = output;

        private const int PartCount = 256;
        private const int InnerReps = 64;
        private const int Samples = 201;

        private static List<SpacePart> BuildParts()
        {
            var parts = new List<SpacePart>(PartCount);
            for (int i = 0; i < PartCount; i++)
            {
                parts.Add(new SpacePart
                {
                    partId = i + 1,
                    defName = "PF_ShipPart_Cube",
                    positionShip = new DVector3(0.0, -i * 2.0, 0.0),
                    boundsSizeShip = new DVector3(2.0, 2.0, 2.0),
                });
            }
            return parts;
        }

        private static double LoopUs_Iface(IReadOnlyList<SpacePart> parts, int reps, out double checksum)
        {
            double sink = 0.0;
            long t0 = Stopwatch.GetTimestamp();
            for (int r = 0; r < reps; r++)
            {
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    sink += p.positionShip.y + p.boundsSizeShip.x;
                }
            }
            long t1 = Stopwatch.GetTimestamp();
            checksum = sink;
            return (t1 - t0) * 1e6 / Stopwatch.Frequency;
        }

        private static double LoopUs_Span(List<SpacePart> parts, int reps, out double checksum)
        {
            double sink = 0.0;
            long t0 = Stopwatch.GetTimestamp();
            for (int r = 0; r < reps; r++)
            {
                var span = CollectionsMarshal.AsSpan(parts);
                for (int i = 0; i < span.Length; i++)
                {
                    var p = span[i];
                    sink += p.positionShip.y + p.boundsSizeShip.x;
                }
            }
            long t1 = Stopwatch.GetTimestamp();
            checksum = sink;
            return (t1 - t0) * 1e6 / Stopwatch.Frequency;
        }

        private static double Median(double[] values)
        {
            var copy = (double[])values.Clone();
            Array.Sort(copy);
            return copy[copy.Length / 2];
        }

        [Fact]
        public void PartAccess_InterfaceVsSpan_Interleaved()
        {
            var parts = BuildParts();
            IReadOnlyList<SpacePart> iface = parts;

            // 预热：两种路径各跑 20 遍整循环（JIT 分层编译在首轮会骗人）
            LoopUs_Iface(iface, 20, out _);
            LoopUs_Span(parts, 20, out _);

            var ifaceUs = new double[Samples];
            var spanUs = new double[Samples];
            double ifaceSum = 0.0, spanSum = 0.0;
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();

            for (int s = 0; s < Samples; s++)
            {
                if ((s & 1) == 0)
                {
                    ifaceUs[s] = LoopUs_Iface(iface, InnerReps, out double c1);
                    spanUs[s] = LoopUs_Span(parts, InnerReps, out double c2);
                    ifaceSum += c1;
                    spanSum += c2;
                }
                else
                {
                    spanUs[s] = LoopUs_Span(parts, InnerReps, out double c2);
                    ifaceUs[s] = LoopUs_Iface(iface, InnerReps, out double c1);
                    spanSum += c2;
                    ifaceSum += c1;
                }
            }

            long alloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

            double ifaceMed = Median(ifaceUs);
            double spanMed = Median(spanUs);
            double perPart = InnerReps * PartCount;
            double ifaceNs = ifaceMed * 1000.0 / perPart;
            double spanNs = spanMed * 1000.0 / perPart;
            double deltaPct = (spanNs - ifaceNs) / ifaceNs * 100.0;

            // 取值逐位相等：两种访问方式读的是同一批字段（不是"跑得快了因为少读了"）
            Assert.Equal(ifaceSum, spanSum, 9);

            _out.WriteLine(
                $"[§2-A 取件台架] 256 件 × {InnerReps} 遍 × {Samples} 采样（中位数）｜" +
                $"接口 {ifaceNs:F2} ns/件（{ifaceMed / InnerReps:F2} µs/遍）｜" +
                $"span {spanNs:F2} ns/件（{spanMed / InnerReps:F2} µs/遍）｜Δ {deltaPct:+0.0;-0.0;0.0}%｜" +
                $"分配 {alloc} B｜校验和 {ifaceSum:F1}（两侧逐位同）");
        }
    }
}
