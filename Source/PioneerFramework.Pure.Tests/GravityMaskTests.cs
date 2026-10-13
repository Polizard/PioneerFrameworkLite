using System;
using System.Collections.Generic;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **[A6] 引力掩码的纯测试**（计划稿 §2.5：“默认空掩码 ⇒ 逐位不变” + “掩码只剔项、不改算式”）。
    /// 合成系统照 `GravityFieldTests`（地球口径根 + 月球口径卫星），带 bodyId。
    ///
    /// ⚠ 组级 API（`SetGravityMask` / `RestoreFullGravityField` 的闩锁与 Scribe）在 `SpaceEntityGroup`
    ///   （带 Verse 引用，链不进纯工程）⇒ 归游戏内/集成面；这里只钉**求和循环里的过滤**。
    /// </summary>
    public class GravityMaskTests
    {
        private const double MuRoot = 2.0e14;
        private const double MuMoon = 5.0e12;
        private const double MoonOrbitRadius = 8.0e7;
        private const int RootId = 1;   // Earth 口径
        private const int MoonId = 2;   // Moon 口径

        private static List<GravitySource> Sources(double tick)
        {
            double w = Math.Sqrt(MuRoot / (MoonOrbitRadius * MoonOrbitRadius * MoonOrbitRadius));
            double phase = w * tick;
            double c = Math.Cos(phase), s = Math.Sin(phase);
            var mPos = new DVector3(MoonOrbitRadius * c, MoonOrbitRadius * s, 0.0);
            var mVel = new DVector3(-MoonOrbitRadius * w * s, MoonOrbitRadius * w * c, 0.0);
            return new List<GravitySource>
            {
                new GravitySource(DVector3.zero, DVector3.zero, MuRoot, RootId),
                new GravitySource(mPos, mVel, MuMoon, MoonId),
            };
        }

        private static void AssertSame(in DVector3 a0, in DVector3 j0, in DVector3 a1, in DVector3 j1)
        {
            Assert.Equal(a0.x, a1.x); Assert.Equal(a0.y, a1.y); Assert.Equal(a0.z, a1.z);
            Assert.Equal(j0.x, j1.x); Assert.Equal(j0.y, j1.y); Assert.Equal(j0.z, j1.z);
        }

        [Fact]
        public void FieldAccelJerk_NullMask_MatchesLegacyOverload_Bitwise()
        {
            // 判据"默认空掩码 ⇒ 逐位不变"：null 掩码走同一循环、同一累加顺序
            var sources = Sources(123.0);
            var pos = new DVector3(5.1e6, 1.2e6, -3.3e5);
            var vel = new DVector3(-1200.0, 6800.0, 90.0);

            NBodyIntegrator.FieldAccelJerk(pos, vel, sources, out var a0, out var j0);
            NBodyIntegrator.FieldAccelJerk(pos, vel, sources, (List<int>)null, out var a1, out var j1);

            AssertSame(a0, j0, a1, j1);
        }

        [Fact]
        public void FieldAccelJerk_Mask_SkipsMaskedSources_BitwiseEqualsPreFilteredList()
        {
            // 掩码 = {月球}（"只算中心天体"）⇒ 必须**逐位**等于"只拿中心天体那张表"算出来的场
            var sources = Sources(123.0);
            var kept = new List<GravitySource> { sources[0] };
            var mask = new List<int> { MoonId };
            var pos = new DVector3(5.1e6, 1.2e6, -3.3e5);
            var vel = new DVector3(-1200.0, 6800.0, 90.0);

            NBodyIntegrator.FieldAccelJerk(pos, vel, kept, out var aKept, out var jKept);
            NBodyIntegrator.FieldAccelJerk(pos, vel, sources, mask, out var aMask, out var jMask);

            AssertSame(aKept, jKept, aMask, jMask);
            // 顺带把"场源数 == 1"钉住（判据里的自检断言）
            Assert.Equal(1, sources.Count - mask.Count);
        }

        [Fact]
        public void FieldAccelJerk_MaskWithUnknownId_ChangesNothing_Bitwise()
        {
            var sources = Sources(123.0);
            var pos = new DVector3(5.1e6, 1.2e6, -3.3e5);
            var vel = new DVector3(-1200.0, 6800.0, 90.0);

            NBodyIntegrator.FieldAccelJerk(pos, vel, sources, out var a0, out var j0);
            NBodyIntegrator.FieldAccelJerk(pos, vel, sources, new List<int> { 999 }, out var a1, out var j1);

            AssertSame(a0, j0, a1, j1);
        }
    }
}
