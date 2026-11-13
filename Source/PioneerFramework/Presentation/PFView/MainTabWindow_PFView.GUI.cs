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
            // F2 = 焦点天体信息面板开关（工具条太挤；双击天体也能打开）。
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.F2)
            {
                TogglePlanetInfoForFocusedBody();
                e.Use();
            }
        }

        /// <summary>
        /// 3D 视口左键（2026-10-18 视图系统 v2）：
        /// · 单击 = 只改选中（不动相机）；
        /// · 双击编队 = 选中 + 聚焦；天体视角下双击其他天体的球面 = 切换焦点；
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

            // Ctrl+左键 = 检视行星地表格（A 方案：逐格拾取 + 高亮 + 检视面板）。
            if (e.control && TryInspectPlanetTile(mouseInView, inRect.size)) { e.Use(); return; }

            if (!e.shift && e.clickCount >= 2 && !dragState.IsActive && focusKind == PFViewFocusKind.Body)
            {
                int bodyId = PickViewportBody(mouseInView, inRect.size);
                if (bodyId >= 0 && bodyId != focusBodyId)
                {
                    FocusBody(bodyId);
                    OpenPlanetInfo(bodyId);   // 双击天体 = 聚焦 + 看信息（A 方案行星信息面板的自然入口）
                    objectsDirty = true;
                    e.Use();
                    return;
                }
            }

            if (!e.shift)
            {
                ClearInspection();
            }
        }

        private int PickViewportBody(Vector2 mouseInView, Vector2 viewSize)
        {
            if (renderCamera == null || presentedBodySpheres.Count == 0) return -1;

            // RT 拉伸到整个视口。保留相机空间 Z = 1，让求交深度直接对应相机裁剪面。
            float tanHalfFov = Mathf.Tan(renderCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float x = (2f * mouseInView.x / viewSize.x - 1f) * tanHalfFov * renderCamera.aspect;
            float y = (1f - 2f * mouseInView.y / viewSize.y) * tanHalfFov;
            var cameraTransform = renderCamera.transform;
            Vector3 direction = cameraTransform.rotation * new Vector3(x, y, 1f);
            return PFViewCameraCore.PickCelestialBody(cameraTransform.position, direction,
                renderCamera.nearClipPlane, renderCamera.farClipPlane, presentedBodySpheres);
        }

        /// <summary>屏幕拾取最近的可见编队。与渲染**同钟**（U19：连续渲染时钟，不是阶梯 physicsTick）。</summary>
        private bool TryPickGroup(Vector2 mouseInView, Vector2 viewSize, out int bestId)
        {
            bestId = -1;
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null || renderCamera == null) return false;

            double tick = RenderTick();
            DVector3 origin = GetRenderOrigin(tick);

            var sysIds = GetCurrentSystemBodyIds(mgr); // 第 12 条：记忆化复用（此前每帧新建 HashSet + 填卫星）
            var sensor = Current.Game?.GetComponent<SensorManager>();   // [V3] 列表准入（本方法低频，直接取组件）

            float best = float.MaxValue;

            groupSnapshotBuf.Clear();
            PhysicsApi.CopySpacecrafts(groupSnapshotBuf);
            for (int gi = 0; gi < groupSnapshotBuf.Count; gi++)
            {
                var g = groupSnapshotBuf[gi];
                if (!g.IsActive || !sysIds.Contains(g.CelestialBodyId)) continue;
                int groupId = g.GroupId;
                if (!CanInspectGroup(groupId)) continue;
                if (sensor != null && !sensor.PlayerCanSee(groupId)) continue;   // [V3]/S4c：列表只列看得见的

                DVector3 gAbs = GetGroupPosAbs(groupId, tick);
                Vector3 vp = AbsToView(gAbs, origin);
                Vector3 sp = renderCamera.WorldToScreenPoint(vp);
                if (sp.z < 0) continue;

                float sx = sp.x / renderCamera.pixelWidth * viewSize.x;
                float sy = (1f - sp.y / renderCamera.pixelHeight) * viewSize.y;
                float d = Vector2.Distance(mouseInView, new Vector2(sx, sy));
                if (d < 25f && sp.z < best)
                {
                    best = sp.z;
                    bestId = groupId;
                }
            }

            return bestId >= 0;
        }

        private void DrawOverlay(Rect inRect)
        {
            uiBlockRects.Clear();   // 每帧重新登记（画在输入处理之前，故同帧内几何一致）

            RefreshObjectRows();
            var cockpit = ResolveCockpitGroup();
            RefreshCockpitAlerts(cockpit);
            ComputeLayout(inRect);

            // 边缘天体指示 + 世界标记先画（面板后画 ⇒ 自然遮挡）
            DrawBodyDirectionIndicators(inRect);
            DrawWorldMarkers(inRect);

            DrawToolbar();
            DrawCockpitDrawer(FromUiRect(uiLayout.Drawer, inRect));
            DrawObjectList(rLeftCol);
            DrawObjectActions(FromUiRect(uiLayout.ObjectActions, inRect));
            DrawInspector(rInspector);
            DrawShipStatus(FromUiRect(uiLayout.ShipStatus, inRect), cockpit);
            DrawCockpitWeapons(FromUiRect(uiLayout.Weapons, inRect), cockpit);
            if (rRadar.width > 0) DrawRadarPanel(ResolveInstrumentGroup());
            if (rNavball.width > 0) DrawNavballPanel(ResolveNavballGroupId());
            DrawSteeringStrip();
            DrawPilotPanel();
            DrawFlightControls(FromUiRect(uiLayout.FlightControls, inRect), cockpit);
            DrawCockpitAlert(FromUiRect(uiLayout.Alert, inRect), cockpit);
            DrawStatusLine();
            DrawPlanetInfoPanel(inRect);
            DrawSurfaceInspectPanel(inRect);

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

        private void ShowContextMenu(Vector2 mouseInRect, Rect inRect)
        {
            Vector2 mouseInView = mouseInRect - inRect.position;
            TryPickGroup(mouseInView, inRect.size, out int hit);
            var opts = new List<FloatMenuOption>();
            if (hit >= 0)
                opts.Add(new FloatMenuOption("PFV_InspectGroup".Translate("G" + hit), () => SelectGroupForInspection(hit)));
            opts.Add(new FloatMenuOption("PFV_ClearInspection".Translate(), ClearInspection));
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        // ── DebugTabMenu_PFL 入口（玩家 UI 不再直接暴露；引擎侧实现在 Debug/PFViewDebugBridge） ──
        internal void DebugDeployCombatPair()
        {
            if (!PFViewDebugBridge.DeployCombatTestPair(currentSystemBodyId, (first, second) =>
                {
                    selectedGroupId = first;
                    targetGroupId = second;
                    FocusGroup(first);
                }))
                QueueToast("需要锚点地图、敌对势力和交火测试舰模板");
        }
        internal void DebugTeleportTarget() => PFViewDebugBridge.TeleportTargetHere(selectedGroupId, targetGroupId);
        internal void DebugAdjustTargetSpeed(double dv) => PFViewDebugBridge.AdjustTargetSpeed(selectedGroupId, targetGroupId, dv);

        internal void DebugPregenerateRoomMaps()
        {
            if (selectedGroupId < 0) { QueueToast("先选中一个编队"); return; }
            int count = PFViewDebugBridge.PregenerateRoomMaps(selectedGroupId);
            if (count < 0) { QueueToast("先选中一个编队"); return; }
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
        private void ToggleManualSteering(int groupId)
        {
            Window_Steering? open = FindSteeringWindow();

            // 已在操舵本编队 ⇒ 再按 = 解除（关窗；PostClose 里 Close + 推进器熄火）
            if (SteeringController.IsOpen && SteeringController.GroupId == groupId)
            {
                EndSteering();
                return;
            }

            if (!CanOperateGroup(groupId, true, out string reason))
            {
                QueueToast(reason);
                return;
            }

            if (open != null)
                open.Close();

            if (SteeringController.IsOpen) Window_Steering.StopControl();
            SteeringController.Open(groupId, true);
            SynchronizeCockpitSource();
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
        private DVector3 GetGroupRelPos(int groupId)
        {
            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            return PhysicsQueries.TryGetGroupExactPosition(groupId, physicsTick, out var position)
                ? position : DVector3.zero;
        }

        private void FireAtTarget(WeaponInstance weapon)
        {
            TryFireAtTarget(ResolveOperationGroupId(), weapon);
        }
    }
}
