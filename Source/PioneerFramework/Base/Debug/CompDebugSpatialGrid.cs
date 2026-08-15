using Verse;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using PioneerFramework.Debug;

namespace PioneerFramework
{
    public class CompProperties_DebugSpatialGrid : CompProperties
    {
        public CompProperties_DebugSpatialGrid()
        {
            this.compClass = typeof(CompDebugSpatialGrid);
        }
    }

    public class CompDebugSpatialGrid : ThingComp
    {
        private SpaceGameComp spaceGame;
        private CelestialBodyManager celestialMgr;
        private static int nextEntityId = 1000;
        private static int nextGroupId = 100;

        // ── 地球天体 ID ──
        /// <summary>现有 RimWorld 世界对应的天体（地球）ID</summary>
        private const int EARTH_BODY_ID = 1;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            spaceGame = Current.Game.GetComponent<SpaceGameComp>();
            celestialMgr = Current.Game.GetComponent<CelestialBodyManager>();
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
                yield return g;

            if (parent.Faction != Faction.OfPlayer) yield break;

            yield return new Command_Action
            {
                defaultLabel = "打开3D网格调试器",
                defaultDesc = "显示空间网格中所有实体及其位置。",
                icon = TexButton.Rename,
                action = () =>
                {
                    var existing = Find.WindowStack.Windows.FirstOrDefault(w => w is Window_SpatialGridDebugger);
                    if (existing != null)
                        existing.Close();
                    Find.WindowStack.Add(new Window_SpatialGridDebugger(spaceGame));
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "显示太阳系全景",
                defaultDesc = "打开3D太阳系可视化，显示所有天体和轨道。",
                icon = TexButton.Info,
                action = () =>
                {
                    var existing = Find.WindowStack.Windows.FirstOrDefault(w => w is Window_SolarSystemMap);
                    if (existing != null)
                        existing.Close();
                    Find.WindowStack.Add(new Window_SolarSystemMap());
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "创建随机实体（自动分组）",
                defaultDesc = "创建一个独立分组的太空实体。",
                action = () => CreateRandomEntity(0)
            };

            yield return new Command_Action
            {
                defaultLabel = "删除最近实体",
                defaultDesc = "删除距离调试建筑最近的实体。",
                action = () => DestroyNearestEntity()
            };

            yield return new Command_Action
            {
                defaultLabel = "删除所有实体",
                defaultDesc = "移除所有实体和集群。",
                action = () => DestroyAllEntities()
            };

            yield return new Command_Action
            {
                defaultLabel = "打印统计数据",
                defaultDesc = "在日志中输出当前实体/集群数量。",
                action = () => PrintStats()
            };
            yield return new Command_Action
            {
                defaultLabel = "创建绕地轨道（圆轨）",
                defaultDesc = "创建围绕地球的圆形轨道集群。",
                action = () => CreateOrbitingGroup()
            };
            yield return new Command_Action
            {
                defaultLabel = "创建绕月轨道",
                defaultDesc = "创建围绕月球（bodyId=2）的圆形轨道集群。",
                action = () => CreateLunarOrbitGroup()
            };
            yield return new Command_Action
            {
                defaultLabel = "创建同步轨道",
                defaultDesc = "创建高圆轨测试同步轨道视觉效果（360°/游戏日）。",
                action = () => CreateSyncOrbitTest()
            };
            yield return new Command_Action
            {
                defaultLabel = "创建空间站（子地图）",
                defaultDesc = "创建一个带有独立子地图的空间站。",
                action = () => CreateSpaceStation()
            };

            yield return new Command_Action
            {
                defaultLabel = "进入空间站",
                defaultDesc = "进入最近的空间站地图。",
                action = () =>
                {
                    var station = spaceGame.GetAllEntities().FirstOrDefault(e => e.HasMap);
                    if (station != null)
                        spaceGame.EnterHiddenMap(station.MapId);
                    else
                        Messages.Message("未找到空间站。", MessageTypeDefOf.RejectInput);
                }
            };

            // ── 3D Physics 测试 ──
            yield return new Command_Action
            {
                defaultLabel = "测试3D Physics",
                defaultDesc = "检测 Unity 3D Physics 模块是否可用（决定战斗碰撞方案）。",
                icon = TexButton.Info,
                action = () =>
                {
                    var existing = Find.WindowStack.Windows.FirstOrDefault(w => w is Window_Physics3DTest);
                    if (existing != null)
                        existing.Close();
                    Find.WindowStack.Add(new Window_Physics3DTest());
                }
            };

            // ── 天体系统调试 ──
            yield return new Command_Action
            {
                defaultLabel = "列出天体系统",
                defaultDesc = "显示所有从XML Defs加载的注册天体。",
                icon = TexButton.Rename,
                action = () => ListCelestialBodies()
            };

            // ── 战斗系统测试 ──
            yield return new Command_Action
            {
                defaultLabel = "战斗系统测试",
                defaultDesc = "打开战斗系统测试窗口，测试炮塔、武器、射弹等功能。",
                icon = TexButton.Info,
                action = () =>
                {
                    var existing = Find.WindowStack.Windows
                        .FirstOrDefault(w => w is Window_CombatTest);
                    if (existing != null)
                        existing.Close();
                    Find.WindowStack.Add(new Window_CombatTest());
                }
            };

            // ── 时间挡位控制 ──
            var timeScaleManager = Current.Game.GetComponent<TimeScaleManager>();
            if (timeScaleManager != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "战斗模式 (1/60x)",
                    defaultDesc = "切换到战斗模式，时间流速减慢60倍，物理计算更精确。",
                    icon = TexCommand.Attack,
                    action = () => timeScaleManager.SetCombatMode()
                };

                yield return new Command_Action
                {
                    defaultLabel = "正常模式 (1x)",
                    defaultDesc = "切换到正常模式，时间流速恢复正常。",
                    icon = TexButton.Info,
                    action = () => timeScaleManager.SetNormalMode()
                };

                yield return new Command_Action
                {
                    defaultLabel = "快速模式 (8x)",
                    defaultDesc = "切换到快速模式，时间流速加快8倍。",
                    icon = TexCommand.DesirePower,
                    action = () => timeScaleManager.SetFastMode()
                };

                yield return new Command_Action
                {
                    defaultLabel = "显示当前时间挡位",
                    defaultDesc = string.Format("当前挡位: {0}", GetTimeScaleLabel(timeScaleManager.CurrentLevel)),
                    icon = TexButton.Rename,
                    action = () =>
                    {
                        int realTicksAbs = Find.TickManager.TicksGame + Find.TickManager.gameStartAbsTick;
                        double displayTicksAbs = timeScaleManager.GetDisplayTicksAbs();
                        int patchedTicksAbs = Find.TickManager.TicksAbs;
                        
                        Messages.Message(
                            string.Format("当前时间挡位: {0}\n历法倍率: {1:F2}x\n物理倍率: {2:F2}x\n" +
                                "真实TicksAbs: {3}\nDisplayTicksAbs: {4:F2}\nPatchedTicksAbs: {5}\n" +
                                "Patch生效: {6}",
                                GetTimeScaleLabel(timeScaleManager.CurrentLevel),
                                timeScaleManager.CalendarSpeedMultiplier,
                                timeScaleManager.PhysicsSpeedMultiplier,
                                realTicksAbs,
                                displayTicksAbs,
                                patchedTicksAbs,
                                patchedTicksAbs != realTicksAbs),
                            MessageTypeDefOf.NeutralEvent);
                    }
                };
            }
        }

