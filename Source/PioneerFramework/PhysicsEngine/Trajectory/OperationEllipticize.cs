using System.Collections.Generic;
using static MechJebLib.Utils.Statics;

namespace PioneerFramework
{
    /// <summary>
    /// "同时改近心点与远心点"——MJ 的 `OperationEllipticize` 的移植（KSP 层，语义移植）。
    /// ⚠ 候选时间参考里**没有 PERIAPSIS**（上游如此：变轨点选在远心点/定时刻/定高度）——照搬。
    /// </summary>
    public class OperationEllipticize : Operation
    {
        public override string GetName() => "改近/远心点";

        private readonly PlanParam[] _planParams;

        public OperationEllipticize()
        {
            _planParams = [new PlanParam("远心点半径", "km", () => NewApA.Val / 1000.0, v => NewApA.Val = v * 1000.0),
                    new PlanParam("近心点半径", "km", () => NewPeA.Val / 1000.0, v => NewPeA.Val = v * 1000.0),];
        }

        public override IReadOnlyList<PlanParam> GetParams() => _planParams;

        public override TimeSelector? GetTimeSelector() => TimeSelector;


        public readonly EditableDoubleMult NewApA = new(200000, 1000);
        public readonly EditableDoubleMult NewPeA = new(100000, 1000);

        private static readonly TimeReference[] _timeReferences =
            [TimeReference.APOAPSIS, TimeReference.X_FROM_NOW, TimeReference.ALTITUDE];

        public readonly TimeSelector TimeSelector = new(_timeReferences);

        public override void DoParametersGUI(OrbitView o, double universalTime, ITargetView target) =>
            TimeSelector.DoChooseTimeGUI();

        protected override List<PlanNode>? MakeNodesImpl(OrbitView o, double universalTime, ITargetView target)
        {
            double ut = TimeSelector.ComputeManeuverTime(o, universalTime, target);

            string burnAltitude = (o.Radius(ut) - o.bodyRadius).ToSI() + "m";
            if (o.bodyRadius + NewPeA > o.Radius(ut))
                throw new OperationException($"新近心点不能高于烧点高度（{burnAltitude}）。");

            if (o.bodyRadius + NewApA < o.Radius(ut))
                throw new OperationException($"新远心点不能低于烧点高度（{burnAltitude}）。");

            if (NewPeA < -o.bodyRadius)
                throw new OperationException($"新近心点不能低于负的中心天体半径（-{o.bodyRadius.ToSI(3)}m）。");

            double newPeR = NewPeA + o.bodyRadius;
            double newApR = NewApA + o.bodyRadius;
            DVector3 deltaV = OrbitalManeuverCalculator.DeltaVToEllipticize(o, ut, newPeR, newApR);

            return new List<PlanNode> { new(deltaV, ut) };
        }
    }
}
