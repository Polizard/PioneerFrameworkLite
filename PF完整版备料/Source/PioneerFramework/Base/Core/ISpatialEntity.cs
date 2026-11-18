using UnityEngine;

namespace PioneerFramework
{
    /// <summary>
    /// 需要被空间索引追踪的对象必须实现此接口。
    /// 空间群组 (SpaceEntityGroup) 与射弹 (ProjectileInstance) 均实现此接口，
    /// 共享同一套 SpatialGrid3D（粗网格）和 CombatGrid（细网格）。
    /// </summary>
    public interface ISpatialEntity
    {
        int EntityId { get; }

        int GroupId { get; }

        int FactionId { get; }

        /// <summary>
        /// 对象当前世界坐标（只读/可写，由物理系统维护）。
        /// </summary>
        DVector3 Position { get; set; }

        /// <summary>
        /// 对象上次所在的区块坐标（由空间索引系统维护）。
        /// </summary>
        (int cx, int cy, int cz) LastCellCoord { get; set; }

        /// <summary>
        /// 标记对象的空间索引是否需要更新（由空间索引系统维护）。
        /// </summary>
        bool IsSpatialDirty { get; set; }
    }
}
