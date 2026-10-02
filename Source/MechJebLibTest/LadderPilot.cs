using System;
using System.Collections.Generic;
using MechJebLib.Primitives;
using PioneerFramework.Pure.Tests;   // 台架的船与靶（RendezvousShip / ITarget / CircularTarget / EllipticTarget）
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **阶梯驾驶员（共享台架）**：队列空就问 `RendezvousLadder`，节点到点开始烧（方向 = MJ 节点语义），
    /// 直到这一发打完再取下一条；出力走**同一个** <see cref="RendezvousShip" /> 执行器
    /// （锥内才出力、RCS 全矢量、质量随耗料减）。
    ///
    /// ## 批5.B（2026-09-28）：从 `LadderVsLawComparison` 提上来 + 补上游戏侧执行器政策
    /// 提上来是因为旧律文件要归档（`LadderVsLawComparison` 的"律"那一侧随之作废），
    /// 而**活路径（阶梯）需要自己的端到端台架**。
    /// 补的政策 = **差额地板 + 反向收手**，走 <see cref="BurnStep.Deficit" />（**唯一算式**：游戏与台架同一份）。
    /// ⚠ 此前台架的阶梯侧**没有**这条政策 ⇒ 小 Δv 节点照烧；加上它才是"律在算、船真动"的同条件对照
    /// （本库栽过 5 次的坑：台架必须建模游戏侧执行器政策；T14 测的正是它）。
    /// </summary>
    public sealed class LadderPilot
    {
        public readonly RendezvousLadder.Params Params = new RendezvousLadder.Params
        {
            // ⚠ 283 不是随手取的：`RendezvousLadder` 的收工门是 `DesiredDistance×1.05 + 2`
            //   （`RendezvousLadder:57`），要让它落在台架判据 300 m 之内 ⇒ 目标距离取 ≤283.8。
            //   若直接取 300，阶梯会在 310 m 处**合法收工**，而判据按"≤300"判它没到（本台架第一版就这么误报过）。
            //   （游戏侧取的是 `EffectiveRendezvousGoal.distanceTol`，门 = 1.05×门+2 ⇒ 同族 5% 松弛，已登记。）
            DesiredDistance = 283.0,
            MaxClosingSpeed = 100.0,
            MaxPhasingOrbits = 5.0,
            MinRadius = 5.1e6,
        };

        /// <summary>**最小重规划间隔**（秒）：队列空但离上次规划不足这么久 ⇒ 不重规划。
        /// 防"每步重规划"（阶梯给出小修正 ⇒ 一步打完 ⇒ 队列又空 ⇒ 又问……）。</summary>
        public double MinReplanSeconds = 30.0;

        /// <summary>**数值地板**（m/s）—— 与游戏 `SpaceGameComp.BurnTargetVFloor` **同值同义**：
        /// 差额小于它即数值达标（末步冲量与测量之间隔着一拍框架，残数永远差 ~1e-3）。</summary>
        public double FloorMps = 1e-3;

        /// <summary>**差额忽略地板**（m/s）—— 与游戏 `SpaceGameComp.BurnDeficitIgnoreFloor` **同值同义**：
        /// 差额小于它 ⇒ **不发指令**（方向已进入噪声，追它只会导致 180° 掉头）。
        /// ⚠ 它必须 **小于完成容差的速度门**（游戏里 1.0 vs 10 m/s），否则"差一点永远卡住"。
        /// 这条就是"死区不憋死"要考的政策。</summary>
        public double DeadbandMps = 1.0;

        private readonly List<PlanNode> _queue = new List<PlanNode>();
        private int _cur;
        private DVector3 _dvRem = DVector3.zero;
        private DVector3 _prevDir = DVector3.zero;
        private double _lastPlanAt = double.NegativeInfinity;

        public bool Done { get; private set; }
        public int PlanCount;
        public readonly List<string> Rungs = new List<string>();

        /// <summary>一次重规划的**指纹**（A.2「计划何时该变」的度量面，2026-10-01 加）。
        /// ⚠ **只增读数、不改行为**：在现有的重规划点原样抄一份（级 + 逐发 `(ut, dV)`）。</summary>
        public sealed class ReplanRecord
        {
            public double T;
            public RendezvousLadder.Rung Rung;
            public readonly List<(double Ut, DVector3 Dv)> Nodes = new List<(double, DVector3)>();
        }

        /// <summary>逐次重规划的指纹（与 <see cref="Rungs" /> 同序、同长度）。</summary>
        public readonly List<ReplanRecord> ReplanLog = new List<ReplanRecord>();

        /// <summary>被"地板/反向"挡下的节点数（诊断：0 = 地板从未起作用 ⇒ 该用例没考到政策）。</summary>
        public int SkippedByPolicy;
        /// <summary>**料尽**：主网 + RCS 都没推力了（此后任何节点都交付不了）。诊断 + 让台架能干净收尾
        /// —— 否则"差额永远到不了地板"会让这一发永不退休、台架空转到超时（第一版实测）。</summary>
        public bool FuelExhausted { get; private set; }

        /// <summary>**逐发账**（诊断）：每一发节点的 计划 Δv / 实际交付 / 步数 / 退休原因。
        /// 为什么要有它：第一版"某一发把料烧光"只能从总量看出来，看不出**是哪一发、为什么退休不了**。</summary>
        public readonly List<string> BurnLog = new List<string>();
        private double _nodeDv0, _nodeDelivered, _nodeSteps;
        private bool _nodeStarted;

        /// <summary>**卡死诊断**：当前这一发的（队列位置 / 剩余差额 / 已烧步数）。红字要能定位到"哪一发卡住"。</summary>
        public string DebugStuck => string.Format("发{0}/{1} 余差额={2:F2} m/s 步={3:F0}（属{4}）",
            _cur + 1, _queue.Count, _dvRem.magnitude, _nodeSteps, Done ? "已完" : "进行中");

        public DVector3 Step(RendezvousShip ship, ITarget target, double mu, double t, double dt)
        {
            if (Done) return DVector3.zero;
            if (ship.MainAccel <= 0.0 && ship.RcsAccel <= 0.0)
            {
                FuelExhausted = true;
                if (_dvRem.magnitude > 0.0)
                    BurnLog.Add(string.Format("★料尽于未完成的一发：计划={0:F1} 已交付={1:F1} 余差额={2:F1} 步={3:F0}",
                        _nodeDv0, _nodeDelivered, _dvRem.magnitude, _nodeSteps));
                Done = true;
                return DVector3.zero;
            }

            // ① 队列空（或上一发打完）⇒ 问阶梯。**只在队列空时问** = 上游第 0 级"已有节点就执行"
            //   由调用方承担；这条前置同时提供了上游靠"节点存在"得到的稳定性。
            while (_cur >= _queue.Count && t - _lastPlanAt >= MinReplanSeconds)
            {
                _lastPlanAt = t;
                var selfOv = OrbitView.FromStateVectors(ship.Pos, ship.Vel, mu, t, DVector3.up, Params.MinRadius);
                var tgtOv = OrbitView.FromStateVectors(target.PosAt(t), target.VelAt(t), mu, t,
                    target.PlaneNormalAt(t), Params.MinRadius);
                var rung = RendezvousLadder.MakePlan(selfOv, tgtOv, t, Params, out List<PlanNode> nodes, out string status);
                PlanCount++;
                Rungs.Add(string.Format("T+{0:F0}s {1}：{2}", t, rung, status));
                var rec = new ReplanRecord { T = t, Rung = rung };
                foreach (PlanNode n in nodes) rec.Nodes.Add((n.ut, n.dV));
                ReplanLog.Add(rec);
                _queue.Clear();
                _queue.AddRange(nodes);
                _cur = 0;
                _dvRem = DVector3.zero;
                _nodeStarted = false;
                if (_queue.Count == 0)
                {
                    Done = rung == RendezvousLadder.Rung.Done;
                    return DVector3.zero;
                }
            }

            // ② 等到这一发的时刻
            if (_cur >= _queue.Count) return DVector3.zero;
            PlanNode node = _queue[_cur];
            // ⚠ **必须用显式"已开始"标志**，不能拿"差额为零"当"还没开始"：
            //   差额被烧到 0 之后、退休判据还没跑到之前，那个门会把**同一个节点重新装载** ⇒
            //   同一发无限重烧（第一版实测：单发 54 m/s 被重烧 183 次 = 10 km/s，把整罐料烧光）。
            if (!_nodeStarted)
            {
                if (t + dt < node.ut) return DVector3.zero;    // 还没到点
                _dvRem = node.dV;                              // 到位 ⇒ 记下这一发的差额**向量**
                _nodeDv0 = node.dV.magnitude;
                _nodeDelivered = 0.0;
                _nodeSteps = 0.0;
                _prevDir = DVector3.zero;
                _nodeStarted = true;
                BurnLog.Add(string.Format("ut={0,7:F0} ⇒开始 计划={1,8:F1} m/s（队列 {2}/{3}）",
                    node.ut, _nodeDv0, _cur + 1, _queue.Count));
            }
            _nodeSteps += 1.0;

            // ③ ★ 游戏侧执行器政策（**逐条对应** `SpaceGameComp:1599-1620`）：
            //    先走唯一算式的"数值地板 + 反向收手"，再走"差额忽略地板"（小于它就不发指令）。
            BurnStep.Deficit(_dvRem, _prevDir, FloorMps, out DVector3 dir, out double rem, out bool reversed);
            bool belowDeadband = rem > 0.0 && rem < DeadbandMps;
            if (rem <= 0.0 || belowDeadband)
            {
                if (reversed || belowDeadband) SkippedByPolicy++;   // 诊断：0 = 该用例没考到政策
                BurnLog.Add(string.Format("ut={0,7:F0} 计划={1,8:F1} 交付={2,8:F1} 步={3,4:F0} 余={4,6:F2}｜因={5}",
                    node.ut, _nodeDv0, _nodeDelivered, _nodeSteps, _dvRem.magnitude,
                    reversed ? "反向收手" : (belowDeadband ? "差额地板" : "达到数值地板")));
                RetireNode();
                return DVector3.zero;
            }

            // ④ 出力（一次一步）。选网走**同一个**权威
            bool aligned = DVector3.Dot(ship.MainAxis, dir) >= RendezvousShip.ThrustConeCos;
            var kind = RendezvousGuidanceCore.SelectActuator(rem, ship.MainAccel, ship.RcsAccel, aligned);
            DVector3 acc = ship.ApplyThrust(kind, dir, rem, dt);
            _dvRem -= acc * dt;                                // 差额按**实际交付**扣减（活差额律同构）
            _nodeDelivered += acc.magnitude * dt;
            // ⚠ 交付把差额吃掉之后**当场退休**：否则下一步的"还没开始"门会重新装载同一发（见上）。
            if (_dvRem.magnitude < FloorMps) RetireNode();
            _prevDir = dir;
            return acc;
        }

        /// <summary>退休当前节点：下一发（或下一次规划）。</summary>
        private void RetireNode()
        {
            _cur++;
            _dvRem = DVector3.zero;
            _nodeStarted = false;
        }
    }
}
