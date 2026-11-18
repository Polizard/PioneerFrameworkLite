using System;

namespace PioneerFramework.Combat
{
    /// <summary>
    /// Morton 码的三维编码/解码（64 位 Z-order curve）。
    /// 每维最多 21 位，覆盖 0 ~ 2,097,151 个格子。
    /// 高位 = 粗粒度空间划分，低位 = 细粒度空间划分。
    /// </summary>
    internal static class Morton3D
    {
        private const int BITS_PER_DIM = 21;
        private const int MAX_DIM = 1 << BITS_PER_DIM;

        /// <summary>
        /// 将三维非负整数坐标编码为 64 位 Morton 码。
        /// </summary>
        public static ulong Encode(int x, int y, int z)
        {
            if (x < 0 || x >= MAX_DIM || y < 0 || y >= MAX_DIM || z < 0 || z >= MAX_DIM)
                return 0;
            return Spread((ulong)x) | (Spread((ulong)y) << 1) | (Spread((ulong)z) << 2);
        }

        /// <summary>
        /// 将 Morton 码解码回三维整数坐标。
        /// </summary>
        public static (int x, int y, int z) Decode(ulong code)
        {
            return ((int)Compact(code), (int)Compact(code >> 1), (int)Compact(code >> 2));
        }

        private static ulong Spread(ulong v)
        {
            v = (v | (v << 32)) & 0xFFFF00000000FFFFUL;
            v = (v | (v << 16)) & 0x00FF0000FF0000FFUL;
            v = (v | (v << 8))  & 0xF00F00F00F00F00FUL;
            v = (v | (v << 4))  & 0x30C30C30C30C30C3UL;
            v = (v | (v << 2))  & 0x1249249249249249UL;
            return v;
        }

        private static ulong Compact(ulong v)
        {
            v = v & 0x1249249249249249UL;
            v = (v | (v >> 2))  & 0x30C30C30C30C30C3UL;
            v = (v | (v >> 4))  & 0xF00F00F00F00F00FUL;
            v = (v | (v >> 8))  & 0x00FF0000FF0000FFUL;
            v = (v | (v >> 16)) & 0xFFFF00000000FFFFUL;
            v = (v | (v >> 32)) & 0xFFFFFFFFFFFFFFFFUL;
            return v;
        }
    }

    /// <summary>
    /// Morton 索引条目。按 code 排序后存入数组，隐含空间层级关系。
    /// </summary>
    public struct MortonEntry : IComparable<MortonEntry>
    {
        public ulong code;
        public int partIndex;  // 指向 SpaceEntity.parts 列表的索引

        public int CompareTo(MortonEntry other)
        {
            return code.CompareTo(other.code);
        }
    }
}
