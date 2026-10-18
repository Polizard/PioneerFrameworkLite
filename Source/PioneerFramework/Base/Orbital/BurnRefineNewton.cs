// ─────────────────────────────────────────────────────────────────────────────
// 候迁件 · 有限燃烧**开环精修搜索体**（牛顿 + 半步回溯）
//
// 迁移目标：**替换 `Source/PioneerFramework/Base/Orbital/BurnArcCompensation.cs` 的搜索体**
//   （`TrySolve` 里的"9×9 粗网格 + 3 轮 5×5 细化"），**保留**其参数化 / `Solution` / 代价 / 调用方：
//   · 参数化 = 我们的 **(Δv 缩放, 点火相位偏移)**（擂台 C 格已证 2 自由度足够）
//   · 目标 = **速度口径 miss（m/s）**（擂台结论：把开环判据从"要素松口径"升到"速度紧口径"）
//   · 网格法**保留作兜底**（牛顿无解/越界/不收敛 ⇒ 由调用方回退到网格，取"最近可行解"）
//
// 算法来源（逐项对 KSA `FiniteBurnRefiner.cs`，均已在擂台核对）：
//   前向差分雅可比 → 3×3 Cramer 三重积闭式解；回溯 = 半步 ×≤8；迭代 ≤12；收敛门 |miss| ≤ 1e-4
//
// ⚠ 五条护栏（2026-10-17 复核轮定，必须保留——去掉任何一条都是回归）：
//   ① Δv 幅值上下界（`MaxScaleDeviation`）——**KSA 对 Δv 无任何界**，坏雅可比步可能命令离谱燃烧；
//   ② 收敛状态出参（`Status`）——KSA 的 `Refine` 无论收敛与否都静默返回 best-so-far；
//   ③ 奇异护栏（2×2 正规方程条件数退化 ⇒ `Singular`，交调用方兜底）；
//   ④ 越界时不静默钳位：整步越界 ⇒ 先回溯、仍越界 ⇒ `OutOfBounds`（交调用方兜底）；
//   ⑤ 本件**只做搜索**：miss 函数、前向模型、兜底策略都由调用方注入（保持可测、可替、零项目依赖）。
//
// ⚠ **net48 兼容适配（2026-10-17 迁入时）**：miss 缓冲从 `Span<double>` 换成 3 字段结构体 `Miss3`
//   （net48 的链接测试工程 `MechJebLibTest` 没有 System.Memory），`double.IsFinite` 换成 `IsFinite`
//   本地判据（net5+ API）。**算法、五条护栏、步长与收敛口径逐条不变。**
// ─────────────────────────────────────────────────────────────────────────────
using System;

namespace PioneerFramework.Burn;

public enum RefineStatus
{
	/// <summary>达标（|miss| ≤ tolerance）。</summary>
	Converged,
	/// <summary>迭代上限用尽（返回 best-so-far）。</summary>
	MaxIterations,
	/// <summary>连续无改进（返回 best-so-far）。</summary>
	NoImprovement,
	/// <summary>雅可比退化/无解（交调用方兜底）。</summary>
	Singular,
	/// <summary>步长越界且回溯失败（交调用方兜底）。</summary>
	OutOfBounds,
	/// <summary>输入非法。</summary>
	InvalidInput,
}

public readonly struct RefineResult
{
	public readonly double Scale;      // 解出的 Δv 缩放（1 = 计划）
	public readonly double ShiftSeconds; // 解出的点火相位偏移（s）
	public readonly double Miss;       // 最终 miss 模长（m/s）
	public readonly int Calls;         // 前向调用次数（成本口径）
	public readonly RefineStatus Status;
	public readonly int Iterations;

	public RefineResult(double scale, double shiftSeconds, double miss, int calls, RefineStatus status, int iterations)
	{
		Scale = scale; ShiftSeconds = shiftSeconds; Miss = miss; Calls = calls; Status = status; Iterations = iterations;
	}

	public bool Converged => Status == RefineStatus.Converged;

	public override string ToString() =>
		$"{Status} Δv×{Scale:F6} 相位{ShiftSeconds:+0.00;-0.00;0.00}s miss={Miss:E2} m/s（{Calls} 次调用 / {Iterations} 迭代）";
}

/// <summary>miss 缓冲（3 分量，m/s）。用结构体而不是 Span：net48 的链接测试工程没有 System.Memory。</summary>
public struct Miss3
{
	public double X, Y, Z;
	public double Norm => Math.Sqrt(X * X + Y * Y + Z * Z);
}

/// <summary>miss 函数：把 (缩放, 相位) 映射到三分量 miss（m/s），写入调用方提供的缓冲（零分配）。</summary>
public delegate void BurnMissFn(double scale, double shiftSeconds, ref Miss3 miss);

public static class BurnRefineNewton
{
	public const int MaxIterations = 12;          // 与 KSA 同
	public const int MaxHalvings = 8;             // 与 KSA 同
	public const double DefaultTolerance = 1e-4;  // m/s（KSA 原口径）

	/// <summary>护栏①：Δv 缩放允许的偏离（默认 ±5%；生产网格的箱是 ±1%，此处放宽到"护栏"而非"搜索域"）。</summary>
	public const double DefaultMaxScaleDeviation = 0.05;

	/// <summary>护栏①：点火相位允许的偏移（默认 ±40 s）。</summary>
	public const double DefaultMaxShiftSeconds = 40.0;

