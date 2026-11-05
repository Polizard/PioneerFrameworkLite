using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.SpaceAI;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **PFView 调试入口的引擎侧实现**（2026-11-01 类重组批 5e §4.3）。
    ///
    /// 这些是 DebugTabMenu_PFL 的调试动作（传送 / 调速 / 部署交火对 / 预生成舱室图），
    /// 需要直接改物理状态或用 `SpaceEntityGroup`；留在 `Debug/`（表现层架构闸门不扫这里），
    /// PFView 只按 id 调——表现层因此不用持有引擎对象。
    /// 语义与搬迁前逐行一致；这是调试台架，不是玩家路径。
    /// </summary>
    internal static class PFViewDebugBridge
    {
        internal static bool TeleportTargetHere(int shooterGroupId, int targetGroupId)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var shooter = spaceGame?.GetGroupById(shooterGroupId);
            var target = spaceGame?.GetGroupById(targetGroupId);
            if (shooter == null || target == null) return false;

            DVector3 shooterPos = GroupExactPos(shooter);
            DVector3 shooterVel = shooter.CurrentOrbit.velocitySystem.v;
            if (shooter.IsCircularOrbit)
            {
                double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
                DVector3 pos = shooter.GetCircularPositionAtTick(physicsTick);
                DVector3 radial = FrameConvert.SystemToBody(pos, shooter.GetCentralBodyOffset(physicsTick)).normalized.v;
                DVector3 tan = shooter.circularOrbitNormal.sqrMagnitude > 0.001
                    ? DVector3.Cross(shooter.circularOrbitNormal, radial).normalized
                    : new DVector3(-radial.z, 0, radial.x).normalized;
                shooterVel = tan * (shooter.circularAngularVelocity * shooter.circularRadius);
            }

            if (target.IsCircularOrbit)
                target.ExitCircularOrbit();
            DVector3 dir = shooterVel.sqrMagnitude > 1e-6 ? shooterVel.normalized : new DVector3(1, 0, 0);
            target.CurrentOrbit.positionSystem = (SystemVec3)(shooterPos + dir * 1000.0);
            target.CurrentOrbit.velocitySystem = (SystemVec3)shooterVel;
            target.OrbitDirty = true;

            Log.Message($"[PFView] 传送 G{target.GroupId} → G{shooter.GroupId} 前方1km | shooter circular={shooter.IsCircularOrbit} pos=({shooterPos.x:F0},{shooterPos.y:F0},{shooterPos.z:F0}) vel=({shooterVel.x:F0},{shooterVel.y:F0},{shooterVel.z:F0}) | target circular={target.IsCircularOrbit} pos=({target.CurrentOrbit.positionSystem.x:F0},{target.CurrentOrbit.positionSystem.y:F0},{target.CurrentOrbit.positionSystem.z:F0}) vel=({target.CurrentOrbit.velocitySystem.x:F0},{target.CurrentOrbit.velocitySystem.y:F0},{target.CurrentOrbit.velocitySystem.z:F0})");
            return true;
        }

        internal static bool AdjustTargetSpeed(int shooterGroupId, int targetGroupId, double dv)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var shooter = spaceGame?.GetGroupById(shooterGroupId);
            var target = spaceGame?.GetGroupById(targetGroupId);
            if (shooter == null || target == null) return false;

            if (target.IsCircularOrbit)
                target.ExitCircularOrbit();

            DVector3 toShooter = GroupExactPos(shooter) - GroupExactPos(target);
            double len = toShooter.magnitude;
            if (len < 1e-6) return false;
            toShooter /= len;
            // 手动 ΔV 走合冲量累积器：物理步首统一施加（与 burn/碰撞同入口，避免一步内多次改速度）
            target.pendingImpulse += toShooter * dv;
            target.OrbitDirty = true;

            Log.Message($"[PFView] G{target.GroupId} 速度沿射向 G{shooter.GroupId} 方向调整 {dv:+#;-#;0} m/s");
            return true;
        }

        /// <summary>预生成选中编队的全部舱室地图；返回新（或已存在）图的数量。</summary>
        internal static int PregenerateRoomMaps(int groupId)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var group = spaceGame?.GetGroupById(groupId);
            if (group == null) return -1;
            int count = 0;
            foreach (int eid in group.GetEntityIds())
            {
                var ent = spaceGame!.GetEntityById(eid);
                if (ent == null) continue;
                foreach (var part in ent.Parts)
                {
                    if (!part.IsRoomModulePart) continue;
                    if (SpaceMapGenerator.GenerateRoomMap(ent, part) != null) count++;
                }
            }
            return count;
        }

        /// <summary>部署一对配有实际设备和内景的交火测试船，间距 200m、高度 500km。
        /// 生成在长事件里完成 ⇒ 结果通过 `onDeployed(首组id, 次组id)` 回调（可能晚于本调用）。</summary>
        internal static bool DeployCombatTestPair(int bodyId, Action<int, int>? onDeployed)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return false;
            var source = SpaceMapGenerator.GetAnchorMap();
            var enemy = IncidentWorker_SpaceEncounter.FindEnemy();
            var template = DefDatabase<SpaceShipTemplateDef>.GetNamedSilentFail("PF_Debug_CombatShip");
            if (source == null || enemy == null || template == null) return false;
            var session = Current.Game;
            LongEventHandler.QueueLongEvent(() =>
            {
                if (!ReferenceEquals(session, Current.Game)) return;
                using var creation = new EncounterBuildTransaction(id => spaceGame.DestroyGroup(id, "交火测试部署回滚"));
                double tick = TimeScaleManager.GetPhysicsTickSafe();
                double orbitRadius = spaceGame.GetBodySurfaceRadius(bodyId) + 500000.0;
                double angle = tick * 0.001;
                double speed = Math.Sqrt(spaceGame.GetBodyGM(bodyId) / orbitRadius);
                var reference = new SpaceEntityGroup(-1, Faction.OfPlayer.loadID) { CelestialBodyId = bodyId };
                var center = reference.GetCentralBodyOffset(tick);
                var centerVelocity = reference.GetCentralBodyVelocity(tick);
                DVector3 basePos = FrameConvert.BodyToSystem((BodyVec3)new DVector3(
                    Math.Cos(angle) * orbitRadius, 0, Math.Sin(angle) * orbitRadius), center);
                DVector3 vel = FrameConvert.BodyToSystemVelocity((BodyVec3)new DVector3(
                    -Math.Sin(angle) * speed, 0, Math.Cos(angle) * speed), centerVelocity);
                var first = CreateTestShip(template, source, basePos, vel, Faction.OfPlayer, bodyId, creation);
                var second = CreateTestShip(template, source, basePos + new DVector3(200, 0, 0), vel, enemy, bodyId, creation);
                creation.Commit();
                onDeployed?.Invoke(first.GroupId, second.GroupId);
                Log.Message($"[PFView] 部署完整交火测试对: G{first.GroupId}(f{first.FactionId}) vs G{second.GroupId}(f{second.FactionId}) 间距200m @ {orbitRadius / 1000.0:F0}km 轨道；传感器/主动雷达/武器/燃料已就绪");
            }, "GeneratingMap", false, e => Log.Error("[PFView] 交火测试部署失败：" + e));
            return true;
        }

        /// <summary>测试舰复用遭遇舰船生成与设备投影，保持玩家可操作的普通建筑。</summary>
        private static SpaceEntityGroup CreateTestShip(SpaceShipTemplateDef template, Map source,
            DVector3 pos, DVector3 vel, Faction faction, int bodyId, EncounterBuildTransaction creation)
        {
            var group = EncounterShipFactory.Create(template, faction, source, pos, vel, bodyId);
            creation.Add(group.GroupId);
            group.IsStealth = false;
            group.ActivePingEnabled = true;
            group.EnterCircularOrbit(Find.TickManager.TicksGame, bodyId);
            var spaceGame = Current.Game.GetComponent<SpaceGameComp>();
            foreach (int eid in group.GetEntityIds())
            {
                var map = SpaceMapGenerator.FindPocketMap(spaceGame.GetEntityById(eid)!.MapId);
                if (map == null) continue;
                Thing? designator = map.spawnedThings.FirstOrDefault(t => t.TryGetComp<CompMapDesignator>() != null);
                if (designator == null) continue;
                foreach (var thing in map.spawnedThings)
                {
                    var turret = thing.TryGetComp<CompMapTurret>();
                    if (turret?.Mount != null && designator.TryGetComp<CompMapDesignator>().RegisterTurret(thing))
                        turret.Mount.boundDesignator = designator;
                }
            }
            return group;
        }

        private static DVector3 GroupExactPos(SpaceEntityGroup g)
        {
            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            return g.IsCircularOrbit ? g.GetCircularPositionAtTick(physicsTick) : g.CurrentOrbit.positionSystem.v;
        }
    }
}
