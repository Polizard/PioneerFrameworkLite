using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **反推着陆的驱动**（2026-09-22）——游戏侧接线，形状**照抄** `RocketLaunchController`：
    /// 每物理步 `EndBurn`（清上一步残余）→ 调纯核算一步 → `StartBurn(dv)`。
    ///
    /// ## 分工（本库纪律：算式在纯核、驱动在游戏侧）
    /// · **唯一算式** = <see cref="LandingGuidanceCore.Step" />（含**点火锁存**，见那个 `State`）；
    /// · 本类只做三件**驱动**的事：**取状态**（高度 / 下降率 / g）、**调核**、**把指令变成 StartBurn**。
    ///   ⇒ "为什么没刹住"永远只有一个地方可查（核），驱动层不重算任何判据。
    ///
    /// ## ⚠ 与 MJ 的关系
    /// MJ 的着陆是 `LandingAutopilot` + `HoverslamSimulation`（ODE/PSG/alglib）那一大套；
    /// 我们拒绝第三方依赖（全库零依赖），且 PFL 的规模不需要那套 ⇒ 用**垂直反推闭式**。
    /// 取的是它"**提交解 + 按时间飞**"的形状，不是它的实现。
    ///
    /// ## 现状（登记，别当成已完成）
    /// · **只做垂直反推那一段**（水平速度应由前面的段消掉/很小）——水平机动是后续；
    /// · 下落**不做大气阻力**（PFL 的火箭返回段在真空段结束，进大气由 skyfaller 接管）；
    /// · 载体用**编队自身的引擎**（`StartBurn`），不走 `Skyfaller`。
    /// </summary>
    public class LandingController : GameComponent
    {
        public LandingController(Game game)
        {
            // 一次性自证：本组件**确实被实例化**（否则后面一切都不成立）。
            Log.Message("[着陆] LandingController 已实例化（GameComponent 注册成功）");
        }

        /// <summary>一次着陆任务。</summary>
        public class LandingState : IExposable
        {
            public int groupId;
            public int bodyId;
            /// <summary>纯核的**跨步锁存**（点火标记）——必须跨步保存，否则"点着了又自己关掉"（见核的注释）。</summary>
            public LandingGuidanceCore.State latch;
            public int startedTick;
            public bool done;

            public void ExposeData()
            {
                Scribe_Values.Look(ref groupId, "groupId");
                Scribe_Values.Look(ref bodyId, "bodyId");
                Scribe_Values.Look(ref latch.ignited, "latchIgnited", false);
                Scribe_Values.Look(ref startedTick, "startedTick");
                Scribe_Values.Look(ref done, "done", false);
            }
        }

        private List<LandingState> active = new List<LandingState>();

        /// <summary>制导参数（默认档；将来要按船/场景可配就提到这里）。</summary>
        private static readonly LandingGuidanceCore.Params Pars = LandingGuidanceCore.Params.CreateDefault();

        /// <summary>发射控制器用的同一个闸门：只在物理步上跑（与 `physicsTick` 同拍）。</summary>
        private static SpaceGameComp? SpaceGame => Current.Game?.GetComponent<SpaceGameComp>();
        private static CelestialBodyManager? BodyMgr => Current.Game?.GetComponent<CelestialBodyManager>();
        private static TimeScaleManager? Tsm => Current.Game?.GetComponent<TimeScaleManager>();

        /// <summary>开始一次反推着陆（供 `RocketBoosterReturnUtility` 等调用）。</summary>
        public static void BeginLanding(int groupId, int bodyId)
        {
            var lc = Current.Game?.GetComponent<LandingController>();
            if (lc == null)
            {
                // ⚠ **不许静默返回**（2026-09-22 教训：第一次游戏内跑，日志里只有"开始"没有下文，
                //   而我无法区分"组件没注册"与"tick 没跑"⇒ 只能靠猜）。
                Log.Error("[着陆] BeginLanding 失败：**LandingController 未注册**（GetComponent 返回 null）" +
                          "⇒ 着陆任务没有启动。查组件注册（见本类的 GameComponent 生命周期）。");
                return;
            }
            if (lc.active.Exists(s => s.groupId == groupId)) return;
            lc.active.Add(new LandingState
            {
                groupId = groupId,
                bodyId = bodyId,
                startedTick = Find.TickManager.TicksGame,
            });
            Log.Message(string.Format("[着陆] 开始反推着陆：group={0} body={1}（任务队列长度={2}）",
                groupId, bodyId, lc.active.Count));
        }

        /// <summary>心跳节拍（真实 tick）。每 120 tick 无条件打一行 ⇒ "tick 到底有没有被调"**可判定**，
        /// 不再靠"没日志"去反推（2026-09-22 教训：那条反推把我引到"组件没注册"的错误方向，
        /// 而实际上组件是注册的、任务也入队了）。</summary>
        private int lastHeartbeatTick = -1000;

        public override void GameComponentTick()
        {
            base.GameComponentTick();

            int hbTick = Find.TickManager.TicksGame;
            if (hbTick - lastHeartbeatTick >= 120)
            {
                lastHeartbeatTick = hbTick;
                Log.Message(string.Format("[着陆·心跳] tick={0} 任务数={1} 组件在={2}",
                    hbTick, active.Count, Current.Game?.GetComponent<LandingController>() != null));
            }

            if (active.Count == 0) return;
            var sg = SpaceGame;
            if (sg == null) return;

            int tick = Find.TickManager.TicksGame;
            if (tick % SpaceGameComp.UPDATE_INTERVAL_TICKS != 0) return;   // 与物理步同拍

            double dt = SpaceGameComp.UPDATE_INTERVAL_TICKS;
            if (Tsm != null) dt *= Tsm.PhysicsSpeedMultiplier;

            for (int i = active.Count - 1; i >= 0; i--)
            {
              try
              {
                var st = active[i];
                var group = sg.GetGroupById(st.groupId);
                if (group == null) { active.RemoveAt(i); continue; }

                // ── 取状态：高度 / 径向下降率 / g(h) ──
                var mgr = BodyMgr;
                if (mgr == null) { active.RemoveAt(i); continue; }
                double radius = Launch.GetSurfaceState.BodyRadius(mgr, st.bodyId);
                double gm = sg.GetBodyGM(st.bodyId);
                DVector3 r = group.localPosition;
                DVector3 v = group.localVelocity;
                double rMag = r.magnitude;
                if (!(rMag > 1.0) || !(gm > 0.0)) { active.RemoveAt(i); continue; }

                double altitude = rMag - radius;                       // 相对**表面**的高度
                DVector3 up = r / rMag;
                double radialSpeed = -DVector3.Dot(v, up);             // **下降率为正**（核的口径）
                double g = gm / (rMag * rMag);                          // 当前高度的重力

                var src = sg.GetGroupPropulsionSource(group);
                double mass = sg.GetGroupMass(group, src!);
                double dryMass = sg.GetGroupMass(group, null!);       // null ⇒ 干质量口径
                double ve = src != null ? src.EffectiveVe : 0.0;
                double fTotal = 0.0;
                if (src != null)
                    foreach (var n in src.Networks) fTotal += n.Thrust;
                double aMax = mass > 0.0 ? fTotal / mass : 0.0;

                var outStep = LandingGuidanceCore.Step(altitude, radialSpeed, g,
                    mass, dryMass, ve, aMax, Pars, ref st.latch);

                switch (outStep.action)
                {
                    case LandingGuidanceCore.Action.Coast:
                        if (group.HasActiveBurn) group.EndBurn();
                        break;

                    case LandingGuidanceCore.Action.Burn:
                    {
                        if (group.HasActiveBurn) group.EndBurn();
                        // 指令加速度 → 本步 Δv（沿**径向向上**= 反推），交 StartBurn 按全油门烧
                        DVector3 dv = up * (outStep.targetAccel * dt);
                        group.burnUseRcs = false;      // 反推用主推（RCS 推不动整船）
                        group.burnCommandAccel = 0.0;  // 不锁常量加速度 ⇒ 按可用烧
                        group.StartBurn(dv);
                        break;
                    }

                    case LandingGuidanceCore.Action.Landed:
                    {
                        group.EndBurn();
                        group.exemptPlanetCollision = false;
                        Log.Message(string.Format(
                            "[着陆] group={0} **着陆完成**：高度 {1:F1} m、下降率 {2:F2} m/s｜{3}",
                            group.GroupId, altitude, radialSpeed, outStep.why));
                        Messages.Message(string.Format("Group {0} 着陆完成", group.GroupId),
                            MessageTypeDefOf.TaskCompletion);
                        active.RemoveAt(i);
                        continue;
                    }

                    case LandingGuidanceCore.Action.Insufficient:
                    {
                        group.EndBurn();
                        Log.Error(string.Format(
                            "[着陆] group={0} **刹不住**：{1}（高度 {2:F0} m、下降率 {3:F1} m/s）",
                            group.GroupId, outStep.why, altitude, radialSpeed));
                        Messages.Message(string.Format("Group {0} 反推着陆失败：{1}", group.GroupId, outStep.why),
                            MessageTypeDefOf.NegativeEvent);
                        active.RemoveAt(i);
                        continue;
                    }
                }

                if (Prefs.DevMode && tick % 60 == 0)
                    Log.Message(string.Format(
                        "[着陆·诊断] group={0} h={1:F0} m 下降率={2:F1} m/s g={3:F2} aMax={4:F2} ⇒ {5}｜{6}",
                        group.GroupId, altitude, radialSpeed, g, aMax, outStep.action, outStep.why));
              }
              catch (System.Exception e)
              {
                  // ⚠ **不许静默吞异常**（2026-09-22 教训：第一次游戏内跑，着陆控制器"启动了但一次都没动"，
                  //   而日志里连一行错都没有 ⇒ 只能靠猜。RimWorld 会把组件 tick 的异常吞掉并打红字，
                  //   但**不一定进 Player.log** ⇒ 这里自己抓、自己报。）
                  Log.Error(string.Format("[着陆] group={0} 控制器异常（已停止该任务）：{1}",
                      active[i].groupId, e));
                  active.RemoveAt(i);
              }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref active, "activeLandings", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && active == null)
                active = new List<LandingState>();
        }
    }
}
