using System.Collections.Generic;
using static MechJebLib.Utils.Statics;

namespace PioneerFramework
{
    /// <summary>
    /// "改半长轴"——MJ 的 `OperationSemiMajor` 的移植（KSP 层，语义移植）。
    ///
    /// ⚠ **本 Operation 会"设置 `ErrorMessage` 但正常返回"**（上游如此）：半长轴过大时给一句
    /// "可能变成双曲轨道"的**非致命提醒**，节点照样产出。这不是笔误——`Operation.MakeNodes` 只在
    /// **开头**重置 `ErrorMessage`，所以 `MakeNodesImpl` 里写它就等于"带着一句警告的成功"。
    /// 调用方必须**同时**看 `nodes != null` 与 `GetErrorMessage()`。
    /// </summary>
    public class OperationSemiMajor : Operation
    {
        public override string GetName() => "改半长轴";

        private readonly PlanParam[] _planParams;

        public OperationSemiMajor()
        {
            _planParams = new[]
            {
                    new PlanParam("新半长轴", "km", () => NewSma.Val / 1000.0, v => NewSma.Val = v * 1000.0),
            };
        }

        public override IReadOnlyList<PlanParam> GetParams() => _planParams;

        public override TimeSelector? GetTimeSelector() => TimeSelector;


        /// <summary>新半长轴（m；显示的 ÷1000）。⚠ 上游**不加**中心天体半径——它是绝对半长轴，不是高度。</summary>
        public EditableDoubleMult NewSma = new EditableDoubleMult(800000, 1000);

        private static readonly TimeReference[] _timeReferences =
            { TimeReference.APOAPSIS, TimeReference.PERIAPSIS, TimeReference.X_FROM_NOW, TimeReference.ALTITUDE };

        public readonly TimeSelector TimeSelector = new TimeSelector(_timeReferences);

        public override void DoParametersGUI(OrbitView o, double universalTime, ITargetView target) =>
            TimeSelector.DoChooseTimeGUI();

        protected override List<PlanNode>? MakeNodesImpl(OrbitView o, double universalTime, ITargetView target)
        {
            double ut = TimeSelector.ComputeManeuverTime(o, universalTime, target);

            // 非致命提醒（见类注释）。⚠ 我们的 SOI 是 **Hill 半径近似**（P-1 §6-R3），
            // 而这里只是"数值荒谬性"的提醒 ⇒ 近似足够，且**绝不**用它做参考系判定。
            if (!double.IsNaN(o.soiRadiusApprox) && 2 * NewSma > o.Radius(ut) + o.soiRadiusApprox)
                ErrorMessage = "警告：新半长轴非常大，可能变成双曲轨道。";

            if (o.Radius(ut) > 2 * NewSma)
                throw new OperationException(
                    $"半长轴不能小于烧点半径的一半（当前半径 {o.Radius(ut).ToSI(3)}m ⇒ 下限 {(o.Radius(ut) / 2.0).ToSI(3)}m）。");

            DVector3 deltaV =
                OrbitalManeuverCalculator.DeltaVForSemiMajorAxis(o, ut, NewSma);

            return new List<PlanNode> { new PlanNode(deltaV, ut) };
        }
    }
}
