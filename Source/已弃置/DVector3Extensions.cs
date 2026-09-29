using System;

namespace PioneerFramework
{
    public static class DVector3Extensions
    {
        /// <summary>
        /// 安全的 normalized：零向量时返回 (1,0,0) 而非零向量。
        /// </summary>
        public static DVector3 SafeNormalized(this DVector3 v)
        {
            double m = v.magnitude;
            if (m < 1e-12)
                return DVector3.right;
            return v / m;
        }

        /// <summary>
        /// 索引访问器（与 MechJebLib V3 兼容）。
        /// </summary>
        public static double Component(this DVector3 v, int index)
        {
            switch (index)
            {
                case 0: return v.x;
                case 1: return v.y;
                case 2: return v.z;
                default: throw new IndexOutOfRangeException($"Bad index {index} for DVector3");
            }
        }

        /// <summary>
        /// 北天极方向（与 MechJebLib V3.northpole 兼容）。
        /// </summary>
        public static readonly DVector3 northpole = DVector3.up;

        /// <summary>
        /// Z 轴（与 MechJebLib V3.zaxis 兼容）。
        /// </summary>
        public static readonly DVector3 zaxis = DVector3.forward;
        public static readonly DVector3 xaxis = DVector3.right;
    }
}
