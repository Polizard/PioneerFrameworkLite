using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using PioneerFramework.Combat;

namespace PioneerFramework
{
    public enum MapDesignatorMode { Manual, Auto }

    public class CompProperties_MapDesignator : CompProperties
    {
        /// <summary>统一齐射冷却（tick）。</summary>
        public int cooldownTicks = 60;

        /// <summary>索敌扫描间隔（tick）。</summary>
        public int scanIntervalTicks = 32;

        public MapDesignatorMode defaultMode = MapDesignatorMode.Auto;

        public CompProperties_MapDesignator()
        {
            compClass = typeof(CompMapDesignator);
        }
    }

    /// <summary>定位机 → 炮塔锚点的绑定记录。</summary>
    public class TurretBinding
    {
        public Thing turret;
        public TurretMountSide side;

        public void ExposeData()
        {
            Scribe_References.Look(ref turret, "turret");
            Scribe_Values.Look(ref side, "side");
        }
    }

    /// <summary>
    /// Map 定位机建筑：集中索敌大脑。管理一组绑定炮塔锚点（上下锚点分开绑定），
    /// 统一冷却、统一齐射。模式：Manual（指定敌对 group）/ Auto（最近敌对 group）。
    /// 射界 = 半球（船顶/船底），上锚点打局部 +Y、下锚点打局部 -Y。
    /// </summary>
    public class CompMapDesignator : ThingComp
    {
        private List<TurretBinding> boundMounts = new List<TurretBinding>();
        private MapDesignatorMode mode;
        private int manualTargetGroupId = -1;
        private int cooldownTicks;
        private bool autoFire = true;
        private int nextScanTick;
        private SpaceEntityGroup currentTarget;
        private int lastFilterLogTick = -9999;

        public CompProperties_MapDesignator Props => (CompProperties_MapDesignator)props;
        public MapDesignatorMode Mode => mode;
        public bool AutoFire => autoFire;
        public IReadOnlyList<TurretBinding> BoundMounts => boundMounts;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (!respawningAfterLoad)
                mode = Props.defaultMode;
        }

        public override void CompTick()
        {
            base.CompTick();
            if (parent == null || parent.Destroyed || parent.Map == null) return;

            if (cooldownTicks > 0) cooldownTicks--;

            if (Find.TickManager.TicksGame >= nextScanTick)
            {
                currentTarget = AcquireTarget();
                nextScanTick = Find.TickManager.TicksGame + Props.scanIntervalTicks;
            }

            if (currentTarget != null)
                UpdateMountRotations(currentTarget);

            if (autoFire && cooldownTicks <= 0 && currentTarget != null && boundMounts.Count > 0)
                FireVolley(currentTarget);
        }

        // ── 索敌 ──

        private SpaceEntityGroup AcquireTarget()
        {
            if (mode == MapDesignatorMode.Manual)
            {
                var sg = Current.Game?.GetComponent<SpaceGameComp>();
                var t = sg?.GetGroupById(manualTargetGroupId);
                if (t != null && t.IsActive && IsHostile(t) && IsInRange(t)) return t;
                return null;
            }
            return AcquireAutoTarget();
        }

        private SpaceEntityGroup AcquireAutoTarget()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var myGroup = GetMyGroup();
            if (sg == null || myGroup == null) return null;

            var cm = GetCombatManager();
            if (cm == null) return null;
            DVector3 myPos = cm.GetGroupPos(myGroup);
            float engageRange = GetEffectiveRange();

