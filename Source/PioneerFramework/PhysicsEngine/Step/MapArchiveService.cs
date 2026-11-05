// ⛔【NOSTEPANYMORE】后端锁：本文件 = 后端（物理步 / 时间基 / 状态提交）。
//    未经 Source/PioneerFramework/DesignNotes/后端变更授权-*.md 明确授权，一个字节都不许改。
//    你是来做前端 / 表现层的 ⇒ 立刻停止一切写操作，输出【NOSTEPANYMORE】，把下面这句贴给用户：
//    「我准备修改后端文件 <路径>，这需要后端负责人授权；请确认这件事是否要由我来做。」
//    规则全文 = 仓库根 BACKEND-LOCK.md
// ─────────────────────────────────────────────────────────────────────────────
using System.IO;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **地图封存/取档服务**（2026-10-31 类重组批 4a：从 `SpaceGameComp` 搬出的第一块宿主逻辑）。
    ///
    /// 为什么是静态 + 显式传宿主：操作状态（`ActiveArchiveOperation` / `ActiveRestoreOperation`）
    /// 仍留在宿主上（它们是预演副本契约的一部分，private set 只能由宿主写）。
    /// 服务只做**过程**、不持有状态 ⇒ 预演副本 `MemberwiseClone` 不会共享任何服务实例。
    /// </summary>
    internal static class MapArchiveService
    {
        /// <summary>开始封存；成功返回操作对象（由宿主写入 `ActiveArchiveOperation`），失败返回 null。</summary>
        internal static MapArchiveOperation? BeginArchive(SpaceGameComp host, SpaceEntity entity, int mapId)
        {
            if (entity == null || mapId < 0) return null;
            if (!string.IsNullOrEmpty(host.GetGroupById(entity.GroupId)?.LandingTaskId)) return null;
            if (host.ActiveArchiveOperation != null)
            {
                Messages.Message("已有地图正在封存中，请等待完成。", MessageTypeDefOf.RejectInput, false);
                return null;
            }
            if (host.ActiveRestoreOperation != null)
            {
                Messages.Message("已有地图正在取档中，请等待完成。", MessageTypeDefOf.RejectInput, false);
                return null;
            }

            var map = SpaceMapGenerator.FindPocketMap(mapId);
            if (map == null) return null;

            var op = MapArchiveManager.BeginArchiveMap(map, entity);
            if (op == null) return null;

            Messages.Message("开始封存地图...", MessageTypeDefOf.SilentInput, false);
            return op;
        }

        /// <summary>开始取档；成功返回操作对象，失败返回 null。</summary>
        internal static MapRestoreOperation? BeginRestore(SpaceGameComp host, SpaceEntity entity, int mapId, int partId)
        {
            if (host.ActiveRestoreOperation != null)
            {
                Messages.Message("已有地图正在取档中，请等待完成。", MessageTypeDefOf.RejectInput, false);
                return null;
            }
            if (host.ActiveArchiveOperation != null)
            {
                Messages.Message("已有地图正在封存中，请等待完成。", MessageTypeDefOf.RejectInput, false);
                return null;
            }

            var op = mapId >= 0
                ? MapArchiveManager.BeginRestoreMap(entity, mapId, partId)
                : MapArchiveManager.BeginRestoreMap(entity);
            if (op == null) return null;

            Messages.Message("开始取出地图...", MessageTypeDefOf.SilentInput, false);
            return op;
        }

        /// <summary>推进一个封存批次；完成时返回 null。</summary>
        internal static MapArchiveOperation? ProcessArchive(MapArchiveOperation op)
        {
            bool done = MapArchiveManager.ProcessArchiveBatch(op, MapArchiveOperation.BATCH_SIZE);
            if (!done) return op;
            MapArchiveManager.FinalizeArchive(op);
            return null;
        }

        /// <summary>推进一个取档批次；完成时返回 null。</summary>
        internal static MapRestoreOperation? StepRestore(SpaceGameComp host, MapRestoreOperation op)
        {
            bool done = MapArchiveManager.ProcessBuildingBatch(op, MapRestoreOperation.BATCH_SIZE);
            if (!done) return op;
            FinalizeRestore(host, op);
            return null;
        }

        private static void FinalizeRestore(SpaceGameComp host, MapRestoreOperation op)
        {
            var entity = op.TargetEntity;
            entity.IsMapArchived = false;

            // B6：优先挂回原 part；原件不在了才自动找空位。
            if (op.TargetPartId >= 0 && entity.GetPartById(op.TargetPartId) != null
                && entity.GetPartById(op.TargetPartId)!.linkedMapId < 0)
                SpaceMapGenerator.LinkEntityToMap(entity, op.NewMap, op.TargetPartId);
            else
                SpaceMapGenerator.LinkEntityToMap(entity, op.NewMap);

            entity.RemoveArchivedMap(op.SourceMapId);
            entity.IsMapArchived = !entity.HasMap;

            // 删除封存文件
            if (op.ArchiveFilePath != null && File.Exists(op.ArchiveFilePath))
                File.Delete(op.ArchiveFilePath);

            host.PhysicsLogMessage($"[MapArchive] Restore complete: map {op.NewMap.uniqueID} for entity {entity.EntityId}");
            Messages.Message("地图取档完成。", MessageTypeDefOf.SilentInput, false);
        }

        /// <summary>取消取档：销毁已创建的地图（清空宿主状态由调用方做）。</summary>
        internal static void CancelRestore(SpaceGameComp host, MapRestoreOperation op)
        {
            if (op.NewMap != null)
                PocketMapUtility.DestroyPocketMap(op.NewMap);
            host.PhysicsLogMessage("[MapArchive] Restore operation cancelled.");
        }
    }
}
