using System.Collections.Generic;
using System.Globalization;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
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
            float header = uiLayout.Compact ? 8f : 42f;
            if (!uiLayout.Compact)
            {
                MiniLabel(new(rect.x + 8, rect.y + 7, rect.width - 88, 27),
                    "PFV_CockpitDetails".Translate(), PFViewPalette.TextPrimary);
                if (Widgets.ButtonText(new(rect.xMax - 72, rect.y + 7, 64, 27), "PFV_Collapse".Translate()))
                    SetDrawer(PFViewDrawerTab.None);
            }

            var space = Current.Game?.GetComponent<SpaceGameComp>();
            var selected = selectedGroupId >= 0 ? space?.GetGroupById(selectedGroupId) : null;
            RefreshInspectorRooms(selected);
            bool own = CanOperateGroup(selected, false, out _);
            fleetSnapshots.TryGetValue(selectedGroupId, out var fleet);
            float height = inspectedContactKey != null ? 328f : selected == null ? 0f : 90f;
            if (own) height += 132f + (showInspectorRooms ? inspectorRooms.Count * 33f : 0);
            if (own && fleet != null) height += FleetInspectorHeight(fleet) + 33f;
            height += 16f;
            var outer = new Rect(rect.x + 8, rect.y + header, rect.width - 16, Mathf.Max(1, rect.height - header - 8));
            var content = new Rect(0, 0, outer.width - 18, Mathf.Max(height, outer.height));
            Widgets.BeginScrollView(outer, ref inspectorScroll, content);
            float y = 0;
            if (inspectedContactKey != null && visibleContacts.TryGetValue(inspectedContactKey, out Contact contact))
            {
                Widgets.Label(new(0, y, content.width, 28), ContactDisplayLabel(contact));
                y += 32;
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
                if (own)
                {
                    if (fleet != null && InspectorButton(content, ref y, "PFV_FleetManeuvers".Translate()))
                        OpenFleetManeuvers(selected.GroupId, false);
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
            else Widgets.Label(new(0, y, content.width, 28), "PFV_NoSelection".Translate());
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
    }
}
