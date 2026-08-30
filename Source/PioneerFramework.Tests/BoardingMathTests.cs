using PioneerFramework;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 跳帮纯逻辑测试：落点世界坐标换算（含目标姿态旋转）+ 降落窗口判定。
    /// </summary>
    public class BoardingMathTests
    {
        [Fact]
        public void WorldAimPoint_NoRotation_AddsOffsetToGroupAndEntity()
        {
            var groupPos = new DVector3(1000, 0, 2000);
            var entityPos = new DVector3(0, 0, 0);
            var offset = new DVector3(10, 0, 20);

            var aim = BoardingMath.WorldAimPoint(groupPos, entityPos, Quaternion.identity, offset);

            Assert.Equal(1010.0, aim.x, 6);
            Assert.Equal(0.0, aim.y, 6);
            Assert.Equal(2020.0, aim.z, 6);
        }

        [Fact]
        public void WorldAimPoint_EntityOffsetIsAdded()
        {
            // 实体相对 group 原点的偏移也计入（设计：实体在 group 原点 → zero，此处验证通用性）
            var groupPos = new DVector3(1000, 0, 2000);
            var entityPos = new DVector3(5, 3, -7);
            var offset = new DVector3(10, 0, 20);

            var aim = BoardingMath.WorldAimPoint(groupPos, entityPos, Quaternion.identity, offset);

            Assert.Equal(1015.0, aim.x, 6);
            Assert.Equal(3.0, aim.y, 6);
            Assert.Equal(2013.0, aim.z, 6);
        }

        [Fact]
        public void WorldAimPoint_Rotate90AboutY_MapsZOffsetToX()
        {
            // +90° 绕 Y（DQuaternion 纯数学，避免 Quaternion.Euler 的 ECall 依赖）：
            // +Z → +X（落点偏移随空间站姿态旋转）
            var groupPos = new DVector3(1000, 0, 2000);
            var offset = new DVector3(0, 0, 10);
            var rot = DQuaternion.AngleAxis(System.Math.PI / 2.0, DVector3.up).ToQuaternion();

            var aim = BoardingMath.WorldAimPoint(groupPos, DVector3.zero, rot, offset);

            Assert.Equal(1010.0, aim.x, 4);
            Assert.Equal(0.0, aim.y, 4);
            Assert.Equal(2000.0, aim.z, 4);
        }

        [Fact]
        public void WorldAimPoint_RotateNeg90AboutY_MapsZOffsetToMinusX()
        {
            // -90° 绕 Y：+Z → -X（旋转方向对称性验证）
            var offset = new DVector3(0, 0, 10);
            var rot = DQuaternion.AngleAxis(-System.Math.PI / 2.0, DVector3.up).ToQuaternion();

            var aim = BoardingMath.WorldAimPoint(DVector3.zero, DVector3.zero, rot, offset);

            Assert.Equal(-10.0, aim.x, 4);
            Assert.Equal(0.0, aim.y, 4);
            Assert.Equal(0.0, aim.z, 4);
        }

        [Fact]
        public void WorldAimPoint_StationTiltKeepsYOffset()
        {
            // 空间站侧倾 30°（绕 Z）：落点偏移的 y 分量随姿态进入世界 x/y
            var offset = new DVector3(0, 10, 0);
            var rot = DQuaternion.AngleAxis(30.0 * System.Math.PI / 180.0, DVector3.forward).ToQuaternion();
            var aim = BoardingMath.WorldAimPoint(DVector3.zero, DVector3.zero, rot, offset);
            // 绕 Z 旋转 (0,10,0) → (−10·sin30, 10·cos30, 0) = (−5, 8.66, 0)
            Assert.Equal(-5.0, aim.x, 4);
            Assert.Equal(10.0 * System.Math.Cos(30.0 * System.Math.PI / 180.0), aim.y, 4);
            Assert.Equal(0.0, aim.z, 4);
        }

        [Fact]
        public void InLandingWindow_InsideWindow_True()
        {
            Assert.True(BoardingMath.InLandingWindow(300.0, 10.0, 400.0, 15.0));
        }

        [Fact]
        public void InLandingWindow_DistTooFar_False()
        {
            Assert.False(BoardingMath.InLandingWindow(500.0, 10.0, 400.0, 15.0));
        }

        [Fact]
        public void InLandingWindow_RelVelTooHigh_False()
        {
            Assert.False(BoardingMath.InLandingWindow(300.0, 40.0, 400.0, 15.0));
        }

        [Fact]
        public void InLandingWindow_BoundaryEquals_NotInWindow()
        {
            // 严格小于：恰好在阈值上不算（与接舷"低速 < 阈值"语义一致）
            Assert.False(BoardingMath.InLandingWindow(400.0, 10.0, 400.0, 15.0));
        }
    }
}