        private string GetTimeScaleLabel(TimeScaleLevel level)
        {
            switch (level)
            {
                case TimeScaleLevel.Combat: return "战斗模式 (1/60x)";
                case TimeScaleLevel.Normal: return "正常模式 (1x)";
                case TimeScaleLevel.Fast: return "快速模式 (8x)";
                default: return "未知";
            }
        }

        // 实现方法
        private void CreateOrbitingGroup()
        {
            if (spaceGame == null) return;

            // 选择轨道半径：例如 150 Unity 单位 = 7,500,000 米
            double radiusUnity = 150.0;
            double radiusMeters = radiusUnity * SpaceConstants.MetersPerUnit;
            // 圆轨速度 = sqrt(GM / r)，使用有效的引力参数兜底值
            double gm = SpaceConstants.PrimaryGravitationalParameter;
            double speedMps = System.Math.Sqrt(gm / radiusMeters);

            // 随机选择一个轨道平面上的位置（简化：在 XZ 平面）
            double angle = Random.Range(0f, 360f) * System.Math.PI / 180.0;
            DVector3 posMeters = new DVector3(System.Math.Cos(angle) * radiusMeters, 0, System.Math.Sin(angle) * radiusMeters);
            // 速度方向垂直于位置矢量（切向）
            DVector3 velDir = new DVector3(-System.Math.Sin(angle), 0, System.Math.Cos(angle)).normalized;
            DVector3 velMeters = velDir * speedMps;

            int groupId = nextGroupId++;
            var group = new SpaceEntityGroup(groupId, factionId: Faction.OfPlayer?.loadID ?? 0);
            group.CelestialBodyId = EARTH_BODY_ID;
            group.CurrentOrbit = new OrbitState
            {
                position = posMeters,
                velocity = velMeters,
                rotation = Quaternion.identity
            };
            group.IsMovable = true;
            spaceGame.RegisterGroup(group);

            // 标记为圆轨模式（更新物理位置时使用解析解）
            group.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            // 添加一个实体（自己作为单个实体集群）
            int entityId = nextEntityId++;
            var entity = new SpaceEntity(entityId, group.FactionId);
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);
            entity.GroupId = groupId;
            entity.CanInteract = true;
            entity.IsMovable = true;
            spaceGame.RegisterEntity(entity);