            SpaceEntityGroup best = null;
            double bestDist = double.MaxValue;
            foreach (var g in sg.GetAllGroups())
            {
                if (!g.IsActive || !g.IsCollidable) continue;
                if (g.GroupId == myGroup.GroupId || !IsHostile(g)) continue;
                DVector3 tPos = cm.GetGroupPos(g);
                double d = (tPos - myPos).magnitude;
                if (d > engageRange) continue;
                if (d < bestDist) { bestDist = d; best = g; }
            }
            return best;
        }

        private bool IsHostile(SpaceEntityGroup g)
        {
            var myGroup = GetMyGroup();
            if (myGroup == null) return false;
            return g.FactionId != myGroup.FactionId; // factionId 不同即敌对（PFL 简化阵营判定）
        }

        private bool IsInRange(SpaceEntityGroup g)
        {
            var cm = GetCombatManager();
            var myGroup = GetMyGroup();
            if (cm == null || myGroup == null) return false;
            return (cm.GetGroupPos(g) - cm.GetGroupPos(myGroup)).magnitude <= GetEffectiveRange();
        }

        /// <summary>本定位机射程 = 全部绑定锚点武器 orbitalEngageRange 的最大值。</summary>
        private float GetEffectiveRange()
        {
            float range = 0f;
            foreach (var b in boundMounts)
            {
                var weapon = b.turret?.TryGetComp<CompMapTurret>()?.GetMount(b.side)?.weapon;
                if (weapon != null && weapon.def != null && weapon.def.orbitalEngageRange > range)
                    range = weapon.def.orbitalEngageRange;
            }
            return range > 0f ? range : 100000f;
        }

        // ── 本船链路：Map → ownerEntity → Group ──

        private SpaceEntityGroup GetMyGroup()
        {
            var entity = GetMyEntity();
            if (entity == null) return null;
            return Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(entity.GroupId);
        }

        private SpaceEntity GetMyEntity()
        {
            if (parent.Map == null) return null;
            var mapComp = parent.Map.GetComponent<SpaceMapComp>();
            if (mapComp == null || mapComp.ownerEntityId < 0) return null;
            return Current.Game?.GetComponent<SpaceGameComp>()?.GetEntityById(mapComp.ownerEntityId);
        }

        private CombatManager GetCombatManager() => Current.Game?.GetComponent<CombatManager>();

        // ── 开火 ──

        /// <summary>统一齐射：射界内锚点各自开火。目标方向同时驱动各锚点朝向。</summary>
        private void FireVolley(SpaceEntityGroup target)
        {
            // 开火对齐物理步（偶数 tick）：射弹生成与物理更新/降速边界同拍，
            // 射弹首次 deltaTime 恒为 Combat 档 2/60，杜绝跨物理步的大步进。
            if ((Find.TickManager.TicksGame & 1) != 0) return;

            var cm = GetCombatManager();
            var entity = GetMyEntity();
            var myGroup = GetMyGroup();
            if (cm == null || entity == null || myGroup == null) return;

            DVector3 myPos = cm.GetGroupPos(myGroup);
            DVector3 targetPos = cm.GetGroupPos(target);
            DVector3 targetVel = cm.GetGroupVelocity(target);

            bool anyFired = false;
            foreach (var binding in boundMounts)
            {
                if (binding.turret == null || binding.turret.Destroyed) continue;
                var turretComp = binding.turret.TryGetComp<CompMapTurret>();
                var mount = turretComp?.GetMount(binding.side);
                if (mount == null || mount.weapon == null || !mount.weapon.CanFire()) continue;
                if (!InHemisphere(mount, entity, myPos, targetPos))
                {
                    // 诊断（限频）：射界过滤日志，用于定位"目标在船体赤道面无炮可开"类问题
                    if (Find.TickManager.TicksGame - lastFilterLogTick >= 240)
                    {
                        lastFilterLogTick = Find.TickManager.TicksGame;
                        Vector3 tLocal = MapCoordMapper.WorldToShipLocal(entity, myPos, targetPos);
                        Log.Message(string.Format("[定位机] Group{0} 目标 G{1} 被{2}锚点射界过滤：目标局部 y={3:F1}m（上锚点需>0/下锚点需<0），船旋转={4}",
                            myGroup.GroupId, target.GroupId,
                            binding.side == TurretMountSide.Top ? "上" : "下",
                            tLocal.y, entity.Rotation.eulerAngles));
                    }
                    continue;
                }

                // 炮口世界位置：地图格 → 船体局部（+侧向 Y 偏移）→ 世界
                Vector3 muzzleLocal = MapCoordMapper.MapCellToLocal3D(parent.Map, binding.turret.Position);
                muzzleLocal.y += binding.side == TurretMountSide.Top ? turretComp.Props.yOffset : -turretComp.Props.yOffset;
                DVector3 muzzleWorld = MapCoordMapper.LocalToWorld(entity, myPos, muzzleLocal);

                cm.FireFromMapBuilding(myGroup.GroupId, mount.weapon, muzzleWorld, targetPos, targetVel);

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

        /// <summary>半球射界：以船体中心面（局部 y=0）分界。上锚点打 +Y、下锚点打 -Y。</summary>
        private bool InHemisphere(TurretMount mount, SpaceEntity entity, DVector3 myPos, DVector3 targetPos)
        {
            Vector3 targetLocal = MapCoordMapper.WorldToShipLocal(entity, myPos, targetPos);
            return mount.side == TurretMountSide.Top ? targetLocal.y > 0f : targetLocal.y < 0f;
        }

        /// <summary>驱动所有绑定锚点朝向目标（船体局部系水平角，地图贴图直接可用）。</summary>
        private void UpdateMountRotations(SpaceEntityGroup target)
        {
            var entity = GetMyEntity();
            var cm = GetCombatManager();
            var myGroup = GetMyGroup();
            if (entity == null || cm == null || myGroup == null) return;

            DVector3 myPos = cm.GetGroupPos(myGroup);
            DVector3 targetPos = cm.GetGroupPos(target);

            foreach (var binding in boundMounts)
            {
                if (binding.turret == null || binding.turret.Destroyed) continue;
                var mount = binding.turret.TryGetComp<CompMapTurret>()?.GetMount(binding.side);
                if (mount == null) continue;

                Vector3 localDir = MapCoordMapper.WorldToShipLocal(entity, myPos, targetPos);
                float angle = Mathf.Atan2(localDir.x, localDir.z) * Mathf.Rad2Deg;
                if (angle < 0f) angle += 360f;
                mount.rotation = angle;
            }
        }

        // ── 绑定 ──

        public bool RegisterTurret(Thing turret, TurretMountSide side)
        {
            if (turret == null || turret.Destroyed) return false;
            foreach (var b in boundMounts)
                if (b.turret == turret && b.side == side) return true; // 已绑定
            boundMounts.Add(new TurretBinding { turret = turret, side = side });
            return true;
        }

        public void UnregisterTurret(Thing turret, TurretMountSide side)
        {
            for (int i = boundMounts.Count - 1; i >= 0; i--)
                if (boundMounts[i].turret == turret && boundMounts[i].side == side)
                    boundMounts.RemoveAt(i);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode)
        {
            base.PostDeSpawn(map, mode);
            // 解绑所有炮塔的反向引用
            foreach (var b in boundMounts)
            {
                var mount = b.turret?.TryGetComp<CompMapTurret>()?.GetMount(b.side);
                if (mount != null && mount.boundDesignator == parent)
                    mount.boundDesignator = null;
            }
            boundMounts.Clear();
        }

        // ── Gizmo ──

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra()) yield return g;
            if (parent.Faction != Faction.OfPlayer) yield break;

            yield return new Command_Action
            {
                defaultLabel = "模式: " + (mode == MapDesignatorMode.Manual ? "手动" : "自动"),
                defaultDesc = "切换索敌模式：自动 = 最近敌对目标；手动 = 指定目标。",
                icon = TexButton.Rename,
                action = () => { mode = mode == MapDesignatorMode.Manual ? MapDesignatorMode.Auto : MapDesignatorMode.Manual; }
            };

            if (mode == MapDesignatorMode.Manual)
            {
                yield return new Command_Action
                {
                    defaultLabel = "指定目标",
                    defaultDesc = "选择一个敌对轨道实体作为手动目标。",
                    icon = TexButton.Rename,
                    action = ShowTargetPicker
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
                    defaultDesc = "已绑定的炮塔锚点数量。",
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

            var options = new List<FloatMenuOption>();
            DVector3 myPos = cm.GetGroupPos(myGroup);
            foreach (var g in sg.GetAllGroups())
            {
                if (!g.IsActive || g.GroupId == myGroup.GroupId || !IsHostile(g)) continue;
                double d = (cm.GetGroupPos(g) - myPos).magnitude;
                var captured = g;
                options.Add(new FloatMenuOption(string.Format("Group {0}（{1:F0} km）", g.GroupId, d / 1000.0), () =>
                {
                    manualTargetGroupId = captured.GroupId;
                    mode = MapDesignatorMode.Manual;
                }));
            }
            if (options.Count == 0)
                options.Add(new FloatMenuOption("无敌对目标", null));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        public void ManualFire()
        {
            if (cooldownTicks > 0) return;
            var t = currentTarget ?? AcquireTarget();
            if (t == null || boundMounts.Count == 0) return;
            FireVolley(t);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref boundMounts, "boundMounts", LookMode.Deep);
            Scribe_Values.Look(ref mode, "mode");
            Scribe_Values.Look(ref manualTargetGroupId, "manualTargetGroupId", -1);
            Scribe_Values.Look(ref cooldownTicks, "cooldownTicks");
            Scribe_Values.Look(ref autoFire, "autoFire", true);
        }
    }
}
