using System;
using System.Collections.Generic;
using System.Text;
using PioneerFramework.Combat;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 引力场 + 制导参数的**游戏内即时电池**（A0/A1，2026-09-14）。
    ///
    /// 与纯测试的分工：纯测试锁定**数学**（参考系等价性、潮汐量级、守恒），本电池锁定**真实 Def 数据
    /// 与真实 API**——纯测试读不到 XML/CelestialBodyManager，"数值在纯测试里对"并不等于"游戏里对"，
    /// 这正是 A1 那处漂移（deadband 1.0 vs 游戏 0.5）暴露的教训。
    ///
    /// 三类断言：
    ///   · 数据路径：天体层级枚举、位置沿父链累加、系统解析（月球与地球同系）
    ///   · 场数学：影子一致性、线性恒等式、潮汐量级与**可分辨判据**（0=旧行为 / 0.1%–0.6%=正确 / ~7%=全量和）
    ///   · A1 参数：Def 派生 == 单一来源（XML 改漂了就要打红）
    ///
    /// 全部为只读计算，不造景、不改世界状态；可单独点按钮跑，也被一键验收在开头调用。
    /// </summary>
    public static class GravityFieldSelfTest
    {
        private const int EARTH_BODY_ID = 1;
        private const int MOON_BODY_ID = 2;
        private const int SUN_BODY_ID = 0;

        private static int passed;
        private static int failed;
        private static readonly List<string> failures = new();
        private static readonly StringBuilder log = new();

        public static int LastPassed => passed;
        public static int LastFailed => failed;
        public static IReadOnlyList<string> LastFailures => failures;

        public static void Run()
        {
            passed = 0;
            failed = 0;
            failures.Clear();
            log.Clear();

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null)
            {
                Log.Error("[引力自检] CelestialBodyManager 缺失，无法运行");
                return;
            }

            Window_PFLSelfTest.OpenIfClosed();
            log.AppendLine("[引力自检] ═══ 引力场 / 多体引力 / 制导参数 即时电池 ═══");
            log.AppendLine("[引力自检] 判据说明：根域影子不一致=0；子天体域差分比值 0=旧行为(错)、0.1%–0.6%=潮汐(对)、~7%=全量和(错)");

            double tick = TimeScaleManager.GetPhysicsTickSafe();

            DataPathChecks(mgr, tick);
            FieldMathChecks(mgr, tick);
            OrbitKeepingChecks(mgr, tick);
            GuidanceParamChecks();

            log.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"[引力自检] ═══ 结果：通过 {passed} 项，失败 {failed} 项 ═══");
            if (failed == 0)
                Log.Message(log.ToString());
            else
            {
                Log.Error(log.ToString());
                foreach (var f in failures) Log.Error("[引力自检] 失败：" + f);
            }
        }

        // ───────────────────────── 一、数据路径 ─────────────────────────

        private static void DataPathChecks(CelestialBodyManager mgr, double tick)
        {
            log.AppendLine("── 一、数据路径（天体层级 / 位置累加 / 系统解析）──");

            var earth = mgr.GetBodyById(EARTH_BODY_ID);
            var moon = mgr.GetBodyById(MOON_BODY_ID);
            Check(earth != null, "地球 Def (bodyId=1) 存在", earth == null ? "缺失" : earth.defName);
            Check(moon != null, "月球 Def (bodyId=2) 存在", moon == null ? "缺失" : moon.defName);
            if (earth == null || moon == null) return;

            var earthMembers = mgr.GetSystemMembers(EARTH_BODY_ID);
            Check(earthMembers.Count == 2, "地球系成员数 = 2（Earth + Moon）",
                $"实际 {earthMembers.Count}：{Names(earthMembers)}");
            Check(earthMembers.Count > 0 && earthMembers[0].bodyId == EARTH_BODY_ID,
                "地球系成员首位是根（Earth 在原点语义）",
                earthMembers.Count > 0 ? "首位 bodyId=" + earthMembers[0].bodyId : "空表");

            var moonMembers = mgr.GetSystemMembers(MOON_BODY_ID);
            Check(SameSet(moonMembers, earthMembers),
                "月球与地球**同系**（GetSystemMembers(Moon) == GetSystemMembers(Earth)）",
                $"月球视角 {Names(moonMembers)} / 地球视角 {Names(earthMembers)}");

            var unknown = mgr.GetSystemMembers(-1);
            Check(unknown.Count == 0, "未知天体 id(-1) 返回空表（不抛异常）",
                "实际 " + unknown.Count + " 项");

            var sunMembers = mgr.GetSystemMembers(SUN_BODY_ID);
            bool sunHasGrandchild = Contains(sunMembers, MOON_BODY_ID);
            Check(sunHasGrandchild, "太阳系成员含孙天体月球（层级枚举递归到全后代）",
                Names(sunMembers));

            var sources = new List<GravitySource>();
            mgr.CollectSystemSources(EARTH_BODY_ID, tick, sources);

            Check(sources.Count == 2, "地球系源数量 = 2（地球 + 月球；恒星在希尔盒内被非惯性抵消）", "实际 " + sources.Count);
            if (sources.Count == 0) return;

            var root = sources[0];
            Check(root.bodyId == EARTH_BODY_ID && root.position.magnitude < 1e-6 && root.velocity.magnitude < 1e-9,
                "根源在原点且速度为零（钉住语义）",
                $"bodyId={root.bodyId} |pos|={root.position.magnitude:E2} |vel|={root.velocity.magnitude:E2}");
            Check(Math.Abs(root.mu - earth.gravitationalParameter) < 1e-6,
                "根源 GM == 地球 Def GM", $"{root.mu:E6} vs {earth.gravitationalParameter:E6}");

            bool foundMoon = false;
            for (int i = 1; i < sources.Count; i++)
            {
                var s = sources[i];
                if (s.bodyId != MOON_BODY_ID) continue;
                foundMoon = true;

                DVector3 expectPos = mgr.GetBodyPositionParent(MOON_BODY_ID, tick);
                DVector3 expectVel = mgr.GetBodyVelocityParent(MOON_BODY_ID, tick);
                double dPos = (s.position - expectPos).magnitude;
                double dVel = (s.velocity - expectVel).magnitude;

                // 1 层层级（父=根）时，"沿父链累加"必须退化为"取该天体的父系局部位置"
                Check(dPos < 1.0, "月球源位置 == GetBodyPositionParent(Moon)（父链累加正确）",
                    $"Δ={dPos:F3} m（源={s.position.magnitude / 1000.0:F0}km 期望={expectPos.magnitude / 1000.0:F0}km）");
                Check(dVel < 1e-3, "月球源速度 == GetBodyVelocityParent(Moon)",
                    $"Δ={dVel:E3} m/s");
                Check(Math.Abs(s.mu - moon.gravitationalParameter) < 1e-6,
                    "月球源 GM == 月球 Def GM", $"{s.mu:E6} vs {moon.gravitationalParameter:E6}");
            }
            Check(foundMoon, "地球系源含月球", foundMoon ? "已含" : "缺失");

            bool hasSun = false;
            for (int i = 0; i < sources.Count; i++)
                if (sources[i].bodyId == SUN_BODY_ID) hasSun = true;
            Check(!hasSun, "地球系源不含恒星（希尔盒内非惯性抵消）",
                hasSun ? "实际含恒星源" : "不含");

            var ids = new HashSet<int>();
            bool dup = false, badMu = false;
            for (int i = 0; i < sources.Count; i++)
            {
                if (!ids.Add(sources[i].bodyId)) dup = true;
                if (sources[i].mu <= 0.0) badMu = true;
            }
            Check(!dup, "源 bodyId 无重复", dup ? "有重复" : "唯一");
            Check(!badMu, "源 GM 均 > 0（无质量天体不参与）", badMu ? "存在 GM≤0" : "全部 >0");
        }

        // ───────────────────────── 二、场数学 ─────────────────────────

        private static void FieldMathChecks(CelestialBodyManager mgr, double tick)
        {
            log.AppendLine("── 二、场数学（影子 / 线性 / 潮汐量级）──");

            var earth = mgr.GetBodyById(EARTH_BODY_ID);
            var moon = mgr.GetBodyById(MOON_BODY_ID);
            if (earth == null || moon == null) return;

            var all = new List<GravitySource>();
            mgr.CollectSystemSources(EARTH_BODY_ID, tick, all);
            if (all.Count < 2) return;

            DVector3 moonPos = mgr.GetBodyPositionParent(MOON_BODY_ID, tick);
            DVector3 moonVel = mgr.GetBodyVelocityParent(MOON_BODY_ID, tick);

            // ── 影子：全域场 vs 「根 + 直接子天体」子集场 ──
            var subset = new List<GravitySource>();
            for (int i = 0; i < all.Count; i++)
            {
                bool directChild = all[i].bodyId == EARTH_BODY_ID
                    || mgr.GetBodyById(all[i].bodyId)?.ParentBodyId == EARTH_BODY_ID
                    || all[i].bodyId == SUN_BODY_ID;   // 行星帧含父恒星（2026-11-04 起）
                if (directChild) subset.Add(all[i]);
            }

            var probe = moonPos + new DVector3(3.3e6, 4.0e5, 0.0);
            var probeVel = moonVel + new DVector3(-120.0, 1500.0, 0.0);

            NBodyIntegrator.FieldAccelJerk(probe, probeVel, all, out var fullAcc, out var fullJerk);
            NBodyIntegrator.FieldAccelJerk(probe, probeVel, subset, out var subAcc, out var subJerk);
            double shadowDiff = (fullAcc - subAcc).magnitude;
            Check(shadowDiff < 1e-9, "根域影子：全域场 == 根+直接子天体场（当前数据无孙天体）",
                $"Δ={shadowDiff:E3} m/s²（全域 {all.Count} 源 / 子集 {subset.Count} 源）");

            // ── 场 == Σ 单体项（逐项累加一致性）──
            DVector3 sumAcc = DVector3.zero, sumJerk = DVector3.zero;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].isUniform)
                {
                    sumAcc += all[i].uniformAccel;   // 均匀场：常向量，jerk=0
                    continue;
                }
                NBodyIntegrator.ComputeBodyAccelJerk(probe, probeVel,
                    all[i].position, all[i].velocity, all[i].mu, out var a, out var j);
                sumAcc += a;
                sumJerk += j;
            }
            Check((fullAcc - sumAcc).magnitude < 1e-9 && (fullJerk - sumJerk).magnitude < 1e-12,
                "场 == Σ 各天体二体项（acc 与 jerk 同时成立）",
                $"Δacc={(fullAcc - sumAcc).magnitude:E3} Δjerk={(fullJerk - sumJerk).magnitude:E3}");

            // ── 线性恒等式：差分(A,B) == 场(A) − 场(B) ──
            NBodyIntegrator.FieldAccelJerk(DVector3.zero, DVector3.zero, all, out var atOrigin, out _);
            NBodyIntegrator.DifferentialAccelJerk(probe, probeVel, DVector3.zero, DVector3.zero, all,
                out var diffAcc, out _);
            Check((diffAcc - (fullAcc - atOrigin)).magnitude < 1e-9,
                "差分 == 场(探针) − 场(参考点)（线性恒等式）",
                $"Δ={(diffAcc - (fullAcc - atOrigin)).magnitude:E3} m/s²");

            // ── 月球域：差分(潮汐) 量级与方向 —— **可分辨判据** ──
            var moonSources = new List<GravitySource>();
            mgr.CollectSystemSources(MOON_BODY_ID, tick, moonSources);
            var onlyMoon = new List<GravitySource>();
            for (int i = 0; i < moonSources.Count; i++)
                if (moonSources[i].bodyId == MOON_BODY_ID) onlyMoon.Add(moonSources[i]);

            NBodyIntegrator.FieldAccelJerk(probe, probeVel, onlyMoon, out var localAcc, out _);
            // 差分必须走共用口径（场 − 计划）。写成"场 − 中心天体单独在场"是外面天体的**全量**项：
            // 首轮验收实测 6.86%（应 0.3%–0.6%），正是这个错。
            mgr.ComputeFieldMinusPlan(probe, probeVel, MOON_BODY_ID, moonSources, out var tidal, out _);
            double localMag = localAcc.magnitude;
            double ratio = localMag > 1e-9 ? tidal.magnitude / localMag : 0.0;

            Check(ratio > 1e-4, "月球域差分 ≠ 0（未退化为旧『只算月球』行为）",
                $"比值 {ratio:P4}");
            Check(ratio < 0.03, "月球域差分是潮汐而非全量（未走『全量和』错路）",
                $"比值 {ratio:P4}（潮汐预期 0.1%–0.6%，全量错路 ~6.8%）");
            Check(ratio > 1e-4 && ratio < 0.03, "月球域差分落在潮汐带 0.01%–3%（实测预期 0.3%–0.6%）",
                $"实测 {ratio:P4}；差分={tidal.magnitude:E3} m/s² 局部={localMag:F3} m/s²");

            // 解析潮汐对照：精确闭式 = 各祖先的潮汐项之和（地球 + 太阳），
            //   −Σ_b μ_b·[ (probe−r_b)/|probe−r_b|³ − (moonPos−r_b)/|moonPos−r_b|³ ]
            // 只算地球会让“误差 <0.01%”假红（太阳潮汐 ~1.5%）。
            double d = moonPos.magnitude;
            if (d > 1.0 && (probe - moonPos).magnitude > 1.0)
            {
                DVector3 analytic = DVector3.zero;
                int ancId = moon.ParentBodyId;
                while (ancId >= 0 && NBodyIntegrator.TryFindSource(moonSources, ancId, out var anc))
                {
                    if (!anc.isUniform)   // 恒星按均匀场处理：无潮汐项（用户近似）
                    {
                        DVector3 r = anc.position;
                        DVector3 p = probe - r, m = moonPos - r;
                        double p3 = p.magnitude * p.magnitude * p.magnitude;
                        double m3 = m.magnitude * m.magnitude * m.magnitude;
                        if (p3 > 1e-9 && m3 > 1e-9)
                            analytic -= mgr.GetBodyById(ancId)!.gravitationalParameter * (p / p3 - m / m3);
                    }
                    ancId = mgr.GetBodyById(ancId)?.ParentBodyId ?? -1;
                }
                double anaErr = (tidal - analytic).magnitude / Math.Max(analytic.magnitude, 1e-12);
                Check(anaErr < 1e-4, "月球域差分 == 精确闭式潮汐（地球 + 太阳，误差 <0.01%）",
                    $"相对误差 {anaErr:P4}（差分={tidal.magnitude:E6} 闭式={analytic.magnitude:E6} m/s²）");
            }

            // 根域对照：地球系里"非白名单天体"的贡献应是月球**全量**（= 旧实现数字）
            NBodyIntegrator.ComputeBodyAccelJerk(probe, probeVel, moonPos, moonVel, moon.gravitationalParameter,
                out var moonFull, out _);
            double rootRatio = moonFull.magnitude > 1e-9 ? moonFull.magnitude / localMag : 0.0;
            Check(rootRatio > 0.5 && rootRatio < 2.0,
                "地球系：月球贡献 = 月球全量（根域语义与旧实现一致）",
                $"全量/局部 = {rootRatio:F2}（应为 ~1 量级，而非 ~0.5%）");
        }

        // ───────────────────────── 三、轨道保持 ─────────────────────────

        private static void OrbitKeepingChecks(CelestialBodyManager mgr, double tick)
        {
            log.AppendLine("── 三、轨道保持（白名单语义：不抵消的天体）──");

            var earth = mgr.GetBodyById(EARTH_BODY_ID);
            var moon = mgr.GetBodyById(MOON_BODY_ID);
            if (earth == null || moon == null) return;

            var all = new List<GravitySource>();
            mgr.CollectSystemSources(EARTH_BODY_ID, tick, all);
            DVector3 moonPos = mgr.GetBodyPositionParent(MOON_BODY_ID, tick);
            DVector3 moonVel = mgr.GetBodyVelocityParent(MOON_BODY_ID, tick);
            var probe = moonPos + new DVector3(3.3e6, 0.0, 0.0);
            var probeVel = moonVel;

            // 月球域：白名单={Moon} → 要抵消的是各祖先的潮汐（地球 + 太阳）
            var moonSources = new List<GravitySource>();
            mgr.CollectSystemSources(MOON_BODY_ID, tick, moonSources);
            NBodyIntegrator.FieldAccelJerk(probe, probeVel, moonSources, out var full, out _);
            var onlyMoon = new List<GravitySource>();
            for (int i = 0; i < moonSources.Count; i++)
                if (moonSources[i].bodyId == MOON_BODY_ID) onlyMoon.Add(moonSources[i]);
            NBodyIntegrator.FieldAccelJerk(probe, probeVel, onlyMoon, out var localMoon, out _);

            // 计划 = 月球二体 + 月球沿整条父链的受迫（地球 + 太阳），与生产 ComputeFieldMinusPlan 同口径。
            DVector3 moonForced = DVector3.zero;
            int ancId = moon.ParentBodyId;
            while (ancId >= 0 && NBodyIntegrator.TryFindSource(moonSources, ancId, out var anc))
            {
                NBodyIntegrator.ComputeBodyAccelJerk(moonPos, moonVel, anc.position, anc.velocity, anc.mu,
                    out var aMoon, out _);
                moonForced += aMoon;
                ancId = mgr.GetBodyById(ancId)?.ParentBodyId ?? -1;
            }

            var keptMoon = localMoon + moonForced;            // 计划（月球二体 + 月球受迫）
            var cancelMoon = full - keptMoon;                 // 要抵消的 = 潮汐
            double ratioMoon = localMoon.magnitude > 1e-9 ? cancelMoon.magnitude / localMoon.magnitude : 0.0;
            Check(ratioMoon > 1e-4 && ratioMoon < 0.03,
                "月球域轨道保持：抵消量 = 潮汐（0.01%–3%），非 0（旧）也非全量（错路）",
                $"实测 {ratioMoon:P4}（抵消={cancelMoon.magnitude:E3} 局部={localMoon.magnitude:F3} m/s²）");

            // 地球域：白名单={Earth} → 要抵消的是月球全量（与旧实现数字一致）
            var onlyEarth = new List<GravitySource>();
            for (int i = 0; i < all.Count; i++)
                if (all[i].bodyId == EARTH_BODY_ID) onlyEarth.Add(all[i]);
            var probeEarth = new DVector3(5.5e6, 0.0, 0.0);
            NBodyIntegrator.FieldAccelJerk(probeEarth, DVector3.zero, all, out var fullE, out _);
            NBodyIntegrator.FieldAccelJerk(probeEarth, DVector3.zero, onlyEarth, out var keptE, out _);
            var cancelEarth = fullE - keptE;
            // 白名单={地球} ⇒ 要抵消的是**所有**非白名单源的单体项（2026-11-04 起含父恒星太阳）。
            DVector3 expectedEarth = DVector3.zero;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].bodyId == EARTH_BODY_ID) continue;
                NBodyIntegrator.ComputeBodyAccelJerk(probeEarth, DVector3.zero,
                    all[i].position, all[i].velocity, all[i].mu, out var a, out _);
                expectedEarth += a;
            }
            double dEarth = (cancelEarth - expectedEarth).magnitude;
            Check(dEarth < 1e-12,
                "地球域轨道保持：抵消量 == 所有非白名单源之和（含父恒星）",
                $"Δ={dEarth:E3} m/s²（抵消={cancelEarth.magnitude:E3} 期望={expectedEarth.magnitude:E3}）");

            // 全白名单：无扰动需抵消 → 抵消量必须为 0
            NBodyIntegrator.FieldAccelJerk(probeEarth, DVector3.zero, all, out var keptAll, out _);
            Check((fullE - keptAll).magnitude < 1e-12,
                "白名单=全部天体时抵消量为 0（无扰动需抵消）",
                $"Δ={(fullE - keptAll).magnitude:E3} m/s²");
        }

        // ───────────────────────── 四、A1 制导参数 ─────────────────────────

        private static void GuidanceParamChecks()
        {
            log.AppendLine("── 四、制导参数单一来源（A1：Def 派生 == MissileGuidanceDefaults）──");

            var std = DefDatabase<MissileDef>.GetNamedSilentFail("PF_Missile_Standard");
            var cruise = DefDatabase<MissileDef>.GetNamedSilentFail("PF_Missile_TestCruise");
            var expect = MissileGuidanceDefaults.Standard;

            Check(std != null, "PF_Missile_Standard Def 存在", std == null ? "缺失" : std.defName);
            Check(cruise != null, "PF_Missile_TestCruise Def 存在", cruise == null ? "缺失" : cruise.defName);

            if (std != null) CompareParams("PF_Missile_Standard", std.GuidanceParams, expect);
            if (cruise != null) CompareParams("PF_Missile_TestCruise", cruise.GuidanceParams, expect);

            double launchDeadband = LaunchGuidanceParams.CreateDefault(SpaceConstants.PlanetRadiusMeters).deadband;
            Check(Math.Abs(expect.deadband - launchDeadband) < 1e-9,
                "导弹死区 == 发射制导死区约定（同项目单一口径）",
                $"导弹 {expect.deadband} / 发射 {launchDeadband}");
            Check(Math.Abs(expect.deadband - 0.5) < 1e-9,
                "死区 = 0.5（意图值；曾漂成 1.0）", $"实际 {expect.deadband}");
            Check(Math.Abs(expect.lateralAccel - 80.082645) < 1e-4,
                "横向加速度 = 推力/湿质量 ≈ 80.0826（曾漂成硬编码 83.0）",
                $"实际 {expect.lateralAccel:F6}");
        }

        /// <summary>
        /// 逐字段比较。容差用 **float 精度**（1e-6 相对）而非 1e-9：
        /// 游戏侧 `turnRateRad = maxAngularRateDegrees * Mathf.Deg2Rad` 是 float×float 运算，
        /// 纯核心用 `Math.PI/180` 是 double —— 两者差 ~1e-8，用 1e-9 判会假报失败
        /// （2026-09-14 首轮验收实测：打印出来各字段全都相等，却判"不一致"）。
        /// 这也划清了边界：本检查判"值是否一致"，不判 float 舍入是否逐位相同。
        /// </summary>
        private static void CompareParams(string defName, MissileGuidanceParams got, MissileGuidanceParams expect)
        {
            bool ok = Near(got.lateralGain, expect.lateralGain)
                && Near(got.closingThreshold, expect.closingThreshold)
                && Near(got.cruiseClosing, expect.cruiseClosing)
                && Near(got.boostMax, expect.boostMax)
                && Near(got.deadband, expect.deadband)
                && Near(got.lateralAccel, expect.lateralAccel)
                && Near(got.turnRateRad, expect.turnRateRad)
                && Near(got.timeMargin, expect.timeMargin)
                && Near(got.lostMarginMeters, expect.lostMarginMeters);

            Check(ok, defName + " 的 GuidanceParams 与单一来源逐字段一致",
                ok ? "一致" : $"不一致：gain {got.lateralGain:R}/{expect.lateralGain:R} thr {got.closingThreshold:R}/{expect.closingThreshold:R} cruise {got.cruiseClosing:R}/{expect.cruiseClosing:R} boost {got.boostMax:R}/{expect.boostMax:R} dead {got.deadband:R}/{expect.deadband:R} latAccel {got.lateralAccel:R}/{expect.lateralAccel:R} turn {got.turnRateRad:R}/{expect.turnRateRad:R} margin {got.timeMargin:R}/{expect.timeMargin:R} lost {got.lostMarginMeters:R}/{expect.lostMarginMeters:R}");
        }

        // ───────────────────────── 工具 ─────────────────────────

        /// <summary>float 精度容差比较：|Δ| ≤ max(1e-6, 1e-6×|期望|)。</summary>
        private static bool Near(double a, double b)
            => Math.Abs(a - b) <= Math.Max(1e-6, 1e-6 * Math.Abs(b));

        private static void Check(bool ok, string label, string detail)
        {
            if (ok)
            {
                passed++;
                log.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"  ✓ {label}｜{detail}");
            }
            else
            {
                failed++;
                string line = $"{label}｜期望：{detail}";
                failures.Add(line);
                log.AppendLine("  ✗ " + line);
            }
        }

        private static string Names(List<CelestialBodyInfo> list)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(list[i].defName);
            }
            return sb.ToString();
        }

        private static bool Contains(List<CelestialBodyInfo> list, int bodyId)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].bodyId == bodyId) return true;
            return false;
        }

        private static bool SameSet(List<CelestialBodyInfo> a, List<CelestialBodyInfo> b)
        {
            if (a.Count != b.Count) return false;
            var set = new HashSet<int>();
            for (int i = 0; i < a.Count; i++) set.Add(a[i].bodyId);
            for (int i = 0; i < b.Count; i++)
                if (!set.Contains(b[i].bodyId)) return false;
            return true;
        }
    }
}
