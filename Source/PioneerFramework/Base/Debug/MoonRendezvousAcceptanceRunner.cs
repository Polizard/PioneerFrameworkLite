using System;
using System.Collections.Generic;
using System.Text;
using MechJebLib.Primitives;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **绕月（非根天体）交会验收** —— 批5.C 首件（2026-09-29，用户报障 + "我们也许需要一个月球交会测试场景"）。
    ///
    /// ## 为什么需要它
    /// 用户 2026-09-29 在游戏里跑绕月交会：阶梯的规划与第一次点火**都对**（压近点 3300→2844 km 正好 46.5 m/s，
    /// 实测 46.58、对账 0.00%），此后**无任何点火**，飞船却**撞月**（落地 2075.9 m/s）。
    /// 而"根行星 / 体心系 / 解析圆轨模式"这一整套机器**只有绕卫星时才会被执行**（地球/太阳场景里那些
    /// 天体偏移项恒为 0）⇒ 这条路径此前**从未被任何验收覆盖**（纯台架是二体的，覆盖不到）。
    ///
    /// ## 判据三条（各有机械口径，不另写算式）
    /// **① 两船都活着**：撞毁会销毁实体（`[Collision] … Entities destroyed`）⇒ "还能找到这两艘"就是判据。
    /// **② 到达判据成立**：调**游戏自己的唯一求值口** `SpaceEntityGroup.IsRendezvousGoalMet`（不另算距离/速度）。
    /// **③ 阶梯在位**：`SpaceEntityGroup.ladderOwnsFarField` —— "跑的是 MJ 阶梯"的**直接证据**（不用抓日志）。
    /// ⚠ **0 样本 ⇒ `UNCHECKED`，不是 PASS**（本库的既有纪律）：找不到"绕月 + 已挂交会引导"的编队就如实报。
    ///
    /// ## 用法
    /// 先点「绕月交会·放置」那一族按钮（`DebugShipSpawner.SpawnMoonRendezvousTest`）⇒ 等交会自己跑
    /// ⇒ 出结果**自动打结论**（也可以随时点判定按钮看"此刻"）。
    /// ⚠ **要等多久由"相位差"决定，不由本判定器决定**（同轨起步时阶梯只能抬轨等目标绕过来）：
    ///   · **目标在后方 10°（快档）** ⇒ 要补 10° ⇒ 整条链**约 1 分钟真实时间**（推荐先跑这档）
    ///   · 目标在前方 30°（硬例）⇒ 要补 330° ⇒ 约 5–8 分钟真实时间
    /// 实测（2026-09-29 那一轮）：模拟:真实 ≈ **300:1**（36868 模拟秒 / 120 真实秒）
    /// ⇒ 船绕一圈（16,845 s）≈ 1 分钟真实时间。这个比值随你的时间档位变，不是常数。
    /// </summary>
    public static class MoonRendezvousAcceptanceRunner
    {
        private const int MOON_BODY_ID = 2;   // 与 DebugShipSpawner 同值

        // ── 自动判定（2026-09-29，用户："我希望自动判定，因为我是人类，跑得可能不及时"）──
        //   放置场景即 `Arm(...)`；此后**每个物理步**由 `Tick(...)` 检查，出结果就**自动打结论**：
        //   ① 收口（游戏自己的判据成立）⇒ PASS；② 任一方消失（撞毁会销毁实体）⇒ FAIL(撞毁)；
        //   ③ 超过截止时刻 ⇒ 未达（附读数）。⇒ **人不需要掐点**。
        private static bool _armed;
        private static int _chaserId = -1, _targetId = -1;
        private static double _startTick, _deadlineTick;
        private static float _armRealTime, _nextProgressRealTime;

        /// <summary>**船的绕月周期**（模拟秒）—— **只用于把进度换算成"绕了几圈"这个读数**。
        /// ⚠ 与 `_deadlineTick` 的口径**故意不同**：期限要按"月球绕地周期"给（那是"给多久"，
        ///   4 个 ≈1.27e6 s），读数按"船的绕月周期"给（16,845 s）。首版拿前者当分母报进度 ⇒
        ///   实测把读数缩小 19 倍（"0.12 个月球周期"看着像刚开始，其实船已绕 2.19 圈）⇒ **判据与读数分开口径**。</summary>
        private static double _shipPeriod = 16845.0;

        /// <summary>**墙钟期限**（真实秒；默认 10 分钟）—— 用户 2026-09-29："我是人类，跑得可能不及时"。
        /// ⚠ 模拟期限（4 个月球绕地周期 ≈1.27e6 模拟秒 ≈8 倍速下 44 真实小时）**人根本等不到**
        ///   ⇒ 必须有墙钟收口：到点就用**当前读数**给结论（收口/未达），保证"任何一轮都出结果"。</summary>
        public static float WallDeadlineSeconds = 600f;
        private static float _nextProgressSeconds = 60f;

        /// <summary>放置场景后调用：开始自动判定。<paramref name="shipPeriodSeconds"/> 只用于进度读数（见 `_shipPeriod`）。</summary>
        public static void Arm(int chaserId, int targetId, double startTick, double deadlineTick,
            double shipPeriodSeconds = 16845.0)
        {
            _armed = true;
            _chaserId = chaserId; _targetId = targetId;
            _startTick = startTick; _deadlineTick = deadlineTick;
            if (shipPeriodSeconds > 1.0) _shipPeriod = shipPeriodSeconds;
            _armRealTime = UnityEngine.Time.realtimeSinceStartup;
            _nextProgressRealTime = _armRealTime + _nextProgressSeconds;
            Log.Message(string.Format(
                "[绕月交会验收] 自动判定已武装：G{0} → G{1}｜模拟截止 t={2:F0}（4 个月球绕地周期）" +
                "｜**墙钟截止 {3:F0}s（真实）**·每 {4:F0}s 一行进度（进度里 `圈` 指**船的绕月周期 {5:F0}s**）" +
                "｜出结果会自动打结论，不必掐点",
                chaserId, targetId, deadlineTick,
                WallDeadlineSeconds, _nextProgressSeconds, _shipPeriod));
        }

        /// <summary>每物理步调用（只做几次字典查表，开销可忽略）。</summary>
        public static void Tick(double physicsTick, SpaceGameComp spaceGame)
        {
            if (!_armed || spaceGame == null) return;
            var chaser = spaceGame.GetGroupById(_chaserId);
            var target = spaceGame.GetGroupById(_targetId);

            // ② 任一方消失 = 撞毁销毁实体 ⇒ 立即 FAIL
            if (chaser == null || target == null)
            {
                _armed = false;
                Log.Message(string.Format(
                    "\n══ 绕月交会验收 · **自动结论** ══\n  **FAIL**：G{0} / G{1} 有一方已不存在（撞毁会销毁实体）" +
                    "⇒ 看 `[Collision] … CRASHED into 月球`｜t={2:F0}（起飞后 {3:F0} s）",
                    _chaserId, _targetId, physicsTick, physicsTick - _startTick));
                return;
            }

            double mu = spaceGame.GetBodyGM(MOON_BODY_ID);
            // ① 收口 ⇒ PASS
            if (chaser.IsRendezvousGoalMet(target, mu, out double phaseErrDeg))
            {
                _armed = false;
                DVector3 dV = target.velocityBody - chaser.velocityBody;
                Log.Message(string.Format(
                    "\n══ 绕月交会验收 · **自动结论** ══\n  **PASS**：到达判据成立（`IsRendezvousGoalMet`）" +
                    "｜距离 {0:F1} m｜相对速度 {1:F3} m/s｜沿轨相位差 {2:F1}°｜t={3:F0}（起飞后 {4:F0} s ≈ {5:F2} 圈，船的绕月周期 {6:F0}s）",
                    (target.positionBody - chaser.positionBody).magnitude, dV.magnitude, phaseErrDeg,
                    physicsTick, physicsTick - _startTick, (physicsTick - _startTick) / _shipPeriod, _shipPeriod));
                return;
            }

            // ③ 期限（**模拟期限 或 墙钟期限**，先到者生效）⇒ 未达（附读数；**不冒充 PASS**）
            //    墙钟那一半是给"人跑不过来"用的：模拟期限 ≈8 倍速下 44 真实小时，人等不到。
            float realNow = UnityEngine.Time.realtimeSinceStartup;

            // ③b 进度（每 60 真实秒一行）：让人在自己的会话里就能看见"在动 / 卡在哪一级 / 在等什么"
            if (realNow >= _nextProgressRealTime)
            {
                _nextProgressRealTime = realNow + _nextProgressSeconds;
                double simElapsed = physicsTick - _startTick;
                // ⚠ **"在等什么"必须可见**（2026-09-29 用户实测一轮的教训）：同轨起步时阶梯会排一发在
                //   **7 万秒之后**的窗口点火，场景看着像"卡死不动"（飞船两两绕到对面去了，距离还涨）
                //   ⇒ 进度行必须报**下一发的剩余时间**，否则读者会得出"算法坏了"的错结论。
                var next = chaser.PeekNextManeuver();
                string nextTxt;
                if (next.HasValue)
                {
                    var pm = next.Value;
                    double dvMag = Math.Sqrt(pm.radialDV * pm.radialDV + pm.tangentialDV * pm.tangentialDV
                                             + pm.normalDV * pm.normalDV);
                    nextTxt = string.Format("下一发 T-{0:F0}s（Δv {1:F1} m/s，共 {2} 发在队）",
                        pm.executeTick - physicsTick, dvMag, chaser.plannedManeuvers.Count);
                }
                else
                {
                    nextTxt = string.Format("队列空（等下一次规划；历史共 {0} 发）", chaser.plannedManeuvers.Count);
                }
                // 相位读数走**游戏自己的唯一求值口**同口径（`RendezvousGoal.SignedPhaseDeg`，+ = 目标在前）
                double phaseDeg = RendezvousGoal.SignedPhaseDeg(chaser.positionBody, chaser.velocityBody, target.positionBody);
                var ovC = OrbitView.FromStateVectors(chaser.positionBody, chaser.velocityBody, mu, 0.0, DVector3.up, 1_500_000.0);
                var ovT = OrbitView.FromStateVectors(target.positionBody, target.velocityBody, mu, 0.0, DVector3.up, 1_500_000.0);
                Log.Message(string.Format(
                    "[绕月交会验收·进度] 已跑 真实 {0:F0}s / 模拟 {1:F0}s（船绕 {2:F2} 圈）｜距离 {3:F0} km｜相对速度 {4:F2} m/s" +
                    "｜相位 {5:F1}°（+ 目标在前）｜**{6}**｜G{7} a={8:F0}km e={9:F4}｜G{10} a={11:F0}km e={12:F4}｜**阶梯在位={13}**",
                    realNow - _armRealTime, simElapsed, simElapsed / _shipPeriod,
                    (target.positionBody - chaser.positionBody).magnitude / 1000.0,
                    (target.velocityBody - chaser.velocityBody).magnitude,
                    phaseDeg, nextTxt,
                    chaser.GroupId, ovC.semiMajorAxis / 1000.0, ovC.eccentricity,
                    target.GroupId, ovT.semiMajorAxis / 1000.0, ovT.eccentricity,
                    chaser.ladderOwnsFarField));
            }

            bool wallOut = realNow - _armRealTime >= WallDeadlineSeconds;
            if (wallOut || physicsTick >= _deadlineTick)
            {
                _armed = false;
                var ovC = OrbitView.FromStateVectors(chaser.positionBody, chaser.velocityBody, mu, 0.0, DVector3.up, 1_500_000.0);
                var ovT = OrbitView.FromStateVectors(target.positionBody, target.velocityBody, mu, 0.0, DVector3.up, 1_500_000.0);
                Log.Message(string.Format(
                    "\n══ 绕月交会验收 · **自动结论** ══\n  **未达（超时）**：到截止时刻仍未收口" +
                    "｜距离 {0:F0} m｜相对速度 {1:F2} m/s｜G{2} 要素 a={3:F0}km e={4:F4}｜G{5} 要素 a={6:F0}km e={7:F4}" +
                    "｜**阶梯在位={8}**｜t={9:F0}（起飞后 {10:F0} s ≈ {11:F2} 圈，船的绕月周期 {12:F0}s）",
                    (target.positionBody - chaser.positionBody).magnitude,
                    (target.velocityBody - chaser.velocityBody).magnitude,
                    chaser.GroupId, ovC.semiMajorAxis / 1000.0, ovC.eccentricity,
                    target.GroupId, ovT.semiMajorAxis / 1000.0, ovT.eccentricity,
                    chaser.ladderOwnsFarField, physicsTick, physicsTick - _startTick,
                    (physicsTick - _startTick) / _shipPeriod, _shipPeriod));
            }
        }

        public static void Run()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null)
            {
                Log.Warning("[绕月交会验收] SpaceGameComp 未找到");
                return;
            }

            double mu = spaceGame.GetBodyGM(MOON_BODY_ID);
            var sb = new StringBuilder();
            sb.Append("\n══ 绕月交会验收（非根天体路径：体心系 + 解析圆轨模式 + 天体偏移）══");

            // 找"绕月 + 挂了交会引导"的编队，以及它的目标（同一天体）
            SpaceEntityGroup? chaser = null, target = null;
            foreach (var g in spaceGame.GetAllGroups())
            {
                if (!g.IsActive || g.CelestialBodyId != MOON_BODY_ID || !g.HasRendezvousGuidance) continue;
                var t = spaceGame.GetGroupById(g.rendezvousTargetId);
                if (t == null || !t.IsActive || t.CelestialBodyId != MOON_BODY_ID) continue;
                chaser = g; target = t;
                break;
            }

            if (chaser == null || target == null)
            {
                // ⚠ 撞毁会**销毁实体**（`[Collision] … Entities destroyed`）⇒ "找不到成对编队"有两种成因，
                //   必须分开报：**绕月编队本来就没铺**（UNCHECKED）vs **铺了但已经撞毁/消失**（FAIL）。
                bool anyMoonGroup = false;
                foreach (var g in spaceGame.GetAllGroups())
                    if (g.IsActive && g.CelestialBodyId == MOON_BODY_ID) { anyMoonGroup = true; break; }
                if (anyMoonGroup)
                    sb.Append("\n  **FAIL**：绕月有编队，但找不到「已挂交会引导的成对」——最可能是一艘**已撞月销毁**" +
                              "（看 `[Collision] … CRASHED into 月球`）。这就是本场景要抓的失败形态。");
                else
                    sb.Append("\n  UNCHECKED 绕月（bodyId=2）下没有任何编队 —— 先点「绕月交会·放置」。");
                sb.Append("\n（**0 样本不折算成 PASS**；本行是「没测」不是「测过了」。）");
                Log.Message(sb.ToString());
                return;
            }

            // ── ① 两船都活着（撞毁会销毁实体 ⇒ 走到这里就等于两艘都还在）──
            double rSelf = chaser.positionBody.magnitude, rTgt = target.positionBody.magnitude;
            bool alive = IsFinite(chaser.positionBody) && IsFinite(chaser.velocityBody)
                      && IsFinite(target.positionBody) && IsFinite(target.velocityBody);
            sb.Append(string.Format("\n  ① 两船都活着：{0}｜G{1} r={2:F0}km v={3:F1}｜G{4} r={5:F0}km v={6:F1}",
                alive ? "✓" : "✗", chaser.GroupId, rSelf / 1000.0, chaser.velocityBody.magnitude,
                target.GroupId, rTgt / 1000.0, target.velocityBody.magnitude));

            // ── ② 到达判据（游戏自己的唯一求值口）──
            bool met = chaser.IsRendezvousGoalMet(target, mu, out double phaseErrDeg);
            DVector3 dR = target.positionBody - chaser.positionBody;
            DVector3 dV = target.velocityBody - chaser.velocityBody;
            sb.Append(string.Format("\n  ② 到达判据（`IsRendezvousGoalMet`）：{0}｜距离 {1:F0} m｜相对速度 {2:F2} m/s｜" +
                                    "沿轨相位差 {3:F1}°｜目标={4}",
                met ? "✓" : "✗", dR.magnitude, dV.magnitude, phaseErrDeg,
                chaser.EffectiveRendezvousGoal.ToString()));

            // ── ③ 阶梯在位（"跑的是 MJ 路径"，不抓日志）──
            sb.Append(string.Format("\n  ③ 阶梯在位（`ladderOwnsFarField`）：{0}",
                chaser.ladderOwnsFarField ? "✓" : "✗（若为 ✗：这条不是阶梯在管，读数不可用于验证阶梯）"));

            // ── 读数（绕月要素：判"有没有被非物理地换过状态"）──
            var ovSelf = OrbitView.FromStateVectors(chaser.positionBody, chaser.velocityBody, mu, 0.0,
                DVector3.up, 1_500_000.0);
            var ovTgt = OrbitView.FromStateVectors(target.positionBody, target.velocityBody, mu, 0.0,
                DVector3.up, 1_500_000.0);
            sb.Append(string.Format("\n  读数：圆轨模式 G{0}={1} / G{2}={3}｜G{4} 要素 a={5:F0}km e={6:F4} PeR={7:F0}km ApR={8:F0}km｜" +
                                    "G{9} 要素 a={10:F0}km e={11:F4} PeR={12:F0}km ApR={13:F0}km（μ月={14:E2}）",
                chaser.GroupId, chaser.IsCircularOrbit, target.GroupId, target.IsCircularOrbit,
                chaser.GroupId, ovSelf.semiMajorAxis / 1000.0, ovSelf.eccentricity, ovSelf.PeR / 1000.0, ovSelf.ApR / 1000.0,
                target.GroupId, ovTgt.semiMajorAxis / 1000.0, ovTgt.eccentricity, ovTgt.PeR / 1000.0, ovTgt.ApR / 1000.0, mu));
            // 月球半径 1500 km ⇒ PeR < 1560 km 就是"掠月/要撞"的量级，直接标出来
            if (ovSelf.PeR / 1000.0 < 1560.0 || ovTgt.PeR / 1000.0 < 1560.0)
                sb.Append("\n  ⛔ **近月点已低于月面 +60 km** ⇒ 这一对已经有撞月风险（本行就是给绕月 bug 用的判据）");

            sb.Append(string.Format("\n  ═══ 总判定：{0} ═══",
                (alive && met && chaser.ladderOwnsFarField) ? "PASS（三条全过）" : "FAIL / 未达"));
            Log.Message(sb.ToString());
        }

        private static bool IsFinite(DVector3 v) =>
            !double.IsNaN(v.x) && !double.IsInfinity(v.x) &&
            !double.IsNaN(v.y) && !double.IsInfinity(v.y) &&
            !double.IsNaN(v.z) && !double.IsInfinity(v.z);
    }
}
