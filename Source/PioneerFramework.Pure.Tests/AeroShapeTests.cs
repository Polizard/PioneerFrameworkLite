using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **[A2-1] 气动形状的纯测试**（计划稿 D13 / [补1] 判据：盒→三轴映射 / 球退化 / 覆盖位 / 退化守卫）。
    ///
    /// ⚠ 用例几何是**合成值**：这里测的是"盒模型到三轴 (Cd·A) 的导出规则"，不是任何具体船的数据档
    /// （测试火箭按"长棍"给数据是 [补3]，属 [A2] 的数据裁定）。
    /// </summary>
    public class AeroShapeTests
    {
        [Fact]
        public void FromBox_MapsAxesCorrectly_AllDistinctDims()
        {
            // 三轴全不同 ⇒ 把"垂直于该轴的两个尺寸之积"这条规则钉死：前(z)=x·y / 左(x)=y·z / 上(y)=x·z
            var s = AeroShape.FromBox(new DVector3(2.0, 3.0, 4.0), dragCoefficient: 1.0);

            Assert.Equal(6.0, s.forwardCdA);    // 2·3
            Assert.Equal(12.0, s.leftCdA);      // 3·4
            Assert.Equal(8.0, s.upCdA);         // 2·4
            Assert.Equal(4.0, s.referenceLength);
        }

        [Fact]
        public void FromBox_ScalesLinearlyWithDragCoefficient()
        {
            var a = AeroShape.FromBox(new DVector3(6.0, 6.0, 12.0), 1.0);
            var b = AeroShape.FromBox(new DVector3(6.0, 6.0, 12.0), 0.5);

            Assert.Equal(2.0 * b.forwardCdA, a.forwardCdA);
            Assert.Equal(2.0 * b.leftCdA, a.leftCdA);
            Assert.Equal(2.0 * b.upCdA, a.upCdA);
        }

        [Fact]
        public void FromBox_Cube_IsIsotropic()
        {
            // 立方体 = D13 的"三轴相等退化数据"的几何来源（近球火箭/悬浮平台的代理）
            var s = AeroShape.FromBox(new DVector3(30.0, 30.0, 30.0), dragCoefficient: 1.0);

            Assert.True(s.IsIsotropic);
            Assert.False(s.IsDegenerate);
            Assert.Equal(900.0, s.forwardCdA);
            Assert.Equal(900.0, s.leftCdA);
            Assert.Equal(900.0, s.upCdA);
        }

        [Fact]
        public void FromBox_LongRod_IsStronglyAnisotropic()
        {
            // [补3] 的"长棍"形状：细长 ⇒ 头/尾迎风面积远小于侧向（侧飞失速 = 大气玩法的数据基础）
            var s = AeroShape.FromBox(new DVector3(1.0, 1.0, 30.0), dragCoefficient: 1.0);

            Assert.False(s.IsIsotropic);
            Assert.Equal(1.0, s.forwardCdA);    // 1·1
            Assert.Equal(30.0, s.leftCdA);      // 1·30
            Assert.Equal(30.0, s.upCdA);        // 1·30
            Assert.Equal(30.0, s.referenceLength);
        }

        [Fact]
        public void Isotropic_Factory_PinsAllThreeAxes()
        {
            var s = AeroShape.Isotropic(cdA: 12.5, referenceLength: 3.0);

            Assert.True(s.IsIsotropic);
            Assert.False(s.IsDegenerate);
            Assert.Equal(12.5, s.forwardCdA);
            Assert.Equal(12.5, s.leftCdA);
            Assert.Equal(12.5, s.upCdA);
            Assert.Equal(3.0, s.referenceLength);
        }

        [Theory]
        [InlineData(0.0, 6.0, 12.0)]
        [InlineData(6.0, 0.0, 12.0)]
        [InlineData(6.0, 6.0, 0.0)]
        [InlineData(-6.0, 6.0, 12.0)]
        public void FromBox_DegenerateBounds_ReportDegenerate(double x, double y, double z)
        {
            // "静默零面积"的仪器：[补1] 要求生产者不许喂退化外形（有 Map 组要有显式 fallback）
            var s = AeroShape.FromBox(new DVector3(x, y, z));
            Assert.True(s.IsDegenerate);
        }

        [Fact]
        public void FromBox_HealthyBounds_IsNotDegenerate()
        {
            Assert.False(AeroShape.FromBox(new DVector3(6.0, 6.0, 12.0)).IsDegenerate);
        }

        [Fact]
        public void DefaultShape_IsDegenerate()
        {
            // default(AeroShape)（三轴 0）必须被识别为退化 —— 免得"没导出外形"冒充"无阻力是物理结论"
            Assert.True(default(AeroShape).IsDegenerate);
        }

        // ── 盒子解析投影（2026-10-28：面积改投影轮廓）──────────────

        [Fact]
        public void DefaultDragCoefficient_IsRocketLike()
        {
            // 1.0 是钝板口径；真实带整流罩火箭 0.3–0.5。默认值钉在 0.35。
            Assert.Equal(0.35, AeroShape.DefaultDragCoefficient, 6);
            var s = AeroShape.FromBox(new DVector3(4.0, 40.0, 4.0));
            Assert.Equal(0.35 * 16.0, s.upCdA, 6);      // 竖着飞的截面 4×4
            Assert.Equal(0.35 * 160.0, s.forwardCdA, 6); // 侧躺时的截面 4×40（不再被当成迎风面）
        }

        [Theory]
        // 沿体轴：面积 = 该轴面
        [InlineData(1.0, 0.0, 0.0)]
        [InlineData(0.0, 0.0, 1.0)]
        // 面对角（x=z=1/√2）：盒子的真投影 = (A_x + A_z)/√2
        [InlineData(1.0, 0.0, 1.0)]
        // 体对角：立方体 = √3·a²
        [InlineData(1.0, 1.0, 1.0)]
        public void ProjectedCdA_MatchesAnalyticBoxShadow(double nx, double ny, double nz)
        {
            const double a = 2.0;                       // 立方体边长
            var s = AeroShape.FromBox(new DVector3(a, a, a), dragCoefficient: 1.0);
            var n = new DVector3(nx, ny, nz).normalized;

            // 解析真值：盒子沿 n 的投影面积 = Σ|n·ê_i|·A_i
            double expected = System.Math.Abs(n.x) * s.leftCdA
                            + System.Math.Abs(n.y) * s.upCdA
                            + System.Math.Abs(n.z) * s.forwardCdA;

            double got = s.ProjectedCdA(n, DVector3.forward, DVector3.right, DVector3.up);
            Assert.Equal(expected, got, 12);
            // 体对角必须 ≈ √3·a²（二次范数口径会给 a² ⇒ 低估 42%）
            if (nx > 0 && ny > 0 && nz > 0)
                Assert.Equal(System.Math.Sqrt(3.0) * a * a, got, 12);
        }

        [Fact]
        public void StackedRocket_FrontalArea_IsMaxSection_NotSum()
        {
            // 用户实报场景：4×40 的罐 + 12 台 2×3 的引擎。旧口径把每件的 CdA **逐轴相加**
            // ⇒ 长轴方向的迎风面会累到 232 m² 量级；新口径 = 整船包围盒一个盒。
            var s = AeroShape.FromBox(new DVector3(4.0, 40.0, 4.0));   // 默认 Cd 0.35

            // 气流沿长轴(+Y)：迎风面 = x·z = 16
            double alongY = s.ProjectedCdA(DVector3.up, DVector3.forward, DVector3.right, DVector3.up);
            Assert.Equal(0.35 * 16.0, alongY, 6);

            // 气流沿前轴(+Z)：迎风面 = x·y = 160（侧躺时才是这个大面）
            double alongZ = s.ProjectedCdA(DVector3.forward, DVector3.forward, DVector3.right, DVector3.up);
            Assert.Equal(0.35 * 160.0, alongZ, 6);

            // 旧口径的"逐件求和"会给出 12×6 + 160 + 1 = 233 量级 ⇒ 两者差一个数量级
            Assert.True(alongZ > 8.0 * alongY);
        }
    }
}
