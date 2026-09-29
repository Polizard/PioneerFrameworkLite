using System.Collections.Generic;
using static MechJebLib.Utils.Statics;

namespace PioneerFramework
{
    /// <summary>
    /// "改近心点"——MJ 的 `OperationPeriapsis` 的移植（KSP 层，语义移植）。
    /// ⚠ 与 `OperationApoapsis` 的不对称：**只有这一个有负值守卫**（上游如此）——照搬。
    /// </summary>
    public class OperationPeriapsis : Operation
    {
        public override string GetName() => "改近心点";

        private readonly PlanParam[] _planParams;

        public OperationPeriapsis()
        {
            _planParams = new[]
            {
                    new PlanParam("近心点半径", "km", () => NewPeA.Val / 1000.0, v => NewPeA.Val = v * 1000.0),
            };
        }

        public override IReadOnlyList<PlanParam> GetParams() => _planParams;

        public override TimeSelector? GetTimeSelector() => TimeSelector;


        /// <summary>新近心点**高度**（m；显示的 ÷1000）。</summary>
        public readonly EditableDoubleMult NewPeA = new EditableDoubleMult(100000, 1000);

        private static readonly TimeReference[] _timeReferences =
            { TimeReference.APOAPSIS, TimeReference.PERIAPSIS, TimeReference.X_FROM_NOW, TimeReference.ALTITUDE };

        public readonly TimeSelector TimeSelector = new TimeSelector(_timeReferences);

        public override void DoParametersGUI(OrbitView o, double universalTime, ITargetView target) =>
            TimeSelector.DoChooseTimeGUI();

        protected override List<PlanNode>? MakeNodesImpl(OrbitView o, double universalTime, ITargetView target)
        {
            double ut = TimeSelector.ComputeManeuverTime(o, universalTime, target);

            if (NewPeA < -o.bodyRadius)
                throw new OperationException(
                    $"新近心点不能低于负的中心天体半径（-{o.bodyRadius.ToSI(3)}m）。");

            DVector3 deltaV =
                OrbitalManeuverCalculator.DeltaVToChangePeriapsis(o, ut, NewPeA + o.bodyRadius);

            return new List<PlanNode> { new PlanNode(deltaV, ut) };
        }
    }
}
