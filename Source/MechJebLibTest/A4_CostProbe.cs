using System;
using System.Diagnostics;
using System.IO;
using MechJebLib.Primitives;
using PioneerFramework.Pure.Tests;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **成本探针**（2026-09-20）：把"阶梯一次调用到底花在哪"量到毫秒级。
    /// ⚠ 关键手法：**进度写文件**（不是测试日志）—— 进程被杀时文件里仍留着最后一行，
    ///   这样"跑不完"本身也是**可读的数据**（死在哪个子步骤）。
    /// </summary>
    public class A4_CostProbe
    {
        private static readonly string Log = Path.Combine(Path.GetTempPath(), "pfl_a4_cost.txt");
        private static void W(string s) { File.AppendAllText(Log, s + "\n"); }

        private const double Mu = 2.0e14, R0 = 7_000_000.0, BodyR = 5.0e6;
        private static double MsDegenerate = 0.0, MsPlan = 0.0;

        [Fact]
        public void Probe_WhereIsTheTime()
        {
            File.WriteAllText(Log, "== A4 成本探针 " + DateTime.Now.ToString("HH:mm:ss") + " ==\n");
            double vc = Math.Sqrt(Mu / R0);
            var self = OrbitView.FromStateVectors(new V3(R0, 0, 0), new V3(0, 0, -vc), Mu, 0.0, OrbitView.ToV3(DVector3.up), BodyR);

            // ⚠ 直接扫"霍曼求解"这一步的**成本 vs Δa**（Δa=0 就是同轨 ⇒ 退化）
            //   每档写一行文件 ⇒ 进程被杀也知道死在哪一档。硬墙钟 25 s/档，超了就跳过后续档。
            double[] daKm = { 0.0, 1.0, 10.0, 100.0, 500.0, 1100.0 };
            foreach (double da in daKm)
            {
                var tgt = CircularTarget.Create(R0 + da * 1000.0, Mu, 90.0 * Math.PI / 180.0, DVector3.up);
                var tgtOv = OrbitView.FromStateVectors(OrbitView.ToV3(tgt.PosAt(0.0)), OrbitView.ToV3(tgt.VelAt(0.0)),
                    Mu, 0.0, OrbitView.ToV3(DVector3.up), BodyR);
                var sw = Stopwatch.StartNew();
                W(string.Format("… Δa={0:F0} km：霍曼求解开始", da));
                string res;
                try
                {
                    var (dv1, ut1, dv2, ut2) = OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(self, tgtOv, 0.0);
                    res = string.Format("dv1={0:F3} dv2={1:F3} ut1={2:F1}", dv1.magnitude, dv2.magnitude, ut1);
                }
                catch (Exception ex) { res = "抛 " + ex.GetType().Name; }
                sw.Stop();
                if (da <= 10.0) MsDegenerate = Math.Max(MsDegenerate, sw.Elapsed.TotalMilliseconds);
                W(string.Format("完成：Δa={0:F0} km ⇒ {1:F1} ms｜{2}", da, sw.Elapsed.TotalMilliseconds, res));
            }

            // 阶梯整体一次调用（同轨）：修好之后应当快
            var same = CircularTarget.Create(R0, Mu, 90.0 * Math.PI / 180.0, DVector3.up);
            var sameOv = OrbitView.FromStateVectors(OrbitView.ToV3(same.PosAt(0.0)), OrbitView.ToV3(same.VelAt(0.0)),
                Mu, 0.0, OrbitView.ToV3(DVector3.up), BodyR);
            MsPlan = 0.0;
            Probe("② 同轨目标的 MakePlan（整体）", () =>
            {
                var r = RendezvousLadder.MakePlan(self, sameOv, 0.0,
                    new RendezvousLadder.Params { DesiredDistance = 300.0, MinRadius = 5.1e6 }, out var n, out var st);
                W("    ⇒ 选中 " + r + "（节点 " + n.Count + " 条）｜" + st);
            });

            W("== 完 ==");
            // ★ 回归判据（这次血泪换来的）：**退化几何必须立刻返回**（曾经是"永不返回"）。
            //   同时钉住一次完整 `MakePlan` 的量级（游戏里每次计划只调一次，帧预算 16 ms）。
            Assert.True(MsDegenerate < 100.0, $"退化几何的霍曼求解 {MsDegenerate:F1} ms 太慢（应为亚毫秒级）");
            Assert.True(MsPlan < 200.0, $"整体 MakePlan {MsPlan:F1} ms 太慢（一次计划一次调用）");
        }

        private static void Probe(string name, Action body)
        {
            var sw = Stopwatch.StartNew();
            W("… 开始：" + name);
            try { body(); }
            catch (Exception ex) { W("    !! 抛异常：" + ex.GetType().Name + " " + ex.Message); }
            sw.Stop();
            if (name.StartsWith("②")) MsPlan = sw.Elapsed.TotalMilliseconds;
            W(string.Format("完成：{0} ⇒ {1:F1} ms", name, sw.Elapsed.TotalMilliseconds));
        }
    }
}
