// ============================================================================
//  方格连通性实验室 —— 驱动：正确性对拍 + 性能基准
//
//  跑法：  dotnet run --project Source/TopologyLab -c Release
//          dotnet run --project Source/TopologyLab -c Release -- --quick
//
//  正确性：
//    ① 边界用例：空图 / 满图 / 1×1 / 对角相碰 / 空心方框（有洞）/ 梳齿
//    ② 随机对拍：随机尺寸 × 两种连通性配对 × 随机增删（含批量），
//       每步都与暴力全量比 计数 / 恒等式 w/4 = c − h / 每格分区
//    ③ 批量与逐格必须给出完全相同的终态
//  性能：
//    全量重算 vs 增量（带 O(1) 检测）vs 同算法但关掉检测（= 脏标记局部 BFS）
//    vs 批量（一批只做一轮修复）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TopologyLab
{
    public static class Program
    {
        private static int _failures;
        private static int _maxBatch = 30;   // 批量等价测试的最大批量（可用 args 覆盖）

        public static int Main(string[] args)
        {
            bool quick = Array.IndexOf(args, "--quick") >= 0;
            int bi = Array.IndexOf(args, "batchmax");
            if (bi >= 0 && bi + 1 < args.Length) _maxBatch = int.Parse(args[bi + 1]);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            if (Array.IndexOf(args, "dumpbox") >= 0) { DumpBox(); return 0; }
            bool benchOnly = Array.IndexOf(args, "bench") >= 0;
            Console.WriteLine("=== 方格连通性实验室 ===");
            Console.WriteLine();

            try
            {
                if (!benchOnly)
                {
                    RunEdgeCases();
                    RunRandomDifferential(quick);
                    RunBatchEquivalence(quick);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ 未捕获异常：{ex.GetType().FullName}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                _failures++;
            }

            Console.WriteLine();
            if (_failures == 0) Console.WriteLine("【正确性】全部通过 ✓");
            else Console.WriteLine($"【正确性】{_failures} 项失败 ✗");

            try
            {
                RunBenchmarks(quick);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ 基准段未捕获异常：{ex.GetType().FullName}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                _failures++;
            }

            return _failures == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------
        //  ① 边界用例
        // ------------------------------------------------------------------
        private static void RunEdgeCases()
        {
            Console.WriteLine("--- 边界用例 ---");

            // 空图：0 块 0 洞
            Check("空图", Build(4, 4), Conn.Fg4Bg8, expectComps: 0, expectHoles: 0);

            // 满图：1 块 0 洞
            var full = new IncTopology(4, 4, Conn.Fg4Bg8);
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) full.Add(x, y);
            CheckState("满图", full, expectComps: 1, expectHoles: 0);

            // 1×1 单格
            var one = new IncTopology(1, 1, Conn.Fg4Bg8);
            one.Add(0, 0);
            CheckState("单格", one, expectComps: 1, expectHoles: 0);

            // 对角相碰：两种配对给不同答案（这正是 §14.1 说的开关）
            var diag4 = Build(3, 3, Conn.Fg4Bg8);
            diag4.Add(0, 0); diag4.Add(1, 1);
            CheckState("对角相碰（前景 4 邻域 ⇒ 算 2 块）", diag4, expectComps: 2, expectHoles: 0);

            var diag8 = Build(3, 3, Conn.Fg8Bg4);
            diag8.Add(0, 0); diag8.Add(1, 1);
            CheckState("对角相碰（前景 8 邻域 ⇒ 算 1 块）", diag8, expectComps: 1, expectHoles: 0);

            // 空心方框：1 块 1 洞
            var ring = Build(5, 5, Conn.Fg4Bg8);
            for (int x = 0; x < 5; x++) { ring.Add(x, 0); ring.Add(x, 4); }
            for (int y = 1; y < 4; y++) { ring.Add(0, y); ring.Add(4, y); }
            CheckState("空心方框", ring, expectComps: 1, expectHoles: 1);

            // 拿掉方框一角 ⇒ 洞破，仍 1 块 0 洞
            ring.Remove(0, 0);
            CheckState("方框破角", ring, expectComps: 1, expectHoles: 0);
            ring.Add(0, 0);
            CheckState("方框补角", ring, expectComps: 1, expectHoles: 1);

            // 拿掉一边中点 ⇒ 断成两块（经典分裂）
            var bar = Build(7, 3, Conn.Fg4Bg8);
            for (int x = 0; x < 7; x++) bar.Add(x, 1);
            CheckState("一字长条", bar, expectComps: 1, expectHoles: 0);
            bar.Remove(3, 1);
            CheckState("一字长条断成两截", bar, expectComps: 2, expectHoles: 0);
            bar.Add(3, 1);
            CheckState("一字长条接回", bar, expectComps: 1, expectHoles: 0);

            // 细环：拿掉一格 ⇒ 断成一条长链（两块修复里最贵的一种，两端都是大的）
            var loop = Build(10, 10, Conn.Fg4Bg8);
            for (int x = 0; x < 10; x++) { loop.Add(x, 0); loop.Add(x, 9); }
            for (int y = 1; y < 9; y++) { loop.Add(0, y); loop.Add(9, y); }
            CheckState("细环", loop, expectComps: 1, expectHoles: 1);
            loop.Remove(5, 0);
            CheckState("细环断口", loop, expectComps: 1, expectHoles: 0);

            // 岛中有岛：洞里的岛 ⇒ 2 块 1 洞
            var island = Build(9, 9, Conn.Fg4Bg8);
            for (int x = 0; x < 9; x++) { island.Add(x, 0); island.Add(x, 8); }
            for (int y = 1; y < 8; y++) { island.Add(0, y); island.Add(8, y); }
            island.Add(4, 4);
            CheckState("洞中有岛", island, expectComps: 2, expectHoles: 1);
            Console.WriteLine();
        }

        // ------------------------------------------------------------------
        //  ② 随机对拍
        // ------------------------------------------------------------------
        private static void RunRandomDifferential(bool quick)
        {
            Console.WriteLine("--- 随机对拍（每步与暴力全量比） ---");
            Console.Out.Flush();
            int[] sizes = { 3, 5, 8, 13, 21, 34 };
            int roundsPerCase = quick ? 3 : 12;
            int opsPerRound = quick ? 200 : 1500;
            int cases = 0;

            foreach (int w in sizes)
            {
                foreach (int h in sizes)
                {
                    foreach (Conn conn in new[] { Conn.Fg4Bg8, Conn.Fg8Bg4 })
                    {
                        for (int round = 0; round < roundsPerCase; round++)
                        {
                            int seed = (w * 7919) ^ (h * 104729) ^ ((int)conn * 15485863) ^ (round * 32452843);
                            var rng = new Random(seed);
                            var topo = new IncTopology(w, h, conn) { GuardFind = true };
                            var log = new List<string>();
                            string err = "";

                            for (int op = 0; op < opsPerRound && err == ""; op++)
                            {
                                int x = rng.Next(w), y = rng.Next(h);
                                if (log.Count > 12) log.RemoveAt(0);
                                bool add = rng.Next(100) < 52 - topo.ComponentCount * 2; // 大约保持在"半满"
                                try
                                {
                                    log.Add($"{(add ? "+" : "-")}{x},{y}");
                                    if (add) topo.Add(x, y); else topo.Remove(x, y);
                                    err = topo.Verify(topo.ClonePresent(), topo.Size, topo.HSize);
                                }
                                catch (Exception ex)
                                {
                                    var bruteNow = GridBrute.Analyze(topo.ClonePresent(), topo.Size, topo.HSize, conn);
                                    err = $"{ex.Message} ｜ 事后暴力：{bruteNow.Comps} 块 {bruteNow.Holes} 洞，" +
                                          $"增量：{topo.ComponentCount} 块 {topo.HoleCount} 洞，绕数 {topo.CornerSum}（w/4={topo.CornerSum / 4}）" +
                                          $" ｜ 历史：{string.Join(" ", log)}";
                                }
                                if (err != "")
                                {
                                    err = $"seed={seed} {w}×{h} {conn} 第 {op} 步（{(add ? "放" : "拿")}({x},{y})）：{err}";
                                }
                            }
                            cases++;
                            if (err != "") Fail(err);
                        }
                    }
                }
            }
            Console.WriteLine($"  跑完 {cases} 例随机对拍，每例最多 {opsPerRound} 步。");
            Console.WriteLine();
        }

        // ------------------------------------------------------------------
        //  ③ 批量 ≡ 逐格
        // ------------------------------------------------------------------
        private static void RunBatchEquivalence(bool quick)
        {
            Console.WriteLine("--- 批量与逐格必须等价 ---");
            int rounds = quick ? 20 : 200;
            for (int r = 0; r < rounds; r++)
            {
                var rng = new Random(1000 + r);
                int w = 8 + rng.Next(40), h = 8 + rng.Next(40);
                var conn = rng.Next(2) == 0 ? Conn.Fg4Bg8 : Conn.Fg8Bg4;

                var a = new IncTopology(w, h, conn) { GuardFind = true }; // 逐格
                var b = new IncTopology(w, h, conn) { GuardFind = true }; // 批量
                string err = "";
                for (int step = 0; step < 60 && err == ""; step++)
                {
                    bool add = rng.Next(2) == 0;
                    int n = 1 + rng.Next(_maxBatch); // 一口气放/拿多格
                    var cells = new List<int>();
                    for (int k = 0; k < n; k++)
                    {
                        int x = rng.Next(w), y = rng.Next(h);
                        cells.Add((y + 1) * b.Size + (x + 1));
                    }
                    try
                    {
                        foreach (int i in cells)
                        {
                            if (add) a.Add(i % a.Size - 1, i / a.Size - 1);
                            else a.Remove(i % a.Size - 1, i / a.Size - 1);
                        }
                        if (add) b.AddBatch(cells); else b.RemoveBatch(cells);
                        err = b.Verify(b.ClonePresent(), b.Size, b.HSize);
                        if (err == "") err = CompareStates(a, b);
                    }
                    catch (Exception ex) { err = ex.Message; }
                    if (err != "")
                    {
                        var ba = GridBrute.Analyze(a.ClonePresent(), a.Size, a.HSize, conn);
                        var bb = GridBrute.Analyze(b.ClonePresent(), b.Size, b.HSize, conn);
                        err = $"第 {step} 步 {(add ? "放" : "拿")} {n} 格：{err}"
                            + $" ｜ a(逐格): {a.ComponentCount}块{a.HoleCount}洞 / 暴力 {ba.Comps}块{ba.Holes}洞"
                            + $" ｜ b(批量): {b.ComponentCount}块{b.HoleCount}洞 / 暴力 {bb.Comps}块{bb.Holes}洞"
                            + $" ｜ 批量格子：{string.Join(" ", cells.ConvertAll(i => (i % b.Size - 1) + "," + (i / b.Size - 1)))}";
                        break;
                    }
                }
                if (err != "") Fail($"批量等价（seed={1000 + r}，{w}×{h} {conn}）：{err}");
            }
            Console.WriteLine($"  跑完 {rounds} 例批量等价。");
            Console.WriteLine();
        }

        private static string CompareStates(IncTopology a, IncTopology b)
        {
            if (a.ComponentCount != b.ComponentCount) return $"块数 {a.ComponentCount} vs {b.ComponentCount}";
            if (a.HoleCount != b.HoleCount) return $"洞数 {a.HoleCount} vs {b.HoleCount}";
            if (a.CornerSum != b.CornerSum) return $"绕数 {a.CornerSum} vs {b.CornerSum}";
            for (int x = 0; x < a.W; x++)
                for (int y = 0; y < a.H; y++)
                    if (a.Present(x, y) != b.Present(x, y)) return $"格 ({x},{y}) 存在性不同";
            return "";
        }

        // ------------------------------------------------------------------
        //  性能基准
        // ------------------------------------------------------------------
        private static void RunBenchmarks(bool quick)
        {
            Console.WriteLine("--- 性能基准 ---");
            int W = 250, H = 250; // 与 RimWorld 地图同量级
            int[] singleOps = { quick ? 2000 : 20000 };
            int[] batchSizes = { 9, 100, 1000 };

            foreach (Conn conn in new[] { Conn.Fg4Bg8, Conn.Fg8Bg4 })
            {
                Console.WriteLine();
                Console.WriteLine($"[配对: {conn}]  地图 {W}×{H}（内部 {(W) * (H)} 格）");

                // 全量重算基准
                var seedPresent = BuildShipPresent(W, H, seed: 7);
                double tFull = TimeMs(() =>
                {
                    for (int i = 0; i < (quick ? 5 : 30); i++) GridBrute.Analyze(seedPresent, W + 2, H + 2, conn);
                }, quick ? 5 : 30);
                int fullReps = quick ? 5 : 30;
                Console.WriteLine($"  全量重算一次          : {tFull / fullReps,10:F3} ms");

                // 增量（带 O(1) 检测）
                var inc = MakeShip(W, H, conn, seed: 7);
                var rng = new Random(12345);
                var singleCells = new List<int>();
                for (int i = 0; i < singleOps[0]; i++) singleCells.Add(RandInterior(rng, W, H));

                double tInc = TimeMs(() =>
                {
                    foreach (int c in singleCells) Toggle(inc, c);
                }, singleCells.Count);
                Console.WriteLine($"  增删一格（带检测+无害跳过）: {tInc * 1e6 / singleCells.Count,10:F1} ns/次");
                Console.WriteLine($"     └ 修复扫描格数/次        : {(double)inc.StatRepairCells / singleCells.Count,10:F1}  （整图 {W * H} 格）");

                // 同一份工作负载：关掉 O(1) 检测（= 脏标记局部 BFS）
                var naive = MakeShip(W, H, conn, seed: 7);
                naive.AlwaysRepair = true;
                double tNaive = TimeMs(() =>
                {
                    foreach (int c in singleCells) Toggle(naive, c);
                }, singleCells.Count);
                Console.WriteLine($"  增删一格（每次都修背景）  : {tNaive * 1e6 / singleCells.Count,10:F1} ns/次  （×{tNaive / Math.Max(tInc, 1e-9):F1}）");
                Console.WriteLine($"     └ 修复扫描格数/次        : {(double)naive.StatRepairCells / singleCells.Count,10:F1}");

                // 批量：一批 9 / 100 / 1000 格
                foreach (int n in batchSizes)
                {
                    var batchCells = new List<int>();
                    for (int i = 0; i < n; i++) batchCells.Add(RandInterior(rng, W, H));

                    var batched = MakeShip(W, H, conn, seed: 7);
                    var perCell = MakeShip(W, H, conn, seed: 7);

                    const int reps = 50;
                    double tB = TimeMs(() =>
                    {
                        for (int r = 0; r < reps; r++)
                        {
                            bool add = (r % 2) == 0;
                            if (add) batched.AddBatch(batchCells); else batched.RemoveBatch(batchCells);
                        }
                    }, reps);
                    double tP = TimeMs(() =>
                    {
                        for (int r = 0; r < reps; r++)
                        {
                            bool add = (r % 2) == 0;
                            foreach (int c in batchCells) Toggle(perCell, c);
                        }
                    }, reps);
                    double perBatchNs = tB * 1e6 / reps, perCellNs = tP * 1e6 / reps;
                    Console.WriteLine($"  一批 {n,4} 格：批量 {perBatchNs / n,9:F0} ns/格（{perBatchNs / 1e6,7:F2} ms/批）"
                        + $"   逐格 {perCellNs / n,9:F0} ns/格（{perCellNs / 1e6,7:F2} ms/批）   提速 ×{tP / Math.Max(tB, 1e-9):F1}");
                }

                // 最坏情况：哑铃 —— 两大块由 1 格宽的桥连起来。
                // 砍桥 ⇒ 断成两大块，修复必须把整条连通区域都扫一遍（O(分量)）。
                Console.WriteLine("  最坏情况（哑铃桥：断成两个大块）：");
                foreach (int side in new[] { 50, 250 })
                {
                    var bell = new IncTopology(side, side, conn);
                    // 左右两个实心方块 + 中间一行的桥
                    for (int y = 0; y < side; y++)
                        for (int x = 0; x < side; x++)
                            if (x < side / 4 || x >= side - side / 4 || y == side / 2) bell.Add(x, y);
                    long before = bell.StatRepairCells;
                    bell.Remove(side / 2, side / 2);          // 砍桥
                    long cost = bell.StatRepairCells - before;
                    string err = bell.Verify(bell.ClonePresent(), bell.Size, bell.HSize);
                    Console.WriteLine($"    {side}×{side} 哑铃：砍桥扫描 {cost,8} 格   块数 {bell.ComponentCount}（应为 2） {(err == "" ? "✓" : "✗ " + err)}");
                    before = bell.StatRepairCells;
                    bell.Add(side / 2, side / 2);             // 接回
                    cost = bell.StatRepairCells - before;
                    err = bell.Verify(bell.ClonePresent(), bell.Size, bell.HSize);
                    Console.WriteLine($"    {side}×{side} 哑铃：接回扫描 {cost,8} 格   块数 {bell.ComponentCount}（应为 1） {(err == "" ? "✓" : "✗ " + err)}");
                }
            }
            Console.WriteLine();
        }

        private static double TimeMs(Action action, int reps)
        {
            action(); // 热身
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        private static void Toggle(IncTopology t, int cell)
        {
            int x = cell % t.Size - 1, y = cell / t.Size - 1;
            if (t.Present(x, y)) t.Remove(x, y); else t.Add(x, y);
        }

        private static int RandInterior(Random rng, int w, int h) =>
            (rng.Next(h) + 1) * (w + 2) + (rng.Next(w) + 1);

        // ------------------------------------------------------------------
        //  造一艘"船"：外板 + 舱壁 + 内部结构，用来当真实形状
        // ------------------------------------------------------------------
        private static bool[] BuildShipPresent(int w, int h, int seed)
        {
            var present = new bool[(w + 2) * (h + 2)];
            void Set(int x, int y) { if (x > 0 && y > 0 && x < w + 1 && y < h + 1) present[(y + 1) * (w + 2) + (x + 1)] = true; }

            int x0 = 6, y0 = 6, x1 = w - 7, y1 = h - 7;
            for (int x = x0; x <= x1; x++) { Set(x, y0); Set(x, y1); }
            for (int y = y0; y <= y1; y++) { Set(x0, y); Set(x1, y); }
            for (int x = x0 + 10; x < x1; x += 10)
                for (int y = y0 + 1; y < y1; y++)
                    if (((y - y0) % 7) != 3) Set(x, y);
            for (int y = y0 + 10; y < y1; y += 10)
                for (int x = x0 + 1; x < x1; x++)
                    if (((x - x0) % 9) != 4) Set(x, y);

            var rng = new Random(seed);
            for (int i = 0; i < 40; i++)
            {
                int bx = rng.Next(x0 + 1, x1 - 2), by = rng.Next(y0 + 1, y1 - 2);
                Set(bx, by); Set(bx + 1, by); Set(bx, by + 1); Set(bx + 1, by + 1);
            }
            return present;
        }

        private static IncTopology MakeShip(int w, int h, Conn conn, int seed)
        {
            var t = new IncTopology(w, h, conn);
            var present = BuildShipPresent(w, h, seed);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (present[(y + 1) * (w + 2) + (x + 1)]) t.Add(x, y);
            return t;
        }

        // ------------------------------------------------------------------
        //  工具
        // ------------------------------------------------------------------
        private static IncTopology Build(int w, int h, Conn conn = Conn.Fg4Bg8) => new IncTopology(w, h, conn);

        private static void Check(string name, IncTopology t, Conn conn, int expectComps, int expectHoles)
        {
            string err = t.Verify(t.ClonePresent(), t.Size, t.HSize);
            if (err != "") { Fail($"{name}：{err}"); return; }
            if (t.ComponentCount != expectComps) { Fail($"{name}：块数应为 {expectComps}，实为 {t.ComponentCount}"); return; }
            if (t.HoleCount != expectHoles) { Fail($"{name}：洞数应为 {expectHoles}，实为 {t.HoleCount}"); return; }
            Console.WriteLine($"  ✓ {name}：{t.ComponentCount} 块 {t.HoleCount} 洞，绕数 {t.CornerSum}（= 4×χ）");
        }

        private static void CheckState(string name, IncTopology t, int expectComps, int expectHoles)
            => Check(name, t, Conn.Fg4Bg8, expectComps, expectHoles);

        private static void Fail(string msg)
        {
            _failures++;
            Console.WriteLine($"  ✗ {msg}");
        }

        /// <summary>临时诊断：5×5 空心方框 → 破角 → 补角，打印背景归属。</summary>
        private static void DumpBox()
        {
            var t = Build(5, 5, Conn.Fg4Bg8);
            for (int x = 0; x < 5; x++) { t.Add(x, 0); t.Add(x, 4); }
            for (int y = 1; y < 4; y++) { t.Add(0, y); t.Add(4, y); }
            t.Remove(0, 0);
            Console.WriteLine($"破角后：{t.ComponentCount} 块 {t.HoleCount} 洞");
            t.Trace = m => Console.WriteLine("    " + m);
            t.Add(0, 0);
            t.Trace = null;
            Console.WriteLine($"补角后：{t.ComponentCount} 块 {t.HoleCount} 洞（应为 1 块 1 洞）");
            var brute = GridBrute.Analyze(t.ClonePresent(), t.Size, t.HSize, Conn.Fg4Bg8);
            Console.WriteLine("背景格归属（行 y，列 x；显示 根/暴力标签；P=砖）：");
            for (int y = -1; y <= 5; y++)
            {
                var line = new System.Text.StringBuilder();
                for (int x = -1; x <= 5; x++)
                {
                    if (x < 0 || y < 0 || x >= 5 || y >= 5) { line.Append("  pad  "); continue; }
                    int i = (y + 1) * t.Size + (x + 1);
                    if (t.Present(x, y)) { line.Append("   P   "); continue; }
                    line.Append($"  {t.RootOf(i)}/{brute.BgLabel[i]}  ");
                }
                Console.WriteLine($"y={y,2}:{line}");
            }
        }
    }
}
