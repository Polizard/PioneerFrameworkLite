using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **P0 适配层端到端探针**（2026-09-18）：把"游戏里选中的编队"真的走一遍
    /// `OrbitView` → `OperationCircularize` → `PlanNode` → `OrbitAdapter.TryQueuePlan` → 机动队列。
    ///
    /// ## 它回答什么问题
    /// 单元测试（`OperationTests`）已经证明"喂合成状态时，这个 Operation 算得对"。
    /// 本探针要证明的是**另一半**：真实游戏状态经过换系之后，
    /// **计划真的能排进队列、而且算出来的轨道是圆轨**。
    ///
    /// **做法是"先预测、再入队"**：先用 `OrbitAdapter.ApplyForPreview` 把 Δv 施加到当前状态上，
    /// 打出预测的偏心率与半长轴（**这一行是判据**：偏心率应当 ≈ 0），然后再真正入队。
    /// 预测用的是适配层的传播，不入队、不改任何状态。
    ///
    /// 【边界】它会**修改游戏状态**（往队列里排一条机动）——这是"端到端"的定义决定的。
    /// 所以只挂在调试页签上、由人点。
    /// </summary>
    public static class OperationProbe
    {
        /// <summary>对当前选中的编队排一条"在远心点圆化"，并打印计划与预测结果。</summary>
        public static void CircularizeSelectedGroup()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();

            if (spaceGame == null)
            {
                Messages.Message("不在太空场景（无 SpaceGameComp）", MessageTypeDefOf.RejectInput);
                return;
            }

            SpaceEntityGroup g = spaceGame.GetGroupById(SpaceGroupVisualManager.GetSelectedGroupId());
            if (g == null)
            {
                Messages.Message("先在世界上选中一个编队", MessageTypeDefOf.RejectInput);
                return;
            }

            double tick = TimeScaleManager.GetPhysicsTickSafe();

            if (!OrbitAdapter.TryToOrbitView(g, celestialMgr, tick, out OrbitView ov))
            {
                Messages.Message($"G{g.GroupId}：查不到中心天体（bodyId={g.CelestialBodyId}），拒绝规划", MessageTypeDefOf.RejectInput);
                return;
            }

            var op = new OperationCircularize();
            // "远心点圆化"是经典选择（那一处最省 Δv）。候选表顺序：APOAPSIS / PERIAPSIS / X_FROM_NOW / ALTITUDE
            op.TimeSelector._currentTimeRef = 0;

            List<PlanNode>? nodes = op.MakeNodes(ov, tick, new FixedTargetView(ov, g.GroupId));

            var sb = new StringBuilder();
            sb.Append("[OrbitAdapter 端到端] G").Append(g.GroupId)
              .Append(g.IsCircularOrbit ? " 圆轨模式" : " 自由飞行")
              .Append(" bodyId=").Append(g.CelestialBodyId)
              .Append(" tick=").Append(tick.ToString("F0"))
              .Append("\n  机动前：").Append(ov.ToString())
              .Append("\n     e=").Append(ov.eccentricity.ToString("E3"))
              .Append("  a=").Append((ov.semiMajorAxis / 1000.0).ToString("F0")).Append("km")
              .Append("  T=").Append(ov.period.ToString("F0")).Append("s");

            if (nodes == null)
            {
                sb.Append("\n  规划失败：").Append(op.GetErrorMessage());
                Log.Message(sb.ToString());
                Messages.Message($"G{g.GroupId} 圆化规划失败：{op.GetErrorMessage()}", MessageTypeDefOf.RejectInput);
                return;
            }

            // 预测（不入队）：这一行是本探针的判据
            PlanNode first = nodes[0];
            OrbitView predicted = OrbitAdapter.ApplyForPreview(ov, first);
            sb.Append("\n  计划：").Append(nodes.Count).Append(" 条")
              .Append("  t=").Append(first.ut.ToString("F0")).Append("s")
              .Append("  |Δv|=").Append(first.dV.magnitude.ToString("F2")).Append("m/s")
              .Append("\n  机动后（预测）：e=").Append(predicted.eccentricity.ToString("E3"))
              .Append("  a=").Append((predicted.semiMajorAxis / 1000.0).ToString("F0")).Append("km")
              .Append("  （判据：e 应 ≈ 0，a 应 = 那一刻的半径）");

            if (!OrbitAdapter.TryQueuePlan(g, ov, nodes, out string reason, out string summary))
            {
                sb.Append("\n  入队失败：").Append(reason);
                Log.Message(sb.ToString());
                Messages.Message($"G{g.GroupId} 入队失败：{reason}", MessageTypeDefOf.RejectInput);
                return;
            }

            sb.Append("\n  入队：").Append(summary)
              .Append("\n  ⇒ 若预测 e ≈ 0 且队列计数 +").Append(nodes.Count).Append("，则整条链通。");

            Log.Message(sb.ToString());
            Messages.Message($"G{g.GroupId}：已排入 {nodes.Count} 条圆化机动（预测 e={predicted.eccentricity:E2}）",
                MessageTypeDefOf.NeutralEvent);
        }
    }
}
