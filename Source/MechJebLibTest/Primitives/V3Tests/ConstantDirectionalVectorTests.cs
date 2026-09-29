/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 */

using MechJebLib.Primitives;
using Xunit;

namespace MechJebLibTest.Primitives.V3Tests
{
    public class ConstantDirectionalVectorTests
    {
        [Fact]
        private void ZeroVector()
        {
            V3 v = V3.zero;
            v.x.ShouldEqual(0.0);
            v.y.ShouldEqual(0.0);
            v.z.ShouldEqual(0.0);
        }

        [Fact]
        private void OneVector()
        {
            V3 v = V3.one;
            v.x.ShouldEqual(1.0);
            v.y.ShouldEqual(1.0);
            v.z.ShouldEqual(1.0);
        }

        [Fact]
        private void PositiveInfinityVector()
        {
            V3 v = V3.positiveinfinity;
            Assert.Equal(double.PositiveInfinity, v.x);
            Assert.Equal(double.PositiveInfinity, v.y);
            Assert.Equal(double.PositiveInfinity, v.z);
        }

        [Fact]
        private void NegativeInfinityVector()
        {
            V3 v = V3.negativeinfinity;
            Assert.Equal(double.NegativeInfinity, v.x);
            Assert.Equal(double.NegativeInfinity, v.y);
            Assert.Equal(double.NegativeInfinity, v.z);
        }

        [Fact]
        private void MaxValueVector()
        {
            V3 v = V3.maxvalue;
            Assert.Equal(double.MaxValue, v.x);
            Assert.Equal(double.MaxValue, v.y);
            Assert.Equal(double.MaxValue, v.z);
        }

        [Fact]
        private void MinValueVector()
        {
            V3 v = V3.minvalue;
            Assert.Equal(double.MinValue, v.x);
            Assert.Equal(double.MinValue, v.y);
            Assert.Equal(double.MinValue, v.z);
        }

        [Fact]
        private void NanVector()
        {
            V3 v = V3.nan;
            Assert.True(double.IsNaN(v.x));
            Assert.True(double.IsNaN(v.y));
            Assert.True(double.IsNaN(v.z));
        }

        [Fact]
        private void DirectionalVectorsNorthEastDown()
        {
            V3.bodyDown.ShouldEqual(new V3(0.0, 0.0, 1.0));
            V3.bodyUp.ShouldEqual(new V3(0.0, 0.0, -1.0));
            V3.bodyLeft.ShouldEqual(new V3(0.0, -1.0, 0.0));
            V3.bodyRight.ShouldEqual(new V3(0.0, 1.0, 0.0));
            V3.bodyForward.ShouldEqual(new V3(1.0, 0.0, 0.0));
            V3.bodyBack.ShouldEqual(new V3(-1.0, 0.0, 0.0));
        }

        [Fact]
        // §13 极轴统一（2026-09-28）：`northpole` 由 +z 改为 +Y ⇒ **期望值随之改**。
        // ⚠ 这是"被测常量本身变了"的正当改动，不是"拿实现反推期望"：上面那 6 个 NED 船体常量
        //    （`bodyUp/bodyDown/bodyLeft/bodyRight/bodyForward/bodyBack`）**值一个都没动**
        //    （§13.2 的 C 类纪律；批5 U1' 只改了名字，因为类型统一后它们与宿主世界系常量撞名）。
        private void NorthPoleVector() => V3.northpole.ShouldEqual(new V3(0, 1, 0));

        [Fact]
        private void AxisVectors()
        {
            V3.xaxis.ShouldEqual(new V3(1, 0, 0));
            V3.yaxis.ShouldEqual(new V3(0, 1, 0));
            V3.zaxis.ShouldEqual(new V3(0, 0, 1));
        }

        [Fact]
        private void DirectionalVectorsAreUnitVectors()
        {
            V3.bodyDown.magnitude.ShouldEqual(1.0);
            V3.bodyUp.magnitude.ShouldEqual(1.0);
            V3.bodyLeft.magnitude.ShouldEqual(1.0);
            V3.bodyRight.magnitude.ShouldEqual(1.0);
            V3.bodyForward.magnitude.ShouldEqual(1.0);
            V3.bodyBack.magnitude.ShouldEqual(1.0);
            V3.xaxis.magnitude.ShouldEqual(1.0);
            V3.yaxis.magnitude.ShouldEqual(1.0);
            V3.zaxis.magnitude.ShouldEqual(1.0);
            V3.northpole.magnitude.ShouldEqual(1.0);
        }

        [Fact]
        private void OppositeDirectionsNegateEachOther()
        {
            (V3.bodyUp + V3.bodyDown).ShouldBeZero();
            (V3.bodyLeft + V3.bodyRight).ShouldBeZero();
            (V3.bodyForward + V3.bodyBack).ShouldBeZero();
        }

        [Fact]
        private void AxisVectorsAreOrthogonal()
        {
            V3.Dot(V3.xaxis, V3.yaxis).ShouldBeZero();
            V3.Dot(V3.xaxis, V3.zaxis).ShouldBeZero();
            V3.Dot(V3.yaxis, V3.zaxis).ShouldBeZero();
        }

        [Fact]
        private void DirectionalVectorsFormRightHandedSystem()
        {
            V3.Cross(V3.bodyForward, V3.bodyRight).ShouldEqual(V3.bodyDown);
            V3.Cross(V3.bodyRight, V3.bodyDown).ShouldEqual(V3.bodyForward);
            V3.Cross(V3.bodyDown, V3.bodyForward).ShouldEqual(V3.bodyRight);
        }

        [Fact]
        private void StaticAccessorsAreImmutable()
        {
            // ReSharper disable once NotAccessedVariable
            V3 v1 = V3.zero;
            v1.x = 10;
            V3 v2 = V3.zero;
            v2.ShouldEqual(new V3(0, 0, 0));

            V3 v3 = V3.one;
            v3.Set(5, 5, 5);
            V3 v4 = V3.one;
            v4.ShouldEqual(new V3(1, 1, 1));
        }
    }
}
