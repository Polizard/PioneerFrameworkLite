using System.Collections.Generic;
using System.Linq;
using MechJebLib.Primitives;
using UnityEngine;
using Verse;
using RimWorld;

namespace PioneerFramework
{
    /// <summary>
    /// **交会窗口（选目标 + 接合/断开）**——照 MJ 的 `MechJebModuleRendezvousAutopilotWindow` 的
    /// 控件面做（2026-09-28，用户："把 MJ 的那套 UI 从机动节点下面迁移出来，然后加一个选择目标的 UI"）。
    ///
    /// ## 与旧入口的关系
    /// 旧的「选时间/燃料档」菜单（`SpaceGroupVisualManager.OpenRendezvousLegMenu` →
    /// `SpaceGameComp.BuildRendezvousLawOptions`/`BeginRendezvousLaw`）是**已作废的自制律**的旋钮
    /// （腿长/螺旋价），而新路（MJ 阶梯）**没有"腿长"这个旋钮** ⇒ 那一串档位按钮是**冗余**的（用户原话）。
    /// 本窗口是它的替代：**选目标 → 接合**，其余交给阶梯自己决定（它的计划与价格打在 `[阶梯]` 日志里）。
    /// ⚠ 按"先有替代再撤旧的"：旧入口此刻**还在**，等本窗口在游戏内确认可用后再撤。
    ///
    /// ## MI 的控件面对照（照搬该照搬的，丢掉宿主专有的）
    /// · "Select a target to rendezvous with." ⇒ 目标列表（**只列同一中心天体**的编队，同 MJ 的同志要求）
    /// · "Engage / Disengage autopilot" ⇒ 「接合（挂阶梯引导）」/「断开」
    /// · Status 行 ⇒ 状态（引导中/待命）+ 距离/相对速度/相位差
    /// · `Auto-warp` ✗ KSP 专有，不要（RimWorld 的速度档与高度无关）
    /// · "Max # of phasing orbits" ✗ 我们的阶梯把它当内部政策（`Params.MaxPhasingOrbits`），不摆给玩家
    /// </summary>
    public class Window_Rendezvous : Window
    {
        private readonly SpaceGameComp _spaceGame;
        private readonly SpaceEntityGroup _self;
        private int _targetId = -1;
        private Vector2 _scroll;

        private const float RowH = 30f;
        private const float Gap = 8f;

        public Window_Rendezvous(SpaceGameComp spaceGame, SpaceEntityGroup self)
        {
            _spaceGame = spaceGame;
            _self = self;
            closeOnCancel = true;
            draggable = true;
        }

        public override Vector2 InitialSize => new Vector2(560f, 520f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), string.Format("交会引导 · G{0}", _self.GroupId));
            Text.Font = GameFont.Small;
            float y = 36f;

            // ── ① 选目标（同 MJ：只列同一中心天体的）────────────────────────────
            Widgets.Label(new Rect(0f, y, inRect.width, 24f), "选择要交会的目标（同一中心天体）：");
            y += 26f;

            List<SpaceEntityGroup> candidates = _spaceGame.GetAllGroups()
                .Where(g => g != null && g.IsActive && g.GroupId != _self.GroupId
                            && g.CelestialBodyId == _self.CelestialBodyId)
                .OrderBy(g => g.GroupId)
                .ToList();

            float listH = Mathf.Min(180f, Mathf.Max(RowH, candidates.Count * (RowH + 2f)));
            var listRect = new Rect(0f, y, inRect.width, listH);
            if (candidates.Count == 0)
            {
                Widgets.Label(listRect, "　（同一中心天体上没有其它可用编队 ⇒ 先派一条船过去）");
            }
            else
            {
                var view = new Rect(0f, 0f, listRect.width - 20f, candidates.Count * (RowH + 2f));
                Widgets.BeginScrollView(listRect, ref _scroll, view);
                float ry = 0f;
                foreach (SpaceEntityGroup g in candidates)
                {
                    (double distKm, double relVel, double phaseDeg, bool ok) = Probe(g);
                    string label = ok
                        ? string.Format("G{0} · 距离 {1:F0} km · 相对速度 {2:F1} m/s · 相位差 {3:F0}°",
                            g.GroupId, distKm, relVel, phaseDeg)
                        : string.Format("G{0} · （轨道状态不可预测）", g.GroupId);

                    var rowRect = new Rect(0f, ry, view.width, RowH);
                    if (_targetId == g.GroupId)
                        Widgets.DrawBoxSolid(rowRect, new Color(0.18f, 0.42f, 0.55f, 0.55f));
                    if (Widgets.ButtonText(rowRect, label))
                        _targetId = g.GroupId;
                    ry += RowH + 2f;
                }
                Widgets.EndScrollView();
            }
            y += listH + Gap;

