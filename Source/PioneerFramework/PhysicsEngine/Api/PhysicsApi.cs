// ⛔【NOSTEPANYMORE】后端锁：本文件 = 后端（物理步 / 时间基 / 状态提交）。
//    未经 Source/PioneerFramework/DesignNotes/后端变更授权-*.md 明确授权，一个字节都不许改。
//    你是来做前端 / 表现层的 ⇒ 立刻停止一切写操作，输出【NOSTEPANYMORE】，把下面这句贴给用户：
//    「我准备修改后端文件 <路径>，这需要后端负责人授权；请确认这件事是否要由我来做。」
//    规则全文 = 仓库根 BACKEND-LOCK.md
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **物理引擎的只读门面**（2026-10-31，类重组批 2）。
    ///
    /// 壳外代码（表现层 / 玩法层 / 调试探针）只通过本类读物理状态：
    /// 返回的都是 <see cref="SpacecraftSnapshot" /> / <see cref="PartSnapshot" /> 值拷贝，
    /// 不暴露 `SpaceEntityGroup` / `SpaceEntity` / `SpacePart` / `SpaceGameComp`。
    ///
    /// 设计约束：
    /// - **只读**：本类不改物理状态；要改请走 <see cref="PhysicsCommands" />。
    /// - **无缓存**：每次调用现取；表现层要每帧一致就每帧取一次（快照是值，不会半路变）。
    /// - **无分配**：批量路径都写调用方给的 buffer，内部不 new List。
    /// </summary>
    public static class PhysicsApi
    {
        /// <summary>按组 id 取一艘船的当前快照。组不存在 / 不在游戏里 ⇒ false（snapshot 为 default）。</summary>
        public static bool TryGetSpacecraft(int groupId, out SpacecraftSnapshot snapshot)
        {
            snapshot = default;
            if (!TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;
            Build(group, out snapshot);
            return true;
        }

        /// <summary>把全部在册组的快照写进 buffer（先 Clear）。返回写入条数。</summary>
        public static int CopySpacecrafts(List<SpacecraftSnapshot> buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            buffer.Clear();
            if (!TryGetHost(out var host)) return 0;
            foreach (var group in host.GetAllGroups())
            {
                if (group == null) continue;
                Build(group, out var snapshot);
                buffer.Add(snapshot);
            }
            return buffer.Count;
        }

        /// <summary>把某组的全部部件快照写进 buffer（先 Clear）。组不存在 ⇒ false。</summary>
        public static bool TryCopyParts(int groupId, List<PartSnapshot> buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            buffer.Clear();
            if (!TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;

            foreach (int entityId in group.GetEntityIds())
            {
                var entity = host.GetEntityById(entityId);
                if (entity == null) continue;
                AppendParts(entity, buffer);
            }
            return true;
        }

        /// <summary>把某个实体的部件快照写进 buffer（先 Clear）。实体不存在 ⇒ false。</summary>
        public static bool TryCopyEntityParts(int entityId, List<PartSnapshot> buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            buffer.Clear();
            if (!TryGetHost(out var host)) return false;
            var entity = host.GetEntityById(entityId);
            if (entity == null) return false;
            AppendParts(entity, buffer);
            return true;
        }

        /// <summary>把某组的实体快照写进 buffer（先 Clear）。组不存在 ⇒ false。</summary>
        public static bool CopyEntities(int groupId, List<EntitySnapshot> buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            buffer.Clear();
            if (!TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;

            foreach (int entityId in group.GetEntityIds())
            {
                var entity = host.GetEntityById(entityId);
                if (entity == null || entity.GroupId != groupId) continue;
                BuildEntity(entity, out var snapshot);
                buffer.Add(snapshot);
            }
            return true;
        }

        /// <summary>按实体 id 取一份快照。实体不存在 ⇒ false。</summary>
        public static bool TryGetEntity(int entityId, out EntitySnapshot snapshot)
        {
            snapshot = default;
            if (!TryGetHost(out var host)) return false;
            var entity = host.GetEntityById(entityId);
            if (entity == null) return false;
            BuildEntity(entity, out snapshot);
            return true;
        }

        private static void AppendParts(SpaceEntity entity, List<PartSnapshot> buffer)
        {
            var parts = entity.Parts;
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part == null) continue;
                buffer.Add(new PartSnapshot
                {
                    PartId = part.partId,
                    EntityId = entity.EntityId,
                    MapId = entity.MapId,
                    StageId = part.stageId,
                    PositionShip = part.positionShip,
                    BoundsSizeShip = part.boundsSizeShip,
                    RotationShip = part.rotationShip,
                    Shape = part.shape,
                    Hp = part.hp,
                    IsDestroyed = part.isDestroyed,
                    IsMapArchived = entity.IsMapArchived,
                    IsEngine = part.GetComponent<Components.EngineComponentData>() != null,
                    IsPayloadBay = part.GetComponent<Components.PayloadBayComponentData>() != null,
                    HasControlCore = part.GetComponent<Components.ControlCoreComponentData>() != null,
                });
            }
        }

        private static void BuildEntity(SpaceEntity entity, out EntitySnapshot s)
        {
            int destroyed = 0;
            var parts = entity.Parts;
            for (int i = 0; i < parts.Count; i++)
                if (parts[i] != null && parts[i].isDestroyed) destroyed++;

            s = new EntitySnapshot
            {
                EntityId = entity.EntityId,
                GroupId = entity.GroupId,
                FactionId = entity.FactionId,
                MapId = entity.MapId,
                HasMap = entity.HasMap,
                IsMapArchived = entity.IsMapArchived,
                IsMovable = entity.IsMovable,
                PartSetVersion = entity.partSetVersion,
                Position = entity.Position,
                Rotation = entity.Rotation,
                AabbHalfSize = entity.AabbHalfSize,
                PartCount = parts.Count,
                DestroyedPartCount = destroyed,
            };
        }

        /// <summary>引擎内部用：壳外不许调（批 5 的架构闸门会盯住对本方法的引用）。</summary>
        internal static bool TryGetGroup(int groupId, out SpaceEntityGroup group)
        {
            group = null!;
            if (!TryGetHost(out var host)) return false;
            var found = host.GetGroupById(groupId);
            if (found == null) return false;
            group = found;
            return true;
        }

        internal static bool TryGetHost(out SpaceGameComp host)
        {
            host = null!;
            var game = Current.Game;
            if (game == null) return false;
            var component = game.GetComponent<SpaceGameComp>();
            if (component == null) return false;
            host = component;
            return true;
        }

        private static void Build(SpaceEntityGroup group, out SpacecraftSnapshot s)
        {
            s = default;

            s.GroupId = group.GroupId;
            s.FactionId = group.FactionId;
            s.CelestialBodyId = group.CelestialBodyId;

            s.EntityCount = group.GetEntityIds().Count;

            s.NextManeuverTick = double.PositiveInfinity;
            var maneuvers = group.plannedManeuvers;
            for (int i = 0; i < maneuvers.Count; i++)
            {
                var node = maneuvers[i];
                if (!node.active) continue;
                s.PlannedManeuverCount++;
                if (node.executeTick < s.NextManeuverTick) s.NextManeuverTick = node.executeTick;
            }

            s.RendezvousArrivalTick = group.HasRendezvousGuidance ? group.rendezvousArrivalTick : double.NaN;

            s.IsActive = group.IsActive;
            s.IsMovable = group.IsMovable;
            s.IsStealth = group.IsStealth;
            s.IsCrippled = group.IsCrippled;
            s.IsCircularOrbit = group.IsCircularOrbit;
            s.OrbitDirty = group.OrbitDirty;
            s.HasActiveBurn = group.HasActiveBurn;
            s.IsControllable = group.IsControllableNow();
            s.HasRendezvousGuidance = group.HasRendezvousGuidance;
            s.HasApproachGuidance = group.HasApproachGuidance;
            s.TransferActive = group.transferActive;
            s.ActivePingEnabled = group.ActivePingEnabled;

            var orbit = group.CurrentOrbit;
            s.PositionSystem = orbit.positionSystem.v;
            s.VelocitySystem = orbit.velocitySystem.v;
            s.Rotation = orbit.rotation;
            s.AngularVelocity = group.angularVelocity;

            s.CircularOrbitNormal = group.circularOrbitNormal;
            s.PendingImpulse = group.pendingImpulse;

            s.LandingTaskId = group.LandingTaskId;
            s.EnabledNetworkDefName = group.propulsionSelection.enabledNetworkDefName;
            s.AutoStageEnabled = group.autoStageEnabled;

            // 机动/交会窗口面（批 5e §4.3b）
            s.PlannedManeuverTotalCount = group.plannedManeuvers.Count;
            s.AppliedBurnDv = group.appliedBurnDV;
            s.RemainingBurnDv = group.remainingBurnDV;
            s.BurnConsumedPropellant = group.burnConsumedPropellant;
            s.RendezvousArmed = group.rendezvousArmed;
            s.RendezvousIsApproach = group.rendezvousIsApproach;
            s.RendezvousTargetId = group.rendezvousTargetId;
            s.ApproachTargetId = group.ApproachTargetId;
            s.RendezvousDistanceTolMeters = group.EffectiveRendezvousGoal.distanceTol;
            s.RendezvousMatchDistMeters = group.rendezvousMatchDist;
            s.CurrentNetworkDefName = group.currentNetwork?.defName;
            s.MaxAngularRateRad = group.MaxAngularRate;
            s.HoverActive = group.hoverActive;
            s.HoverAnalytic = group.hoverAnalytic;
            s.TransferStartTick = group.transferStartTick;
            s.TransferDuration = group.transferDuration;
            s.TransferDvTotal = group.transferDvTotal;
            s.TransferArrivalErrorMeters = group.transferArrivalErrorMeters;
            s.DebugInfinitePropellant = group.debugInfinitePropellant;

            s.RadarScanning = group.RadarAimMode == RadarAim.Scan;
            var radar = group.RadarCap;
            s.Radar = new RadarSnapshot
            {
                BeamRangeMeters = radar.beamRangeMeters,
                BeamHalfAngleDeg = radar.beamHalfAngleDeg,
                ScanRateDegPerSec = radar.scanRateDegPerSec,
                ActiveGainBonus = radar.activeGainBonus
            };
        }
    }
}
