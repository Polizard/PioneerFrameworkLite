using System;
using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// "改倾角"——MJ 的 `OperationInclination` 的移植（KSP 层，语义移植）。
    ///
    /// ## ⚠ 偏离一：上游**漏了度→弧度转换**（bug，有证据）
    /// 上游把 `NewInc` **直接**喂给 `OrbitalManeuverCalculator.DeltaVToChangeInclination`。
    /// 而那条链的底是 `Astro.VelocityForInclination → ENUHeadingForInclination`，
    /// **吃的是弧度**（里面直接用 `Sin/Cos`）。可上游 GUI 的标签是 **"º"**（度），且没有转换
    /// ⇒ 用户输入 `30`（度）会被当成 `30 rad ≈ 1718.9° ≈ 279°` 使用，**算出来的是垃圾**。
    ///
    /// **证据**：它的兄弟 `OperationStationary` **显式做了** `* Math.PI / 180.0`
    /// （`Maneuver/OperationStationary.cs:78,80`）⇒ 上游的惯例是"GUI 用度、用时转弧度"，
    /// `OperationInclination` 漏了。
    ///
    /// ## ⚠⚠ 偏离二：**顺行基准的换算（本 mod 特有，游戏内实测）**
    /// `OrbitView.inclination` 沿用 `Astro`/MJ/KSP 的口径：**`h` 与 `+极轴` 的夹角**
    /// （所以"0°" = `h ∥ +极轴`）。而在**本 mod 里 `+极轴 = +Y` 是"北极"，顺行却是 `h ∥ −Y`**：
    /// `GetSurfaceState` 的冻结约定是「东 = (−z→+x)」且 `SurfaceVelocity = ω·(−p_z,0,p_x)`
    /// ⇒ 在 `p = +x̂` 处**东向**速度 = `ω·(0,0,R) = +ẑ` ⇒ **`h = p×v ∥ −Ŷ`**。
    /// （游戏内实测：一条"顺行（与地表自转同向）"的圆轨读出 `inclination = 180.0000°` ✓ ——
    ///   这不是错误，是约定。纯测试 `ProgradeEquatorialOrbit_HasInclination180_Not0` 钉住它。）
    ///
    /// ⇒ **面向玩家的参数用直觉口径**：`0° = 赤道顺行 · 90° = 极轨 · 180° = 赤道逆行`。
    /// 因此送进适配层的目标是 **`180° − i`**。
    /// **不换算的症状**：用户输入 `30°`（想顺行倾斜 30°）会得到一个近乎**逆行**的轨道 ✗。
    ///
    /// ## 换算放在这一层，`OrbitView` 不动
    /// `OrbitView.inclination` / `Astro` 的元素口径**保持与上游一致**（那是"跨求解器可比"的前提）；
    /// "面向玩家的倾角怎么定义"是**本层**的决定。⇒ 改动只落在 Operation / 将来的 UI。
    /// </summary>
    public class OperationInclination : Operation
    {
        public override string GetName() => "改倾角";

        private readonly PlanParam[] _planParams;

        public OperationInclination()
        {
            _planParams = [new PlanParam("新倾角", "°", () => NewInc.Val, v => NewInc.Val = v),];
        }

        public override IReadOnlyList<PlanParam> GetParams() => _planParams;

        public override TimeSelector? GetTimeSelector() => TimeSelector;


        /// <summary>
        /// 新倾角，**单位：度**，**直觉口径**：`0° = 赤道顺行 · 90° = 极轨 · 180° = 赤道逆行`。
        /// （送进适配层时会换算成 `180° − i`，见类注释的偏离二。）
        /// </summary>
        public readonly EditableDoubleMult NewInc = new EditableDouble(0);

        private static readonly TimeReference[] _timeReferences =
        [TimeReference.EQ_HIGHEST_AD, TimeReference.EQ_NEAREST_AD,
            TimeReference.EQ_ASCENDING, TimeReference.EQ_DESCENDING, TimeReference.X_FROM_NOW,
        ];

        public readonly TimeSelector TimeSelector = new(_timeReferences);

        public override void DoParametersGUI(OrbitView o, double universalTime, ITargetView target) =>
            TimeSelector.DoChooseTimeGUI();

        protected override List<PlanNode>? MakeNodesImpl(OrbitView o, double universalTime, ITargetView target)
        {
            double ut = TimeSelector.ComputeManeuverTime(o, universalTime, target);

            // ① 度 → 弧度（修上游的漏转）
            // ② 直觉口径 → 适配层口径：`180° − i`（见类注释的偏离二）
            double newIncRad = Math.PI - NewInc.Val * Math.PI / 180.0;

            DVector3 deltaV = OrbitalManeuverCalculator.DeltaVToChangeInclination(o, ut, newIncRad);

            return new List<PlanNode> { new(deltaV, ut) };
        }
    }
}
