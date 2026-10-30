using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **PFView 太阳圆片**的纯核心钉子（2026-10-11 用户四条裁定：角大小 / 只改 PFView / 星空复用世界星表 + 大气淡出 / 地图背景保世界口径）。
    ///
    /// 钉三件事：① 角半径 = `atan(R_sun / 绕日半径)`（换行星自动变；数据缺就退回兜底，**绝不返回 0**）；
    /// ② 摆位距离 ↔ 世界尺寸 ↔ 角大小三者自洽（往返一致）；
    /// ③ 星空盒的整片缩放必须把壳推**近裁剪面之外**（PFView 近裁剪面随相机距离自适应，可到 5000 单位），且是**等比**（星点角大小不变）。
    /// </summary>
    public class PFViewSunDiscTests
    {
        // 本 mod 的太阳系（XML）：Sun 3000 km、地球绕日 120,000 km
        const double SunRadius = 3000000.0;
        const double EarthOrbit = 120000000.0;

        [Fact]
        public void AngularRadius_EarthCaliber_IsAboutOnePointFourThreeDegrees()
        {
            float r = PFViewSunDisc.AngularRadiusDeg(SunRadius, EarthOrbit);
            Assert.True(Mathf.Abs(r - 1.43f) < 0.02f, "地球档角半径应 ≈1.43°（直径 ≈2.86°），实际 " + r);
        }

        [Fact]
        public void AngularRadius_DependsOnlyOnOrbitRadius()
        {
            // "大小只和当前行星系环绕太阳的半径有关系"：半径减半 ⇒ 视半径翻倍（≈2×）
            float near = PFViewSunDisc.AngularRadiusDeg(SunRadius, EarthOrbit * 0.5);
            float far = PFViewSunDisc.AngularRadiusDeg(SunRadius, EarthOrbit * 2.0);
            Assert.True(Mathf.Abs(near - 2f * PFViewSunDisc.AngularRadiusDeg(SunRadius, EarthOrbit)) < 0.05f,
                "近一倍应≈两倍角大小：" + near);
            Assert.True(far < PFViewSunDisc.AngularRadiusDeg(SunRadius, EarthOrbit), "远一倍应更小：" + far);
        }

        [Fact]
        public void AngularRadius_Degenerate_FallsBackToVisibleSun()
        {
            // 数据缺（XML 没填 / 不是绕恒星的行星）⇒ 兜底，且**不许给 0**（否则天上没太阳）
            Assert.Equal(PFViewSunDisc.DefaultAngularRadiusDeg, PFViewSunDisc.AngularRadiusDeg(0.0, EarthOrbit));
            Assert.Equal(PFViewSunDisc.DefaultAngularRadiusDeg, PFViewSunDisc.AngularRadiusDeg(SunRadius, 0.0));
            Assert.Equal(PFViewSunDisc.DefaultAngularRadiusDeg, PFViewSunDisc.AngularRadiusDeg(-1.0, -1.0));
            Assert.True(PFViewSunDisc.DefaultAngularRadiusDeg > 0f);
        }

        [Fact]
        public void QuadSize_AtDistance_SubtendsTheSameAngle()
        {
            const float dist = 1e6f;
            float angDeg = PFViewSunDisc.AngularRadiusDeg(SunRadius, EarthOrbit);
            float size = PFViewSunDisc.QuadSizeAtDistance(dist, angDeg);
            // 往返：已知世界尺寸与距离，反推角半径应回到原值
            float back = Mathf.Atan((size * 0.5f) / dist) * Mathf.Rad2Deg;
            Assert.True(Mathf.Abs(back - angDeg) < 1e-3f, string.Format("往返失配：{0} vs {1}", back, angDeg));
        }

        [Fact]
        public void DiameterPixels_1080p45_IsATensOfPixelsDisc()
        {
            // 屏高 1080 / FOV 45° 下，1.43° 半径的圆盘直径应 ≈65 px（量级判据：不小到看不见、不糊满屏）
            float px = PFViewSunDisc.DiameterPixels(1.43f, 45f, 1080f);
            Assert.True(px > 40f && px < 100f, "1080p/45° 下直径应在几十像素量级，实际 " + px);
        }

        [Fact]
        public void StarShellScale_PushesShellBeyondNearClip_AndIsUniform()
        {
            const float shell = 300f;
            // 近裁剪面大（看行星那档被 Clamp 到 5000）⇒ 壳必须整体挪到它之外（2× 余量）
            float scale = PFViewSunDisc.StarShellScale(5000f, shell);
            Assert.True(shell * scale > 5000f * 2f * 0.99f, "壳半径必须 > 2×近裁剪面：" + shell * scale);
            // 近裁剪面很小时不动（保持世界视图那套原尺寸口径）
            Assert.Equal(1f, PFViewSunDisc.StarShellScale(1f, shell));
            // ⚠ 期望是"2× 余量"而不是"相等就不放大"：公式刻意留 2 倍安全边（near=壳半径 ⇒ scale=2）
            Assert.Equal(2f, PFViewSunDisc.StarShellScale(shell, shell));
        }

        [Fact]
        public void QuadSizeForDisc_MakesTheSolidCoreSubtendTheNominalAngle()
        {
            const float dist = 1e6f;
            float angDeg = PFViewSunDisc.AngularRadiusDeg(SunRadius, EarthOrbit);
            float size = PFViewSunDisc.QuadSizeForDisc(dist, angDeg);

            // 实心芯（= 半宽的 DiscCoreFraction）对的角必须是**名义角半径**（"1.43° 就是 1.43°"）
            float halfCore = size * 0.5f * PFViewSunDisc.DiscCoreFraction;
            float back = Mathf.Atan(halfCore / dist) * Mathf.Rad2Deg;
            Assert.True(Mathf.Abs(back - angDeg) < 1e-3f, string.Format("芯应回落名义角半径：{0} vs {1}", back, angDeg));

            // 外沿（整块四边形）比名义半径略出一点（柔边在芯之外，不是"整块都是芯"）
            Assert.True(size > PFViewSunDisc.QuadSizeAtDistance(dist, angDeg), "外沿应比名义半径略出（柔边）");
        }

        // ── 朝向约定（2026-10-11 用户实测："我看到了星空但没看到太阳" 的回归钉）──────────────
        // 病根 = 圆片被画成**背面朝相机**，而 `Custom/Transparent`（= ShaderDatabase.Transparent）
        // 实测 cull = Back ⇒ 整片剔除（星空没事：它那个 shader 是 WorldOverlayAdditiveTwoSided，cull = Off）。

        [Fact]
        public void QuadFacingAxis_MakesTheFrontFacePointAtTheCamera()
        {
            var camPos = new Vector3(3f, 1f, -2f);
            var placePos = camPos + new Vector3(0f, -0.2f, 0.98f) * 4e7f;

            Vector3 axis = PFViewSunDisc.QuadFacingAxis(camPos, placePos);

            // quadMesh 的正面法线在局部是 **−Z** ⇒ 正面朝相机 ⟺ −axis 指向相机（dot > 0）
            Vector3 toCam = (camPos - placePos).normalized;
            float frontFacesCamera = Vector3.Dot(-axis, toCam);
            Assert.True(frontFacesCamera > 0.999f,
                "圆片正面（−Z）必须朝向相机（第一版写成了 +Z 朝相机 ⇒ 被背面剔除 ⇒ 没太阳）；dot=" + frontFacesCamera);
        }

        [Fact]
        public void QuadFacingAxis_DegeneratePlacement_DoesNotReturnZero()
        {
            // 摆位与相机重合（理论上不会发生：placeDist = FarClip×0.4）⇒ 回退一个合法朝向，不产生零四元数
            Vector3 axis = PFViewSunDisc.QuadFacingAxis(new Vector3(1f, 2f, 3f), new Vector3(1f, 2f, 3f));
            Assert.True(axis.sqrMagnitude > 0.99f, "退化时也必须给单位向量");
        }

        [Fact]
        public void SafeUp_AvoidsDegenerateLookRotation_WhenFacingIsVertical()
        {
            // ⚠ 只比分量、不把 Vector3 塞进断言消息（headless 里 Vector3.ToString() 会抛 ⇒ "冤红"）
            Assert.True((PFViewSunDisc.SafeUp(Vector3.forward) - Vector3.up).sqrMagnitude < 1e-6f,
                "非竖直朝向应沿用 Vector3.up");
            Assert.True((PFViewSunDisc.SafeUp(Vector3.up) - Vector3.forward).sqrMagnitude < 1e-6f,
                "正上 ⇒ 换 forward 当 up");
            Assert.True((PFViewSunDisc.SafeUp(-Vector3.up) - Vector3.forward).sqrMagnitude < 1e-6f,
                "正下 ⇒ 换 forward 当 up");
        }

        // ── "画了但看不见"的第二层病根：**四边形必须有 UV**（2026-10-11；径向贴图 + 无 UV 的网格 = 只采角上透明 texel）──

        [Fact]
        public void DiscAlpha_Profile_CornerIsTransparent_CenterIsOpaque()
        {
            // 无 UV 的四边形四个顶点全采 texel(0,0) = 归一化坐标 (−1,−1) ⇒ 一定透明
            // ⇒ 这条就是"径向贴图配无 UV 网格 = 画了也看不见"的判据
            Assert.True(PFViewSunDisc.DiscAlphaAt(-1f, -1f) < 1e-6f, "贴图角上必须透明（无 UV 时整片都会采到这里）");
            Assert.True(PFViewSunDisc.DiscAlphaAt(1f, -1f) < 1e-6f, "其余三角同样透明");
            Assert.True(PFViewSunDisc.DiscAlphaAt(0f, 0f) > 0.999f, "贴图中心必须不透明（实心芯）");
            Assert.True(PFViewSunDisc.DiscAlphaAt(PFViewSunDisc.DiscCoreFraction * 0.99f, 0f) > 0.999f, "芯内不透明");
            Assert.True(PFViewSunDisc.DiscAlphaAt(1f, 0f) < 1e-6f, "柔边之外为 0");
        }

        [Fact]
        public void DiscQuad_HasUvs_CenterAligned_AndBothWindings()
        {
            PFViewSunDisc.DiscQuadGeometry(out var verts, out var uvs, out var tris);

            Assert.Equal(4, verts.Length);
            Assert.Equal(4, uvs.Length);
            Assert.Equal(12, tris.Length);          // 正反各两片（绕序不赌）

            // ① UV 必须铺满：对角跨度 ≈ √2；全 (0,0) 就是那个"看不见"的 bug
            float span = (uvs[3] - uvs[0]).magnitude;
            Assert.True(span > 1f, "UV 必须铺满整片（对角跨度 ≈ √2），实际 " + span);

            // ② 片中心对应 UV 中心（径向贴图 ⇒ 圆心必须在片心）
            Vector2 uvCenter = (uvs[0] + uvs[3]) * 0.5f;
            Assert.True((uvCenter - new Vector2(0.5f, 0.5f)).magnitude < 1e-5f,
                "UV 中心必须落在片心（否则圆盘偏心）");

            // ③ 所有三角形都必须落在 z = 0 平面上、且法线为 ±Z（两个方向都要有 ⇒ 任一套绕序被剔除都还在）
            bool sawMinusZ = false, sawPlusZ = false;
            for (int t = 0; t < tris.Length; t += 3)
            {
                Vector3 a = verts[tris[t]], b = verts[tris[t + 1]], c = verts[tris[t + 2]];
                Assert.True(Mathf.Abs(a.z) < 1e-6f && Mathf.Abs(b.z) < 1e-6f && Mathf.Abs(c.z) < 1e-6f, "四边形必须共面于 z=0");
                Vector3 n = Vector3.Cross(b - a, c - a).normalized;
                Assert.True(Mathf.Abs(Mathf.Abs(n.z) - 1f) < 1e-4f, "法线必须是 ±Z");
                if (n.z < 0f) sawMinusZ = true; else sawPlusZ = true;
            }
            Assert.True(sawMinusZ && sawPlusZ, "两面都要发（绕序不赌：Cull Back 的 shader 自己挑朝相机那面）");
        }
    }
}
