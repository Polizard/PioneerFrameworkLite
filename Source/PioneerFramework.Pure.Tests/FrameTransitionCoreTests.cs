using System;
using System.Collections.Generic;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 换系纯核测试（2026-11-04，Batch B）。
    /// 钉住：LCA 偏移、父子/兄弟/根往返、位置+速度同时变换、换两次回原位。
    /// </summary>
    public class FrameTransitionCoreTests
    {
        private readonly ITestOutputHelper output;

        public FrameTransitionCoreTests(ITestOutputHelper output) => this.output = output;

        private static readonly FrameNode Sun = new(0, -1, new DVector3(0, 0, 0), new DVector3(0, 0, 0));
        private static readonly FrameNode Earth = new(1, 0, new DVector3(100, 200, 300), new DVector3(1, 2, 3));
        private static readonly FrameNode Moon = new(2, 1, new DVector3(400, 500, 600), new DVector3(4, 5, 6));
        private static readonly FrameNode ZheSha = new(3, 0, new DVector3(-700, 800, 900), new DVector3(7, -8, 9));

        private static List<FrameNode> Path(params FrameNode[] nodes) => new(nodes);

        [Fact]
        public void Offset_FindsLca_AndTransformsRoundTrip()
        {
            var sun = Path(Sun);
            var earth = Path(Earth, Sun);
            var moon = Path(Moon, Earth, Sun);
            var zheSha = Path(ZheSha, Sun);

            // 根 → 子：偏移 = −子的父系偏移
            var toEarth = FrameTransitionCore.Offset(sun, earth);
            Assert.Equal(new DVector3(-100, -200, -300), toEarth.Position);
            Assert.Equal(new DVector3(-1, -2, -3), toEarth.Velocity);

            // 子 → 根：偏移 = +子的父系偏移
            var toSun = FrameTransitionCore.Offset(earth, sun);
            Assert.Equal(Earth.Position, toSun.Position);
            Assert.Equal(Earth.Velocity, toSun.Velocity);

            // 父 → 卫星：偏移 = −卫星（LCA = Earth）
            var toMoon = FrameTransitionCore.Offset(earth, moon);
            Assert.Equal(-Moon.Position, toMoon.Position);
            Assert.Equal(-Moon.Velocity, toMoon.Velocity);

            // 卫星 → 父：偏移 = +卫星
            var toEarthFromMoon = FrameTransitionCore.Offset(moon, earth);
            Assert.Equal(Moon.Position, toEarthFromMoon.Position);
            Assert.Equal(Moon.Velocity, toEarthFromMoon.Velocity);

            // 兄弟：LCA = Sun，偏移 = Earth − ZheSha
            var sibling = FrameTransitionCore.Offset(earth, zheSha);
            Assert.Equal(Earth.Position - ZheSha.Position, sibling.Position);
            Assert.Equal(Earth.Velocity - ZheSha.Velocity, sibling.Velocity);

            // 同一参考系：0
            var same = FrameTransitionCore.Offset(earth, earth);
            Assert.Equal(DVector3.zero, same.Position);
            Assert.Equal(DVector3.zero, same.Velocity);

            // 位置+速度一起搬：月球中心 → 地球系 → 搬回
            var moonOffset = FrameTransitionCore.Offset(moon, earth);
            FrameTransitionCore.Transform(
                DVector3.zero, DVector3.zero,
                moonOffset.Position, moonOffset.Velocity,
                DVector3.zero, DVector3.zero,
                out var moonCenterInEarth, out var moonVelInEarth);
            Assert.Equal(Moon.Position, moonCenterInEarth);
            Assert.Equal(Moon.Velocity, moonVelInEarth);
            FrameTransitionCore.Transform(
                moonCenterInEarth, moonVelInEarth,
                DVector3.zero, DVector3.zero,
                moonOffset.Position, moonOffset.Velocity,
                out var backPos, out var backVel);
            Assert.Equal(DVector3.zero, backPos);
            Assert.Equal(DVector3.zero, backVel);

            output.WriteLine($"toEarth offset={toEarth.Position}; toMoon={toMoon.Position}; sibling={sibling.Position}");
        }

        [Fact]
        public void Transform_IsExactAndComposable()
        {
            // from = Earth 系，to = Sun 系：p' = p + Earth − Sun = p + Earth
            var earthOffset = FrameTransitionCore.Offset(Path(Earth, Sun), Path(Sun));
            var sunOffset = FrameTransitionCore.Offset(Path(Sun), Path(Sun));
            FrameTransitionCore.Transform(
                new DVector3(10, 20, 30), new DVector3(1, 2, 3),
                earthOffset.Position, earthOffset.Velocity,
                sunOffset.Position, sunOffset.Velocity,
                out var p, out var v);
            Assert.Equal(new DVector3(110, 220, 330), p);
            Assert.Equal(new DVector3(2, 4, 6), v);

            // 再换回去：必须逐位回到原值
            FrameTransitionCore.Transform(
                p, v,
                sunOffset.Position, sunOffset.Velocity,
                earthOffset.Position, earthOffset.Velocity,
                out var p2, out var v2);
            Assert.Equal(new DVector3(10, 20, 30), p2);
            Assert.Equal(new DVector3(1, 2, 3), v2);

            // 根路径无共同根 → 报错
            Assert.Throws<InvalidOperationException>(() =>
                FrameTransitionCore.Offset(Path(new FrameNode(9, -1, DVector3.zero, DVector3.zero)), Path(Sun)));
        }
    }
}
