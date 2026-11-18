using System;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 双精度三维向量。用于所有物理/轨道计算以消除 float 精度问题。
    /// 与 Unity Vector3 可隐式双转换（double→float 有精度损失，显式标注）。
    /// </summary>
    public struct DVector3 : IEquatable<DVector3>
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

        #endregion

        #region 常量

        public static readonly DVector3 zero = new DVector3(0, 0, 0);
        public static readonly DVector3 one = new DVector3(1, 1, 1);
        public static readonly DVector3 up = new DVector3(0, 1, 0);
        public static readonly DVector3 down = new DVector3(0, -1, 0);
        public static readonly DVector3 right = new DVector3(1, 0, 0);
        public static readonly DVector3 left = new DVector3(-1, 0, 0);
        public static readonly DVector3 forward = new DVector3(0, 0, 1);
        public static readonly DVector3 back = new DVector3(0, 0, -1);

        #endregion

        #region 属性

        public double magnitude => Math.Sqrt(x * x + y * y + z * z);
        public double sqrMagnitude => x * x + y * y + z * z;

        public DVector3 normalized
        {
            get
            {
                double m = magnitude;
                if (m < double.Epsilon) return zero;
                return new DVector3(x / m, y / m, z / m);
            }
        }

        #endregion

        #region 运算符

        public static DVector3 operator +(DVector3 a, DVector3 b) => new DVector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static DVector3 operator -(DVector3 a, DVector3 b) => new DVector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static DVector3 operator -(DVector3 a) => new DVector3(-a.x, -a.y, -a.z);
        public static DVector3 operator *(DVector3 a, double d) => new DVector3(a.x * d, a.y * d, a.z * d);
        public static DVector3 operator *(double d, DVector3 a) => new DVector3(a.x * d, a.y * d, a.z * d);
        public static DVector3 operator *(DVector3 a, float f) => new DVector3(a.x * f, a.y * f, a.z * f);
        public static DVector3 operator *(float f, DVector3 a) => new DVector3(a.x * f, a.y * f, a.z * f);
        public static DVector3 operator /(DVector3 a, double d) => new DVector3(a.x / d, a.y / d, a.z / d);
        public static DVector3 operator /(DVector3 a, float f) => new DVector3(a.x / f, a.y / f, a.z / f);

        public static bool operator ==(DVector3 a, DVector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        public static bool operator !=(DVector3 a, DVector3 b) => !(a == b);

        // 与 Unity Vector3 互转
        public static implicit operator Vector3(DVector3 v) => new Vector3((float)v.x, (float)v.y, (float)v.z);
        public static explicit operator DVector3(Vector3 v) => new DVector3(v.x, v.y, v.z);

        #endregion

        #region 静态方法

        public static double Dot(DVector3 a, DVector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

        public static DVector3 Cross(DVector3 a, DVector3 b) => new DVector3(
            a.y * b.z - a.z * b.y,
            a.z * b.x - a.x * b.z,
            a.x * b.y - a.y * b.x);

        public static double Distance(DVector3 a, DVector3 b) => (a - b).magnitude;
        public static double DistanceSquared(DVector3 a, DVector3 b) => (a - b).sqrMagnitude;

        public static DVector3 Lerp(DVector3 a, DVector3 b, double t) => new DVector3(
            a.x + (b.x - a.x) * t,
            a.y + (b.y - a.y) * t,
            a.z + (b.z - a.z) * t);

        public static DVector3 Max(DVector3 a, DVector3 b) => new DVector3(
            Math.Max(a.x, b.x), Math.Max(a.y, b.y), Math.Max(a.z, b.z));

        public static DVector3 Min(DVector3 a, DVector3 b) => new DVector3(
            Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Min(a.z, b.z));

        public static DVector3 Scale(DVector3 a, DVector3 b) => new DVector3(a.x * b.x, a.y * b.y, a.z * b.z);

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
        public static DVector3 Rotate(DVector3 v, Quaternion q)
        {
            return Rotate(v, (DQuaternion)q);
        }

        #endregion

        #region 实例方法

        public void Normalize()
        {
            double m = magnitude;
            if (m > double.Epsilon)
            {
                x /= m;
                y /= m;
                z /= m;
            }
        }

        public DVector3 WithMagnitude(double newMag)
        {
            double m = magnitude;
            if (m < double.Epsilon) return zero;
            return this * (newMag / m);
        }

        #endregion

        #region IEquatable / 覆盖

        public bool Equals(DVector3 other) => x == other.x && y == other.y && z == other.z;
        public override bool Equals(object obj) => obj is DVector3 other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + x.GetHashCode();
                hash = hash * 31 + y.GetHashCode();
                hash = hash * 31 + z.GetHashCode();
                return hash;
            }
        }
        public override string ToString() => string.Format("({0:F3}, {1:F3}, {2:F3})", x, y, z);

        #endregion

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

        public static implicit operator Quaternion(DQuaternion q) => new Quaternion((float)q.x, (float)q.y, (float)q.z, (float)q.w);
        public static explicit operator DQuaternion(Quaternion q) => new DQuaternion(q.x, q.y, q.z, q.w);

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
        public static DQuaternion FromQuaternion(Quaternion q) => new DQuaternion(q.x, q.y, q.z, q.w);
        public Quaternion ToQuaternion() => new Quaternion((float)x, (float)y, (float)z, (float)w);
    }
}
