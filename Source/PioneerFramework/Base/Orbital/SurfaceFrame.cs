using System;

namespace PioneerFramework
{
    /// <summary>
    /// **天地映射的纯数学**（固连系 ↔ ECI / 经纬度 / 地表状态）。
    ///
    /// ## 为什么它在这里而不在 `Launch/GetSurfaceState.cs`
    /// 这些函数**只依赖 `DVector3` 与三角函数**（不碰 `PlanetLayer` / `Verse`），
    /// 而**纯层要用它们**：`OrbitView.North/East` 之外，`LAN` / `Longitude` / `Stationary`
    /// 这三个 Operation 都要"由位置反求经纬度"，而 Operation 层是**纯**的（链入 net48 测试工程）。
    /// ⇒ 把它们放在纯层、由 `GetSurfaceState` **转发**，**实现仍只有一份**（J2），
    /// 且这份"冻结约定"的文档就停在实现现场（不是停在转发处）。
    ///
    /// ## 冻结约定（原文来自 `Launch/GetSurfaceState.cs`，2026-09-05 以 RimWorld 源码逐环节锚定）
    /// · **自转轴 = +y（北极）**，赤道面 = xz；
    /// · RW 经度 0 = 世界 **−z**（`LongLatOf: λ=Atan2(x,−z)`），**东（λ 增）= −z→+x**；
    /// · RW 正午太阳 = `(0,0,−1)`，固连系中太阳星下点经度随时间减少（西移）；
    /// · 固连系地表位置 `p_fix = R(sinλcosφ, sinφ, −cosλcosφ)`；
    /// · 行星在 ECI 中自转 `p_eci(t) = Ry(−ωt)·p_fix`（惯性系太阳固定 + 固连系太阳西移 ⇒ 唯一自转解）。
    ///
    /// ## ⚠ 推论：**本 mod 里"顺行"是 `h ∥ −Y`**（`inclination = 180°`，不是 0°）
    /// 由"东 = −z→+x"与 `SurfaceVelocity = ω·(−p_z, 0, p_x)`：
    /// 在 `p = +x̂` 处东向速度 = `ω·(0,0,R) = +ẑ` ⇒ **`h = p×v ∥ −Ŷ`**。
    /// 而 `inclination`（`Astro` 口径）是 `h` 与 `+极轴` 的夹角 ⇒ **顺行赤道轨读出 180°**。
    /// ⇒ 与 KSP/MJ 的直觉相反；面向玩家的倾角参数要做 `180° − i` 换算（见 `OperationInclination`）。
    /// （游戏内实测 + 纯测试 `ProgradeEquatorialOrbit_HasInclination180_Not0` 双向钉住。）
    ///
    /// 【边界】本文件**纯**（不引 UnityEngine/Verse）⇒ 链入 `Pure.Tests` 与 `MechJebLibTest`，可单测。
    /// </summary>
    public static class SurfaceFrame
    {
        /// <summary>固连系 → ECI：`Ry(−ωt)`（惯性系太阳固定 + 固连系太阳星下点西移 ⇒ 唯一自转解）。</summary>
        public static DVector3 FixedToEci(DVector3 pFixed, double omega, double physicsTick)
        {
            double ang = omega * physicsTick;
            double c = Math.Cos(ang), s = Math.Sin(ang);
            return new DVector3(pFixed.x * c - pFixed.z * s, pFixed.y, pFixed.x * s + pFixed.z * c);
        }

        /// <summary>ECI → 固连系：`Ry(+ωt)`（<see cref="FixedToEci" /> 的逆变换，与渲染端 `ApplyVisualRotation` 同构）。</summary>
        public static DVector3 EciToFixed(DVector3 pEci, double omega, double physicsTick)
        {
            double ang = omega * physicsTick;
            double c = Math.Cos(ang), s = Math.Sin(ang);
            return new DVector3(pEci.x * c + pEci.z * s, pEci.y, -pEci.x * s + pEci.z * c);
        }

