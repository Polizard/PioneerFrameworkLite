using System;
using System.Collections.Generic;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **爬升台架的型线扫描**（[A6+] 2026-10-06）：在纯台架上找一组能**带着气动**到达 5500 km 的
    /// （俯仰程序 + 燃料）——不给用户加游戏内轮次。
    ///
    /// 背景（本轮读数）：现行 `CreateDefault`（vAscent 500 / 20→450 km / vRadMax 300）连**真空**都到不了
    /// （台架：顶点 375 km、余料 0）；历史"50 t 余 2.9 t"是 09-02 的读数，**俯仰程序 09-09 才落库** ⇒ 已过期。
    /// </summary>
    public class AscentTuningTests
    {
        private readonly ITestOutputHelper _out;

        public AscentTuningTests(ITestOutputHelper o) { _out = o; }

        /// <summary>跑一组参数（顺气流 + 气动），返回一行读数。</summary>
        private static string RunCase(string tag, double vAscent, double pitchStartKm, double pitchEndKm,
            double vRadMax, double tau, double fuelKg, bool drag = true, bool localCircular = false,
            double latitudeDeg = 51.7, double spawnAltKm = 10.0, bool inPlaneTangent = false, bool delay = false,
            double radialGain = 1.0, double tanGain = 1.0, bool gravityLead = false, int delaySteps = 1,
            double dtSec = 2.0, bool predictState = false, bool predictPos = false)
        {
            var s = AscentFlightSim.Setup.TestRocket();
            s.drag = drag;
            s.fuelMass = fuelKg;
            s.latitudeRad = latitudeDeg * Math.PI / 180.0;
            s.spawnAltitude = spawnAltKm * 1000.0;
            s.dt = dtSec;
            s.predictState = predictState;
            s.predictPos = predictPos;
            s.delaySteps = delay ? delaySteps : 0;
            s.guidanceOverride = new AscentFlightSim.Setup.GuidanceKnobs
            {
                vAscent = vAscent,
                altPitchStart = pitchStartKm * 1000.0,
                altPitchEnd = pitchEndKm * 1000.0,
                vRadMax = vRadMax,
                tau = tau,
                localCircularTarget = localCircular,
                inPlaneTangent = inPlaneTangent,
                radialGain = radialGain,
                tanGain = tanGain,
                gravityLead = gravityLead,
            };
            var r = AscentFlightSim.Run(s);
            string p80 = string.Format(" [80km: t={0:F0}s v={1:F0} 燃={2:F0}kg]", r.tAt80km, r.vAt80km, r.fuelAt80km);
            return r.arrived
                ? string.Format("{0}: **到达** t={1:F0}s 余料={2:F0}kg finalR={3:F0}km finalV={4:F0} |dv|={5:F0} drag={6:F0} grav={7:F0}{8}",
                    tag, r.timeSeconds, r.fuelLeft, r.finalR / 1000.0, r.finalV, r.dvSpent, r.dragLoss, r.gravityLoss, p80)
                : string.Format("{0}: 未到 耗尽@t={1:F0}s alt={2:F0}km v={3:F0} vRad={4:F0} apo={5:F0}km |dv|={6:F0} drag={7:F0} grav={8:F0}",
                    tag, r.burnoutTime, r.burnoutAltKm, r.burnoutV, r.burnoutVRad,
                    r.burnoutApoKm, r.burnoutDvSpent, r.burnoutDragLoss, r.burnoutGravityLoss);
        }

        [Fact]
        public void Sweep_Report()
        {
            var lines = new List<string>
            {
                RunCase("基准 51.7°/10km 真空", 500, 20, 450, 300, 600, 50000, drag: false),
                RunCase("矩阵 0°/0km 真空(同既有纯测试)", 500, 20, 450, 300, 600, 50000, drag: false, latitudeDeg: 0, spawnAltKm: 0),
                RunCase("矩阵 0°/10km 真空", 500, 20, 450, 300, 600, 50000, drag: false, latitudeDeg: 0),
                RunCase("矩阵 51.7°/0km 真空", 500, 20, 450, 300, 600, 50000, drag: false),
                RunCase("矩阵 0°/0km 带气动", 500, 20, 450, 300, 600, 50000, latitudeDeg: 0, spawnAltKm: 0),
                RunCase("矩阵 51.7°/10km 带气动", 500, 20, 450, 300, 600, 50000),
                RunCase("A vAscent250/End250/vRad150", 250, 20, 250, 150, 600, 50000),
                RunCase("B vAscent150/End150/vRad120", 150, 15, 150, 120, 600, 50000),
                RunCase("C vAscent200/End200/vRad120/tau400", 200, 15, 200, 120, 400, 50000),
                RunCase("D vAscent120/End120/vRad100", 120, 15, 120, 100, 600, 50000),
                RunCase("E 同D + 燃料60t", 120, 15, 120, 100, 600, 60000),
                RunCase("F 同C + 燃料60t", 200, 15, 200, 120, 400, 60000),
                RunCase("G 同A 真空对照", 250, 20, 250, 150, 600, 50000, drag: false),
                RunCase("H 局部圆轨/默认其余", 500, 20, 150, 120, 600, 50000, localCircular: true),
                RunCase("I 局部圆轨/缓爬", 250, 15, 150, 80, 400, 50000, localCircular: true),
                RunCase("J 局部圆轨/缓爬/燃料60t", 250, 15, 150, 80, 400, 60000, localCircular: true),
                RunCase("K 现型线+燃料55t", 500, 20, 450, 300, 600, 55000),
                RunCase("L 现型线+燃料60t", 500, 20, 450, 300, 600, 60000),
                RunCase("M 现型线+燃料60t+vRadMax250", 500, 20, 450, 250, 600, 60000),
                RunCase("N 现型线+燃料60t+vRadMax200/pitchEnd350", 500, 20, 350, 200, 600, 60000),
                RunCase("O 现型线+燃料65t+vRadMax200", 500, 20, 400, 200, 600, 65000),
                RunCase("P 面内切向 51.7°/10km 真空", 500, 20, 450, 300, 600, 50000, drag: false, inPlaneTangent: true),
                RunCase("Q 面内切向 51.7°/10km 带气动", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true),
                RunCase("R 面内切向 高纬70° 带气动", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, latitudeDeg: 70),
                RunCase("S 面内切向 赤道 带气动", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, latitudeDeg: 0, spawnAltKm: 0),
                RunCase("T 面内切向 51.7° 带气动 燃料52t", 500, 20, 450, 300, 600, 52000, inPlaneTangent: true),
                RunCase("U 面内切向 51.7° 带气动 燃料55t", 500, 20, 450, 300, 600, 55000, inPlaneTangent: true),
                RunCase("V 面内切向 51.7° 带气动 燃料58t", 500, 20, 450, 300, 600, 58000, inPlaneTangent: true),
                RunCase("W 面内+一拍延迟 51.7° 气动", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true),
                RunCase("X 面内+一拍延迟+tau2000", 500, 20, 450, 300, 2000, 50000, inPlaneTangent: true, delay: true),
                RunCase("Y 面内+一拍延迟+vRadMax120", 500, 20, 450, 120, 600, 50000, inPlaneTangent: true, delay: true),
                RunCase("Z 面内+一拍延迟+tau2000/vRadMax120", 500, 20, 450, 120, 2000, 50000, inPlaneTangent: true, delay: true),
                RunCase("延迟·径向增益0.5", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, radialGain: 0.5),
                RunCase("延迟·径向增益0.3", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, radialGain: 0.3),
                RunCase("延迟·径向增益0.3+切向0.5", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, radialGain: 0.3, tanGain: 0.5),
                RunCase("延迟·重力前馈(全增益)", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, gravityLead: true),
                RunCase("延迟·重力前馈+径向增益0.5", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, gravityLead: true, radialGain: 0.5),
                RunCase("延迟2拍", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 2),
                RunCase("延迟3拍", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 3),
                RunCase("延迟2拍+径向0.5+切向0.5", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 2, radialGain: 0.5, tanGain: 0.5),
                RunCase("dt=6s 延迟1拍 全增益", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, dtSec: 6),
                RunCase("dt=6s 延迟1拍 增益0.5/0.5", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, dtSec: 6, radialGain: 0.5, tanGain: 0.5),
                RunCase("dt=2s 增益0.3/0.5 无延迟", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, radialGain: 0.3, tanGain: 0.5),
                RunCase("dt=6s 无延迟 全增益", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, dtSec: 6),
                RunCase("延迟2拍+径向0.3+切向0.5", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 2, radialGain: 0.3, tanGain: 0.5),
                RunCase("延迟2拍+径向0.3+切向0.5+前馈", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 2, radialGain: 0.3, tanGain: 0.5, gravityLead: true),
                RunCase("延迟1拍+径向0.3+切向0.5+前馈", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, radialGain: 0.3, tanGain: 0.5, gravityLead: true),
                RunCase("延3拍 0.3/0.5", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 3, radialGain: 0.3, tanGain: 0.5),
                RunCase("延3拍 0.2/0.3", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 3, radialGain: 0.2, tanGain: 0.3),
                RunCase("延3拍 0.1/0.2", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 3, radialGain: 0.1, tanGain: 0.2),
                RunCase("延4拍 0.2/0.3", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 4, radialGain: 0.2, tanGain: 0.3),
                RunCase("延4拍 0.1/0.2", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 4, radialGain: 0.1, tanGain: 0.2),
                RunCase("延4拍 0.05/0.1", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 4, radialGain: 0.05, tanGain: 0.1),
                RunCase("dt2 延1 无预测", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 1),
                RunCase("dt2 延1 **预测**", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 1, predictState: true),
                RunCase("dt8 延1 无预测", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 1, dtSec: 8),
                RunCase("dt8 延1 **预测**", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 1, dtSec: 8, predictState: true),
                RunCase("dt16 延1 无预测", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 1, dtSec: 16),
                RunCase("dt16 延1 速度+位置预测", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 1, dtSec: 16, predictState: true, predictPos: true),
                RunCase("dt16 延1 仅速度预测", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 1, dtSec: 16, predictState: true),
                RunCase("dt8 延1 仅速度预测", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 1, dtSec: 8, predictState: true),
                RunCase("dt16 延1 仅速度预测+增益1/1", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 1, dtSec: 16, predictState: true, radialGain: 1.0, tanGain: 1.0),
                RunCase("dt8 延1 无预测+增益0.3/0.5", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 1, dtSec: 8, radialGain: 0.3, tanGain: 0.5),
                RunCase("dt8 延1 预测+增益1/1", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 1, dtSec: 8, predictState: true, radialGain: 1.0, tanGain: 1.0),
                RunCase("dt2 延1 预测+增益0.3/0.5", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 1, predictState: true, radialGain: 0.3, tanGain: 0.5),
                RunCase("dt2 延1 预测+增益1/1", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 1, predictState: true, radialGain: 1.0, tanGain: 1.0),
                RunCase("dt2 延2 预测+增益1/1", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 2, predictState: true, radialGain: 1.0, tanGain: 1.0),
                RunCase("dt8 延2 预测+增益1/1", 500, 20, 450, 300, 600, 50000, inPlaneTangent: true, delay: true, delaySteps: 2, dtSec: 8, predictState: true, radialGain: 1.0, tanGain: 1.0),
            };
            string all = string.Join("\n", lines);
            _out.WriteLine(all);

            // 本用例是**扫描位**（出数用，不设通过判据——判据等修法定下来冻进 `AscentBenchTests`）；
            // 但仍钉一条防空转：每组都必须真的跑出数（否则"没跑"会冒充"跑过"）。
            foreach (var line in lines)
                Assert.True(line.Contains("到达") || line.Contains("耗尽@t="), "该组没跑出结果：" + line);
        }
    }
}
