using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **「一键测试（全覆盖）」入口**（S2 首片，2026-09-23）—— 把**一次调用能跑完**的验收收进统一判定，
    /// 并把**跑不完 / 还没有判据的**逐条**显式登记**（而不是假装覆盖了）。
    ///
    /// ## 它现在覆盖什么
    /// · **框架自检两用例**（阵营解析 [V12] / 可见性四字段 [P1]）—— 直接调用例本体，判据不复制；
    /// · **引力场自检电池**（34 项，同步）—— 薄壳：跑完把它的通过/失败计数记成一条用例；
    /// · **降落判据（S3）**：报价（地月星下点）· 距离门拒（对跖点）· 跨层错配拒 + 同层对照 · tile 越界拒 ·
    ///   缺表面拒（查不到这种天体 ⇒ UNCHECKED）· 执行三件事（消息 + 扣料 + 销毁）；
    /// · **撞击事件（S3，异步）**：两颗探针（20 / 300 m/s）落地后断言"着陆 / 坠毁各一次且经纬度不同"。
    ///
    /// ## 它**不**覆盖什么（显式登记：按本库纪律，"没跑"不许静默）
    /// · **异步段**（弹群齐射/合并态聚合器/炮塔对射/无Map碰撞/轴向燃烧/月球域/弹头引力/漏靶 —— 8 阶段）：
    ///   要等物理过程跑完，仍在「一键验收」（`PerfAcceptanceRunner`）里；本入口**只登记不重复跑**
    ///   （重复跑会两个状态机抢同一批场景 ✗）。
    /// · **场景类探针尚无可判定判据**的：交会阶梯（有界时间到达）· 炮塔六场景（命中）· 发射爬升（入轨）
    ///   —— 属 **S3** 的活，逐条补判据后接进来。
    /// </summary>
    public static class FullTestRunner
    {
        public static void Run()
        {
            PflTestKit.Reset();

            // ① 框架自检的用例本体（共享同一份判据）
            FrameworkSelfTest.RunCases();

            // ② 引力场自检电池（同步 34 项）—— 薄壳：不重写它的判据，只把结论收进来
            PflTestKit.Begin("引力场自检电池（同步）", "00 验收");
            try
            {
                GravityFieldSelfTest.Run();
                PflTestKit.Check(PflTestKit.Kind.Invariant, GravityFieldSelfTest.LastFailed == 0,
                    "电池失败项为 0",
                    string.Format("通过 {0} / 失败 {1}", GravityFieldSelfTest.LastPassed, GravityFieldSelfTest.LastFailed));
                if (GravityFieldSelfTest.LastFailed > 0)
                {
                    var detail = new System.Text.StringBuilder();
                    var fails = GravityFieldSelfTest.LastFailures;
                    for (int i = 0; i < fails.Count; i++)
                    {
                        if (i > 0) detail.Append("；");
                        detail.Append(fails[i]);
                    }
                    PflTestKit.Check(PflTestKit.Kind.Value, false, "电池失败明细", detail.ToString());
                }
            }
            catch (System.Exception e)
            {
                // 电池自己炸了 ⇒ 明确记失败（不是静默）
                PflTestKit.Check(PflTestKit.Kind.Invariant, false, "电池异常", e.Message);
            }
            PflTestKit.End();

            // ③ 着陆判据（S3，2026-09-23）：同步段 = 报价/距离门/跨层错配/越界/缺表面/执行三件事
            LandingAcceptanceRunner.RunSync();

            // ③b 三个**同步**跑批器（2026-09-24 收编）：不重写它们的判据，只把结论收进来。
            //     ⚠ 0 判据 ⇒ UNCHECKED（"跑批器没跑起来"不许冒充通过）。
            SyncBattery("适配层一键验收（同步 · 借判据；收工清场）", AdapterAcceptanceRunner.Run,
                () => AdapterAcceptanceRunner.LastChecks, () => AdapterAcceptanceRunner.LastFailures,
                "铺场前置不满足（无 SpaceGameComp / 天体管理器）⇒ 一条判据都没跑", cleanup: true);
            SyncBattery("计划编辑器验收（同步 · 借判据；**会顺手打开规划器窗口**）", PlanEditorAcceptanceRunner.Run,
                () => PlanEditorAcceptanceRunner.LastChecks, () => PlanEditorAcceptanceRunner.LastFailures,
                "前置不满足 ⇒ 一条判据都没跑（它自己会打结论块说明）");
            // 星座自检自己会清场（`Cleanup(comp)`）⇒ 这里不用再收
            SyncBattery("星座自检（同步 · 借判据；自带清场）", ConstellationSelfTest.Run,
                () => ConstellationSelfTest.LastPassed + ConstellationSelfTest.LastFailed,
                () => ConstellationSelfTest.LastFailed,
                "前置不满足 ⇒ 一条判据都没跑");

            // ④ 显式登记"没覆盖的"（**这是本入口最重要的一节**：不许用沉默冒充全覆盖）
            PflTestKit.Begin("未覆盖项登记（异步段 + 待补判据的场景）", "00 验收");
            PflTestKit.CheckUnchecked("异步 8 阶段（弹群/对射/碰撞/燃烧/月球域/弹头/漏靶）",
                "要等物理过程跑完 ⇒ 仍在「一键验收」里跑（本入口不重复跑，避免两个状态机抢同一批场景）");
            PflTestKit.CheckUnchecked("四个**长耗时**燃烧场景（真飞 / 燃烧 A/B / 相位三点 / 架构验证）",
                "要等真实燃烧跑完（几分钟游戏时间）⇒ 按各自按钮跑；**它们现在都有超时**（等不到会打 UNCOVERED 一行，不再永久静默）");
            PflTestKit.CheckUnchecked("交会阶梯（有界时间到达 300 m / 2 m/s）",
                "游戏内只有铺场 + 人读；纯核替代（阶梯端到端台架）尚未建 ⇒ S3/S4");
            PflTestKit.CheckUnchecked("炮塔六场景（命中）· 发射爬升（入轨）",
                "只有部署与人读 ⇒ S3");
            PflTestKit.CheckUnchecked("降落 UI 的选点交互（鼠标拾取 / 落点窗口）",
                "要人在屏幕前点（S3 的手动清单）；报价与执行本身已由「降落执行」用例覆盖");
            PflTestKit.Check(PflTestKit.Kind.Coverage, true, "登记完成");
            PflTestKit.End();

            // ⑤ **异步段必须最后登记**（登记表只有一个槽位：异步段开着时再 Begin 别的用例，
            //    它落地时会把判据写进那个用例并提前结算）—— 撞击事件落地后会自己打一行「用例…结算」。
            LandingAcceptanceRunner.StageImpact();

            PflTestKit.PrintVerdict("一键测试（全覆盖）");
        }

        /// <summary>
        /// 把一个**同步**跑批器收成一条用例：跑它 → 读它的判据计数 → 记结论。
        /// 判据本体不复制（借用它的计数）；**0 判据 ⇒ UNCHECKED**（不是通过）。
        /// </summary>
        private static void SyncBattery(string name, System.Action run,
            System.Func<int> checks, System.Func<int> failures, string emptyWhy, bool cleanup = false)
        {
            PflTestKit.Begin(name, "00 验收");
            if (cleanup) PflTestKit.SnapshotGroups();   // 只清"本段新建的"
            try
            {
                run();
            }
            catch (System.Exception e)
            {
                // 跑批器自己炸了 ⇒ 明确记失败（不是静默）
                PflTestKit.Check(PflTestKit.Kind.Invariant, false, "跑批异常", e.Message);
                PflTestKit.End();
                return;
            }
            int c = checks();
            if (c <= 0)
            {
                PflTestKit.Unchecked("0 判据", emptyWhy);
                return;
            }
            int f = failures();
            PflTestKit.Check(PflTestKit.Kind.Invariant, f == 0, "失败判据为 0",
                string.Format("判据 {0} 条 / 失败 {1} 条", c, f));
            if (cleanup)
            {
                int cleared = PflTestKit.CleanupSince();
                PflTestKit.Note(string.Format("清场销毁了 {0} 个跑批器自建的编队（**不留资产**）", cleared));
            }
            PflTestKit.End();
        }
    }
}
