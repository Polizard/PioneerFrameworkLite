using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class PFViewGroupCameraCoreTests
{
    [Theory]
    [InlineData(null, PFViewGroupCameraMode.ThirdPersonHull)]
    [InlineData(PFViewGroupCameraMode.Free, PFViewGroupCameraMode.Free)]
    [InlineData(PFViewGroupCameraMode.ThirdPersonHull, PFViewGroupCameraMode.ThirdPersonHull)]
    [InlineData(PFViewGroupCameraMode.ThirdPersonVelocity, PFViewGroupCameraMode.ThirdPersonVelocity)]
    [InlineData((PFViewGroupCameraMode)99, PFViewGroupCameraMode.ThirdPersonHull)]
    public void FocusDefaultRespectsPreferenceAndNewInstallDefault(PFViewGroupCameraMode? preference, PFViewGroupCameraMode expected)
        => Assert.Equal(expected, PFViewGroupCameraCore.DefaultMode(preference));

    [Theory]
    [InlineData(0, true, PFViewGroupCameraMode.Free)]
    [InlineData(1, true, PFViewGroupCameraMode.ThirdPersonHull)]
    [InlineData(2, false, PFViewGroupCameraMode.ThirdPersonVelocity)]
    // 新键缺席（-1/越界）⇒ 按旧 bool 迁移：旧"第三人称"只有船体正向一种。
    [InlineData(-1, true, PFViewGroupCameraMode.ThirdPersonHull)]
    [InlineData(-1, false, PFViewGroupCameraMode.Free)]
    [InlineData(7, true, PFViewGroupCameraMode.ThirdPersonHull)]
    [InlineData(7, false, PFViewGroupCameraMode.Free)]
    public void StoredModeMigratesOldThirdPersonPreferenceWithoutLosingNewChoice(
        int stored, bool legacyThirdPersonOnFocus, PFViewGroupCameraMode expected)
        => Assert.Equal(expected, PFViewGroupCameraCore.MigrateMode(stored, legacyThirdPersonOnFocus));

    [Theory]
    [InlineData(PFViewGroupCameraMode.Free, false)]
    [InlineData(PFViewGroupCameraMode.ThirdPersonHull, true)]
    [InlineData(PFViewGroupCameraMode.ThirdPersonVelocity, true)]
    public void BothThirdPersonModesShareTheOrbitFollowingRules(PFViewGroupCameraMode mode, bool thirdPerson)
        => Assert.Equal(thirdPerson, PFViewGroupCameraCore.IsThirdPerson(mode));

    [Fact]
    public void EveryDeclaredModeSurvivesStoredMigrationRegardlessOfLegacyFlag()
    {
        foreach (PFViewGroupCameraMode mode in System.Enum.GetValues<PFViewGroupCameraMode>())
        {
            Assert.Equal(mode, PFViewGroupCameraCore.MigrateMode((int)mode, false));
            Assert.Equal(mode, PFViewGroupCameraCore.MigrateMode((int)mode, true));
        }
    }

    [Theory]
    [InlineData(-200f, -60f)]
    [InlineData(200f, 60f)]
    [InlineData(25f, 25f)]
    public void RearLookCannotTurnAroundToTheFront(float yaw, float expected)
        => Assert.Equal(expected, PFViewGroupCameraCore.ClampYaw(yaw));

    [Theory]
    [InlineData(-90f, -10f)]
    [InlineData(90f, 45f)]
    [InlineData(15f, 15f)]
    public void RearLookPitchStaysWithinApprovedLimits(float pitch, float expected)
        => Assert.Equal(expected, PFViewGroupCameraCore.ClampPitch(pitch));

    [Theory]
    [InlineData(10f, 16f / 9f)]
    [InlineData(100f, 16f / 9f)]
    [InlineData(5000f, .75f)]
    public void FramingOccupiesOneThirdOfNarrowerScreenAxisAndNeverEntersHull(float radius, float aspect)
    {
        var range = PFViewGroupCameraCore.Distances(radius, 45, aspect);
        float tangent = MathF.Tan(MathF.PI / 8) * MathF.Min(1, aspect);
        float occupancy = radius / MathF.Sqrt(range.DefaultMeters * range.DefaultMeters - radius * radius) / tangent;
        Assert.InRange(occupancy, .32f, .34f);
        Assert.True(range.MinMeters > radius);
        Assert.True(range.DefaultMeters > range.MinMeters);
        Assert.True(range.MaxMeters > range.DefaultMeters);
        Assert.Equal(range.MinMeters, range.ClampMeters(0));
        Assert.Equal(range.MaxMeters, range.ClampMeters(float.MaxValue));
    }

    [Fact]
    public void SmallHullCanBeViewedCloserThanFreeViewWithoutCappingLargeHullInsideItsBounds()
    {
        Assert.True(PFViewGroupCameraCore.Distances(10, 45, 16f / 9f).DefaultMeters < 300);
        Assert.True(PFViewGroupCameraCore.Distances(5000, 45, 16f / 9f).MinMeters > 5000);
    }

    [Fact]
    public void MissingOrInvalidExtentStillProducesFiniteOrderedZoomRange()
    {
        foreach (float radius in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
        {
            var range = PFViewGroupCameraCore.Distances(radius, float.NaN, 0);
            Assert.True(float.IsFinite(range.MaxMeters));
            Assert.True(range.MinMeters > 0 && range.MinMeters < range.DefaultMeters && range.DefaultMeters < range.MaxMeters);
            Assert.Equal(range.DefaultMeters, range.ClampMeters(float.NaN));
        }
    }

    [Fact]
    public void ModesAndGroupsRetainIndependentPosesAndWorldResetClearsAll()
    {
        var memory = new PFViewGroupCameraMemory<string>();
        memory.Remember(3, PFViewGroupCameraMode.Free, "free3");
        memory.Remember(3, PFViewGroupCameraMode.ThirdPersonHull, "hull3");
        memory.Remember(3, PFViewGroupCameraMode.ThirdPersonVelocity, "velocity3");
        memory.Remember(8, PFViewGroupCameraMode.Free, "free8");
        Assert.True(memory.TryGet(3, PFViewGroupCameraMode.Free, out var free));
        Assert.Equal("free3", free);
        Assert.True(memory.TryGet(3, PFViewGroupCameraMode.ThirdPersonHull, out var hull));
        Assert.Equal("hull3", hull);
        Assert.True(memory.TryGet(3, PFViewGroupCameraMode.ThirdPersonVelocity, out var velocity));
        Assert.Equal("velocity3", velocity);
        Assert.True(memory.TryGet(8, PFViewGroupCameraMode.Free, out var other));
        Assert.Equal("free8", other);
        Assert.False(memory.TryGet(8, PFViewGroupCameraMode.ThirdPersonVelocity, out _));
        memory.Clear();
        Assert.False(memory.TryGet(3, PFViewGroupCameraMode.Free, out _));
        Assert.False(memory.TryGet(3, PFViewGroupCameraMode.ThirdPersonHull, out _));
        Assert.False(memory.TryGet(3, PFViewGroupCameraMode.ThirdPersonVelocity, out _));
    }
}
