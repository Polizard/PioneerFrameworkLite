using System;
using System.Collections.Generic;
using UnityEngine;
using RimWorld;
using Verse;
using PioneerFramework.Combat;

namespace PioneerFramework
{
    public class CompProperties_MissileLauncher : CompProperties
    {
        /// <summary>发射武器（Missile 型 WeaponDef）。</summary>
        public string weaponDefName = "PF_Weapon_MissileLauncher";
        /// <summary>Auto 索敌重扫间隔（tick）。</summary>
        public int scanIntervalTicks = 32;

        public CompProperties_MissileLauncher()
        {
            compClass = typeof(CompMissileLauncher);
        }
    }

    /// <summary>
    /// 导弹发射器（独立建筑类型，2026-09-06 重构：不复用炮塔 CompMapTurret / 定位机 CompMapDesignator——
    /// 发射器不是炮塔，没有转动炮头/射界/绑定语义；也不是定位机）。
    /// 职责：
    ///   武器驱动（WeaponInstance：冷却/弹药，载入时恢复状态并绑定 Def）；
    ///   目标管理（Auto = 最近敌对 / Manual = 玩家指定，武器 orbitalEngageRange 射程）；
    ///   出膛发射（出膛点 = 炮口射线 ∩ 本船 AABB 六面，复用 F2 几何 → FireFromMapBuilding，
    ///   释放导弹 Mission=Following——目标跟随本发射器、不立即进终末）；
    ///   跟随同步（发射器换目标 → AVM.SyncFollowerTargets 同步在飞导弹）；
    ///   Gizmo：模式切换 / 指定目标（FloatMenu 敌对列表）/ 手动开火 / 导弹控制面板（本发射器过滤）。
    /// </summary>
    public class CompMissileLauncher : ThingComp
    {
        private WeaponInstance weapon = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        /// <summary>
        /// 武器 Def 指向的 MissileDef（供 UI 显示与发射口径引用）。
        /// 缺失时回退到 `FallbackMissileDef`——**它的字段初始化器就是 Def 的 C# 默认值**，
        /// 故读数口径仍只有一处来源（不在 UI 里再抄一份数字）。
        /// </summary>
        private MissileDef ResolvedMissileDef
        {
            get
            {
                var name = weapon?.def?.missileDefName;
                if (name == null) return FallbackMissileDef;
                return DefDatabase<MissileDef>.GetNamedSilentFail(name) ?? FallbackMissileDef;
            }
        }

        /// <summary>仅用于读 C# 默认值（Def 缺失时的 UI 兜底）；不注册进 DefDatabase，不参与玩法。</summary>
        private static readonly MissileDef FallbackMissileDef = new();
        private SpaceEntityGroup? target;
        // ⚠ [N]（用户裁定"删分配、留执行"，2026-10-02 S3b 落地）：**"自动索敌取最近敌对"已删除**。
        //   目标来源只剩"被指定"（`manualTargetGroupId`，序列化的**意图**）；没有指定 ⇒ **不开火**。
        //   开火时机（`autoFire`）是**执行**，保留：指定了目标之后要不要立刻打，是玩家的战术开关。
        private bool autoFire = true; // 发射档位：true = 自动发射（目标有效+冷却就绪即打）；false = 手动发射（仅手动开火/面板/全体命令触发）
        private int manualTargetGroupId = -1;
        /// <summary>弹群齐射枚数（0 = 跟随武器 Def.missileCount；1..CombatManager.MaxVolleyCount = 发射器覆盖，2026-09-08 §5）。</summary>
        private int volleyCount;
        private int nextScanTick;

        // 性能第 14 条：CompTick 每建筑每 tick 都要取 SpaceGameComp，按 Game 缓存
        private readonly GameComponentRef<SpaceGameComp> spaceGameRef = new();
        private readonly GameComponentRef<SensorManager> sensorMgrRef = new();   // [V3] 认知层

        // ── 精确打击点（§10 弹着偏置，2026-09-07）：瞄准目标地图指定格，本发射器释放的导弹终末撞该格 ──
        private int preciseStrikeGroupId = -1;
        private IntVec3 preciseStrikeCell = IntVec3.Invalid;
        /// <summary>格 → 目标船体局部米（y=0；导弹制导每步随目标旋转换算世界瞄准点；持久化）。</summary>
        private DVector3 preciseAimOffsetShip;
        /// <summary>目标地图上的选格红色标记（瞬态，不持久化）。</summary>
        private Mote preciseStrikeMarker = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        public WeaponInstance Weapon => weapon;
        public SpaceEntityGroup CurrentTarget => target;
        public bool HasPreciseStrike => preciseStrikeGroupId >= 0 && preciseStrikeCell.IsValid;
        public DVector3 PreciseAimOffsetShip => preciseAimOffsetShip;

