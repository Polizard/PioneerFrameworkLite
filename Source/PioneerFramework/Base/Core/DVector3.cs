using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using MechJebLib.Primitives;
using MechJebLib.Utils;
#if !PURE_MATH
using UnityEngine;
using Verse;
#endif

namespace PioneerFramework
{
    /// <summary>
    /// 双精度三维向量。用于所有物理/轨道计算以消除 float 精度问题。
    /// 与 Unity Vector3 可显式双转换（double→float 有精度损失，显式标注）。
    ///
    /// ⚠ **本类型同时是移植面 `V3` 的收编体**（批5 U2/U3，2026-09-28；用户 2026-09-26 裁定
    /// "V3 与 DVector3 统一"）：`V3` 的全部成员逐字搬入本类型，其中**四个分歧成员采用上游语义**
    /// （`Equals` 的 NaN 相等 · `ToString` 的 `[G17]` 不变文化 · `normalized`/`Normalize()` 的 NaN→zero
    /// ⇒ `EqualityHashingTests` / `StringRepresentationTests` 的断言仍全绿）；
    /// 撞名的 6 个 NED 船体常量**上游改名为 `bodyUp/…`**（批5 U1'，值一个都没动），
    /// 世界系那 6 个（`up/down/left/right/forward/back`）保持本类型原值。
    /// `ThirdParty/MechJebLib/Primitives/V3.cs` 已删除，移植面源码靠共享树的
    /// `global using V3 = PioneerFramework.DVector3;` 一个字不改（见 <c>ThirdParty/MechJebLib/_PFL/GlobalAlias.cs</c>）。
    /// </summary>
    public struct DVector3 : IEquatable<DVector3>, IFormattable
    {
        public double x;
        public double y;
        public double z;

        #region 构造函数

