using System;

namespace PioneerFramework
{
    /// <summary>
    /// **大气密度提供者（[A1]，计划稿 §2.1，2026-10-06）** —— 气动（[A2]）/ 发动机包线（[A4]）/
    /// 进气（[A3]）**共用**的唯一一份 ρ(h)。
    ///
    /// ## 形状（计划稿 §2.1 的裁定）
    /// · **64 点表 + 线性插值**：建表时用 `exp` 采样一次（每天体一次，不在热路径上）；
    ///   运行期只做"一次乘法取索引 + 一次线性插值"——守住 D2 红线"**热路径无超越函数**（ρ 用表不用 exp）"。
    /// · 参数（ρ₀、标尺高度 H）由 `CelestialBodyDef` 提供；上界 = `atmosphereThickness`（地球 100 km）。
    /// · **顶之上恒 0**（硬截断）——"大气有顶"的模型固有台阶（与 KSP 同态）；表顶值 = ρ₀·exp(−top/H)
    ///   本就 ~1e-5·ρ₀ 量级 ⇒ 台阶在物理上可忽略，**不做平滑**（平滑会踩"不许改既有算式/偏离"那条）。
    /// · **惰性（缺席即零）**：ρ₀ ≤ 0 或 H ≤ 0 或 top ≤ 0 ⇒ **不建表**，`Density` 恒返 0。
    ///   太阳/月球 `atmosphereThickness = 0` 走这里；**[补2]：ρ₀/H 的数值延后裁定**，
    ///   数值未定期间整个模型天然惰性（对既有 42 条验收逐位无扰）。
    ///
    /// ## 为什么不是"每次调用算 exp"
    /// 密度是**每对象每步**都要读的量（[A2] 阻力、[A4] 包线、(A3) 进气各一次），
    /// 而 `exp` 在 .NET 上是几十 ns 级 ⇒ 表+插值把它压到几次算术（D2：每对象每步 ~400 ops 预算内）。
    ///
    /// ## 精度（纯测试钉住）
    /// 线性插值对指数式的最大相对误差 ≈ (Δh)²/(8H²)（Δh = top/(N−1)）；
    /// 对 H=8500 m、top=100 km：Δh≈1587 m ⇒ ≈0.44%（判据 ≤1%）。密度误差按比例进入阻力/推力，
    /// 半个百分点在"数据本来就待定"的 L1 完全够用。
    ///
    /// 【边界】本文件**纯**（只 `using System`，不引 Verse/Unity）⇒ 链入 `Pure.Tests`，可单测。
    /// </summary>
    public static class AtmosphereModel
    {
        /// <summary>表点数（计划稿 §2.1 定：64）。</summary>
        public const int TableSize = 64;

        /// <summary>
        /// **有没有任何一份"活的"大气**（[A2-2] 热路径的"缺席即零"闸门）：全库当前无大气
        /// （[补2] 数值未定）⇒ 物理步里**一次静态 bool 读**就能跳过整个气动分支（连天体 Def 查表都不做）。
        /// 写端只在 **`Build` 成功**时自增；建表由 `EnsureAtmosphereProfile` 在**主线程、进并行区之前**
        /// 完成（`CelestialBodyManager.EnsureInitialized` 预暖）⇒ 读端在并行区里只读。
        /// ⚠ 只表达"有没有"，并发首建导致的多计无害（单调，不回退）。
        /// </summary>
        public static bool AnyAtmosphereActive => anyActive > 0;

        private static volatile int anyActive;

        /// <summary>
        /// 建一份剖面（每个天体一次，由 `CelestialBodyDef.EnsureAtmosphereProfile` 缓存）。
        /// **任何参数非正 ⇒ 返回惰性剖面**（`IsActive == false`、`Density` 恒 0）。
        /// </summary>
        public static AtmosphereProfile Build(double rho0, double scaleHeight, double topAltitude)
        {
            if (!(rho0 > 0.0) || !(scaleHeight > 0.0) || !(topAltitude > 0.0))
                return default(AtmosphereProfile);

            double step = topAltitude / (TableSize - 1);
            var table = new double[TableSize];
            for (int i = 0; i < TableSize; i++)
                table[i] = rho0 * Math.Exp(-(i * step) / scaleHeight);

            anyActive++;
            return new AtmosphereProfile(rho0, scaleHeight, topAltitude,
                (TableSize - 1) / topAltitude, table);
        }
    }

