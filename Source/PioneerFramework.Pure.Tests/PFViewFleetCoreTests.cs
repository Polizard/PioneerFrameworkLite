using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class PFViewFleetCoreTests
{
    [Theory]
    [InlineData(true, false, true, 0, 100, "PropellantEmpty")]
    [InlineData(true, false, true, 19, 100, "LowPropellant")]
    [InlineData(true, false, true, 20, 100, "None")]
    [InlineData(false, false, true, 0, 100, "None")]
    [InlineData(true, false, false, 0, 0, "None")]
    [InlineData(true, true, false, 0, 0, "Crippled")]
    [InlineData(true, false, true, -1, 100, "None")]
    [InlineData(true, false, true, double.NaN, 100, "None")]
    [InlineData(true, false, true, 1, double.PositiveInfinity, "None")]
    public void AlertsDistinguishExhaustionFromLowReserveWithoutFlaggingStationsOrUnknownData(
        bool movable, bool crippled, bool propulsion, double mass, double capacity, string expected)
        => Assert.Equal(expected, PFViewFleetCore.ResolveAlerts(movable, crippled, propulsion, mass, capacity, 0, 0, 0, 0, 0).ToString());

    [Fact]
    public void SimultaneousDamageAndWeaponProblemsAreAllRetained()
    {
        var flags = PFViewFleetCore.ResolveAlerts(true, true, true, 8d, 100d, 2, 1, 3, 1, 2).ToString();
        foreach (string name in new[] { "Crippled", "LowPropellant", "Damage", "Overheated", "DisabledWeapons", "EmptyWeapons" })
            Assert.Contains(name, flags);
    }

    [Theory]
    [InlineData(100, 160d, 200d, "Node", 60)]
    [InlineData(100, 160d, 120d, "Arrival", 20)]
    [InlineData(100, 99d, 120d, "Node", 0)]
    [InlineData(100, null, null, "None", 0)]
    [InlineData(100, double.NaN, 160d, "Arrival", 60)]
    public void NextEventUsesPhysicalSecondsAndKeepsDueNodesVisible(double now, double? node, double? arrival,
        string kind, double seconds)
    {
        var result = PFViewFleetCore.NextEvent(now, node, arrival);
        Assert.Equal(kind, result.Kind.ToString());
        Assert.Equal(seconds, result.Seconds);
    }
}