            if (spaceGame.GetPlanetGrids() != null)
                foreach (var grid in spaceGame.GetPlanetGrids().Values)
                    grid.ApplyChanges();
            Messages.Message(string.Format("Created orbiting group {0} at radius {1} km", groupId, radiusMeters / 1000f), MessageTypeDefOf.TaskCompletion);
        }

        private const int MOON_BODY_ID = 2;

        /// <summary>
        /// 创建绕月圆轨测试集群。
        /// </summary>
        private void CreateLunarOrbitGroup()
        {
            if (spaceGame == null || celestialMgr == null) return;

            var moonDef = celestialMgr.GetBodyById(MOON_BODY_ID);
            if (moonDef == null)
            {
                Messages.Message("Moon (bodyId=2) not found in celestial body definitions.", MessageTypeDefOf.RejectInput);
                return;
            }

            int currentTick = Find.TickManager.TicksGame;
            DVector3 moonPos = celestialMgr.GetBodyLocalPosition(MOON_BODY_ID, currentTick);
            DVector3 moonVel = celestialMgr.GetBodyLocalVelocity(MOON_BODY_ID, currentTick);

            // 绕月轨道半径 36 Unity 单位 = 1,800,000 米（月球半径 1500km + 300km 轨道高度）
            double radiusUnity = 36.0;
            double radiusMeters = radiusUnity * SpaceConstants.MetersPerUnit;
            double speedMps = System.Math.Sqrt(SpaceConstants.PrimaryGravitationalParameter / radiusMeters);

            // 随机起始角度
            double angle = Random.Range(0f, 360f) * System.Math.PI / 180.0;
            // 相对月球的位置偏移
            DVector3 offsetMeters = new DVector3(
                System.Math.Cos(angle) * radiusMeters,
                0,
                System.Math.Sin(angle) * radiusMeters);
            // 相对月球的切向速度
            DVector3 offsetVelDir = new DVector3(
                -System.Math.Sin(angle),
                0,
                System.Math.Cos(angle)).normalized;
            DVector3 relVel = offsetVelDir * speedMps;

            // 转地球参考系
            DVector3 posMeters = moonPos + offsetMeters;
            DVector3 velMeters = moonVel + relVel;

            int groupId = nextGroupId++;
            var group = new SpaceEntityGroup(groupId, factionId: Faction.OfPlayer?.loadID ?? 0);
            group.CelestialBodyId = MOON_BODY_ID;
            group.CurrentOrbit = new OrbitState
            {
                position = posMeters,
                velocity = velMeters,
                rotation = Quaternion.identity
            };
            group.IsMovable = true;
            spaceGame.RegisterGroup(group);

            // 进入绕月圆轨
            group.EnterCircularOrbit(currentTick, MOON_BODY_ID);

            int entityId = nextEntityId++;
            var entity = new SpaceEntity(entityId, group.FactionId);
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);
            entity.GroupId = groupId;
            entity.CanInteract = true;
            entity.IsMovable = true;
            spaceGame.RegisterEntity(entity);