        /// <summary>生效齐射枚数：发射器覆盖（1..MaxVolleyCount）优先，0 = 跟随武器 Def.missileCount。</summary>
        public int EffectiveVolleyCount()
        {
            if (volleyCount > 0) return Mathf.Min(volleyCount, CombatManager.MaxVolleyCount);
            var wdef = weapon?.def;
            return wdef != null ? Mathf.Clamp(wdef.missileCount, 1, CombatManager.MaxVolleyCount) : 1;
        }

        /// <summary>设置齐射枚数（0 = 跟随武器 Def；1..MaxVolleyCount = 覆盖）。测试场景/调试共用。</summary>
        public void SetVolleyCount(int n)
        {
            volleyCount = n < 0 ? 0 : Mathf.Min(n, CombatManager.MaxVolleyCount);
        }

        public CompProperties_MissileLauncher Props => (CompProperties_MissileLauncher)props;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            parent.Map?.GetComponent<SpaceMapComp>()?.RegisterSpaceWeapon(parent);
            base.PostSpawnSetup(respawningAfterLoad);
            if (weapon == null) BuildWeapon();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Deep.Look(ref weapon, "weapon");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (weapon == null) BuildWeapon();
                else weapon.def = DefDatabase<WeaponDef>.GetNamedSilentFail(Props.weaponDefName);
            }
            // 手动目标/模式 + 精确打击点（2026-09-07 补：此前发射器运行状态不存盘，读档后回 Auto 重扫）
            Scribe_Values.Look(ref autoFire, "autoFire", true);
            Scribe_Values.Look(ref volleyCount, "volleyCount", 0);
            Scribe_Values.Look(ref manualTargetGroupId, "manualTargetGroupId", -1);
            Scribe_Values.Look(ref preciseStrikeGroupId, "preciseStrikeGroupId", -1);
            Scribe_Values.Look(ref preciseStrikeCell, "preciseStrikeCell", IntVec3.Invalid);
            DVector3.Scribe("preciseAimOffsetShip", ref preciseAimOffsetShip);
        }

        private void BuildWeapon()
        {
            var def = DefDatabase<WeaponDef>.GetNamedSilentFail(Props.weaponDefName);
            if (def == null)
            {
                Log.Error($"[导弹发射器] 武器 Def '{Props.weaponDefName}' 未找到");
                return;
            }
            weapon = new WeaponInstance { def = def };
        }

        public override void CompTick()
        {
            base.CompTick();
            if (parent == null || parent.Destroyed || parent.Map == null) return;
            weapon?.Update(Find.TickManager.TicksGame);

            var sg = spaceGameRef.Get();
            if (sg == null) return;

            // 精确打击点维护：目标失活 → 自动清除；目标地图重载（读档/切换）后补选格标记
            if (preciseStrikeGroupId >= 0)
            {
                var pg = sg.GetGroupById(preciseStrikeGroupId);
                if (pg == null || !pg.IsActive)
                    ClearPreciseStrike(false);
                else
                    EnsurePreciseStrikeMarker();
            }

            // 目标失效清理（手动模式清空等待玩家重指；Auto 下轮重扫）
            if (target != null && (sg.GetGroupById(target.GroupId) == null || !target.IsActive))
            {
                target = null;
                manualTargetGroupId = -1;          // 目标没了 ⇒ 意图也清掉（等玩家重新指定；[N]）
            }

            // 索敌 + 目标变化 → 同步在飞导弹的跟随目标
            if (Find.TickManager.TicksGame >= nextScanTick)
            {
                nextScanTick = Find.TickManager.TicksGame + Props.scanIntervalTicks;
                int prevId = target?.GroupId ?? -1;
                target = AcquireTarget(sg);
                int newId = target?.GroupId ?? -1;
                if (newId >= 0 && newId != prevId)
                    SyncFollowerTargets(newId);
            }

            // 自动发射（发射档位 = 自动）：目标有效 + 武器冷却就绪（偶 tick 对齐物理步；WeaponInstance.Fire 冷却节流）
            if (autoFire && target != null && weapon != null && weapon.CanFire() && (Find.TickManager.TicksGame & 1) == 0)
                TryFire(target);
        }

        // ── 索敌 ──

        private SpaceEntityGroup? AcquireTarget(SpaceGameComp sg)
        {
            var cm = GetCombatManager();
            var myGroup = GetMyGroup();
            if (cm == null || myGroup == null) return null;
            DVector3 myPos = cm.GetGroupPos(myGroup);
            // B3：缺 Def 时**报错并明确放弃交战**，不再静默回退 100km（旧值 = WeaponDef 的 C# 默认值，
            // 恰好掩盖"武器未初始化/Def 未找到"这类配置错误，让发射器看起来"能用但从不命中"）。
            if (weapon?.def == null)
            {
                Log.Error($"[导弹发射器] 武器 Def 缺失（{(Props?.weaponDefName ?? "?")}），本组件不参与交战检索");
                return null;
            }
            float range = weapon.def.orbitalEngageRange;

            // 唯一来源 = **被指定**（[N]：删掉"没分配我打谁那我打谁"的自动分配）
            if (manualTargetGroupId < 0) return null;
            var g = sg.GetGroupById(manualTargetGroupId);
            if (g == null || !g.IsActive || !IsHostile(g, myGroup)) return null;
            var sensor = sensorMgrRef.Get();
            if (sensor != null && !sensor.TryGetKnownState(myGroup.FactionId, g, out _)) return null;
            DVector3 knownPos = sensor != null && sensor.TryGetKnownState(myGroup.FactionId, g, out var observed)
                ? ContactMemory.CoastedPosition(observed, TimeScaleManager.GetPhysicsTickSafe()) : cm.GetGroupPos(g);
            if ((knownPos - myPos).magnitude > range) return null;
            // [V3]（S3b）：**开火前过认知层** —— 看不见就不许开火（"分配是意图、捕获是认知"）。
            //   与视图用**同一条**准入判据（`SensorManager.PlayerCanSee`）⇒ 能看到才打得到，不会出现
            //   "列表里没有、却能开火"这种自相矛盾。接触丢而复得 ⇒ 自动继续（与 K6"目标失活即中止"同构）。
            return CanEngage(g) ? g : null;
        }

        /// <summary>
        /// **[V3] 认知层准入**（与视图同一条判据）：玩家此刻**看得见**才能打（"分配是意图、捕获是认知"）。
        /// 影子也算看得见（`PlayerCanSee` 对 Shadow 返回 true）⇒ 允许对"最后已知位置"下令撞击（打影子是玩法）。
        /// ⚠ `SensorManager.DebugShowGroundTruth`（dev 真值开关）**不**在这里放行 —— 它只改画/点，不改战斗。
        /// 认知层缺位（组件没加载）⇒ 放行（= 旧行为），别让"缺组件"变成"全都不许打"。
        /// </summary>
        private bool CanEngage(SpaceEntityGroup g)
        {
            var sensor = sensorMgrRef.Get();
            var own = GetMyGroup();
            return own != null && (sensor == null || sensor.TryGetKnownState(own.FactionId, g, out _));
        }

        private static bool IsHostile(SpaceEntityGroup g, SpaceEntityGroup myGroup)
        {
            // [V12] 2026-09-23：**真阵营语义**（同上，`FactionResolver` 是唯一判定口）
            return FactionResolver.IsHostile(g.FactionId, myGroup.FactionId);
        }

        // ── 发射 ──

        /// <summary>立即发射一次（冷却允许时；手动开火 Gizmo 与调试场景共用）。</summary>
        public void ManualFire()
        {
            if (weapon == null || !weapon.CanFire()) return;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) return;
            TryFire(target ?? AcquireTarget(sg));
        }

        /// <summary>「命令全体导弹」：对本发射器释放的全部在册导弹统一下发指令（撞击当前目标 / 待命 / 自毁）。</summary>
        private void ShowAllMissileCommandMenu()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var avm = Current.Game?.GetComponent<AutonomousVehicleManager>();
            var myGroup = GetMyGroup();
            if (sg == null || avm == null || myGroup == null) return;

            int alive = CountLauncherMissiles(avm, sg, myGroup.GroupId);
            var options = new List<FloatMenuOption>();
            if (alive == 0)
            {
                options.Add(new FloatMenuOption("本发射器暂无在册导弹", null));
            }
            else
            {
                if (target != null && target.IsActive)
                {
                    int tgtId = target.GroupId;
                    var cm = GetCombatManager();
                    double d = cm != null ? (cm.GetGroupPos(target) - cm.GetGroupPos(myGroup)).magnitude / 1000.0 : 0.0;
                    options.Add(new FloatMenuOption($"全体撞击当前目标 G{tgtId}（{d:F0}km）",
                        () => avm.CommandAll(myGroup.GroupId, AutonomousVehicleManager.MissionType.Attack, tgtId)));
                }
                else
                {
                    options.Add(new FloatMenuOption("无索敌目标：先指定/锁定目标再下令全体撞击", null));
                }
                options.Add(new FloatMenuOption("全体待命（中止当前动作，保持轨道待命）",
                    () => avm.CommandAll(myGroup.GroupId, AutonomousVehicleManager.MissionType.Standby)));
                options.Add(new FloatMenuOption("全体自毁（就地引爆全部在册导弹）",
                    () => avm.CommandAll(myGroup.GroupId, AutonomousVehicleManager.MissionType.SelfDestruct)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        /// <summary>本发射器释放且 3D 仍存活的在册导弹数。</summary>
        private static int CountLauncherMissiles(AutonomousVehicleManager avm, SpaceGameComp sg, int launcherGroupId)
        {
            int n = 0;
            for (int i = 0; i < avm.Vehicles.Count; i++)
            {
                var rec = avm.Vehicles[i];
                if (rec.launcherGroupId != launcherGroupId) continue;
                if (sg.GetGroupById(rec.groupId) != null) n++;
            }
            return n;
        }

        /// <summary>手动指定目标（仅敌对；切换 Manual 模式并立即重扫）。调试场景/玩家共用。成功返回 true。</summary>
        public bool SetManualTarget(int groupId)
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var myGroup = GetMyGroup();
            if (sg == null || myGroup == null) return false;
            var g = sg.GetGroupById(groupId);
            if (g == null || !g.IsActive || !IsHostile(g, myGroup))
            {
                Log.Warning($"[导弹发射器] 手动目标 G{groupId} 无效（不存在/失活/非敌对）");
                return false;
            }
            manualTargetGroupId = groupId;
            target = g;
            SyncFollowerTargets(groupId);
            Log.Message($"[导弹发射器] 手动锁定目标 G{groupId}");
            return true;
        }

        /// <summary>出膛发射：出膛点 = 炮口射线 ∩ 本船 AABB 六面（F2 几何）→ FireFromMapBuilding
        /// （Missile 分支：出生点外推、继承速度、Mission=Following）。发射成本 = 武器 ammo（Fire 链扣）。
        /// 弹着偏置：仅当目标 = 精确打击点目标时携带（换目标后新导弹回退瞄船心）。</summary>
        private void TryFire(SpaceEntityGroup? tgt)
        {
            if (tgt == null || weapon == null) return;
            var cm = GetCombatManager();
            var entity = GetMyEntity();
            var myGroup = GetMyGroup();
            if (cm == null || entity == null || myGroup == null) return;
            if (!CanEngage(tgt)) return;          // [V3] 开火前过认知层（此处是**唯一漏斗**，`AcquireTarget` 之外还有手动/命令路径）

            DVector3 myPos = cm.GetGroupPos(myGroup);
            DVector3 targetPos = cm.GetGroupPos(tgt);
            DVector3 targetVel = cm.GetGroupVelocity(tgt);
            var sensor = sensorMgrRef.Get();
            if (sensor != null)
            {
                if (!sensor.TryGetKnownState(myGroup.FactionId, tgt, out var contact)) return;
                targetPos = ContactMemory.CoastedPosition(contact, TimeScaleManager.GetPhysicsTickSafe());
                targetVel = contact.velocity;
            }

            UnityEngine.Vector3 muzzleShip = MapCoordMapper.MapCellToShip(parent.Map, parent.Position);
            UnityEngine.Vector3 targetShip = MapCoordMapper.SystemToShip(entity, myPos, targetPos);
            UnityEngine.Vector3 dirShip = targetShip - muzzleShip;
            if (dirShip.sqrMagnitude < 1f) return; // 目标与炮口重合
            dirShip.Normalize();
            UnityEngine.Vector3 half = (UnityEngine.Vector3)entity.AabbHalfSize;
            UnityEngine.Vector3 exitShip = MapCoordMapper.RayExitAabb(muzzleShip, dirShip, -half, half, out int exitFace);
            DVector3 exitSystem = MapCoordMapper.ShipToSystem(entity, myPos, exitShip);

            DVector3 aimOffset = (preciseStrikeGroupId == tgt.GroupId && preciseStrikeCell.IsValid)
                ? preciseAimOffsetShip
                : DVector3.zero;
            cm.FireFromMapBuilding(myGroup.GroupId, weapon, exitSystem, targetPos, targetVel, tgt.GroupId, aimOffset, EffectiveVolleyCount());
            Log.Message($"[导弹发射器] G{myGroup.GroupId}→G{tgt.GroupId} 出膛 面={exitFace} 距离={(targetPos - myPos).magnitude / 1000.0:F1}km 冷却={weapon.cooldownRemaining} N={EffectiveVolleyCount()}{(aimOffset != DVector3.zero
                    ? $" 弹着偏置=({aimOffset.x:F1},0,{aimOffset.z:F1})m"
                    : "")}");
        }

        // ── 精确打击：点选目标地图一格为打击落点（§10；镜像定位机精确炮击，CompMapDesignator 同构）──

        /// <summary>开始设置精确打击点：确定目标（手动/当前目标，或从带 Map 敌舰列表选）后跳转其地图选格。</summary>
        private void BeginPrecisionStrike()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var myGroup = GetMyGroup();
            if (sg == null || myGroup == null) return;

            SpaceEntityGroup? t = null;
            if (manualTargetGroupId >= 0)
                t = sg.GetGroupById(manualTargetGroupId);
            if (t == null) t = target;
            if (t == null)
            {
                ShowPrecisionTargetPicker();
                return;
            }
            BeginPrecisionPick(t);
        }

        /// <summary>精确打击目标选择列表（仅列出带 Map 的敌对 group——落点选格需要目标地图）。</summary>
        private void ShowPrecisionTargetPicker()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var cm = GetCombatManager();
            var myGroup = GetMyGroup();
            if (sg == null || cm == null || myGroup == null) return;

            var sensor = sensorMgrRef.Get();   // [V3]（2026-10-11 B1）：影子期条目的标注口
            var options = new List<FloatMenuOption>();
            DVector3 myPos = cm.GetGroupPos(myGroup);
            foreach (var g in sg.GetAllGroups())
            {
                if (!g.IsActive || g.GroupId == myGroup.GroupId || !IsHostile(g, myGroup)) continue;
                if (!CanEngage(g)) continue;           // [V3] 列表只列"看得见"的（与视图同一判据）
                if (GetMapEntity(g) == null) continue; // 只需有 Map 的目标（空间站/带图船）
                double d = (cm.GetGroupPos(g) - myPos).magnitude;
                string suffix = sensor != null ? sensor.PlayerPhaseSuffix(g) : "";   // 影子期标注
                var captured = g;
                options.Add(new FloatMenuOption($"Group {g.GroupId}（{d / 1000.0:F0} km）{suffix}",
                    () => BeginPrecisionPick(captured)));
            }
            if (options.Count == 0)
                options.Add(new FloatMenuOption("无敌对带图舰船目标", null));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        /// <summary>跳转目标地图并用点选器选取打击格；同时锁定该目标为手动目标（精确打击 = 指定目标 + 指定落点）。</summary>
        private void BeginPrecisionPick(SpaceEntityGroup pickTarget)
        {
            var tEntity = GetMapEntity(pickTarget);
            if (tEntity == null || tEntity.MapId < 0)
            {
                Messages.Message("目标船没有可用的地图，无法设置精确打击点。", MessageTypeDefOf.RejectInput);
                return;
            }
            Map targetMap = SpaceMapGenerator.FindPocketMap(tEntity.MapId);
            if (targetMap == null) return;

            if (!SetManualTarget(pickTarget.GroupId)) return; // 锁定手动目标（校验敌对/有效 + 同步在飞导弹）

            CameraJumper.TryJump(targetMap.Center, targetMap);
            var tp = new TargetingParameters
            {
                canTargetLocations = true,
                canTargetBuildings = false,
                canTargetPawns = false,
                validator = (TargetInfo t) => t.Cell.InBounds(targetMap)
            };
            int capturedGroupId = pickTarget.GroupId;
            Map capturedMap = targetMap;
            Find.Targeter.BeginTargeting(tp, (LocalTargetInfo targ) =>
            {
                SetPreciseStrikePoint(capturedGroupId, targ.Cell, capturedMap);
            }, null, null, null, true);
        }

        /// <summary>写入精确打击点（格 + 目标局部偏移 + 标记刷新 + 提示）。</summary>
        private void SetPreciseStrikePoint(int groupId, IntVec3 cell, Map targetMap)
        {
            preciseStrikeGroupId = groupId;
            preciseStrikeCell = cell;
            UnityEngine.Vector3 local = MapCoordMapper.MapCellToShip(targetMap, cell);
            preciseAimOffsetShip = new DVector3(local.x, 0f, local.z); // 目标局部米（y=0 甲板层）
            DestroyPreciseStrikeMarker();
            EnsurePreciseStrikeMarker();
            Log.Message($"[精确打击] 发射器设定弹着点: G{groupId} 格=({cell.x},{cell.z}) 局部偏移=({local.x:F1},0,{local.z:F1})m");
            Messages.Message($"精确打击点已设置: G{groupId} ({cell.x},{cell.z})",
                MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>清除精确打击点，恢复瞄目标船心。notify=true 时弹游戏内消息。</summary>
        private void ClearPreciseStrike(bool notify)
        {
            preciseStrikeGroupId = -1;
            preciseStrikeCell = IntVec3.Invalid;
            preciseAimOffsetShip = DVector3.zero;
            DestroyPreciseStrikeMarker();
            if (notify)
                Messages.Message("精确打击点已清除，恢复瞄准目标船心。", MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>目标地图选格标记：不存在则补一个（地图重载后标记消失，CompTick 周期补）。</summary>
        private void EnsurePreciseStrikeMarker()
        {
            if (preciseStrikeMarker != null && !preciseStrikeMarker.Destroyed) return;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var tEntity = GetMapEntity(sg?.GetGroupById(preciseStrikeGroupId));
            if (tEntity == null || tEntity.MapId < 0) return;
            Map tMap = SpaceMapGenerator.FindPocketMap(tEntity.MapId);
            if (tMap == null || !preciseStrikeCell.InBounds(tMap)) return;
            var def = DefDatabase<ThingDef>.GetNamedSilentFail("PF_Mote_PreciseStrike");
            if (def == null) return;
            preciseStrikeMarker = MoteMaker.MakeStaticMote(preciseStrikeCell.ToVector3Shifted(), tMap, def, 1f, true, 0f);
        }

        private void DestroyPreciseStrikeMarker()
        {
            if (preciseStrikeMarker != null && !preciseStrikeMarker.Destroyed)
                preciseStrikeMarker.Destroy();
            preciseStrikeMarker = null;
        }

        // ── 跟随同步：本发射器释放的 Following 导弹改指当前目标 ──

        private void SyncFollowerTargets(int targetGroupId)
        {
            var avm = Current.Game?.GetComponent<AutonomousVehicleManager>();
            var myGroup = GetMyGroup();
            if (avm == null || myGroup == null) return;
            avm.SyncFollowerTargets(myGroup.GroupId, targetGroupId);
        }

        // ── Gizmo ──

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra()) yield return g;
            // 无 faction（调试/测试场景放置的建筑）与玩家阵营显示 Gizmo；外族/敌对阵营建筑不显示
            if (parent.Faction != null && parent.Faction != Faction.OfPlayer) yield break;

            // 目标来源只剩"指定"（[N]）⇒ 不再有"索敌模式"开关，只剩"指定目标"这一个入口
            yield return new Command_Action
            {
                defaultLabel = "指定目标",
                defaultDesc = "选择一个敌对轨道实体作为打击目标（导弹释放后自动跟随，直到你下令撞击或改目标）。**出厂不自动交战**：没有指定目标就不开火。",
                icon = TexButton.Rename,
                action = ShowTargetPicker
            };

            // 发射档位：自动（冷却好就发射）/ 手动（不自动打，手动开火单发或面板/全体命令指挥已释放导弹）
            yield return new Command_Toggle
            {
                defaultLabel = autoFire ? "自动发射" : "手动发射",
                defaultDesc = "发射档位：自动 = 索敌有效且冷却就绪即自动发射；手动 = 不自动发射——用「手动开火」单发，或先让导弹跟随目标，再用「命令全体导弹」/导弹控制面板统一指挥撞击。",
                icon = TexButton.Rename,
                isActive = () => autoFire,
                toggleAction = () => { autoFire = !autoFire; }
            };

            // 齐射数量（弹群 N，§5 2026-09-08）：单次发射的弹群枚数（深度队列，上限 8）
            yield return new Command_Action
            {
                defaultLabel = "齐射数量: " + EffectiveVolleyCount(),
                defaultDesc = "单次发射的弹群枚数（1.." + CombatManager.MaxVolleyCount + "，发射器设置覆盖武器默认；上限 = 深度队列安全容量）：N 枚实体同群编队巡航（群级推进由物理按 N 源同步，每枚自带满罐），发射沿航向排成深度队列（间距自适应 ~1.4-2km，同深度仅 1 枚）；终末接管时解离、沿队列逐枚撞击——前弹 AoE 打不到后到兄弟。",
                icon = TexButton.Rename,
                action = () => Find.WindowStack.Add(new Window_VolleyCountSlider(EffectiveVolleyCount(),
                    n =>
                    {
                        volleyCount = n;
                        Log.Message($"[导弹发射器] 齐射数量 → {n}");
                    },
                    safetySpacing: ResolvedMissileDef.safetySpacingMeters,
                    arraySideLimit: ResolvedMissileDef.arraySideLimitMeters,
                    minSpacing: ResolvedMissileDef.blastRadius * 1.2f))
            };

            yield return new Command_Action
            {
                defaultLabel = "手动开火",
                defaultDesc = "冷却就绪时立即发射一枚导弹。",
                icon = TexButton.Rename,
                action = ManualFire
            };

            // 精确打击点（§10 弹着偏置）：目标地图选格 → 释放导弹终末撞该格（爆炸落该格）
            yield return new Command_Action
            {
                defaultLabel = HasPreciseStrike
                    ? $"精确打击: G{preciseStrikeGroupId} ({preciseStrikeCell.x},{preciseStrikeCell.z})"
                    : "设置精确打击点",
                defaultDesc = HasPreciseStrike
                    ? "已瞄准该目标地图指定格（重设：跳转目标地图后点选新格；清除后恢复瞄船心）。在飞导弹保持释放时的落点，仅对新发射的导弹生效。"
                    : "在目标（空间站等带地图舰船）地图上点选一格作为打击落点：本发射器释放的导弹终末瞄准该格（相对船心偏移，随船运动/旋转保持贴格），弹头命中与 Map 爆炸即落于该格。会锁定该目标为手动目标。",
                icon = TexButton.Rename,
                action = BeginPrecisionStrike
            };
            if (HasPreciseStrike)
            {
                yield return new Command_Action
                {
                    defaultLabel = "清除精确打击点",
                    defaultDesc = "恢复瞄准目标船心（影响之后发射的导弹；在飞导弹保持原落点）。",
                    icon = TexButton.Rename,
                    action = () => ClearPreciseStrike(true)
                };
            }

            // 命令全体导弹：本发射器释放的全部在册导弹统一下发指令（撞击当前目标/待命/自毁）
            yield return new Command_Action
            {
                defaultLabel = "命令全体导弹",
                defaultDesc = "对本发射器释放的全部在飞导弹统一下发指令：全体撞击（当前索敌目标）、全体待命或全体自毁。",
                icon = TexButton.Info,
                action = ShowAllMissileCommandMenu
            };

            // 导弹控制面板（过滤本发射器释放的导弹：目标跟随状态 + 下令撞击/待命/自毁）
            var myGroupNow = GetMyGroup();
            int filterLauncher = myGroupNow?.GroupId ?? -1;
            yield return new Command_Action
            {
                defaultLabel = "导弹控制面板",
                defaultDesc = "管理本发射器释放的导弹：查看跟随/撞击状态，下达撞击、待命或自毁指令。",
                icon = TexButton.Info,
                action = () => Find.WindowStack.Add(new Window_MissileControl(filterLauncher))
            };
        }

        private void ShowTargetPicker()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var cm = GetCombatManager();
            var myGroup = GetMyGroup();
            if (sg == null || cm == null || myGroup == null) return;

            // [V3]（2026-10-11 B1）：列表准入 = **与开火同一条判据**（`CanEngage` → `PlayerCanSee`；影子算看得见）——
            // 原来这里不筛 ⇒ 列表会列出玩家还没发现的敌舰（信息泄漏），选了又打不着。
            var sensor = sensorMgrRef.Get();

            var options = new List<FloatMenuOption>();
            DVector3 myPos = cm.GetGroupPos(myGroup);
            foreach (var g in sg.GetAllGroups())
            {
                if (!g.IsActive || g.GroupId == myGroup.GroupId || !IsHostile(g, myGroup)) continue;
                if (!CanEngage(g)) continue;
                double d = (cm.GetGroupPos(g) - myPos).magnitude;
                string suffix = sensor != null ? sensor.PlayerPhaseSuffix(g) : "";
                var captured = g;
                options.Add(new FloatMenuOption($"Group {g.GroupId}（{d / 1000.0:F0} km）{suffix}", () =>
                {
                    SetManualTarget(captured.GroupId);
                }));
            }
            if (options.Count == 0)
                options.Add(new FloatMenuOption("无敌对目标", null));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        // ── 本船链路：Map → ownerEntity → Group（与定位机同构）──

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

        /// <summary>取 group 中第一个带 Map 的实体（空间站/带图船本体；无 Map 目标如导弹/无人机返回 null）。
        /// 精确打击点选格/标记/偏置换算都要求目标有地图。</summary>
        private SpaceEntity? GetMapEntity(SpaceEntityGroup? g)
        {
            if (g == null) return null;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) return null;
            foreach (int eid in g.GetEntityIds())
            {
                var e = sg.GetEntityById(eid);
                if (e != null && e.MapId >= 0) return e;
            }
            return null;
        }

        private CombatManager GetCombatManager() => Current.Game?.GetComponent<CombatManager>();
    }

    /// <summary>
    /// 齐射数量滑杆（弹群 N，2026-09-08 §5）：1..CombatManager.MaxVolleyCount 连续可调；
    /// 「恢复武器默认」把发射器设置清回 0（跟随 Def.missileCount）。确定回调 n ≥ 1（0 = 武器默认）。
    /// </summary>
    public class Window_VolleyCountSlider : Window
    {
        private const int MinN = 1;
        private const int MaxN = CombatManager.MaxVolleyCount; // 单一来源（深度队列安全容量）
        private readonly Action<int> onConfirm;
        private float sliderVal;

        /// <summary>
        /// 队列间距预览用的 Def 口径（safetySpacingMeters / arraySideLimitMeters / 爆炸半径）。
        /// **必须与发射路径同源**：真实间距由 `MissileVolleyMath.ComputeSpacing` 算出
        /// （`CombatManager.CreateMissileVolley`），UI 不能自己写死数——2026-09-14 修：
        /// 旧 UI 写死 `min(2000, 10000/(n−1))` 且文案称"≤10km"，而 Def 上限是 16km，
        /// n=8 时 UI 显示 1429m、实际 2000m，属于误导。
        /// </summary>
        private readonly float safetySpacing;
        private readonly float arraySideLimit;
        private readonly float minSpacing;

        public Window_VolleyCountSlider(int currentEffective, Action<int> onConfirm,
            float safetySpacing = 2000f, float arraySideLimit = 16000f, float minSpacing = 1500f * 1.2f)
        {
            this.onConfirm = onConfirm;
            this.safetySpacing = safetySpacing;
            this.arraySideLimit = arraySideLimit;
            this.minSpacing = minSpacing;
            sliderVal = Mathf.Clamp(currentEffective, MinN, MaxN);
            forcePause = true;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = true;
            draggable = true;
            absorbInputAroundWindow = false;
        }

        public override Vector2 InitialSize => new(400f, 170f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 6, inRect.width, 26f), "齐射数量（弹群 N 枚）");
            Text.Font = GameFont.Small;

            int n = Mathf.RoundToInt(sliderVal);
            Widgets.Label(new Rect(0, 40, inRect.width, 22f),
                $"当前：{n} 枚 —— N 枚同群编队巡航（每枚自带满罐，群级推进按 N 源同步），终末解离逐枚撞击。");
            sliderVal = Widgets.HorizontalSlider(new Rect(0, 72, inRect.width - 20f, 26f), sliderVal, MinN, MaxN, false);
            sliderVal = Mathf.RoundToInt(sliderVal); // 吸附整数

            float spacing = n > 1
                ? (float)MissileVolleyMath.ComputeSpacing(safetySpacing, arraySideLimit, n, minSpacing)
                : 0f;
            Widgets.Label(new Rect(0, 100, inRect.width, 22f),
                n == 1
                    ? "单发模式（同旧版逐发）"
                    : $"弹群深度队列：{n} 枚沿航向排开，间距 {spacing:F0}m，总长 {(n - 1) * spacing:F0}m（超总长上限 {arraySideLimit / 1000f:F0}km 时压间距，下限 = 爆炸冲量半径）");
            Text.Font = GameFont.Small;

            if (Widgets.ButtonText(new Rect(0, 132, 110f, 28f), "确定"))
            {
                onConfirm(Mathf.RoundToInt(sliderVal));
                Close();
            }
            if (Widgets.ButtonText(new Rect(120f, 132f, 150f, 28f), "恢复武器默认"))
            {
                onConfirm(0); // 0 = 跟随武器 Def
                Close();
            }
            if (Widgets.ButtonText(new Rect(inRect.width - 90f, 132f, 90f, 28f), "取消"))
                Close();
        }
    }
}
