using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 跳帮测试场景部署（调试/验收用，与游戏 UI 解耦）：
    /// - DeployEvaTest：N 兵 EVA 多点突入（每兵独立落点）
    /// - DeployShuttleTest：母船穿梭机 + 乘客 → LaunchShuttle
    /// - DeployHighSpeedCrashTest：EVA 高速冲目标 → 验证 CombatManager 接舷分支不误消费（应爆炸）
    /// 每个场景：双空间站（母船 + 共轨目标站）+ 强制 1x + 目标站 Map + 详细日志/探针自动开启。
    /// </summary>
    public static class BoardingTestScenarios
    {
        private const int EARTH_BODY_ID = 1;

        // ════════════════════════════════════════════
        //  场景入口
        // ════════════════════════════════════════════

        /// <summary>
        /// 单兵/多兵 EVA 突入：pawnCount 名突击兵各选落点出发。
        /// 距离取 3km：&lt; ApproachTerminalDistance(5km) → 出生即进引导终端段（直线收拢+消速），
        /// 几分钟内完成降落；10km 时巡航段 Lambert 转移对近距离同轨追赶极慢（数十 m/s 反复修正振荡）。
        /// </summary>
        public static void DeployEvaTest(int pawnCount, double distanceMeters = 3000.0)
        {
            var env = DeployTwoStations(distanceMeters);
            if (env.playerEntity == null) return;

            var soldierCells = new[] { new IntVec3(28, 0, 30), new IntVec3(30, 0, 28), new IntVec3(26, 0, 32) };
            var offsets = PickLandingOffsets(env.enemyMap, pawnCount);
            var soldiers = new List<Pawn>();
            for (int i = 0; i < pawnCount; i++)
            {
                IntVec3 cell = soldierCells[i % soldierCells.Length];
                soldiers.Add(SpawnColonist(env.playerMap, cell, "跳帮突击兵" + (i + 1)));
            }
            SpawnColonist(env.enemyMap, new IntVec3(30, 0, 30), "跳帮守军");

            // 出生位置：目标站轨道上「目标站外侧 distanceMeters」（远离母船方向），速度 = 目标站速度。
            // 三重收益：
            // ① 同半径同相位速度 → 单位相对目标站静止（relv≈0），出生 dist≈3km < 终端窗口 5km →
            //    全程直线收拢，永不进巡航 Lambert（巡航段对近距离用 60+m/s 大 burn 反复振荡，日志实证）；
            // ② 单位距母船 2×distanceMeters（≈6km）> 扫掠盒步长一半 → 扫掠 AABB 不覆盖母船，免疫 tunneling 误判；
            // ③ 多兵用世界 Y（轨道面法向）±60m 隔离：扫掠盒是 xz 长条、y 仅 3m 厚，Y 差 60m → 永不重叠。
            //    （径向分散无效——所有单位都在 xz 轨道面内运动，扫掠盒 xz 必然重叠，日志实证 3 兵互相 tunneling）
            DVector3 enemyPos = GetGroupPosSafe(env.enemyGroup);
            DVector3 motherPos = GetGroupPosSafe(env.playerGroup);
            DVector3 away = (enemyPos - motherPos).normalized; // 目标站 → 远离母船（≈轨道切向）
            DVector3 orbitNormal = new DVector3(0, 1, 0);      // 轨道面（xz）法向
            // ⚠️ positionOffset 是「相对母船的偏移」（CreateTransitVehicle: pos = 母船位置 + offset），
            // 必须传 出生绝对位置 - 母船位置，不能直接传绝对坐标（否则单位 = 母船 + 目标站 ≈ 14000km 外）
            DVector3 birthAbs = enemyPos + away * distanceMeters;
            DVector3 relBirth = birthAbs - motherPos;
            var birthOffsets = new[]
            {
                relBirth,
                relBirth + orbitNormal * 60.0,
                relBirth - orbitNormal * 60.0
            };
            // 速度 = 目标站速度（单位默认用母船速度，补 offset = 目标站速度 - 母船速度）
            DVector3 targetVel = GetGroupVelocitySafe(env.enemyGroup);
            DVector3 motherVel = GetGroupVelocitySafe(env.playerGroup);
            DVector3 velOffset = targetVel - motherVel;
            var velocityOffsets = new[] { velOffset, velOffset, velOffset };

            var boarding = Current.Game.GetComponent<BoardingManager>();
            int launched = 0;
            for (int i = 0; i < soldiers.Count; i++)
            {
                var unit = boarding.LaunchEva(soldiers[i], env.enemyGroup.GroupId,
                    env.enemyEntity.Parts[0].partId, offsets[i],
                    velocityOffsets[i % velocityOffsets.Length], birthOffsets[i % birthOffsets.Length]);
                if (unit != null) launched++;
            }

            FinishDeploy($"EVA 测试：{launched}/{soldiers.Count} 兵已起飞 → 站 G{env.enemyGroup.GroupId}（{distanceMeters / 1000.0:F0}km）", env);
        }

        /// <summary>穿梭机测试：母船放穿梭机 + passengerCount 名乘客 → LaunchShuttle。</summary>
        public static void DeployShuttleTest(int passengerCount = 2, double distanceMeters = 3000.0)
        {
            var env = DeployTwoStations(distanceMeters);
            if (env.playerEntity == null) return;

            // 母船 Map 内放置穿梭机（3x3，占地）与旁边乘客
            var shuttleDef = DefDatabase<ThingDef>.GetNamedSilentFail("PF_Building_Shuttle");
            if (shuttleDef == null)
            {
                Log.Error("[BoardingTest] PF_Building_Shuttle Def 缺失，穿梭机场景部署失败");
                return;
            }
            IntVec3 dockCell = new IntVec3(26, 0, 30);
            var shuttle = ThingMaker.MakeThing(shuttleDef);
            GenSpawn.Spawn(shuttle, dockCell, env.playerMap, Rot4.North, WipeMode.Vanish, false);

            var passengers = new List<Pawn>();
            var seatCells = new[] { new IntVec3(24, 0, 30), new IntVec3(24, 0, 32), new IntVec3(24, 0, 34) };
            for (int i = 0; i < passengerCount; i++)
                passengers.Add(SpawnColonist(env.playerMap, seatCells[i % seatCells.Length], "穿梭机乘客" + (i + 1)));
            SpawnColonist(env.enemyMap, new IntVec3(30, 0, 30), "跳帮守军");

            var offsets = PickLandingOffsets(env.enemyMap, 1);
            var boarding = Current.Game.GetComponent<BoardingManager>();
            // 出生：目标站轨道上「目标站外侧 distanceMeters」，速度 = 目标站速度
            // （同轨相对静止 + 远离母船防扫掠误判 + 出生 dist<5km 直接进终端段直线收拢，见 DeployEvaTest 注释）
            DVector3 enemyPos = GetGroupPosSafe(env.enemyGroup);
            DVector3 motherPos = GetGroupPosSafe(env.playerGroup);
            DVector3 away = (enemyPos - motherPos).normalized;
            // positionOffset 是相对母船的偏移（勿传绝对坐标，见 DeployEvaTest 注释）
            DVector3 birthPos = enemyPos + away * distanceMeters - motherPos;
            DVector3 velOffset = GetGroupVelocitySafe(env.enemyGroup) - GetGroupVelocitySafe(env.playerGroup);
            var unit = boarding.LaunchShuttle(shuttle, passengers, env.enemyGroup.GroupId,
                env.enemyEntity.Parts[0].partId, offsets[0], birthPos, velOffset);

            FinishDeploy($"穿梭机测试：Shuttle{(unit != null ? unit.shuttleId : -1)} {passengers.Count} 名乘客已起飞 → 站 G{env.enemyGroup.GroupId}（{distanceMeters / 1000.0:F0}km）", env);
        }

        /// <summary>
        /// 高速撞击测试：EVA 兵以 80 m/s 高速直冲目标站（超 ContactConsumeRelVel 30 / 爆炸阈值 50）。
        /// 期望：CombatManager 接舷分支不消费（vRel≥30）→ 正常船-船爆炸 → 单位击毁结算。
        /// 距离 1500m：1500/80≈19s &lt; 引导首次检查(30s) → 在引导消速干预前撞上。
        /// </summary>
        public static void DeployHighSpeedCrashTest(double distanceMeters = 1500.0)
        {
            var env = DeployTwoStations(distanceMeters);
            if (env.playerEntity == null) return;

            var soldier = SpawnColonist(env.playerMap, new IntVec3(28, 0, 30), "高速撞击兵");
            var offsets = PickLandingOffsets(env.enemyMap, 1);

            // 速度偏移：朝目标站方向 -80 m/s（目标站共轨相对静止 → 相对接近速度 ≈ 80 m/s）
            DVector3 dirToTarget = (GetGroupPosSafe(env.enemyGroup) - GetGroupPosSafe(env.playerGroup)).normalized;
            var boarding = Current.Game.GetComponent<BoardingManager>();
            var unit = boarding.LaunchEva(soldier, env.enemyGroup.GroupId,
                env.enemyEntity.Parts[0].partId, offsets[0], dirToTarget * -80.0);

            FinishDeploy($"高速撞击测试：Unit{(unit != null ? unit.id : -1)} 以 80m/s 冲 G{env.enemyGroup.GroupId}（期望碰撞爆炸，接舷分支不误消费）", env);
        }

        // ════════════════════════════════════════════
        //  基础设施
        // ════════════════════════════════════════════

        private class TestEnv
        {
            public SpaceEntity playerEntity = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
            public SpaceEntityGroup playerGroup = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
            public Map playerMap = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
            public SpaceEntity enemyEntity = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
            public SpaceEntityGroup enemyGroup = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
            public Map enemyMap = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        }

        /// <summary>部署双站测试环境：母船（圆轨）+ 目标站（共轨 distanceMeters 外，敌对 faction）。</summary>
        private static TestEnv DeployTwoStations(double distanceMeters)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return new TestEnv();

            bool wasPaused = Find.TickManager.CurTimeSpeed == TimeSpeed.Paused;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            if (wasPaused)
                Log.Message("[BoardingTest] 检测到暂停，已自动解除（TimeSpeed.Normal）");

            int playerFactionId = Faction.OfPlayer?.loadID ?? 0;

            // 母船（圆轨）
            var (pEntity, pGroup, pMap) = CreateStation(spaceGame, playerFactionId, 2000000.0, "跳帮母船");
            if (pEntity == null || pGroup == null || pMap == null) return new TestEnv();

            // 目标站：母船轨道圆上距离 distanceMeters 的相邻点（同半径圆轨 → 真正共轨，相对静止）。
            // 若像之前那样"母船位置 + 偏移"再进圆轨，半径差 ~偏移量 → 角速度不同 → 持续相对漂移
            // （relv 十几~几十 m/s），跳帮单位要绕轨道追大半圈，测试耗时数倍。
            DVector3 playerPos = GetGroupPosSafe(pGroup);
            double R = playerPos.magnitude;
            if (R < 1.0)
                R = SpaceConstants.PlanetRadiusMeters + 2000000.0;
            double halfAngle = System.Math.Asin(System.Math.Min(1.0, distanceMeters / (2.0 * R)));
            DVector3 enemyPos = DVector3.Rotate(playerPos, DVector3.up, 2.0 * halfAngle);
            int enemyFactionId = playerFactionId + 1;
            var (eEntity, eGroup, eMap) = CreateStationAt(spaceGame, enemyFactionId, enemyPos,
                "跳帮目标站");
            if (eEntity == null || eGroup == null || eMap == null) return new TestEnv();

            return new TestEnv
            {
                playerEntity = pEntity, playerGroup = pGroup, playerMap = pMap,
                enemyEntity = eEntity, enemyGroup = eGroup, enemyMap = eMap
            };
        }

        private static (SpaceEntity? entity, SpaceEntityGroup? group, Map? map) CreateStation(
            SpaceGameComp spaceGame, int factionId, double altitudeMeters, string name)
        {
            double earthRadius = SpaceConstants.PlanetRadiusMeters;
            double orbitRadius = earthRadius + altitudeMeters;
            // 2026-09-14：相位改由物理 tick 推导（原 DateTime.Now.Millisecond 每次运行不同 →
            // 同一场景几何不可复现，"这次没覆盖/超时"无法与上次对照）
            double angle = (TimeScaleManager.GetPhysicsTickSafe() % 60000.0) / 60000.0 * System.Math.PI * 2.0;
            double x = System.Math.Cos(angle) * orbitRadius;
            double z = System.Math.Sin(angle) * orbitRadius;
            return CreateStationAt(spaceGame, factionId, new DVector3(x, 0, z), name);
        }

        private static (SpaceEntity? entity, SpaceEntityGroup? group, Map? map) CreateStationAt(
            SpaceGameComp spaceGame, int factionId, DVector3 pos, string name)
        {
            int groupId = spaceGame.AllocGroupId();
            int entityId = spaceGame.AllocEntityId();
            var entity = spaceGame.CreateStation(entityId, groupId, factionId, pos, 60, 60, name);
            if (entity == null) return (null, null, null);
            var group = spaceGame.GetGroupById(groupId);
            if (group != null)
                group.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);
            var map = SpaceMapGenerator.FindPocketMap(entity.MapId);
            return (entity, group, map);
        }

        /// <summary>在目标站 Map 中心附近挑选 count 个彼此间距 ≥5m 的可站立落点（局部系偏移，散布 ±22 格）。</summary>
        private static List<DVector3> PickLandingOffsets(Map map, int count)
        {
            var result = new List<DVector3>();
            int tries = 0;
            while (result.Count < count && tries < 300)
            {
                tries++;
                IntVec3 c = map.Center + new IntVec3(Rand.Range(-22, 23), 0, Rand.Range(-22, 23));
                if (!c.InBounds(map) || !c.Walkable(map)) continue;
                var off = (DVector3)MapCoordMapper.MapCellToShip(map, c);
                if (result.Any(o => (o - off).sqrMagnitude < 25.0)) continue; // 至少 5m 间距
                result.Add(off);
            }
            if (result.Count == 0)
                result.Add(DVector3.zero);
            return result;
        }

        /// <summary>生成一名玩家殖民者并放置（测试用）。</summary>
        private static Pawn SpawnColonist(Map map, IntVec3 cell, string name)
        {
            var pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            pawn.Name = new NameTriple(name, name, name);
            GenSpawn.Spawn(pawn, cell, map, Rot4.North, WipeMode.Vanish, false);
            return pawn;
        }

        private static DVector3 GetGroupPosSafe(SpaceEntityGroup g)
        {
            if (g == null) return DVector3.zero;
            if (g.IsCircularOrbit)
                return g.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe());
            return g.CurrentOrbit.positionSystem.v;
        }

        private static DVector3 GetGroupVelocitySafe(SpaceEntityGroup g)
        {
            if (g == null) return DVector3.zero;
            if (g.IsCircularOrbit)
                return g.GetCircularVelocityAtTick(TimeScaleManager.GetPhysicsTickSafe());
            return g.CurrentOrbit.velocitySystem.v;
        }

        /// <summary>场景收尾：跳转目标站 Map + 开启详细日志与探针。</summary>
        private static void FinishDeploy(string summary, TestEnv env)
        {
            BoardingManager.DebugDetailedBoarding = true;
            BoardingManager.ProbeInterval = 60; // 每秒一条状态探针
            SpaceMapGenerator.EnterPocketMap(env.enemyMap);

            Log.Message("[BoardingTest] " + summary);
            Log.Message("[BoardingTest] 详细日志已开启（DebugDetailedBoarding），状态探针 60 tick/条");
            Messages.Message(summary, MessageTypeDefOf.TaskCompletion);
        }
    }
}
