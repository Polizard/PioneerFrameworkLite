using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using PioneerFramework.Combat;

namespace PioneerFramework
{

    public class CompProperties_MapDesignator : CompProperties
    {
        /// <summary>统一齐射冷却（tick）。</summary>
        public int cooldownTicks = 60;

        /// <summary>索敌扫描间隔（tick）。</summary>
        public int scanIntervalTicks = 32;


        public CompProperties_MapDesignator()
        {
            compClass = typeof(CompMapDesignator);
        }
    }

    /// <summary>定位机 → 炮塔的绑定记录（整体式，无上下之分）。</summary>
    public class TurretBinding : IExposable
    {
        public Thing turret = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        public void ExposeData()
        {
            Scribe_References.Look(ref turret, "turret");
        }
    }

    /// <summary>
    /// Map 定位机建筑：集中索敌大脑。管理一组绑定炮塔，统一冷却、统一齐射。
    /// 模式：Manual（指定敌对 group）/ Auto（最近敌对 group）。
    /// 射界 = 360° 无死角：目标在船体局部任意方位（上/下/前/后/左/右）都能开火，
    /// 射弹出膛点 = 炮口射线与本船 AABB 六面的碰撞点（与 F3 命中几何对称）。
    /// </summary>
    public class CompMapDesignator : ThingComp
    {
        private List<TurretBinding> boundMounts = new();
        private int manualTargetGroupId = -1;
        private readonly GameComponentRef<SensorManager> sensorMgrRef = new();   // [V3] 认知层
        private int cooldownTicks;
        private bool autoFire = true;
        private int nextScanTick;
        private SpaceEntityGroup? currentTarget;
        // 精确炮击：瞄准目标船 Map 指定格对应的世界点（偏移船心），参考 SOS2but2D preciseTargetCell
        private int preciseStrikeGroupId = -1;
        private IntVec3 preciseStrikeCell = IntVec3.Invalid;
        // 测试用：目标格上的红色标记 Mote（生命周期随精确炮击点）
        private Mote preciseStrikeMarker = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        public CompProperties_MapDesignator Props => (CompProperties_MapDesignator)props;
        public bool AutoFire => autoFire;
        public IReadOnlyList<TurretBinding> BoundMounts => boundMounts;
        public bool HasPreciseStrike => preciseStrikeGroupId >= 0 && preciseStrikeCell.IsValid;
        public int PreciseStrikeGroupId => preciseStrikeGroupId;
        public IntVec3 PreciseStrikeCell => preciseStrikeCell;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);

