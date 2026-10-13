using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **[A7] 悬浮控制律的定律边界**（计划稿 §2.6 / D10）。
    /// 台架（"抖幅 ∝ dt²"那条断言）在 `HoverBenchTests`；本文件只钉**逐条输入输出**：
    /// 死区按模判 · 上限截模长 · 引力抵消用**场本身** · 姿态设定点是"上轴 = 径向"（不是主推轴）。
    /// </summary>
    public class HoverCoreTests
    {
        private static readonly DVector3 Up = new DVector3(0, 1, 0);
        private static readonly DVector3 X = new DVector3(1, 0, 0);

        // ── ① PD：死区 / 上限 / 阻尼 ──

        [Fact]
        public void PidAccel_DeadZone_ZeroesPositionTermOnly()
        {
            // 死区内（|posErr| ≤ 5 m）⇒ 位置项归零；**速度项照给**（阻尼不能也停）
            var a = HoverCore.PidAccel(X * 4.0, X * 10.0, kp: 0.01, kd: 0.2, deadZoneMeters: 5.0);
            Assert.Equal(2.0, a.x, 12);           // 只有 Kd·v = 0.2 × 10
            Assert.Equal(0.0, a.y); Assert.Equal(0.0, a.z);
        }

        [Fact]
        public void PidAccel_OutsideDeadZone_AddsPositionTerm()
        {
            var a = HoverCore.PidAccel(X * 100.0, DVector3.zero, kp: 0.01, kd: 0.2, deadZoneMeters: 5.0);
            Assert.Equal(1.0, a.x, 12);           // Kp·100
        }

        [Fact]
        public void PidAccel_ClampsMagnitude_NotPerAxis()
        {
            // 位置上限：Kp·1000 = 10 m/s²，但上限 3 ⇒ 整体截到 3（方向不变）
            var a = HoverCore.PidAccel(X * 1000.0, DVector3.zero, kp: 0.01, kd: 0.2, maxAccelMps2: 3.0);
            Assert.Equal(3.0, a.magnitude, 12);
            Assert.Equal(1.0, a.normalized.x, 12);
        }

        [Fact]
        public void PidAccel_ZeroError_IsExactlyZero()
        {
            var a = HoverCore.PidAccel(DVector3.zero, DVector3.zero);
            Assert.Equal(0.0, a.sqrMagnitude);
        }

        // ── ② 指令加速度 = 抵消引力 + PD ──

        [Fact]
        public void CommandAccel_OnTarget_CancelsGravityExactly()
        {
            // 无误差 ⇒ 指令 = −g（**正好抵消**，不是"近似抵消"）
            var g = new DVector3(0, -8.0, 0);
            var a = HoverCore.CommandAccel(g, DVector3.zero, DVector3.zero);
            Assert.Equal(0.0, a.x); Assert.Equal(8.0, a.y, 12); Assert.Equal(0.0, a.z);
        }

        [Fact]
        public void CommandAccel_OffTarget_AddsPdOnTopOfCancellation()
        {
            var g = new DVector3(0, -8.0, 0);
            var a = HoverCore.CommandAccel(g, Up * 100.0, DVector3.zero, kp: 0.01, kd: 0.2);
            Assert.Equal(9.0, a.y, 12);           // 8（抵消）+ 1（PD）
        }

        // ── ③ 姿态设定点（D10：上轴 = 本地铅垂）──

        [Fact]
        public void UpAxisTarget_IsLocalVertical_Normalized()
        {
            var t = HoverCore.UpAxisTarget(new DVector3(3, 4, 0));
            Assert.Equal(0.6, t.x, 12);
            Assert.Equal(0.8, t.y, 12);
            Assert.Equal(0.0, t.z);
            Assert.Equal(1.0, t.magnitude, 12);
        }

        [Fact]
        public void UpAxisTarget_DegeneratePosition_FallsBackToWorldUp()
        {
            Assert.Equal(Up, HoverCore.UpAxisTarget(DVector3.zero));
        }

        // ── ④ 定点目标（固连点跟着天体转）──

        [Fact]
        public void TargetState_FollowsBodyRotation()
        {
            const double R = 5_000_000.0, omega = 2.0 * Math.PI / 60000.0, alt = 20000.0;
            var (p0, v0) = HoverCore.TargetState(R, 0.0, 0.0, alt, omega, 0.0);
            var (p1, v1) = HoverCore.TargetState(R, 0.0, 0.0, alt, omega, 60000.0);   // 一个自转周期后

            Assert.Equal(R + alt, p0.magnitude, 6);
            Assert.Equal(R + alt, p1.magnitude, 6);
            Assert.Equal(p0.x, p1.x, 6); Assert.Equal(p0.z, p1.z, 6);   // 同上一点（转回来了）
            // 速度 = 共转切向（**不是零** —— 定点在惯性系里是圆运动）
            Assert.Equal(HoverCore.CoRotationSpeed(R + alt, 0.0, omega), v0.magnitude, 6);
            Assert.Equal(v0.magnitude, v1.magnitude, 6);
        }

        // ── ④b [A8] 料尽 ⇒ 退出悬浮（真值表；2026-10-07 自动判据抓到缺口后补的规则）──

        [Theory]
        [InlineData(0, 0, false)]   // 没装网 ⇒ 配置问题，**不改状态**（留着让人看见）
        [InlineData(1, 0, false)]   // 一网、有料 ⇒ 继续悬
        [InlineData(2, 1, false)]   // 两网、只干一个 ⇒ 还能悬（另一个顶着）
        [InlineData(1, 1, true)]    // 一网、干透 ⇒ 必须退出
        [InlineData(2, 2, true)]    // 两网全干 ⇒ 必须退出
        public void ShouldExitHoverWhenDry_TruthTable(int nets, int dry, bool expect)
            => Assert.Equal(expect, HoverCore.ShouldExitHoverWhenDry(nets, dry));

        /// <summary>
        /// 试验台**推重比**：长续航档（油多）必须同时给推力 —— 实测抓过一次"油加到 6 t 而推力没跟上"
        /// ⇒ a = 6.7 m/s² **低于**本地重力 7.9 ⇒ 悬不起来（物理档一路掉到 −12.5 km）。
        ///
        /// 判据取"**必须超过重力、且留出控制余量**"（余量 = a_max − g；它决定能不能刹住开局那 20 m/s）：
        /// · 手动档实测 TWR **1.27**（a=10.1 vs g=7.9 ⇒ 余量 2.2 m/s²，游戏中确实悬住了）⇒ 判 ≥1.2；
        /// · 长续航档必须**厚**（它是自动跑批连着跑几条链用的）⇒ 判 ≥2.0。
        /// ⚠ 别把判据写成"≥1.5"这种拍脑袋的数 —— 第一版就是这么写的，被手动档 1.27 当场判红，
        ///   而那个 1.27 **本来是能悬的**（判据比现实更严 = 假红，也属仪器事故）。
        /// </summary>
        [Fact]
        public void HoverTestbed_ThrustToWeight_IsAdequate()
        {
            const double gravity = 7.904;   // 30 km 处重力（游戏实测）
            // 手动档：3000 kg 油 + 3000 kg 船体，ve 3000 / ṁ 20 ⇒ 60 kN
            double twrManual = 60000.0 / 6000.0 / gravity;
            Assert.True(twrManual >= 1.2,
                "手动档推重比应 ≥1.2（实测 1.27 能悬），实测 " + twrManual.ToString("F3"));
            // 长续航档：6000 kg 油 + 3000 kg 船体，ve 9000 / ṁ = 9t·8/9000·2.5 = 20 ⇒ 180 kN
            double mf = (3000.0 + 6000.0) * 8.0 / 9000.0 * 2.5;
            double thrust = 9000.0 * mf;
            double twrLong = thrust / 9000.0 / gravity;
            Assert.True(twrLong >= 2.0,
                "长续航档推重比应 ≥2.0（自动跑批连跑多条链），实测 " + twrLong.ToString("F3") +
                "（ṁ=" + mf.ToString("F1") + " kg/s ⇒ " + (thrust / 1000.0).ToString("F0") + " kN）");
        }

        // ── ⑤ 解析档等效计费（[A8]/D8 的对照口径）──

        [Fact]
        public void EquivalentDvPerStep_IsGravityTimesDt()
        {
            Assert.Equal(15.9, HoverCore.EquivalentDvPerStep(7.95, 2.0), 12);
            Assert.Equal(0.0, HoverCore.EquivalentDvPerStep(0.0, 2.0));
            Assert.Equal(0.0, HoverCore.EquivalentDvPerStep(7.95, 0.0));
            Assert.Equal(0.0, HoverCore.EquivalentDvPerStep(-1.0, 2.0));
        }
    }
}
