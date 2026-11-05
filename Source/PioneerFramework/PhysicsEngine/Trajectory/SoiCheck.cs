using System;

namespace PioneerFramework
{
    /// <summary>
    /// SOI 判定纯核（2026-11-04 用户裁定：**立方体** SOI）。
    ///
    /// 边界 = 轴对齐立方体、半边长 R：Inside ⇔ max(|x|,|y|,|z|) ≤ R。
    /// 就一层，不做三轴盒粗筛/平方精筛——盒本身就是 SOI。
    ///
    /// ⚠ 90% 引导/捕获仍用**半径**（positionBody.magnitude ≤ 0.9R），不是盒角；
    ///   盒只用于进/出 SOI 与换系判定。滞回仍由调用方给 0.99R/1.01R。
    /// </summary>
    public static class SoiCheck
    {
        public enum Relation
        {
            Inside,
            Outside,
        }

        /// <param name="x">相对目标天体的位置分量（米）。</param>
        /// <param name="y">同上。</param>
        /// <param name="z">同上。</param>
        /// <param name="radius">盒半边长（米，&gt;0）；立方体 SOI 下 = 希尔半径。</param>
        public static Relation Classify(double x, double y, double z, double radius)
        {
            if (!(radius > 0.0) || !double.IsFinite(radius))
                throw new ArgumentOutOfRangeException(nameof(radius));

            return Math.Abs(x) <= radius && Math.Abs(y) <= radius && Math.Abs(z) <= radius
                ? Relation.Inside
                : Relation.Outside;
        }

        public static bool IsInside(double x, double y, double z, double radius)
            => Classify(x, y, z, radius) == Relation.Inside;
    }
}
