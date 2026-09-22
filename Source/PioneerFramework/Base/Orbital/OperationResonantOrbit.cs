using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// "进入共振轨道"——MJ 的 `OperationResonantOrbit` 的移植（KSP 层，语义移植）。
    /// 参数是共振比 `分子/分母`（例如 2/3 = 周期变成原来的 2/3 ⇒ 每 3 圈对方走 2 圈）。
    /// </summary>
    public class OperationResonantOrbit : Operation
    {
        public override string GetName() => "共振轨道";

        private readonly PlanParam[] _planParams;

        public OperationResonantOrbit()
        {
            _planParams = new[]
            {
                    new PlanParam("共振比·分子", "", () => ResonanceNumerator.Val, v => ResonanceNumerator.Val = (int)System.Math.Round(v)),
                    new PlanParam("共振比·分母", "", () => ResonanceDenominator.Val, v => ResonanceDenominator.Val = (int)System.Math.Round(v)),
            };
        }

        public override IReadOnlyList<PlanParam> GetParams() => _planParams;

        public override TimeSelector? GetTimeSelector() => TimeSelector;


        public EditableInt ResonanceNumerator = new EditableInt(2);
        public EditableInt ResonanceDenominator = new EditableInt(3);

        private static readonly TimeReference[] _timeReferences =
            { TimeReference.APOAPSIS, TimeReference.PERIAPSIS, TimeReference.X_FROM_NOW, TimeReference.ALTITUDE };

        public readonly TimeSelector TimeSelector = new TimeSelector(_timeReferences);

        public override void DoParametersGUI(OrbitView o, double universalTime, ITargetView target) =>
            TimeSelector.DoChooseTimeGUI();

        protected override List<PlanNode>? MakeNodesImpl(OrbitView o, double universalTime, ITargetView target)
        {
            double ut = TimeSelector.ComputeManeuverTime(o, universalTime, target);

            if (ResonanceDenominator.Val == 0)
                throw new OperationException("共振比的分母不能为 0。");

            double f = (double)ResonanceNumerator.Val / ResonanceDenominator.Val;
            MechJebLib.Primitives.V3 dV = OrbitalManeuverCalculator.DeltaVToResonantOrbit(o, ut, f);

            // ⚠ **零脉冲 = 不可行**（2026-09-20）：计算器在几何不可行时返回 `V3.zero`（上游同款），
            //   若不在这里拦下，就会**静默给出一个 |dV|=0 的节点**（界面上看着"计划好了"、其实什么都不会发生 ✗）。
            //   判据见 `P1_OperationResonantOrbit_InfeasibleBurnPoint_IsReportedNotSilentlyZeroed`。
            if (dV.sqrMagnitude <= 0.0)
                throw new OperationException("该共振比在此轨道上不可行（无解，或所需机动为零）。");

            return new List<PlanNode> { new PlanNode(dV, ut) };
        }
    }
}
