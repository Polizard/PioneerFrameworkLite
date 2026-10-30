using System;

namespace PioneerFramework
{
    /// <summary>
    /// **三轴各向异性阻力的唯一算式（[A2-2]，计划稿 §2.2 / D13，2026-10-06）**
    ///
    /// ```
    /// p_i   = v̂_air · ê_i                          // 气流方向在机体三轴（前/左/上）上的分量
    /// A_eff = √( Σ_i ((Cd·A)_i · p_i)² )           // 有效迎风面积 = 三轴投影面积的二次范数
    /// F     = − q · A_eff · v̂_air       (q = ½ρ|v_air|²)
    /// a     = F/m
    /// ```
    ///
    /// ## 两条红线（照 D13）
    /// · **禁用"逐轴力相加"**：投影面积之和 ≠ 投影面积的范数；斜向气流下逐轴相加给出 √3 倍量级的
    ///   **错误**。本类是唯一实现，风洞台架里有一条专门抓它（`ObliqueFlow_...`）。
    /// · **退化要求：三轴系数相等 ⇒ 任意姿态与各向同性公式逐位一致**——`AeroShape.IsIsotropic`
    ///   用**精确相等**判定走标量路径（`A_eff ≡ Cd·A`、不做 √），故与既有各向同性算式**逐位相同**
    ///   （"一切新能力 = 既有机制的数据退化情形"那一条的落地）。
    ///
    /// ## 谁来调
    /// 消费者只有一个：`SpaceGameComp.ComputeGroupPhysics` 的力总和（系统系）。
    /// 空气相对速度由**唯一换系口** `SurfaceFrame.SurfaceVelocity` 给（`v_air = v − ω·(p×up)`）；
    /// 密度由 `AtmosphereProfile.Density(h)` 给（64 点表）。
    ///
    /// 【边界】本文件**纯**（只 using System）⇒ 链入 `Pure.Tests`，风洞台架（五条判据）在那边。
    /// </summary>
    public static class AeroDrag
    {
        /// <summary>
        /// 有效迎风面积 `A_eff` (m²)。
        /// ⚠ `vAirUnit` **必须是单位方向**——各向同性退化路径直接返回系数、不再乘 |v̂|（这正是"逐位一致"的来源）；
        /// 非单位入参只在退化路径上少乘一个模长（`Accel` 内部恒传归一化后的方向）。
        /// 机体三轴（前/左/上）必须是**正交单位**基（由 `rotation` 旋转世界基向量得到，见调用点）。
        /// </summary>
        public static double EffectiveArea(in AeroShape shape, DVector3 vAirUnit,
            DVector3 axisForward, DVector3 axisLeft, DVector3 axisUp)
        {
            // 球退化（三轴精确相等）：投影面积与方向无关 ⇒ 直接返回系数（与"球"一致，也保逐位一致）
            if (shape.IsIsotropic)
                return shape.forwardCdA;

            // 2026-10-28：改成**盒子解析投影**（|n·轴| 加权求和）。
            // 旧口径是二次范数 √(Σ((CdA)·p)²)：立方体沿体对角只给 a²，真值 √3·a² ⇒ 斜向低估。
            // 禁令针对的是"**力**逐轴相加"；**面积**的逐轴投影加权正是盒模型的正解。
            return shape.ProjectedCdA(vAirUnit, axisForward, axisLeft, axisUp);
        }

        /// <summary>
        /// 气动加速度（系统系；调用方把它加进力总和）：`a = −q·A_eff/m · v̂_air`。
        ///
        /// 精确零的入口（域外零成本的最后一道）：`ρ ≤ 0`（真空/未定）· `mass ≤ 0` ·
        /// `v_air = 0`（与大气共转——"大气随行星转"的语义）· 有效面积 ≤ 0。
        ///
        /// ⚠ **局部带估算**：只用当前时刻的 ρ 与 v_air（不估它们在一个物理步内的变化）——
        ///   dt=2 s 正常时标下是二阶小量，战斗时标（1/30 s）更小；分子步路径按子步重算（见调用点）。
        /// </summary>
        public static DVector3 Accel(double rho, DVector3 vAir, in AeroShape shape,
            DVector3 axisForward, DVector3 axisLeft, DVector3 axisUp, double mass)
        {
            if (!(rho > 0.0) || !(mass > 0.0)) return DVector3.zero;
            double speed = vAir.magnitude;
            if (!(speed > 0.0)) return DVector3.zero;

            DVector3 vHat = vAir / speed;
            double aEff = EffectiveArea(shape, vHat, axisForward, axisLeft, axisUp);
            if (!(aEff > 0.0)) return DVector3.zero;

            double q = 0.5 * rho * speed * speed;
            return vHat * (-(q * aEff / mass));
        }
    }
}
