using System.Collections.Generic;
using System.Globalization;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
        private readonly List<PFViewUiObjectRow> objectRows = new();
        private readonly List<PFViewUiObjectRow> filteredObjectRows = new();
        private readonly Dictionary<string, Contact> visibleContacts = new();
        private readonly Dictionary<string, string> contactDisplayIds = new();
        private readonly HashSet<int> knownEntityIds = new();
        private readonly List<string> expiredContactKeys = new();
        private string objectQuery = "";
        private PFViewUiObjectFilter objectFilter = PFViewUiObjectFilter.OwnGroups;
        private PFViewUiObjectSort objectSort;
        private Vector2 objectScroll;
        private float nextObjectRefresh;
        private bool objectsDirty = true;
        private string? inspectedContactKey;
        private int nextContactDisplayId;
        private bool preferObjectList;
        private bool objectSearchFocused;
        internal bool HasTextInputFocus => objectSearchFocused;
        private string distanceReferenceLabel = "—";

        private bool WantsInspector => !preferObjectList
            && (selectedGroupId >= 0 || inspectedContactKey != null || operationContext.WeaponsOpen);

        private static string ContactKey(in Contact contact)
            => contact.targetGroupId >= 0 ? "g:" + contact.targetGroupId.ToString(CultureInfo.InvariantCulture)
                : "c:" + contact.decoyId.ToString(CultureInfo.InvariantCulture);

        private void SelectGroupForInspection(int groupId)
        {
            if (!CanInspectEntity(Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(groupId))) return;
            selectedGroupId = groupId;
            showFleetMembers = false;
            inspectedContactKey = null;
            preferObjectList = false;
        }

        private void SelectContactForInspection(in Contact contact, int observerFactionId)
        {
            if (Faction.OfPlayer?.loadID != observerFactionId) return;
            selectedGroupId = -1;
            inspectedContactKey = ContactKey(contact);
            preferObjectList = false;
        }

        private void RefreshObjectRows()
        {
            if (!objectsDirty && Time.realtimeSinceStartup < nextObjectRefresh) return;
            nextObjectRefresh = Time.realtimeSinceStartup + .25f;
            objectRows.Clear();
            filteredObjectRows.Clear();
            fleetSnapshots.Clear();
            fleetAlertCount = 0;
            visibleContacts.Clear();
            knownEntityIds.Clear();
            distanceReferenceLabel = "—";
            var space = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var sensor = Current.Game?.GetComponent<SensorManager>();
            var combat = Current.Game?.GetComponent<Combat.CombatManager>();
            var player = Faction.OfPlayer;
            if (space == null || mgr == null || player == null) return;

            var reference = ResolveOperationGroup() ?? GetFocusGroup();
            bool hasReference = TryGetKnownContact(reference?.GroupId ?? -1, out Contact knownReference);
            distanceReferenceLabel = hasReference ? "G" + reference!.GroupId.ToString(CultureInfo.InvariantCulture) : "—";
            var systems = GetCurrentSystemBodyIds(mgr);
            foreach (var group in space.GetAllGroups())
            {
                if (!CanInspectEntity(group)) continue;
                // 已知身份在区域筛选之前登记，区域外遥测不能被重新包装成未知接触。
                knownEntityIds.Add(group.GroupId);
                bool own = group.FactionId == player.loadID;
                if (!own && !systems.Contains(group.CelestialBodyId)) continue;
                // 匿踪接触的实体字段不进入列表；自己的遥测仍可读。
                if (!own && (group.IsStealth || sensor == null || !sensor.PlayerCanSee(group))) continue;
                if (!TryGetKnownContact(group.GroupId, out Contact known)) continue;
                string identifier = "G" + group.GroupId.ToString(CultureInfo.InvariantCulture);
                if (own && PFViewFleetTelemetry.TryCollect(space, combat, group.GroupId, player.loadID, out var snapshot))
                {
                    fleetSnapshots[group.GroupId] = snapshot;
                    if (snapshot.Alerts != PFViewFleetAlert.None) fleetAlertCount++;
                }
                double? distance = hasReference && reference!.CelestialBodyId == group.CelestialBodyId
                    ? (known.position - knownReference.position).magnitude : null;
                objectRows.Add(new("group:" + identifier, "PFV_Group".Translate(identifier), identifier,
                    own ? PFViewUiObjectKind.OwnGroup : PFViewUiObjectKind.OtherGroup, distance,
                    own ? mgr.GetBodyName(group.CelestialBodyId) ?? "—" : "PFV_Visible".Translate(), true, group.GroupId));
            }

            contactBuf.Clear();
            sensor?.AppendVisible(player.loadID, contactBuf);
            for (int i = 0; i < contactBuf.Count; i++)
            {
                Contact contact = contactBuf[i];
                if (contact.targetGroupId >= 0 && knownEntityIds.Contains(contact.targetGroupId)) continue;
                string key = ContactKey(contact);
                if (!contactDisplayIds.TryGetValue(key, out string? identifier))
                {
                    identifier = "C" + (++nextContactDisplayId).ToString(CultureInfo.InvariantCulture);
                    contactDisplayIds.Add(key, identifier);
                }
                visibleContacts[key] = contact;
                // Contact 不携带参考系 ID，不能用隐藏实体元数据给不同接触补距离。
                // 所有未识别接触使用相同只读操作，身份 Key 只用于会话内关联。
                objectRows.Add(new(key, "PFV_Contact".Translate(identifier), identifier,
                    PFViewUiObjectKind.Contact, null,
                    (contact.isDecoy ? "PFV_DecoyIdentified" : "PFV_Unidentified").Translate(), false, -1));
            }
            PFViewUiObjectListCore.FilterAndSort(objectRows, filteredObjectRows, objectQuery, objectFilter, objectSort);
            if (fleetAlertsOnly && objectFilter == PFViewUiObjectFilter.OwnGroups)
                filteredObjectRows.RemoveAll(row => !fleetSnapshots.TryGetValue(row.GroupId, out var data)
                    || data.Alerts == PFViewFleetAlert.None);
            expiredContactKeys.Clear();
            foreach (string key in contactDisplayIds.Keys)
                if (!visibleContacts.ContainsKey(key)) expiredContactKeys.Add(key);
            for (int i = 0; i < expiredContactKeys.Count; i++) contactDisplayIds.Remove(expiredContactKeys[i]);
            objectsDirty = false;
            if (inspectedContactKey != null && !visibleContacts.ContainsKey(inspectedContactKey))
            {
                ClearInspection();
                QueueToast("PFV_ContactLost".Translate());
            }
        }

        private void DrawObjectList(Rect rect)
        {
            if (rect.width <= 0 || rect.height <= 0) { objectSearchFocused = false; return; }
            RefreshObjectRows();
            Widgets.DrawBoxSolid(rect, PFViewPalette.PanelBg);
            UiKit.SpacecraftUIDecor.DrawCornerLBrackets(rect, PFViewPalette.Border);
            Text.Font = GameFont.Small;
            float x = rect.x + 8f, width = rect.width - 16f, y = rect.y + 6f;
            bool fleetView = objectFilter == PFViewUiObjectFilter.OwnGroups;
            Widgets.Label(new(x, y, fleetView ? width - 100 : width, 24),
                (fleetView ? "PFV_FleetOverview" : "PFV_Objects").Translate(filteredObjectRows.Count));
            if (fleetView && Widgets.ButtonText(new(x + width - 96, y, 96, 24),
                (fleetAlertsOnly ? "PFV_FleetShowAll" : "PFV_FleetAttention").Translate(fleetAlertCount)))
            {
                fleetAlertsOnly = !fleetAlertsOnly;
                objectsDirty = true;
                objectScroll = Vector2.zero;
            }
            GUI.SetNextControlName("PFViewSearch");
            string query = Widgets.TextField(new(x, y + 27, width, 28), objectQuery);
            objectSearchFocused = GUI.GetNameOfFocusedControl() == "PFViewSearch";
            if (objectQuery.Length == 0 && GUI.GetNameOfFocusedControl() != "PFViewSearch")
                MiniLabel(new(x + 5, y + 31, width - 10, 20), "PFV_Search".Translate(), PFViewPalette.TextSecondary);
            if (query != objectQuery) { objectQuery = query; objectsDirty = true; objectScroll = Vector2.zero; }
            if (Widgets.ButtonText(new(x, y + 60, width * .55f - 3, 26), ("PFV_Filter_" + objectFilter).Translate()))
            {
                objectFilter = (PFViewUiObjectFilter)(((int)objectFilter + 1) % 4);
                objectsDirty = true;
                objectScroll = Vector2.zero;
            }
            if (Widgets.ButtonText(new(x + width * .55f + 3, y + 60, width * .45f - 3, 26),
                    ("PFV_Sort_" + objectSort).Translate()))
            {
                objectSort = objectSort == PFViewUiObjectSort.Name ? PFViewUiObjectSort.Distance : PFViewUiObjectSort.Name;
                objectsDirty = true;
            }
            MiniLabel(new(x, y + 90, width, 20), fleetView ? "PFV_FleetAllRegions".Translate()
                : "PFV_DistanceReference".Translate(distanceReferenceLabel), PFViewPalette.TextSecondary);
            var scrollRect = new Rect(x, y + 116, width, Mathf.Max(1, rect.yMax - y - 124));
            float rowsHeight = 0;
            for (int i = 0; i < filteredObjectRows.Count; i++)
                rowsHeight += fleetSnapshots.ContainsKey(filteredObjectRows[i].GroupId) ? FleetRowHeight : 54f;
            var content = new Rect(0, 0, width - 18f, Mathf.Max(scrollRect.height, rowsHeight));
            Widgets.BeginScrollView(scrollRect, ref objectScroll, content);
            if (filteredObjectRows.Count == 0)
                Widgets.Label(new(3, 4, content.width - 6, 44), "PFV_NoObjects".Translate());
            float rowY = 0;
            for (int i = 0; i < filteredObjectRows.Count; i++)
            {
                var row = filteredObjectRows[i];
                if (fleetSnapshots.TryGetValue(row.GroupId, out var fleet))
                {
                    if (rowY + FleetRowHeight >= objectScroll.y && rowY <= objectScroll.y + scrollRect.height)
                        DrawFleetCard(new(0, rowY, content.width, FleetRowHeight - 4), row, fleet);
                    rowY += FleetRowHeight;
                    continue;
                }
                var rr = new Rect(0, rowY, content.width, 50f);
                rowY += 54f;
                if (rr.yMax < objectScroll.y || rr.y > objectScroll.y + scrollRect.height) continue;
                bool selected = row.Kind == PFViewUiObjectKind.Contact ? row.Key == inspectedContactKey : row.GroupId == selectedGroupId;
                if (selected || Mouse.IsOver(rr)) Widgets.DrawBoxSolid(rr, selected ? PFViewPalette.PanelBgRaised : PFViewPalette.PanelBgHover);
                Widgets.Label(new(rr.x + 5, rr.y + 2, rr.width - 10, 24), row.Label);
                string secondary = row.Status + " · " + (row.DistanceMeters.HasValue
                    ? PFViewUiObjectListCore.FormatDistance(row.DistanceMeters.Value) : "—");
                MiniLabel(new(5, rr.y + 27, rr.width - 10, 20), secondary, PFViewPalette.TextSecondary);
                TooltipHandler.TipRegion(rr, row.Label + "\n" + secondary + "\n" + "PFV_TargetShortcut".Translate());
                if (!Mouse.IsOver(rr) || Event.current.type != EventType.MouseDown || Event.current.button != 0) continue;
                bool doubleClick = Event.current.clickCount >= 2;
                if (row.Kind == PFViewUiObjectKind.Contact)
                {
                    if (visibleContacts.TryGetValue(row.Key, out Contact contact))
                    {
                        if (Event.current.shift) SetTargetContact(contact);
                        else SelectContactForInspection(contact, Faction.OfPlayer.loadID);
                    }
                }
                else
                {
                    if (Event.current.shift) SetTargetGroup(row.GroupId);
                    else
                    {
                        SelectGroupForInspection(row.GroupId);
                        if (doubleClick && row.CanFocus) FocusGroup(row.GroupId);
                    }
                }
                Event.current.Use();
            }
            Widgets.EndScrollView();
        }
    }
}
