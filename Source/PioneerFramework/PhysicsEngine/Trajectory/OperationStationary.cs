using System;
using System.Collections.Generic;
using MechJebLib.Primitives;

namespace PioneerFramework
{
    /// <summary>
    /// "静止轨道（同步）"——MJ 的 `OperationStationaryOrbit` 的移植（KSP 层，语义移植）。
    ///
    /// **语义**：把航天器送进**圆、赤道、周期 = 天体自转周期**的轨道，并且落点在指定经线正上方。
    /// 产出**两个**节点（霍曼式转移的两次点火）。
    ///
    /// ## ⚠ 与上游的三处偏离
    /// ① **删死代码**：上游算了 `targMNA`（"目标平近点角"）却**从头到尾没用过** ✗
    ///    —— 看得出它本想补偿相位（见 ③），但没接上。
    /// ② **相位：不加迭代**（这里我要留个反面教材）。我一度以为上游那行死代码说明"相位没被补偿"
    ///    —— 上游把目标点取在"目标经线**在 `ut` 时刻**的惯性位置"，而转移在 `ut2` 才到达，
    ///    看起来该差一个 `ω·(ut2 − ut)`。**实测证明这个怀疑是错的**：相位由
    ///    **"该点的时刻 == 目标轨道的 `epoch`"** 这一条锁定（`A == E`），而上游 `GetWorldSurfacePosition`
    ///    取"当前自转"的位置、`epoch` 又传 `ut` ⇒ **`A == E` 成立 ⇒ 上游是对的** ✓。
    ///    我加的相位迭代反倒把它破坏了：`A = ut2 ≠ E = ut` ⇒ **实测落点差 ω·ut2 ≈ 100°** ✗✗。
    ///    ⇒ 现在与上游同构（单次构造），并**把这条写下来**：改"时间点 + 参考时刻"这一对时，
    ///    必须同时改，只改一个就是整圈相位错。
    /// ③ **删 KSP 要素簿记**：上游 `targOrbit.eccentricity = 0; inclination = 0; Init()` 是 KSP 先摆要素、
    ///    再让 `Orbit` 反解的写法。我们的 `OrbitView` **直接从状态矢量导出要素**，
    ///    而目标轨道按构造（`SurfaceFrame.SurfaceState` 给的圆赤道点 + 共转速度）本来就是圆赤道 ⇒ 不需要。
    /// </summary>
    public class OperationStationary : Operation
    {
        public override string GetName() => "静止轨道（同步）";

        private readonly PlanParam[] _planParams;

        public OperationStationary()
        {
            _planParams = [new PlanParam("目标地面经度", "°", () => NewLongitude.Val, v => NewLongitude.Val = v),];
        }

        public override IReadOnlyList<PlanParam> GetParams() => _planParams;


        /// <summary>目标地面经度，**单位：度**。</summary>
        public readonly EditableDoubleMult NewLongitude = new EditableDouble(0);

        public override void DoParametersGUI(OrbitView o, double universalTime, ITargetView target) { }

        protected override List<PlanNode>? MakeNodesImpl(OrbitView o, double universalTime, ITargetView target)
        {
            if (!(o.angularVelocity > 0.0))
                throw new OperationException("静止轨道需要天体的自转数据，但这颗天体的 angularVelocity ≤ 0。");

            // 同步半径：ω²a³ = μ（与 `OrbitAdapter.AngularVelocity` 的 2π/一天 同一口径）
            double syncSma = System.Math.Pow(o.mu / (o.angularVelocity * o.angularVelocity), 1.0 / 3.0);

            if (!double.IsNaN(o.soiRadiusApprox) && o.soiRadiusApprox > 0.0 && syncSma > o.soiRadiusApprox)
                throw new OperationException(
                    $"同步轨道半径约 {syncSma / 1000.0:F0} km，超出这颗天体的影响范围（约 {o.soiRadiusApprox / 1000.0:F0} km）" +
                    "—— 静止轨道不存在。");

            // ⚠ 目标点的"时刻"必须与目标轨道的 `epoch` 一致（见类注释偏离 ②）：
            // `SurfaceState(..., ut)` 给的是"ut 时刻该经线在天体坐标系里的位置"，
            // 而以它 + 共转速度、`epoch = ut` 构造出的轨道，在任意时刻都正压着当时的那条经线 ✓
            (DVector3 pos, DVector3 vel) = SurfaceFrame.SurfaceState(
                syncSma, 0.0, NewLongitude.Val * (System.Math.PI / 180.0), o.angularVelocity, universalTime);

            OrbitView targOrbit = OrbitView.FromGameStateVectors(pos, vel, o.mu, universalTime,
                DVector3.up, o.bodyRadius, o.soiRadiusApprox, o.angularVelocity);

            (V3 dv1, double ut1, V3 dv2, double ut2) = OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(
                o, targOrbit, universalTime, lagTime: 0.0, fixedTime: false, coplanar: false);

            return new List<PlanNode> { new(dv1, ut1), new(dv2, ut2) };
        }
    }
}
