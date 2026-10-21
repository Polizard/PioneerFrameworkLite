using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;
using RimWorld;
using PioneerFramework.Combat;
using PioneerFramework.SpaceAI;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView : MainTabWindow
    {
        // ════════════════════════════════════════════════════
        //  GUI 交互
        // ════════════════════════════════════════════════════

        private void HandleGUIEvents(Rect inRect)
        {
            var e = Event.current;
            if (e == null) return;

            // 点在 UI 或上层窗口上时不点选（画 UI 在前、控件已消费自己身上的左键；本判据是第二道保险）
            if (e.type == EventType.MouseDown && e.button == 0 && !isDraggingRight && MouseInViewport(inRect))
                HandleViewportClick(e, inRect);
        }

        /// <summary>
        /// 3D 视口左键（2026-10-18 视图系统 v2）：
        /// · 单击 = 只改选中（不动相机）；
        /// · 双击 = 选中 + 聚焦（唯一会动相机的鼠标单击动作）；
        /// · Shift+左键 = 设为目标（不改选中、不动相机）；
        /// · 空白 = 取消选中（相机也**不动**）。
        /// </summary>
        private void HandleViewportClick(Event e, Rect inRect)
        {
            Vector2 mouseInView = e.mousePosition - inRect.position;
            if (TryPickGroup(mouseInView, inRect.size, out int bestId))
            {
                if (e.shift) { SetTargetGroup(bestId); return; }
                SelectGroupForInspection(bestId);
                if (e.clickCount >= 2) FocusGroup(bestId);
                return;
            }

            if (TrySelectMegastructure(mouseInView, inRect.size, float.MaxValue)) return;

            if (!e.shift)
            {
                ClearInspection();
            }
        }

        /// <summary>屏幕拾取最近的可见编队。与渲染**同钟**（U19：连续渲染时钟，不是阶梯 physicsTick）。</summary>
        private bool TryPickGroup(Vector2 mouseInView, Vector2 viewSize, out int bestId)
        {
            bestId = -1;
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null || renderCamera == null) return false;

            double tick = RenderTick();
            DVector3 origin = GetRenderOrigin(tick);

            var sysIds = GetCurrentSystemBodyIds(mgr); // 第 12 条：记忆化复用（此前每帧新建 HashSet + 填卫星）
            var sensor = Current.Game?.GetComponent<SensorManager>();   // [V3] 列表准入（本方法低频，直接取组件）

            float best = float.MaxValue;

            foreach (var g in spaceGame.GetAllGroups())
            {
                if (!g.IsActive || !sysIds.Contains(g.CelestialBodyId)) continue;
                if (!CanInspectEntity(g)) continue;
                if (sensor != null && !sensor.PlayerCanSee(g)) continue;   // [V3]/S4c：列表只列看得见的

                DVector3 gAbs = GetGroupPosAbs(g, tick);
                Vector3 vp = AbsToView(gAbs, origin);
                Vector3 sp = renderCamera.WorldToScreenPoint(vp);
                if (sp.z < 0) continue;

                float sx = sp.x / renderCamera.pixelWidth * viewSize.x;
                float sy = (1f - sp.y / renderCamera.pixelHeight) * viewSize.y;
                float d = Vector2.Distance(mouseInView, new Vector2(sx, sy));
                if (d < 25f && sp.z < best)
                {
                    best = sp.z;
                    bestId = g.GroupId;
                }
            }

            return bestId >= 0;
        }

        private void DrawOverlay(Rect inRect)
        {
            uiBlockRects.Clear();   // 每帧重新登记（画在输入处理之前，故同帧内几何一致）

            ComputeLayout(inRect);   // V-1：五分区（工具条 / 左栏 / 雷达 / 姿态球 / 状态行）

            // 边缘天体指示 + 世界标记先画（面板后画 ⇒ 自然遮挡）
            DrawBodyDirectionIndicators(inRect);
            DrawWorldMarkers(inRect);

            DrawToolbar();
            DrawObjectList(rLeftCol);
            DrawInspector(rInspector);
            // 仪表基准 = 当前选中的编队（2026-10-18 用户裁定③；操作编队只决定命令来源）
            if (rRadar.width > 0) DrawRadarPanel(ResolveInstrumentGroup());
            if (rNavball.width > 0) DrawNavballPanel(ResolveNavballGroup());
            DrawSteeringStrip();
            DrawPilotPanel();
            DrawStatusLine();

            // 视图内 toast 最后画（必须在所有面板之上）
            DrawToasts(inRect);
        }

        private void DrawBodyDirectionIndicators(Rect inRect)
        {
            if (renderCamera == null || inRect.width < 10f) return;
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return;

            double tick = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 origin = GetRenderOrigin();
            Vector2 center = inRect.center;
            float halfW = inRect.width * 0.5f;
            float halfH = inRect.height * 0.5f;

            var bodies = mgr.GetAllBodies();
            if (bodies == null) return;

            int camW = renderCamera.pixelWidth;
            int camH = renderCamera.pixelHeight;
            if (camW < 1 || camH < 1) return;

            foreach (var body in bodies)
            {
                // 焦点是天体时不显示自身指示器
                if (focusKind == PFViewFocusKind.Body && body.bodyId == focusBodyId) continue;

                DVector3 wp = mgr.GetBodyPositionAbs(body.bodyId, tick);
                Vector3 vp = AbsToView(wp, origin);
                Vector3 sp = renderCamera.WorldToScreenPoint(vp);
                if (sp.z <= 0f) continue; // 相机后方

                bool onScreen = sp.x >= 0f && sp.x <= camW && sp.y >= 0f && sp.y <= camH;
                if (onScreen) continue;

                // 屏幕空间方向
                float sx = sp.x / camW * inRect.width;
                float sy = (1f - sp.y / camH) * inRect.height;
                Vector2 dir = new Vector2(sx - center.x, sy - center.y).normalized;

                // 钳制到屏幕边缘
                Vector2 edge;
                float dxAbs = Mathf.Abs(dir.x);
                float dyAbs = Mathf.Abs(dir.y);
                if (dxAbs > 0.0001f && dyAbs > 0.0001f)
                {
                    float scaleX = halfW / dxAbs;
                    float scaleY = halfH / dyAbs;
                    float scale = Mathf.Min(scaleX, scaleY);
                    edge = center + dir * (scale - 20f);
                }
                else
                {
                    edge = center;
                }

                edge.x = Mathf.Clamp(edge.x, inRect.x + 30f, inRect.xMax - 80f);
                edge.y = Mathf.Clamp(edge.y, inRect.y + 14f, inRect.yMax - 14f);

                string label = mgr.GetBodyName(body.bodyId);
                DrawTag(new Vector2(edge.x - 30f, edge.y - 10f), label, PFViewPalette.TextSecondary);
                GUI.color = Color.white;
            }
        }

        private void ShowGroupMenu()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null) return;

            var sysIds = GetCurrentSystemBodyIds(mgr); // 第 12 条：记忆化复用（此前每帧新建 HashSet + 填卫星）
            var sensor = Current.Game?.GetComponent<SensorManager>();   // [V3] 列表准入（本方法低频，直接取组件）

            var opts = new List<FloatMenuOption>();
            foreach (var g in spaceGame.GetAllGroups())
            {
                if (!g.IsActive || !sysIds.Contains(g.CelestialBodyId)) continue;
                if (!CanInspectEntity(g)) continue;
                if (sensor != null && !sensor.PlayerCanSee(g)) continue;   // [V3]/S4c：列表只列看得见的
                var gg = g;
                int entityCount = gg.GetEntityIds()?.Count ?? 0;
                string label = $"编队 {gg.GroupId}（{entityCount} 个实体）";
                opts.Add(new FloatMenuOption(label, () =>
                {
                    SelectGroupForInspection(gg.GroupId);
                }));
            }

            if (opts.Count == 0)
                opts.Add(new FloatMenuOption("（本星系无编队）", null));

            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private void ShowSystemMenu()
        {
            var opts = new List<FloatMenuOption>();
            foreach (var b in selectableSystems)
            {
                var bb = b;
                opts.Add(new FloatMenuOption(bb.label, () =>
                {
                    FocusBody(bb.bodyId);
                    objectsDirty = true;
                }));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        /// <summary>选中组读数面板（V-1：搬进左栏底部；宽度取左栏宽，高度按内容累加）。</summary>
        private void ShowContextMenu(Vector2 mouseInRect, Rect inRect)
        {
            Vector2 mouseInView = mouseInRect - inRect.position;
            TryPickGroup(mouseInView, inRect.size, out int hit);
            var opts = new List<FloatMenuOption>();
            if (hit >= 0)
            {
                opts.Add(new FloatMenuOption($"选中 G{hit}", () => SelectGroupForInspection(hit)));
                opts.Add(new FloatMenuOption($"聚焦 G{hit}", () => { SelectGroupForInspection(hit); FocusGroup(hit); }));
                if (RefreshNavigableGroupMaps(hit))
                    opts.Add(new FloatMenuOption("PFV_EnterMap".Translate(), () => ShowGroupMapMenu(hit)));
                opts.Add(new FloatMenuOption($"设为目标 G{hit}", () => SetTargetGroup(hit)));
            }
            opts.Add(new FloatMenuOption("取消选中", ClearInspection));
            opts.Add(new FloatMenuOption("行星视角", FocusCurrentBody));
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        // ── DebugTabMenu_PFL 入口（玩家 UI 不再直接暴露） ──
        internal void DebugDeployCombatPair() => DeployCombatTestPair();
        internal void DebugTeleportTarget() => TeleportTargetHere();
        internal void DebugAdjustTargetSpeed(double dv) => AdjustTargetSpeed(dv);

        internal void DebugPregenerateRoomMaps()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var g = selectedGroupId >= 0 ? spaceGame?.GetGroupById(selectedGroupId) : null;
            if (g == null) { QueueToast("先选中一个编队"); return; }
            int count = 0;
            foreach (int eid in g.GetEntityIds())
            {
                var ent = spaceGame?.GetEntityById(eid);
                if (ent == null) continue;
                foreach (var part in ent.Parts)
                {
                    if (!part.IsRoomModulePart) continue;
                    if (SpaceMapGenerator.GenerateRoomMap(ent, part) != null) count++;
                }
            }
            QueueToast($"预生成舱室地图：{count} 张");
        }

        /// <summary>
        /// 手动操舵开关（左栏"选中编队"面板）：按下进入手动操舵，再按解除。
        /// 与船上操舵台的 Gizmo 走**同一条路**：`SteeringController.Open` + 打开操舵 HUD 窗口
        /// （键位读取/SAS/动作组/主推进方向都在那个窗口里，这里不复制第二套）。
        /// 单例语义（`SteeringController` 同时只操舵一艘）：换船时**先关旧窗**再开新的 ——
        /// 不能只依赖窗口的 onlyOneOfTypeAllowed 自动替换：替换发生在 Add 时，旧窗的 PostClose 会把
        /// 刚设好的 GroupId 一起清掉（顺序错了新窗就会立刻自关）。
        /// 准入判据与 HUD 每帧校验**同一份**：船自己的图上要有完好操舵台。
        /// </summary>
        private void ToggleManualSteering(SpaceEntityGroup g)
        {
            Window_Steering? open = FindSteeringWindow();

            // 已在操舵本编队 ⇒ 再按 = 解除（关窗；PostClose 里 Close + 推进器熄火）
            if (SteeringController.IsOpen && SteeringController.GroupId == g.GroupId)
            {
                EndSteering();
                return;
            }

            if (!CanOperateGroup(g, true, out string reason))
            {
                QueueToast(reason);
                return;
            }

            if (open != null)
                open.Close();

            if (SteeringController.IsOpen) Window_Steering.StopControl();
            SteeringController.Open(g.GroupId, true);
        }

        private static Window_Steering? FindSteeringWindow()
        {
            var windows = Find.WindowStack.Windows;
            for (int i = 0; i < windows.Count; i++)
                if (windows[i] is Window_Steering ws)
                    return ws;
            return null;
        }

        /// <summary>方向 → "方位 x° / 仰角 y°"（方位 = 绕 +Y 从 +Z 起算，仰角 = 相对 XZ 平面）。
        /// 用方向读数而不是"前/后/左/右"这类船体局部轴向名：局部轴的命名在本库里本身有歧义
        /// （`ThrustAxis` 的逐项注释说 +Z 是船头，`RotateByRot4` 的注释与导弹默认 `mainAxisShip=-Z`
        /// 都指向 -Z 是船头），表现层不替它选边；方位/仰角与姿态球上那个 T 记号（主推轴）一一对应
        /// ——数值与记号同取 `GetMainThrustAxisSystem`，同一份数据、同一套角度约定。</summary>
        private static string DirectionLabel(DVector3 dir)
        {
            DVector3 n = dir.normalized;
            float azimuth = Mathf.Atan2((float)n.x, (float)n.z) * Mathf.Rad2Deg;
            if (azimuth < 0f) azimuth += 360f;
            float elevation = Mathf.Asin(Mathf.Clamp((float)n.y, -1f, 1f)) * Mathf.Rad2Deg;
            return $"方位 {azimuth:F0}° / 仰角 {elevation:F0}°";
        }

        /// <summary>获取编队在行星系相对坐标下的当前位置（与 CombatManager 开火基准一致）。</summary>
        private DVector3 GetGroupRelPos(SpaceEntityGroup g)
        {
            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            return g.IsCircularOrbit ? g.GetCircularPositionAtTick(physicsTick) : g.CurrentOrbit.positionSystem.v;
        }

        private void FireAtTarget(WeaponInstance weapon)
        {
            TryFireAtTarget(ResolveOperationGroup()?.GroupId ?? -1, weapon);
        }

        private void TeleportTargetHere()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var shooter = spaceGame?.GetGroupById(selectedGroupId);
            var target = spaceGame?.GetGroupById(targetGroupId);
            if (shooter == null || target == null) return;

            DVector3 shooterPos = GetGroupRelPos(shooter);
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
        }

        private void AdjustTargetSpeed(double dv)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var shooter = spaceGame?.GetGroupById(selectedGroupId);
            var target = spaceGame?.GetGroupById(targetGroupId);
            if (shooter == null || target == null) return;

            if (target.IsCircularOrbit)
                target.ExitCircularOrbit();

            DVector3 toShooter = GetGroupRelPos(shooter) - GetGroupRelPos(target);
            double len = toShooter.magnitude;
            if (len < 1e-6) return;
            toShooter /= len;
            // 手动 ΔV 走合冲量累积器：物理步首统一施加（与 burn/碰撞同入口，避免一步内多次改速度）
            target.pendingImpulse += toShooter * dv;
            target.OrbitDirty = true;

            Log.Message($"[PFView] G{target.GroupId} 速度沿射向 G{shooter.GroupId} 方向调整 {dv:+#;-#;0} m/s");
        }

        /// <summary>部署一对配有实际设备和内景的交火测试船，间距 200m、高度 500km。</summary>
        private void DeployCombatTestPair()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            var source = SpaceMapGenerator.GetAnchorMap();
            var enemy = IncidentWorker_SpaceEncounter.FindEnemy();
            var template = DefDatabase<SpaceShipTemplateDef>.GetNamedSilentFail("PF_Debug_CombatShip");
            if (source == null || enemy == null || template == null)
            { QueueToast("需要锚点地图、敌对势力和交火测试舰模板"); return; }
            var session = Current.Game;
            int bodyId = currentSystemBodyId;
            LongEventHandler.QueueLongEvent(() =>
            {
                if (!ReferenceEquals(session, Current.Game)) return;
                using var creation = new EncounterBuildTransaction(id => spaceGame.DestroyGroup(id, "交火测试部署回滚"));
                double tick = TimeScaleManager.GetPhysicsTickSafe();
                double orbitRadius = spaceGame.GetBodySurfaceRadius(bodyId) + 500000.0;
                double angle = tick * 0.001;
                double speed = System.Math.Sqrt(spaceGame.GetBodyGM(bodyId) / orbitRadius);
                var reference = new SpaceEntityGroup(-1, Faction.OfPlayer.loadID) { CelestialBodyId = bodyId };
                var center = reference.GetCentralBodyOffset(tick);
                var centerVelocity = reference.GetCentralBodyVelocity((int)System.Math.Round(tick));
                DVector3 basePos = FrameConvert.BodyToSystem((BodyVec3)new DVector3(
                    System.Math.Cos(angle) * orbitRadius, 0, System.Math.Sin(angle) * orbitRadius), center);
                DVector3 vel = FrameConvert.BodyToSystemVelocity((BodyVec3)new DVector3(
                    -System.Math.Sin(angle) * speed, 0, System.Math.Cos(angle) * speed), centerVelocity);
                var first = CreateTestShip(template, source, basePos, vel, Faction.OfPlayer, bodyId, creation);
                var second = CreateTestShip(template, source, basePos + new DVector3(200, 0, 0), vel, enemy, bodyId, creation);
                creation.Commit();
                selectedGroupId = first.GroupId;
                targetGroupId = second.GroupId;
                FocusGroup(first.GroupId);
                Log.Message($"[PFView] 部署完整交火测试对: G{first.GroupId}(f{first.FactionId}) vs G{second.GroupId}(f{second.FactionId}) 间距200m @ {orbitRadius / 1000.0:F0}km 轨道；传感器/主动雷达/武器/燃料已就绪");
            }, "GeneratingMap", false, e => Log.Error("[PFView] 交火测试部署失败：" + e));
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
    }
}
