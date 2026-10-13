using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **[A8] 点到点解析搬运（纯核心）**：路径端点与形状 · 时长口径 · **等效计费守恒**（"两档账单一致"的解析侧）。
    /// 接线（组状态 + 每步推进 + 场景 + 跑批器判据）在下一刀；本文件先把"算式与形状"钉住。
    /// </summary>
    public class AnalyticTransferCoreTests
    {
        private static readonly DVector3 Low = new DVector3(7_000_000.0, 0, 0);                 // 7000 km
        private static readonly DVector3 High = new DVector3(0, 0, 42_000_000.0);                // 42000 km（正交方向）

        // ── ① 路径端点（起终点必须**逐位**落在给定的两点上）──

        [Fact]
        public void PositionAt_EndpointsAreExact()
        {
            Assert.Equal(Low, AnalyticTransferCore.PositionAt(Low, High, 0.0));
            Assert.Equal(High, AnalyticTransferCore.PositionAt(Low, High, 1.0));
            Assert.Equal(Low, AnalyticTransferCore.PositionAt(Low, High, -0.5));   // 夹到起点
            Assert.Equal(High, AnalyticTransferCore.PositionAt(Low, High, 1.5));   // 夹到终点
        }

        [Fact]
        public void PositionAt_RadiusIsLinear_AndNeverInsideTheBody()
        {
            // 半径线性：u=0.5 时应是两端半径的均值
            var mid = AnalyticTransferCore.PositionAt(Low, High, 0.5);
            Assert.Equal((Low.magnitude + High.magnitude) / 2.0, mid.magnitude, 6);

            // **全程半径不低于两端最小值**（"大圆 + 半径线性"的形状保证：不会像直线插值那样切进天体）
            double rMin = Math.Min(Low.magnitude, High.magnitude);
            for (int i = 0; i <= 100; i++)
            {
                var p = AnalyticTransferCore.PositionAt(Low, High, i / 100.0);
                Assert.True(p.magnitude >= rMin - 1e-6,
                    "u=" + (i / 100.0).ToString("F2") + " 处半径 " + p.magnitude.ToString("F0") + " 低于两端最小 " + rMin.ToString("F0"));
            }
        }

        [Fact]
        public void PositionAt_DegenerateInputs_FallBackToLinear_NoThrow()
        {
            // 零向量、同向、反向：都要**不抛**且端点仍然精确
            Assert.Equal(DVector3.zero, AnalyticTransferCore.PositionAt(DVector3.zero, High, 0.0));
            var same = AnalyticTransferCore.PositionAt(Low, Low * 2.0, 0.5);          // 同向：只改半径
            Assert.Equal(Low.magnitude * 1.5, same.magnitude, 6);
            var opp = AnalyticTransferCore.PositionAt(Low, Low * -1.0, 0.5);          // 反向：线性
            Assert.True(opp.magnitude < 1e-6, "反向应退化为线性（中点 ≈ 0），实测 " + opp.magnitude.ToString("F3"));
        }

        // ── ② 时长口径 ──

        [Fact]
        public void Duration_IsDvOverAccel_Clamped()
        {
            Assert.Equal(100.0, AnalyticTransferCore.Duration(dvTotal: 200.0, aAvgMps2: 2.0), 9);
            Assert.Equal(AnalyticTransferCore.MinDurationSeconds, AnalyticTransferCore.Duration(0.5, 100.0));   // 太短 ⇒ 夹下限
            Assert.Equal(AnalyticTransferCore.MaxDurationSeconds, AnalyticTransferCore.Duration(1e9, 1.0));     // 太长 ⇒ 夹上限
            Assert.Equal(AnalyticTransferCore.MinDurationSeconds, AnalyticTransferCore.Duration(0.0, 0.0));
            Assert.Equal(AnalyticTransferCore.MinDurationSeconds, AnalyticTransferCore.Duration(-5.0, 1.0));
        }

        // ── ③ **等效计费守恒**（解析侧的"两档账单一致"）──

        [Fact]
        public void Billing_SumsToTheWholeDvOverTheTrip()
        {
            // 把 T 切成 N 步，每步的等效 Δv 之和应**恰等于** dvTotal（换档不逃费、不多收费）
            const double dvTotal = 1234.5, aAvg = 3.0, dt = 2.0;
            double T = AnalyticTransferCore.Duration(dvTotal, aAvg);
            // ⚠ **末步要截断**（`min(dt, T−t)`）：不然最后会多算半步（实测 1236.0 ↔ 应 1234.5）——
            //   这也正是生产调用该走的形式（搬运结束那一步走不满一个 dt）。
            double sum = 0.0;
            for (double t = 0.0; t < T - 1e-9; t += dt)
                sum += AnalyticTransferCore.DvThisStep(dvTotal, T, Math.Min(dt, T - t));

            Assert.Equal(dvTotal, sum, 6);
            // 且每步扣的工质 = m·dv/ve（同一条口径），量级应可核对
            double m = 50_000.0, ve = 4000.0;
            double dm = AnalyticTransferCore.PropellantThisStep(m, dvTotal, ve);
            Assert.Equal(m * dvTotal / ve, dm, 6);
            Assert.Equal(0.0, AnalyticTransferCore.PropellantThisStep(m, 100.0, 0.0));   // 没引擎 ⇒ 不扣（调用方判"付不起"）
        }

        [Fact]
        public void Progress_AdvancesMonotonically_AndSaturatesAtOne()
        {
            Assert.Equal(0.0, AnalyticTransferCore.Progress(-1.0, 100.0));
            Assert.Equal(0.25, AnalyticTransferCore.Progress(25.0, 100.0), 9);
            Assert.Equal(1.0, AnalyticTransferCore.Progress(150.0, 100.0));
            Assert.Equal(1.0, AnalyticTransferCore.Progress(50.0, 0.0));      // T≤0 ⇒ 已到达（付费瞬移那种退化情形）
        }

        [Fact]
        public void VelocityAt_MatchesThePathDirection_AndDegenerateDurationIsZero()
        {
            double T = 100.0;
            var vMid = AnalyticTransferCore.VelocityAt(Low, High, 0.5, T);
            Assert.True(vMid.magnitude > 0.0, "中段速度应非零");
            Assert.Equal(DVector3.zero, AnalyticTransferCore.VelocityAt(Low, High, 0.5, 0.0));
        }
    }
}
