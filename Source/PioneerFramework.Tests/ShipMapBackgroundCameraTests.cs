using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 含图编队**地图背景相机**的"甲板法线"约定（2026-10-09）。
    ///
    /// ## 为什么值得一条测试
    /// 用户口径（原文）："我们地图是一张大饼，我们看背景的摄像头安在大饼的下方而不是别处"、
    /// "舱底下方，跟着舱底的朝向走"。落到代码就是 <see cref="ShipMapBackgroundCamera.DeckUndersideRotation"/>
    /// 这一个常量：**视线 = 甲板法线外侧（船体局部 −Y）、画面上方 = 船头（局部 +Z）**。
    /// 它是个"手写四元数"，最容易出的错不是算不出来，而是**有人顺手把它改成 −90° 或绕错轴** —— 那种改动
    /// 不报错、只在画面里"背景换了个方向"，最难查。这里把三条基向量映射逐条钉死。
    ///
    /// ## "−Y 才是舱底/下方"的依据（两条既有约定，本测试不重复验证它们）
    /// `MapCoordMapper`（地图 xz 平面 = 船体局部 xz、局部 y = 船体厚度方向，地面 y=0）
    /// × `HoverCore` D10（**船体上轴 +Y = 本地铅垂朝外**）⇒ 局部 −Y 是甲板背面、也是朝行星那一侧。
    ///
    /// 纯受管四元数运算（不用 `Quaternion.LookRotation/AngleAxis` 那族 ECall）⇒ headless 可跑。
    /// </summary>
    public class ShipMapBackgroundCameraTests
    {
        const float Tol = 0.001f;

        static void AssertNear(Vector3 expected, Vector3 actual)
        {
            Assert.True(Vector3.Distance(expected, actual) <= Tol,
                string.Format("期望 ({0}, {1}, {2})，实得 ({3}, {4}, {5})",
                    expected.x, expected.y, expected.z, actual.x, actual.y, actual.z));
        }

        [Fact]
        public void CameraLooksAlongMinusLocalY()
        {
            // 视线（相机 +Z）＝ 甲板法线**外侧** = 船体局部 −Y
            AssertNear(new Vector3(0f, -1f, 0f), ShipMapBackgroundCamera.DeckUndersideRotation * Vector3.forward);
        }

        [Fact]
        public void CameraUpIsShipNose()
        {
            // 画面上方（相机 +Y）＝ 船头 = 船体局部 +Z（"跟着舱底的朝向走"：甲板转，画面跟着转）
            AssertNear(new Vector3(0f, 0f, 1f), ShipMapBackgroundCamera.DeckUndersideRotation * Vector3.up);
        }

        [Fact]
        public void CameraRightIsUnchanged()
        {
            // 右手侧（相机 +X）＝ 船体局部 +X —— 与前两条一起构成**右手性一致**的基（不成镜像）
            AssertNear(new Vector3(1f, 0f, 0f), ShipMapBackgroundCamera.DeckUndersideRotation * Vector3.right);
        }

        [Fact]
        public void DeckUndersideRotation_IsAUnitQuaternion()
        {
            var q = ShipMapBackgroundCamera.DeckUndersideRotation;
            float m = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            Assert.True(Mathf.Abs(m - 1f) < 1e-4f, "不是单位四元数（长 " + m + "）⇒ 会把相机压歪");
        }
    }
}