            // ── ② 选中目标 + 接合/断开 ──────────────────────────────────────────
            SpaceEntityGroup target = _targetId >= 0 ? _spaceGame.GetGroupById(_targetId) : null;
            if (target == null)
            {
                Widgets.Label(new Rect(0f, y, inRect.width, 24f), "未选目标。");
                y += 26f;
            }
            else
            {
                (double distKm, double relVel, double phaseDeg, bool ok) = Probe(target);
                Widgets.Label(new Rect(0f, y, inRect.width, 24f), ok
                    ? string.Format("目标 G{0}：距离 {1:F0} km · 相对速度 {2:F1} m/s · 相位差 {3:F0}°",
                        target.GroupId, distKm, relVel, phaseDeg)
                    : string.Format("目标 G{0}：轨道状态不可预测（稍后再试）", target.GroupId));
                y += 26f;

                // 到位判据与"停下尺度"是**阶梯读的两个门**（目的给距离容差、停下属另一个）⇒ 摆出来给玩家看，
                // 但不在这里改（它们由 `EffectiveRendezvousGoal`/`rendezvousMatchDist` 一处定义）。
                Widgets.Label(new Rect(0f, y, inRect.width, 44f), string.Format(
                    "到位容差 {0:F0} m（目的口径）· 停下尺度 {1:F0} m\n"
                    + "计划与价格由阶梯在运行时给出（日志 `[阶梯] … Δv 合计`）——UI 不另算一份，免得两份口径打架。",
                    _self.EffectiveRendezvousGoal.distanceTol, _self.rendezvousMatchDist));
                y += 50f;
            }

            // ── ③ 状态 + 按钮 ──────────────────────────────────────────────────
            bool engaged = _self.HasRendezvousGuidance;
            GUI.color = engaged ? new Color(0.35f, 1f, 0.55f, 1f) : new Color(0.72f, 0.76f, 0.8f, 1f);
            Widgets.Label(new Rect(0f, y, inRect.width, 24f), engaged
                ? string.Format("状态：引导中（{0}）", _self.rendezvousArmed ? "已接合" : "已登记但未接合")
                : "状态：待命");
            GUI.color = Color.white;
            y += 30f;

            bool canEngage = target != null && !engaged;
            if (!canEngage) GUI.color = new Color(1f, 1f, 1f, 0.45f);
            if (Widgets.ButtonText(new Rect(0f, y, 200f, 34f), "接合（挂阶梯引导）") && canEngage)
            {
                // 与调试场景同一条入口：登记 → 接合（登记即生效，阶梯不需要"出发段"）
                int tick = (int)System.Math.Round(TimeScaleManager.GetPhysicsTickSafe());
                _self.RegisterRendezvousGuidance(target.GroupId, tick, prograde: true, useNetworks: null);
                _self.ArmRendezvousGuidance();
            }
            GUI.color = Color.white;

            if (!engaged) GUI.color = new Color(1f, 1f, 1f, 0.45f);
            if (Widgets.ButtonText(new Rect(210f, y, 140f, 34f), "断开") && engaged)
                _self.ClearRendezvousGuidance();
            GUI.color = Color.white;

            if (Widgets.ButtonText(new Rect(inRect.width - 110f, y, 110f, 34f), "关闭"))
                Close();
        }

        /// <summary>
        /// 取本机与目标的相对状态。**走 `OrbitAdapter.TryToOrbitView`**（与制导同一条构造路径）——
        /// 不直接读 `positionBody`：那两个字段只在少数点被写入，会给出滞后读数（P-1 §6 R5 的实测教训）。
        /// </summary>
        private (double distKm, double relVel, double phaseDeg, bool ok) Probe(SpaceEntityGroup other)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            double tick = TimeScaleManager.GetPhysicsTickSafe();
            if (mgr == null
                || !OrbitAdapter.TryToOrbitView(_self, mgr, tick, out OrbitView ovSelf)
                || !OrbitAdapter.TryToOrbitView(other, mgr, tick, out OrbitView ovTgt))
                return (0.0, 0.0, 0.0, false);

            (V3 rSelf, V3 vSelf) = ovSelf.RightHandedStateVectorsAtUT(tick);
            (V3 rTgt, V3 vTgt) = ovTgt.RightHandedStateVectorsAtUT(tick);
            double dist = (rTgt - rSelf).magnitude;
            double relVel = (vTgt - vSelf).magnitude;
            double phaseDeg = RendezvousLadder.PhaseDifferenceDeg(ovSelf, ovTgt, tick);
            return (dist / 1000.0, relVel, phaseDeg, true);
        }
    }
}