        /// <summary>
        /// ECI → 固连**经纬度**（rad）。**与 RW `LongLatOf` 同约定**：`λ = Atan2(x, −z)`、`φ = Asin(y/r)`。
        /// 经纬度 → ECI 走 <see cref="SurfaceState" />；此为反向。
        /// </summary>
        public static (double latRad, double lonRad) EciToLongLat(DVector3 pEci, double omega, double physicsTick)
        {
            DVector3 pFixed = EciToFixed(pEci, omega, physicsTick);
            double r = pFixed.magnitude;
            if (r < 1e-9) return (0.0, 0.0);
            double lat = Math.Asin(Math.Max(-1.0, Math.Min(1.0, pFixed.y / r)));
            double lon = Math.Atan2(pFixed.x, -pFixed.z);
            return (lat, lon);
        }

        /// <summary>
        /// 固连经纬度 → **表面点状态**（ECI 位置与速度）。`latRad` 为纬度 φ、`lonRad0` 为**固连**经度 λ₀。
        /// </summary>
        /// <param name="radius">天体半径 (m)。</param>
        /// <param name="omega">自转角速度 (rad/s)。</param>
        /// <param name="physicsTick">当前物理时间 (s)。</param>
        public static (DVector3 pos, DVector3 vel) SurfaceState(double radius, double latRad, double lonRad0, double omega, double physicsTick)
        {
            double lon = lonRad0 + omega * physicsTick;   // ECI 经度 = λ0 + ωt（Ry(−ωt) 自转）
            double cosP = Math.Cos(latRad), sinP = Math.Sin(latRad);
            double sinL = Math.Sin(lon), cosL = Math.Cos(lon);
            // p = R·(cosφ·sinλ, sinφ, −cosφ·cosλ)（RimWorld 轴向约定）
            var p = new DVector3(sinL * cosP, sinP, -cosL * cosP) * radius;
            // v = ω·(p×up) = ωRcosφ·(cosλ, 0, sinλ)（真实自转切向；注意与 ω×p 反向）
            var v = new DVector3(cosL, 0.0, sinL) * (omega * radius * cosP);
            return (p, v);
        }

        /// <summary>表面上方出生点：`p + radial·alt`，速度继承地表（尚未被推力改变）。</summary>
        public static (DVector3 pos, DVector3 vel) SurfaceStateAbove((DVector3 pos, DVector3 vel) surface, double altMeters)
        {
            double r = surface.pos.magnitude;
            if (r < 1e-3) return (surface.pos, surface.vel);
            var radial = surface.pos / r;
            return (surface.pos + radial * altMeters, surface.vel);
        }

        /// <summary>表面点自转速度（ECI，米/秒）：`v = ω·(p×up) = ω·(−p_z, 0, p_x)`（**与 ω×p 反向**）。</summary>
        public static DVector3 SurfaceVelocity(DVector3 pEci, double omega)
            => new DVector3(-pEci.z, 0.0, pEci.x) * omega;

        /// <summary>
        /// **相对地表**（共转系）的速度 = 相对天体的速度 − 地表共转速度。
        /// ⚠ 两个输入都必须**以天体中心为原点**（`SpaceEntityGroup.localPosition/localVelocity` 就是这个口径）。
        /// </summary>
        public static DVector3 GroundRelativeVelocity(DVector3 pEci, DVector3 vEci, double omega)
            => vEci - SurfaceVelocity(pEci, omega);

        /// <summary>
        /// **接地速度**（m/s）= <see cref="GroundRelativeVelocity"/> 的模。
        /// 这是"着陆/坠毁"唯一该用的速度口径（见 <see cref="SpaceConstants.MaxSafeLandingSpeedMps"/>）：
        /// 惯性系速度会白送你一份地表共转（地球赤道 523.6 m/s）。
        /// </summary>
        public static double GroundSpeed(DVector3 pEci, DVector3 vEci, double omega)
            => GroundRelativeVelocity(pEci, vEci, omega).magnitude;
    }
}
