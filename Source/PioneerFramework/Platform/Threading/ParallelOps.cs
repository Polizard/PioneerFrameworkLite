using System;
using System.Threading.Tasks;

namespace PioneerFramework
{
    /// <summary>
    /// **统一调度原语**（批7 第 7a 片，2026-09-30）。全库只此一份"要不要并行、怎么并行、怎么归约"的口径。
    ///
    /// ## 三条硬规则（写在这里，因为每条都对应一类已发生过的缺陷）
    /// 1. **确定性**：并行体**只准写自己那个下标**的槽位（`results[i]` / 自己的局部量）。
    ///    归约一律走 <see cref="MinIndex(double[])" />（**按下标严格取最小、并列取小下标**）——
    ///    **不许**在并行体里做 `min`/`+=` 之类的共享累加（浮点归约不定序 ⇒ 结果不定 ⇒ 同场景对比整套验收失效，
    ///    计划稿 §四 裁决表已把"浮点并行归约"列为**绝不做**）。
    /// 2. **要不要并行看"总工作量"**，不是只看条数：<see cref="ShouldParallelize" /> 用 `count × 单项成本`。
    ///    旧口径 `PARALLEL_PHYSICS_MIN_GROUPS = 32` 只数条数，**两端都错**（32 个很便宜的活不值得并行；
    ///    3 个很贵的活反而值得）—— 见 §17.4。
    /// 3. **并行区标记只有这一份**（<see cref="InParallelRegion" />）：共享写路径拿它断言"我不在并行区里"
    ///    （G1 守卫）。`SpaceGameComp.ParallelPhysicsInProgress` **转发**到这里，不另立第二个标记
    ///    （两个标记 = 两个权威，必有一份会漏）。
    ///
    /// 【边界】本文件**不碰** Verse/Unity ⇒ 可被纯测试工程与移植测试工程直接链接。
    /// </summary>
    public static class ParallelOps
    {
        /// <summary>
        /// 并行区标记（共享写路径读它做守卫）。**全库唯一一份**。
        ///
        /// ⚠ 它是**计数**不是 bool：两个并行区**同时活跃**（例如"物理相位"里嵌套"规划批并行"）时，
        /// 用 bool 的话**先退出的一方会把标记清掉**，另一方的守卫就失效了（那是静默的）⇒
        /// 用 `Interlocked` 计数，`> 0` 才算在区里。测试断言也因此**只该断言"区内为真"**，
        /// 不要在无关时刻断言"区外为假"（别的用例的并行区会让它短暂为真 —— 实测红过一次）。
        /// </summary>
        private static int _regionDepth;

        /// <summary>当前是否在任一并行区内（见 <see cref="_regionDepth" /> 的说明）。</summary>
        public static bool InParallelRegion => System.Threading.Volatile.Read(ref _regionDepth) > 0;

        /// <summary>
        /// 并行地板：`count × costPerItem` 低于它就**串行**跑。
        ///
        /// **16 µs（2026-10-14 net10 内核实测重标，原 60 µs 是 Mono 时代口径）**：
        /// 台架 `PioneerFramework.Bench` §6 扫描（同轮交错 best-of-9，本机 16 核；Sqrt 环标定，
        /// 总工作量 ≠ 绝对秒数但同量级）。**并行/串行 时间比**：
        /// <code>
        /// 总工作量   8 µs   16 µs   32 µs   64 µs   128 µs
        /// count=16  1.21↓   0.63    0.36    0.28    0.23
        /// count=8   0.72    0.51    0.38    0.32    0.29
        /// count=4   0.71    0.69    0.55    0.51    0.53
        /// count=2   0.86    0.80    0.79    0.59    0.57
        /// </code>
        /// 读法：**8 µs 档在最坏 count（16 件微小件）上仍亏（1.21）**；16 µs 档起最坏 0.80、普遍 0.5–0.7
        /// ⇒ 取 **16 µs** 为地板（该档起并行至少不亏，多数情形成倍赚）。旧 60 µs 会把 16–60 µs 的活
        /// 全留在串行（那是 2–4× 的便宜）。
        /// ⚠ 常数与核数无关（单一全局旋钮）：核少的机器上 16 µs 档大约退到"打平"（count=2 实测 0.80 即两核情形），
        ///   仍不会亏 ⇒ 保持单常数。两条路必须给出同样的值 ⇒ 由 `ParallelOpsTests` 钉住"串行与并行结果逐位相同"。
        /// </summary>
        public const double MinParallelWorkSeconds = 16e-6;

