using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class PFViewUiObjectListCoreTests
{
    private static PFViewUiObjectRow Row(string key, string label, PFViewUiObjectKind kind, double? distance)
        => new(key, label, label, kind, distance, "", false, -1);

    [Fact]
    public void AllRowsRemainReachableBeyondOldDisplayCaps()
    {
        var rows = Enumerable.Range(0, 40).Select(i => Row(i.ToString(), "G" + i,
            i < 20 ? PFViewUiObjectKind.OwnGroup : PFViewUiObjectKind.Contact, i)).ToList();
        var result = new List<PFViewUiObjectRow>();
        PFViewUiObjectListCore.FilterAndSort(rows, result, "", PFViewUiObjectFilter.All, PFViewUiObjectSort.Name);
        Assert.Equal(40, result.Count);
        PFViewUiObjectListCore.FilterAndSort(rows, result, "", PFViewUiObjectFilter.Contacts, PFViewUiObjectSort.Name);
        Assert.Equal(20, result.Count);
    }

    [Fact]
    public void SearchUsesOnlyVisibleNamesAndIdentifiers()
    {
        var row = Row("hidden-entity-987", "Explorer G3", PFViewUiObjectKind.OwnGroup, 1200);
        Assert.True(PFViewUiObjectListCore.Matches(row, "explorer", PFViewUiObjectFilter.OwnGroups));
        Assert.False(PFViewUiObjectListCore.Matches(row, "987", PFViewUiObjectFilter.All));
        Assert.False(PFViewUiObjectListCore.Matches(row, "G3", PFViewUiObjectFilter.Contacts));
    }

    [Fact]
    public void DistanceSortKeepsUnknownValuesLastAndUsesVisibleTieBreakers()
    {
        var rows = new[] { Row("1", "C2", PFViewUiObjectKind.Contact, null),
            Row("9", "G8", PFViewUiObjectKind.OtherGroup, 12000),
            Row("2", "G3", PFViewUiObjectKind.OwnGroup, 300) };
        var result = new List<PFViewUiObjectRow>();
        PFViewUiObjectListCore.FilterAndSort(rows, result, "", PFViewUiObjectFilter.All, PFViewUiObjectSort.Distance);
        Assert.Equal(new[] { "G3", "G8", "C2" }, result.Select(r => r.Label));
    }

    [Fact]
    public void UnidentifiedRowsCannotRevealInternalContactIdentity()
    {
        var real = Row("group:12", "C2", PFViewUiObjectKind.Contact, 1200);
        var forged = Row("decoy:5", "C2", PFViewUiObjectKind.Contact, 1200);
        Assert.Equal(0, PFViewUiObjectListCore.Compare(real, forged, PFViewUiObjectSort.Name));
        Assert.Equal(0, PFViewUiObjectListCore.Compare(real, forged, PFViewUiObjectSort.Distance));
        Assert.False(real.CanFocus);
        Assert.False(forged.CanFocus);
    }

    [Theory]
    [InlineData(300, "300 m")]
    [InlineData(12400, "12.4 km")]
    [InlineData(1500000, "1500 km")]
    [InlineData(-1, "—")]
    [InlineData(double.NaN, "—")]
    public void DistanceAlwaysShowsUnitsAndRejectsInvalidValues(double meters, string text)
        => Assert.Equal(text, PFViewUiObjectListCore.FormatDistance(meters));

    [Theory]
    [InlineData(523, "523 m/s")]
    [InlineData(7600, "7.6 km/s")]
    public void SpeedUsesReadableMagnitude(double metersPerSecond, string text)
        => Assert.Equal(text, PFViewUiObjectListCore.FormatSpeed(metersPerSecond));

    [Theory]
    [InlineData(12, -34, 0, "(12, -34, 0) m")]
    [InlineData(3200, -1200, 0, "(3.2, -1.2, 0.0) km")]
    [InlineData(double.NaN, 0, 1, "—")]
    public void ObservedCoordinatesShowSignedComponentsAndUnits(double x, double y, double z, string expected)
        => Assert.Equal(expected, PFViewUiObjectListCore.FormatObservedPosition(x, y, z));

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(100.1, 100, false)]
    [InlineData(100, double.NaN, false)]
    public void ObservationFreshnessUsesOnlyVisibleTimestamp(double now, double last, bool current)
        => Assert.Equal(current, PFViewUiObjectListCore.ObservationIsCurrent(now, last));
}
