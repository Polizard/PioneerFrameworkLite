using System.Collections.Generic;
using MechJebLib.Functions;

namespace PioneerFramework
{
    /// <summary>
    /// "改拱点所在的地面经度（下一圈）"——MJ 的 `OperationLongitude` 的移植（KSP 层，语义移植）。
    ///
    /// **语义**（很容易误解，所以写清楚）：它**不动拱点位置**，而是**改周期**——
    /// 在拱点做切向燃烧只改 SMA（拱点位置不变），而周期决定"转回这一点时天体自转了多少"
    /// ⇒ 让 `周期 = 自转周期 × (经度差/360 + N)` 就能把拱点压到目标经线上。
    /// 所以它烧出来的是一条**周期被调过**的轨道，而不是"把拱点挪过去"。
    ///
    /// **认知层语义**：只用自身状态与天体数据（不需要目标）⇒ `target` 参数未使用。
    ///
    /// ## ⚠ 两处与上游的偏离
    /// ① **删掉 KSP 专有的时间加速约束**：上游的 `N` 循环条件是
    ///   `oppositeRadius - body.Radius < body.timeWarpAltitudeLimits[4]`，那说的是"别让对面拱点低到
    ///   只能 5 倍时间加速"——**我们的宿主没有按高度分档的时间加速**（RimWorld 的速度档与高度无关）
    ///   ⇒ 只保留唯一的物理约束：**对面那个拱点不能钻进天体里**。
    /// ② **SOI 提示不放在计算器里**：上游注释承认"有可能把拱点甩出 SOI"。我们在**算出来的 Δv 上**判
    ///   （施加后再读 SMA），而不是重算一遍那份周期算式（那会变成同一算式的第二份权威）。
    ///   这是**非致命**提示：超 SOI 的解贵但不违法。
    /// </summary>
    public class OperationLongitude : Operation
    {
        public override string GetName() => "改拱点地面经度（下一圈）";

        private readonly PlanParam[] _planParams;

        public OperationLongitude()
        {
            _planParams = new[]
            {
                    new PlanParam("目标地面经度", "°", () => NewLongitude.Val, v => NewLongitude.Val = v),
            };
        }

        public override IReadOnlyList<PlanParam> GetParams() => _planParams;

        public override TimeSelector? GetTimeSelector() => TimeSelector;


        /// <summary>目标地面经度，**单位：度**（任意实值，内部取模到 `[0,360)`）。</summary>
        public readonly EditableDoubleMult NewLongitude = new EditableDouble(0);

        private static readonly TimeReference[] _timeReferences =
            { TimeReference.APOAPSIS, TimeReference.PERIAPSIS };

        public readonly TimeSelector TimeSelector = new TimeSelector(_timeReferences);

        public override void DoParametersGUI(OrbitView o, double universalTime, ITargetView target) =>
            TimeSelector.DoChooseTimeGUI();

        protected override List<PlanNode>? MakeNodesImpl(OrbitView o, double universalTime, ITargetView target)
        {
            double ut = TimeSelector.ComputeManeuverTime(o, universalTime, target);
            MechJebLib.Primitives.V3 dV = OrbitalManeuverCalculator.DeltaVToShiftNodeLongitude(o, ut, NewLongitude.Val);

            // 非致命提示：新轨道的外拱点是否会被甩出天体影响范围（判据见类注释 ②）
            if (!double.IsNaN(o.soiRadiusApprox) && o.soiRadiusApprox > 0.0)
            {
                (MechJebLib.Primitives.V3 r, MechJebLib.Primitives.V3 v) = o.RightHandedStateVectorsAtUT(ut);
                double newSma = Astro.SmaFromStateVectors(o.mu, r, v + dV);
                double outerApsis = 2.0 * newSma - r.magnitude;
                if (outerApsis > o.soiRadiusApprox)
                    ErrorMessage = $"提示：新轨道的外拱点约 {outerApsis / 1000.0:F0} km，" +
                                   $"超出天体影响范围（约 {o.soiRadiusApprox / 1000.0:F0} km）。";
            }

            return new List<PlanNode> { new PlanNode(dV, ut) };
        }
    }
}