        public DVector3(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public DVector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public DVector3(double x, double y)
        {
            this.x = x;
            this.y = y;
            z = 0.0;
        }

        #endregion

        #region 分量别名 / 索引器 / 整组写（来自上游 V3）

        public double roll
        {
            get => x;
            set => x = value;
        }

        public double pitch
        {
            get => y;
            set => y = value;
        }

        public double yaw
        {
            get => z;
            set => z = value;
        }

        public double this[int index]
        {
            get =>
                index switch
                {
                    0 => x,
                    1 => y,
                    2 => z,
                    _ => throw new IndexOutOfRangeException($"Bad vector index {index} in getter")
                };

            set
            {
                switch (index)
                {
                    case 0:
                        x = value;
                        break;
                    case 1:
                        y = value;
                        break;
                    case 2:
                        z = value;
                        break;
                    default:
                        throw new IndexOutOfRangeException($"Bad vector index {index} in setter");
                }
            }
        }

        public void Set(double nx, double ny, double nz)
        {
            x = nx;
            y = ny;
            z = nz;
        }

        #endregion

        #region 常量

        public static DVector3 zero { get; } = new DVector3(0.0, 0.0, 0.0);

        public static DVector3 one { get; } = new DVector3(1.0, 1.0, 1.0);

        public static DVector3 positiveinfinity { get; } = new DVector3(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);

        public static DVector3 negativeinfinity { get; } = new DVector3(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);

        public static DVector3 maxvalue { get; } = new DVector3(double.MaxValue, double.MaxValue, double.MaxValue);

        public static DVector3 minvalue { get; } = new DVector3(double.MinValue, double.MinValue, double.MinValue);

        public static DVector3 nan { get; } = new DVector3(double.NaN, double.NaN, double.NaN);

        // ── 世界系方向常量（游戏/Unity 约定，极轴 = +Y；§13 极轴统一 2026-09-28） ──────────
        // ⚠ 与下面那组 NED **船体姿态**常量是两个概念，别混：这里 `up=(0,1,0)` 是"世界的上"，
        //   那里 `bodyUp=(0,0,-1)` 是"船体的上（NED 的 −z）"。批5 U1' 之前它们同名（撞名），
        //   现在名字分开了，各自的值一个都没动。
        public static DVector3 up { get; } = new DVector3(0, 1, 0);
        public static DVector3 down { get; } = new DVector3(0, -1, 0);
        public static DVector3 right { get; } = new DVector3(1, 0, 0);
        public static DVector3 left { get; } = new DVector3(-1, 0, 0);
        public static DVector3 forward { get; } = new DVector3(0, 0, 1);
        public static DVector3 back { get; } = new DVector3(0, 0, -1);

        // ── NED 船体姿态常量（上游 V3 的原始约定；批5 U1' 由 up/down/left/right/forward/back 改名） ──
        public static DVector3 bodyDown { get; } = new DVector3(0.0, 0.0, 1.0);

        public static DVector3 bodyUp { get; } = new DVector3(0.0, 0.0, -1.0);

        public static DVector3 bodyLeft { get; } = new DVector3(0.0, -1.0, 0.0);

        public static DVector3 bodyRight { get; } = new DVector3(0.0, 1.0, 0.0);

        public static DVector3 bodyForward { get; } = new DVector3(1.0, 0.0, 0.0);

        public static DVector3 bodyBack { get; } = new DVector3(-1.0, 0.0, 0.0);

        /// <summary>世界极轴（§13 极轴统一 2026-09-28）：**+Y**（原上游 +z）。
        /// 理由：RimWorld 的 tile/日光/PlanetLayer 全是 +Y ⇒ 统一到 +Y 后边界**零转换**，
        /// 且 `OrbitView.GameWorldToAdapter` 变成恒等（41 处散落转换整体消失）。
        /// ⚠ **不许碰** `bodyUp/bodyDown/bodyLeft/bodyRight/bodyForward/bodyBack` 那 6 个——它们是 **NED 船体姿态**约定，
        ///   与"世界极轴"是两回事。它们原先叫 `up/down/…`，与宿主世界系常量撞名，
        ///   批5 U1' 已改名（值未动）；**今天它们与 `northpole` 不同值**（(0,0,-1) vs (0,1,0)）⇒ 任何"合并"念头都是错的。</summary>
        public static DVector3 northpole { get; } = new DVector3(0, 1, 0);

        // X,Y,Z axis
        public static DVector3 xaxis { get; } = new DVector3(1, 0, 0);

        public static DVector3 yaxis { get; } = new DVector3(0, 1, 0);

        public static DVector3 zaxis { get; } = new DVector3(0, 0, 1);

        #endregion

        #region 属性

        public double magnitude
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Math.Sqrt(x * x + y * y + z * z);
        }

        public double sqrMagnitude
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => x * x + y * y + z * z;
        }

