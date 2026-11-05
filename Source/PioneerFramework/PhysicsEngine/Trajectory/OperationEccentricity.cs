using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// "改偏心率"——MJ 的 `OperationEccentricity` 的移植（KSP 层，语义移植）。
    /// ⚠ 上游这里参数类型是 `EditableDouble`（不是 `EditableDoubleMult`）——照搬：偏心率是**比值**，
    /// 没有"显示单位 × 乘数"的口径问题。
    /// </summary>
    public class OperationEccentricity : Operation
    {
        public override string GetName() => "改偏心率";

        private readonly PlanParam[] _planParams;

        public OperationEccentricity()
        {
            _planParams = [new PlanParam("新偏心率", "", () => NewEcc.Val, v => NewEcc.Val = v),];
        }

        public override IReadOnlyList<PlanParam> GetParams() => _planParams;

        public override TimeSelector? GetTimeSelector() => TimeSelector;


        public readonly EditableDoubleMult NewEcc = new EditableDouble(0);

        private static readonly TimeReference[] _timeReferences =
            [TimeReference.APOAPSIS, TimeReference.PERIAPSIS, TimeReference.X_FROM_NOW, TimeReference.ALTITUDE];

        public readonly TimeSelector TimeSelector = new(_timeReferences);

        public override void DoParametersGUI(OrbitView o, double universalTime, ITargetView target) =>
            TimeSelector.DoChooseTimeGUI();

        protected override List<PlanNode>? MakeNodesImpl(OrbitView o, double universalTime, ITargetView target)
        {
            double ut = TimeSelector.ComputeManeuverTime(o, universalTime, target);
            DVector3 deltaV = OrbitalManeuverCalculator.DeltaVToChangeEccentricity(o, ut, NewEcc);

            return new List<PlanNode> { new(deltaV, ut) };
        }
    }
}
