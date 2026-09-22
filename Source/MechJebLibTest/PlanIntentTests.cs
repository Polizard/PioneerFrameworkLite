using System;
using System.Collections.Generic;
using MechJebLib.Functions;
using MechJebLib.Primitives;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **计划意图（目标条件）与活重解**的判据（《MechJeb参考大修计划》§2.5 / B1.2）。
    ///
    /// 核心那一条是 `ReSolve_OnDriftedState_HitsTheGoal_WhileTheFrozenDvMisses`：
    /// 同一个点火时刻，**冻结向量**照烧会偏、**按活状态重解**达标 —— 这正是计划稿把
    /// "上轮提交 dv2 失败"归因到的那条语义差别，现在它在纯测试里是**可判的**。
    /// </summary>
    public class PlanIntentTests
    {
        private static readonly double Mu = OrbitMath.GM;
        private const double R0 = 7_000_000.0;
        private const double BodyR = 5_000_000.0;
        private static readonly V3 Pole = new V3(0, 0, 1);

        /// <summary>倾角 30° 的椭圆 e=0.3（a = 1e7）——与游戏内一键验收铺的同一条轨道。</summary>
        private static OrbitView Fixture()
        {
            double vc = Math.Sqrt(Mu / R0);
            double inc = Math.PI / 6;
            return OrbitView.FromStateVectors(new V3(R0, 0, 0),
                new V3(0, Math.Sqrt(1.3) * vc * Math.Cos(inc), Math.Sqrt(1.3) * vc * Math.Sin(inc)),
                Mu, 0.0, Pole, BodyR);
        }

        /// <summary>配一条"在 `lead` 秒后圆化"的操作。</summary>
        private static OperationCircularize CircularizeIn(double lead)
        {
            var op = new OperationCircularize();
            Assert.True(op.TimeSelector.TrySetTimeRef(TimeReference.X_FROM_NOW),
                "前提：圆化操作的候选表里要有 X_FROM_NOW");
            op.TimeSelector.LeadTime.Val = lead;
            return op;
        }

        [Fact]
        public void Capture_ThenApply_RestoresOperationState()
        {
            var o = Fixture();
            OperationCircularize op = CircularizeIn(1234.0);

            var intent = PlanIntent.Capture(op, absoluteTick: 5678.0);
            Operation? back = intent.Apply(out string why);

            Assert.True(back != null, $"按名字找回操作：{why}");
            Assert.Equal(TimeReference.X_FROM_NOW, back!.GetTimeSelector()!.TimeReference);
            Assert.Equal(1234.0, back.GetTimeSelector()!.LeadTime.Val, 6);
            Assert.Equal(5678.0, intent.absoluteTick, 6);

            _ = o;
        }

        [Fact]
        public void Apply_RefusesWhenParamCountMismatches()
        {
            // 伪造一条"旧版本存的"意图：参数个数与当前代码不符
            var stale = new PlanIntent
            {
                operationName = new OperationInclination().GetName(),
                paramValues = new[] { 1.0, 2.0 }, // 改倾角只有 1 个参数
            };

            Assert.Null(stale.Apply(out string why));
            Assert.Contains("参数", why);
        }

        [Fact]
        public void Apply_RefusesWhenOperationIsGone()
        {
            var gone = new PlanIntent { operationName = "这个操作不存在" };
            Assert.Null(gone.Apply(out string why));
            Assert.Contains("找不到", why);
        }

        /// <summary>活状态没变时，重解必须与原计划**逐位一致**（否则重解就不是"重算"而是"改计划"）。</summary>
        [Fact]
        public void ReSolve_OnUnchangedState_MatchesTheOriginalPlan()
        {
            var o = Fixture();
            OperationCircularize op = CircularizeIn(6000.0);
            List<PlanNode>? original = op.MakeNodes(o, 0.0, new FixedTargetView(o));
            Assert.NotNull(original);

            var intent = PlanIntent.Capture(op, absoluteTick: original![0].ut);
            List<PlanNode>? again = intent.ReSolve(o, 0.0, new FixedTargetView(o), out string why);

            Assert.True(again != null, $"重解应当成功：{why}");
            Assert.Equal(original[0].ut, again![0].ut, 6);
            Assert.Equal(original[0].dV.x, again[0].dV.x, 9);
            Assert.Equal(original[0].dV.y, again[0].dV.y, 9);
            Assert.Equal(original[0].dV.z, again[0].dV.z, 9);
        }

        /// <summary>
        /// ⭐ **§2.5 那条语义差别，纯测试版**：点火时刻相同，
        /// ① 冻结向量（规划那一刻算的）在活状态上照烧 ⇒ **偏**；
        /// ② 按意图重解 ⇒ **达标**（圆化后 e ≈ 0）。
        /// </summary>
        [Fact]
        public void ReSolve_OnDriftedState_HitsTheGoal_WhileTheFrozenDvMisses()
        {
            var o0 = Fixture();
            double lead = 6000.0;
            OperationCircularize op = CircularizeIn(lead);
            List<PlanNode>? original = op.MakeNodes(o0, 0.0, new FixedTargetView(o0));
            Assert.NotNull(original);

            var intent = PlanIntent.Capture(op, absoluteTick: original![0].ut);
            double ut = original[0].ut;

            // 漂移：计划外的一次小增量（5 m/s 顺行）——计划稿里"等它的那几百秒"就是这个意思
            double tMid = 2000.0;
            (V3 rMid, V3 vMid) = o0.RightHandedStateVectorsAtUT(tMid);
            var oDrift = OrbitView.FromStateVectors(rMid, vMid + 5.0 * vMid.normalized, Mu, tMid, Pole, BodyR);

            // 点火时刻的**活**状态
            (V3 rLive, V3 vLive) = oDrift.RightHandedStateVectorsAtUT(ut);
            var oLive = OrbitView.FromStateVectors(rLive, vLive, Mu, ut, Pole, BodyR);

            // ① 冻结向量照烧
            V3 vFrozen = vLive + original[0].dV;
            double eFrozen = Astro.EccFromStateVectors(Mu, rLive, vFrozen);

            // ② 活重解
            List<PlanNode>? resolved = intent.ReSolve(oLive, ut, new FixedTargetView(oLive), out string why);
            Assert.True(resolved != null, $"重解应当成功：{why}");
            V3 vResolved = vLive + resolved![0].dV;
            double eResolved = Astro.EccFromStateVectors(Mu, rLive, vResolved);

            Assert.True(Math.Abs(resolved[0].ut - ut) < 1e-6,
                $"重解必须烧在**同一个**绝对时刻：{resolved[0].ut:F3} vs {ut:F3}");
            Assert.True(eResolved < 1e-9,
                $"活重解应当达标：e={eResolved:E3}｜|Δv|={resolved[0].dV.magnitude:F3} m/s");
            Assert.True(eFrozen > 1e-5,
                $"冻结向量应当明显偏（这正是要治的病）：e={eFrozen:E3}｜|Δv|={original[0].dV.magnitude:F3} m/s");
        }

        /// <summary>
        /// **相对口径不许漂**：`X_FROM_NOW`（"从现在起 N 秒"）若照原样重解，每解一次就往后推一次 ✗。
        /// 意图记了绝对时刻 ⇒ 在更晚的时刻重解，点火时刻必须**还是原来那一个**。
        /// </summary>
        [Fact]
        public void ReSolve_WithRelativeTimeRef_DoesNotDrift()
        {
            var o = Fixture();
            OperationCircularize op = CircularizeIn(5000.0);
            List<PlanNode>? original = op.MakeNodes(o, 0.0, new FixedTargetView(o));
            Assert.NotNull(original);
            double ut = original![0].ut;
            Assert.Equal(5000.0, ut, 6);

            var intent = PlanIntent.Capture(op, absoluteTick: ut);

            // 500 秒后再重解一次：没有绝对时刻钉子的话，这里会变成 0 + 5000 = 5000
            // 而"从现在起"的口径本该给 500 + 5000 = 5500 ✗；钉子应当把它压回 5000。
            List<PlanNode>? again = intent.ReSolve(o, 500.0, new FixedTargetView(o), out string why);
            Assert.True(again != null, why);
            Assert.Equal(5000.0, again![0].ut, 6);
        }
    }
}
