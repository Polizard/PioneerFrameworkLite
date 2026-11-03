using System;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Launch
{
    /// <summary>显式调用的飞船起飞过场入口；现有发射流程不调用此类。</summary>
    public static class LaunchTransitService
    {
        private const string TransitDefName = "PF_LaunchTransit";

        // 登记可能先改变管理器再抛异常；回退不得依赖登记方法正常返回。
        internal static bool TryRunWithRollback(Action apply, Action rollback, out string reason)
        {
            reason = string.Empty;
            try
            {
                apply();
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                try { rollback(); }
                catch (Exception cleanupEx) { reason += "；回退失败：" + cleanupEx.Message; }
                return false;
            }
        }

        public static bool TryValidateTarget(OrbitState targetOrbit, DVector3 bodyCenterSystem,
            double bodyRadiusMeters, out string reason)
        {
            reason = string.Empty;
            if (!bodyCenterSystem.IsFinite() || !targetOrbit.positionSystem.IsFinite() ||
                !targetOrbit.velocitySystem.IsFinite() || double.IsNaN(bodyRadiusMeters) ||
                double.IsInfinity(bodyRadiusMeters) || bodyRadiusMeters <= 0.0)
            {
                reason = "目标轨道包含无效坐标、速度或天体半径。";
                return false;
            }

            var rotation = targetOrbit.rotation;
            double rotationNorm = (double)rotation.x * rotation.x + (double)rotation.y * rotation.y +
                (double)rotation.z * rotation.z + (double)rotation.w * rotation.w;
            if (double.IsNaN(rotationNorm) || double.IsInfinity(rotationNorm) || rotationNorm < 1e-12)
            {
                reason = "目标轨道姿态无效。";
                return false;
            }

            if ((targetOrbit.positionSystem.v - bodyCenterSystem).magnitude <= bodyRadiusMeters)
            {
                reason = "目标轨道位置必须位于天体表面之外。";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 创建临时大世界对象。目标系统系轨道状态在到达时直接使用，不做轨道传播。
        /// 仅登记过场；实体与 group 在动画完成后创建。
        /// </summary>
        public static bool TryStart(Map groundMap, SpacecraftMapParent spacecraft, int bodyId,
            int factionId, OrbitState targetOrbit, int durationTicks,
            out LaunchTransitWorldObject? transit, out string reason)
        {
            transit = null;
            reason = string.Empty;
            if (groundMap == null || spacecraft?.Map == null || Find.World == null || Find.WorldObjects == null)
            {
                reason = "地面地图、飞船地图或世界对象管理器不可用。";
                return false;
            }
            if (durationTicks <= 0 || factionId < 0)
            {
                reason = "动画时长必须为正数，阵营 ID 必须有效。";
                return false;
            }
            if (spacecraft.state != SpacecraftState.Ground ||
                (spacecraft.sourceMap != null && spacecraft.sourceMap != groundMap))
            {
                reason = "飞船地图不在指定的地面地图上。";
                return false;
            }

            var celestial = Current.Game?.GetComponent<CelestialBodyManager>();
            if (celestial?.GetBodyById(bodyId) == null ||
                GetSurfaceState.GetLayerForBody(celestial, bodyId) != groundMap.Tile.Layer)
            {
                reason = "起飞地块与中心天体不匹配。";
                return false;
            }

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mapComp = spacecraft.Map.GetComponent<SpaceMapComp>();
            if (spaceGame == null || mapComp == null || mapComp.ownerEntityId >= 0)
            {
                reason = "飞船地图已有空间实体归属，或缺少必要的空间地图组件。";
                return false;
            }
            if (TryFindByMapId(spacecraft.Map.uniqueID, out _))
            {
                reason = "这张飞船地图已有起飞过场。";
                return false;
            }

            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 bodyCenter = ViewFrame.BodySystemPosition(bodyId, physicsTick);
            if (!TryValidateTarget(targetOrbit, bodyCenter, GetSurfaceState.BodyRadius(celestial, bodyId), out reason))
                return false;

            double physicsSpeed = Current.Game?.GetComponent<TimeScaleManager>()?.PhysicsSpeedMultiplier ?? 1.0;
            DVector3 arrivalBodyCenter = ViewFrame.BodySystemPosition(bodyId,
                physicsTick + durationTicks * physicsSpeed);
            if (!TryValidateTarget(targetOrbit, arrivalBodyCenter,
                GetSurfaceState.BodyRadius(celestial, bodyId), out reason))
            {
                reason = "预计到达时目标轨道无效：" + reason;
                return false;
            }

            var def = DefDatabase<WorldObjectDef>.GetNamedSilentFail(TransitDefName);
            if (def == null)
            {
                reason = "缺少 PF_LaunchTransit 大世界对象定义。";
                return false;
            }

            LaunchTransitWorldObject? created = null;
            int tick = Find.TickManager.TicksGame;
            if (!TryRunWithRollback(() =>
            {
                created = WorldObjectMaker.MakeWorldObject(def) as LaunchTransitWorldObject;
                if (created == null) throw new InvalidOperationException("无法创建飞船起飞过场对象。");

                DVector3 startLocal = GetSurfaceState.GroundStateAtLaunch(groundMap, 0.0, bodyId).pos;
                created.Tile = groundMap.Tile;
                created.Initialize(spacecraft.Map.uniqueID, spacecraft.spacecraftId, bodyId, factionId,
                    bodyCenter, bodyCenter + startLocal, targetOrbit, tick, durationTicks);
                Find.WorldObjects.Add(created);
            }, () =>
            {
                if (created == null || created.Destroyed) return;
                // 清理自身若再次失败，保留的对象也不会自动结算为 group。
                created.MarkFailed("起飞过场登记失败。", tick);
                created.Destroy();
            }, out reason))
            {
                reason = "登记起飞过场失败：" + reason;
                return false;
            }

            transit = created;
            return true;
        }

        public static bool TryFindByMapId(int mapId, out LaunchTransitWorldObject? transit)
        {
            transit = null;
            if (Current.Game == null || Find.World == null) return false;
            transit = Find.WorldObjects?.AllWorldObjects
                .OfType<LaunchTransitWorldObject>()
                .FirstOrDefault(x => !x.Destroyed && x.SpacecraftMapId == mapId);
            return transit != null;
        }

        public static bool TryCancel(LaunchTransitWorldObject transit, out string reason)
        {
            reason = string.Empty;
            if (transit == null || transit.Destroyed || transit.Status == LaunchTransitStatus.Completed)
            {
                reason = "起飞过场不存在或已经结算。";
                return false;
            }
            try
            {
                transit.Destroy();
                return true;
            }
            catch (Exception ex)
            {
                reason = "取消起飞过场失败：" + ex.Message;
                return false;
            }
        }

        internal static bool TryComplete(LaunchTransitWorldObject transit, out string reason)
        {
            reason = string.Empty;
            if (transit == null || transit.Status != LaunchTransitStatus.InFlight)
            {
                reason = "起飞过场状态无效。";
                return false;
            }

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var spacecraft = SpacecraftMapRegistry.GetByMapId(transit.SpacecraftMapId)
                ?? Find.Maps.FirstOrDefault(x => x.uniqueID == transit.SpacecraftMapId)?.Parent as SpacecraftMapParent;
            Map? map = spacecraft?.Map;
            var mapComp = map?.GetComponent<SpaceMapComp>();
            if (spaceGame == null || spacecraft == null || map == null || mapComp == null ||
                spacecraft.spacecraftId != transit.SpacecraftId ||
                spacecraft.state != SpacecraftState.Ground || mapComp.ownerEntityId >= 0)
            {
                reason = "飞船地图已丢失、已起飞或已有空间实体归属。";
                return false;
            }

            var celestial = Current.Game?.GetComponent<CelestialBodyManager>();
            if (celestial?.GetBodyById(transit.BodyId) == null)
            {
                reason = "中心天体已不可用。";
                return false;
            }
            if (!TryValidateTarget(transit.TargetOrbit,
                ViewFrame.BodySystemPosition(transit.BodyId, TimeScaleManager.GetPhysicsTickSafe()),
                GetSurfaceState.BodyRadius(celestial, transit.BodyId), out reason))
                return false;

            SpaceEntityGroup? group = null;
            int entityId = -1;
            if (!TryRunWithRollback(() =>
            {
                int groupId = spaceGame.AllocGroupId();
                entityId = spaceGame.AllocEntityId();
                group = new SpaceEntityGroup(groupId, transit.FactionId)
                {
                    IsMovable = true,
                    IsCollidable = true,
                    CelestialBodyId = transit.BodyId,
                    CurrentOrbit = transit.TargetOrbit
                };
                var entity = SpaceMapGenerator.CreateStationEntity(entityId, transit.FactionId,
                    transit.TargetOrbit.positionSystem.v, map.Size.x, map.Size.z);
                entity.GroupId = groupId;

                spaceGame.RegisterGroup(group, GroupBirthKind.Instantiated);   // 转运落地建组：无母体
                if (!ReferenceEquals(spaceGame.GetGroupById(groupId), group))
                    throw new InvalidOperationException("group 注册失败");

                spaceGame.RegisterEntity(entity);
                if (!ReferenceEquals(spaceGame.GetEntityById(entityId), entity))
                    throw new InvalidOperationException("空间实体注册失败");

                SpaceMapGenerator.LinkEntityToMap(entity, map);
                if (entity.MapId != map.uniqueID || mapComp.ownerEntityId != entityId)
                    throw new InvalidOperationException("飞船地图绑定失败");

                spacecraft.MarkLaunched();
            }, () =>
            {
                if (mapComp.ownerEntityId == entityId) mapComp.ownerEntityId = -1;
                if (group != null && ReferenceEquals(spaceGame.GetGroupById(group.GroupId), group))
                    spaceGame.DestroyGroup(group.GroupId);
            }, out reason))
            {
                reason = "创建飞船 group 失败：" + reason;
                return false;
            }

            transit.MarkCompleted();
            try { transit.Destroy(); }
            catch (Exception ex) { Log.Error("[LaunchTransit] 清理已完成的过场对象失败：" + ex); }
            return true;
        }
    }
}
