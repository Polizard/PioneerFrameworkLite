using System;
using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>父链上的一帧：天体 id / 父 id / 相对父的位置与速度（tick 时刻）。</summary>
    public readonly struct FrameNode
    {
        public FrameNode(int bodyId, int parentId, DVector3 position, DVector3 velocity)
        {
            BodyId = bodyId;
            ParentId = parentId;
            Position = position;
            Velocity = velocity;
        }

        public int BodyId { get; }
        public int ParentId { get; }
        public DVector3 Position { get; }
        public DVector3 Velocity { get; }
    }

    /// <summary>
    /// 换系纯核（2026-11-04，Batch B）。
    ///
    /// 只做两件事：
    ///   1) `Offset(Path(from), Path(to))`：沿父链找 LCA，把 from 系原点到 LCA 的位置/速度
    ///      与 to 系原点到 LCA 的位置/速度相减——**不经过 `GetBodyPositionAbs`**（绝对帧只许表现层用）；
    ///   2) `Transform`：把状态从 from 系搬到 to 系：p' = p + fromOffset − toOffset；
    ///      v' = v + fromVel − toVel。
    ///
    /// 路径约定：Path[0] = 该天体本身，向父链一直到根（ParentId &lt; 0）；两路径必须有共同根。
    /// 本文件纯算术（只 System），由 Pure.Tests 链入；`CelestialBodyManager.GetFrameOffset` 只是薄适配。
    /// </summary>
    public static class FrameTransitionCore
    {
        /// <summary>from 系原点相对 to 系原点的偏移（位置、速度）。</summary>
        public static (DVector3 Position, DVector3 Velocity) Offset(
            IReadOnlyList<FrameNode> fromPath,
            IReadOnlyList<FrameNode> toPath)
        {
            if (fromPath == null || toPath == null || fromPath.Count == 0 || toPath.Count == 0)
                throw new ArgumentException("frame paths must be non-empty");

            int lca = FindLca(fromPath, toPath);
            if (lca < 0) throw new InvalidOperationException("frame paths share no common root");

            DVector3 fromPosition = DVector3.zero, fromVelocity = DVector3.zero;
            for (int i = 0; i < fromPath.Count; i++)
            {
                if (fromPath[i].BodyId == lca) break;
                fromPosition += fromPath[i].Position;
                fromVelocity += fromPath[i].Velocity;
            }

            DVector3 toPosition = DVector3.zero, toVelocity = DVector3.zero;
            for (int i = 0; i < toPath.Count; i++)
            {
                if (toPath[i].BodyId == lca) break;
                toPosition += toPath[i].Position;
                toVelocity += toPath[i].Velocity;
            }

            return (fromPosition - toPosition, fromVelocity - toVelocity);
        }

        /// <summary>把 from 系状态搬到 to 系。offset = (from 原点在根系的位置 − to 原点在根系的位置)。</summary>
        public static void Transform(
            DVector3 position,
            DVector3 velocity,
            DVector3 fromOffsetPosition,
            DVector3 fromOffsetVelocity,
            DVector3 toOffsetPosition,
            DVector3 toOffsetVelocity,
            out DVector3 targetPosition,
            out DVector3 targetVelocity)
        {
            targetPosition = position + fromOffsetPosition - toOffsetPosition;
            targetVelocity = velocity + fromOffsetVelocity - toOffsetVelocity;
        }

        public static int FindLca(IReadOnlyList<FrameNode> fromPath, IReadOnlyList<FrameNode> toPath)
        {
            for (int i = 0; i < fromPath.Count; i++)
            {
                int id = fromPath[i].BodyId;
                for (int j = 0; j < toPath.Count; j++)
                    if (toPath[j].BodyId == id) return id;
            }
            return -1;
        }
    }
}
