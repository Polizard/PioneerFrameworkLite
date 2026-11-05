using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **大气 / 悬浮 / 档位（L1）自动判据跑批器**（2026-10-07 建，起因：用户"你能不能把测试变成自动化的，
    /// 因为我肯定会耽误时间"）。
    ///
    /// ## 它替掉的是什么
    /// L1（[A1]–[A8]）此前**大半靠人读日志**（"看 [大气·进气] 熄火一行"、"看 [悬浮] 区间 min/max"）——
    /// 那种验证的代价是**人得坐在屏幕前盯**，而且读数对不对还得再找一次人。本跑批器把那些读数
    /// **变成断言**：判据由程序自己算、自己判、自己打结论（`PflTestKit`），人只需**点一下**。
    ///
    /// ## 跑什么（一条按钮，全部自动）
    /// **同步段**（几毫秒出结论）：解析档**准入拒绝**（无全矢量网 ⇒ 必须拒，付不起不接）· 其**对照**（有网 ⇒ 接）·
    ///   **解析轨与气动互斥**（ρ>0 时不许进解析圆轨）。
    /// **异步段**（一个用例，顺序推进；物理按 60× 压缩 ⇒ 整段约几秒真实时间）：
    ///   ① **进气充填**：空罐起飞 ⇒ 数十秒后空气罐必须有料（`ρ·v·A` 那条路真在跑）；
    ///   ② **包线**：同一时刻的燃烧推力必须**显著低于真空标称** `ve·ṁ`（30 km 处 φ = ρ/ρ₀ ≈ 0.02）；
    ///   ③ **物理档悬浮**：高度差收敛到十米内、横向收敛、料在掉、`hoverActive` 仍为真；
    ///   ④ **切解析档**：**位置误差 < 1 m**（定点解析=钉死）· **定点未被重锁**（λ0/高度不变）· 耗料率 ≈ `m·g/ve`；
    ///   ⑤ **切回物理档**：仍在悬浮且未重新锁点；
    ///   ⑥ **料尽 ⇒ 显式退出**：抽空后 `hoverActive` 必须**自动变假**（不静默、不留"免费档"）。
    /// 收工清场（只清本段新建的组）。
    ///
    /// ## 纪律
    /// · **一条用例装全部**（`PflTestKit` 登记表只有一个槽位 ⇒ 异步段只能有一个）；
    /// · **没有证据的通过等于没测**：每条都带读数明细，够不到前置的判 `UNCHECKED` 并说清为什么；
    /// · 每条判据都是**量**（米 / kg / kg/s / 比值），不是"看着对"。
    /// </summary>
    public static class AtmosphereAcceptanceRunner
    {
        private const string Category = "00 验收";
        private const string CaseName = "大气/悬浮/档位（L1：充填·包线·悬浮·两档·料尽退出）";

        private const int EARTH_BODY_ID = 1;

        /// <summary>跑批器用的试验船料量（kg）：够走完整条链（物理 150 + 解析 50 + 回物理 25 + 抽空）。</summary>
        private const double RunnerFuelKg = 6000.0;

        private enum Phase { Idle, Intake, HoverPhysical, HoverAnalytic, HoverBack, HoverDrain, HoverTakeover, MapAero, Transfer, Done }

        private static Phase stage = Phase.Idle;
        private static int stageStartTick;
        private static int hoverGroupId = -1;
        private static int intakeGroupId = -1;

        // 切档前的定点（判"切档不重锁"）
        private static double latBefore, lonBefore, altBefore;
        // 解析档起点（判"两档账单一致"：解析档实测 kg/s ↔ m·g/ve）
        private static double analyticStartFuel, analyticStartTick;
        private static double analyticMass;

        /// <summary>
        /// 每阶段上限，单位 = **物理秒**。
        /// ⚠ **本 mod 里"物理 tick"就是"物理秒"**（燃烧实测：t 走 876→982 = 106，恰是 106 秒的燃烧）
        /// —— 第一版我按"900 tick ≈ 15 秒"写，实际是**900 秒** ⇒ 程序在那儿干等 2700 秒，
        /// 而试验机的料只够 188 秒 ⇒ 船当面烧干坠地，判据还没到点（实测抓到的）。
        /// 现在的取值对着**实测的时间线**：收敛 ~60 s、切档 20 s 足够、抽空后 10 s 内必退。
        /// </summary>
        private const int IntakeTicks = 40;          // 空罐起飞 → 充填可见
        private const int HoverPhysicalTicks = 150;  // −20 m/s 起步 → 收敛（实测 ~60 s，留一倍余量）
        private const int HoverAnalyticTicks = 50;
        private const int HoverBackTicks = 25;
        private const int HoverDrainTicks = 25;
        /// <summary>[D7 触发①] 操舵台接管 ⇒ 退出悬浮：铺船 + 开悬浮 + 给一次操舵输入 + 看它有没有退。</summary>
        private const int HoverTakeoverTicks = 10;
        /// <summary>[A3] 有 Map 侧飞行中行为：负对照（停着不进气）+ 给空速（进气/耗气/耗料）。
        /// ⚠ 预算给足 **150 物理秒**：地图主推要先对准（默认轴 Back ⇒ 与 burn 方向差 90°）才出力，
        ///   对准 + 点火 + 抽气都要时间 —— 40 秒那版会把"还没对准"误判成"没在烧"。</summary>
        private const int MapAeroTicks = 150;
        /// <summary>
        /// [A8] 解析搬运的**兜底**预算（物理秒）。
        /// ⚠ 真实等待**跟搬运单走**（`transferDuration + 余量`）—— 实测抓过一次：我按"Δv 400 / a≈1"拍 480 s，
        ///   而这艘船可用 a 只有 **0.543 m/s²** ⇒ 核心算出的时长是 **736.8 s** ⇒ 判据在"还在搬"时到点判红。
        ///   **预算要跟着生产量走，不许拍**（"自己的算术"那类仪器事故的又一例）。
        /// </summary>
        private const int TransferTicks = 900;
        private const int CaseTimeoutTicks = IntakeTicks + HoverPhysicalTicks + HoverAnalyticTicks + HoverBackTicks + HoverDrainTicks + HoverTakeoverTicks + MapAeroTicks + TransferTicks + 400;

        private static double lastProgressTick;

        /// <summary>本子阶段的预算（物理秒；只给进度读数用）。</summary>
        private static double BudgetOf(Phase p)
        
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      => p switch
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      {
            Phase.Intake => IntakeTicks,
            Phase.HoverPhysical => HoverPhysicalTicks,
            Phase.HoverAnalytic => HoverAnalyticTicks,
            Phase.HoverBack => HoverBackTicks,
            Phase.HoverDrain => HoverDrainTicks,
            Phase.HoverTakeover => HoverTakeoverTicks,
            Phase.MapAero => MapAeroTicks,
            Phase.Transfer => TransferTicks,
            _ => 0.0,
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      };

        private static double PhysTick()
        {
            var tsm = Current.Game?.GetComponent<TimeScaleManager>();
            return tsm != null ? tsm.GetPhysicsTick() : (Find.TickManager?.TicksGame ?? 0);
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  同步段
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>同步判据（几毫秒出结论）。返回（判据数, 失败数）。</summary>
        public static void RunSync()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null)
            {
                PflTestKit.Unchecked("大气/档位（同步）", "没有游戏（要在一盘存档里跑）");
                return;
            }

            // ── ① 解析档准入：无全矢量网必须**拒绝**（D8"付不起直接拒绝"），有网必须**接受**（对照组）──
            PflTestKit.Begin("大气·解析档准入（无网拒绝 / 有网接受）", Category);
            {
                var mgr0 = Current.Game?.GetComponent<CelestialBodyManager>();
                var body0 = mgr0 != null ? mgr0.GetBodyById(EARTH_BODY_ID) : null;
                double r0 = (body0 != null ? body0.radius : SpaceConstants.PlanetRadiusMeters) + 400_000.0;
                var pos0 = new DVector3(r0, 0, 0);
                double mu0 = SpaceConstants.PrimaryGravitationalParameter;
                var vel0 = DVector3.Cross(pos0, DVector3.up).normalized * Math.Sqrt(mu0 / r0);   // 顺行圆速（同其它放置点）

                // 甲：把 RCS 网**摘掉**（改判 isVectoredRcs=false ⇒ 退化成主网）⇒ 解析档必须拒
                var grpA = DebugShipSpawner.CreateNoMapShipGroup(spaceGame, EARTH_BODY_ID, pos0, vel0,
                    out int entA, overThrust: false, circularOrbit: true);
                if (grpA == null)
                {
                    PflTestKit.CheckUnchecked("前置：对照船", "CreateNoMapShipGroup 返回空 ⇒ 解析档准入判不了");
                }
                else
                {
                    var ea = spaceGame.GetEntityById(entA);
                    if (ea != null)
                    {
                        for (int i = 0; i < ea.propulsion.Count; i++)
                            if (ea.propulsion[i] != null && ea.propulsion[i].isVectoredRcs) ea.propulsion[i].isVectoredRcs = false;
                        ea.InvalidatePropulsion();
                    }
                    bool refused = !spaceGame.SetHover(grpA, true, analytic: true);
                    PflTestKit.Check(PflTestKit.Kind.Value, refused,
                        "无全矢量网 ⇒ 拒绝进解析档（付不起不接；D8 先例）");
                    spaceGame.SetHover(grpA, false);

                    // 乙（对照）：同一艘船**保留** RCS 网 ⇒ 必须接受（否则"拒"可能只是别的原因拒的）
                    var grpB = DebugShipSpawner.CreateNoMapShipGroup(spaceGame, EARTH_BODY_ID, pos0, vel0,
                        out int _, overThrust: false, circularOrbit: true);
                    if (grpB == null)
                        PflTestKit.CheckUnchecked("对照组：有网船", "第二艘对照船没建起来");
                    else
                    {
                        bool accepted = spaceGame.SetHover(grpB, true, analytic: true);
                        PflTestKit.Check(PflTestKit.Kind.Value, accepted, "有全矢量网 ⇒ 接受解析档（对照必须过）");
                        spaceGame.SetHover(grpB, false);
                        spaceGame.DestroyGroup(grpB.GroupId);
                    }
                    spaceGame.DestroyGroup(grpA.GroupId);
                }
            }
            PflTestKit.End();

            // ── ①b 有 Map 侧三件套的**数据一致性**（2026-10-07 接线；防 Def 以后悄悄漂掉）──
            //    为什么不在这里跑"飞行中的有 Map 用例"：地图组的空速来自它的轨道速度 ⇒
            //    停在地表的站 v_air≈0（**如实不进气**），500 km 上 ρ=0 —— 要跑通得先有一艘**飞着的地图船**
            //    （= 发射场景那套）。⇒ 那一层留给发射场景与常规一键验收；这里只把"数据面"钉住。
            PflTestKit.Begin("大气·有 Map 三件套（进气道/空气罐/吸气式发动机）数据一致性", Category);
            {
                var thrDef = DefDatabase<ThingDef>.GetNamedSilentFail("PF_Building_Thruster_AirBreathing");
                var tankDef = DefDatabase<ThingDef>.GetNamedSilentFail("PF_Building_Tank_IntakeAir");
                var intakeDef = DefDatabase<ThingDef>.GetNamedSilentFail("PF_Building_IntakeAir");
                var airDef = StandardPropulsion.IntakeAirDef;
                if (thrDef == null || tankDef == null || intakeDef == null || airDef == null)
                {
                    PflTestKit.CheckUnchecked("有 Map 三件套",
                        $"Def 缺失：推进器={thrDef != null} 空气罐={tankDef != null} 进气道={intakeDef != null} 空气工质={airDef != null}");
                }
                else
                {
                    var thr = thrDef.GetCompProperties<CompProperties_Thruster>();
                    var tank = tankDef.GetCompProperties<CompProperties_PropellantTank>();
                    var intake = intakeDef.GetCompProperties<CompProperties_IntakeAir>();

                    PflTestKit.Check(PflTestKit.Kind.Value, thr != null && thr.densityExponent > 0f,
                        "吸气式发动机：密度指数 a > 0（推力随 ρ 走；火箭是 0）",
                        thr == null ? "没有推进器 comp" : "a=" + $"{thr.densityExponent:F2}");
                    PflTestKit.Check(PflTestKit.Kind.Value,
                        thr?.gasIntakes != null && thr.gasIntakes.Count == 1
                        && thr.gasIntakes[0]?.propellant == airDef
                        && Math.Abs(thr.gasIntakes[0].perUnitRatio - 15f) < 1e-6,
                        "吸气式发动机：15:1 抽空气（KSP 形状；抽不到 ⇒ 熄火）");
                    PflTestKit.Check(PflTestKit.Kind.Value,
                        tank != null && tank.propellantDefName == StandardPropulsion.IntakeAirDefName && tank.capacity > 0f,
                        "空气罐：装的是空气工质且容量 > 0（容量按工质分账）",
                        tank == null ? "没有储罐 comp" : (tank.propellantDefName + " / " + $"{tank.capacity:F0}" + " kg"));
                    PflTestKit.Check(PflTestKit.Kind.Value,
                        intake != null && intake.intakeArea > 0f && intake.coefficient > 0f,
                        "进气道：面积 > 0 且系数 > 0（没有它 ⇒ 一点不充）",
                        intake == null ? "没有进气道 comp" : ("A=" + $"{intake.intakeArea:F0}" + " m²·k=" + $"{intake.coefficient:F2}"));
                }
            }
            PflTestKit.End();

            // ── ② 解析轨与气动互斥：100 km 之内（ρ>0）不许进解析圆轨 ──
            PflTestKit.Begin("大气·解析轨与气动互斥（ρ>0 不许进解析圆轨）", Category);
            {
                var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
                var body = mgr != null ? mgr.GetBodyById(EARTH_BODY_ID) : null;
                if (body == null)
                {
                    PflTestKit.CheckUnchecked("前置：天体", "拿不到地球 Def");
                }
                else
                {
                    double h = 30_000.0;                                  // 30 km：ρ 明显非零
                    double rho = body.Atmosphere.IsActive ? body.Atmosphere.Density(h) : 0.0;
                    PflTestKit.Check(PflTestKit.Kind.Value, rho > 0.0, "前置：30 km 处 ρ > 0", "ρ=" + $"{rho:F5}");
                    var posC = new DVector3(body.radius + h, 0, 0);
                    var velC = DVector3.Cross(posC, DVector3.up).normalized
                               * Math.Sqrt(SpaceConstants.PrimaryGravitationalParameter / posC.magnitude);
                    var grpC = DebugShipSpawner.CreateNoMapShipGroup(spaceGame, EARTH_BODY_ID,
                        posC, velC, out int _, overThrust: false, circularOrbit: false);
                    if (grpC == null)
                    {
                        PflTestKit.CheckUnchecked("前置：船", "建船失败");
                    }
                    else
                    {
                        grpC.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);
                        PflTestKit.Check(PflTestKit.Kind.Value, !grpC.IsCircularOrbit,
                            "ρ>0 的 30 km 上，EnterCircularOrbit 必须**拒绝**（解析轨忽略阻力=错）");
                        spaceGame.DestroyGroup(grpC.GroupId);
                    }
                }
            }
            PflTestKit.End();
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  异步段（一条用例装全部；**必须最后登记**）
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// **交战/子弹时间里不许跑**（2026-10-08 加固）：本用例的预算是**物理秒**、框架超时是**真实 tick**，
        /// 而"有射弹 ⇒ Combat(1/60) 子弹时间"会把整个时间轴拉长 60×（游戏时间也走得慢）⇒
        /// 整段要 20+ 分钟墙钟才可能结算，期间日志看起来就是"冻结"。
        /// 判据口径 = **既有的唯一战斗判据** `TimeScaleManager.IsInCombatAction()`（射弹在飞 / 导弹终末）。
        /// </summary>
        private static bool CombatActive()
        {
            var tsm = tsmRef.Get();
            return tsm != null && tsm.IsInCombatAction();
        }

        private static readonly GameComponentRef<TimeScaleManager> tsmRef = new();

        /// <summary>起异步段：铺两艘试验船，顺序推进六个子阶段。</summary>
        public static void Stage()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null)
            {
                Log.Warning("[大气判据] 没有游戏 ⇒ 异步段不跑");
                return;
            }
            // [2026-10-08] 交战（有射弹/导弹终末）⇒ **拒绝开跑**：物理秒预算在 1/60 下跑不完，
            //   硬跑只能得到"看起来冻结"的一段日志（实测撞到过）⇒ 明说原因，等交火结束再点。
            if (CombatActive())
            {
                Log.Warning("[大气判据] **拒绝开跑：当前处于交战动作**（有射弹在飞 / 导弹终末 ⇒ Combat 1/60 子弹时间）" +
                            "——物理秒预算会被拉长 60×，整段跑不完。等交火结束、时间流速回正常后再点。");
                Messages.Message("大气判据没跑：现在有射弹在飞（子弹时间），跑不完 ⇒ 等交火结束再点一次。",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            RunSync();

            // ⚠ **不取快照**（2026-10-08 事故）：快照清场（`PflTestKit.CleanupSince`）会把「别人那一段」新建的编队
            //   也当成自己的（本库允许两个验收入口并行）⇒ 本用例改为**只销毁自己记过 id 的探针**（见 `Cleanup`）。
            hoverGroupId = -1;
            intakeGroupId = -1;

            PflTestKit.Begin(CaseName, Category, CaseTimeoutTicks);

            // ① 吸气式试验机（空罐起飞）——充填与包线的证据都在它身上
            DebugShipSpawner.SpawnAirBreathingTest(announce: false);
            var groups = spaceGame.GetAllGroups();
            foreach (var g in groups)
            {
                if (g != null && g.IsActive && g.GroupId > hoverGroupId) hoverGroupId = g.GroupId;   // 最新那个
            }
            intakeGroupId = hoverGroupId;

            if (intakeGroupId < 0)
                PflTestKit.CheckUnchecked("前置：吸气式试验机", "船没建起来 ⇒ 充填/包线判不了");

            stageStartTick = (int)PhysTick();
            stage = Phase.Intake;
            Log.Message($"[大气判据] 开工：吸气式试验机 G{intakeGroupId}；后续自动推进（物理时间压缩 ⇒ 整段约几秒真实时间）");
        }

        /// <summary>每物理步推进一次（由 `SpaceGameComp` 调；空闲时第一行返回）。</summary>
        public static void Tick()
        {
            // 排队段：等当前异步用例结完 ⇒ 自动开跑（「一键全覆盖」与"载入自动跑"都走这条）
            if (stage == Phase.Idle && queued)
            {
                if (PflTestKit.AnyPending) return;
                queued = false;
                if (Current.Game?.GetComponent<SpaceGameComp>() == null) return;
                Log.Message("[大气判据] 轮到我了 ⇒ 开跑");
                Stage();
                return;
            }
            if (stage == Phase.Idle || stage == Phase.Done) return;
            if (!PflTestKit.IsCurrent(CaseName))
            {
                // 登记表只有一个槽位：被别人顶掉了 ⇒ 放弃（不静默），并清场
                Log.Warning("[大气判据] 用例已被顶掉 ⇒ 本轮不再结算（异步段要最后登记）");
                Cleanup();
                stage = Phase.Done;
                return;
            }
            // [2026-10-08] **跑到一半遇到交战 ⇒ 立刻按"没拿到证据"收场**（不硬撑、不假红）：
            //   1/60 下物理秒预算要 20+ 分钟墙钟才跑完 ⇒ 硬撑只会让日志看起来像冻结（实测撞到过）。
            //   用 UNCOVERED + 一行 Note 明说是"交战中断"，**已完成的判据不受影响**（框架语义）。
            if (CombatActive())
            {
                Log.Warning("[大气判据] **交战中断**（有射弹在飞 / 导弹终末 ⇒ Combat 1/60）：本轮按 UNCOVERED 收场、清场；" +
                            "等交火结束后重新点一次「大气/悬浮/档位·判据」。");
                PflTestKit.Note("交战中断：跑批途中出现射弹/导弹终末 ⇒ 物理秒预算无法在合理墙钟内推进，本轮作废（未验到）");
                PflTestKit.Abort();
                Cleanup();
                stage = Phase.Done;
                return;
            }

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            double t = PhysTick();
            int elapsed = (int)(t - stageStartTick);

            // 进度可见（每 ~30 物理秒一行）：**有它才看得出"卡在哪一段"** —— 第一版吃过"干等 2700 秒"的哑巴亏
            if (t - lastProgressTick >= 30.0)
            {
                lastProgressTick = t;
                Log.Message($"[大气判据] …仍在 {stage}（已等 {elapsed:F0} / 预算 {BudgetOf(stage):F0} 物理秒）");
            }

            switch (stage)
            {
                case Phase.Intake: TickIntake(spaceGame, elapsed); break;
                case Phase.HoverPhysical: TickHoverPhysical(spaceGame, elapsed); break;
                case Phase.HoverAnalytic: TickHoverAnalytic(spaceGame, elapsed); break;
                case Phase.HoverBack: TickHoverBack(spaceGame, elapsed); break;
                case Phase.HoverDrain: TickHoverDrain(spaceGame, elapsed); break;
                case Phase.HoverTakeover: TickHoverTakeover(spaceGame, elapsed); break;
                case Phase.MapAero: TickMapAero(spaceGame, elapsed); break;
                case Phase.Transfer: TickTransfer(spaceGame, elapsed); break;
            }
        }

        /// <summary>下一子阶段：重置计时。</summary>
        private static void Next(Phase s, string what)
        {
            stage = s;
            stageStartTick = (int)PhysTick();
            Log.Message("[大气判据] → " + what);
        }

        // ① 充填 + 包线
        private static void TickIntake(SpaceGameComp spaceGame, int elapsed)
        {
            if (elapsed < IntakeTicks) return;
            var g = spaceGame.GetGroupById(intakeGroupId);
            var airDef = StandardPropulsion.IntakeAirDef;
            if (g == null || airDef == null)
            {
                PflTestKit.CheckUnchecked("充填/包线", "船在天体上找不到（或 IntakeAir Def 缺席）");
                StartHover(spaceGame);
                return;
            }
            double air = 0.0, fuel = 0.0;
            foreach (int eid in g.GetEntityIds())
            {
                var e = spaceGame.GetEntityById(eid);
                if (e == null) continue;
                air += e.GetPropellantMass(airDef);
                var hyd = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Hydrogen");
                if (hyd != null) fuel += e.GetPropellantMass(hyd);
            }
            // ① 充填：空罐起飞 ⇒ 现在必须有料（`k·A·ρ·|v_air|` 那条路真在跑）
            PflTestKit.Check(PflTestKit.Kind.Value, air > 20.0,
                "进气充填：空罐起飞约 30 物理秒后，空气罐应进料（>20 kg）", "空气 " + $"{air:F1}" + " kg");
            // ② 包线：30 km 处燃烧推力必须**显著低于真空标称** ve·ṁ（φ = ρ/ρ₀ ≈ 0.02）
            bool nominal = false; double fMeasured = 0.0, fNominal = 0.0;
            foreach (int eid in g.GetEntityIds())
            {
                var e = spaceGame.GetEntityById(eid);
                if (e == null) continue;
                for (int i = 0; i < e.propulsion.Count; i++)
                {
                    var d = e.propulsion[i];
                    if (d == null || d.isVectoredRcs || d.massFlow <= 0f) continue;
                    fNominal = d.ve * d.massFlow;
                    fMeasured = g.burnDbgF;
                    nominal = true;
                }
            }
            if (!nominal || fNominal <= 0.0)
                PflTestKit.CheckUnchecked("包线（推力随 ρ 衰减）", "该船没有主网 ⇒ 量不到");
            else
                PflTestKit.Check(PflTestKit.Kind.Value, fMeasured > 0.0 && fMeasured < fNominal * 0.35,
                    "包线：30 km 处实测推力应远低于真空标称（<35% ⇒ φ 真在起作用）",
                    "实测 " + $"{(fMeasured / 1000.0):F1}" + " kN ↔ 标称 " + $"{(fNominal / 1000.0):F1}" + " kN");
            PflTestKit.Note("（氢燃料余量 " + $"{fuel:F0}" + " kg —— 仅供人读，不作判据）");
            StartHover(spaceGame);
        }

        private static void StartHover(SpaceGameComp spaceGame)
        {
            // 收掉吸气式那艘，避免与悬浮那艘互相干扰（同一片空域）
            if (intakeGroupId >= 0) spaceGame.DestroyGroup(intakeGroupId);
            intakeGroupId = -1;

            // 料 6000 kg：够"物理 150 + 解析 50 + 回物理 25 + 抽空"整条链（≈16–20 kg/s）
            DebugShipSpawner.SpawnHoverTest(announce: false, fuelKg: RunnerFuelKg);
            hoverGroupId = -1;
            foreach (var g in spaceGame.GetAllGroups())
            {
                if (g != null && g.IsActive && g.GroupId > hoverGroupId) hoverGroupId = g.GroupId;
            }
            if (hoverGroupId < 0)
            {
                PflTestKit.CheckUnchecked("前置：悬浮试验机", "船没建起来 ⇒ 悬浮/档位判不了");
                Finish();
                return;
            }
            Next(Phase.HoverPhysical, "物理档悬浮（G" + hoverGroupId + "）");
        }

        // ③ 物理档悬浮
        private static void TickHoverPhysical(SpaceGameComp spaceGame, int elapsed)
        {
            if (elapsed < HoverPhysicalTicks) return;
            var g = spaceGame.GetGroupById(hoverGroupId);
            if (g == null || !g.hoverActive)
            {
                PflTestKit.Check(PflTestKit.Kind.Invariant, false, "物理档悬浮：应处于悬浮中",
                    g == null ? "组不见了" : "hoverActive=false");
                Finish();
                return;
            }
            double r = g.CurrentOrbit.positionSystem.v.magnitude;
            double altErr = r - (SpaceConstants.PlanetRadiusMeters + g.hoverAltitudeMeters);
            var src = spaceGame.GetGroupPropulsionSource(g);
            double fuel = src != null ? src.TotalPropellantMass : 0.0;

            PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(altErr) < 25.0,
                "物理档：高度差应收敛到十米级", "高度差 " + $"{altErr:F1}" + " m");
            PflTestKit.Check(PflTestKit.Kind.Value, fuel > 0.0 && fuel < RunnerFuelKg * 0.95,
                "物理档：燃料应在按保持耗料下走（至少烧掉 5%）",
                "余料 " + $"{fuel:F0}" + " / 起步 " + $"{RunnerFuelKg:F0}" + " kg");

            // 记录定点，供"切档不重锁"判据
            latBefore = g.hoverLatRad; lonBefore = g.hoverLonRad0; altBefore = g.hoverAltitudeMeters;
            analyticStartFuel = fuel;
            analyticMass = GroupMass(spaceGame, g, src);   // 与 `GetGroupMass` 同口径（它不公开）

            bool switched = spaceGame.SwitchHoverTier(g, true);
            PflTestKit.Check(PflTestKit.Kind.Value, switched, "切解析档：有全矢量网 ⇒ 应接受");
            analyticStartTick = PhysTick();
            Next(Phase.HoverAnalytic, "解析档悬浮（应被钉死在定点）");
        }

        // ④ 解析档
        private static void TickHoverAnalytic(SpaceGameComp spaceGame, int elapsed)
        {
            if (elapsed < HoverAnalyticTicks) return;
            var g = spaceGame.GetGroupById(hoverGroupId);
            if (g == null || !g.hoverActive)
            {
                // ⚠ **前置不足 ⇒ UNCHECKED**（不是功能红字）：物理档段就把料烧完时，"料尽 ⇒ 退出"会
                //   提前发生（实测抓到过一次），这时候解析档**根本没被验到** ⇒ 如实登记，别误报 FAIL。
                PflTestKit.CheckUnchecked("解析档（钉死/账单）",
                    "船在物理档段就料尽退出了 ⇒ 这一档没验到（把试验船料调大或缩短等待）");
                Finish();
                return;
            }
            var tsm = Current.Game.GetComponent<TimeScaleManager>();
            var mgr = Current.Game.GetComponent<CelestialBodyManager>();
            var body = mgr.GetBodyById(g.CelestialBodyId);
            double omega = Launch.GetSurfaceState.RotationOmega(mgr, g.CelestialBodyId);
            var (tp, _) = HoverCore.TargetState(body.radius, g.hoverLatRad, g.hoverLonRad0,
                g.hoverAltitudeMeters, omega, tsm.GetPhysicsTick());
            double posErr = (g.CurrentOrbit.positionSystem.v - tp).magnitude;

            // ④a **位置被钉死**（定点解析的招牌）
            PflTestKit.Check(PflTestKit.Kind.Value, posErr < 1.0,
                "解析档：位置必须**钉死在定点**（<1 m；物理档的抖幅在米级）", "位置误差 " + $"{posErr:F3}" + " m");
            // ④b **定点未被重锁**（切档不该把船按到"现在的位置"上）
            PflTestKit.Check(PflTestKit.Kind.Invariant,
                Math.Abs(g.hoverLatRad - latBefore) < 1e-12 && Math.Abs(g.hoverAltitudeMeters - altBefore) < 1e-9,
                "切档不重锁定点（λ0/高度逐位不变）",
                $"λ0 {g.hoverLonRad0:F9}/{lonBefore:F9}｜高 {g.hoverAltitudeMeters:F1}/{altBefore:F1}");
            // ④c **两档账单一致**：解析档实测 kg/s ↔ m·g/ve
            var src = spaceGame.GetGroupPropulsionSource(g);
            double fuelNow = src != null ? src.TotalPropellantMass : 0.0;
            double dtSec = PhysTick() - analyticStartTick;
            double rateMeasured = dtSec > 0.0 ? (analyticStartFuel - fuelNow) / dtSec : 0.0;
            double gLocal = body.radius > 0.0
                ? SpaceConstants.PrimaryGravitationalParameter / (body.radius * body.radius) : 0.0;
            float ve = 0f; float mf = 0f;
            if (src != null)
            {
                var nets = src.Networks;
                for (int i = 0; i < nets.Count; i++)
                {
                    if (!nets[i].HasRcsEngines || nets[i].MassFlow <= 0f) continue;
                    ve += nets[i].Thrust; mf += nets[i].MassFlow;
                }
                ve = mf > 0f ? ve / mf : 0f;
            }
            double rateExpected = ve > 0f ? analyticMass * gLocal / ve : 0.0;
            double ratio = rateExpected > 0.0 ? rateMeasured / rateExpected : 0.0;
            PflTestKit.Check(PflTestKit.Kind.Value, ratio > 0.75 && ratio < 1.25,
                "两档账单一致：解析档实测耗料率 ≈ m·g/ve（±25%）",
                "实测 " + $"{rateMeasured:F2}" + " ↔ 期望 " + $"{rateExpected:F2}" + " kg/s（比 "
                + $"{ratio:F2}" + "）");

            bool back = spaceGame.SwitchHoverTier(g, false);
            PflTestKit.Check(PflTestKit.Kind.Value, back, "切回物理档：应接受");
            Next(Phase.HoverBack, "切回物理档（定点应仍不变）");
        }

        // ⑤ 回物理档
        private static void TickHoverBack(SpaceGameComp spaceGame, int elapsed)
        {
            if (elapsed < HoverBackTicks) return;
            var g = spaceGame.GetGroupById(hoverGroupId);
            if (g == null || !g.hoverActive || g.hoverAnalytic)
            {
                PflTestKit.Check(PflTestKit.Kind.Invariant, false, "切回物理档：应处于悬浮且档位=物理",
                    g == null ? "组不见了" : ("hoverActive=" + g.hoverActive + " analytic=" + g.hoverAnalytic));
                Finish();
                return;
            }
            double r = g.CurrentOrbit.positionSystem.v.magnitude;
            double altErr = r - (SpaceConstants.PlanetRadiusMeters + g.hoverAltitudeMeters);
            PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(altErr) < 40.0,
                "回到物理档后仍在守住（高度差 <40 m）", "高度差 " + $"{altErr:F1}" + " m");
            PflTestKit.Check(PflTestKit.Kind.Invariant, Math.Abs(g.hoverAltitudeMeters - altBefore) < 1e-9,
                "两次切档都没动定点（高度逐位不变）", "高 " + $"{g.hoverAltitudeMeters:F1}");

            DebugShipSpawner.DrainHoverFuel(60.0);   // 抽空 ⇒ 看"料尽 ⇒ 显式退出"
            Next(Phase.HoverDrain, "抽空 ⇒ 等「料尽 ⇒ 显式退出」");
        }

        // ⑥ 料尽 ⇒ 显式退出
        private static void TickHoverDrain(SpaceGameComp spaceGame, int elapsed)
        {
            if (elapsed < HoverDrainTicks) return;
            var g = spaceGame.GetGroupById(hoverGroupId);
            PflTestKit.Check(PflTestKit.Kind.Value, g == null || !g.hoverActive,
                "料尽 ⇒ 悬浮必须**自动退出**（不留'免费档'）", g == null ? "组已销毁" : "hoverActive=" + g.hoverActive);

            // 再铺一艘（前一艘已经料尽了）看 [D7 触发①]：**操舵台接管 ⇒ 退出悬浮**
            DebugShipSpawner.SpawnHoverTest(announce: false, fuelKg: RunnerFuelKg);
            hoverGroupId = -1;
            foreach (var gg in spaceGame.GetAllGroups())
            {
                if (gg != null && gg.IsActive && gg.GroupId > hoverGroupId) hoverGroupId = gg.GroupId;
            }
            if (hoverGroupId < 0 || !spaceGame.SetHover(spaceGame.GetGroupById(hoverGroupId), true))
            {
                PflTestKit.CheckUnchecked("操舵台接管 ⇒ 退出悬浮（D7 触发①）", "试验船没建起来或开悬浮失败");
                Finish();
                return;
            }
            Next(Phase.HoverTakeover, "操舵台接管 ⇒ 应退出悬浮（D7 触发①）");
        }

        /// <summary>
        /// **[D7 触发①] 操舵台接管 ⇒ 退出悬浮**（2026-10-07 接线时一并钉住）：
        /// 悬浮是**物理档的工作模式**，操舵台是手动自由机动 ⇒ 两者不能同时成立；
        /// 接管在 `ApplySteering` 里与"中止 burn / 清队列"同一处、同一时机发生。
        /// 本阶段**程序化**给一次操舵输入（`SteeringController` 的公开状态 + 一次平移键），
        /// 不劳玩家点键盘 —— 这也是"测试自动化"那条要求的落点。
        /// </summary>
        private static void TickHoverTakeover(SpaceGameComp spaceGame, int elapsed)
        {
            // 前一拍先确认"确实在悬浮"，再给输入（不然判据会因"本来就没悬"而假绿）
            if (elapsed < 2)
            {
                var g0 = spaceGame.GetGroupById(hoverGroupId);
                if (g0 != null && !g0.hoverActive)
                {
                    PflTestKit.CheckUnchecked("操舵台接管 ⇒ 退出悬浮（D7 触发①）", "开悬浮后没进到悬浮态 ⇒ 这条判不了");
                    Finish();
                }
                return;
            }
            if (elapsed < 3)
            {
                SteeringController.Open(hoverGroupId);
                SteeringController.KeyForward = true;      // 一次平移输入 = 接管
                return;
            }
            if (elapsed < HoverTakeoverTicks) return;

            var g = spaceGame.GetGroupById(hoverGroupId);
            PflTestKit.Check(PflTestKit.Kind.Value, g == null || !g.hoverActive,
                "操舵台接管 ⇒ 悬浮必须**退出**（D7 触发①：玩家下令）",
                g == null ? "组已销毁" : "hoverActive=" + g.hoverActive);
            SteeringController.KeyForward = false;
            SteeringController.Close();

            // ③ 最后一站：**有 Map 侧飞行中行为**（[A3] 进气道 / 15:1 抽气 / 包线在同一批数据上）
            // ① 负对照船：**大气之外**（150 km，ρ=0）② 正对照船：**大气之内**（60 km，ρ≈6.7e-4）
            if (!DebugShipSpawner.SpawnMapAeroTest(out int mgNeg, out _, announce: false, altitudeMeters: 150000.0))
            {
                PflTestKit.CheckUnchecked("有 Map 侧飞行中（进气道充填）",
                    "没建起试验台（多半是没有殖民地地图）⇒ 这一段判不了");
                Finish();
                return;
            }
            mapAeroNegGroupId = mgNeg;
            if (!DebugShipSpawner.SpawnMapAeroTest(out int mg, out _, announce: false, altitudeMeters: 60000.0))
            {
                PflTestKit.CheckUnchecked("有 Map 侧飞行中（进气道充填）", "第二艘试验台没建起来");
                Finish();
                return;
            }
            mapAeroGroupId = mg;
            Next(Phase.MapAero, "有 Map 大气：停着（负对照）→ 给空速（进气/耗气）");
        }

        // ── [A3] 有 Map 侧飞行中：负对照 + 正对照 ──
        private static int mapAeroGroupId = -1;

        /// <summary>
        /// **有 Map 侧的飞行中行为**（2026-10-07 接）：判据要的是**空气相对速度**，不必真发射 ——
        /// 停在地表时 `v_air ≈ 0`（**负对照：气不该涨**），把群速度改成"共转 + 500 m/s 顺行"之后
        /// 进气道就工作（**正对照：气从 0 涨起来**），同时开一次燃烧看**燃料与空气双双在扣**。
        /// </summary>
        private static void TickMapAero(SpaceGameComp spaceGame, int elapsed)
        {
            var g = spaceGame.GetGroupById(mapAeroGroupId);
            var airDef = StandardPropulsion.IntakeAirDef;
            if (g == null || airDef == null)
            {
                PflTestKit.CheckUnchecked("有 Map 侧飞行中（进气道充填）", "试验台组不见了");
                Finish();
                return;
            }
            var mapComp = g.GetLinkedMapComp();
            if (mapComp == null)
            {
                PflTestKit.CheckUnchecked("有 Map 侧飞行中（进气道充填）", "试验台没有关联地图");
                Finish();
                return;
            }

            // ① 负对照（第 10 秒）：停在地表 ⇒ v_air ≈ 0 ⇒ **一点气都不该进**
            if (!mapAeroNegativeChecked && elapsed >= 20)
            {
                mapAeroNegativeChecked = true;
                var gNeg = spaceGame.GetGroupById(mapAeroNegGroupId);
                var mapCompNeg = gNeg?.GetLinkedMapComp();
                double air0 = mapCompNeg != null ? mapCompNeg.GetPropellantMass(airDef) : 0.0;
                PflTestKit.Check(PflTestKit.Kind.Value, air0 <= 0.001,
                    "有 Map 侧负对照：**大气之外（ρ=0）⇒ 不进气**（气罐应仍是 0）",
                    "空气 " + $"{air0:F3}" + " kg");

                // 给空速：共转速度 + **500 m/s 顺行** ⇒ v_air = 500（进气道开始工作）
                //   ⚠ 不在这儿起燃烧：这座站 60 km 处包线 φ≈6e-4，出了力也顶不住重力 ⇒ 只会加速坠落；
                //     "抽气（15:1）"这条由**无 Map 侧**那条判据覆盖（实测 15 倍抽取 + 熄火），此处不重复。
                double omega = Launch.GetSurfaceState.RotationOmega(
                    Current.Game.GetComponent<CelestialBodyManager>(), g.CelestialBodyId);
                var pos = g.CurrentOrbit.positionSystem.v;
                var vAirDir = DVector3.Cross(pos, DVector3.up).normalized;          // 顺行（赤道系）
                g.CurrentOrbit.velocitySystem =
                    (SystemVec3)(SurfaceFrame.SurfaceVelocity(pos, omega) + vAirDir * 500.0);
                g.OrbitDirty = true;
            }

            if (elapsed < MapAeroTicks) return;

            // ② 正对照（第 40 秒）：气从 0 涨起来 + 燃料在扣
            double air = mapComp.GetPropellantMass(airDef);
            PflTestKit.Check(PflTestKit.Kind.Value, air > 20.0,
                "有 Map 侧：给了 500 m/s 空气相对速度后，进气道把空气灌进来（>20 kg）",
                "空气 " + $"{air:F1}" + " kg（负对照时是 0）");

            // ── 最后一站：[A8] 点到点**解析搬运**（计划稿的"两档账单一致"的实测面）──
            // **[A8] 大气层内的航渡**（用户 2026-10-07 纠正：巡航不是轨道搬家）：30 km 高度、两点间约 500 km 弧线
            double rT = SpaceConstants.PlanetRadiusMeters + 30_000.0;
            var posT = new DVector3(rT, 0, 0);
            var velT = SurfaceFrame.SurfaceVelocity(posT, 2.0 * Math.PI / 60000.0);   // 与地表共转（"悬停"起点）
            var tg = DebugShipSpawner.CreateNoMapShipGroup(spaceGame, EARTH_BODY_ID,
                posT, velT, out int tgEnt, overThrust: false, circularOrbit: false);
            if (tg == null)
            {
                PflTestKit.CheckUnchecked("解析搬运（两档账单一致）", "没造出搬运试验船");
                Finish();
                return;
            }
            _ = tgEnt;
            transferGroupId = tg.GroupId;
            var tSrc = spaceGame.GetGroupPropulsionSource(tg);
            transferFuel0 = tSrc != null ? tSrc.TotalPropellantMass : 0.0;
            transferMass0 = GroupMass(spaceGame, tg, tSrc);

            // 目标：同方向另一条圆轨（半径 +100 km）；Δv 总额给 400 m/s（估时用可用 a）
            double r0 = tg.CurrentOrbit.positionSystem.v.magnitude;
            var rad0 = tg.CurrentOrbit.positionSystem.v * (1.0 / r0);
            double mu = SpaceConstants.PrimaryGravitationalParameter;
            // 终点 = 同高度、沿赤道顺行约 500 km 处的点；**到达速度取该点的共转速度**（"悬停在那个点上"）
            double arcRad = 500_000.0 / r0;                       // 弧长 → 圆心角
            double lonNow = Math.Atan2(tg.CurrentOrbit.positionSystem.v.x, -tg.CurrentOrbit.positionSystem.v.z);
            double lonTgt = lonNow + arcRad;
            transferTargetPos = new DVector3(Math.Sin(lonTgt) * r0, 0.0, -Math.Cos(lonTgt) * r0);
            var tgtVel = SurfaceFrame.SurfaceVelocity(transferTargetPos, 2.0 * Math.PI / 60000.0);
            transferDvTotal = 400.0;
            transferTargetVel0 = tgtVel.magnitude;
            transferStartTick0 = PhysTick();
            // 终点的**固连经纬度**（该量不随时间变；判据用它，见 TickTransfer 的说明）
            var (tLat, tLon) = SurfaceFrame.EciToLongLat(transferTargetPos, 2.0 * Math.PI / 60000.0, transferStartTick0);
            transferTargetLat = tLat;
            transferTargetLon = tLon;
            transferTargetRadius = transferTargetPos.magnitude;
            // **[模式互斥] 起搬前先把悬浮打开**（回归判据：起搬必须把它**显式退掉**）——
            //   2026-10-07 用户实测："飞船疑似被锚定死了，走不出去"：旧代码里悬浮（定点保持）与搬运
            //   （按航线推位置）各改各的位置、互不知道对方 ⇒ 两条链把船互相按着。
            spaceGame.SetHover(tg, true);
            bool hoverWasOn = tg.hoverActive;
            if (!spaceGame.StartAnalyticTransfer(tg, transferTargetPos, tgtVel, transferDvTotal, PhysTick()))
            {
                PflTestKit.CheckUnchecked("解析搬运（两档账单一致）", "起搬被拒（没有引擎 / 端点在地表以下）");
                Finish();
                return;
            }
            PflTestKit.Check(PflTestKit.Kind.Value, hoverWasOn && !tg.hoverActive,
                "模式互斥：起搬前悬浮开着 ⇒ 起搬把它退掉（不然两条链互相按着船）",
                "起搬前 hoverActive=" + hoverWasOn + "，起搬后=" + tg.hoverActive);
            bool reHoverRejected = !spaceGame.SetHover(tg, true);
            PflTestKit.Check(PflTestKit.Kind.Value, reHoverRejected && !tg.hoverActive,
                "模式互斥：搬运中不许开悬浮（拒绝 + 不落档）",
                "SetHover 返回 " + (reHoverRejected ? "false（被拒 ✓）" : "true（没拦住 ✗）")
                + "，hoverActive=" + tg.hoverActive);
            Next(Phase.Transfer, "解析搬运（应精确到点 + 账单 = Δv 总额）");
        }

        // [A8] 解析搬运段的读数锚点
        private static int transferGroupId = -1;
        private static double transferFuel0, transferMass0, transferDvTotal;
        private static DVector3 transferTargetPos;
        private static double transferTargetVel0;
        private static double transferStartTick0;
        private static double transferTargetLat, transferTargetLon, transferTargetRadius;

        /// <summary>
        /// **[A8] 解析搬运的判据**（计划稿说"同一次搬运两档账单一致"）：到达后
        /// ① **精确落位**（位置 = 目标，误差 <1 m）；② **账单 = Δv 总额**：`ve·ln(m0/m1) ≈ Δv_total`（±25%，
        /// 与物理档同一条火箭方程口径）⇒ 换档既不逃费也不多收；③ 搬运单已清（不留悬挂状态）。
        /// </summary>
        private static void TickTransfer(SpaceGameComp spaceGame, int elapsed)
        {
            var g = spaceGame.GetGroupById(transferGroupId);
            if (g == null)
            {
                PflTestKit.CheckUnchecked("解析搬运（两档账单一致）", "搬运试验船不见了（多半是途中被撞/毁）");
                Finish();
                return;
            }
            bool arrived = !g.transferActive;
            // **等多久由搬运单自己定**（核心算的时长 + 余量），兜底才用常量
            double waitNeed = Math.Max(TransferTicks, g.transferDuration + 60.0);
            if (!arrived && elapsed < waitNeed) return;
            if (!arrived)
            {
                PflTestKit.Check(PflTestKit.Kind.Value, false, "解析搬运：预算内应到达",
                    "已等 " + elapsed + " / " + TransferTicks + " 物理秒，仍在搬");
                Finish();
                return;
            }

            var src = spaceGame.GetGroupPropulsionSource(g);
            double fuel1 = src != null ? src.TotalPropellantMass : 0.0;
            double mass1 = GroupMass(spaceGame, g, src);
            // ⚠ **按轨道量判"落位"**（不按"站在那一点上"）：到达设在**这一步的主线程**，
            //   而物理照旧推进 ⇒ 下一步来看时船已沿新轨道飞出去约 14 km（实测误差 11846.975 m ≈ v·dt）。
            //   这正是"精确落位"的本义：**落位后的轨道 = 目标轨道**（半径与速度对上），不是"停在那一点"。
            // ⚠ 大气层内的落位**下一秒就被物理接管**（重力 + 阻力）⇒ 位置容差按"一步的量级"给（几十米），
            //   速度同理；**账**才是这一条的主要内容（下面那条）。
            // **读出游戏在"到达那一刻"当场记下的误差**（`group.transferArrivalErrorMeters`）。
            // ⚠ 判据侧三次口径迭代（惯性快照 1053.6 m / ω 搬点 387.6 km / 固连经纬度 387.6 km）互相打架 ——
            //   后两次**互相印证**同一个数，说明我"事后重建"的那条线与生产不是一条线。
            //   与其继续调"我算的线"，不如用**生产当场记的量**（谁也没法比它更准，也堵死"采样时刻"这类歧义）。
            double posErr = g.transferArrivalErrorMeters;
            PflTestKit.Check(PflTestKit.Kind.Value, posErr < 200.0,
                "解析搬运（大气层内航渡）：到达即落在目标点附近（<200 m；此后物理立刻接管）",
                "位置误差 " + $"{posErr:F1}" + " m");

            // **[模式互斥·到达侧] 起搬前在悬着 ⇒ 到点必须**恢复悬浮**（2026-10-07 用户实测"一搬就坠毁"：
            //   不恢复的话，大气层里搬完就开始掉 —— 30 km 掉到地面、撞地 339.7 m/s ⇒ 坠毁 + 地图销毁）。
            PflTestKit.Check(PflTestKit.Kind.Value, g.hoverActive,
                "模式互斥：起搬前悬着 ⇒ 到达后自动恢复悬浮（不恢复＝在大气里搬完就开始掉）",
                "到达后 hoverActive=" + g.hoverActive
                + (g.hoverActive ? "（档位=" + (g.hoverAnalytic ? "解析" : "物理") + "）" : "✗"));

            // 账单：ve·ln(m0/m1) 应与 Δv 总额同量级（火箭方程口径，物理档同一条）
            float ve = src != null ? src.EffectiveVe : 0f;
            double dvBilled = ve > 0f && mass0Ok()
                ? ve * Math.Log(transferMass0 / Math.Max(mass1, 1.0))
                : 0.0;
            double ratio = transferDvTotal > 0.0 ? dvBilled / transferDvTotal : 0.0;
            PflTestKit.Check(PflTestKit.Kind.Value, ratio > 0.75 && ratio < 1.25,
                "解析搬运：账单 = Δv 总额（ve·ln(m0/m1) / Δv_total 在 ±25%）",
                "账单 " + $"{dvBilled:F1}" + " ↔ 指令 " + $"{transferDvTotal:F1}" + " m/s（比 "
                + $"{ratio:F2}" + "）｜料 " + $"{transferFuel0:F0}" + " → " + $"{fuel1:F0}" + " kg");
            Finish();

            bool mass0Ok() => transferMass0 > 0.0;
        }

        private static bool mapAeroNegativeChecked;
        private static int mapAeroNegGroupId = -1;

        private static void Finish()
        {
            Cleanup();
            PflTestKit.End();
            stage = Phase.Done;
            Log.Message("[大气判据] 收工（结论见上方「用例 … 结算」行；汇总块由调用方打印）");
        }

        private static void Cleanup()
        {
            // ⚠ **[2026-10-08 事故：清场只能清自己] 不要用 `PflTestKit.CleanupSince()`（按快照扫"本段新建的组"）**：
            //   本库允许两个验收入口同时跑（实测撞到过）：本用例的收工清场会把**别人那段**新建的编队也当成自己的。
            //   15:31 那局的证据链：本用例因「交战中断」按 UNCOVERED 收场 ⇒ 清场 ⇒ `[大气判据] 清场：销毁本段新建的 10 个编队`
            //   ⇒ 那 10 个正好是**弹群齐射场景的发射舰 G21 + 靶舰 G22 + 8 枚导弹**（日志里 `[地图] G21/G22 销毁` 紧随其后）
            //   ⇒ 那一阶段此后 4800 物理秒无物可观测（⑮/⑯/接触 全 0）⇒ 判 UNCOVERED（"未观测到弹头 AoE 走网格"）。
            //   ⇒ **只销毁本用例自己记过 id 的探针**（下面五个），一个不多杀。
            TryDestroy(hoverGroupId, "判据清场（本用例自建：悬浮探针）");
            TryDestroy(intakeGroupId, "判据清场（本用例自建：吸气式探针）");
            TryDestroy(mapAeroGroupId, "判据清场（本用例自建：有 Map 大气探针）");
            TryDestroy(mapAeroNegGroupId, "判据清场（本用例自建：负对照探针）");
            TryDestroy(transferGroupId, "判据清场（本用例自建：搬运探针）");
            hoverGroupId = -1;
            intakeGroupId = -1;
            // （同步段的探针各自在自己的用例里已显式销毁：grpA/grpB/grpC ✓）
        }

        /// <summary>群质量（与 `SpaceGameComp.GetGroupMass` 同口径：Σ 实体总质量 + 工质总质量）。</summary>
        private static double GroupMass(SpaceGameComp spaceGame, SpaceEntityGroup g, IPropulsionSource? src)
        {
            double mass = 0.0;
            foreach (int eid in g.GetEntityIds())
            {
                var e = spaceGame.GetEntityById(eid);
                if (e != null) mass += e.TotalMass;
            }
            if (src != null) mass += src.TotalPropellantMass;
            return mass;
        }

        private static void TryDestroy(int groupId, string reason = "判据清场（本用例自建探针）")
        {
            if (groupId < 0) return;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg != null && sg.GetGroupById(groupId) != null) sg.DestroyGroup(groupId, reason);
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  排队与自动跑（2026-10-07：用户"为什么又要点" ⇒ 别再加新按钮）
        // ══════════════════════════════════════════════════════════════════════════

        private static bool queued;
        private static bool autoRunChecked;

        /// <summary>
        /// **排队**：等当前异步用例结完再开跑（登记表只有一个槽位 ⇒ 不能与别人并行）。
        /// 由「一键测试（全覆盖）」调用 —— 那条按钮**用户本来就在按**，于是 L1 判据不再需要专门点一次。
        /// </summary>
        public static void QueueAfterCurrent()
        {
            if (stage != Phase.Idle && stage != Phase.Done) return;   // 已在跑/已跑完
            queued = true;
            Log.Message("[大气判据] 已排队：等当前异步用例结完后自动开跑（不用再点）");
        }

        /// <summary>
        /// **载入自动跑（可选）**：DevMode + 模组目录里存在标记文件 `autotest-l1.flag` ⇒ 载入后自动排队跑一次，
        /// 然后**删掉标记**（只跑一次，不打扰后续游戏）。
        /// 用法：把空文件丢进 `Mods/PioneerFrameworkLite/autotest-l1.flag`，下次进游戏就自动出结论。
        /// ⚠ 只在 DevMode 生效（它会长/收探针船，普通玩家不该被它惊到）。
        /// </summary>
        public static void TickAutoRun()
        {
            if (autoRunChecked) return;
            autoRunChecked = true;
            if (!Prefs.DevMode) return;
            try
            {
                foreach (var m in ModLister.AllInstalledMods)
                {
                    if (m == null || !m.Active || m.RootDir == null) continue;
                    string marker = System.IO.Path.Combine(m.RootDir.FullName, "autotest-l1.flag");
                    if (!System.IO.File.Exists(marker)) continue;
                    System.IO.File.Delete(marker);                 // 只跑一次
                    Log.Message("[大气判据] 发现标记 " + marker + " ⇒ 载入后自动跑一次 L1 判据（跑完即止）");
                    QueueAfterCurrent();
                    return;
                }
            }
            catch (System.Exception e)
            {
                Log.Warning("[大气判据] 自动跑标记检查失败（不影响游戏）：" + e.Message);
            }
        }

        /// <summary>跑批入口（调试页签按钮）：同步段 + 异步段 + 结论块。</summary>
        public static void Run()
        {
            PflTestKit.Reset();
            Stage();
            PflTestKit.PrintVerdict("大气/悬浮/档位（L1）");
        }
    }
}
