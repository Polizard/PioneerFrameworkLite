using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
        private PFViewDrawerTab drawerTab;
        private readonly List<string> cockpitAlerts = new(8);

        private static string CockpitGroupLabel(SpaceEntityGroup? group) => group == null ? "—" : "G" + group.GroupId;

        private void SetDrawer(PFViewDrawerTab tab)
        {
            drawerTab = tab;
            preferObjectList = tab != PFViewDrawerTab.Details;
            if (tab != PFViewDrawerTab.Objects && objectSearchFocused)
            {
                GUI.FocusControl(null);
                objectSearchFocused = false;
            }
        }

        private void ToggleDrawer(PFViewDrawerTab tab)
        {
            bool same = tab == PFViewDrawerTab.Status
                ? drawerTab is PFViewDrawerTab.Status or PFViewDrawerTab.Weapons or PFViewDrawerTab.Radar
                : drawerTab == tab;
            SetDrawer(same ? PFViewDrawerTab.None : tab);
        }

        private void DrawCockpitDrawer(Rect rect)
        {
            if (rect.width <= 0) return;
            Widgets.DrawBoxSolid(rect, PFViewPalette.PanelBg);
            Text.Font = GameFont.Tiny;
            if (Widgets.ButtonText(new(rect.xMax - 27, rect.y + 4, 23, 28), "×"))
                SetDrawer(PFViewDrawerTab.None);
            if (uiLayout.DrawerTab is PFViewDrawerTab.Objects or PFViewDrawerTab.Details)
            {
                if (uiLayout.DrawerTab == PFViewDrawerTab.Objects && uiLayout.ObjectActions.Height < 28f)
                {
                    DrawObjectActions(new(rect.x, rect.y + 4, rect.width - 28, 28));
                    return;
                }
                string key = uiLayout.DrawerTab == PFViewDrawerTab.Objects ? "PFV_CockpitQueue" : "PFV_CockpitDetails";
                MiniLabel(new(rect.x + 8, rect.y + 4, rect.width - 40, 28), key.Translate(), PFViewPalette.TextPrimary);
                return;
            }

            float width = (rect.width - 34) / 3;
            for (int i = 0; i < 3; i++)
            {
                var tab = PFViewDrawerTab.Status + i;
                string title = ("PFV_CockpitTab_" + tab).Translate();
                var button = new Rect(rect.x + 4 + i * width, rect.y + 4, width - 2, 28);
                if (Widgets.ButtonText(button, title) && uiLayout.DrawerTab != tab) SetDrawer(tab);
                if (uiLayout.DrawerTab == tab)
                    Widgets.DrawBoxSolid(new(button.x, button.yMax, button.width, 2), PFViewPalette.Accent);
            }
        }

        private static bool CockpitButton(Rect rect, string label, bool enabled = true, string? tip = null)
        {
            bool previous = GUI.enabled;
            GUI.enabled = previous && enabled;
            bool clicked = Widgets.ButtonText(rect, label);
            GUI.enabled = previous;
            if (!string.IsNullOrEmpty(tip)) TooltipHandler.TipRegion(rect, tip);
            return clicked && previous && enabled;
        }

        private void DrawObjectActions(Rect rect)
        {
            if (rect.height < 28f) return;
            var selected = Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(selectedGroupId);
            bool groupKnown = CanInspectGroup(selected?.GroupId ?? -1);
            var contact = default(Contact);
            bool contactKnown = inspectedContactKey != null && visibleContacts.TryGetValue(inspectedContactKey, out contact);
            string label = groupKnown ? CockpitGroupLabel(selected) : contactKnown ? ContactDisplayLabel(contact) : "—";
            string title = "PFV_CockpitObjectActions".Translate(label);
            Widgets.DrawBoxSolid(rect, PFViewPalette.PanelBg);
            if (!groupKnown && !contactKnown)
            {
                MiniLabel(new(rect.x + 8, rect.y + 4, rect.width - 16, 22), title, PFViewPalette.TextPrimary);
                return;
            }

            int id = selected?.GroupId ?? -1;

            // 火炬转移状态（2026-11-19）：阶段 + 距目标距离（由物理步探测刷新）
            if (selected != null && selected.torchPhase > 0 && selected.torchTargetDistance > 0.0)
            {
                string phase = selected.torchPhase switch
                {
                    1 => "出航段",
                    2 => "捕获段",
                    3 => "等待圆化",
                    4 => "已圆化",
                    _ => "—",
                };
                double ratio = selected.torchSoi > 0.0 ? selected.torchTargetDistance / selected.torchSoi : 0.0;
                MiniLabel(new(rect.x + 8, rect.y + 26, rect.width - 16, 20),
                    $"火炬 {phase}｜距目标 {selected.torchTargetDistance / 1e9:F2}e9 m（{ratio:F2}×SOI）",
                    PFViewPalette.TextSecondary);
            }
            bool hasMap = groupKnown && RefreshNavigableGroupMaps(id);
            bool canTarget = contactKnown ? !contact.isDecoy : id != ResolveOperationGroup()?.GroupId;
            bool canOpenDetails = !WantsInspector;
            if (rect.height < 92f)
            {
                if (CockpitButton(new(rect.x + 8, rect.y + (rect.height - 28f) * .5f, rect.width - 16, 28), title))
                {
                    var options = new List<FloatMenuOption>();
                    if (groupKnown)
                    {
                        options.Add(new("PFV_Focus".Translate(), () => FocusGroup(id)));
                        options.Add(new("PFV_EnterMap".Translate(), hasMap ? () => ShowGroupMapMenu(id) : null));
                    }
                    options.Add(new("PFV_SetTarget".Translate(), canTarget ? () => TargetInspectedObject(selected, contactKnown, contact) : null));
                    options.Add(new("PFV_CockpitDetails".Translate(), canOpenDetails ? ShowObjectDetails : null));
                    Find.WindowStack.Add(new FloatMenu(options));
                }
                return;
            }

            MiniLabel(new(rect.x + 8, rect.y + 4, rect.width - 16, 22), title, PFViewPalette.TextPrimary);
            Text.Font = GameFont.Small;
            float width = (rect.width - 22) * .5f;
            float x = rect.x + 8, y = rect.y + 30;
            if (groupKnown)
            {
                if (CockpitButton(new(x, y, width, 28), "PFV_Focus".Translate())) FocusGroup(id);
                if (CockpitButton(new(x + width + 6, y, width, 28), "PFV_EnterMap".Translate(), hasMap))
                    ShowGroupMapMenu(id);
                y += 32f;
            }
            if (CockpitButton(new(x, y, width, 28), "PFV_SetTarget".Translate(), canTarget))
                TargetInspectedObject(selected, contactKnown, contact);
            if (CockpitButton(new(x + width + 6, y, width, 28), "PFV_CockpitDetails".Translate(), canOpenDetails))
                ShowObjectDetails();
        }

        private void ShowObjectDetails() => SetDrawer(PFViewDrawerTab.Details);

        private void TargetInspectedObject(SpaceEntityGroup? selected, bool contactKnown, Contact contact)
        {
            if (contactKnown) SetTargetContact(contact);
            else if (selected != null) SetTargetGroup(selected.GroupId);
        }

        private void RequestManualSteering(int groupId)
        {
            var game = Current.Game;
            var group = game?.GetComponent<SpaceGameComp>()?.GetGroupById(groupId);
            if (SteeringController.IsOpen && SteeringController.GroupId == groupId) return;
            if (!CanOperateGroup(group?.GroupId ?? -1, true, out string reason))
            {
                QueueToast(reason);
                return;
            }
            var source = ResolveOperationGroup();
            if (source == null || source.GroupId == groupId)
            {
                ToggleManualSteering(group!.GroupId);
                return;
            }

            int sourceId = source.GroupId;
            string key = SteeringController.IsOpen ? "PFV_CockpitSwitchManual" : "PFV_CockpitSwitchSource";
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                key.Translate(CockpitGroupLabel(source), CockpitGroupLabel(group)), () =>
                {
                    if (Current.Game != game || ResolveOperationGroup()?.GroupId != sourceId)
                    {
                        QueueToast("PFV_SourceLost".Translate());
                        return;
                    }
                    var current = game?.GetComponent<SpaceGameComp>()?.GetGroupById(groupId);
                    if (CanOperateGroup(current?.GroupId ?? -1, true, out string rejected)) ToggleManualSteering(current!.GroupId);
                    else QueueToast(rejected);
                }));
        }

        private void RequestAutomatic(int groupId)
        {
            if (!SteeringController.IsOpen)
            {
                OpenFleetManeuvers(groupId, true);
                return;
            }
            var game = Current.Game;
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("PFV_CockpitAutomaticConfirm".Translate("G" + groupId), () =>
            {
                if (Current.Game != game || !SteeringController.IsOpen || SteeringController.GroupId != groupId)
                {
                    QueueToast("PFV_SourceLost".Translate());
                    return;
                }
                var group = game?.GetComponent<SpaceGameComp>()?.GetGroupById(groupId);
                if (!CanOperateGroup(group?.GroupId ?? -1, false, out string reason))
                {
                    QueueToast(reason);
                    return;
                }
                EndSteering();
                OpenFleetManeuvers(groupId, true);
            }));
        }

        private void ShowAutomaticCommands(int groupId, int inspectedId)
        {
            if (inspectedId < 0 || inspectedId == groupId)
            {
                RequestAutomatic(groupId);
                return;
            }
            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
            {
                new("PFV_CockpitQueueFor".Translate("G" + groupId), () => RequestAutomatic(groupId)),
                new("PFV_CockpitQueueFor".Translate("G" + inspectedId), () => OpenFleetManeuvers(inspectedId, true))
            }));
        }

        private void ShowManualCommands(int groupId, int alternateId)
        {
            if (alternateId < 0)
            {
                RequestManualSteering(groupId);
                return;
            }
            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
            {
                new("PFV_CockpitManual".Translate("G" + groupId), () => RequestManualSteering(groupId)),
                new("PFV_CockpitManual".Translate("G" + alternateId), () => RequestManualSteering(alternateId))
            }));
        }

        private void EndManualSteeringFor(int groupId)
        {
            if (SteeringController.IsOpen && SteeringController.GroupId == groupId) EndSteering();
        }

        private bool CanReturnToPilotShip(int groupId)
            => groupId != selectedGroupId
                || (uiLayout.ObjectActions.Height < 28f && uiLayout.DrawerTab != PFViewDrawerTab.Objects);

        private void DrawFlightControls(Rect rect, SpaceEntityGroup? cockpit)
        {
            if (rect.width <= 6 || rect.height < 28) return;
            float y = rect.y + (rect.height - 28f) * .5f;
            Text.Font = GameFont.Small;
            Widgets.DrawBoxSolid(rect, PFViewPalette.PanelBg);
            int id = cockpit?.GroupId ?? -1;
            bool manual = SteeringController.IsOpen;
            var inspected = Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(selectedGroupId);
            int inspectedId = CanOperateGroup(inspected?.GroupId ?? -1, false, out _) ? inspected!.GroupId : -1;
            bool canTakeInspection = CanOperateGroup(inspected?.GroupId ?? -1, true, out _) && inspectedId != id;
            bool cockpitCanSteer = CanOperateGroup(cockpit?.GroupId ?? -1, true, out string manualReason);
            var manualGroup = !manual && !cockpitCanSteer && canTakeInspection ? inspected : cockpit;
            int alternateManualId = !manual && cockpitCanSteer && canTakeInspection ? inspectedId : -1;
            int manualId = manual ? SteeringController.GroupId : manualGroup?.GroupId ?? -1;
            bool canAuto = CanOperateGroup(cockpit?.GroupId ?? -1, false, out string autoReason);
            bool canManual = cockpitCanSteer || (!manual && canTakeInspection);
            bool canTransfer = manual && canTakeInspection;
            bool needsPilotMenu = manual && rPilotPanel.width <= 0;
            string ship = CockpitGroupLabel(cockpit);
            string autoLabel = "PFV_CockpitAutomatic".Translate();
            string manualLabel = alternateManualId >= 0 ? "PFV_CockpitManualChoose".Translate()
                : (manual ? "PFV_CockpitEndManual" : "PFV_CockpitManual").Translate(CockpitGroupLabel(manualGroup));
            string transferLabel = canTransfer ? "PFV_CockpitTakeOverGroup".Translate("G" + inspectedId).ToString() : "";
            float minInlineWidth = canTransfer ? 274f : 188f;
            if (rect.width < minInlineWidth || needsPilotMenu)
            {
                if (CockpitButton(new(rect.x + 3, y, rect.width - 6, 28), "PFV_CockpitFlightControls".Translate()))
                {
                    Action? manualAction = manual ? () => EndManualSteeringFor(manualId)
                        : canManual ? () => ShowManualCommands(manualId, alternateManualId) : null;
                    var options = new List<FloatMenuOption>
                    {
                        new(autoLabel + " · " + ship, canAuto ? () => ShowAutomaticCommands(id, inspectedId) : null),
                        new(manualLabel, manualAction)
                    };
                    if (canTransfer) options.Add(new(transferLabel, () => RequestManualSteering(inspectedId)));
                    if (needsPilotMenu)
                    {
                        if (CanReturnToPilotShip(manualId))
                            options.Add(new("PFV_Pilot_Focus".Translate(), () => FocusGroup(manualId)));
                        options.Add(new("PFV_Pilot_Details".Translate(), OpenPilotDetails));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
                return;
            }

            Text.Font = rect.width < 300f ? GameFont.Tiny : GameFont.Small;
            int buttons = canTransfer ? 3 : 2;
            float width = (rect.width - 5 * (buttons + 1)) / buttons;
            float x = rect.x + 5;
            if (CockpitButton(new(x, y, width, 28), autoLabel, canAuto, canAuto ? ship : autoReason))
                ShowAutomaticCommands(id, inspectedId);
            if (CockpitButton(new(x + width + 5, y, width, 28), manualLabel, manual || canManual,
                manual ? null : manualReason))
            {
                if (manual) EndManualSteeringFor(manualId);
                else ShowManualCommands(manualId, alternateManualId);
            }
            if (canTransfer && CockpitButton(new(x + 2 * (width + 5), y, width, 28), transferLabel))
                RequestManualSteering(inspectedId);
        }

        private static readonly PFViewFleetAlert[] CockpitAlertOrder =
        {
            PFViewFleetAlert.Crippled, PFViewFleetAlert.PropellantEmpty, PFViewFleetAlert.LowPropellant,
            PFViewFleetAlert.Damage, PFViewFleetAlert.DisabledWeapons, PFViewFleetAlert.Overheated, PFViewFleetAlert.EmptyWeapons
        };

        private void RefreshCockpitAlerts(SpaceEntityGroup? cockpit)
        {
            cockpitAlerts.Clear();
            if (cockpit == null) return;
            fleetSnapshots.TryGetValue(cockpit.GroupId, out var snapshot);
            var alerts = snapshot?.Alerts ?? PFViewFleetAlert.None;
            foreach (var alert in CockpitAlertOrder)
            {
                if (alert == PFViewFleetAlert.PropellantEmpty && SteeringController.IsOpen
                    && SteeringController.GroupId == cockpit.GroupId && SteeringController.ThrustUnavailable)
                    cockpitAlerts.Add("PFV_Pilot_NoThrust".Translate());
                if ((alerts & alert) != 0) cockpitAlerts.Add(("PFV_FleetAlert_" + alert).Translate());
            }
        }

        private void DrawCockpitAlert(Rect rect, SpaceEntityGroup? cockpit)
        {
            if (rect.width <= 0 || cockpitAlerts.Count == 0) return;
            Widgets.DrawBoxSolid(rect, PFViewPalette.PanelBgRaised);
            string line = "! " + CockpitGroupLabel(cockpit) + " · " + cockpitAlerts[0];
            if (cockpitAlerts.Count > 1) line += "  +" + (cockpitAlerts.Count - 1);
            MiniLabel(rect.ContractedBy(4), line, PFViewPalette.TextPrimary, TextAnchor.MiddleCenter);
            if (Mouse.IsOver(rect)) TooltipHandler.TipRegion(rect, string.Join("\n", cockpitAlerts));
        }

        private void DrawShipStatus(Rect rect, SpaceEntityGroup? cockpit)
        {
            if (rect.width <= 0) return;
            Widgets.DrawBoxSolid(rect, PFViewPalette.PanelBg);
            UiKit.SpacecraftUIDecor.DrawCornerLBrackets(rect, PFViewPalette.Border);
            float x = rect.x + 8, y = rect.y + 6, width = rect.width - 16;
            MiniLabel(new(x, y, width, 22), "PFV_CockpitShipStatus".Translate(CockpitGroupLabel(cockpit)), PFViewPalette.TextPrimary);
            if (cockpit == null || !fleetSnapshots.TryGetValue(cockpit.GroupId, out var fleet))
            {
                MiniLabel(new(x, y + 28, width, 24), "—", PFViewPalette.TextSecondary);
                return;
            }
            if (fleet.MemberCount == 1)
            {
                var entity = Current.Game?.GetComponent<SpaceGameComp>()?.GetEntityById(fleet.Members[0].EntityId);
                if (entity?.GroupId != cockpit.GroupId || !DrawHullDiagram(new(x, y + 26, 104, 90), entity))
                    MiniLabel(new(x, y + 34, 104, 24), "—", PFViewPalette.TextSecondary, TextAnchor.MiddleCenter);
            }
            else if (fleet.MemberCount > 1)
                MiniLabel(new(x, y + 34, 104, 24), "PFV_CockpitMembers".Translate(fleet.MemberCount), PFViewPalette.TextPrimary);
            else
                MiniLabel(new(x, y + 34, 104, 24), "—", PFViewPalette.TextSecondary);

            x += 110;
            width -= 110;
            MiniLabel(new(x, y + 28, width, 24), FleetFuel(fleet), PFViewPalette.TextPrimary);
            MiniLabel(new(x, y + 52, width, 24), "PFV_CockpitDestroyed".Translate(fleet.DestroyedParts), PFViewPalette.TextSecondary);
            string weapons = fleet.UnloadedMaps ? "—" : fleet.ReadyWeapons + "/" + fleet.Weapons;
            MiniLabel(new(x, y + 76, width, 24), "PFV_CockpitReadyWeapons".Translate(weapons), PFViewPalette.TextSecondary);
        }

        private static bool DrawHullDiagram(Rect rect, SpaceEntity entity)
        {
            Vector2 min = new(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 max = new(float.NegativeInfinity, float.NegativeInfinity);
            Span<Vector2> corners = stackalloc Vector2[8];
            foreach (var part in entity.Parts)
            {
                if (!ProjectHullPart(part, corners)) continue;
                foreach (var point in corners)
                {
                    min = Vector2.Min(min, point);
                    max = Vector2.Max(max, point);
                }
            }
            Vector2 size = max - min;
            if (!float.IsFinite(size.x) || !float.IsFinite(size.y) || size.x <= 0 || size.y <= 0) return false;
            float scale = Mathf.Min((rect.width - 8) / size.x, (rect.height - 8) / size.y);
            Vector2 midpoint = min + size * .5f;
            foreach (var part in entity.Parts)
            {
                if (!ProjectHullPart(part, corners)) continue;
                Vector2 partMin = new(float.PositiveInfinity, float.PositiveInfinity);
                Vector2 partMax = new(float.NegativeInfinity, float.NegativeInfinity);
                for (int i = 0; i < corners.Length; i++)
                {
                    corners[i] = rect.center + (corners[i] - midpoint) * scale;
                    partMin = Vector2.Min(partMin, corners[i]);
                    partMax = Vector2.Max(partMax, corners[i]);
                }
                bool damaged = !part.isIndestructible && part.hp < SpacePart.MaxHp;
                Color color = part.isDestroyed ? PFViewPalette.StateDead
                    : damaged ? PFViewPalette.StateWarn : PFViewPalette.AccentDim;
                for (int i = 0; i < corners.Length; i++)
                for (int axis = 1; axis <= 4; axis *= 2)
                    if ((i & axis) == 0) Widgets.DrawLine(corners[i], corners[i | axis], color, 1);
                if (part.isDestroyed || damaged)
                    Widgets.DrawLine(new(partMin.x, partMax.y), new(partMax.x, partMin.y), PFViewPalette.TextPrimary, 1);
                if (part.isDestroyed)
                    Widgets.DrawLine(partMin, partMax, PFViewPalette.TextPrimary, 1);
            }
            return true;
        }

        private static bool ProjectHullPart(SpacePart part, Span<Vector2> corners)
        {
            PartGeometry.Axes(part.rotationShip, out var x, out var y, out var z);
            var size = part.boundsSizeShip * .5;
            for (int i = 0; i < corners.Length; i++)
            {
                var point = part.positionShip + x * ((i & 1) == 0 ? -size.x : size.x)
                    + y * ((i & 2) == 0 ? -size.y : size.y) + z * ((i & 4) == 0 ? -size.z : size.z);
                var screen = new Vector2((float)point.x, (float)-point.z);
                if (!float.IsFinite(screen.x) || !float.IsFinite(screen.y)) return false;
                corners[i] = screen;
            }
            return true;
        }
    }
}