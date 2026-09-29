using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// "对齐速度"（把相对速度清零）——MJ 的 `OperationKillRelVel` 的移植（KSP 层，语义移植）。
    /// 候选时间参考只有**最近接近**与**定时刻**两种（上游如此）。
    ///
    /// **认知层语义**：目标轨道只能经 `ITargetView.TryOrbit` 拿——拿不到就是"计划不可做"，**不回退真值**
    /// （P-1 §4.2）。
    ///
    /// ⚠ 与上游的一处**口径替换**：上游检查 `o.referenceBody != target.TargetOrbit.referenceBody`（同一个 SOI）。
    /// 我们的 `OrbitView` 不带天体身份，而"同一个中心天体"对两体问题而言就是**同一个 μ**
    /// ⇒ 改用 `o.mu == targetOrbit.mu` 判定，并注明。**（将来 `ContactTargetView` 带上中心天体后应改判身份。）**
    /// </summary>
    public class OperationKillRelVel : Operation
    {
        public override string GetName() => "对齐速度";


        public override TimeSelector? GetTimeSelector() => TimeSelector;

        public override bool RequiresTarget => true;


        private static readonly TimeReference[] _timeReferences =
            { TimeReference.CLOSEST_APPROACH, TimeReference.X_FROM_NOW };

        public readonly TimeSelector TimeSelector = new TimeSelector(_timeReferences);

        public override void DoParametersGUI(OrbitView o, double universalTime, ITargetView target) =>
            TimeSelector.DoChooseTimeGUI();

        protected override List<PlanNode>? MakeNodesImpl(OrbitView o, double universalTime, ITargetView target)
        {
            double ut = TimeSelector.ComputeManeuverTime(o, universalTime, target);

            // 目标轨道：拿不到 ⇒ 计划不可做（不猜、不回退真值）
            if (!target.TryOrbit(ut, out OrbitView targetOrbit))
                throw new OperationException("没有可用目标（或目标情报已过期）。");

            if (o.mu != targetOrbit.mu)
                throw new OperationException("目标必须与你在同一个中心天体的引力场内。");

            DVector3 dV = OrbitalManeuverCalculator.DeltaVToMatchVelocities(o, ut, targetOrbit);

            return new List<PlanNode> { new PlanNode(dV, ut) };
        }
    }
}