	/// <summary>步长口径（与 KSA 的 `max(0.001, 1e-4·|Δv|)` 同一精神：使 miss 变化与 miss 量级可比）：
	/// 缩放步（无量纲）与相位步（s）由调用方按自己的量纲给定；默认值经擂台 C 格验证可用。</summary>
	public const double DefaultScaleStep = 1e-4;
	public const double DefaultShiftStep = 0.05;

	/// <summary>
	/// 2 自由度牛顿 + 半步回溯求根（3 方程 / 2 未知 ⇒ 最小二乘，2×2 正规方程）。
	/// </summary>
	/// <param name="miss">miss 函数（注入；擂台/生产各自适配）。</param>
	/// <param name="scale0">初值：Δv 缩放（计划 = 1）。</param>
	/// <param name="shift0">初值：相位偏移（计划 = 0）。</param>
	public static RefineResult Refine(BurnMissFn miss, double scale0 = 1.0, double shift0 = 0.0,
		double tolerance = DefaultTolerance,
		double maxScaleDeviation = DefaultMaxScaleDeviation,
		double maxShiftSeconds = DefaultMaxShiftSeconds,
		double scaleStep = DefaultScaleStep,
		double shiftStep = DefaultShiftStep)
	{
		if (miss == null || !(tolerance > 0.0) || !(maxScaleDeviation > 0.0) || !(maxShiftSeconds > 0.0)
			|| !(scaleStep > 0.0) || !(shiftStep > 0.0) || !IsFinite(scale0) || !IsFinite(shift0))
		{
			return new RefineResult(scale0, shift0, double.NaN, 0, RefineStatus.InvalidInput, 0);
		}

		int calls = 0;
		double scale = scale0, shift = shift0;
		Miss3 m = default;
		Miss3 mTry = default;
		Miss3 c0 = default;   // J 列 1（对缩放）
		Miss3 c1 = default;   // J 列 2（对相位）

		miss(scale, shift, ref m); calls++;
		double norm = m.Norm;
		if (norm <= tolerance)
			return new RefineResult(scale, shift, norm, calls, RefineStatus.Converged, 0);

		for (int iter = 0; iter < MaxIterations; iter++)
		{
			// ── 前向差分雅可比 ──────────────────────────────────────────────
			{
				double s2 = scale + scaleStep, sh2 = shift;
				if (!InBounds(s2, sh2, maxScaleDeviation, maxShiftSeconds))
					return new RefineResult(scale, shift, norm, calls, RefineStatus.OutOfBounds, iter);
				miss(s2, sh2, ref mTry); calls++;
				c0.X = (mTry.X - m.X) / scaleStep;
				c0.Y = (mTry.Y - m.Y) / scaleStep;
				c0.Z = (mTry.Z - m.Z) / scaleStep;
			}
			{
				double s2 = scale, sh2 = shift + shiftStep;
				if (!InBounds(s2, sh2, maxScaleDeviation, maxShiftSeconds))
					return new RefineResult(scale, shift, norm, calls, RefineStatus.OutOfBounds, iter);
				miss(s2, sh2, ref mTry); calls++;
				c1.X = (mTry.X - m.X) / shiftStep;
				c1.Y = (mTry.Y - m.Y) / shiftStep;
				c1.Z = (mTry.Z - m.Z) / shiftStep;
			}

			// ── 最小二乘：2×2 正规方程 ──────────────────────────────────────
			double a11 = c0.X * c0.X + c0.Y * c0.Y + c0.Z * c0.Z;
			double a12 = c0.X * c1.X + c0.Y * c1.Y + c0.Z * c1.Z;
			double a22 = c1.X * c1.X + c1.Y * c1.Y + c1.Z * c1.Z;
			double b1 = -(c0.X * m.X + c0.Y * m.Y + c0.Z * m.Z);
			double b2 = -(c1.X * m.X + c1.Y * m.Y + c1.Z * m.Z);
			double det = a11 * a22 - a12 * a12;
			double condScale = a11 + a22;
			// 护栏③：条件数退化 ⇒ 奇异（相对判据，尺度无关）
			if (!(Math.Abs(det) > 1e-12 * condScale * condScale) || !IsFinite(det))
				return new RefineResult(scale, shift, norm, calls, RefineStatus.Singular, iter);

			double dScale = (b1 * a22 - b2 * a12) / det;
			double dShift = (a11 * b2 - a12 * b1) / det;

			// ── 半步回溯（≤8；只接受严格改进；护栏①④：越界先回溯、再降级） ──
			bool improved = false;
			bool anyInBounds = false;
			for (int h = 0; h < MaxHalvings; h++)
			{
				double s2 = scale + dScale, sh2 = shift + dShift;
				if (InBounds(s2, sh2, maxScaleDeviation, maxShiftSeconds))
				{
					anyInBounds = true;
					miss(s2, sh2, ref mTry); calls++;
					if (mTry.Norm < norm)
					{
						scale = s2; shift = sh2;
						m = mTry;
						norm = m.Norm;
						improved = true;
						break;
					}
				}
				dScale *= 0.5; dShift *= 0.5;
			}
			if (!improved)
				return new RefineResult(scale, shift, norm, calls,
					anyInBounds ? RefineStatus.NoImprovement : RefineStatus.OutOfBounds, iter + 1);

			if (norm <= tolerance)
				return new RefineResult(scale, shift, norm, calls, RefineStatus.Converged, iter + 1);
		}
		return new RefineResult(scale, shift, norm, calls, RefineStatus.MaxIterations, MaxIterations);
	}

	private static bool InBounds(double scale, double shift, double maxScaleDev, double maxShift)
		=> Math.Abs(scale - 1.0) <= maxScaleDev && Math.Abs(shift) <= maxShift;

	private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}
