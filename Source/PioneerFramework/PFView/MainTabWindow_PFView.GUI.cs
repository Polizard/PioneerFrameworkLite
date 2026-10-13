using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;
using RimWorld;
using PioneerFramework.Combat;

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
            {
                TrySelectGroup(e.mousePosition - inRect.position, inRect.size);
            }

            // 相机复位键让位于操舵：操舵键位里 R = "上"（见 Window_Steering 键位表），
            // 若两处都认，按一次 R 会"一边上升一边把相机拽回复位"（用户 2026-10-02 起 PFView 也能操舵）。
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.R && !SteeringController.IsOpen)
            {
                InitCameraForCurrentBody();
            }
        }

        private void TrySelectGroup(Vector2 mouseInView, Vector2 viewSize)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null || renderCamera == null) return;

            // 拾取必须与**渲染**用同一个 tick：physicsTick 单位是秒，取整会带来最多半秒的位移
            // （几 km/s 的船上就是几 km），相机拉近到船附近时这点偏差等于整屏——船会点不中。
            double tick = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 origin = GetRenderOrigin();

            var sysIds = GetCurrentSystemBodyIds(mgr); // 第 12 条：记忆化复用（此前每帧新建 HashSet + 填卫星）
            var sensor = Current.Game?.GetComponent<SensorManager>();   // [V3] 列表准入（本方法低频，直接取组件）

            float best = float.MaxValue;
            int bestId = -1;

            foreach (var g in spaceGame.GetAllGroups())
            {
                if (!g.IsActive || !sysIds.Contains(g.CelestialBodyId)) continue;
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

            if (bestId >= 0)
            {
                selectedGroupId = bestId;
                InitCameraForCurrentBody();
            }
        }

        private void DrawOverlay(Rect inRect)
        {
            uiBlockRects.Clear();   // 每帧重新登记（画在输入处理之前，故同帧内几何一致）

            ComputeLayout(inRect);   // V-1：五分区（工具条 / 左栏 / 雷达 / 姿态球 / 状态行）

            // 边缘天体指示先画：被面板盖住的部分自然隐藏（面板后画）
            DrawBodyDirectionIndicators(inRect);

            DrawToolbar();
            DrawLeftColumn();
            DrawRadarPanel();
            DrawNavballPanel();
            DrawStatusLine();
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
                // 锚定天体时不显示自身指示器
                if (selectedGroupId < 0 && body.bodyId == currentSystemBodyId) continue;

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
                Color bodyColor = body.bodyType switch
                {
                    CelestialBodyType.Star => Color.yellow,
                    CelestialBodyType.Planet => new Color(0.3f, 0.6f, 1f),
                    _ => Color.gray
                };
                GUI.color = bodyColor;
                Widgets.Label(new Rect(edge.x - 30, edge.y - 10, 60, 20), label);
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
                if (sensor != null && !sensor.PlayerCanSee(g)) continue;   // [V3]/S4c：列表只列看得见的
                var gg = g;
                int entityCount = gg.GetEntityIds()?.Count ?? 0;
                string label = $"编队 {gg.GroupId}（{entityCount} 个实体）";
                opts.Add(new FloatMenuOption(label, () =>
                {
                    selectedGroupId = gg.GroupId;
                    InitCameraForCurrentBody();
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
                    currentSystemBodyId = bb.bodyId;
                    selectedGroupId = -1;
                    InitCameraForCurrentBody();
                }));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        /// <summary>选中组读数面板（V-1：搬进左栏底部；宽度取左栏宽，高度按内容累加）。</summary>
        private void DrawInfoPanel(Rect colRect, float width)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            var g = spaceGame.GetGroupById(selectedGroupId);
            if (g == null) { selectedGroupId = -1; return; }

            var combatManager = Current.Game?.GetComponent<CombatManager>();
            var weapons = combatManager != null ? combatManager.GetGroupWeapons(selectedGroupId) : new List<WeaponInstance>();
            int shownWeapons = Mathf.Min(weapons.Count, 4);

            // 收集本编队的舱室部件（用于"进入舱室"）
            var roomEntities = new List<SpaceEntity>();
            var roomParts = new List<SpacePart>();
            foreach (int eid in g.GetEntityIds())
            {
                var e = spaceGame.GetEntityById(eid);
                if (e == null) continue;
                foreach (var part in e.Parts)
                {
                    if (part.IsRoomModulePart)
                    {
                        roomEntities.Add(e);
                        roomParts.Add(part);
                    }
                }
            }
            float roomSectionH = roomParts.Count > 0 ? 20 + roomParts.Count * 26 + 26 : 0;

            var target = targetGroupId >= 0 ? spaceGame.GetGroupById(targetGroupId) : null;

            // 面板高度 = 下面逐行累加的行高之和（旧版是手写常数 195 + …，与实际画出的行对不上：
            // 无目标时底部空出一大块，而"目标被销毁"时又按 80 预留 → 高度与内容长期错位）。
            const float RowTitle = 22f, RowText = 18f, RowBtn = 30f;
            float pw = width;
            float ph = 5                                             // 上留白
                     + RowTitle                                      // 标题
                     + RowText + RowBtn                              // 目标行 + 设为目标
                     + RowBtn                                        // 手动操舵开关
                     + shownWeapons * 22 + (weapons.Count > shownWeapons ? RowText : 0) + 4 + RowBtn
                     + (target != null ? 26 + 30 : 0)                 // 传送 + 调速
                     + roomSectionH
                     + RowBtn + 6;                                    // 取消选中
            Rect p = new Rect(colRect.xMax - pw, colRect.yMax - ph, pw, ph);

            // 并入左栏（不再是一扇独立小窗）：面板底 + 顶部分割线；标题去掉旧标签「（射手）」
            //（那是"手动开火射手"时代的叫法，现在它就是"当前选中编队"；V-1.1，用户 2026-10-02）。
            // 姿态/主推轴两行已挪到姿态球两侧（同一份数据只显示一处）。
            Widgets.DrawBoxSolid(p, PFViewPalette.PanelBg);
            UiKit.SpacecraftUIDecor.DrawDivider(p, p.y, p.width, PFViewPalette.Divider);
            uiBlockRects.Add(p);

            float x = p.x + 5;
            float w = pw - 10;
            float y = p.y + 5;

            MiniLabel(new Rect(x, y, w, RowTitle), $"编队 {g.GroupId}", PFViewPalette.TextPrimary);
            y += RowTitle;

            if (target != null)
            {
                double dist = (GetGroupRelPos(g) - GetGroupRelPos(target)).magnitude;
                Widgets.Label(new Rect(x, y, w, RowText), $"目标 编队 {target.GroupId}　距离 {dist / 1000.0:F0} km");
            }
            else
            {
                Widgets.Label(new Rect(x, y, w, RowText), "目标: 无（选中目标编队后按钮设置）");
            }
            y += RowText;

            if (Widgets.ButtonText(new Rect(x, y, w, 24), targetGroupId == selectedGroupId ? "清除目标" : "设为目标"))
            {
                targetGroupId = targetGroupId == selectedGroupId ? -1 : selectedGroupId;
            }
            y += RowBtn;

            // ── 手动操舵开关（用户 2026-10-02："操舵开关集成到左边；按下进入手动操舵，再按解除"）──
            //    按下 = 打开操舵 HUD（`Window_Steering`，键位/SAS/动作组都在那儿），再按 = 关窗解除。
            bool steeringThis = SteeringController.IsOpen && SteeringController.GroupId == g.GroupId;
            if (Widgets.ButtonText(new Rect(x, y, w, 24),
                    steeringThis ? "◼ 手动操舵（再按解除）" : "手动操舵"))
            {
                ToggleManualSteering(g);
            }
            y += RowBtn;

            // 武器列表（逐把开火）
            for (int i = 0; i < shownWeapons; i++)
            {
                var wp = weapons[i];
                string wState = wp.isOverheated ? "过热" : (wp.cooldownRemaining > 0 ? "冷却" + wp.cooldownRemaining : "就绪");
                Widgets.Label(new Rect(x, y, w - 60, RowText), $"W{i} {wp.def.defName} [{wState}]");
                if (Widgets.ButtonText(new Rect(x + w - 55, y, 50, RowText), "开火"))
                    FireAtTarget(wp);
                y += 22;
            }
            if (weapons.Count > shownWeapons)
            {
                Widgets.Label(new Rect(x, y, w, RowText - 2), $"... +{weapons.Count - shownWeapons} 把武器");
                y += RowText;
            }
            y += 4;

            if (Widgets.ButtonText(new Rect(x, y, w, 24), "全部开火"))
            {
                if (target != null)
                    foreach (var wp in weapons)
                        if (wp.CanFire())
                            FireAtTarget(wp);
            }
            y += RowBtn;

            // 目标操作
            if (target != null)
            {
                if (Widgets.ButtonText(new Rect(x, y, w, 24), "把目标传送到正前方 1km"))
                    TeleportTargetHere();
                y += 26;
                if (Widgets.ButtonText(new Rect(x, y, w * 0.48f, 24), "目标 +100 m/s"))
                    AdjustTargetSpeed(100.0);
                if (Widgets.ButtonText(new Rect(x + w * 0.52f, y, w * 0.48f, 24), "目标 -100 m/s"))
                    AdjustTargetSpeed(-100.0);
                y += RowBtn;
            }

            // 舱室操作：进入舱室 / 预生成地图（测试）
            if (roomParts.Count > 0)
            {
                Widgets.Label(new Rect(x, y, w, 18), "<b>舱室:</b>");
                y += 20;
                for (int i = 0; i < roomParts.Count; i++)
                {
                    var rp = roomParts[i];
                    var ent = roomEntities[i];
                    string label = rp.RoomTypeDef != null
                        ? rp.RoomTypeDef.label + " P" + rp.partId
                        : "舱室 P" + rp.partId;
                    bool hasMap = rp.linkedMapId >= 0 && SpaceMapGenerator.FindPocketMap(rp.linkedMapId) != null;
                    if (hasMap) label += " (已生成)";

                    if (Widgets.ButtonText(new Rect(x, y, w, 22), "进入 " + label))
                    {
                        var m = SpaceMapGenerator.GenerateRoomMap(ent, rp);
                        if (m != null) SpaceMapGenerator.EnterPocketMap(m);
                    }
                    y += 26;
                }
                if (Widgets.ButtonText(new Rect(x, y, w, 22), "预生成所有舱室地图（测试）"))
                {
                    for (int i = 0; i < roomParts.Count; i++)
                        SpaceMapGenerator.GenerateRoomMap(roomEntities[i], roomParts[i]);
                }
                y += 26;
            }

            if (Widgets.ButtonText(new Rect(x, y, w, 24), "取消选中"))
            {
                selectedGroupId = -1;
                targetGroupId = -1;
                InitCameraForCurrentBody();
            }
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
            if (open != null && SteeringController.IsOpen && SteeringController.GroupId == g.GroupId)
            {
                open.Close();
                return;
            }

            if (open != null)
                open.Close();   // 换船：先解除旧的（见上面注释的顺序陷阱）

            Map? shipMap = g.GetLinkedMapComp()?.map;
            if (!CompSteeringConsole.HasConsole(shipMap))
            {
                Messages.Message("本编队没有可用的操舵台（未安装 / 已损坏 / 被拆）——手动操舵需要船上有操舵台。",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            SteeringController.Open(g.GroupId);
            // absorbAroundWindow: false —— PFView 这条路上鼠标要留给 3D 相机（旋转/平移/缩放）；
            // 船上操舵台那条路仍是 true（操舵时别让点击漏到地图上误下命令）。
            Find.WindowStack.Add(new Window_Steering(absorbAroundWindow: false));
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
            return string.Format("方位 {0:F0}° / 仰角 {1:F0}°", azimuth, elevation);
        }

        /// <summary>获取编队在行星系相对坐标下的当前位置（与 CombatManager 开火基准一致）。</summary>
        private DVector3 GetGroupRelPos(SpaceEntityGroup g)
        {
            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            return g.IsCircularOrbit ? g.GetCircularPositionAtTick(physicsTick) : g.CurrentOrbit.positionSystem.v;
        }

        private void FireAtTarget(WeaponInstance weapon)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var combatManager = Current.Game?.GetComponent<CombatManager>();
            var target = targetGroupId >= 0 ? spaceGame?.GetGroupById(targetGroupId) : null;
            if (combatManager == null || target == null) return;

            DVector3 targetPos = GetGroupRelPos(target);
            combatManager.FireWeapon(selectedGroupId, weapon, targetPos);
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

            Log.Message(string.Format(
                "[PFView] 传送 G{0} → G{1} 前方1km | shooter circular={2} pos=({3:F0},{4:F0},{5:F0}) vel=({6:F0},{7:F0},{8:F0}) | target circular={9} pos=({10:F0},{11:F0},{12:F0}) vel=({13:F0},{14:F0},{15:F0})",
                target.GroupId, shooter.GroupId,
                shooter.IsCircularOrbit, shooterPos.x, shooterPos.y, shooterPos.z,
                shooterVel.x, shooterVel.y, shooterVel.z,
                target.IsCircularOrbit, target.CurrentOrbit.positionSystem.x, target.CurrentOrbit.positionSystem.y, target.CurrentOrbit.positionSystem.z,
                target.CurrentOrbit.velocitySystem.x, target.CurrentOrbit.velocitySystem.y, target.CurrentOrbit.velocitySystem.z));
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

            Log.Message(string.Format("[PFView] G{0} 速度沿射向 G{1} 方向调整 {2:+#;-#;0} m/s", target.GroupId, shooter.GroupId, dv));
        }

        /// <summary>部署一对 100m 间距的交火测试船（敌对 faction），绕当前系统天体 500km 轨道。</summary>
        private void DeployCombatTestPair()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;

            int gidA = spaceGame.AllocGroupId();
            int gidB = gidA + 1;
            int eidA = spaceGame.AllocEntityId();
            int eidB = eidA + 1;

            int bodyId = currentSystemBodyId;
            double orbitRadius = SpaceConstants.PlanetRadiusMeters + 500000.0;
            double angle = Find.TickManager.TicksGame * 0.001;
            DVector3 basePos = new DVector3(System.Math.Cos(angle) * orbitRadius, 0, System.Math.Sin(angle) * orbitRadius);
            double mu = spaceGame.GetBodyGM(bodyId);
            double speed = System.Math.Sqrt(mu / orbitRadius);
            DVector3 vel = new DVector3(-System.Math.Sin(angle) * speed, 0, System.Math.Cos(angle) * speed);

            CreateTestShip(spaceGame, gidA, eidA, basePos, vel, Faction.OfPlayer.loadID, bodyId);   // [V12] 真阵营 id
            CreateTestShip(spaceGame, gidB, eidB, basePos + new DVector3(200, 0, 0), vel, FactionResolver.HostileFactionIdForPlayer(), bodyId);

            selectedGroupId = gidA;
            targetGroupId = gidB;
            InitCameraForCurrentBody();

            Log.Message(string.Format("[PFView] 部署交火测试对: G{0}(f0) vs G{1}(f1) 间距200m @ {2:F0}km 轨道",
                gidA, gidB, orbitRadius / 1000.0));
        }

        /// <summary>创建单 part 测试船（Lite 裁剪：无 Ship3D 蓝本，直接构造唯一 part 盒体）。</summary>
        private static void CreateTestShip(SpaceGameComp spaceGame, int groupId, int entityId,
            DVector3 pos, DVector3 vel, int factionId, int bodyId)
        {
            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = bodyId
            };
            group.CurrentOrbit = new OrbitState { positionSystem = (SystemVec3)pos, velocitySystem = (SystemVec3)vel, rotation = Quaternion.identity };
            spaceGame.RegisterGroup(group);
            group.EnterCircularOrbit(Find.TickManager.TicksGame, bodyId);

            var entity = new SpaceEntity(entityId, factionId)
            {
                GroupId = groupId,
                IsCollidable = true,
                IsMovable = true,
                IsAffectedByGravity = true
            };
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);
            entity.SetParts(new List<SpacePart>
            {
                new SpacePart
                {
                    partId = 1,
                    defName = "PF_TestShip",
                    positionShip = DVector3.zero,
                    boundsSizeShip = new DVector3(100.0, 40.0, 100.0)
                }
            });
            spaceGame.RegisterEntity(entity);
        }
    }
}
