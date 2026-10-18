using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class PFViewUiOperationContextTests
{
    [Fact]
    public void OpeningWeaponsLatchesSourceUntilExplicitClose()
    {
        var context = new PFViewUiOperationContext();
        Assert.True(context.TryOpenWeapons(3, true, -1));
        Assert.Equal(3, context.ResolveSource(-1));
        Assert.False(context.TryOpenWeapons(8, false, -1));
        Assert.Equal(3, context.ResolveSource(-1));
        context.CloseWeapons();
        Assert.Equal(-1, context.ResolveSource(-1));
        Assert.False(context.WeaponsOpen);
    }

    [Fact]
    public void ActiveSteeringWinsWithoutAllowingInspectionToRetargetWeapons()
    {
        var context = new PFViewUiOperationContext();
        Assert.True(context.TryOpenWeapons(3, true, 3));
        Assert.False(context.TryOpenWeapons(8, true, 3));
        Assert.Equal(3, context.ResolveSource(3));
        Assert.Equal(8, context.ResolveSource(8));
        Assert.Equal(8, context.ResolveSource(-1));
    }

    [Fact]
    public void InvalidatingUnrelatedObjectKeepsSourceButInvalidatingSourceClosesIt()
    {
        var context = new PFViewUiOperationContext();
        context.TryOpenWeapons(3, true, -1);
        context.InvalidateSource(8);
        Assert.Equal(3, context.ResolveSource(-1));
        context.InvalidateSource(3);
        Assert.Equal(-1, context.ResolveSource(-1));
        Assert.False(context.WeaponsOpen);
    }

    [Theory]
    [InlineData(-1, true, -1)]
    [InlineData(3, false, -1)]
    [InlineData(8, true, 3)]
    public void InvalidOpenCannotCreateOperationSource(int group, bool operable, int steering)
    {
        var context = new PFViewUiOperationContext();
        Assert.False(context.TryOpenWeapons(group, operable, steering));
        Assert.False(context.WeaponsOpen);
        Assert.Equal(-1, context.WeaponSourceGroupId);
    }

    [Theory]
    [InlineData(3, 8, true, true, true, true)]
    [InlineData(3, 3, true, true, true, false)]
    [InlineData(-1, 8, true, true, true, false)]
    [InlineData(3, -1, true, true, true, false)]
    [InlineData(3, 8, false, true, true, false)]
    [InlineData(3, 8, true, false, true, false)]
    [InlineData(3, 8, true, true, false, false)]
    public void CommandsRequireExplicitValidSourceAndKnownTarget(int source, int target,
        bool operable, bool known, bool ready, bool expected)
    {
        Assert.Equal(expected, PFViewUiOperationContext.CanFire(source, target, operable, known, ready));
    }

    [Fact]
    public void ResetDropsWeaponContextWithoutChangingExternalSteering()
    {
        var context = new PFViewUiOperationContext();
        context.TryOpenWeapons(3, true, 3);
        context.Reset();
        Assert.False(context.WeaponsOpen);
        Assert.Equal(3, context.ResolveSource(3));
        Assert.Equal(-1, context.ResolveSource(-1));
    }
}
