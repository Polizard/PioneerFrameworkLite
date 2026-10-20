using System;
using System.Collections.Generic;
using System.Globalization;

namespace PioneerFramework
{
    internal enum PFViewUiObjectKind { OwnGroup, OtherGroup, Contact }
    internal enum PFViewUiObjectFilter { All, OwnGroups, OtherGroups, Contacts }
    internal enum PFViewUiObjectSort { Name, Distance }

    internal readonly record struct PFViewUiObjectRow(string Key, string Label, string VisibleIdentifier,
        PFViewUiObjectKind Kind, double? DistanceMeters, string Status, bool CanFocus, int GroupId);

    internal static class PFViewUiObjectListCore
    {
        public static bool Matches(in PFViewUiObjectRow row, string query, PFViewUiObjectFilter filter)
        {
            bool kind = filter switch
            {
                PFViewUiObjectFilter.OwnGroups => row.Kind == PFViewUiObjectKind.OwnGroup,
                PFViewUiObjectFilter.OtherGroups => row.Kind == PFViewUiObjectKind.OtherGroup,
                PFViewUiObjectFilter.Contacts => row.Kind == PFViewUiObjectKind.Contact,
                _ => true
            };
            query = query.Trim();
            return kind && (query.Length == 0 || row.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.VisibleIdentifier.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        public static int Compare(in PFViewUiObjectRow left, in PFViewUiObjectRow right, PFViewUiObjectSort sort)
        {
            if (sort == PFViewUiObjectSort.Distance)
            {
                double a = ValidDistance(left.DistanceMeters), b = ValidDistance(right.DistanceMeters);
                int distance = a.CompareTo(b);
                if (distance != 0) return distance;
            }
            int name = StringComparer.OrdinalIgnoreCase.Compare(left.Label, right.Label);
            return name != 0 ? name : StringComparer.OrdinalIgnoreCase.Compare(left.VisibleIdentifier, right.VisibleIdentifier);
        }

        private static double ValidDistance(double? value)
            => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0 ? value.Value : double.PositiveInfinity;

        public static void FilterAndSort(IReadOnlyList<PFViewUiObjectRow> rows, List<PFViewUiObjectRow> result,
            string query, PFViewUiObjectFilter filter, PFViewUiObjectSort sort)
        {
            result.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (Matches(row, query, filter)) result.Add(row);
            }
            result.Sort((a, b) => Compare(a, b, sort));
        }

        public static string FormatDistance(double meters)
        {
            if (!double.IsFinite(meters) || meters < 0) return "—";
            if (meters < 1000) return meters.ToString("F0", CultureInfo.InvariantCulture) + " m";
            return (meters / 1000).ToString(meters < 1000000 ? "F1" : "F0", CultureInfo.InvariantCulture) + " km";
        }

        public static string FormatSpeed(double metersPerSecond)
        {
            if (!double.IsFinite(metersPerSecond) || metersPerSecond < 0) return "—";
            return metersPerSecond < 1000
                ? metersPerSecond.ToString("F0", CultureInfo.InvariantCulture) + " m/s"
                : (metersPerSecond / 1000).ToString("F1", CultureInfo.InvariantCulture) + " km/s";
        }

        public static bool ObservationIsCurrent(double now, double lastUpdate)
            => double.IsFinite(now) && double.IsFinite(lastUpdate) && now <= lastUpdate;

        public static string FormatObservedPosition(double x, double y, double z)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return "—";
            return Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))) >= 1000
                ? FormattableString.Invariant($"({x / 1000:F1}, {y / 1000:F1}, {z / 1000:F1}) km")
                : FormattableString.Invariant($"({x:F0}, {y:F0}, {z:F0}) m");
        }
    }
}