        /// <summary>单位化。**上游语义**（批5 U2 采用）：`norm > 0` 才除，否则 `zero` ⇒ **NaN → zero**。</summary>
        public DVector3 normalized
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Normalize(this);
        }

        /// <summary>先按最大分量缩放再单位化（抗上溢/下溢）。零向量 ⇒ `zero`。</summary>
        public DVector3 safeNormalized
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => SafeNormalize(this);
        }

        /// <summary>与自身垂直的任一单位向量（选取最小分量轴做叉积，数值上最稳的那个方向）。</summary>
        public DVector3 orthonormal
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                DVector3 vunit = zero;
                vunit[min_magnitude_index] = 1.0;
                return Cross(this, vunit).normalized;
            }
        }

        public double max_magnitude => Math.Max(Math.Max(Math.Abs(x), Math.Abs(y)), Math.Abs(z));
        public double min_magnitude => Math.Min(Math.Min(Math.Abs(x), Math.Abs(y)), Math.Abs(z));

        public int max_magnitude_index
        {
            get
            {
                int largest_idx = Math.Abs(y) > Math.Abs(x) ? 1 : 0;
                return Math.Abs(z) > Math.Abs(this[largest_idx]) ? 2 : largest_idx;
            }
        }

        public int min_magnitude_index
        {
            get
            {
                int lowest_idx = Math.Abs(y) < Math.Abs(x) ? 1 : 0;
                return Math.Abs(z) < Math.Abs(this[lowest_idx]) ? 2 : lowest_idx;
            }
        }

        /// <summary>球坐标 (r, theta, phi) ⇒ 直角坐标（上游语义；零生产调用者，见批4 P3/B4 登记）。</summary>
        public DVector3 sph2cart => x * new DVector3(Math.Cos(z) * Math.Sin(y), Math.Sin(z) * Math.Sin(y), Math.Cos(y));

        /// <summary>直角坐标 ⇒ 球坐标 (r, theta, phi)（上游语义；零生产调用者，见批4 P3/B4 登记）。</summary>
        public DVector3 cart2sph
        {
            get
            {
                double r = magnitude;
                if (r == 0)
                    return zero;

                double rho = new DVector3(x, y, 0).magnitude;
                double theta = Math.Atan2(rho, z);
                double phi = Statics.Clamp2Pi(Math.Atan2(y, x));

                return new DVector3(r, theta, phi);
            }
        }

        /// <summary>交换 y/z（上游语义）。</summary>
        public DVector3 xzy => new DVector3(this[0], this[2], this[1]);

        #endregion

        #region 运算符

        public static DVector3 operator +(DVector3 a, DVector3 b) => new DVector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static DVector3 operator -(DVector3 a, DVector3 b) => new DVector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static DVector3 operator -(DVector3 a) => new DVector3(-a.x, -a.y, -a.z);

        public static DVector3 operator *(DVector3 a, double d) => new DVector3(a.x * d, a.y * d, a.z * d);
        public static DVector3 operator *(double d, DVector3 a) => new DVector3(a.x * d, a.y * d, a.z * d);
        public static DVector3 operator *(DVector3 a, float f) => new DVector3(a.x * f, a.y * f, a.z * f);
        public static DVector3 operator *(float f, DVector3 a) => new DVector3(a.x * f, a.y * f, a.z * f);

        /// <summary>**逐分量**乘（上游 V3 语义；不是点积、不是标量乘）。</summary>
        public static DVector3 operator *(DVector3 a, DVector3 b) => new DVector3(a.x * b.x, a.y * b.y, a.z * b.z);

        public static DVector3 operator /(DVector3 a, double d) => new DVector3(a.x / d, a.y / d, a.z / d);
        public static DVector3 operator /(DVector3 a, float f) => new DVector3(a.x / f, a.y / f, a.z / f);

        /// <summary>**逐分量**除（上游 V3 语义）。</summary>
        public static DVector3 operator /(DVector3 a, DVector3 b) => new DVector3(a.x / b.x, a.y / b.y, a.z / b.z);

        /// <summary>标量 ÷ 向量（逐分量；上游 V3 语义）。</summary>
        public static DVector3 operator /(double d, DVector3 a) => new DVector3(d / a.x, d / a.y, d / a.z);

        public static bool operator ==(DVector3 a, DVector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        public static bool operator !=(DVector3 a, DVector3 b) => !(a == b);

        // 与 Unity Vector3 互转
#if !PURE_MATH
        public static explicit operator Vector3(DVector3 v) => new Vector3((float)v.x, (float)v.y, (float)v.z);
        public static explicit operator DVector3(Vector3 v) => new DVector3(v.x, v.y, v.z);
#endif

        #endregion

        #region 静态方法

        public static double Dot(DVector3 a, DVector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

        public static DVector3 Cross(DVector3 a, DVector3 b) => new DVector3(
            a.y * b.z - a.z * b.y,
            a.z * b.x - a.x * b.z,
            a.x * b.y - a.y * b.x);

        public static DVector3 Scale(DVector3 a, DVector3 b) => new DVector3(a.x * b.x, a.y * b.y, a.z * b.z);

        public static DVector3 Divide(DVector3 a, DVector3 b) => new DVector3(a.x / b.x, a.y / b.y, a.z / b.z);

        public static DVector3 Abs(DVector3 a) => new DVector3(Math.Abs(a.x), Math.Abs(a.y), Math.Abs(a.z));

        public static DVector3 Sign(DVector3 a) => new DVector3(Math.Sign(a.x), Math.Sign(a.y), Math.Sign(a.z));

        public static DVector3 Sqrt(DVector3 a) => new DVector3(Math.Sqrt(a.x), Math.Sqrt(a.y), Math.Sqrt(a.z));

        public static DVector3 Max(DVector3 a, DVector3 b) => new DVector3(
            Math.Max(a.x, b.x), Math.Max(a.y, b.y), Math.Max(a.z, b.z));

        public static DVector3 Min(DVector3 a, DVector3 b) => new DVector3(
            Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Min(a.z, b.z));

        public static double Distance(DVector3 a, DVector3 b) => (a - b).magnitude;
        public static double DistanceSquared(DVector3 a, DVector3 b) => (a - b).sqrMagnitude;

        public static DVector3 Lerp(DVector3 a, DVector3 b, double t) => a + t * (b - a);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Magnitude(DVector3 vector) => vector.magnitude;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double SqrMagnitude(DVector3 vector) => vector.sqrMagnitude;

        /// <summary>单位化（**上游语义**：`norm > 0` 才除，否则 `zero` ⇒ NaN → zero）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DVector3 Normalize(DVector3 v)
        {
            double norm = v.magnitude;
            return norm > 0 ? v / norm : zero;
        }

        /// <summary>先按最大分量缩放再单位化（抗上溢/下溢）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DVector3 SafeNormalize(DVector3 v)
        {
            double c = v.max_magnitude;
            return c > 0 ? (v / c).normalized : zero;
        }

        /// <summary>两个向量绕轴的有向夹角（上游语义）。⚠ **会就地单位化三个入参**（上游如此，照搬）。</summary>
        public static double SignedAngle(DVector3 from, DVector3 to, DVector3 axis)
        {
            from.Normalize();
            to.Normalize();
            axis.Normalize();

            DVector3 cross = Cross(from, to);
            double sign = Dot(cross, axis);
            double angle = Angle(from, to);

            return sign >= 0 ? angle : -angle;
        }

        /// <summary>
        /// 两向量夹角（上游语义：先按最大分量缩放到 O(1) 再算，避免上溢/下溢）。
        /// 用 `Atan2(叉积模, 点积)` 而不是 `Acos` ⇒ 小角与大角都稳定。
        /// </summary>
        public static double Angle(DVector3 from, DVector3 to)
        {
            double c = Math.Max(from.max_magnitude, to.max_magnitude);
            if (c <= 0) return 0;

            DVector3 from_scaled = from / c;
            DVector3 to_scaled = to / c;

            double denominator = from_scaled.magnitude * to_scaled.magnitude;
            if (denominator <= 0) return 0;

            DVector3 cross_scaled = Cross(from_scaled, to_scaled);

            double sinAngle = cross_scaled.magnitude / denominator;
            double cosAngle = Dot(from_scaled, to_scaled) / denominator;

            return Math.Atan2(sinAngle, cosAngle);
        }

        public static DVector3 ClampMagnitude(DVector3 vector, double maxLength)
        {
            double mag = vector.magnitude;
            if (mag > maxLength)
                return vector.normalized * maxLength;

            return vector;
        }

        /// <summary>把 `vector` 投影到 `onNormal` 方向上（上游语义：两个入参都做了缩放归一，抗上溢）。</summary>
        public static DVector3 Project(DVector3 vector, DVector3 onNormal)
        {
            double invC = 1.0 / Math.Max(vector.max_magnitude, onNormal.max_magnitude);
            if (double.IsPositiveInfinity(invC)) return zero;

            DVector3 vectorC = vector * invC;
            DVector3 onNormalC = onNormal * invC;

            double invSqrMag = 1.0 / Dot(onNormalC, onNormalC);
            if (double.IsPositiveInfinity(invSqrMag)) return zero;

            double dot = Dot(vectorC, onNormalC);
            return onNormal * dot * invSqrMag;
        }

        /// <summary>把 `vector` 投影到以 `planeNormal` 为法向的平面上（上游语义）。</summary>
        public static DVector3 ProjectOnPlane(DVector3 vector, DVector3 planeNormal)
        {
            if (vector == zero)
                return zero;

            if (planeNormal == zero)
                return vector;

            double invC = 1.0 / Math.Max(vector.max_magnitude, planeNormal.max_magnitude);
            if (double.IsPositiveInfinity(invC)) return zero;

            DVector3 vectorC = vector * invC;
            DVector3 planeNormalC = planeNormal * invC;

            double invSqrMag = 1.0 / Dot(planeNormalC, planeNormalC);
            double dot = Dot(vectorC, planeNormalC);

            return new DVector3(vector.x - planeNormal.x * dot * invSqrMag,
                vector.y - planeNormal.y * dot * invSqrMag,
                vector.z - planeNormal.z * dot * invSqrMag);
        }

        /// <summary>方向球面插值（模长线性插值），参考轴取 NED 的 `bodyForward`（上游语义）。</summary>
        public static DVector3 Slerp(DVector3 a, DVector3 b, double t)
        {
            double magA = a.magnitude;
            double magB = b.magnitude;

            if (magA == 0 || magB == 0)
                return Lerp(a, b, t);

            var qa = Q3.FromToRotation(bodyForward, a.normalized);
            var qb = Q3.FromToRotation(bodyForward, b.normalized);
            var qSlerp = Q3.Slerp(qa, qb, t);

            DVector3 direction = qSlerp * bodyForward;

            double magnitude = magA + t * (magB - magA);

            return direction * magnitude;
        }

        /// <summary>外积（列矢构造矩阵；上游语义）。</summary>
        public static M3 Outer(DVector3 v1, DVector3 v2) => new M3(v1 * v2.x, v1 * v2.y, v1 * v2.z);

        /// <summary>就地正交化 `normal` 与 `tangent`（上游语义：三个断言只在 Debug 生效）。</summary>
        public static void OrthoNormalize(ref DVector3 normal, ref DVector3 tangent)
        {
            System.Diagnostics.Debug.Assert(normal.magnitude > 0);
            System.Diagnostics.Debug.Assert(tangent.magnitude > 0);
            System.Diagnostics.Debug.Assert(Cross(normal, tangent).magnitude > 0);

            normal.Normalize();
            tangent = Cross(Cross(normal, tangent).normalized, normal).normalized;
        }

        /// <summary>
        /// 绕任意轴旋转向量（四元数旋转）。
        /// </summary>
        public static DVector3 Rotate(DVector3 v, DVector3 axis, double angleRad)
        {
            double cos = Math.Cos(angleRad);
            double sin = Math.Sin(angleRad);
            return v * cos + Cross(axis, v) * sin + axis * Dot(axis, v) * (1 - cos);
        }

        /// <summary>
        /// 用四元数旋转向量。
        /// </summary>
        public static DVector3 Rotate(DVector3 v, DQuaternion q)
        {
            // q * v * q⁻¹
            DVector3 t = 2.0 * Cross(new DVector3(q.x, q.y, q.z), v);
            return v + q.w * t + Cross(new DVector3(q.x, q.y, q.z), t);
        }

        /// <summary>
        /// 从 Unity Quaternion 旋转。
        /// </summary>
#if !PURE_MATH
        public static DVector3 Rotate(DVector3 v, Quaternion q)
        {
            return Rotate(v, (DQuaternion)q);
        }
#endif

        #endregion

        #region 实例方法

        /// <summary>就地单位化。**上游语义**（批5 U2 采用）：`norm > 0` 才除、且**赋值 `this`** ⇒ **NaN → zero**。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Normalize()
        {
            double norm = Math.Sqrt(x * x + y * y + z * z);
            this = norm > 0 ? this / norm : zero;
        }

        /// <summary>就地"安全单位化"：先按最大分量缩放 ⇒ 抗上溢/下溢。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SafeNormalize()
        {
            double c = max_magnitude;
            this = c > 0 ? (this / c).normalized : zero;
        }

        /// <summary>逐分量缩放（就地；上游 V3 语义）。</summary>
        public void Scale(DVector3 scale)
        {
            x *= scale.x;
            y *= scale.y;
            z *= scale.z;
        }

        public DVector3 WithMagnitude(double newMag)
        {
            double m = magnitude;
            if (m < double.Epsilon) return zero;
            return this * (newMag / m);
        }

        /// <summary>三个分量都有限（非 NaN、非 ±∞）。</summary>
        public bool IsFinite() => Statics.IsFinite(x) && Statics.IsFinite(y) && Statics.IsFinite(z);

        public void CopyFrom(IList<double> other, int index = 0)
        {
            this[0] = other[index];
            this[1] = other[index + 1];
            this[2] = other[index + 2];
        }

        public void CopyTo(IList<double> other, int index = 0)
        {
            other[index] = this[0];
            other[index + 1] = this[1];
            other[index + 2] = this[2];
        }

        public void CopyTo(double[,] other, int i, int j)
        {
            other[i, j] = this[0];
            other[i + 1, j] = this[1];
            other[i + 2, j] = this[2];
        }

        public static DVector3 CopyFromIndices(IList<double> array, (int, int, int) indices) => new DVector3(array[indices.Item1], array[indices.Item2], array[indices.Item3]);

        public void CopyToIndices(IList<double> array, (int, int, int) indices)
        {
            array[indices.Item1] = this[0];
            array[indices.Item2] = this[1];
            array[indices.Item3] = this[2];
        }

        #endregion

        #region IEquatable / 覆盖

        /// <summary>**上游语义**（批5 U2 采用）：按位比较分量 ⇒ **NaN 判相等**（与 `operator ==` 不同！）。
        /// 理由：`EqualityHashingTests` 断言 `NaN.Equals(NaN)`，而字典/哈希容器要的就是这个语义。</summary>
        public bool Equals(DVector3 other) => x.Equals(other.x) && y.Equals(other.y) && z.Equals(other.z);

        public override bool Equals(object? obj) => obj is DVector3 other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = x.GetHashCode();
                hashCode = (hashCode * 397) ^ y.GetHashCode();
                hashCode = (hashCode * 397) ^ z.GetHashCode();
                return hashCode;
            }
        }

        /// <summary>**上游格式**（批5 U2 采用）：`[1.5, 2.5, 3.5]`、`G17`、不变文化（原为 `(1.500, 2.500, 3.500)` 当前文化）。</summary>
        public override string ToString() => ToString(null, CultureInfo.InvariantCulture.NumberFormat);

        public string ToString(string? format) => ToString(format, CultureInfo.InvariantCulture.NumberFormat);

        public string ToString(string? format, IFormatProvider formatProvider)
        {
            if (string.IsNullOrEmpty(format))
                format = "G17";
            return
                $"[{x.ToString(format, formatProvider)}, {y.ToString(format, formatProvider)}, {z.ToString(format, formatProvider)}]";
        }

        #endregion

