using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
        private readonly Dictionary<int, PFViewFleetSnapshot> fleetSnapshots = new();
        private bool fleetAlertsOnly;
        private bool showFleetMembers;
        private int fleetAlertCount;
        private const float FleetRowHeight = 128f;

        private static string FleetDuration(double seconds)
        {
            if (!double.IsFinite(seconds) || seconds < 0) return "—";
            if (seconds >= 86400) return (seconds / 86400).ToString("F1", CultureInfo.InvariantCulture) + " d";
            int total = (int)Math.Ceiling(seconds);
            return total >= 3600 ? $"{total / 3600:D2}:{total / 60 % 60:D2}:{total % 60:D2}"
                : $"{total / 60:D2}:{total % 60:D2}";
        }

        private static string FleetNextEvent(PFViewFleetSnapshot snapshot)
        {
            var next = PFViewFleetCore.NextEvent(TimeScaleManager.GetPhysicsTickSafe(), snapshot.NextNodeTick, snapshot.ArrivalTick);
            return next.Kind == PFViewFleetEventKind.None ? "PFV_FleetNoEvent".Translate()
                : ("PFV_FleetEvent_" + next.Kind).Translate(FleetDuration(next.Seconds));
        }

        private static string FleetMobility(PFViewFleetSnapshot snapshot)
            => !snapshot.HasPropulsion ? "PFV_FleetNoPropulsion".Translate() : snapshot.EnvironmentDependent
                ? "PFV_FleetEndurance".Translate(FleetDuration(snapshot.ShortestEndurance))
                : "PFV_FleetDeltaV".Translate(PFViewUiObjectListCore.FormatSpeed(snapshot.ReferenceDeltaV));

        private static string FleetFuel(PFViewFleetSnapshot snapshot)
        {
            double mass = snapshot.PropellantMass, capacity = snapshot.PropellantCapacity;
            return double.IsFinite(mass) && mass >= 0 && double.IsFinite(capacity) && capacity > 0
                ? "PFV_FleetFuel".Translate((mass / capacity).ToString("P0", CultureInfo.InvariantCulture))
                : "PFV_FleetFuel".Translate("—");
        }

        private static string FleetAlerts(PFViewFleetSnapshot snapshot)
        {
            if (snapshot.AlertLabel != null) return snapshot.AlertLabel;
            if (snapshot.Alerts == PFViewFleetAlert.None) return snapshot.AlertLabel = snapshot.ActivityKey.Translate();
            var labels = new List<string>();
            foreach (var alert in Enum.GetValues<PFViewFleetAlert>())
                if (alert != PFViewFleetAlert.None && (snapshot.Alerts & alert) != 0)
                    labels.Add(("PFV_FleetAlert_" + alert).Translate());
            return snapshot.AlertLabel = string.Join(" · ", labels);
        }

        private void DrawFleetCard(Rect rowRect, PFViewUiObjectRow row, PFViewFleetSnapshot snapshot)
        {
            Text.Font = GameFont.Small;
            bool selected = row.GroupId == selectedGroupId;
            if (selected || Mouse.IsOver(rowRect))
                Widgets.DrawBoxSolid(rowRect, selected ? PFViewPalette.PanelBgRaised : PFViewPalette.PanelBgHover);
            Widgets.DrawBoxSolid(new(rowRect.x, rowRect.y + 2, selected ? 3 : 1, rowRect.height - 4),
                selected ? PFViewPalette.Accent : PFViewPalette.Divider);
            float x = rowRect.x + 8, w = rowRect.width - 16, y = rowRect.y + 3;
            MiniLabel(new(x, y, w, 21), row.Label + " · " + row.Status, PFViewPalette.TextPrimary);
            MiniLabel(new(x, y + 23, w, 20), FleetFuel(snapshot) + " · " + FleetMobility(snapshot), PFViewPalette.TextSecondary);
            MiniLabel(new(x, y + 44, w, 20), FleetAlerts(snapshot), PFViewPalette.TextPrimary);
            MiniLabel(new(x, y + 65, w, 20), FleetNextEvent(snapshot), PFViewPalette.TextSecondary);
            TooltipHandler.TipRegion(new(x, y, w, 87), row.Label + "\n" + FleetFuel(snapshot) + " · " + FleetMobility(snapshot)
                + "\n" + FleetAlerts(snapshot) + "\n" + snapshot.ActivityKey.Translate() + "\n" + FleetNextEvent(snapshot)
                + "\n" + "PFV_FleetEstimateTip".Translate());
            Text.Font = GameFont.Small;
            if (Widgets.ButtonText(new(x, y + 89, (w - 6) * .5f, 25), "PFV_Focus".Translate())) FocusGroup(row.GroupId);
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && snapshot.HasMap;
            if (Widgets.ButtonText(new(x + (w + 6) * .5f, y + 89, (w - 6) * .5f, 25), "PFV_EnterMap".Translate()))
                ShowGroupMapMenu(row.GroupId);
            GUI.enabled = enabled;
            if (!Mouse.IsOver(rowRect) || Event.current.type != EventType.MouseDown || Event.current.button != 0) return;
            if (Event.current.shift) SetTargetGroup(row.GroupId);
            else
            {
                SelectGroupForInspection(row.GroupId);
                if (Event.current.clickCount >= 2) FocusGroup(row.GroupId);
            }
            Event.current.Use();
        }

        private float FleetInspectorHeight(PFViewFleetSnapshot snapshot)
            => 7 * 25f + snapshot.Networks.Count * 25f + (snapshot.Alerts != PFViewFleetAlert.None ? 48f : 0)
                + (snapshot.UnloadedMaps ? 40f : 0) + (snapshot.MemberCount > 1 ? 33f : 0)
                + (showFleetMembers && snapshot.MemberCount > 1 ? snapshot.Members.Count * 25f : 0) + 8f;

        private void DrawFleetInspector(Rect content, ref float y, PFViewFleetSnapshot snapshot)
        {
            FleetLine(content, ref y, "PFV_FleetPropellant".Translate(),
                $"{snapshot.PropellantMass:F1} / {snapshot.PropellantCapacity:F1} kg");
            FleetLine(content, ref y, "PFV_FleetMobility".Translate(), FleetMobility(snapshot));
            FleetLine(content, ref y, "PFV_FleetStructure".Translate(),
                "PFV_FleetDamageCounts".Translate(snapshot.DamagedBuildings, snapshot.DestroyedParts));
            FleetLine(content, ref y, "PFV_FleetMinimumHp".Translate(), snapshot.MinimumPartHp?.ToString("F0", CultureInfo.InvariantCulture) ?? "—");
            FleetLine(content, ref y, "PFV_FleetWeapons".Translate(), "PFV_FleetWeaponCounts".Translate(snapshot.ReadyWeapons, snapshot.Weapons));
            FleetLine(content, ref y, "PFV_State".Translate(), snapshot.ActivityKey.Translate());
            FleetLine(content, ref y, "PFV_FleetNextEvent".Translate(), FleetNextEvent(snapshot));
            foreach (var network in snapshot.Networks)
            {
                string value = network.EnvironmentDependent
                    ? $"{network.Mass:F1} kg · " + "PFV_FleetEndurance".Translate(FleetDuration(network.Endurance))
                    : $"{network.Mass:F1} kg";
                FleetLine(content, ref y, network.Name, value);
            }
            if (snapshot.Alerts != PFViewFleetAlert.None)
            {
                Text.Font = GameFont.Small;
                Widgets.Label(new(0, y, content.width, 44), FleetAlerts(snapshot));
                TooltipHandler.TipRegion(new(0, y, content.width, 44), FleetAlerts(snapshot));
                y += 48;
            }
            if (snapshot.UnloadedMaps)
            {
                Text.Font = GameFont.Small;
                Widgets.Label(new(0, y, content.width, 36), "PFV_FleetUnloadedMaps".Translate());
                y += 40;
            }
            Text.Font = GameFont.Small;
            if (snapshot.MemberCount > 1 && InspectorButton(content, ref y, "PFV_FleetMembers".Translate(snapshot.MemberCount)))
                showFleetMembers = !showFleetMembers;
            if (showFleetMembers && snapshot.MemberCount > 1)
                foreach (var member in snapshot.Members)
                    FleetLine(content, ref y, "E" + member.EntityId,
                        "PFV_FleetMemberParts".Translate(member.Parts, member.Destroyed));
            y += 8;
            Text.Font = GameFont.Small;
        }

        private static void FleetLine(Rect content, ref float y, string label, string value)
        {
            string line = label + ": " + value;
            bool wrap = Text.WordWrap;
            Text.WordWrap = false;
            MiniLabel(new(0, y, content.width, 23), line, PFViewPalette.TextSecondary);
            Text.WordWrap = wrap;
            TooltipHandler.TipRegion(new(0, y, content.width, 23), line);
            y += 25;
        }

        private void OpenFleetManeuvers(int groupId, bool queue)
        {
            if (!CanOperateGroup(groupId, false, out string reason)) { QueueToast(reason); return; }
            if (queue) Find.WindowStack.Add(new ManeuverQueueWindow(groupId));
            else ManeuverPlanner.TryOpen(groupId);
        }
    }
}