            if (spaceGame.GetPlanetGrids() != null)
                foreach (var grid in spaceGame.GetPlanetGrids().Values)
                    grid.ApplyChanges();
            Messages.Message(string.Format("Created Moon-orbiting group {0} at radius {1} km", groupId, radiusMeters / 1000f), MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// 创建同步轨道测试集群。
        /// 同步轨道半径计算：T = 1 游戏日 = 60,000 ticks = 60,000 s
        /// r = (GM × T² / 4π²)^(⅓) ≈ 526.3 Unity 单位（26,315 km）
        /// 物理角速度 = 0.006°/tick，视觉自转 = 360°/60,000 tick = 0.006°/tick
        /// 两者方向相反正好抵消 → 卫星在世界视图中静止（模拟地球同步效果）
        /// </summary>
        private void CreateSyncOrbitTest()
        {
            if (spaceGame == null) return;

            // 同步轨道半径：526.3 Unity 单位 = 26,315,000 米
            double radiusUnity = 526.3;
            double radiusMeters = radiusUnity * SpaceConstants.MetersPerUnit;
            double speedMps = System.Math.Sqrt(SpaceConstants.PrimaryGravitationalParameter / radiusMeters);

            // 固定从 +X 轴起始，方便验证位置
            DVector3 posMeters = new DVector3(radiusMeters, 0, 0);
            DVector3 velDir = new DVector3(0, 0, 1).normalized; // 向 +Z（切向 → 逆时针轨道）
            DVector3 velMeters = velDir * speedMps;

            int groupId = nextGroupId++;
            var group = new SpaceEntityGroup(groupId, factionId: Faction.OfPlayer?.loadID ?? 0);
            group.CelestialBodyId = EARTH_BODY_ID;
            group.CurrentOrbit = new OrbitState
            {
                position = posMeters,
                velocity = velMeters,
                rotation = Quaternion.identity
            };
            group.IsMovable = true;
            spaceGame.RegisterGroup(group);

            // 标记该 Group 为圆轨模式
            group.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            int entityId = nextEntityId++;
            var entity = new SpaceEntity(entityId, group.FactionId);
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);
            entity.GroupId = groupId;
            entity.CanInteract = true;
            entity.IsMovable = true;
            spaceGame.RegisterEntity(entity);

            if (spaceGame.GetPlanetGrids() != null)
                foreach (var grid in spaceGame.GetPlanetGrids().Values)
                    grid.ApplyChanges();
            Messages.Message(string.Format("Sync test group {0} at r={1:F0} km (sync orbit = 26,315 km ✓)",
                groupId, radiusMeters / 1000f), MessageTypeDefOf.TaskCompletion);
        }

        private void CreateRandomEntity(int groupId)
        {
            if (spaceGame == null) return;

            int entityId = nextEntityId++;
            var entity = new SpaceEntity(entityId, factionId: Faction.OfPlayer?.loadID ?? 0);

            // 随机位置范围：-10,000 km 到 10,000 km → -10,000,000 米 到 10,000,000 米
            DVector3 posMeters = new DVector3(
                Random.Range(-10_000_000f, 10_000_000f),
                Random.Range(-10_000_000f, 10_000_000f),
                Random.Range(-10_000_000f, 10_000_000f));
            // 随机速度：-5 km/s 到 5 km/s → -5,000 米/秒 到 5,000 米/秒
            DVector3 velMeters = new DVector3(
                Random.Range(-5000f, 5000f),
                Random.Range(-5000f, 5000f),
                Random.Range(-5000f, 5000f));
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);
            entity.CanInteract = true;
            entity.IsSelectable = true;
            entity.IsMovable = true;

            int finalGroupId;
            if (groupId == 0)
            {
                finalGroupId = nextGroupId++;
                var group = new SpaceEntityGroup(finalGroupId, entity.FactionId);
                group.CelestialBodyId = EARTH_BODY_ID;
                group.CurrentOrbit = new OrbitState
                {
                    position = posMeters,
                    velocity = velMeters,
                    rotation = Quaternion.identity
                };
                group.IsMovable = true;
                spaceGame.RegisterGroup(group);
                entity.GroupId = finalGroupId;
                group.AddEntity(entityId);
                Messages.Message(string.Format("Auto-created group {0} for entity {1}", finalGroupId, entityId), MessageTypeDefOf.TaskCompletion);
            }
            else
            {
                var group = spaceGame.GetGroupById(groupId);
                if (group == null)
                {
                    Messages.Message(string.Format("Group {0} does not exist. Cannot add entity.", groupId), MessageTypeDefOf.RejectInput);
                    return;
                }
                finalGroupId = groupId;
                entity.GroupId = finalGroupId;
                group.AddEntity(entityId);
                entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
                Messages.Message(string.Format("Added entity {0} to existing group {1}", entityId, finalGroupId), MessageTypeDefOf.TaskCompletion);
            }