#if !PURE_MATH
        #region 序列化辅助

        /// <summary>
        /// 从 Vector3 显式转换（保持 double 精度）。
        /// </summary>
        public static DVector3 FromVector3(Vector3 v) => new DVector3(v.x, v.y, v.z);

        /// <summary>
        /// 转 Unity Vector3（精度损失）。
        /// </summary>
        public Vector3 ToVector3() => new Vector3((float)x, (float)y, (float)z);

        /// <summary>
        /// 用 Scribe_Values 序列化 DVector3 为三个 double 分量。
        /// </summary>
        public static void Scribe(string label, ref DVector3 value)
        {
            double x = value.x, y = value.y, z = value.z;
            Scribe_Values.Look(ref x, label + "_x", 0.0);
            Scribe_Values.Look(ref y, label + "_y", 0.0);
            Scribe_Values.Look(ref z, label + "_z", 0.0);
            if (Verse.Scribe.mode == LoadSaveMode.LoadingVars)
                value = new DVector3(x, y, z);
        }

        /// <summary>
        /// 用 Scribe_Values 序列化 DVector3? 。
        /// </summary>
        public static void ScribeNullable(string label, ref DVector3? value)
        {
            bool hasValue = value.HasValue;
            Scribe_Values.Look(ref hasValue, label + "_exists", false);
            DVector3 inner = value ?? DVector3.zero;
            double x = inner.x, y = inner.y, z = inner.z;
            // 只在有值时读写分量，避免写出多余字段
            if (hasValue)
            {
                Scribe_Values.Look(ref x, label + "_x", 0.0);
                Scribe_Values.Look(ref y, label + "_y", 0.0);
                Scribe_Values.Look(ref z, label + "_z", 0.0);
            }
            if (Verse.Scribe.mode == LoadSaveMode.LoadingVars)
                value = hasValue ? new DVector3(x, y, z) : (DVector3?)null;
        }

        #endregion
