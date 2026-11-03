using System.Collections.Generic;
using System.Globalization;
using PioneerFramework.Combat;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
        private const float WeaponRowHeight = 60f;
        private Vector2 inspectorScroll;
        private readonly List<(SpaceEntity entity, SpacePart part)> inspectorRooms = new();
        private int inspectorRoomsGroup = -1;
        private float nextRoomRefresh;
        private bool showInspectorRooms;

        private void RefreshInspectorRooms(SpaceEntityGroup? group)
        {
            int id = group?.GroupId ?? -1;
            if (id == inspectorRoomsGroup && Time.realtimeSinceStartup < nextRoomRefresh) return;
            inspectorRoomsGroup = id;
            nextRoomRefresh = Time.realtimeSinceStartup + .25f;
            inspectorRooms.Clear();
            if (!CanOperateGroup(group, false, out _)) return;
            var space = Current.Game?.GetComponent<SpaceGameComp>();
            foreach (int entityId in group!.GetEntityIds())
            {
                var entity = space?.GetEntityById(entityId);
                if (entity == null) continue;
                foreach (var part in entity.Parts)
                    if (part.IsRoomModulePart) inspectorRooms.Add((entity, part));
            }
        }

        private void DrawInspector(Rect rect)
        {
            if (rect.width <= 0 || rect.height <= 0) return;
            RefreshObjectRows();
            Widgets.DrawBoxSolid(rect, PFViewPalette.PanelBg);
            UiKit.SpacecraftUIDecor.DrawCornerLBrackets(rect, PFViewPalette.Border);
            Text.Font = GameFont.Small;
            if (Widgets.ButtonText(new(rect.x + 8, rect.y + 7, rect.width - 88, 27), "PFV_BackToList".Translate()))
                preferObjectList = true;
            if (Widgets.ButtonText(new(rect.xMax - 72, rect.y + 7, 64, 27), "PFV_Collapse".Translate()))
                ClearInspection();

            var space = Current.Game?.GetComponent<SpaceGameComp>();
            var selected = selectedGroupId >= 0 ? space?.GetGroupById(selectedGroupId) : null;
            RefreshInspectorRooms(selected);
            var combat = Current.Game?.GetComponent<CombatManager>();
            var source = ResolveOperationGroup();
            int weapons = operationContext.WeaponsOpen && source != null ? combat?.GetGroupWeapons(source.GroupId).Count ?? 0 : 0;
            bool own = CanOperateGroup(selected, false, out _);
            fleetSnapshots.TryGetValue(selectedGroupId, out var fleet);
            bool hasMap = selected != null && RefreshNavigableGroupMaps(selected.GroupId);
            float height = inspectedContactKey != null ? 361f : selected == null ? 0f : 123f;
            if (hasMap) height += 33f;
            if (selected != null && selected.GroupId != source?.GroupId) height += 33f;
            if (own) height += 198f + (source != null && source.GroupId != selected!.GroupId ? 33f : 0f)
                + (showInspectorRooms ? inspectorRooms.Count * 33f : 0);
            if (own && fleet != null) height += FleetInspectorHeight(fleet) + 66f;
            if (operationContext.WeaponsOpen) height += 133f + (weaponTarget.IsSet ? 33f : 0f) + weapons * WeaponRowHeight;
            height += 16f;
            var outer = new Rect(rect.x + 8, rect.y + 42, rect.width - 16, Mathf.Max(1, rect.height - 50));
            var content = new Rect(0, 0, outer.width - 18, Mathf.Max(height, outer.height));
            Widgets.BeginScrollView(outer, ref inspectorScroll, content);
            float y = 0;
            if (inspectedContactKey != null && visibleContacts.TryGetValue(inspectedContactKey, out Contact contact))
            {
                string id = contactDisplayIds[inspectedContactKey];
                Widgets.Label(new(0, y, content.width, 28), "PFV_Contact".Translate(id));
                y += 32;
                if (!contact.isDecoy && InspectorButton(content, ref y, "PFV_SetTarget".Translate())) SetTargetContact(contact);
                DrawContactDetails(content, ref y, contact);
            }
            else if (selected != null && TryGetKnownContact(selected.GroupId, out Contact known))
            {
                Widgets.Label(new(0, y, content.width, 28), "PFV_Group".Translate("G" + selected.GroupId));
                y += 32;
                string body = Current.Game?.GetComponent<CelestialBodyManager>()?.GetBodyName(selected.CelestialBodyId) ?? "—";
                InspectorLine(content, ref y, "PFV_Region".Translate(), body);
                InspectorLine(content, ref y, "PFV_Speed".Translate(), PFViewUiObjectListCore.FormatSpeed(known.velocity.magnitude));
                if (own && fleet != null) DrawFleetInspector(content, ref y, fleet);
                if (InspectorButton(content, ref y, "PFV_Focus".Translate())) FocusGroup(selected.GroupId);
                if (hasMap && InspectorButton(content, ref y, "PFV_EnterMap".Translate())) ShowGroupMapMenu(selected.GroupId);
                if (selected.GroupId != source?.GroupId
                    && InspectorButton(content, ref y, "PFV_SetTarget".Translate())) SetTargetGroup(selected.GroupId);
                if (CanOperateGroup(selected, false, out _))
                {
                    if (fleet != null)
                    {
                        if (InspectorButton(content, ref y, "PFV_FleetManeuvers".Translate())) OpenFleetManeuvers(selected.GroupId, false);
                        if (InspectorButton(content, ref y, "PFV_FleetQueue".Translate(fleet.ManeuverCount))) OpenFleetManeuvers(selected.GroupId, true);
                    }
                    string label = SteeringController.GroupId == selected.GroupId ? "PFV_StopSteering" : "PFV_StartSteering";
                    if (InspectorButton(content, ref y, label.Translate("G" + selected.GroupId))) ToggleManualSteering(selected);
                    if (InspectorButton(content, ref y, "PFV_OpenWeapons".Translate("G" + selected.GroupId))) TryOpenWeaponsFor(selected.GroupId);
                    DrawNetworkSelectorButton(content, ref y, selected);
                    if (InspectorButton(content, ref y, "自动抛级：" + (selected.autoStageEnabled ? "开" : "关")))
                        space?.SetAutoStage(selected, !selected.autoStageEnabled);
                    if (InspectorButton(content, ref y, "立即抛级"))
                    {
                        SpaceEntityGroup? separated = null;
                        string sepReason = "";
                        if (space != null) separated = space.TrySeparateStage(selected, out sepReason);
                        Messages.Message(separated != null ? $"已抛级 → G{separated.GroupId}" : $"无法抛级：{sepReason}",
                            separated != null ? MessageTypeDefOf.TaskCompletion : MessageTypeDefOf.RejectInput, false);
                    }
                    if (source != null && source.GroupId != selected.GroupId
                        && InspectorButton(content, ref y, "PFV_ChangeOperator".Translate("G" + selected.GroupId)))
                    {
                        if (SteeringController.IsOpen) ToggleManualSteering(selected);
                        else TryOpenWeaponsFor(selected.GroupId);
                    }
                    if (InspectorButton(content, ref y, "PFV_Rooms".Translate(inspectorRooms.Count))) showInspectorRooms = !showInspectorRooms;
                    if (showInspectorRooms)
                    {
                        for (int i = 0; i < inspectorRooms.Count; i++)
                        {
                            var item = inspectorRooms[i];
                            string roomName = item.part.RoomTypeDef?.label ?? "PFV_Room".Translate();
                            if (InspectorButton(content, ref y, "PFV_EnterRoom".Translate(roomName))
                                && CanOperateGroup(space?.GetGroupById(selected.GroupId), false, out _))
                            {
                                var map = SpaceMapGenerator.GenerateRoomMap(item.entity, item.part);
                                if (map != null) SpaceMapGenerator.EnterPocketMap(map);
                            }
                        }
                    }
                }
            }
            if (operationContext.WeaponsOpen) DrawOperationPanel(new(0, y + 12, content.width, content.height - y - 12));
            Widgets.EndScrollView();
        }

        private static void InspectorLine(Rect content, ref float y, string label, string value)
        {
            Widgets.Label(new(0, y, content.width * .43f, 25), label);
            Widgets.Label(new(content.width * .43f, y, content.width * .57f, 25), value);
            y += 29;
        }

        private static void DrawContactDetails(Rect content, ref float y, in Contact contact)
        {
            InspectorLine(content, ref y, "PFV_State".Translate(), (contact.isDecoy ? "PFV_DecoyIdentified" : "PFV_Unidentified").Translate());
            double now = TimeScaleManager.GetPhysicsTickSafe();
            InspectorLine(content, ref y, "PFV_Tracking".Translate(),
                (PFViewUiObjectListCore.ObservationIsCurrent(now, contact.lastUpdatePhysics) ? "PFV_TrackLive" : "PFV_TrackRecord").Translate());
            InspectorLine(content, ref y, "PFV_Error".Translate(), PFViewUiObjectListCore.FormatDistance(contact.positionSigma));
            double age = System.Math.Max(0, now - contact.lastUpdatePhysics);
            InspectorLine(content, ref y, "PFV_ObservationAge".Translate(), age.ToString("F1", CultureInfo.InvariantCulture) + " s");
            Widgets.Label(new(0, y, content.width, 48), "PFV_ObservedPosition".Translate());
            y += 50;
            Widgets.Label(new(0, y, content.width, 48), PFViewUiObjectListCore.FormatObservedPosition(
                contact.position.x, contact.position.y, contact.position.z));
            y += 50;
            Widgets.Label(new(0, y, content.width, 74), "PFV_ContactReadOnly".Translate());
            y += 80;
        }

        private static bool InspectorButton(Rect content, ref float y, string label)
        {
            bool clicked = Widgets.ButtonText(new(0, y, content.width, 27), label);
            y += 33;
            return clicked;
        }

        /// <summary>
        /// **推进网络选择**（2026-10-27）：列出当前舰的推进网络（主/RCS、ve、ṁ、余量），
        /// 玩家选一个就只启用它；"全部"恢复默认。选择落盘（`enabledNetworkDefName`），
        /// 机动未显式指定网络时沿用（见 `SpaceGameComp` 的 `pm.useNetworks ?? PlayerEnabledNetworks`）。
        /// </summary>
        private void DrawNetworkSelectorButton(Rect content, ref float y, SpaceEntityGroup group)
        {
            string label = string.IsNullOrEmpty(group.enabledNetworkDefName) ? "全部" : group.enabledNetworkDefName;
            if (!InspectorButton(content, ref y, "推进网络：" + label)) return;

            var options = new List<FloatMenuOption>
            {
                new("全部网络", () => group.ApplyNetworkSelection("", false)),
            };
            var space = Current.Game?.GetComponent<SpaceGameComp>();
            var src = space?.GetGroupPropulsionSource(group);
            if (src != null)
            {
                var seen = new HashSet<string>();
                foreach (var n in src.Networks)
                {
                    if (n.propellant == null || n.MassFlow <= 0f) continue;
                    string key = n.propellant.defName;
                    if (!seen.Add(key)) continue;
                    string kind = n.HasRcsEngines ? "RCS" : "主";
                    double avail = src.GetPropellantMass(n.propellant);
                    options.Add(new FloatMenuOption(
                        $"{n.propellant.label}（{kind} ve {n.Ve:F0} ṁ {n.MassFlow:F1} 余 {avail:F0}kg）",
                        () => group.ApplyNetworkSelection(key, n.HasRcsEngines)));
                }
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void DrawOperationPanel(Rect rect)
        {
            var source = ResolveOperationGroup();
            var combat = Current.Game?.GetComponent<CombatManager>();
            if (source == null || combat == null) return;
            float y = rect.y;
            Widgets.DrawLineHorizontal(0, y, rect.width);
            y += 8;
            Widgets.Label(new(0, y, rect.width, 28), "PFV_Operating".Translate("G" + source.GroupId));
            y += 32;
            Widgets.Label(new(0, y, rect.width, 44), "PFV_WeaponTarget".Translate(WeaponTargetLabel));
            y += 48;
            if (InspectorButton(rect, ref y, "PFV_CloseWeapons".Translate())) { CloseWeaponsPanel(); return; }
            if (weaponTarget.IsSet && InspectorButton(rect, ref y, "PFV_ClearTarget".Translate())) SetTargetGroup(-1);
            var weapons = combat.GetGroupWeapons(source.GroupId);
            for (int i = 0; i < weapons.Count; i++)
            {
                var weapon = weapons[i];
                string state = (weapon.isOverheated ? "PFV_Overheated" : weapon.CanFire() ? "PFV_Ready" : "PFV_NotReady").Translate();
                bool hasShots = weapon.def.UsesShotComponents;
                Widgets.Label(new(0, y, rect.width - (hasShots ? 144 : 58), 27), weapon.def.LabelCap + " · " + state);
                if (hasShots && Widgets.ButtonText(new(rect.width - 140, y, 82, 27), Combat.ShotSelectionUI.Label(weapon)))
                    Combat.ShotSelectionUI.Open(weapon);
                bool enabled = PFViewUiOperationContext.CanFire(source.GroupId, weaponTarget, true,
                    TryGetWeaponTarget(out _), weapon.CanFire());
                bool old = GUI.enabled;
                GUI.enabled = old && enabled;
                if (Widgets.ButtonText(new(rect.width - 54, y, 54, 27), "PFV_Fire".Translate())) TryFireAtTarget(source.GroupId, weapon);
                GUI.enabled = old;
                string range = PFViewUiObjectListCore.FormatDistance(SpaceAI.ContactTargetService.EffectiveRange(weapon));
                Widgets.Label(new(0, y + 27, rect.width, 27), "PFV_EngagementRange".Translate(range));
                y += WeaponRowHeight;
            }
        }
    }
}
