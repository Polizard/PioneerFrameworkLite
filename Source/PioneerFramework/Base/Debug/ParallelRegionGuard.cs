using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **并行区共享写守卫（G1，2026-09-26）**：并行物理区相位里，任何对**共享状态**的写都是数据竞争。
    ///
    /// ## 为什么要有它（而不是"逐条修完就算了"）
    /// 批 2 开工审计查出**并行区内可达的共享写有 9 类**（最终计划原表只点名了 1 类）⇒ 修法必然是逐条的，
    /// 但**"下一条漏进来的"没有人拦**。本类给出**一条判据覆盖整类**：共享写路径在动手写之前问一次
    /// <see cref="GuardWrite"/>，相位里被调用即记一次 + 报一行错 ⇒ "再漏一条"是**当场可见**的，
    /// 而不是等某天表现为"偶尔一步结果被丢掉"（R1 就是这样活了很久：277 次异常，而指纹判据照样报"可复现"）。
    ///
    /// ## 相位旗为什么能当守卫（**不另立 `[ThreadStatic] inRegion`**）
    /// `SpaceGameComp.ParallelPhysicsInProgress` 由**驱动线程**在 `Parallel.For` 前后置位/清位 ⇒
    /// "它为真" ⟺ "此刻有工作线程在跑" ⇒ 正是"这次写是并发的"的定义。它已经存在、已被 R1 的守卫用过
    /// ⇒ 不新立第二份相位表示（**两份相位 = 两份权威，必有一份漏同步**，这正是本库反复吃的那个坑）。
    ///
    /// ## 三分档：守卫只贴"零容忍"的两档（**贴错档会让守卫对合法路径误报 ⇒ 很快被无视**）
    /// ① **共享容器**（Dictionary 一类）⇒ 零容忍 ⇒ **贴守卫**（修法 = 搬出并行区）。
    /// ② **决策量**（读-改-写当节流/开关，如 `BurnDiagNextTick`、`ShouldProbeGravity`）⇒ 零容忍
    ///    ⇒ **贴守卫**（修法 = 判据去共享或搬出并行区）——注意"读-改-写"哪怕 64 位对齐**不撕裂**，
    ///    它的**决策结果**仍随调度变化 ⇒ 是正确性问题不是计数问题。
    /// ③ **聚合量**（可交换/可结合：计数、min/max）⇒ **不贴守卫**，修法是**原子化**（`Interlocked`）。
    ///    贴了守卫反而错：它会把"已经接受的写法"报成竞争。
    ///
    /// ## 它覆盖不到的东西（**明说，不许用沉默冒充覆盖**）
    /// · `Log.Message` 一类**拦不住**（`Verse.Log` 是静态类，没有可挂的钩子）⇒ R4 的 8 处日志只能靠
    ///   "把诊断块整块搬回主线程" + **静态 grep 判据**（在并行体区间里不许出现 `Log.`）。
    /// · `Current.Game.GetComponent<T>()` 这类**全局查找**不自动覆盖 ⇒ 要在调用点手放（R5 的三条路）。
    /// · `CelestialBodyManager` 的体位置采样（`BodySampleCache.Store`）**只登记不守卫**：它是
    ///   "按 body 槽的引用赋值 + 同 tick 幂等"（2026-09-14 已因值类型撕裂改成引用语义）⇒ 值相同、无害；
    ///   前提是 `EnsureCapacity`/`Reset` 不在相位里跑（那两条只在 `EnsureInitialized`，已由 R1 的预暖拉回主线程）。
    ///
    /// ## 它自己怎么被验证（**没有正对照的守卫 = 无法区分"守卫正常"与"守卫根本不生效"**）
    /// <see cref="SelfTest"/>：**正对照**（相位里故意写一次 ⇒ 必须报出）+ **负对照**（相位外写一次 ⇒
    /// 必须不报），两条在**同一个用例**里，任一不合即 FAIL。两个对照必须同场做 ——
    /// 分开做的话，"守卫恒不报"与"守卫正常"这两个世界仍然无法区分（本库栽过的那类）。
    ///
    /// ## 判据形态（**用增量，不用绝对值**）
    /// `SharedWrites` 会**保留自检那几次**（自检故意制造 ≥8 次）⇒ 任何验收读它都必须**先取基线再取增量**，
    /// 否则点过一次自检的会话里判据永远红。
    /// </summary>
    internal static class ParallelRegionGuard
    {
        /// <summary>本会话累计：相位里发生的**共享写**次数（判据：**增量**恒 0）。</summary>
        public static long SharedWrites;

        /// <summary>本会话累计：相位里发生的**全局查找**（`Current.Game.GetComponent<T>()` 一类）次数。
        /// 单独计数而不与 `SharedWrites` 合并：两者的**修法不同**（前者搬出/去共享，后者传引用）⇒
        /// 合成一个计数就只能说"有条漏了"，分不开是哪一类。</summary>
        public static long GlobalLookups;

        private static long warned;
        private static long warnedLookup;

        private sealed class SiteCount { public long n; }

        // **按站点分档**：总数只说"有几条漏了"，说不出**是哪几条** ⇒ 修的时候还得再查一遍。
        // 用 `ConcurrentDictionary` + `Interlocked`：**守卫自己的记账不许是竞争**（否则它成了它要抓的东西）。
        // ⚠ 计入总量与分档的都是**原子聚合**（可交换）⇒ 按三分档属于"允许在相位里做"的那一档
        //   （与它要抓的"共享容器写 / 决策读-改-写"不是一回事）。
        private static readonly ConcurrentDictionary<string, SiteCount> writesBySite =
            new ConcurrentDictionary<string, SiteCount>();
        private static readonly ConcurrentDictionary<string, SiteCount> lookupsBySite =
            new ConcurrentDictionary<string, SiteCount>();

        /// <summary>按站点取读数（验收结论用）。调用方在开工前 `ResetSiteTally()` ⇒ 这里就是本场景的增量。
        /// 字符串按字典遍历序拼接（量少，不需要稳定序）。</summary>
        public static string SiteReport()
        {
            var sb = new StringBuilder();
            AppendSites(sb, "写", writesBySite);
            AppendSites(sb, "查", lookupsBySite);
            return sb.Length == 0 ? "（无）" : sb.ToString();
        }

        private static void AppendSites(StringBuilder sb, string tag, ConcurrentDictionary<string, SiteCount> map)
        {
            foreach (var kv in map)
            {
                long n = Interlocked.Read(ref kv.Value.n);
                if (n == 0) continue;
                if (sb.Length > 0) sb.Append("｜");
                sb.Append(tag).Append("×").Append(n).Append(' ').Append(kv.Key);
            }
        }

        /// <summary>把相位里某一栈事务的写入 `what` 名册清空（验收开工前调一次，读增量）。</summary>
        public static void ResetSiteTally()
        {
            writesBySite.Clear();
            lookupsBySite.Clear();
        }

        /// <summary>
        /// 共享状态**动手写之前**调用一次。相位外零成本（一次 bool 读）。
        /// `what` **必须是编译期字面量**（不许拼接/插值）：它在热路径上每群每步被求值，
        /// 拼接字符串 = **每次一次分配** ⇒ 会破坏台账第 22 条「并行区零分配」，即"守卫自己引入的性能问题"。
        /// 需要站点细节（如 rootId）时，用**常量**名字 + 另一处分档读数，别在参数里拼。
        /// </summary>
        public static void GuardWrite(string what)
        {
            if (!SpaceGameComp.ParallelPhysicsInProgress) return;

            Interlocked.Increment(ref SharedWrites);
            Interlocked.Increment(ref writesBySite.GetOrAdd(what, _ => new SiteCount()).n);
            if (Interlocked.CompareExchange(ref warned, 1L, 0L) == 0L)
            {
                Log.Error("[并行区] **相位里写共享状态**（G1）：" + what
                    + " —— 并行区里只有「写本迭代独占的槽位」合法。"
                    + "首次报出后不再刷屏（按站点的分档读数看 `SiteReport()`）。");
            }
        }

        /// <summary>
        /// **全局查找**（`Current.Game.GetComponent<T>()` / `Find.Maps` 一类）之前调用一次。
        /// 它不是"写共享状态"，但**是同一类病**：在相位里做全局查找既拿了共享对象、又踩了引擎的
        /// 非线程安全路径，而且**每组每步一次**的量级让它同时是性能问题（R5 的三条路）。
        /// `what` 同样是**编译期字面量**（理由见 `GuardWrite`）。
        /// </summary>
        public static void GuardLookup(string what)
        {
            if (!SpaceGameComp.ParallelPhysicsInProgress) return;

            Interlocked.Increment(ref GlobalLookups);
            Interlocked.Increment(ref lookupsBySite.GetOrAdd(what, _ => new SiteCount()).n);
            if (Interlocked.CompareExchange(ref warnedLookup, 1L, 0L) == 0L)
            {
                Log.Error("[并行区] **相位里做全局查找**（G1/R5）：" + what
                    + " —— 引用本该在手上（逐级传参），别在相位里问引擎。"
                    + "首次报出后不再刷屏（按站点的分档读数看 `SiteReport()`）。");
            }
        }

        /// <summary>正/负对照同跑（调试页签按钮）。同步用例 ⇒ 不占框架的异步单槽位。</summary>
        public static void SelfTest()
        {
            PflTestKit.Begin("G1 守卫自检（正负对照同场）", "00 验收");

            // 正对照：相位里两类都必须报出。用**真正的 Parallel.For** ⇒ 与物理步走同一条路径（工作线程）。
            long w0 = SharedWrites, l0 = GlobalLookups;
            SpaceGameComp.ParallelPhysicsInProgress = true;
            try
            {
                Parallel.For(0, 8, i =>
                {
                    GuardWrite("正对照（自检：故意在相位里写）");
                    GuardLookup("正对照（自检：故意在相位里查）");
                });
            }
            finally
            {
                SpaceGameComp.ParallelPhysicsInProgress = false;   // try/finally：自检抛异常也不能把相位留在 true
            }
            long inWrites = SharedWrites - w0;
            long inLookups = GlobalLookups - l0;

            // 负对照：相位外一次都不许报（"守卫恒报"和"守卫正常"必须能分开）。
            long w1 = SharedWrites, l1 = GlobalLookups;
            GuardWrite("负对照（自检：相位外，正常路径）");
            GuardLookup("负对照（自检：相位外，正常路径）");
            long outWrites = SharedWrites - w1;
            long outLookups = GlobalLookups - l1;

            PflTestKit.Check(PflTestKit.Kind.Invariant, inWrites >= 8 && inLookups >= 8,
                "正对照：相位里的共享写 / 全局查找**都必须**被报出（否则守卫是个从不触发的摆设）",
                string.Format("相位内增量 写={0} 查={1}（各应 ≥ 8 = 8 个并行迭代各一次）", inWrites, inLookups));
            PflTestKit.Check(PflTestKit.Kind.Invariant, outWrites == 0 && outLookups == 0,
                "负对照：相位外的调用**不许**被报出（否则它会对正常路径误报，很快被无视）",
                string.Format("相位外增量 写={0} 查={1}（都应为 0）", outWrites, outLookups));
            PflTestKit.End();
        }
    }
}
