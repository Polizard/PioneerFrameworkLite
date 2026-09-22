using System;
using MechJebLib.Primitives;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **台架的通用 plant（唯一一份）**（B5-5，2026-09-23）。
    ///
    /// ## 为什么要有它
    /// 此前"中推力船"在**两处逐字重复**：`RendezvousGuidanceCoreTests.MidThrustShip` 与
    /// `TerminalCommitComparison.T5Ship`（后者注释里自己写着"逐字照抄"）。两份 plant 是"两份口径"的温床
    /// ——B5 的动机就是"台架与真代码不一致 ⇒ 只能靠猜"；台架**内部**再互相抄一份，是同一个病的轻症。
    ///
    /// ## 参数（两组调用方原本都写这几行）
    /// 干质量 5000 kg · 主网 **8 kN / ve 4000 / ṁ2 / 料 12000 kg** ·
    /// RCS **1.5 kN / ve 1500 / ṁ1 / 料 500 kg**。改这里 = 改全部调用方。
    ///
    /// ## ⚠ 旋向/主推轴约定（踩过，别改）
    /// `CircularPos(r,ang) = (cos,0,sin)·r` ⇒ 轨道法向是 **−Y** ⇒ 顺行切向 = `ĥ×r̂` = **+Z**
    /// ⇒ 初速与**主推轴**都必须是 `(0,0,+1)`。写成 `(0,0,−1)` 会让船**逆行**（与目标永远交不上、
    /// 一路烧到 9.5 km/s 逃逸 —— 而"三档全逃逸"正是"仪器坏了"的签名：连已知会通过的档也逃逸）。
    /// </summary>
    internal static class TestPlants
    {
        /// <summary>与 `SpaceConstants.PrimaryGravitationalParameter` 同值（默认口径）。</summary>
        public const double Mu = 2.0e14;
        public const double RLeo = 5.5e6;        // LEO（地表 +500 km）
        public const double RGeo = 2.6322e7;     // 同步半径（μ/ω² 开立方，本 mod 一天 60000 s）

        public static DVector3 CircularPos(double r, double ang)
            => new DVector3(Math.Cos(ang) * r, 0, Math.Sin(ang) * r);

        public static DVector3 CircularVel(double r, double ang)
        {
            double v = Math.Sqrt(Mu / r);
            return new DVector3(-Math.Sin(ang) * v, 0, Math.Cos(ang) * v);
        }

        /// <summary>
        /// **中推力船**（连位置/速度/主推轴一起给）：圆轨 `(r, ang)` 处、顺行、主推轴 +Z。
        /// </summary>
        public static RendezvousShip MidThrust(double r, double ang)
        {
            var s = RendezvousShip.Spawn(CircularPos(r, ang), CircularVel(r, ang), new DVector3(0, 0, 1));
            s.DryMass = 5000.0;
            s.MainThrust = 8000.0; s.MainVe = 4000.0; s.MainMassFlow = 2.0; s.MainFuel = 12000.0;
            s.RcsThrust = 1500.0; s.RcsVe = 1500.0; s.RcsMassFlow = 1.0; s.RcsFuel = 500.0;
            return s;
        }

        /// <summary>中推力船 @ 同步轨道赤道 = `MidThrust(RGeo, 0)`（原 `TerminalCommitComparison.T5Ship`）。</summary>
        public static RendezvousShip MidThrustAtGeo() => MidThrust(RGeo, 0.0);
    }
}
