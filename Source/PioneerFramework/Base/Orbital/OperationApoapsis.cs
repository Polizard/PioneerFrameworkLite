using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// "改远心点"——MJ 的 `OperationApoapsis` 的移植（KSP 层，语义移植）。
    /// `MakeNodesImpl` 与上游逐行对应，只有三处类型替换（见 `Operation` 的类注释）。
    /// </summary>
    public class OperationApoapsis : Operation
    {
        public override string GetName() => "改远心点";

        private readonly PlanParam[] _planParams;

        public OperationApoapsis()
        {
            _planParams = new[]
            {
                    new PlanParam("远心点半径", "km", () => NewApA.Val / 1000.0, v => NewApA.Val = v * 1000.0),
            };
        }

        public override IReadOnlyList<PlanParam> GetParams() => _planParams;

        public override TimeSelector? GetTimeSelector() => TimeSelector;


        /// <summary>新远心点**高度**（km 口径：显示的 200 × 1000 = 存 200000 m）。</summary>
        public readonly EditableDoubleMult NewApA = new EditableDoubleMult(200000, 1000);

        private static readonly TimeReference[] _timeReferences =
            { TimeReference.APOAPSIS, TimeReference.PERIAPSIS, TimeReference.X_FROM_NOW, TimeReference.ALTITUDE };

        public readonly TimeSelector TimeSelector = new TimeSelector(_timeReferences);

        public override void DoParametersGUI(OrbitView o, double universalTime, ITargetView target) =>
            TimeSelector.DoChooseTimeGUI();

        protected override List<PlanNode>? MakeNodesImpl(OrbitView o, double universalTime, ITargetView target)
        {
            double ut = TimeSelector.ComputeManeuverTime(o, universalTime, target);

            // ⚠ 上游这里**没有**"负值守卫"（而 `OperationPeriapsis` 有）——照搬，不替它加。
            // 高度 → 半径：加中心天体半径（与 `OperationCircularize` 的 ALTITUDE 分支同口径）。
            MechJebLib.Primitives.V3 deltaV =
                OrbitalManeuverCalculator.DeltaVToChangeApoapsis(o, ut, NewApA + o.bodyRadius);

            return new List<PlanNode> { new PlanNode(deltaV, ut) };
        }
    }
}
