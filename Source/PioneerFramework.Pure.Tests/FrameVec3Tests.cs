using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 批6 S3-3c：**系载体**（`SystemVec3` / `BodyVec3` / `FrameConvert`）的契约。
    ///
    /// 判据形态 = **逐位差分**（§11.3 S3 的判据③）：这些类型的定义就是"薄"——
    /// 一切运算必须与 `DVector3` 自己的算子**逐位相同**（`BitConverter.DoubleToInt64Bits`），
    /// 跨系换算必须**可逆且逐位**。任何一处出现"我在这里重写了一遍数学"都会当场红。
    /// </summary>
    public class FrameVec3Tests
    {
        private static readonly DVector3 A = new DVector3(7.0e6, -1.2345e5, 3.21e4);
        private static readonly DVector3 B = new DVector3(-1.1e2, 7.65e3, -2.5e2);
        private static readonly DVector3 Offset = new DVector3(3.844e8, -1.0e3, 2.0e7);

        private static long Bits(double x) => BitConverter.DoubleToInt64Bits(x);

        private static void AssertSameVec(DVector3 expected, DVector3 actual, string what)
        {
            Assert.True(Bits(expected.x) == Bits(actual.x)
                     && Bits(expected.y) == Bits(actual.y)
                     && Bits(expected.z) == Bits(actual.z),
                $"「{what}」必须逐位相同：期望 ({expected.x:R},{expected.y:R},{expected.z:R}) 实得 ({actual.x:R},{actual.y:R},{actual.z:R})");
        }

        /// <summary>同系算子逐位等于 `DVector3` 的算子（本类型不许有自己的一套数学）。</summary>
        [Fact]
        public void SameFrameOperators_AreBitIdenticalToDVector3()
        {
            var sa = new SystemVec3(A);
            var sb = new SystemVec3(B);
            var ba = new BodyVec3(A);
            var bb = new BodyVec3(B);

            AssertSameVec(A + B, (sa + sb).v, "SystemVec3 +");
            AssertSameVec(A - B, (sa - sb).v, "SystemVec3 -");
            AssertSameVec(-A, (-sa).v, "SystemVec3 一元 -");
            AssertSameVec(A * 1.35, (sa * 1.35).v, "SystemVec3 *");
            AssertSameVec(A / 1.35, (sa / 1.35).v, "SystemVec3 /");
            AssertSameVec(DVector3.Cross(A, B), SystemVec3.Cross(sa, sb).v, "SystemVec3 Cross");
            AssertSameVec(A.normalized, sa.normalized.v, "SystemVec3 normalized");
            Assert.Equal(Bits(DVector3.Dot(A, B)), Bits(SystemVec3.Dot(sa, sb)));
            Assert.Equal(Bits(A.magnitude), Bits(sa.magnitude));
            Assert.Equal(Bits(A.sqrMagnitude), Bits(sa.sqrMagnitude));

            AssertSameVec(A + B, (ba + bb).v, "BodyVec3 +");
            AssertSameVec(A - B, (ba - bb).v, "BodyVec3 -");
            AssertSameVec(DVector3.Cross(A, B), BodyVec3.Cross(ba, bb).v, "BodyVec3 Cross");
            AssertSameVec(A.normalized, ba.normalized.v, "BodyVec3 normalized");
        }

        /// <summary>跨系换算：**可逆且逐位**（`SystemToBody` 后再 `BodyToSystem` 必须逐位回到原值）。</summary>
        [Fact]
        public void CrossFrameRoundTrip_IsBitIdentical()
        {
            var off = new SystemVec3(Offset);

            BodyVec3 body = FrameConvert.SystemToBody(A, off);
            AssertSameVec(A - Offset, body.v, "SystemToBody（= 减去偏移）");
            AssertSameVec(A, FrameConvert.BodyToSystem(body, off), "BodyToSystem 往返");

            BodyVec3 bodyVel = FrameConvert.SystemToBodyVelocity(B, off);
            AssertSameVec(B - Offset, bodyVel.v, "SystemToBodyVelocity");
            AssertSameVec(B, FrameConvert.BodyToSystemVelocity(bodyVel, off), "BodyToSystemVelocity 往返");
        }

        /// <summary>
        /// 语义（**独立于实现**）：偏移为零时（中心天体 = 系统系原点，即行星情形），
        /// 天体局部系与系统系**数值相同** —— 这正是"看起来一样、错了看不出"的来源，
        /// 所以这条要显式钉住（否则将来谁把"行星返零"改成别的东西也不会红）。
        /// </summary>
        [Fact]
        public void ZeroOffset_MeansBodyFrameEqualsSystemFrame()
        {
            BodyVec3 body = FrameConvert.SystemToBody(A, SystemVec3.zero);
            AssertSameVec(A, body.v, "零偏移 ⇒ 两系数值相同");
        }
    }
}
