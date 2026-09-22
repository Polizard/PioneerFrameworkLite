using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// "改升交点经度（LAN）"——MJ 的 `OperationLan` 的移植（KSP 层，语义移植）。
    ///
    /// **认知层语义**：它只用**自身**状态与天体数据（不需要目标）✓ ⇒ `target` 参数未使用。
    ///
    /// ## ✅ 2026-09-19 治本：那条"恒差 180°"的账结清了（原症状留着引以为戒）
    /// 原症状：oracle 请求什么 LAN 都得到"≈请求+180°"，且 `|Δv|` 几乎不随目标变
    /// （45°/300°/0° 都给 2766.89 m/s）。
    /// **真因不在公式**：上游的地面导航模型在**烧点不在赤道面内**时是**精确**的
    /// （2026-09-19 实测：把烧点挪到最大纬度处，请求 45°/120° 的 AN 都**精确**落在请求值上，误差 0.00°）。
    /// 退化只发生在**烧点落进赤道面内**时：此时新平面被 `span(r, d̂)` 唯一确定为赤道面本身
    /// ⇒ 节点线退化 ⇒ 指定的经度**物理上不可达**（可达到的只有烧点经度或其对面），而旧实现静默地
    /// 烧了 2766 m/s 去改倾角。**oracle 自己也有责任**：它用默认的 `APOAPSIS` 时刻，
    /// 而测试轨道是**圆轨** ⇒ 拱点任意 ⇒ 正好落在交点（赤道）上 ⇒ 一头撞进奇点。
    /// 现在：解存在时精确、解不存在时 `DeltaVToShiftLAN` 抛 `OperationException`，
    /// 由 `Operation.MakeNodes` 变成给玩家看的一句话（**不产出节点**）⇒ **不再静默乱烧**。
    ///
    /// ## ⚠ 与上游的一处偏离：低倾角告警的**判据**（上游又是度/弧度混用）
    /// 上游写 `if (o.inclination < 10)` 然后告警"倾角太低、这个机动可能不准"。
    /// 但 KSP 的 `Orbit.inclination` 是**弧度** ⇒ `< 10` 恒真 ⇒ **那条告警永远在响** ✗（上游的漏转，同族于
    /// `OperationInclination` 那处）。
    ///
    /// 而且**它的意图在我们这儿要翻译**：告警真正想说的是"**接近赤道**时升交点没有意义"，
    /// 而在本 mod 里**顺行赤道轨的 `inclination` 是 180°**（见 `SurfaceFrame` 的类注释）
    /// ⇒ "接近赤道" = `min(i, π−i)` 小 ✓（两个极端都算）⇒ 按这个判据重写。
    ///
    /// ## 登记（UX 债，不在本次范围内）
    /// 时刻候选表照搬上游的 `{APOAPSIS, PERIAPSIS, X_FROM_NOW}`。**对近圆轨道**，`APOAPSIS`/`PERIAPSIS`
    /// 是任意点 ⇒ 可能正好落在赤道 ⇒ 玩家会拿到那句"请另选点火时刻"。这是**可读的拒绝**（不是错答案），
    /// 但默认项选得不好：将来若做 UI，默认应改成"离赤道最远的那个点"。
    /// </summary>
    public class OperationLan : Operation
    {
        public override string GetName() => "改升交点经度";

        private readonly PlanParam[] _planParams;

        public OperationLan()
        {
            _planParams = new[]
            {
                    new PlanParam("新升交点经度", "°", () => NewLAN.Val, v => NewLAN.Val = v),
            };
        }

        public override IReadOnlyList<PlanParam> GetParams() => _planParams;

        public override TimeSelector? GetTimeSelector() => TimeSelector;


        /// <summary>新升交点经度，**单位：度**（与上游 GUI 的 "º" 一致）。</summary>
        public readonly EditableDoubleMult NewLAN = new EditableDouble(0);

        private static readonly TimeReference[] _timeReferences =
            { TimeReference.APOAPSIS, TimeReference.PERIAPSIS, TimeReference.X_FROM_NOW };

        public readonly TimeSelector TimeSelector = new TimeSelector(_timeReferences);

        public override void DoParametersGUI(OrbitView o, double universalTime, ITargetView target) =>
            TimeSelector.DoChooseTimeGUI();

        protected override List<PlanNode>? MakeNodesImpl(OrbitView o, double universalTime, ITargetView target)
        {
            // 低倾角告警（**非致命**：置 ErrorMessage 后照常产出节点）。判据见类注释。
            double incDeg = o.inclination * 180.0 / System.Math.PI;
            double distToEquatorDeg = System.Math.Min(incDeg, System.Math.Abs(180.0 - incDeg));
            if (distToEquatorDeg < 10.0)
                ErrorMessage = $"警告：轨道接近赤道（倾角 {incDeg:F1}°），升交点没有意义，这个机动可能不准。";

            double ut = TimeSelector.ComputeManeuverTime(o, universalTime, target);
            MechJebLib.Primitives.V3 dV = OrbitalManeuverCalculator.DeltaVToShiftLAN(o, ut, NewLAN.Val);

            return new List<PlanNode> { new PlanNode(dV, ut) };
        }
    }
}
