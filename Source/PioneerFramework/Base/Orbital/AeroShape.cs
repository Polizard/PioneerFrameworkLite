using System;

namespace PioneerFramework
{
    /// <summary>
    /// **气动外形的薄描述（[A2-1]，计划稿 D13 / [补1]，2026-10-06）** —— 三轴迎风面积系数
    /// `(Cd·A)`（单位 m²）+ 参考长度。**只有这一个类型**（D13：一个 struct、两个生产者、一个消费者，
    /// 不做第二套类型系统）。
    ///
    /// ## 三轴 = 船体局部系的前 / 左 / 上
    /// 与 `boundsSizeShip` 同系（Unity 约定：前 = +z、上 = +y、左 = −x；面积取"垂直于该轴的两个尺寸之积"）。
    ///
    /// ## 退化要求（D13，判据 1）
    /// **三轴系数相等 ⇒ 任意姿态下与各向同性公式逐位一致** —— 本 struct 用
    /// <see cref="IsIsotropic"/> 给消费者一个**精确相等**判定（不是容差）：各向同性时消费者走标量路径
    /// （`A_eff = Cd·A`，恰是既有各向同性公式），从而把"斜向气流下不退化"的 1/3 量级错误挡在门外。
    ///
    /// ## 谁生产
    /// · 无 Map（第一批）：`FromBox` ← `SpacePart.boundsSizeShip`（盒模型）；
    /// · 有 Map（[补1]：**不第一批做**）：地图足迹 + 浮雕三档 ⇒ 先用 `FromBox` 的显式 fallback
    ///   （map 组的 part 也带 bounds），换生产者时不改本类型；
    /// · 特殊船可覆盖（数据）。
    ///
    /// ## 为什么没有"逐轴力相加"
    /// D13 明令禁用：斜向气流下逐轴相加对角飞行给出 1/3 量级的**错误**（因为投影面积之和 ≠ 投影面积的范数）。
    /// 唯一算式在消费者侧：`A_eff = √(Σ_i((Cd·A)_i·p_i)²)`。
    ///
    /// 【边界】本文件**纯**（只 using System）⇒ 链入 `Pure.Tests`，可单测。
    /// </summary>
    public readonly struct AeroShape
    {
        /// <summary>
        /// 默认阻力系数。**2026-10-28 从 1.0 改 0.35**（用户裁定"面积改投影轮廓"同批）：
        /// 1.0 是"钝板/降落伞"口径，真实带整流罩的火箭是 0.3–0.5 ⇒ 旧值让同面积的阻力虚高约 3 倍。
        /// 只影响 `FromBox` 的默认值；显式传参的调用点（台架/合成用例）不受影响。
        /// </summary>
        public const double DefaultDragCoefficient = 0.35;

        /// <summary>沿船体**前轴**（±z）的 `Cd·A` (m²)。</summary>
        public readonly double forwardCdA;
        /// <summary>沿船体**左/右轴**（±x）的 `Cd·A` (m²)。</summary>
        public readonly double leftCdA;
        /// <summary>沿船体**上/下轴**（±y）的 `Cd·A` (m²)。</summary>
        public readonly double upCdA;
        /// <summary>
        /// 参考长度 (m)：**风标力矩**用的力臂尺度。L1 **没有消费者**（D13：第一期不做风标力矩）
        /// —— 留字段是"只预埋数据形状、不预埋代码"（盒 = 最长边；球 = 0）。
        /// </summary>
        public readonly double referenceLength;

        public AeroShape(double forwardCdA, double leftCdA, double upCdA, double referenceLength = 0.0)
        {
            this.forwardCdA = forwardCdA;
            this.leftCdA = leftCdA;
            this.upCdA = upCdA;
            this.referenceLength = referenceLength;
        }

        /// <summary>三轴系数**精确相等**（退化判定；消费者据此走标量路径，保证逐位一致）。</summary>
        public bool IsIsotropic => forwardCdA == leftCdA && leftCdA == upCdA;

        /// <summary>
        /// 有无"静默零面积"：任一轴系数 ≤ 0（或 NaN）。**生产者必须不喂退化外形**
        /// （[补1]：有 Map 组要有显式 fallback，不许静默零阻力）——这个属性是那条纪律的仪器。
        /// </summary>
        public bool IsDegenerate => !(forwardCdA > 0.0) || !(leftCdA > 0.0) || !(upCdA > 0.0);

        /// <summary>三轴相等的退化数据（球 / 近球火箭 / 悬浮平台；也为"任意姿态逐位一致"提供样本）。</summary>
        public static AeroShape Isotropic(double cdA, double referenceLength = 0.0)
            => new(cdA, cdA, cdA, referenceLength);

        /// <summary>
        /// **沿气流方向的解析投影面���（CdA 口径）**——盒子的标准公式：
        /// `A(n) = |n·x̂|·A_x + |n·ŷ|·A_y + |n·ẑ|·A_z`，轴序 = 前(z)/左(x)/上(y)。
        ///
        /// 对**单个盒子精确**：立方体沿体对角 = √3·a²、沿面对角 = √2·a²、沿轴向 = a²。
        /// 与旧口径（二次范数 `√(Σ((CdA)_i·p_i)²)`）的差别：斜向时范数**低估**（立方体体对角给 a²，
        /// 真值 √3·a²）——旧值是 2026-10-06 为判"逐轴力相加"选的口径，2026-10-28 按盒子投影纠正：
        /// 被禁用的一直是"**力**逐轴相加"，而**面积**的逐轴投影加权本来就是盒模型的正解。
        /// </summary>
        public double ProjectedCdA(DVector3 vHat, DVector3 axisForward, DVector3 axisLeft, DVector3 axisUp)
            => Math.Abs(DVector3.Dot(vHat, axisForward)) * forwardCdA
             + Math.Abs(DVector3.Dot(vHat, axisLeft)) * leftCdA
             + Math.Abs(DVector3.Dot(vHat, axisUp)) * upCdA;

        /// <summary>
        /// **盒模型生产者**（无 Map 的默认导出）：面积 = 垂直于该轴的两个尺寸之积。
        /// 轴序 = 前(z) / 左(x) / 上(y)；参考长度 = 最长边（**只记不消费**）。
        /// ⚠ 退化尺寸（任一 ≤ 0）会给出退化外形 ⇒ 调用方负责"没有 bounds 时走显式 fallback"。
        /// </summary>
        public static AeroShape FromBox(DVector3 boundsSize, double dragCoefficient = DefaultDragCoefficient)
        {
            double sx = boundsSize.x, sy = boundsSize.y, sz = boundsSize.z;
            return new AeroShape(
                forwardCdA: dragCoefficient * sx * sy,   // 垂直于前轴 (z) 的迎风面：x·y
                leftCdA: dragCoefficient * sy * sz,      // 垂直于左右轴 (x)：y·z
                upCdA: dragCoefficient * sx * sz,        // 垂直于上下轴 (y)：x·z
                referenceLength: Math.Max(sx, Math.Max(sy, sz)));
        }
    }
}
