using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 批6 S2（2026-09-30）：`OrbitView` **两个跨系口的契约** ——
    /// **进** `FromGameStateVectors`（游戏系状态 → 适配层）/ **出** `GameFrameStateAtUT`（适配层 → 游戏系状态）。
    ///
    /// ## 为什么必须钉（否则是"读码代替测量"）
    /// S2 把两个恒等转换（`GameWorldToAdapter` / `AdapterToGameWorld`）**降为 `OrbitView` 私有**之后，
    /// 探针的"往返恒等"判据（`OrbitViewProbe` ① / `AdapterAcceptanceRunner` ①）从"直接读 `ov.r`/`ov.v`"
    /// 改成**经出边口**——也就是多走一次 `Shepperd` 传播（`Δt = ut − epoch = 0`）。
    /// 我读 `Shepperd.Solve` 判定 Δt=0 时它逐位返回原状态（`u=0 ⇒ f=1,g=0,ff=0,gg=1`），
    /// 但**读码不是证据**（本库 11 次仪器事故的教训）⇒ 把这条契约写成断言。
    ///
    /// ## ⚠ 若本条将来红了
    /// 只有当**两系真的重新分离**（出边口要施加一个旋转）时它才会红 —— 那时**不是改测试**，
    /// 而是：① 确认 S3「类型承载系」是否已把它改成显式类型转换；② 同步改两个口的文档与探针的写法。
    /// </summary>
    public class OrbitViewFramePortTests
    {
        private const double Mu = 3.986004418e14;   // 地球 GM（m³/s²），本测试只关心"搬运是否逐位"
        private const double BodyR = 6.371e6;
        private const double Epoch = 12345.0;

        private static OrbitView Sample(out DVector3 r, out DVector3 v)
        {
            r = new DVector3(7.0e6, -1.2345e5, 3.21e4);
            v = new DVector3(1.1e2, 7.65e3, -2.5e2);
            return OrbitView.FromGameStateVectors(r, v, Mu, Epoch, DVector3.up, BodyR);
        }

        private static long Bits(double x) => BitConverter.DoubleToInt64Bits(x);

        /// <summary>出边口在**构造时刻**上必须逐位返回构造时喂进去的状态（不许有传播噪声）。</summary>
        [Fact]
        public void OutPort_AtConstructionEpoch_IsBitIdenticalToInput()
        {
            OrbitView ov = Sample(out DVector3 r, out DVector3 v);

            (DVector3 p, DVector3 q) = ov.GameFrameStateAtUT(Epoch);

            Assert.Equal(Bits(r.x), Bits(p.x));
            Assert.Equal(Bits(r.y), Bits(p.y));
            Assert.Equal(Bits(r.z), Bits(p.z));
            Assert.Equal(Bits(v.x), Bits(q.x));
            Assert.Equal(Bits(v.y), Bits(q.y));
            Assert.Equal(Bits(v.z), Bits(q.z));
        }

        /// <summary>
        /// **当前契约（批4 P4 极轴统一 + 批6 S2 封边界之后）**：出边口 ≥ 现在**就是恒等**，
        /// 与适配层自己的传播 `RightHandedStateVectorsAtUT(ut)` 逐位相同 —— 即"跨系"这一步在数值上是零成本。
        /// 这条把"两系同轴"从散文里的事实变成**可执行断言**：谁让它红了，就该去看上面那段说明。
        /// </summary>
        [Fact]
        public void OutPort_IsCurrentlyTheIdentity_ForAnyUt()
        {
            OrbitView ov = Sample(out _, out _);
            foreach (double ut in new[] { Epoch, Epoch + 600.0, Epoch + 5731.0, Epoch - 300.0 })
            {
                (DVector3 pOut, DVector3 vOut) = ov.GameFrameStateAtUT(ut);
                (DVector3 pIn, DVector3 vIn) = ov.RightHandedStateVectorsAtUT(ut);

                Assert.Equal(Bits(pIn.x), Bits(pOut.x));
                Assert.Equal(Bits(pIn.y), Bits(pOut.y));
                Assert.Equal(Bits(pIn.z), Bits(pOut.z));
                Assert.Equal(Bits(vIn.x), Bits(vOut.x));
                Assert.Equal(Bits(vIn.y), Bits(vOut.y));
                Assert.Equal(Bits(vIn.z), Bits(vOut.z));
            }
        }
    }
}