    /// <summary>
    /// 一份天体的**大气密度剖面**（值类型；由 <see cref="AtmosphereModel.Build"/> 建立、按天体缓存一次）。
    ///
    /// ⚠ 建表是**唯一一次分配**（64×8 B）。若首次访问发生在**并行物理区**，那次分配就落在并行区里
    /// （本库红线"并行区只读无分配"）⇒ 调用方（[A2]）应在**进并行区前预暖**（与
    /// `CelestialBodyManager.GetSystemMembers` 的"并行区建表"事故同一纪律）。稳态下 `Density` 无分配。
    ///
    /// ⚠ 惰性剖面的三个公开字段全是 0 —— **不要去读它们当"参数"**，先看 <see cref="IsActive"/>。
    /// </summary>
    public readonly struct AtmosphereProfile
    {
        /// <summary>基准高度（h=0）处的密度 ρ₀ (kg/m³)。惰性剖面 = 0。</summary>
        public readonly double rho0;

        /// <summary>标尺高度 H (m)（ρ(h) = ρ₀·exp(−h/H) 的 H）。惰性剖面 = 0。</summary>
        public readonly double scaleHeight;

        /// <summary>大气顶高度 (m)（= `CelestialBodyDef.atmosphereThickness`；h ≥ 它 ⇒ ρ ≡ 0）。惰性剖面 = 0。</summary>
        public readonly double topAltitude;

        /// <summary>`(TableSize−1)/topAltitude`：热路径一次乘法取索引（省一次除法）。</summary>
        private readonly double invStep;

        /// <summary>TableSize 个采样（h = i·top/(N−1) 处的 ρ）；**null ⇒ 惰性剖面**（恒 0）。</summary>
        private readonly double[]? table;

        internal AtmosphereProfile(double rho0, double scaleHeight, double topAltitude,
            double invStep, double[] table)
        {
            this.rho0 = rho0;
            this.scaleHeight = scaleHeight;
            this.topAltitude = topAltitude;
            this.invStep = invStep;
            this.table = table;
        }

        /// <summary>是否有效（参数齐、表已建）。false ⇒ <see cref="Density"/> 恒 0。</summary>
        public bool IsActive => table != null;

        /// <summary>
        /// **"气动不可忽略"判据（[A6] 的唯一语义）**：高度在 `k·H` 之下、且在大气顶之下。
        /// 与 `ρ/ρ₀ > e^−k` 等价（用高度直接比 —— **不用超越函数**）。
        /// 惰性剖面恒 false（没有大气 ⇒ 谈不上"不可忽略"；[补2] 数值未定期间整条规则因此休眠）。
        /// 两个消费者：① 引力掩码闩锁的**恢复**判据（取反）；② **解析圆轨准入**（为 true ⇒ 拒绝进入）。
        /// </summary>
        public bool IsAerodynamicallySignificant(double altitudeMeters, double restoreScaleHeights)
        {
            if (table == null) return false;
            if (!(altitudeMeters < topAltitude)) return false;   // 出大气顶 ⇒ 可忽略
            return altitudeMeters < scaleHeight * restoreScaleHeights;
        }

        /// <summary>
        /// **"在大气壳内"（ρ &gt; 0）** —— 解析圆轨准入判据用它（[A6] 的"互斥"）。
        /// ⚠ 与 <see cref="IsAerodynamicallySignificant"/> **不是同一条**：那条管"气动可以忽略到什么程度"
        /// （9-10 个标尺高度，供引力掩码的闩锁恢复）；这条只管"**阻力是否严格为零**"——
        /// 解析轨（on-rails）不积分、天然漏掉阻力，所以它只接受零阻力的点。
        /// </summary>
        public bool IsInsideAtmosphere(double altitudeMeters) => table != null && altitudeMeters < topAltitude;

        /// <summary>
        /// 空气密度 (kg/m³)。**唯一实现**：
        /// h ≤ 0 ⇒ ρ₀（地表/地下钳位）；h ≥ top ⇒ 0（硬截断）；表内 ⇒ 相邻两点线性插值。
        /// </summary>
        public double Density(double altitudeMeters)
        {
            if (table == null) return 0.0;
            if (!(altitudeMeters > 0.0)) return rho0;
            if (altitudeMeters >= topAltitude) return 0.0;

            double t = altitudeMeters * invStep;
            int i = (int)t;
            // 浮点极近 top 时 t 可能取到 N−1（整数）⇒ 钳到 N−2，插值权重 f=1 正好落在表顶值上
            if (i >= AtmosphereModel.TableSize - 1) i = AtmosphereModel.TableSize - 2;
            return table[i] + (table[i + 1] - table[i]) * (t - i);
        }
    }
}
