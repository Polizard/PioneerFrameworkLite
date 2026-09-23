using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **阵营解析（[V12]，2026-09-23）** —— 把"整数 `factionId`"接回**真阵营语义**。
    ///
    /// ## 为什么需要这一步
    /// 现状①：**"敌对"是裸整数比较**（`g.FactionId != myGroup.FactionId`，两处）；现状②：调试场景里的
    /// 敌人阵营是**假 id** —— `(Faction.OfPlayer.loadID) + 1`（14 处），而 `Find.FactionManager` 里
    /// **根本不存在**这个阵营。后果不是立刻出错，而是：真阵营的敌对关系（附庸/同盟/永久敌对）与
    /// 整数不等**不是一回事**，而 P2 的 `Contact` 要带 "`factionId` → `Faction`" 的解析（[V3] 的输入）
    /// ⇒ 得先把语义接回来，否则 P3 接线时会把假 id 当可用阵营 id。
    ///
    /// ## 语义（**必须显式定义**：别让"解析失败"变成静默的"不敌对"）
    /// · `Get(loadID)`：命中缓存；未命中**线性扫描一次**（既有先例唯一一处：`MapArchiveManager._factionCache`）；
    ///   仍未命中 ⇒ `null`（不抛、不返回假对象）。**只缓存命中**——阵营可能之后才生成。
    /// · `IsHostile(a, b)`：两边都解析成功 ⇒ `Verse.Faction.HostileTo`（含附庸/同盟语义）；
    ///   **任一方解析失败 ⇒ 按"敌对"处理**（保守）。理由（计划稿原文）：旧档/调试场景可能残留假 id，
    ///   若把"解析失败"当成"不敌对"，**匿踪组会变成对谁都不敌对** —— 那比误伤更糟。
    ///
    /// ## 读数（本库纪律：**不许用沉默冒充"没问题"**）
    /// 解析失败除了**限频** `Log.Warning`（60 s 一条，防刷屏）之外，还累计 **`UnresolvedCount`**
    /// —— 探针/自检面板读它 ⇒ "没有警告"不再等于"没问题"（限频会把它后面的失败压掉）。
    /// </summary>
    public static class FactionResolver
    {
        private static readonly Dictionary<int, Faction> cache = new Dictionary<int, Faction>();
        private static int lastWarnTick = int.MinValue;
        private const int WarnIntervalTicks = 3600;   // 60 s（1 s = 60 tick）

        /// <summary>累计**解析失败**次数（无条件读数；限频警告之外的独立证据）。</summary>
        public static int UnresolvedCount { get; private set; }

        /// <summary>按 loadID 解析阵营。未命中 ⇒ `null`（故返回类型**显式可空**）。</summary>
        public static Faction? Get(int loadID)
        {
            if (loadID < 0) return null;
            if (cache.TryGetValue(loadID, out var cached)) return cached;

            Faction? found = null;
            var all = Find.FactionManager?.AllFactions;
            if (all != null)
            {
                foreach (var f in all)
                {
                    if (f != null && f.loadID == loadID) { found = f; break; }
                }
            }
            if (found != null) cache[loadID] = found;   // 只缓存命中（未命中可能因为阵营还没生成）
            return found;
        }

        /// <summary>
        /// 限频判据（**纯**，分离出来是为了能被测试钉住 —— 它自己曾经是坏的，见下）。
        ///
        /// ⚠ 2026-09-23 修：哨兵 `int.MinValue`（"从未警告过"）**不能**直接参与减法 ——
        /// `nowTick - int.MinValue` 在 unchecked 下**整型溢出**回负数 ⇒ 判据恒假 ⇒
        /// **警告从第一次调用起就再也不会打印**（而且 `lastWarnTick` 永不更新 ⇒ 永久失效）。
        /// 代码里那句"限频 Log.Warning"曾经是一句**做不到的承诺**：`UnresolvedCount` 照常累计、
        /// `LogProbe` 照常显示，只有日志行是空的 ⇒ 单看日志会得出"没有解析失败"的结论。
        /// ⇒ 哨兵必须**显式判**（本条由 `FactionResolverTests.WarnPredicate_*` 钉住）。
        /// </summary>
        internal static bool ShouldWarn(int nowTick, int lastWarnTick, int intervalTicks)
            => lastWarnTick == int.MinValue || nowTick - lastWarnTick >= intervalTicks;

        /// <summary>
        /// 两个 loadID 之间是否**敌对**。任一方解析失败 ⇒ **true（未知即敌）** + 记数 + 限频警告。
        /// ⚠ 只用于"**系统替你选目标**"的地方（自动索敌）；玩家自己点选的地方保持"可见即可打"（§11 D7）。
        /// </summary>
        public static bool IsHostile(int factionIdA, int factionIdB)
        {
            var a = Get(factionIdA);
            var b = Get(factionIdB);
            if (a == null || b == null)
            {
                UnresolvedCount++;
                int nowTick = Find.TickManager?.TicksGame ?? 0;
                if (ShouldWarn(nowTick, lastWarnTick, WarnIntervalTicks))
                {
                    lastWarnTick = nowTick;
                    Log.Warning(string.Format(
                        "[FactionResolver] 阵营解析失败（{0}→{1}｜{2}→{3}）⇒ 按敌对处理；累计 {4} 次",
                        factionIdA, a == null ? "未找到" : a.Name,
                        factionIdB, b == null ? "未找到" : b.Name, UnresolvedCount));
                }
                return true;
            }
            return a.HostileTo(b);
        }

        /// <summary>
        /// **调试场景用的敌对阵营 id**（替换原来的假 id `(Faction.OfPlayer.loadID) + 1`）：
        /// 取第一个**与玩家敌对**的可见阵营；一个都没有 ⇒ **-1**（并记一次解析失败读数）。
        /// ⚠ 返回 -1 时**不造假 id**：假 id 在 `FactionManager` 里不存在 ⇒ 只会把"解析失败"这一读数喂脏。
        /// </summary>
        public static int HostileFactionIdForPlayer()
        {
            var fm = Find.FactionManager;
            if (fm == null) return -1;
            var player = Faction.OfPlayer;
            if (player == null) return -1;

            foreach (var f in fm.AllFactionsVisible)
            {
                if (f != null && f != player && f.HostileTo(player)) return f.loadID;
            }
            return -1;
        }

        /// <summary>
        /// **探针读数**（无条件）：把"解析失败"从一个**沉寂的限频警告**变成一行可读证据
        /// （本库纪律：不许用沉默冒充"没问题"）。调试页签「80 调试日志」→「阵营解析读数」调用它。
        /// </summary>
        public static void LogProbe()
        {
            var fm = Find.FactionManager;
            var player = Faction.OfPlayer;
            int hostileId = HostileFactionIdForPlayer();
            var hostile = Get(hostileId);
            string playerTxt = player != null ? player.Name + "(" + player.loadID + ")" : "无";
            string hostileTxt = hostile != null
                ? hostile.Name + "(" + hostileId + ")"
                : "**一个都没有** —— 调试场景会拿到 -1（不再造假 id）";

            Log.Message(string.Format(
                "[V12·阵营] 玩家={0}｜调试用敌对阵营={1}｜阵营总数={2}（可见 {3}）｜**解析失败累计 {4} 次**",
                playerTxt, hostileTxt, CountOf(fm != null ? fm.AllFactions : null),
                CountOf(fm != null ? fm.AllFactionsVisible : null), UnresolvedCount));
        }

        private static int CountOf(IEnumerable<Faction>? seq)
        {
            int n = 0;
            if (seq != null) foreach (var _ in seq) n++;
            return n;
        }

        /// <summary>清缓存与计数（读档/新游戏时调用；缓存是**派生数据**，不是事实源）。
        /// `lastWarnTick` 回到**哨兵** `int.MinValue` = "从未警告过"（见 <see cref="ShouldWarn"/>，别拿它直接做减法）。</summary>
        public static void Clear()
        {
            cache.Clear();
            UnresolvedCount = 0;
            lastWarnTick = int.MinValue;
        }
    }
}