            spaceGame.RegisterEntity(entity);
            if (spaceGame.GetPlanetGrids() != null)
                foreach (var grid in spaceGame.GetPlanetGrids().Values)
                    grid.ApplyChanges();
        }

        private void CreateRandomGroup()
        {
            if (spaceGame == null) return;

            int groupId = nextGroupId++;
            var group = new SpaceEntityGroup(groupId, factionId: Faction.OfPlayer?.loadID ?? 0);
            group.CelestialBodyId = EARTH_BODY_ID;
            DVector3 centerMeters = new DVector3(
                Random.Range(-10_000_000f, 10_000_000f),
                Random.Range(-10_000_000f, 10_000_000f),
                Random.Range(-10_000_000f, 10_000_000f));
            DVector3 velMeters = new DVector3(
                Random.Range(-3000f, 3000f),
                Random.Range(-3000f, 3000f),
                Random.Range(-3000f, 3000f));
            group.CurrentOrbit = new OrbitState
            {
                position = centerMeters,
                velocity = velMeters,
                rotation = Quaternion.identity
            };
            group.IsMovable = true;
            spaceGame.RegisterGroup(group);

            for (int i = 0; i < 3; i++)
            {
                int entityId = nextEntityId++;
                var entity = new SpaceEntity(entityId, group.FactionId);
                DVector3 offsetMeters = new DVector3(
                    Random.Range(-200_000f, 200_000f),
                    Random.Range(-200_000f, 200_000f),
                    Random.Range(-200_000f, 200_000f));
                // 局部偏移 = 实体相对 group 原点的偏移；速度局部为零（随 group 运动）
                entity.SetPositionVelocity(offsetMeters, DVector3.zero);
                entity.SetRotation(Quaternion.identity);
                entity.GroupId = groupId;
                entity.CanInteract = true;
                entity.IsMovable = true;
                spaceGame.RegisterEntity(entity);
                group.AddEntity(entityId);
            }
            if (spaceGame.GetPlanetGrids() != null)
                foreach (var grid in spaceGame.GetPlanetGrids().Values)
                    grid.ApplyChanges();
            Messages.Message(string.Format("Created group {0} with 3 entities", groupId), MessageTypeDefOf.TaskCompletion);
        }

        private void DestroyNearestEntity()
        {
            if (spaceGame == null) return;
            Vector3 myPosUnity = parent.DrawPos;
            DVector3 myPosMeters = (DVector3)myPosUnity * SpaceConstants.MetersPerUnit;
            SpaceEntity nearest = null;
            double minDistSq = double.MaxValue;
            foreach (var ent in spaceGame.GetAllEntities())
            {
                double dsq = (ent.Position - myPosMeters).sqrMagnitude;
                if (dsq < minDistSq)
                {
                    minDistSq = dsq;
                    nearest = ent;
                }
            }
            if (nearest != null)
            {
                int id = nearest.EntityId;
                spaceGame.UnregisterEntity(nearest);
                if (spaceGame.GetPlanetGrids() != null)
                    foreach (var grid in spaceGame.GetPlanetGrids().Values)
                        grid.ApplyChanges();
                Messages.Message(string.Format("Destroyed entity {0}", id), MessageTypeDefOf.TaskCompletion);
            }
            else
            {
                Messages.Message("No entities to destroy.", MessageTypeDefOf.RejectInput);
            }
        }

        private void DestroyAllEntities()
        {
            if (spaceGame == null) return;
            foreach (var ent in spaceGame.GetAllEntities().ToList())
                spaceGame.UnregisterEntity(ent);
            foreach (var grp in spaceGame.GetAllGroups().ToList())
                spaceGame.UnregisterGroup(grp);
            if (spaceGame.GetPlanetGrids() != null)
                foreach (var grid in spaceGame.GetPlanetGrids().Values)
                    grid.ApplyChanges();
            Messages.Message("All entities and groups destroyed.", MessageTypeDefOf.TaskCompletion);
        }

        private void CreateSpaceStation()
        {
            if (spaceGame == null) return;

            int entityId = nextEntityId++;
            int groupId = nextGroupId++;
            double radiusUnity = 150.0;
            double radiusMeters = radiusUnity * SpaceConstants.MetersPerUnit;
            double speedMps = System.Math.Sqrt(SpaceConstants.PrimaryGravitationalParameter / radiusMeters);
            double angle = Random.Range(0f, 360f) * System.Math.PI / 180.0;
            DVector3 posMeters = new DVector3(System.Math.Cos(angle) * radiusMeters, 0, System.Math.Sin(angle) * radiusMeters);
            DVector3 velDir = new DVector3(-System.Math.Sin(angle), 0, System.Math.Cos(angle)).normalized;
            DVector3 velMeters = velDir * speedMps;

            var station = spaceGame.CreateStation(entityId, groupId, parent.Faction?.loadID ?? 0, posMeters, 200, 200, "TestStation");
            if (station == null)
            {
                Messages.Message("Failed to create space station.", MessageTypeDefOf.RejectInput);
                return;
            }

            // 配置实体交互属性（CreateStation 不会设置这些）
            station.CanInteract = true;
            station.IsSelectable = true;
            station.IsMovable = true;

            // 配置群组为圆轨模式 + 绑定地球
            var group = spaceGame.GetGroupById(groupId);
            if (group != null)
            {
                group.CelestialBodyId = EARTH_BODY_ID;
                group.CurrentOrbit = new OrbitState
                {
                    position = posMeters,
                    velocity = velMeters,
                    rotation = Quaternion.identity
                };
                group.IsMovable = true;
                group.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);
            }

            if (spaceGame.GetPlanetGrids() != null)
                foreach (var grid in spaceGame.GetPlanetGrids().Values)
                    grid.ApplyChanges();
            Messages.Message(string.Format("Space station created with map ID {0}", station.MapId), MessageTypeDefOf.TaskCompletion);
        }

        private void PrintStats()
        {
            if (spaceGame == null) return;
            int entityCount = spaceGame.GetAllEntities().Count();
            int groupCount = spaceGame.GetAllGroups().Count();
            Log.Message(string.Format(
                "=== PioneerFramework Stats ===\n" +
                "Orbital Entities: {0} | Orbital Groups: {1}",
                entityCount, groupCount));
            var first = spaceGame.GetAllEntities().FirstOrDefault();
            if (first != null)
                Log.Message(string.Format("First orbital entity position (meters): {0}", first.Position));
            int totalCells = 0;
            var planetGrids = spaceGame.GetPlanetGrids();
            if (planetGrids != null)
            {
                foreach (var kv in planetGrids)
                {
                    kv.Value.ApplyChanges();
                    totalCells += kv.Value.CellCount;
                }
                Log.Message(string.Format("Non-empty grid cells: {0}", totalCells));
            }
        }

        /// <summary>
        /// 列出从 XML 加载的所有天体定义，弹窗显示。
        /// </summary>
        private void ListCelestialBodies()
        {
            var manager = Current.Game.GetComponent<CelestialBodyManager>();
            if (manager == null)
            {
                Messages.Message("CelestialBodyManager not found.", MessageTypeDefOf.RejectInput);
                return;
            }

            var bodies = manager.GetAllBodies();
            if (bodies.Count == 0)
            {
                Messages.Message("No celestial bodies registered. Check XML Defs.", MessageTypeDefOf.RejectInput);
                return;
            }

            // 构建显示文本
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Celestial Bodies ===\n");

            foreach (var body in bodies)
            {
                sb.AppendLine(string.Format("  [{0}] {1} ({2})", body.bodyId, body.label, body.defName));
                sb.AppendLine(string.Format("    Type: {0}", body.bodyType));
                sb.AppendLine(string.Format("    Radius: {0:N0} m  |  GM: {1:E3} m³/s²", body.radius, body.gravitationalParameter));
                sb.AppendLine(string.Format("    HasSurface: {0}  |  Atmosphere: {1:N0} m", body.hasSurface, body.atmosphereThickness));

                if (body.HasOrbit)
                {
                    sb.AppendLine(string.Format("    Orbit Radius: {0:N0} m", body.orbitRadius));
                    sb.AppendLine(string.Format("    Orbit Period: {0:N0} ticks", body.orbitPeriod));
                    sb.AppendLine(string.Format("    Orbit Normal: {0}", body.orbitNormal));
                    sb.AppendLine(string.Format("    Initial Phase: {0:F2} rad", body.initialPhase));
                    string parentName = body.ParentBodyId >= 0
                        ? manager.GetBodyName(body.ParentBodyId)
                        : "(directly around star)";
                    sb.AppendLine(string.Format("    Parent: {0}", parentName));
                }
                sb.AppendLine();
            }

            sb.AppendLine(string.Format("Total: {0} bodies", bodies.Count));

            // 同时输出到日志
            Log.Message(sb.ToString());

            // 弹窗显示
            Find.WindowStack.Add(new Dialog_MessageBox(sb.ToString(), "OK"));
        }
    }
}