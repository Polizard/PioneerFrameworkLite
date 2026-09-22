using System;
using System.Collections.Generic;
using MechJebLib.Primitives;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// `Operation` 的**参数描述**（`PlanParam`）与整族的**机械体检**。
    ///
    /// 为什么值得单独测：UI（`Window_PlanEditor`）是**只靠这些描述**渲染的 ⇒ 某个 Operation 忘了声明
    /// `GetParams()`，界面上就"什么都改不了"，而**没有任何编译错误**（静默失灵）。
    /// 本文件用反射把整族扫一遍，让这种遗漏**在测试里就红**。
    /// </summary>
    public class PlanParamTests
    {
        private static readonly double Mu = OrbitMath.GM;
        private const double R0 = 7_000_000.0;
        private const double BodyR = 5_000_000.0;
        private const double Omega = 2.0 * Math.PI / 60000.0;
        private static readonly V3 Pole = new V3(0, 0, 1);

        /// <summary>倾角 30° 的圆轨 + 自转（**带上自转**，这样 `Longitude`/`Stationary` 也能算）。</summary>
        private static OrbitView Fixture()
        {
            double vc = Math.Sqrt(Mu / R0);
            double inc = Math.PI / 6;
            return OrbitView.FromStateVectors(new V3(R0, 0, 0),
                new V3(0, vc * Math.Cos(inc), vc * Math.Sin(inc)), Mu, 0.0,
                Pole, BodyR, double.NaN, Omega);
        }

        [Fact]
        public void EveryDiscoveredOperation_IsNameableAndEditable()
        {
            Operation[] ops = Operation.GetAvailableOperations();

            Assert.True(ops.Length >= 12, $"应当发现全部 Operation（实得 {ops.Length}）");

            foreach (Operation op in ops)
            {
                Assert.False(string.IsNullOrWhiteSpace(op.GetName()), "每个 Operation 都要有名字（UI 靠它列表）");
                Assert.True(op.GetParams().Count > 0 || op.GetTimeSelector() != null,
                    $"「{op.GetName()}」既没有参数也没有时间选择器 ⇒ 界面上无从操作（忘写 GetParams()？）");
            }
        }

        [Fact]
        public void EveryParam_RoundTripsThroughDisplayUnits()
        {
            foreach (Operation op in Operation.GetAvailableOperations())
            foreach (PlanParam p in op.GetParams())
            {
                double before = p.Get();

                // 探针取整数：`OperationResonantOrbit` 的共振比背后是 `EditableInt`（写小数会被取整）
                const double probe = 123.0;
                p.Set(probe);
                Assert.True(Math.Abs(p.Get() - probe) < 1e-9,
                    $"「{op.GetName()}」的参数「{p.Label}」：写 {probe} 读回 {p.Get()}（显示单位换算不对称？）");

                p.Set(before);
                Assert.True(Math.Abs(p.Get() - before) < 1e-9,
                    $"「{op.GetName()}」的参数「{p.Label}」写回原值不成立");
            }
        }

        /// <summary>
        /// **契约：产出非空计划时，节点必须是"能入队"的形状**——`ut` 有限且 ≥ 0、`dV` 三个分量有限。
        /// 这正是 `OrbitAdapter.TryQueuePlan` 依赖的前提；某一代操作里破了它，在这里就该红。
        /// （返回 `null` 是**允许**的：那是"带理由的拒绝"，`Operation.MakeNodes` 负责把理由放进 `ErrorMessage`。）
        /// </summary>
        [Fact]
        public void EveryOperation_ProducesQueueableNodesOrAReason()
        {
            var o = Fixture();
            var target = new FixedTargetView(o);

            foreach (Operation op in Operation.GetAvailableOperations())
            {
                List<PlanNode>? nodes = op.MakeNodes(o, 0.0, target);

                if (nodes == null)
                {
                    Assert.False(string.IsNullOrWhiteSpace(op.GetErrorMessage()),
                        $"「{op.GetName()}」既没产出节点、又没给理由（红字必须能解释自己）");
                    continue;
                }

                Assert.True(nodes.Count > 0, $"「{op.GetName()}」产出了空计划");

                foreach (PlanNode n in nodes)
                {
                    Assert.True(!double.IsNaN(n.ut) && !double.IsInfinity(n.ut) && n.ut >= 0.0,
                        $"「{op.GetName()}」的节点时刻非法：{n.ut}");
                    Assert.True(IsFinite(n.dV), $"「{op.GetName()}」的 Δv 非法：({n.dV.x}, {n.dV.y}, {n.dV.z})");
                }
            }
        }

        private static bool IsFinite(V3 v) =>
            !double.IsNaN(v.x) && !double.IsInfinity(v.x) &&
            !double.IsNaN(v.y) && !double.IsInfinity(v.y) &&
            !double.IsNaN(v.z) && !double.IsInfinity(v.z);
    }
}