            // 一体式建筑（导弹发射器等自带炮塔）：定位机与同建筑炮塔自动绑定，免去玩家手动绑定
            var ownTurret = parent?.TryGetComp<CompMapTurret>();
            if (ownTurret != null && ownTurret.Mount != null)
            {
                RegisterTurret(parent);
                ownTurret.Mount.boundDesignator = parent;
            }
        }

        public override void CompTick()
        {
            base.CompTick();
            if (parent == null || parent.Destroyed || parent.Map == null) return;

            if (cooldownTicks > 0) cooldownTicks--;

            // 精确炮击目标失效（被毁/失活）时自动清除，避免残留瞄准点
            if (preciseStrikeGroupId >= 0)
            {
                var pg = Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(preciseStrikeGroupId);
                if (pg == null || !pg.IsActive)
                    ClearPreciseStrike(false);
            }

            // 测试用：在被精确瞄准的目标格子上保持红色标记
            if (HasPreciseStrike)
                EnsurePreciseStrikeMarker();

            if (Find.TickManager.TicksGame >= nextScanTick)
            {
                var prevTargetGroupId = currentTarget?.GroupId ?? -1;
                currentTarget = AcquireTarget();
                nextScanTick = Find.TickManager.TicksGame + Props.scanIntervalTicks;
                // 目标变化（Auto 换锁 / 手动改指）→ 同步已释放导弹的跟随目标（Following 态）
                int newTargetGroupId = currentTarget?.GroupId ?? -1;
                if (newTargetGroupId != prevTargetGroupId && newTargetGroupId >= 0)
                    SyncMissileFollowers(newTargetGroupId);
            }

            // 炮塔朝向每 4 tick 更新一次（对齐 SOS2 的 4 tick 限频：旋转是平滑展示，每 tick 更新浪费）
            if (currentTarget != null && (Find.TickManager.TicksGame & 3) == 0)
                UpdateMountRotations(currentTarget);

            if (autoFire && cooldownTicks <= 0 && currentTarget != null && boundMounts.Count > 0)
                FireVolley(currentTarget);
        }

        // ── 索敌 ──

        /// <summary>
        /// 目标来源 = **被指定**（[N]：删掉"没分配我打谁那我打谁"的自动分配，2026-10-02 S3b）。
        /// 没有指定 ⇒ 返回 null ⇒ **不开火**；并**开火前过认知层**（`CanEngage`，与视图同一条判据）。
        /// </summary>
        private SpaceEntityGroup? AcquireTarget()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var t = sg?.GetGroupById(manualTargetGroupId);
            if (t == null || !t.IsActive || !IsHostile(t) || !IsInRange(t)) return null;
            return CanEngage(t) ? t : null;
        }

        /// <summary>
        /// **[V3] 认知层准入**（与视图同一条判据）：玩家此刻**看得见**才能打。
        /// 影子也算（允许打"最后已知位置"）；dev 真值开关**不**在此放行（它只改画/点）。
        /// 认知层缺位 ⇒ 放行（= 旧行为）。
        /// </summary>
        private bool CanEngage(SpaceEntityGroup g)
        {
            var sensor = sensorMgrRef.Get();
            var own = GetMyGroup();
            return own != null && (sensor == null || sensor.TryGetKnownState(own.FactionId, g, out _));
        }

        private bool IsHostile(SpaceEntityGroup g)
        {
            var myGroup = GetMyGroup();
            if (myGroup == null) return false;
            // [V12] 2026-09-23：**真阵营语义**（`Faction.HostileTo`，含附庸/同盟）；
            // 解析失败按"未知即敌"处理 —— 见 `FactionResolver` 的语义与读数说明。
            return FactionResolver.IsHostile(g.FactionId, myGroup.FactionId);
        }

        private bool IsInRange(SpaceEntityGroup g)
        {
            var cm = GetCombatManager();
            var myGroup = GetMyGroup();
            if (cm == null || myGroup == null) return false;
            return (GetAimTargetPosition(g) - cm.GetGroupPos(myGroup)).magnitude <= GetEffectiveRange();
        }

        /// <summary>本定位机射程 = 全部绑定炮塔武器 orbitalEngageRange 的最大值。</summary>
        private float GetEffectiveRange()
        {
            float range = 0f;
            foreach (var b in boundMounts)
            {
                var weapon = b.turret?.TryGetComp<CompMapTurret>()?.Mount?.weapon;
                if (weapon != null && weapon.def != null)
                    range = System.Math.Max(range, (float)PioneerFramework.SpaceAI.ContactTargetService.EffectiveRange(weapon));
            }
            return range > 0f ? range : 100000f;
        }

        // ── 本船链路：Map → ownerEntity → Group ──

        private SpaceEntityGroup? GetMyGroup()
        {
            var entity = GetMyEntity();
            if (entity == null) return null;
            return Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(entity.GroupId);
        }

        private SpaceEntity? GetMyEntity()
        {
            if (parent.Map == null) return null;
            var mapComp = parent.Map.GetComponent<SpaceMapComp>();
            if (mapComp == null || mapComp.ownerEntityId < 0) return null;
            return Current.Game?.GetComponent<SpaceGameComp>()?.GetEntityById(mapComp.ownerEntityId);
        }

        private CombatManager GetCombatManager() => Current.Game?.GetComponent<CombatManager>();

        // ── 精确炮击 ──

        /// <summary>
        /// 实际瞄准点（世界）：默认目标船心；精确炮击时为目标船内指定格对应的世界点。
        /// 格 → 船体局部米（y=0）→ 世界（含目标姿态旋转），瞄准随目标船运动/旋转保持在该点。
        /// </summary>
        private DVector3 GetAimTargetPosition(SpaceEntityGroup target)
        {
            DVector3 centerPos = GetCombatManager()?.GetGroupPos(target) ?? DVector3.zero;
            var own = GetMyGroup();
            var sensor = sensorMgrRef.Get();
            if (own != null && sensor != null && sensor.TryGetKnownState(own.FactionId, target, out var known))
                centerPos = ContactMemory.CoastedPosition(known, TimeScaleManager.GetPhysicsTickSafe());
            if (preciseStrikeGroupId != target.GroupId || !preciseStrikeCell.IsValid) return centerPos;

            var tEntity = GetTargetEntity(target);
            if (tEntity == null || tEntity.MapId < 0) return centerPos;
            Map tMap = SpaceMapGenerator.FindPocketMap(tEntity.MapId);
            if (tMap == null) return centerPos;

            Vector3 localPoint = MapCoordMapper.MapCellToShip(tMap, preciseStrikeCell);
            localPoint.y = 0f; // 地面目标（爆炸层）
            return MapCoordMapper.ShipToSystem(tEntity, centerPos, localPoint);
        }

        /// <summary>取目标 group 中第一个带 Map 的实体（空间站本体；无 Map 目标如导弹/无人机返回 null）。</summary>
        private SpaceEntity? GetTargetEntity(SpaceEntityGroup? target)
        {
            if (target == null) return null;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) return null;
            foreach (int eid in target.GetEntityIds())
            {
                var e = sg.GetEntityById(eid);
                if (e != null && e.MapId >= 0) return e;
            }
            return null;
        }

        /// <summary>清除精确炮击点，恢复瞄准船心。notify=true 时弹游戏内消息。</summary>
        public void ClearPreciseStrike(bool notify)
        {
            preciseStrikeGroupId = -1;
            preciseStrikeCell = IntVec3.Invalid;
            DestroyPreciseStrikeMarker();
            if (notify)
                Messages.Message("精确炮击点已清除，恢复瞄准船心。", MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>测试用：在被精确瞄准的目标地图格子上保持红色标记（无标记或已销毁则补一个）。</summary>
        private void EnsurePreciseStrikeMarker()
        {
            if (preciseStrikeMarker != null && !preciseStrikeMarker.Destroyed) return;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var target = sg?.GetGroupById(preciseStrikeGroupId);
            var tEntity = GetTargetEntity(target);
            if (tEntity == null || tEntity.MapId < 0) return;
            Map tMap = SpaceMapGenerator.FindPocketMap(tEntity.MapId);
            if (tMap == null || !preciseStrikeCell.InBounds(tMap)) return;
            var def = DefDatabase<ThingDef>.GetNamedSilentFail("PF_Mote_PreciseStrike");
            if (def == null) return;
            // makeOffscreen=true：即使玩家相机不在目标地图上也要生成（标记应持续存在）
            preciseStrikeMarker = MoteMaker.MakeStaticMote(preciseStrikeCell.ToVector3Shifted(), tMap, def, 1f, true, 0f);
        }

        /// <summary>销毁精确炮击标记（若有）。</summary>
        private void DestroyPreciseStrikeMarker()
        {
            if (preciseStrikeMarker != null && !preciseStrikeMarker.Destroyed)
                preciseStrikeMarker.Destroy();
            preciseStrikeMarker = null;
        }

        // ── 开火 ──

        /// <summary>
        /// 统一齐射：360° 无射界过滤，各绑定炮塔朝目标开火。
        /// 出膛点 = 炮口射线与本船 AABB 六面求交的碰撞点；物理射弹在该点生成（Map→3D 转换），
        /// 与 F3（3D 命中 → Map 贯穿弹）几何对称。
        /// </summary>
        private void FireVolley(SpaceEntityGroup target)
        {
            // 开火对齐物理步（偶数 tick）：射弹生成与物理更新/降速边界同拍，
            // 射弹首次 deltaTime 恒为 Combat 档 2/60，杜绝跨物理步的大步进。
            if ((Find.TickManager.TicksGame & 1) != 0) return;

            var cm = GetCombatManager();
            var entity = GetMyEntity();
            var myGroup = GetMyGroup();
            if (cm == null || entity == null || myGroup == null) return;
            if (!CanEngage(target)) return;      // [V3] 开火前过认知层（唯一漏斗；`AcquireTarget` 之外还有炮击/手动路径）

            DVector3 myPos = cm.GetGroupPos(myGroup);
            DVector3 targetPos = GetAimTargetPosition(target);
            DVector3 targetVel = cm.GetGroupVelocity(target);
            var sensor = sensorMgrRef.Get();
            if (sensor != null)
            {
                if (!sensor.TryGetKnownState(myGroup.FactionId, target, out var known)) return;
                targetVel = known.velocity;
            }

            // 精确炮击日志（详细日志）：瞄准点相对船心的偏移量
            if (CombatManager.DebugDetailedCombat && preciseStrikeGroupId == target.GroupId && HasPreciseStrike)
            {
                DVector3 centerPos = cm.GetGroupPos(target);
                DVector3 offset = targetPos - centerPos;
                Log.Message($"[精确炮击] G{target.GroupId} 瞄准格=({preciseStrikeCell.x},{preciseStrikeCell.z}) 偏移船心=({offset.x:F0},{offset.y:F0},{offset.z:F0})m 世界瞄准点=({targetPos.x:F0},{targetPos.y:F0},{targetPos.z:F0})m");
            }

            // 齐射详情（详细日志）：目标状态 + 绑定炮塔数，一次齐射一行
            if (CombatManager.DebugDetailedCombat)
            {
                Log.Message($"[齐射详情] G{myGroup.GroupId} 目标G{target.GroupId} 距离={(targetPos - myPos).magnitude / 1000.0:F1}km 目标位置=({targetPos.x:F0},{targetPos.y:F0},{targetPos.z:F0}) 目标速度=({targetVel.x:F0},{targetVel.y:F0},{targetVel.z:F0}) 绑定炮塔={boundMounts.Count} 冷却={cooldownTicks} TicksGame={Find.TickManager.TicksGame}");
            }

            bool anyFired = false;
            foreach (var binding in boundMounts)
            {
                if (binding.turret == null || binding.turret.Destroyed) continue;
                var turretComp = binding.turret.TryGetComp<CompMapTurret>();
                var mount = turretComp?.Mount;
                if (mount == null || mount.weapon == null || !mount.weapon.CanFire()) continue;

                // 炮口局部位置：地图格 → 船体局部（整体式炮塔，y=0，无上下表面偏移）
                Vector3 muzzleShip = MapCoordMapper.MapCellToShip(parent.Map, binding.turret.Position);

                // 目标方向（船体局部系，指向目标当前位置；提前量由 FireWeapon 以出膛点为基准解算）
                Vector3 targetShip = MapCoordMapper.SystemToShip(entity, myPos, targetPos);
                Vector3 dirShip = targetShip - muzzleShip;
                if (dirShip.sqrMagnitude < 1f) continue; // 目标与炮口重合（<1m）跳过
                dirShip.Normalize();

                // 用户裁定（2026-10-18）：炮弹从**开火 part 的炮塔位置**直接出来，不做船壳出膛点；
                // 若因此打中自己，后果自负。不要再加 AABB/OBB 出膛修正。
                Vector3 exitShip = muzzleShip;
                DVector3 exitSystem = MapCoordMapper.ShipToSystem(entity, myPos, exitShip);

                // 炮塔详情（详细日志）：炮口/目标局部坐标 + 出射距离（part 直出 ⇒ 应恒 0）
                if (CombatManager.DebugDetailedCombat)
                {
                    Log.Message($"[炮塔详情] G{myGroup.GroupId}→G{target.GroupId} 炮口局部=({muzzleShip.x:F1},{muzzleShip.y:F1},{muzzleShip.z:F1}) 目标局部=({targetShip.x:F1},{targetShip.y:F1},{targetShip.z:F1}) dirShip=({dirShip.x:F3},{dirShip.y:F3},{dirShip.z:F3}) 出射距离={(exitShip - muzzleShip).magnitude:F2}m [part 直出]");
                }

                cm.FireFromMapBuilding(myGroup.GroupId, mount.weapon, exitSystem, targetPos, targetVel, target.GroupId);

                // 出膛诊断日志：验证六面出膛（射界解除 + 碰撞点准确）+ 目标距离与武器状态
                Log.Message($"[炮塔出膛] G{myGroup.GroupId}→G{target.GroupId} 炮塔({binding.turret.Position.x},{binding.turret.Position.z}) 出膛=part直出 局部=({exitShip.x:F1},{exitShip.y:F1},{exitShip.z:F1})m 世界=({exitSystem.x:F0},{exitSystem.y:F0},{exitSystem.z:F0})m 距离={(targetPos - myPos).magnitude / 1000.0:F1}km 冷却={mount.weapon.cooldownRemaining} 过热={mount.weapon.isOverheated} 弹={mount.weapon.ammo}");

                // Map 出膛视觉弹：从炮塔格飞向出膛点映射格（纯视觉叙事，不伤害己方建筑）
                SpawnMuzzleShell(parent.Map, muzzleShip, exitShip);

                // 开火表现：炮口一道长烟（沿炮口朝向延伸），替代闪光（玩家地图可见）
                if (binding.turret.Map != null)
                {
                    var smokeDef = DefDatabase<ThingDef>.GetNamedSilentFail("Mote_Smoke");
                    if (smokeDef != null)
                    {
                        var smoke = MoteMaker.MakeStaticMote(binding.turret.DrawPos + Vector3.up * 0.5f,
                            binding.turret.Map, smokeDef, 1f);
                        if (smoke != null)
                        {
                            // 拉长成一道长烟，朝向 = 炮口目标方向角（船体局部系，与贴图一致）
                            smoke.linearScale = new Vector3(1.6f, 1f, 0.35f);
                            smoke.exactRotation = mount.rotation;
                        }
                    }
                }
                anyFired = true;
            }

            if (anyFired) cooldownTicks = Props.cooldownTicks;
        }

        /// <summary>
        /// Map 内出膛视觉弹（F2 镜像 F3 贯穿弹）：从炮塔格（y=0）沿瞄准方向飞向出膛点映射格。
        /// 纯视觉：不碰撞己方建筑、落点无伤害（PFProjectile3D.visualOnly）。
        /// </summary>
        private void SpawnMuzzleShell(Map map, Vector3 muzzleShip, Vector3 exitShip)
        {
            if (map == null) return;
            var def = DefDatabase<ThingDef>.GetNamedSilentFail("PF_Projectile_Shell");
            if (def == null) return;

            Vector3 start = MapCoordMapper.ShipToMap(map, muzzleShip);
            Vector3 end = MapCoordMapper.ShipToMap(map, exitShip);
            end.x = Mathf.Clamp(end.x, 0f, map.Size.x - 1f);
            end.z = Mathf.Clamp(end.z, 0f, map.Size.z - 1f);
            end.y = Mathf.Max(end.y, 0f); // 出膛点在下表面（y<0）时夹到地面，避免潜入地图下层

            var proj = (PFProjectile3D)ThingMaker.MakeThing(def);
            if (proj == null) return;
            IntVec3 cell = end.ToIntVec3();
            if (!cell.InBounds(map)) cell = map.Center; // 防御
            GenSpawn.Spawn(proj, cell, map);
            // 时间放大：出膛弹 3D 行程几十米，Map 内表现约 0.25s（速度 = 距离/15 tick）
            float dist = (end - start).magnitude;
            float speed = Mathf.Max(2f, dist / 15f);
            int ticks = Mathf.Max(1, Mathf.CeilToInt(dist / speed));
            proj.Launch3D(start, end, cell, visualOnly: true, speedOverride: speed);

            // 出膛视觉弹详情（详细日志）：起止格 + 距离/时长/速度
            if (CombatManager.DebugDetailedCombat)
            {
                IntVec3 s0 = start.ToIntVec3();
                Log.Message($"[出膛视觉弹] G起格=({s0.x},{s0.z})y={start.y:F0} 止格=({cell.x},{cell.z})y={end.y:F0} 距离={dist:F1}m 时长={ticks}tick({ticks / 60f:F2}s) 速度={speed:F1}/tick visualOnly");
            }
        }

        /// <summary>驱动所有绑定炮塔朝向目标（船体局部系水平角，地图贴图直接可用）。</summary>
        private void UpdateMountRotations(SpaceEntityGroup target)
        {
            var entity = GetMyEntity();
            var cm = GetCombatManager();
            var myGroup = GetMyGroup();
            if (entity == null || cm == null || myGroup == null) return;

            DVector3 myPos = cm.GetGroupPos(myGroup);
            DVector3 targetPos = GetAimTargetPosition(target);

            foreach (var binding in boundMounts)
            {
                if (binding.turret == null || binding.turret.Destroyed) continue;
                var mount = binding.turret.TryGetComp<CompMapTurret>()?.Mount;
                if (mount == null) continue;

                Vector3 dirShip = MapCoordMapper.SystemToShip(entity, myPos, targetPos);
                float angle = Mathf.Atan2(dirShip.x, dirShip.z) * Mathf.Rad2Deg;
                if (angle < 0f) angle += 360f;
                mount.rotation = angle;
            }
        }

        /// <summary>索敌目标变化 → 同步本定位机（发射器）释放的 Following 导弹改指新目标（2026-09-06）。</summary>
        private void SyncMissileFollowers(int newTargetGroupId)
        {
            var avm = Current.Game?.GetComponent<AutonomousVehicleManager>();
            var myGroup = GetMyGroup();
            if (avm == null || myGroup == null) return;
            avm.SyncFollowerTargets(myGroup.GroupId, newTargetGroupId);
        }

        // ── 绑定 ──

        public bool RegisterTurret(Thing? turret)
        {
            if (turret == null || turret.Destroyed) return false;
            foreach (var b in boundMounts)
                if (b.turret == turret) return true; // 已绑定
            boundMounts.Add(new TurretBinding { turret = turret });
            return true;
        }

        public void UnregisterTurret(Thing turret)
        {
            for (int i = boundMounts.Count - 1; i >= 0; i--)
                if (boundMounts[i].turret == turret)
                    boundMounts.RemoveAt(i);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode)
        {
            base.PostDeSpawn(map, mode);
            // 解绑所有炮塔的反向引用
            foreach (var b in boundMounts)
            {
                var mount = b.turret?.TryGetComp<CompMapTurret>()?.Mount;
                if (mount != null && mount.boundDesignator == parent)
                    mount.boundDesignator = null;
            }
            boundMounts.Clear();
            // 定位机拆除：清理精确炮击标记
            DestroyPreciseStrikeMarker();
        }

        // ── Gizmo ──

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra()) yield return g;
            if (parent.Faction != Faction.OfPlayer) yield break;

            // 目标来源只剩"指定"（[N]）⇒ 不再有"索敌模式"开关
            yield return new Command_Action
            {
                defaultLabel = "指定目标",
                defaultDesc = "选择一个敌对轨道实体作为打击目标。**出厂不自动交战**：没有指定目标就不开火。",
                icon = TexButton.Rename,
                action = ShowTargetPicker
            };

            // 精确炮击：瞄准敌舰 Map 指定格（偏移船心）
            yield return new Command_Action
            {
                defaultLabel = HasPreciseStrike
                    ? $"精确炮击: G{preciseStrikeGroupId} ({preciseStrikeCell.x},{preciseStrikeCell.z})"
                    : "设置精确炮击点",
                defaultDesc = HasPreciseStrike
                    ? "已瞄准敌舰指定格（重设：跳转目标地图后点选新格）。"
                    : "在敌舰地图上点选一格作为炮击目标，瞄准时从船心偏移到该点（自动锁定该目标为手动目标）。",
                icon = TexButton.Rename,
                action = BeginPrecisionStrike
            };

            if (HasPreciseStrike)
            {
                yield return new Command_Action
                {
                    defaultLabel = "清除精确炮击点",
                    defaultDesc = "恢复瞄准目标船中心。",
                    icon = TexButton.Rename,
                    action = () => ClearPreciseStrike(true)
                };
            }

            yield return new Command_Action
            {
                defaultLabel = "手动开火",
                defaultDesc = "冷却就绪时立即齐射一次。",
                icon = TexButton.Rename,
                action = ManualFire
            };

            yield return new Command_Toggle
            {
                defaultLabel = "自动开火",
                defaultDesc = "开关自动开火。",
                icon = TexButton.Rename,
                isActive = () => autoFire,
                toggleAction = () => autoFire = !autoFire
            };

            if (boundMounts.Count > 0)
            {
                yield return new Command_Action
                {
                    defaultLabel = "绑定炮塔: " + boundMounts.Count,
                    defaultDesc = "已绑定的炮塔数量。",
                    icon = TexButton.Rename,
                    action = delegate { }
                };
            }
        }

        private void ShowTargetPicker()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var cm = GetCombatManager();
            var myGroup = GetMyGroup();
            if (sg == null || cm == null || myGroup == null) return;

            // [V3]（2026-10-11 B1）：列表准入 = **与开火同一条判据**（`PlayerCanSee`；影子算看得见）。
            // 原来这里不筛 ⇒ 玩家能在列表里看到自己**没发现**的敌舰（信息泄漏），选了又打不着。
            var sensor = Current.Game?.GetComponent<SensorManager>();

            var options = new List<FloatMenuOption>();
            DVector3 myPos = cm.GetGroupPos(myGroup);
            foreach (var g in sg.GetAllGroups())
            {
                if (!g.IsActive || g.GroupId == myGroup.GroupId || !IsHostile(g)) continue;
                if (sensor != null && !sensor.PlayerCanSee(g)) continue;
                double d = (cm.GetGroupPos(g) - myPos).magnitude;
                string suffix = sensor != null ? sensor.PlayerPhaseSuffix(g) : "";
                var captured = g;
                options.Add(new FloatMenuOption($"Group {g.GroupId}（{d / 1000.0:F0} km）{suffix}", () =>
                {
                    manualTargetGroupId = captured.GroupId;
                }));
            }
            if (options.Count == 0)
                options.Add(new FloatMenuOption("无敌对目标", null));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        // ── 精确炮击：选点流程 ──

        /// <summary>开始设置精确炮击点：确定目标（手动/当前目标，或从敌舰列表选）后跳转其地图选格。</summary>
        private void BeginPrecisionStrike()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var myGroup = GetMyGroup();
            if (sg == null || myGroup == null) return;

            SpaceEntityGroup? target = null;
            if (manualTargetGroupId >= 0)
                target = sg.GetGroupById(manualTargetGroupId);
            if (target == null) target = currentTarget;
            if (target == null)
            {
                ShowPrecisionTargetPicker();
                return;
            }
            BeginPrecisionPick(target);
        }

        /// <summary>精确炮击目标选择列表（仅列出带 Map 的敌舰）。⚠ 准入同"指定目标"：只列看得见的（2026-10-11 B1）。</summary>
        private void ShowPrecisionTargetPicker()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var cm = GetCombatManager();
            var myGroup = GetMyGroup();
            if (sg == null || cm == null || myGroup == null) return;

            var sensor = Current.Game?.GetComponent<SensorManager>();
            var options = new List<FloatMenuOption>();
            DVector3 myPos = cm.GetGroupPos(myGroup);
            foreach (var g in sg.GetAllGroups())
            {
                if (!g.IsActive || g.GroupId == myGroup.GroupId || !IsHostile(g)) continue;
                if (sensor != null && !sensor.PlayerCanSee(g)) continue;   // [V3] 只列看得见的（影子算）
                if (GetTargetEntity(g) == null) continue; // 只需有 Map 的目标（空间站）
                double d = (cm.GetGroupPos(g) - myPos).magnitude;
                string suffix = sensor != null ? sensor.PlayerPhaseSuffix(g) : "";
                var captured = g;
                options.Add(new FloatMenuOption($"Group {g.GroupId}（{d / 1000.0:F0} km）{suffix}",
                    () => BeginPrecisionPick(captured)));
            }
            if (options.Count == 0)
                options.Add(new FloatMenuOption("无敌对舰船目标", null));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        /// <summary>跳转目标地图并用点选器选取炮击格；同时把定位机切到 Manual 锁定该目标（精确炮击 = 指定目标 + 指定落点）。</summary>
        private void BeginPrecisionPick(SpaceEntityGroup target)
        {
            var tEntity = GetTargetEntity(target);
            if (tEntity == null || tEntity.MapId < 0)
            {
                Messages.Message("目标船没有可用的地图，无法精确炮击。", MessageTypeDefOf.RejectInput);
                return;
            }
            Map targetMap = SpaceMapGenerator.FindPocketMap(tEntity.MapId);
            if (targetMap == null) return;

            manualTargetGroupId = target.GroupId;

            CameraJumper.TryJump(targetMap.Center, targetMap);
            var tp = new TargetingParameters
            {
                canTargetLocations = true,
                canTargetBuildings = false,
                canTargetPawns = false,
                validator = (TargetInfo t) => t.Cell.InBounds(targetMap)
            };
            int capturedGroupId = target.GroupId;
            Map capturedMap = targetMap;
            Find.Targeter.BeginTargeting(tp, (LocalTargetInfo targ) =>
            {
                preciseStrikeGroupId = capturedGroupId;
                preciseStrikeCell = targ.Cell;
                Vector3 local = MapCoordMapper.MapCellToShip(capturedMap, targ.Cell);
                Log.Message($"[精确炮击] 定位机设定目标点: G{capturedGroupId} 格=({targ.Cell.x},{targ.Cell.z}) 局部偏移=({local.x:F1},0,{local.z:F1})m");
                Messages.Message($"精确炮击点已设置: G{capturedGroupId} ({targ.Cell.x},{targ.Cell.z})",
                    MessageTypeDefOf.TaskCompletion);
            }, null, null, null, true);
        }

        /// <summary>调试辅助：直接设定精确炮击点（测试场景用）。</summary>
        public void DebugSetPreciseStrike(int groupId, IntVec3 cell)
        {
            preciseStrikeGroupId = groupId;
            preciseStrikeCell = cell;
            manualTargetGroupId = groupId;
        }

        public void ManualFire()
        {
            if (cooldownTicks > 0) return;
            var t = currentTarget ?? AcquireTarget();
            if (t == null || boundMounts.Count == 0) return;
            FireVolley(t);
        }

        /// <summary>调试辅助：直接指定手动目标（等价于 Gizmo「指定目标」，测试场景用）。</summary>
        public void DebugSetManualTarget(int groupId)
        {
            manualTargetGroupId = groupId;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref boundMounts, "boundMounts", LookMode.Deep);
            Scribe_Values.Look(ref manualTargetGroupId, "manualTargetGroupId", -1);
            Scribe_Values.Look(ref cooldownTicks, "cooldownTicks");
            Scribe_Values.Look(ref autoFire, "autoFire", true);
            Scribe_Values.Look(ref preciseStrikeGroupId, "preciseStrikeGroupId", -1);
            Scribe_Values.Look(ref preciseStrikeCell, "preciseStrikeCell", IntVec3.Invalid);
        }
    }
}
