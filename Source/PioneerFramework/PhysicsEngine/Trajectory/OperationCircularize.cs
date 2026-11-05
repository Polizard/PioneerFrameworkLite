using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// "圆化轨道"——MJ 的 `OperationCircularize` 的移植（KSP 层，语义移植）。
    /// **这是 A4（P-1 判据）落地的那一个端到端操作。**
    ///
    /// 选它的理由（不是随便挑的）：它的 Δv 只依赖 `MechJebLib.Maneuvers.Simple`
    /// （`Simple.DeltaVToCircularize`，**不含 alglib**），所以现在就能做。
    /// `OperationApoapsis` 那一族走 `ChangeOrbitalElement`，**依赖 alglib、属 P1**。
    ///
    /// **`MakeNodesImpl` 的函数体与上游逐行对应**，只有三处类型替换（`Orbit`→`OrbitView`、
    /// `MechJebModuleTargetController`→`ITargetView`、`ManeuverParameters`→`PlanNode`）：
    /// ```
    /// double ut = _timeSelector.ComputeManeuverTime(o, universalTime, target);
    /// Vector3d deltaV = OrbitalManeuverCalculator.DeltaVToCircularize(o, ut);
    /// return new List<ManeuverParameters> { new ManeuverParameters(deltaV, ut) };
    /// ```
    ///
    /// **一处有意偏离**：上游把 `_timeSelector` 声明为 `private static readonly`（**所有实例共享一份可变状态**）。
    /// 这里改为**实例字段**并公开——共享静态可变状态是个隐患，而我们的用法就是
    /// "建一个操作 → 设参数 → 调 `MakeNodes`"，实例状态才是自然形态。
    /// </summary>
    public class OperationCircularize : Operation
    {
        public override string GetName() => "圆化轨道";


        public override TimeSelector? GetTimeSelector() => TimeSelector;


        private static readonly TimeReference[] _timeReferences =
            [TimeReference.APOAPSIS, TimeReference.PERIAPSIS, TimeReference.X_FROM_NOW, TimeReference.ALTITUDE];

        /// <summary>点火时刻选择器。UI 通过它读写"什么时候烧"。</summary>
        public readonly TimeSelector TimeSelector = new(_timeReferences);

        public override void DoParametersGUI(OrbitView o, double universalTime, ITargetView target) =>
            TimeSelector.DoChooseTimeGUI();

        protected override List<PlanNode>? MakeNodesImpl(OrbitView o, double universalTime, ITargetView target)
        {
            double ut = TimeSelector.ComputeManeuverTime(o, universalTime, target);
            DVector3 deltaV = OrbitalManeuverCalculator.DeltaVToCircularize(o, ut);

            return new List<PlanNode> { new(deltaV, ut) };
        }
    }
}