        /// <summary>
        /// **对照开关**（默认 `false`）：强制所有 <see cref="For" /> 走串行路。
        ///
        /// 用途只有一个 —— **做"并行 vs 串行"的逐位对照**（两条路必须给出同样的值；这条由
        /// `ParallelOpsTests`/`TwoImpulseDeterminismTests` 钉住）。它**不改变结果**，
        /// 只改变走哪条路 ⇒ 拿它当"性能对照实验"的单变量开关也是正当的。
        /// </summary>
        public static bool ForceSerial;

        /// <summary>
        /// 该不该并行：**条数 × 单项成本** 够不够摊平调度开销，且本机真有多核。
        /// <paramref name="costPerItemSeconds" /> 由调用方给**量级**（例如"每次 Lambert ≈0.5 ms"），
        /// 不要求精确 —— 它只决定走哪条路，两条路结果相同（见类型注释规则 1）。
        /// </summary>
        public static bool ShouldParallelize(int count, double costPerItemSeconds)
        {
            if (ForceSerial) return false;
            if (count <= 1) return false;
            if (Environment.ProcessorCount <= 1) return false;
            return count * costPerItemSeconds >= MinParallelWorkSeconds;
        }

        /// <summary>
        /// 并行的 `for`：`body(i)` **只准写第 i 个槽位**（确定性靠这条约定，不靠调度）。
        /// 工作量不够时走同一条 `body` 的串行路径 ⇒ **两条路语义完全相同**。
        /// ⚠ `body` 里若需要 `try/catch`，**必须留在 body 内**（抛到 `Parallel.For` 外会变成
        /// `AggregateException` ⇒ 改控制流，见 §17.4 #1 的告警）。
        /// </summary>
        public static void For(int count, double costPerItemSeconds, Action<int> body)
        {
            if (count <= 0) return;

            if (!ShouldParallelize(count, costPerItemSeconds))
            {
                for (int i = 0; i < count; i++) body(i);
                return;
            }

            bool wasInRegion = InParallelRegion;
            System.Threading.Interlocked.Increment(ref _regionDepth);
            try
            {
                Parallel.For(0, count, body);
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref _regionDepth);   // 计数：并发/嵌套都不会互相清除
                _ = wasInRegion;
            }
        }

        /// <summary>
        /// **唯一允许的聚合器**：`values` 里最小值的**下标**；并列取**最小的下标**。
        /// 空数组或全 `NaN` 时返回 `-1`（调用方必须显式处理，不许拿 0 冒充"找到了"）。
        /// 整个归约在里面是**一条顺序循环** ⇒ 与线程数、调度、机器无关。
        /// </summary>
        public static int MinIndex(double[] values)
            => MinIndex(values, values?.Length ?? 0);

        /// <summary>
        /// **带长度的唯一归约器**（2026-11-01）：调用方用复用数组时只归约前 `count` 项
        /// （数组尾部是上一次的陈旧值）。语义与 <see cref="MinIndex(double[])" /> **逐字相同**：
        /// 跳过 NaN、严格 `&lt;`、并列取最小下标；`count` 夹到数组长度内。
        /// </summary>
        public static int MinIndex(double[] values, int count)
        {
            if (values == null || count <= 0) return -1;
            int limit = Math.Min(count, values.Length);
            int best = -1;
            double bestVal = double.PositiveInfinity;
            for (int i = 0; i < limit; i++)
            {
                double v = values[i];
                if (double.IsNaN(v)) continue;
                if (best < 0 || v < bestVal)   // 严格 `<` ⇒ 并列保留先到的（即更小的下标）
                {
                    best = i;
                    bestVal = v;
                }
            }
            return best;
        }
    }
}
