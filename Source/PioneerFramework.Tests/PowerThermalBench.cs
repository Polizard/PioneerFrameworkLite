using System.Diagnostics;
using PioneerFramework.Components;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **§5 电/热规则的每群每步成本**（设计稿 §0-2：新增内容必须给同口径读数）。
    ///
    /// 口径：纯函数 `PowerThermalCore.Step`，一个"典型有电船"输入（反应堆 120 kW / 电池 5 MJ /
    /// 推进 50 kW + 武器 50 kW + 传感器 2 kW / 产热 300 kW = 散热 300 kW），预热 1000 次后取
    /// **1,000,000 次**的中位量级读数。这是**群级每步**的单价（不是每件、不是每帧）。
    /// 另量"无输入"快路径（老船走的就是它）。
    /// 只出读数，不判 PASS/FAIL（同 `CollisionContactBench` 例）。
    /// </summary>
    public class PowerThermalBench
    {
        private readonly ITestOutputHelper _out;
        public PowerThermalBench(ITestOutputHelper output) => _out = output;

        private static PowerThermalCore.Inputs Typical() => new()
        {
            genWatts = 120_000f,
            dischargeLimitWatts = 200_000f,
            chargeLimitWatts = 200_000f,
            capacityJoules = 5_000_000f,
            storedJoules = 1_000_000f,
            nominalPropulsion = 50_000f,
            nominalShield = 0f,
            nominalWeapon = 50_000f,
            nominalSensor = 2_000f,
            activityPropulsion = 1f,
            activityShield = 1f,
            activityWeapon = 1f,
            activitySensor = 1f,
            heatProduction = 300_000f,
            heatDissipation = 300_000f,
            dtSeconds = 2f,
        };

        private static double MeasureNs(PowerThermalCore.Inputs input, int reps, out float sink)
        {
            float s = 0f;
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < reps; i++)
                s += PowerThermalCore.Step(input).brownout;
            long t1 = Stopwatch.GetTimestamp();
            sink = s;
            return (t1 - t0) * 1e9 / Stopwatch.Frequency / reps;
        }

        [Fact]
        public void Step_TypicalAndEmptyGroup_Cost()
        {
            var typical = Typical();
            var empty = default(PowerThermalCore.Inputs);

            for (int w = 0; w < 1000; w++) { PowerThermalCore.Step(typical); PowerThermalCore.Step(empty); }

            double nsTypical = MeasureNs(typical, 1_000_000, out float sinkA);
            double nsEmpty = MeasureNs(empty, 1_000_000, out float sinkB);

            Assert.True(sinkA > 0f && sinkB > 0f);   // 防死代码消除（同时证明两条路都真的执行了）

            _out.WriteLine(
                $"[§5 电/热] 分配一步（群级）：典型有电船 {nsTypical:F1} ns/群；无输入快路径 {nsEmpty:F1} ns/群");
        }
    }
}
