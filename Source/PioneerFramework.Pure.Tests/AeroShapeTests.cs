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
            var s = AeroShape.FromBox(new DVector3(30.0, 30.0, 30.0));

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
            var s = AeroShape.FromBox(new DVector3(1.0, 1.0, 30.0));

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
    }
}
