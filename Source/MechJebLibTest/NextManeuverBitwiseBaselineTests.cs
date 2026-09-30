using System;
using System.Text;
using MechJebLib.Maneuvers;
using MechJebLib.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **`NextManeuver`（冷启动）的逐位基线**（2026-09-30 建）——`8 个 double` 全比位。
    ///
    /// ## 它从哪来、为什么留着
    /// 起因是语句级优化 **A1**（`LambertFunction` 里那条 `Shepperd(r1,dt)` 加一格记忆）的差分判据：
    /// 先在改动**之前**采基线，改完必须一字不差。**A1 后来被用户裁定回滚**（实测只有 1.05× 墙钟，
    /// 不值得多背一个类 + 两个计数 + 一个开关），但**这份基线留下** —— 因为它挡的是另一类事：
    ///
    /// ## 为什么必须是**逐位**，而不是"Δv 对不对"
    /// 上游那批用例（`TwoImpulseTransferTests`）断言的是**性质**（Δv 对上解析霍曼、交会精确到容差）。
    /// 而 warm start 那次实测证明：**窗口可以被改掉而 Δv 逐位不变**（同一几何每个窗口的 Δv 相同）
    /// ⇒ 只对 Δv 的判据**天生看不见换窗口**。本文件按 **全部 8 个 double 的位**立基线
    /// （`dv1.xyz` + `dt1` + `dv2.xyz` + `dt2`），换窗口立刻红 ⇒ **将来谁要动这条搜索路（含"窗口政策显式化"），
    /// 先让它跑一遍**：红了就说明计划被改了，必须走"决策改动"的流程，而不是当性能优化处理。
    ///
    /// 几何档：三个真实 μ · 近共线 · 共面（`coplanar:true` 会走旋转那条路）· 只拦截不捕获 · 大等待 · 异面 15°。
    ///
    /// ⚠ **基线是本机采集的**（`System.Math` 的超越函数走运行库/CRT ⇒ 换机器/换 runtime 可能要重采；
    ///   四则运算与 `Sqrt` 是 IEEE 精确的，跨机器一致）。重采方式：把 `Golden` 置成 `""` 跑一次，读打印值填回。
    /// </summary>
    public class NextManeuverBitwiseBaselineTests
    {
        private readonly ITestOutputHelper _out;
        public NextManeuverBitwiseBaselineTests(ITestOutputHelper output) => _out = output;

        /// <summary>**逐位基线**（每格 = `dv1.xyz/dt1/dv2.xyz/dt2` 的 8 个 `DoubleToInt64Bits`，十六进制）。
        /// 采集于 2026-09-30（A1 之前；A1 期间验过一字不差，回滚后同值）。
        /// ⚠ 故意用 `static readonly`（不是 `const`）：采集模式那句比较要留成**运行期**判断，
        ///   否则编译器把 `""` 分支折叠掉 ⇒ CS0162 不可达代码。</summary>
        private static readonly string Golden =
            "c042ee7af573d7ea8000000000000000c0666bae41e4ad3f40e438c3a41bebf2404247ec676ba62600000000000000004065a67b148ee80340e675f667d1926bc04eb72e38d14a540000000000000000406596fd2d882b9a40e1dc7fbb4b3cac404da90fcc828aeb0000000000000000c064d9173611c83640e419b27f00e3264081c2c9fefdc298800000000000000040613364e92ae09140cb8440e16ab836c07f8ef975a3c7c60000000000000000c05e9026883cff7240d3b6a9042d1763c06805b5101a46558000000000000000c081424fc19dfad940c0ed6d0b360c294065578192961f800000000000000000407eaab2520d150740ccd67ea47c8dc7c019685552b0882f000000000000000040449aa81dda22ae40ee5385d55f5ea44018998e3dd7fb580000000000000000c043f2e7a63478f140f1090a22eac58ec083327cdd0d84790000000000000000409afcbc7a103bb340ac93ff9211fa8a408289a9daef779b0000000000000000c09a0f5d04748b8040b0148ecc00b5a6c04eb72e38d14a540000000000000000406596fd2d882b9a40e1dc7fbb4b3cac404da90fcc828aeb0000000000000000c064d9173611c83640e419b27f00e326c04eb730bb2cce6f0000000000000000406596fcf46a13fc40e1dc7fbb4b3cac404da90921e32d020000000000000000c064d917cdd2105d40e419b269822fd040502657d7b29a12800000000000000040140891d359ab7440f996a0e6b19749c04fe8799bb270d48000000000000000c013c6224113681e40faa1760bec48d0c04ab02b4f14fad0c0740658ab46e5bf406565503c4cb01a40e1f698e010031b4056dc586f30458f408fd53df8cac0c0c0305f61e8fe833940e469185ab61650";

        private static readonly (string name, double mu, double r1, double r2, double phase, bool coplanar, bool rendezvous, bool capture)[]
            Cases =
            {
                ("地球 7000→8050 φ=0", 2.0e14, 7.0e6, 8.05e6, 0.0, false, true, true),
                ("地球 7000→8050 φ=40", 2.0e14, 7.0e6, 8.05e6, 40.0, false, true, true),
                ("地球 7000→11200 φ=0", 2.0e14, 7.0e6, 1.12e7, 0.0, false, true, true),
                ("地球 7000→11200 φ=120", 2.0e14, 7.0e6, 1.12e7, 120.0, false, true, true),
                ("月球 2900→3300 φ=60", 5.0e12, 2.9e6, 3.3e6, 60.0, false, true, true),
                ("太阳 7000→8050 φ=40", 2.0e16, 7.0e6, 8.05e6, 40.0, false, true, true),
                ("地球 7000→8050 φ=40（共面支）", 2.0e14, 7.0e6, 8.05e6, 40.0, true, true, true),
                ("地球 7000→8050 φ=40（只拦截）", 2.0e14, 7.0e6, 8.05e6, 40.0, false, false, false),
                ("地球 7000→7350 φ=30（大等待）", 2.0e14, 7.0e6, 7.35e6, 30.0, false, true, true),
                ("地球 7000→8050 φ=40（异面 15°）", 2.0e14, 7.0e6, 8.05e6, 40.0, false, true, true),
            };

        private static (V3 r, V3 v) Circular(double mu, double radius, double phaseDeg, double incDeg = 0.0)
        {
            double vc = Math.Sqrt(mu / radius);
            double ph = phaseDeg * Math.PI / 180.0;
            double i = incDeg * Math.PI / 180.0;
            var r = new V3(radius * Math.Cos(ph), 0, radius * Math.Sin(ph));
            // 切向 = ĥ × r̂；倾角 = 绕 +X 轴转（异面档用）
            var t = new V3(Math.Sin(ph), 0, -Math.Cos(ph));
            if (incDeg != 0.0)
            {
                double cy = Math.Cos(i), sy = Math.Sin(i);
                r = new V3(r.x, r.y * cy - r.z * sy, r.y * sy + r.z * cy);
                t = new V3(t.x, t.y * cy - t.z * sy, t.y * sy + t.z * cy);
            }
            return (r, t * vc);
        }

        [Fact]
        public void NextManeuver_EightDoubles_AreBitIdenticalToBaseline()
        {
            var sb = new StringBuilder();
            foreach (var c in Cases)
            {
                (V3 r1, V3 v1) = Circular(c.mu, c.r1, 0.0);
                (V3 r2, V3 v2) = Circular(c.mu, c.r2, c.phase, c.name.Contains("异面") ? 15.0 : 0.0);
                var s = TwoImpulseTransfer.NextManeuver(c.mu, r1, v1, r2, v2, coplanar: c.coplanar,
                    rendezvous: c.rendezvous, capture: c.capture);
                sb.Append(Bits(s.dv1.x)).Append(Bits(s.dv1.y)).Append(Bits(s.dv1.z)).Append(Bits(s.dt1))
                  .Append(Bits(s.dv2.x)).Append(Bits(s.dv2.y)).Append(Bits(s.dv2.z)).Append(Bits(s.dt2));
            }

            string got = sb.ToString();
            if (Golden.Length == 0)
            {
                _out.WriteLine("（采集模式：把下面这串填回 `Golden`）");
                _out.WriteLine(got);
                return;   // 采集模式不断言
            }

            Assert.True(Golden == got,
                "`NextManeuver` 的 8 个 double 与**逐位基线**不同 ⇒ 计划被改了" +
                "（哪怕 Δv 一样、只是换了窗口也不行）⇒ 这属于**决策改动**，要走那条流程，不能当性能优化收下。\n" +
                $"基线长度 {Golden.Length} vs 实得 {got.Length}\n基线：{Golden}\n实得：{got}");
        }

        /// <summary>`DoubleToInt64Bits`（小写十六进制，固定宽度 ⇒ 可拼接比较）。</summary>
        private static string Bits(double x) => BitConverter.DoubleToInt64Bits(x).ToString("x16");
    }
}
