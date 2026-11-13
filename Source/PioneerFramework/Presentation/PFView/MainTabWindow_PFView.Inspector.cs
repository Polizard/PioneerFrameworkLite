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
        private readonly List<RoomPartSnapshot> inspectorRooms = new();
        private readonly List<NetworkOptionSnapshot> networkBuf = new();
        private int inspectorRoomsGroup = -1;
        private float nextRoomRefresh;
        private bool showInspectorRooms;

        private void RefreshInspectorRooms(int groupId)
        {
            if (groupId == inspectorRoomsGroup && Time.realtimeSinceStartup < nextRoomRefresh) return;
            inspectorRoomsGroup = groupId;
            nextRoomRefresh = Time.realtimeSinceStartup + .25f;
            inspectorRooms.Clear();
            if (!CanOperateGroup(groupId, false, out _)) return;
            PhysicsQueries.CopyRoomParts(groupId, inspectorRooms);
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

            SpacecraftSnapshot selectedSnap = default;
            bool hasSelected = selectedGroupId >= 0 && PhysicsApi.TryGetSpacecraft(selectedGroupId, out selectedSnap);
            RefreshInspectorRooms(selectedGroupId);
            bool own = CanOperateGroup(selectedGroupId, false, out _);
            fleetSnapshots.TryGetValue(selectedGroupId, out var fleet);
            float height = inspectedContactKey != null ? 328f : hasSelected ? 90f : 0f;
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
            else if (hasSelected && TryGetKnownContact(selectedGroupId, out Contact known))
            {
                Widgets.Label(new(0, y, content.width, 28), "PFV_Group".Translate("G" + selectedGroupId));
                y += 32;
                string body = Current.Game?.GetComponent<CelestialBodyManager>()?.GetBodyName(selectedSnap.CelestialBodyId) ?? "—";
                InspectorLine(content, ref y, "PFV_Region".Translate(), body);
                InspectorLine(content, ref y, "PFV_Speed".Translate(), PFViewUiObjectListCore.FormatSpeed(known.velocity.magnitude));
                if (own && fleet != null) DrawFleetInspector(content, ref y, fleet);
                if (own)
                {
                    if (fleet != null && InspectorButton(content, ref y, "PFV_FleetManeuvers".Translate()))
                        OpenFleetManeuvers(selectedGroupId, false);
                    DrawNetworkSelectorButton(content, ref y, selectedGroupId);
                    if (InspectorButton(content, ref y, "自动抛级：" + (selectedSnap.AutoStageEnabled ? "开" : "关")))
                        PhysicsCommands.SetAutoStage(selectedGroupId, !selectedSnap.AutoStageEnabled);
                    if (InspectorButton(content, ref y, "立即抛级"))
                    {
                        bool separated = PhysicsCommands.SeparateStageMulti(selectedGroupId, out int[] newGroupIds, out string sepReason);
                        string ids = "";
                        for (int i = 0; i < newGroupIds.Length; i++)
                            ids += (i > 0 ? "、" : "") + "G" + newGroupIds[i];
                        Messages.Message(separated ? $"已抛级 → {ids}" : $"无法抛级：{sepReason}",
                            separated ? MessageTypeDefOf.TaskCompletion : MessageTypeDefOf.RejectInput, false);
                    }
                    if (InspectorButton(content, ref y, "PFV_Rooms".Translate(inspectorRooms.Count))) showInspectorRooms = !showInspectorRooms;
                    if (showInspectorRooms)
                    {
                        for (int i = 0; i < inspectorRooms.Count; i++)
                        {
                            var item = inspectorRooms[i];
                            string roomName = item.RoomLabel ?? "PFV_Room".Translate();
                            if (InspectorButton(content, ref y, "PFV_EnterRoom".Translate(roomName))
                                && CanOperateGroup(selectedGroupId, false, out _))
                                PhysicsCommands.EnterRoomMap(selectedGroupId, item.PartId);
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
        private void DrawNetworkSelectorButton(Rect content, ref float y, int groupId)
        {
            PhysicsApi.TryGetSpacecraft(groupId, out var snap);
            string label = string.IsNullOrEmpty(snap.EnabledNetworkDefName) ? "全部" : snap.EnabledNetworkDefName;
            if (!InspectorButton(content, ref y, "推进网络：" + label)) return;

            var options = new List<FloatMenuOption>
            {
                new("全部网络", () => PhysicsCommands.SetBurnNetwork(groupId, "", false)),
            };
            networkBuf.Clear();
            PhysicsQueries.CopyNetworkOptions(groupId, networkBuf);
            for (int i = 0; i < networkBuf.Count; i++)
            {
                var n = networkBuf[i];
                string kind = n.IsRcs ? "RCS" : "主";
                options.Add(new FloatMenuOption(
                    $"{n.Label}（{kind} ve {n.Ve:F0} ṁ {n.MassFlow:F1} 余 {n.AvailableKg:F0}kg）",
                    () => PhysicsCommands.SetBurnNetwork(groupId, n.DefName, n.IsRcs)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}
