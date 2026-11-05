using System;
using System.Diagnostics;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 立方体 SOI 判定测试（2026-11-04 用户裁定）。
    /// 钉住：盒边界、角上算内、任一轴出 R 算外、与盒判定逐点一致、成本。
    /// </summary>
    public class SoiCheckTests
    {
        private const double R = 132_521_195.3; // 赭砂 SOI（米）

        private readonly ITestOutputHelper output;

        public SoiCheckTests(ITestOutputHelper output) => this.output = output;

        [Fact]
        public void CubeBoundary_IsOneLayer_AndMatchesBoxExactly()
        {
            // 立方体：三轴都在 R 内 = 内（包括球外的角，如 0.9R/0.9R/0.9R）
            Assert.Equal(SoiCheck.Relation.Inside, SoiCheck.Classify(0.9 * R, 0.9 * R, 0.9 * R, R));
            Assert.Equal(SoiCheck.Relation.Inside, SoiCheck.Classify(0.0, 0.9 * R, 0.9 * R, R));
            Assert.Equal(SoiCheck.Relation.Inside, SoiCheck.Classify(0.6 * R, 0.6 * R, 0.0, R));
            Assert.Equal(SoiCheck.Relation.Inside, SoiCheck.Classify(0.8 * R, 0.8 * R, 0.0, R));
            // 任一轴 > R ⇒ 外
            Assert.Equal(SoiCheck.Relation.Outside, SoiCheck.Classify(1.0001 * R, 0.0, 0.0, R));
            Assert.Equal(SoiCheck.Relation.Outside, SoiCheck.Classify(0.0, -1.0001 * R, 0.0, R));
            // 边界算内
            Assert.Equal(SoiCheck.Relation.Inside, SoiCheck.Classify(R, 0.0, 0.0, R));
            Assert.Equal(SoiCheck.Relation.Inside, SoiCheck.Classify(-R, R, -R, R));

            // 随机 10 万点：Classify 与 max(|x|,|y|,|z|) ≤ R 逐个一致
            var rng = new Random(12345);
            int mismatch = 0;
            for (int i = 0; i < 100_000; i++)
            {
                double x = (rng.NextDouble() * 2.0 - 1.0) * 1.2 * R;
                double y = (rng.NextDouble() * 2.0 - 1.0) * 1.2 * R;
                double z = (rng.NextDouble() * 2.0 - 1.0) * 1.2 * R;
                bool exact = Math.Abs(x) <= R && Math.Abs(y) <= R && Math.Abs(z) <= R;
                bool got = SoiCheck.Classify(x, y, z, R) == SoiCheck.Relation.Inside;
                if (exact != got) mismatch++;
            }
            output.WriteLine($"random 100k mismatch = {mismatch}");
            Assert.Equal(0, mismatch);

            const int n = 10_000_000;
            double sink = 0.0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                double scale = (i % 100) * 0.01 * R;
                sink += SoiCheck.Classify(scale, 0.4 * R, 0.2 * R, R) == SoiCheck.Relation.Inside ? 1.0 : 0.0;
            }
            sw.Stop();
            double ns = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / n;
            output.WriteLine($"SoiCheck.Classify (cube): {ns:F2} ns/call（{n} 次，sink={sink:F0}）");
            Assert.True(ns < 100.0, $"SoiCheck {ns:F2} ns/call > 100 ns");
        }
    }
}
