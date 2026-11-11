using System;
using System.Reflection;
using LudeonTK;
using PioneerFramework.Combat;
using PioneerFramework.Launch;   // GetSurfaceState（天体→PlanetLayer 的唯一出处；烘焙入口要用）
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 「PFL 测试」页签（2026-09-10 规范化）：把原先挂在调试建筑 ThingComp 上的全部测试入口
    /// 收进**游戏自带 Debug 菜单**（dev mode 顶部页签）。
    ///
    /// 扩展方式 = 官方扩展点：<c>DebugTabMenuDef.menuClass</c> 指向本类（Def：<c>PF_DebugTab</c>），
    /// **不需要 Harmony 补丁**。写法照官方 <c>DebugTabMenu_Settings</c>：
    ///   · 普通动作 = <c>Action</c> 类型节点；
    ///   · 复选框 = <c>Action</c> 类型节点 + <c>settingsField</c>（框架只据此渲染成勾选框，
    ///     **切换动作由本类提供**——所以能一次联动多个字段，如"详细战斗日志"同时设/清射弹探针间隔）。
    ///   · 动态文案用 <c>labelGetter</c>（如"运行中…"、开关状态）。
    /// 场景实现全在 <see cref="DebugShipSpawner"/>（静态、自建地图，不依赖建筑）。
    /// </summary>
    public class DebugTabMenu_PFL : DebugTabMenu
    {
        public DebugTabMenu_PFL(DebugTabMenuDef def, Dialog_Debug dialog, DebugActionNode root)
            : base(def, dialog, root)
        {
        }

        public override DebugActionNode InitActions(DebugActionNode absRoot)
        {
            myRoot = new DebugActionNode("PFL 测试", DebugActionType.Action, null, null);
            absRoot.AddChild(myRoot);
            AddAction("PF_PlanetKind_DebugOpen".Translate(), "PF_PlanetKind_DebugCategory".Translate(), 100, DebugActions_CelestialFramework.PlanetKinds, null);
            AddAction("PF_PlanetKind_DebugCreateAll".Translate(), "PF_PlanetKind_DebugCategory".Translate(), 90, DebugActions_CelestialFramework.CreatePlanetKinds, null);
            AddAction("PF_Celestial_Title".Translate(), "PF_PlanetKind_DebugCategory".Translate(), 80, DebugActions_CelestialFramework.Inspect, null);
            AddAction("太空 AI：生成 3 艘完整敌舰（攻击活动玩家船）", "10 战斗", 90, SpaceAiDebugActions.SpawnThree, null);
            AddAction("太空 AI：生成 6 艘完整敌舰（攻击活动玩家船）", "10 战斗", 89, SpaceAiDebugActions.SpawnSix, null);
            AddAction("太空 AI：状态与性能读数", "10 战斗", 88, SpaceAiDebugActions.Report, null);

            // ═══ 验收（性能优化 9 条 + 中低优先批）═══
            // S2 首片（2026-09-23）：单一入口「一键测试（全覆盖）」—— 跑得完的全收进统一判定框架，
            // 跑不完/还没判据的**逐条显式登记**（不许用沉默冒充全覆盖）
            AddAction("大气/悬浮/档位判据", "00 验收", 58, AtmosphereAcceptanceRunner.Run, null);
            AddAction("【08】一键测试（全覆盖）", "00 验收", 35, FullTestRunner.Run, null);
            // 2026-11-01（用户要求）：单按钮专项自检 —— 只跑"合并态聚合器"阶段，3 秒后自动出结论并清场。
            AddAction("【09】2PC 专项自检（单阶段·自动清场·自动判定）", "00 验收", 34,
                () => PerfAcceptanceRunner.RunFocusedSelfTest(1, 180),
                () => PerfAcceptanceRunner.Running
                    ? "【09】2PC 专项自检（运行中…）"
                    : "【09】2PC 专项自检（单阶段·自动清场·自动判定）");
            // [V7]（2026-10-01）：自跑站 + 站内传感器阵列建筑，验"投影非零 → 打伤减半 → 摧毁归零"，并打印存档前读数
            // PFView 天体地表烘焙（2026-10-10）：手动选天体 → 只烤**有地块覆盖的球冠** + 山/河/路点缀 → 落旁挂 PNG（跟存档走）
            AddAction("PFView·烘焙天体地表补丁（只烘有地块的区域 + 山/河/路；手动选天体；结果落旁挂文件，不自动烘）", "20 视图/表现", 60,
                BakeSurfaceMenu, null);

            // PFView 截图（2026-10-14）：把"只能看"的问题（西瓜纹/亮度）落成 PNG ⇒ 离线可判、可以拿图说事
            AddAction("PFView 截图 → PNG（落 SaveDataFolder/PFView_shots；PFView 需处于打开状态）", "20 视图/表现", 59,
                delegate
                {
                    string? p = MainTabWindow_PFView.CaptureToPng();
                    Log.Message(p != null ? "[PFView·截图] 已写出：" + p : "[PFView·截图] 失败：PFView 未打开或无 RT");
                }, null);

            // PFView 视图系统 v2（2026-10-18）：原先挂在玩家工具条/面板上的测试与作弊项，统一收进这里。
            AddAction("PFView·部署交火测试对（1 我方 + 1 敌对，100 m 间距；需 PFView 打开）", "20 视图/表现", 57,
                PFViewDebugActions.DeployCombatPair, null);
            AddAction("PFView·把目标传送到正前方 1km（作弊；需先选中射手与目标）", "20 视图/表现", 56,
                PFViewDebugActions.TeleportTarget, null);
            AddAction("PFView·目标 +100 m/s（作弊）", "20 视图/表现", 55,
                PFViewDebugActions.TargetSpeedUp, null);
            AddAction("PFView·目标 -100 m/s（作弊）", "20 视图/表现", 54,
                PFViewDebugActions.TargetSpeedDown, null);
            AddAction("PFView·预生成当前选中编队的舱室地图（测试）", "20 视图/表现", 53,
                PFViewDebugActions.PregenerateRooms, null);
            AddCheckbox("PFView·放大部件（×200，调试）", "20 视图/表现", 52,
                typeof(MainTabWindow_PFView), "DebugScaleParts");
            AddCheckbox("PFView·太阳位置标记（黄块，调试）", "20 视图/表现", 51,
                typeof(MainTabWindow_PFView), "DebugShowSunMarker");

            AddAction("传感器/雷达投影验收（[V7]：站 + 两座建筑 → 投影 / W1 开机 / 打伤 / 摧毁 / 往返）", "00 验收", 34,
                SensorProjectionAcceptanceRunner.Run, null);
            // S1（2026-09-23）：统一判定框架的首两个用例 —— 把"只打印数字"的探针变成有判据的用例
            // （阵营解析 [V12] · 可见性四字段 [P1]；"今天验不了"的显式登记为 UNCHECKED 子项）
            AddAction("测试框架自检（统一判定：阵营解析 + 可见性四字段）", "00 验收", 30, FrameworkSelfTest.Run, null);
            // 批 0-4：≥32 组的可复现性（判 R1–R5 那批**只在 ≥32 组暴露**的并行竞态，靠它；
            //   同时也是批 2 的开工前提：不先建这个场景，改完也没有判据能证明改对了）
            AddAction("规模验收A（≥32 组 · 解析圆轨 · 不燃烧：连跑 3 次 · 指纹逐位相同）", "00 验收", 33,
                SwarmScaleAcceptanceRunner.Run,
                () => SwarmScaleAcceptanceRunner.Running
                    ? "规模验收（运行中…）"
                    : "规模验收A（≥32 组 · 解析圆轨 · 不燃烧）");
            // 变体 B（批 2）：非圆轨（三阶泰勒真积分）+ 全群同时点火 ⇒ 两样变体 A 给不了的：
            //   ① 位置/速度**依赖积分结果** ⇒ 丢掉一步 = 永久位置差（判据从"偶尔"变"必然"）；
            //   ② `HasActiveBurn` 每步为真 ⇒ R3 的静态节流 / R4 的 8 处日志 / R7 的引力探针真的被走到。
            AddAction("规模验收B（≥32 组 · 积分+全群燃烧：R3/R4 判据面）", "00 验收", 32,
                SwarmScaleAcceptanceRunner.RunIntegratedBurn,
                () => SwarmScaleAcceptanceRunner.Running
                    ? "规模验收（运行中…）"
                    : "规模验收B（≥32 组 · 积分+全群燃烧）");
            // N 扫描（2026-09-27 用户提议）：不同组数下的"每群每步"代价 ⇒ **斜率**才是"N 未知"要的答案。
            // [E3] 干扰机建筑（2026-10-02）：往站内放一座，并开关本站的干扰（看敌方观测方被按住）
            AddAction("来袭演示·我方干扰机开机 ⇄ 关机（[E3] 建筑级：抬高敌方阈值）", "00 验收", 15,
                SensorAmbushDemo.ToggleStationJammer, null);
            // 🎯 电子战**一键全测**（2026-10-02 用户要求）：五段依次跑完 + 合并结论
            AddAction("★ 电子战系统·一键全测（投影/能力面 → 认知层 → 视野对账 → 干扰与揭示 → 假目标）", "00 验收", 35,
                EwSystemAcceptanceRunner.Run,
                () => EwSystemAcceptanceRunner.Running ? "电子战全测（运行中…）" : "★ 电子战系统·一键全测");
            // [V7]+[V3] 来袭演示（2026-10-02，用户要的"看得见的场景"）：一键铺好"看不见的敌人"，
            //   再一键开机/点火让它当场现身（W1 枢纽的可视化）
            AddAction("来袭演示·布置（我方船/我方弹/敌船/敌弹×3：两枚远弹看不见）", "00 验收", 23,
                SensorAmbushDemo.Deploy,
                () => SensorAmbushDemo.Deployed ? "来袭演示（已布置）" : "来袭演示·布置");
            AddAction("来袭演示·敌远弹 B 开机 ⇄ 关机（看它现身/消失）", "00 验收", 22, SensorAmbushDemo.TogglePing, null);
            AddAction("来袭演示·敌远弹 C 点火 ⇄ 熄火（看它现身/消失）", "00 验收", 21, SensorAmbushDemo.ToggleBurn, null);
            AddAction("来袭演示·敌方干扰机 开机 ⇄ 关机（看近弹也被按住；公开组不受影响）", "00 验收", 20, SensorAmbushDemo.ToggleJam, null);
            AddAction("来袭演示·敌方投诱饵（[E5] 假接触：图上多一枚敌弹，它没有真值）", "00 验收", 19, SensorAmbushDemo.InjectDecoyDemo, null);
            AddAction("来袭演示·清诱饵", "00 验收", 18, SensorAmbushDemo.ClearDecoys, null);
            AddAction("诱饵验收（[E5]：依赖对方开机 / 识破 = 深层提示 / 生命期）", "00 验收", 17,
                SensorCognitionAcceptanceRunner.CheckDecoys, null);
            AddAction("来袭演示·打印花名册（谁可见、为什么）", "00 验收", 16, SensorAmbushDemo.PrintRoster, null);
            AddAction("来袭演示·清场", "00 验收", 19, SensorAmbushDemo.Cleanup, null);
            // [V3] 认知层验收（2026-10-02 S2）：观测者 + 匿踪目标，验 点亮/影子/失联 + 两个读入口 + 公开投影
            AddAction("认知层验收（[V3]：点亮 → 影子 → 失联 → 恢复 + 点查询不刷新 + 公开投影）", "00 验收", 28,
                SensorCognitionAcceptanceRunner.Run,
                () => SensorCognitionAcceptanceRunner.Running ? "认知层验收（运行中…）" : "认知层验收（[V3]）");
            AddAction("认知层验收·中止并清场", "00 验收", 27, SensorCognitionAcceptanceRunner.Abort, null);
            AddAction("EW 验收（干扰抬高阈值 → 按住目标 → 拉近烧穿 + 开火揭示 + 干扰机自暴露）", "00 验收", 27,
                SensorCognitionAcceptanceRunner.CheckJammingAndReveal, null);
            AddAction("视野对账（绘制/点击准入 = 认知层可见集；dev 真值开关开着时必红）", "00 验收", 26,
                SensorCognitionAcceptanceRunner.CheckViewReconciliation, null);
            AddAction("认知快照（接触/相位，存档往返用：点→存→读→再点，两行应一致）", "80 调试日志", 40,
                SensorCognitionAcceptanceRunner.PrintSnapshot, null);
            // 来袭性能场景（2026-10-02）：导弹群 + 舰队来袭的**整步**成本（基线 → 铺场 → 计量 → 自动清场）。
            // 想定 = 用户给的"每侧 1000 导弹 + 20 飞船"。⚠ 全程 Normal 档跑，别切快进（口径）。
            AddAction("来袭性能·小（2×(5 舰 + 100 弹) = 210 组）", "00 验收", 27,
                SwarmAttackPerfRunner.RunSmall,
                () => SwarmAttackPerfRunner.Running ? "来袭性能（运行中…）" : "来袭性能·小（210 组）");
            AddAction("来袭性能·中（2×(20 舰 + 500 弹) = 1040 组）", "00 验收", 26,
                SwarmAttackPerfRunner.RunMedium,
                () => SwarmAttackPerfRunner.Running ? "来袭性能（运行中…）" : "来袭性能·中（1040 组）");
            AddAction("来袭性能·全（2×(20 舰 + 1000 弹) = 2040 组 · 用户想定）", "00 验收", 25,
                SwarmAttackPerfRunner.RunFull,
                () => SwarmAttackPerfRunner.Running ? "来袭性能（运行中…）" : "来袭性能·全（2040 组）");
            AddAction("来袭性能·中止并清场", "00 验收", 24, SwarmAttackPerfRunner.Abort, null);
            // 跨过 32（顺序/并行分界）⇒ 顺带量出并行区实际收益；用常驻步钟 ⇒ 不用勾探针开关。
            AddAction("【06】规模验收·N 扫描（8/16/32/64/128 组：每群每步耗时与引力段）", "00 验收", 29,
                SwarmScaleAcceptanceRunner.RunScaleSweep,
                () => SwarmScaleAcceptanceRunner.Running
                    ? "【06】规模验收（运行中…）"
                    : "【06】规模验收·N 扫描");
            AddAction("规模验收B · 猎捕（连跑到抓到红为止，上限 10 次 ≈30 轮）", "00 验收", 30,
                SwarmScaleAcceptanceRunner.RunIntegratedBurnHunt,
                () => SwarmScaleAcceptanceRunner.Running
                    ? "规模验收（运行中…）"
                    : "规模验收B · 猎捕");
            // 连跑 4 次：专门抓那个**间歇**的指纹分歧（单次只有 3 轮 ⇒ 常常看不到）
            AddAction("规模验收B ×4 连跑（抓间歇指纹分歧：逐步定位报第几个物理时刻变化点）", "00 验收", 31,
                SwarmScaleAcceptanceRunner.RunIntegratedBurnX4,
                () => SwarmScaleAcceptanceRunner.Running
                    ? "规模验收（运行中…）"
                    : "规模验收B ×4 连跑");
            AddAction("G1 守卫自检（正负对照同场：相位内必报 / 相位外必不报）", "00 验收", 31,
                ParallelRegionGuard.SelfTest, null);
            AddAction("【01】一键验收", "00 验收", 100, PerfAcceptanceRunner.Run,
                () => PerfAcceptanceRunner.Running ? "【01】一键验收（运行中…）" : "【01】一键验收");
            AddAction("【02】中止验收（立即打印结论与计数）", "00 验收", 90, PerfAcceptanceRunner.Abort, null);
            AddAction("多part 一键验收", "00 验收", 58, MultipartAcceptanceRunner.Run, null);
            AddCheckbox("【07】性能优化验收探针（影子校验 + 周期汇总）", "00 验收", 80, typeof(PerfProbe), "Enabled");
            AddCheckbox("【04】tick 级日志（冲量/物理步/燃烧诊断/燃料账/点火；默认关）", "00 验收", 79, typeof(PerfProbe), "TickLog");
            // ★ 按需读"物理步总耗时"（常驻测量，与影子校验**无关** ⇒ 测耗时不会被观测者效应污染）：
            //   打印并清零 ⇒ 每次读数 = 上一份读数以来这些步的 平均/最慢/引力段占比。
            AddAction("【05】物理步耗时·打印并清零（平均/最慢 + 最慢那一步的 tick）", "00 验收", 79,
                () => { PerfProbe.LogStepClockNow(); }, null);
            AddAction("自检进度窗口（实时：阶段进度 + 引力读数 + 失败清单）", "00 验收", 70,
                Window_PFLSelfTest.OpenIfClosed, null);
            // 2026-10-16：本轮（反射清理 / 行为迁移）的可机判部分——只读 Def 表与组件表，不造景。
            AddAction("【03】机制在册自检（天空图层/地表层/地图组件/火箭链 def/相机驱动器——只读）", "00 验收", 69,
                RefactorMechanismsSelfTest.Run, null);
            // P1-8：规划器一键验收 —— 12 个操作各走一遍"参数描述 → 计算 → 入队"，
            // 打完判决块后**自动把规划器窗口开出来**（窗口长什么样只有人眼能判，探针明写 UNCHECKED）。
            AddAction("规划器一键验收", "00 验收", 60, PlanEditorAcceptanceRunner.Run, null);
            // 真飞验收（两按钮，因为"要等"）：唯一会**真的让船变轨**的验收工具。
            AddAction("真飞验收·布置（真排队，会真的变轨；烧完按「检查」）", "00 验收", 55,
                LiveBurnAcceptanceRunner.Stage, null);
            AddAction("真飞验收·检查（读实际轨道与已耗 Δv，对照期望出结论）", "00 验收", 54,
                LiveBurnAcceptanceRunner.Check, null);
            // 漂移检查（第三个按钮）：把"燃烧"整个移出变量 —— 只问"环境/动力学是否等于纯二体"
            AddAction("漂移检查·布置（放一条不烧的船，120 s 后自动对比纯二体传播）", "00 验收", 53,
                LiveBurnAcceptanceRunner.StageCoast, null);
            AddAction("矩阵对照·布置（4 轨道 × 基准/冻结方向/无保持 + 同轨重复，共 13 条；消除偶然）", "00 验收", 52,
                BurnABAcceptanceRunner.Stage,
                () => "4 条不同轨道 × 3 个变体 + 同轨重复：同轨重复给出噪声地板；变体效应必须在 4 条轨道上一致才算数。位置/半径/时间全错开，不互撞、不错峰。");
            AddAction("相位三点·布置（3 条船同轨同节点，只差点火相位 −4s/0s/自动；判「相位=残差」）", "00 验收", 50,
                BurnPhaseAcceptanceRunner.Stage,
                () => "台架给出相位→e 曲线（−4s→1.474e-3｜0s→5.96e-4）；若游戏在同一曲线上，残差就是点火相位的 tick 量化，B2.2 弧补偿即正解。船数=3 ⇒ 负载噪声回到 0.35%。");
            AddAction("相位三点·出表（手动）", "00 验收", 49,
                BurnPhaseAcceptanceRunner.Report, null);
            AddAction("矩阵对照·出表（手动，若自动未触发）", "00 验收", 51,
                BurnABAcceptanceRunner.Report, null);

            // ═══ 场景 · 引力场（A0 回归；判据见 PerfProbe 汇总行 ㉑㉒）═══
            AddAction("引力场自检电池（即时：数据路径 + 场数学 + A1 参数）", "05 场景 · 引力场(A0)", 110,
                GravityFieldSelfTest.Run, null);
            AddAction("月球引力回归（月球附近非圆轨群组）", "05 场景 · 引力场(A0)", 100,
                DebugShipSpawner.SpawnMoonGravityTest, null);
            AddAction("漏靶弹头（A2：段内插值飞行 + 引力弯曲）", "05 场景 · 引力场(A0)", 90,
                DebugShipSpawner.SpawnMissedWarheadTest, null);

            // ═══ 场景 · 着陆（2026-09-22：撞击事件 = "着陆 / 坠毁"的唯一事件源）═══
            AddAction("撞击事件（地表上方 200 m：20 m/s ⇒ 着陆｜300 m/s ⇒ 坠毁，消息带经纬度）", "05 场景 · 引力场(A0)", 78,
                DebugShipSpawner.SpawnImpactEventTest, null);
            AddAction("降落·报价探针（近地圆轨 + 绕月圆轨：星下点 vs 远处对照）", "05 场景 · 引力场(A0)", 77,
                DebugShipSpawner.SpawnLandingQuoteProbe, null);
            AddAction("降落·执行探针（对星下点落点真降一次：消息 + 扣料 + 销毁）", "05 场景 · 引力场(A0)", 76,
                DebugShipSpawner.RunLandingExecutionProbe, null);

            // ═══ 着陆判据（S3，2026-09-23）：把上面三个探针的"人读日志"变成机器判定（走统一框架）═══
            AddAction("降落/撞击·判据（S3：报价·距离门·跨层错配·越界·缺表面·执行三件事 + 撞击事件）",
                "05 场景 · 引力场(A0)", 74, RunLandingJudgments, null);
            AddAction("降落/撞击·判据中止", "05 场景 · 引力场(A0)", 73, LandingAcceptanceRunner.Abort, null);
            AddAction("带图接地测试（20 m/s：仅登记落点，地图手动请求）", "05 场景 · 引力场(A0)", 72,
                SurfaceTouchdownAcceptanceRunner.SpawnSafe, null);
            AddAction("带图高速坠毁回归（300 m/s）", "05 场景 · 引力场(A0)", 71,
                SurfaceTouchdownAcceptanceRunner.SpawnCrash, null);
            AddAction("打印带图接地任务与到达内容", "05 场景 · 引力场(A0)", 70,
                SurfaceTouchdownAcceptanceRunner.Report, null);

            // ═══ 场景 · 多part（批 0：惯量底座，2026-10-17）═══
            AddAction("多part 调试船",
                "07 场景 · 多part", 92, () => DebugShipSpawner.SpawnMultiPartDebugShip(), null);
            AddAction("打印全部编队惯量读数",
                "07 场景 · 多part", 90,
                () =>
                {
                    var sg = Current.Game?.GetComponent<SpaceGameComp>();
                    if (sg == null) return;
                    int n = 0;
                    foreach (var g in sg.GetAllGroups())
                    {
                        Log.Message($"[惯量读数] G{g.GroupId}: {g.InertiaDebugString()}");
                        n++;
                    }
                    Log.Message($"[惯量读数] 共 {n} 组；判据：**静止时再按一次，所有组的 重算# 都不应增长**");
                }, null);
            AddAction("多part 地图站",
                "07 场景 · 多part", 88, () => DebugShipSpawner.SpawnMultipartStationDebug(), null);
            AddAction("多part 双舱室船（B6：两图各挂一件舱盒）",
                "07 场景 · 多part", 89, () => DebugShipSpawner.SpawnDualCabinDebugShip(), null);
            AddAction("256 件基准船（B7：装配/惯量/碰撞/存档规模）",
                "07 场景 · 多part", 91, () => DebugShipSpawner.Spawn256PartDebugShip(), null);
            AddAction("256 件船飞行·一键（无Map 圆轨，自动顺行点火 15 m/s）",
                "07 场景 · 多part", 72, DebugShipSpawner.Spawn256PartFlightDemo, null);
            AddAction("256 件船对撞·一键（两艘 256 件，间距 60m / 6 m/s，约 3s）",
                "07 场景 · 多part", 71, DebugShipSpawner.Spawn256PartCollisionDemo, null);
            AddAction("256 件紧凑高速近失·一键（8×8×4，相对 45m/s；测 part 级 CCD）",
                "07 场景 · 多part", 93, DebugShipSpawner.SpawnCompact256CcdDemo, null);
            AddAction("多part 交会·一键（3 件追击 → 3 件目标，同轨 30° 相位）",
                "07 场景 · 多part", 70, DebugShipSpawner.SpawnMultipartRendezvousDemo, null);
            AddAction("交会·批量 20 组（20 份阶梯规划同拍到期；验预热 #序号曲线）",
                "07 场景 · 多part", 94, DebugShipSpawner.SpawnRendezvousBatch20, null);
            AddAction("256/多part 演示·清场（只清飞行/对撞/交会按钮生成的组）",
                "07 场景 · 多part", 69, DebugShipSpawner.CleanupStressDemos, null);
            AddAction("多part 多点对撞·一键（3 件×2 侧靠错位，专门触发多接触求解）",
                "07 场景 · 多part", 68, DebugShipSpawner.SpawnMultipartMultiContactDemo, null);
            AddAction("形状接触·一键（Sphere/Box 混合，低速对撞）",
                "07 场景 · 多part", 67, DebugShipSpawner.SpawnShapeContactDemo, null);
            AddAction("极端高速对撞（10 km/s：只判撞上，不追 part 精确）",
                "07 场景 · 多part", 66, DebugShipSpawner.SpawnExtremeSpeedCollisionDemo, null);
            AddAction("相邻格对穿（相对 10 km/s：一步 20km 互换格位，验证配对完备性）",
                "07 场景 · 多part", 65, DebugShipSpawner.SpawnAdjacentCellCrossingDemo, null);
            AddAction("同格 256 艘（独立单part，500m 间距；测宽相配对/枚举）",
                "07 场景 · 多part", 64, DebugShipSpawner.SpawnDenseSameCellDemo, null);
            AddAction("B8 表现验收·全部布置（残骸色 + 主推轴 + 双舱室）",
                "07 场景 · 多part", 77, DebugShipSpawner.SpawnAllB8Demos, null);
            AddAction("B8 表现验收·残骸分色（生成 3 件船并损毁第 2 件）",
                "07 场景 · 多part", 76, DebugShipSpawner.SpawnB8WreckColorDemo, null);
            AddAction("B8 表现验收·主推轴（无 Map 多引擎，清空旧 mainAxisShip）",
                "07 场景 · 多part", 75, DebugShipSpawner.SpawnB8MainAxisDemo, null);
            AddAction("B8 表现验收·多图入口（双舱室；旧面板应出「选择地图入口」）",
                "07 场景 · 多part", 74, DebugShipSpawner.SpawnB8MultiMapDemo, null);
            AddAction("B8 表现验收·清场（只清 B8 按钮生成的组）",
                "07 场景 · 多part", 73, DebugShipSpawner.CleanupB8Demos, null);
            AddAction("载荷舱·释放（选中的舰；首次自动注册调试载荷：控制核心+罐+引擎）",
                "07 场景 · 多part", 72, DeployPayloadForSelectedGroup, null);
            AddAction("载荷舱·套娃验收（自动建舰 → 两级释放 → 核对状态/件数）",
                "07 场景 · 多part", 71, PayloadNestingAcceptanceRunner.Run, null);
            AddAction("动量轮·三档验收（对称 / 偏置 / RCS 空）",
                "07 场景 · 多part", 70, WheelAcceptanceRunner.Run, null);
            AddAction("按级号抛级·验收（自动造三级舰 → 连抛两次 → 核对）",
                "07 场景 · 多part", 69, StageSeparationAcceptanceRunner.Run, null);
            AddAction("双天体·各 10 发爬升验收（地球+月球，纬度 −60°…+60°，自动结算）",
                "07 场景 · 多part", 65, DualBodyLaunchAcceptanceRunner.Stage, null);
            AddAction("三级火箭·升空序列（活动级供能 → 点火 → 抛级 → 切级 → 上面级）",
                "07 场景 · 多part", 68, ThreeStageLaunchAcceptanceRunner.Run, null);
            AddAction("三级火箭·地表发射演示（真起飞；每 15 s 自动抛一级）",
                "07 场景 · 多part", 67, SurfaceLaunchDemo.Run, null);
            AddAction("月球·自动发射演示（天体解析 + SOI 夹取 + 制导）",
                "07 场景 · 多part", 66, MoonLaunchAcceptanceRunner.Run, null);
            AddAction("多part 复杂碰撞·一键验收（4 组不同角度，自动结算角动量）",
                "07 场景 · 多part", 87, ComplexCollisionAcceptanceRunner.Stage, null);
            AddAction("低速对撞（4 m/s：看最小穿透轴/质量反比/e=0.2/角速度）",
                "07 场景 · 多part", 86, () => DebugShipSpawner.SpawnLowSpeedCollisionDemo(), null);
            AddAction("高速对撞（80 m/s：看爆炸）",
                "07 场景 · 多part", 85, () => DebugShipSpawner.SpawnHighSpeedCollisionDemo(), null);
            AddAction("预对准消旋演示·布置（船开始转）",
                "07 场景 · 多part", 84, () => DebugShipSpawner.SpawnPrealignCancelDemo(), null);
            AddAction("预对准消旋演示·取消（应停转）",
                "07 场景 · 多part", 83, DebugShipSpawner.CancelPrealignDemo, null);
            AddAction("交会引导切断演示·布置（2 引导 + 1 手动节点）",
                "07 场景 · 多part", 82, () => DebugShipSpawner.SpawnGuidanceCutDemo(), null);
            AddAction("交会引导切断演示·断开（旧节点应消失）",
                "07 场景 · 多part", 81, DebugShipSpawner.CutGuidanceDemo, null);
            AddAction("关闭碰撞详细日志（测完性能用）",
                "07 场景 · 多part", 80,
                () =>
                {
                    bool was = CombatManager.DebugDetailedCollision;
                    CombatManager.DebugDetailedCollision = false;
                    Log.Message($"[碰撞] 详细日志：{(was ? "已关闭（恢复生产性能口径）" : "本来就是关的")}");
                }, null);
            AddAction("贾尼别科夫演示·布置（中间轴自旋，看长轴翻转）",
                "07 场景 · 多part", 79, DzhanibekovDemo.Spawn, null);
            AddAction("贾尼别科夫演示·停止",
                "07 场景 · 多part", 78, DzhanibekovDemo.Stop, null);

            // ═══ 场景 · 大气（A1–A6；[A7] 悬浮落地后并入本类）═══
            AddAction("吸气式试验机（30 km 水平 400 m/s，空罐起飞：看充填 / 包线衰减 / 抽干熄火）",
                "06 场景 · 大气(A)", 90, () => DebugShipSpawner.SpawnAirBreathingTest(),
                () => "L1 无升力 ⇒ 它注定会落，这不是判据场景。三件该看的事：① 空气罐从 0 涨起来（充填）；" +
                      "② 推力随 ρ 衰减（包线 φ = ρ/ρ0）；③ 空气抽干 ⇒ [大气·进气] 熄火一行（该步燃料不扣）；" +
                      "飞进真空 ⇒ [大气·包线] 不路由一行（D5 网络门控）。");
            AddAction("赤道 × 本初子午线 测试船（0°N 0°E 上空 30 km：调试超级引擎 + 无限燃料 + 悬浮 + 操舵台）",
                "06 场景 · 大气(A)", 89, () => DebugShipSpawner.SpawnEquatorHoverTest(),
                () => "定点 (φ0 λ0) 的长期基准船（**有 Map**，带操舵台 ⇒ 可开「船上控制台」）。" +
                      "它用**调试超级引擎** `PF_Building_Thruster_TestSuper`（单台 300 MN）——因为地图站质量 = mapW×50×mapH×90 " +
                      "⇒ 60×60 站 = **16,200 t** ⇒ 悬停要 ≈128 MN，常规 RCS（4.5 kN/台）差 3 个数量级（那台\"推力不足⇒会掉\"的真因）。" +
                      "**台数按质量当场推**（1.6× 余量）。**无限燃料** = `debugInfinitePropellant`（唯一扣减口报\"已扣完\"但不动账，" +
                      "不是把罐加大——那会加重量）。");
            AddAction("悬浮试验机（30 km −20 m/s 起步，立刻开悬浮：看高度守住 + 燃料 16 kg/s）",
                "06 场景 · 大气(A)", 88, () => DebugShipSpawner.SpawnHoverTest(),
                () => "[A7] 定点保持 + 锁铅垂（D10），推力**只走全矢量 RCS**、不转船体。" +
                      "该看：高度守在 30 km（抖幅量级 = 死区 ≈ 几米）· 燃料 ≈|g|·m/ve ≈16 kg/s 下走 · " +
                      "上轴朝铅垂 · [悬浮] 读数每 10 游戏秒一行（需 DebugDetailedSteering）。" +
                      "⚠ RCS 给不出会报「推力不足」—— 那是如实报，不是 bug。");
            AddAction("悬浮·全部关闭（调试用；玩家向入口在操舵台/PFView 那条线）",
                "06 场景 · 大气(A)", 86, DebugShipSpawner.StopAllHover, null);
            AddAction("悬浮·切档（解析 ⇄ 物理；进出写 [档位] 日志，看两档耗料率是否同量级）",
                "06 场景 · 大气(A)", 84, DebugShipSpawner.ToggleHoverTier,
                () => "[A8] 判据 = 两档账单一致：解析档按 `m·g/ve` 记账，物理档真给推力。切到解析档后位置应被" +
                      "**钉死在定点**（高度差恒 0、无锯齿），耗料率应与物理档同量级；料尽会**显式退出**并报一行。");
            AddAction("有 Map 大气三件套（进气道/空气罐/吸气式发动机立在殖民地地图上；停着不进气=负对照）",
                "06 场景 · 大气(A)", 82, () => DebugShipSpawner.SpawnMapAeroTest(out _, out _, announce: true),
                () => "有 Map 侧的飞行中行为：停在地表 v_air≈0 ⇒ **不进气**（负对照）；给它空气相对速度后进气道才工作。" +
                      "跑批器已自动跑这一条（负+正对照），本按钮是手动看建筑用的。");
            AddAction("悬浮·抽空（抽到剩 60 kg：几秒内看「料尽 ⇒ 显式退出 ⇒ 自由落体」）",
                "06 场景 · 大气(A)", 83, () => DebugShipSpawner.DrainHoverFuel(),
                () => "试验机的料是**刻意短**的（≈190 s）——测试要的是几秒内看到状态，不是让船吊一刻钟。" +
                      "要看\"料尽\"这条路径，按本钮抽空即可（不用等它自己烧干）。");

            // ═══ 场景 · 轨道 / 空间站 ═══
            AddAction("部署空间站", "10 场景 · 轨道/空间站", 100, DebugShipSpawner.SpawnTestStation, null);
            AddAction("双引擎测试（核热 + 离子，网络独立选择）", "10 场景 · 轨道/空间站", 90, DebugShipSpawner.SpawnDualEngineTest, null);
            AddAction("架构验证测试（burn/储罐差值/合冲量）", "10 场景 · 轨道/空间站", 80, BurnArchitectureTest.Run, null);

            // ═══ 场景 · 炮塔（动能）═══
            AddAction("对射", "20 场景 · 炮塔（动能）", 100, DebugShipSpawner.SpawnTurretTest, null);
            AddAction("全方位六方位", "20 场景 · 炮塔（动能）", 90, DebugShipSpawner.SpawnTurretOmniTest, null);
            AddAction("精确炮击", "20 场景 · 炮塔（动能）", 80, DebugShipSpawner.SpawnTurretPrecisionTest, null);

            // ═══ 场景 · 炮塔（定向能）═══
            AddAction("对射", "30 场景 · 炮塔（定向能）", 100, DebugShipSpawner.SpawnRayTurretTest, null);
            AddAction("遮挡验证", "30 场景 · 炮塔（定向能）", 90, DebugShipSpawner.SpawnRayOcclusionTest, null);
            AddAction("旋转命中", "30 场景 · 炮塔（定向能）", 80, DebugShipSpawner.SpawnRayRotatedTest, null);
            AddAction("多目标齐射", "30 场景 · 炮塔（定向能）", 70, DebugShipSpawner.SpawnRayMultiTest, null);

            // ═══ 场景 · 无 Map 实体 / 姿态 ═══
            AddAction("无 Map 碰撞测试（扫掠膨胀盒）", "40 场景 · 无Map/姿态", 100, DebugShipSpawner.SpawnCollisionTest, null);
            AddAction("主推轴向 + RCS 燃烧", "40 场景 · 无Map/姿态", 90, DebugShipSpawner.SpawnAxialThrustTest, null);
            AddAction("操舵测试场景", "40 场景 · 无Map/姿态", 80, DebugShipSpawner.SpawnSteeringTest, null);

            // ═══ 放置 · 无 Map 编队（预设圆轨；三种都带 dV，可机动）═══
            AddAction("行星际演示：Rimata → 赭砂（日心转移 → 进 SOI 自动换系）", "45 放置 · 无Map编队", 110,
                InterplanetaryRouteDemo.SpawnRimataToZheSha,
                () => "铺在恒星帧日心转移轨道（已推出 Rimata SOI）；按提示加速到到达 tick，观察进赭砂 SOI 自动换系。");
            AddAction("关键点探针：Rimata/赭砂 7 个位置（盒内/盒外近旁/日心/表面）", "45 放置 · 无Map编队", 103,
                InterplanetaryRouteDemo.SpawnKeyPointProbes,
                () => "撒 7 个组并打印帧根/|posBody|/源表/盒外 gap；G 组应直接触发赭砂撞击结算。");
            AddAction("SOI 入射演示：赭砂（1.1R 外，10 km/s 向内，秒级进 SOI 换系）", "45 放置 · 无Map编队", 105,
                InterplanetaryRouteDemo.SpawnZheShaSoiEntry,
                () => "铺在赭砂 SOI 外 0.1R，相对速度 10 km/s 向内；开加速几秒后看 [换系] 日志。");
            AddAction("放置：近地轨道圆轨（地表 500km，顺行）", "45 放置 · 无Map编队", 100, DebugShipSpawner.PlaceLeoNoMapGroup, null);
            AddAction("放置：同步轨道圆轨（周期 = 天体自转周期）", "45 放置 · 无Map编队", 90, DebugShipSpawner.PlaceGeoSyncNoMapGroup, null);
            AddAction("放置：绕月轨道圆轨（月面 1800km）", "45 放置 · 无Map编队", 80, DebugShipSpawner.PlaceMoonOrbitNoMapGroup, null);
            // 绕月交会（2026-09-29 用户报障后加）：非根天体路径此前零覆盖（体心系 + 解析圆轨模式 + 天体偏移）
            // ⏱ **异轨是主档**（用户 2026-09-29："我需要的是异轨交会测试，你他妈怎么这么喜欢同轨"）：
            //   追击方与目标不同半径 ⇒ 霍曼有窗口、能扫到"等 0.00 圈" ⇒ 阶梯第一次规划就是直接霍曼，
            //   计划用时 7.7e3（升）/ 9.2e3（降）模拟秒 ⇒ 约 1 分钟真实出结论（离线读数见台架 G6）。
            //   同轨是**病理档**（Δa=0 ⇒ 霍曼无解 ⇒ 只能抬轨等一个会合周期）—— 留作对照，别拿它看结果。
            AddAction("绕月交会·【异轨·升】追击 2900km → 目标 3300km（直接霍曼 ≈1 分钟）", "45 放置 · 无Map编队", 46,
                () => DebugShipSpawner.SpawnMoonRendezvousDiffOrbitTest(2900.0), null);
            AddAction("绕月交会·【异轨·降】追击 3700km → 目标 3300km（直接霍曼 ≈1 分钟）", "45 放置 · 无Map编队", 45,
                () => DebugShipSpawner.SpawnMoonRendezvousDiffOrbitTest(3700.0), null);
            AddAction("绕月交会·判定（三条判据：都活着 / 到达判据 / 阶梯在位）", "45 放置 · 无Map编队", 41,
                MoonRendezvousAcceptanceRunner.Run, null);
            AddAction("绕月交会·【同轨·病理对照】3300↔3300（最多等一个会合周期 ≈4.5 分钟）", "45 放置 · 无Map编队", 40,
                () => DebugShipSpawner.SpawnMoonRendezvousTest(30.0), null);
            AddAction("放置：GTO 椭圆轨道目标（近地点 LEO／远地点同步）", "45 放置 · 无Map编队", 75, DebugShipSpawner.PlaceGtoNoMapGroup, null);
            // ⚠ 时间标注口径（2026-09-29 实测更正）：旧标"8× 约 N 分钟"用的是"8 倍速"，而实测
            //   **模拟:真实 ≈ 300:1**（36868 模拟秒 / 120 真实秒）⇒ 旧标注把时间**放大了 ~37 倍**。
            //   现在按同一套"要补的相位"规则给**粗估**（推导与实测见 `DebugShipSpawner.SpawnMoonRendezvousTest` 的文档）。
            AddAction("交会验收·相位 30°（同轨 ⇒ 要补 330° ⇒ 粗估 ~4 圈 ≈ 1.5 分钟真实）", "45 放置 · 无Map编队", 70,
                () => DebugShipSpawner.SpawnRendezvousTest(overThrust: false, phaseDegAhead: 30.0), null);
            AddAction("交会验收·相位 120°（同轨 ⇒ 要补 240° ⇒ 粗估 ~3 圈 ≈ 1 分钟真实）", "45 放置 · 无Map编队", 65,
                () => DebugShipSpawner.SpawnRendezvousTest(overThrust: false, phaseDegAhead: 120.0), null);
            AddAction("交会验收·高推船（相位 120°，160kN 执行者）", "45 放置 · 无Map编队", 60,
                () => DebugShipSpawner.SpawnRendezvousTest(overThrust: true, phaseDegAhead: 120.0), null);
            // 异轨（考**形状段**）：LEO → +1500km，Δv≈800 m/s 在预算内 ⇒ 能真的跑完"先变轨、再收相位、再终端"。
            // ⚠ LEO→同步轨道（+21322km）会被玩家路径的预算闸门正确拒掉（需 4911 vs 4385 m/s），
            //   要跑那个工况得先给船加料或用这个"缩小版"。
            AddAction("交会验收·变轨段（LEO → +1500km，考形状/霍曼段）", "45 放置 · 无Map编队", 55,
                () => DebugShipSpawner.SpawnRendezvousTest(overThrust: false, phaseDegAhead: 30.0, targetAltKm: 1500.0), null);
            // ⚠ 耗时很长（实测 2026-09-21）：45° 相位差 + 3° 异面 + 800 km 抬升
            //   三件叠在一起 ⇒ 阶梯会走 `匹配平面 → 调相轨道`，而调相就是“建轨道 + 等一圈”
            //   （本例轨道 ~6168 km ⇒ 一圈 **6800 模拟秒 ≈ 113 分钟**；8× 下约 **14 分钟真实时间**）。
            //   **等不到那么久就不要跑这条** —— 上一轮就是“船还在等 T-733s 的节点”时被关掉的，交会段没开始 ✗。
            //   想快验收用下面那条（同轨 30°，不需要调相）。
            // 异轨 + **异面**：阶梯「匹配平面」那一级唯一会上场的几何（同面时它永远不被选中）
            // ⇒ 这条场景不跑，那一级在游戏内就是零覆盖（只有台架/纯测试覆盖过）。
            // ⚠ **必须用高推船**（`overThrust: true`，160 kN ⇒ a≈9.1 m/s²）：标准船 0.91 m/s² 做这条变轨要烧
            //   ~700 s（Θ≈0.77）⇒ 被分派正确地送去**连续律**，阶梯还是不会被选中 ✗（实测：上一轮
            //   `[NewLaw]` 两段 + 阶梯 0 行）。高推下 Θ≈0.03 ⇒ 才真的考到「匹配平面 + 霍曼/相位」。
            AddAction("交会验收·异面异轨（高推船 +800km 且倾角 3°，考匹配平面+转移）", "45 放置 · 无Map编队", 50,
                () => DebugShipSpawner.SpawnRendezvousTest(overThrust: true, phaseDegAhead: 45.0,
                    targetAltKm: 800.0, targetIncDeg: 3.0), null);
            // 同一几何、**标准推力**（走连续区）—— 不是把上面那条改软，而是把**同一几何的另一区制**也纳入覆盖 ✓
            AddAction("交会验收·异面异轨（标准推力 +800km 且倾角 3°，同样走阶梯）", "45 放置 · 无Map编队", 45,
                () => DebugShipSpawner.SpawnRendezvousTest(overThrust: false, phaseDegAhead: 45.0,
                    targetAltKm: 800.0, targetIncDeg: 3.0), null);

            // ═══ 场景 · 导弹（自主武器平台）═══
            AddAction("发射测试（20km，Following 待命）", "50 场景 · 导弹", 100, DebugShipSpawner.SpawnMissileLaunchTest, null);
            AddAction("撞击测试（1.5km，单发）", "50 场景 · 导弹", 95, DebugShipSpawner.SpawnMissileImpactTest, null);
            AddAction("巡航→终末", "50 场景 · 导弹", 90, DebugShipSpawner.SpawnMissileCruiseTest, null);
            AddAction("拦截对撞（反导 3 波）", "50 场景 · 导弹", 85, DebugShipSpawner.SpawnMissileInterceptTest, null);
            AddAction("弹群齐射→解离（N=8）", "50 场景 · 导弹", 80, DebugShipSpawner.SpawnVolleyImpactTest, null);
            AddAction("弹群合并期被撞", "50 场景 · 导弹", 75, DebugShipSpawner.SpawnVolleyMergedHitTest, null);
            AddAction("长距制导（60km）", "50 场景 · 导弹", 70, DebugShipSpawner.SpawnMissileLongRangeTest, null);
            AddAction("销毁来袭弹（测中止）", "50 场景 · 导弹", 65, DebugShipSpawner.DestroyIncomingMissiles, null);
            AddAction("导弹控制面板", "50 场景 · 导弹", 60, () => Find.WindowStack.Add(new Window_MissileControl()), null);

            // ═══ 场景 · 跳帮 / 发射面板 ═══
            AddAction("单兵 EVA。跳帮测试", "60 场景 · 跳帮", 100, DebugShipSpawner.SpawnBoardingTest, null);
            AddAction("跳帮测试面板", "60 场景 · 跳帮", 90, () => Find.WindowStack.Add(new Window_BoardingTest()), null);
            AddAction("发射测试面板", "70 场景 · 发射", 100, () => Find.WindowStack.Add(new Window_LaunchTest()), null);

            // ═══ 场景 · 运行时 PlanetLayer ═══
            AddAction("创建测试地表（PlanetLayer）", "75 场景 · PlanetLayer", 100,
                RuntimePlanetLayerDebugActions.CreateTestLayer, null);
            AddAction("选择查看 PlanetLayer", "75 场景 · PlanetLayer", 90,
                RuntimePlanetLayerDebugActions.ChooseLayerToView, null);
            AddAction("删除空 PlanetLayer（保留主地表/轨道）", "75 场景 · PlanetLayer", 80,
                RuntimePlanetLayerDebugActions.ChooseLayerToRemove, null);
            AddAction("打印 PlanetLayer 状态", "75 场景 · PlanetLayer", 70,
                RuntimePlanetLayerDebugActions.PrintLayers, null);

            AddAction("PF_RingCreateSmall".Translate(), "76 场景 · 环世界", 100, RingWorld.RingWorldDebugActions.CreateSmall, null);
            AddAction("PF_RingCreateLarge".Translate(), "76 场景 · 环世界", 95, RingWorld.RingWorldDebugActions.CreateLarge, null);
            AddAction("PF_RingView".Translate(), "76 场景 · 环世界", 90, RingWorld.RingWorldDebugActions.View, null);
            AddAction("PF_RingFocus".Translate(), "76 场景 · 环世界", 85, RingWorld.RingWorldDebugActions.FocusSelected, null);
            AddAction("PF_RingEnterMap".Translate(), "76 场景 · 环世界", 80, RingWorld.RingWorldDebugActions.EnterSelectedMap, null);
            AddAction("PF_RingValidate".Translate(), "76 场景 · 环世界", 75, RingWorld.RingWorldDebugActions.Validate, null);
            AddAction("PF_RingRemove".Translate(), "76 场景 · 环世界", 70, RingWorld.RingWorldDebugActions.Remove, null);
            AddAction("PF_Mega_DebugOpen".Translate(), "78 场景 · 通用巨构", 100, delegate
            {
                var tab=DefDatabase<MainButtonDef>.GetNamedSilentFail("PF_Megastructures");
                if(tab!=null)Find.MainTabsRoot.SetCurrentTab(tab);
            }, null);

            // ═══ 场景 · 星座（组织层）═══
            AddAction("星座自检（API 全链）", "90 场景 · 星座", 100, ConstellationSelfTest.Run, null);

            // ═══ 调试日志开关（直接绑定现有 static 字段）═══
            AddCheckbox("详细战斗日志（含射弹探针 30t）", "80 调试日志", 100, typeof(CombatManager), "DebugDetailedCombat",
                afterToggle: () => CombatManager.DebugProjectileProbeInterval = CombatManager.DebugDetailedCombat ? 30 : 0);
            AddCheckbox("详细碰撞日志（扫掠盒/枚举）", "80 调试日志", 95, typeof(CombatManager), "DebugDetailedCollision");
            // 2026-11-01：量生产口径用——保留 [碰撞链成本] 的外层读数，关掉逐对子段钟（否则 1,760 对 × 6 次 Stopwatch 会虚增 ~200µs/步）。
            AddCheckbox("碰撞链成本·关子段钟（生产口径）", "80 调试日志", 93, typeof(PerfProbe), "CollisionStagesForceOff");
            // [V3]（2026-10-02 用户要求）：开启后绘制/点击**绕过认知层**，直接看真实棋子（调试/对比用）
            AddCheckbox("显示真实棋子（绕过认知层 · dev）", "80 调试日志", 92, typeof(SensorManager), "DebugShowGroundTruth");
            AddCheckbox("详细操舵日志（[Steer]/[Align]）", "80 调试日志", 90, typeof(SpaceGameComp), "DebugDetailedSteering");
            AddCheckbox("详细推进日志（无Map主推/RCS [Thrust]）", "80 调试日志", 85, typeof(SpaceGameComp), "DebugDetailedThrust");
            AddCheckbox("详细导弹制导日志", "80 调试日志", 80, typeof(AutonomousVehicleManager), "DebugDetailedGuidance");
            AddCheckbox("详细跳帮日志", "80 调试日志", 75, typeof(BoardingManager), "DebugDetailedBoarding");
            AddCheckbox("详细发射日志", "80 调试日志", 70, typeof(RocketLaunchController), "DebugDetailedLaunch");
            // [V12]（2026-09-23）：阵营解析读数 —— 把"解析失败"的限频警告变成可查的一行
            AddAction("阵营解析读数（[V12]：敌对阵营 / 解析失败累计）", "80 调试日志", 68, FactionResolver.LogProbe, null);

            // [V7] 传感器读数（2026-09-24）：逐部件列组件 + 实体级聚合 + 编队级派生 DetectTech 三档一起看，
            // 用来核"给量纲"这条链（机器判据在 `FrameworkSelfTest`：编队级 = 实体级求和）。
            AddAction("传感器读数（[V7]：选中编队 → 部件组件 / 实体聚合 / 编队派生）", "80 调试日志", 67,
                () =>
                {
                    var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
                    if (spaceGame == null)
                    {
                        Messages.Message("不在太空场景（无 SpaceGameComp）", MessageTypeDefOf.RejectInput);
                        return;
                    }
                    var g = spaceGame.GetGroupById(SpaceGroupVisualManager.GetSelectedGroupId());
                    if (g == null)
                    {
                        Messages.Message("先在世界上选中一个编队", MessageTypeDefOf.RejectInput);
                        return;
                    }

                    var sb = new System.Text.StringBuilder();
                    sb.Append("[V7·传感器] G").Append(g.GroupId)
                      .Append(" 实体 ").Append(g.GetEntityIds().Count).Append(" 个");
                    foreach (int eid in g.GetEntityIds())
                    {
                        var e = spaceGame.GetEntityById(eid);
                        if (e == null) continue;
                        var agg = e.AggregatedStats;                 // 触发实体级懒聚合
                        sb.Append("\n  E").Append(eid)
                          .Append(" 实体聚合：灵 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{agg.sensorRange:F0}").Append(" m")
                          .Append("｜抗压 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{agg.ecmResist:F2}")
                          .Append("｜抗骗 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{agg.eccmResist:F2}");
                        foreach (var part in e.Parts)
                        {
                            if (part.isDestroyed) continue;
                            for (int i = 0; i < part.components.Count; i++)
                                sb.Append("\n    part#").Append(part.partId).Append(" ← ").Append(part.components[i].TypeId);
                        }
                    }
                    var dt = g.DetectTech;                            // 编队级**派生量**（与 SignalStrength 同趟重算）
                    sb.Append("\n  **编队派生 DetectTech**：灵 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{dt.sensitivityMeters:F0}").Append(" m")
                      .Append("｜抗压 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{dt.ecmResist:F2}")
                      .Append("｜抗骗 ").Append(System.Globalization.CultureInfo.CurrentCulture, $"{dt.eccmResist:F2}")
                      .Append("｜信号 S=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{g.SignalStrength:F3}");
                    Log.Message(sb.ToString());
                }, null);
            AddCheckbox("详细星座日志（入编/收尸/锚点）", "80 调试日志", 60, typeof(ConstellationGameComp), "DebugDetailedConstellation");
            // ⚠ 标签 2026-09-28 更正：原写"交会用新远场律（RendezvousGuidanceLaw）"——那套律已作废、
            //   远场段与 5% CW 终端已删除。字段名沿用（跨文件反射引用，改名要一次改两处）。
            AddCheckbox("交会引导=阶梯（默认开；关=回退旧巡航 Lambert 路径）", "80 调试日志", 58, typeof(SpaceGameComp), "UseNewRendezvousLaw");
            // 2026-10-21（用户令"不允许增加性能消耗"）：闭式霍曼的影子对照**默认关** ——
            // 打开才每次闭式计划多跑一次搜索（≈1.7–2.7 ms），用来在游戏内自证"闭式 ≈ 搜索"；查完关掉。
            AddCheckbox("闭式霍曼·影子对照（默认关；开=每次闭式计划多一次搜索 ≈2ms，查完关掉）", "80 调试日志", 57,
                typeof(SpaceGameComp), "DebugShadowCompareClosedForm");
            AddAction("射弹飞行探针（30 tick / 关）", "80 调试日志", 65, ToggleProjectileProbe,
                () => CombatManager.DebugProjectileProbeInterval > 0
                    ? "射弹飞行探针：开（" + CombatManager.DebugProjectileProbeInterval + "t）"
                    : "射弹飞行探针：关");

            // 可见性快照（[V1] 探针）：**一次性动作**而非勾选框——验"存档往返存活"只需
            // "点一次 → 存档 → 读档 → 再点一次 → 对比两份日志"，不需要持续刷屏。
            AddAction("打印可见性快照（匿踪 / 侦测技术）", "80 调试日志", 55, VisibilityProbe.PrintSnapshot, null);

            // OrbitView / F1 换系自检（P0 适配层）：验收"游戏 +Y 极轴 ↔ 适配层 +z 极轴"那一次换系。
            // 一次性动作：跑一次看结论行是 PASS 还是 FAIL。
            AddAction("OrbitView/F1 换系自检（打印快照）", "00 验收", 60, OrbitViewProbe.PrintSnapshot, null);

            // P0 适配层端到端：选中一个编队 → OrbitView → OperationCircularize → 机动队列。
            // ⚠ 它会**真的往队列里排一条机动**（这就是"端到端"）；预测值先打在日志里，可只读不点。
            AddAction("圆化选中编队（OrbitView → OperationCircularize → 队列）", "00 验收", 55,
                OperationProbe.CircularizeSelectedGroup, null);

            // ⭐ P0 适配层**一键验收**：一次点击 = 铺 4 个受控轨道场景 + 跑全套判据 + 打结论块。
            // 不需要事先选中任何东西；会铺场并真的排一条机动（见 runner 的类注释）。
            AddAction("适配层一键验收", "00 验收", 65, AdapterAcceptanceRunner.Run, null);

            return myRoot;
        }

        private static void ToggleProjectileProbe()
        {
            CombatManager.DebugProjectileProbeInterval = CombatManager.DebugProjectileProbeInterval > 0 ? 0 : 30;
        }

        /// <summary>
        /// 降落/撞击判据（S3）：**同步段立刻出结论块**（报价/距离门/跨层错配/越界/缺表面/执行三件事），
        /// 撞击事件登记为**异步用例**（两颗探针落地后自己结算并打一行「用例…结算」）——所以结论块打完不等于
        /// 全跑完：那行会晚几秒出现。
        /// </summary>
        private static void RunLandingJudgments()
        {
            PflTestKit.Reset();
            LandingAcceptanceRunner.RunSync();
            LandingAcceptanceRunner.StageImpact();
            PflTestKit.PrintVerdict("降落/撞击判据（S3）");
        }

        /// <summary>
        /// PFView 天体地表**补丁烘焙**（2026-10-10 用户口径："给个按钮，点开选天体，手动烘焙；结果跟存档走；不自动烘"
        /// + "只烘焙有 tile 覆盖的区域，其他的部分用基底填充"）。
        /// 选完当场同步烤（几十~几百 ms），落旁挂 PNG（`SaveDataFolderPath/PF_SurfaceBake/<世界种子>_<bodyId>_patch.png`）。
        /// </summary>
        private static void BakeSurfaceMenu()
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null)
            {
                Messages.Message("不在太空场景（无 CelestialBodyManager）", MessageTypeDefOf.RejectInput, false);
                return;
            }

            var options = new System.Collections.Generic.List<FloatMenuOption>();
            foreach (var body in mgr.GetAllBodies())
            {
                var b = body;
                var layer = GetSurfaceState.GetLayerForBody(mgr, b.bodyId);
                string label = layer == null
                    ? $"{b.defName}（{b.bodyType}；无地表层，不可烤）"
                    : $"{b.defName}（{b.bodyType}；层格 {layer.TilesCount}）";
                options.Add(new FloatMenuOption(label, delegate
                {
                    PFViewSurfaceBake.TryBake(b, layer, Find.World.info.Seed, out string? msg);
                    Messages.Message(msg ?? "烘焙返回空消息", MessageTypeDefOf.TaskCompletion, false);
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private DebugActionNode AddAction(string label, string category, int priority, Action action, Func<string>? labelGetter)
        {
            // ⚠ **RimWorld 的 `DebugActionNode.LabelNow` 优先用 `labelGetter()` 作为显示文字**（不是 tooltip！）
            //   ⇒ 固定文案传 null；只有"运行中…/已布置"这类动态状态才用它。
            //   长说明请写日志/计划，别塞 getter（2026-10-17 用户实测："菜单里的字和你给我的不一样"即此）。
            var node = new DebugActionNode(label, DebugActionType.Action, action, null);
            node.category = category;
            node.displayPriority = priority;
            if (labelGetter != null) node.labelGetter = labelGetter;
            myRoot.AddChild(node);
            return node;
        }

        /// <summary>复选框节点。settingsField 只影响渲染（框架画成勾选框），切换动作由本类给出，
        /// 因此 afterToggle 可做联动（如同时设/清射弹探针间隔）。</summary>
        private DebugActionNode? AddCheckbox(string label, string category, int priority, Type type, string fieldName, Action? afterToggle = null)
        {
            var fi = type.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (fi == null || fi.FieldType != typeof(bool))
            {
                Log.Error($"[PFL] 调试页签：{type.Name}.{fieldName} 不是可用的 static bool 字段，节点「{label}」已跳过");
                return null;
            }

            var node = new DebugActionNode(label, DebugActionType.Action, delegate
            {
                bool now = (bool)fi.GetValue(null);
                fi.SetValue(null, !now);
                afterToggle?.Invoke();
            }, null);
            node.category = category;
            node.displayPriority = priority;
            node.settingsField = fi;
            myRoot.AddChild(node);
            return node;
        }

        /// <summary>载荷舱释放（2026-10-26）：选中的舰 → 找 payload-bay 部件 → 未绑设计则注册一个调试载荷 → 部署。</summary>
        private static void DeployPayloadForSelectedGroup()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var g = sg?.GetGroupById(SpaceGroupVisualManager.GetSelectedGroupId());
            if (sg == null || g == null)
            {
                Log.Message("[载荷舱] 先选中一条舰。");
                return;
            }

            SpaceEntity? owner = null;
            SpacePart? bay = null;
            foreach (int eid in g.GetEntityIds())
            {
                var e = sg.GetEntityById(eid);
                if (e == null) continue;
                for (int i = 0; i < e.Parts.Count; i++)
                {
                    var p = e.Parts[i];
                    if (p.GetComponent<Components.PayloadBayComponentData>() != null) { owner = e; bay = p; break; }
                }
                if (bay != null) break;
            }
            if (bay == null)
            {
                Log.Message("[载荷舱] 选中的舰没有载荷舱（category=payload-bay）部件。");
                return;
            }

            var comp = bay.GetComponent<Components.PayloadBayComponentData>()!;
            if (comp.payloadDesignId <= 0)
            {
                var design = new GroundAssembly.RocketDesignSpec
                {
                    parts = new System.Collections.Generic.List<GroundAssembly.DesignPartSpec>
                    {
                        new() { defName = "PF_ShipPart_ControlCore_Debug", category = "control", quantity = 1 },
                        new() { defName = "PF_ShipPart_Tank_Debug", category = "tank", quantity = 1 },
                        new() { defName = "PF_ShipPart_Thruster_Debug", category = "thruster", quantity = 1 },
                    },
                };
                comp.payloadDesignId = sg.RegisterPayloadDesign(design);
                Log.Message($"[载荷舱] 已注册调试载荷设计 id={comp.payloadDesignId}");
            }

            var ng = sg.TryDeployPayload(g, bay, out string reason);
            Log.Message(ng != null ? $"[载荷舱] 已释放 → G{ng.GroupId}" : $"[载荷舱] 释放失败：{reason}");
        }
    }
}
