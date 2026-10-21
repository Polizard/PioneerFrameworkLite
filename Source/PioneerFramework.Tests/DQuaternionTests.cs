using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **B2 DQuaternion 等价性**（2026-10-17）：double 姿态与 Unity Quaternion 在同一处比对；
    /// 旋转向量/复合顺序/逆/往返都在 1e-12 内（Unity 侧 float 截断只在边界）。
    /// </summary>
    public class DQuaternionTests
    {
        [Fact]
        public void FromToUnity_RoundTrips_WithinFloatEpsilon()
        {
            Quaternion q = Quaternion.Euler(10f, 20f, 30f);
            DQuaternion dq = DQuaternion.FromQuaternion(q);
            Quaternion back = dq.ToQuaternion();

            Assert.Equal(q.x, back.x, 6);
            Assert.Equal(q.y, back.y, 6);
            Assert.Equal(q.z, back.z, 6);
            Assert.Equal(q.w, back.w, 6);
        }

        [Fact]
        public void Rotate_MatchesUnityQuaternion()
        {
            Quaternion q = Quaternion.Euler(17f, -43f, 71f);
            DQuaternion dq = DQuaternion.FromQuaternion(q);
            DVector3 v = new(1.25, -2.5, 3.75);

            DVector3 actual = DVector3.Rotate(v, dq);
            Vector3 expectedU = q * v.ToVector3();

            // Unity 侧是 float（边界只有 float 精度）；double 侧与它的差应 < 1e-6。
            Assert.True(System.Math.Abs(expectedU.x - actual.x) < 1e-6, $"x {expectedU.x} vs {actual.x}");
            Assert.True(System.Math.Abs(expectedU.y - actual.y) < 1e-6, $"y {expectedU.y} vs {actual.y}");
            Assert.True(System.Math.Abs(expectedU.z - actual.z) < 1e-6, $"z {expectedU.z} vs {actual.z}");
        }

        [Fact]
        public void CompositionOrder_IsRightToLeft()
        {
            DQuaternion q1 = DQuaternion.AngleAxis(System.Math.PI / 2.0, DVector3.up);
            DQuaternion q2 = DQuaternion.AngleAxis(System.Math.PI / 2.0, DVector3.right);
            DVector3 v = DVector3.forward;

            // q1*q2 作用 = 先 q2 再 q1
            DVector3 combined = DVector3.Rotate(v, q1 * q2);
            DVector3 stepwise = DVector3.Rotate(DVector3.Rotate(v, q2), q1);
            Assert.Equal(stepwise.x, combined.x, 12);
            Assert.Equal(stepwise.y, combined.y, 12);
            Assert.Equal(stepwise.z, combined.z, 12);
        }

        [Fact]
        public void Inverse_UndoesRotation()
        {
            DQuaternion q = DQuaternion.AngleAxis(1.2345, new DVector3(0.3, -0.4, 0.866));
            DVector3 v = new(2, -3, 5);
            DVector3 back = DVector3.Rotate(DVector3.Rotate(v, q), DQuaternion.Inverse(q));

            Assert.Equal(v.x, back.x, 12);
            Assert.Equal(v.y, back.y, 12);
            Assert.Equal(v.z, back.z, 12);
        }
    }
}