#endif
    }

    /// <summary>
    /// 双精度四元数。用于轨道旋转计算。
    /// </summary>
    public struct DQuaternion : IEquatable<DQuaternion>
    {
        public double x;
        public double y;
        public double z;
        public double w;

        public DQuaternion(double x, double y, double z, double w)
        {
            this.x = x; this.y = y; this.z = z; this.w = w;
        }

        public static readonly DQuaternion identity = new DQuaternion(0, 0, 0, 1);

#if !PURE_MATH
        public static explicit operator Quaternion(DQuaternion q) => new Quaternion((float)q.x, (float)q.y, (float)q.z, (float)q.w);
        public static explicit operator DQuaternion(Quaternion q) => new DQuaternion(q.x, q.y, q.z, q.w);
#endif

        public bool Equals(DQuaternion other) => x == other.x && y == other.y && z == other.z && w == other.w;
        public override bool Equals(object obj) => obj is DQuaternion other && Equals(other);
        public override int GetHashCode()
        {
            int hash = 17;
            hash = hash * 31 + x.GetHashCode();
            hash = hash * 31 + y.GetHashCode();
            hash = hash * 31 + z.GetHashCode();
            hash = hash * 31 + w.GetHashCode();
            return hash;
        }
        public override string ToString() => string.Format("({0:F4}, {1:F4}, {2:F4}, {3:F4})", x, y, z, w);

        public static DQuaternion operator *(DQuaternion a, DQuaternion b) => new DQuaternion(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

        public static DQuaternion Inverse(DQuaternion q) => new DQuaternion(-q.x, -q.y, -q.z, q.w);

        /// <summary>
        /// 从角度/轴构造。
        /// </summary>
        public static DQuaternion AngleAxis(double angleRad, DVector3 axis)
        {
            double half = angleRad * 0.5;
            double s = Math.Sin(half);
            DVector3 n = axis.normalized;
            return new DQuaternion(n.x * s, n.y * s, n.z * s, Math.Cos(half));
        }

        /// <summary>
        /// 从 Unity Quaternion 显式转换。
        /// </summary>
#if !PURE_MATH
        public static DQuaternion FromQuaternion(Quaternion q) => new DQuaternion(q.x, q.y, q.z, q.w);
        public Quaternion ToQuaternion() => new Quaternion((float)x, (float)y, (float)z, (float)w);
#endif
    }
}
