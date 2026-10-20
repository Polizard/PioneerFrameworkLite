using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class SteeringInputCoreTests
{
    [Theory]
    [InlineData(SteeringAction.PitchUp, 119)]
    [InlineData(SteeringAction.PitchDown, 115)]
    [InlineData(SteeringAction.YawLeft, 97)]
    [InlineData(SteeringAction.YawRight, 100)]
    [InlineData(SteeringAction.RollLeft, 113)]
    [InlineData(SteeringAction.RollRight, 101)]
    [InlineData(SteeringAction.Forward, 114)]
    [InlineData(SteeringAction.Back, 102)]
    [InlineData(SteeringAction.Left, 122)]
    [InlineData(SteeringAction.Right, 99)]
    [InlineData(SteeringAction.Up, 116)]
    [InlineData(SteeringAction.Down, 103)]
    public void DefaultChordsDriveTheRequestedAction(object actionValue, int key)
    {
        var action = (SteeringAction)actionValue;
        var profile = new SteeringBindingProfile();
        Assert.True(profile.Matches(action, key, SteeringModifiers.None, false));
        Assert.False(profile.Matches(action, key, SteeringModifiers.Control, false));
    }

    [Fact]
    public void PrecisionAddsShiftWithoutTurningControlCombinationsIntoBareKeys()
    {
        var profile = new SteeringBindingProfile();
        Assert.True(profile.Matches(SteeringAction.PitchUp, 119, SteeringModifiers.Shift, true));
        Assert.False(profile.Matches(SteeringAction.PitchUp, 119, SteeringModifiers.Control | SteeringModifiers.Shift, true));
        Assert.True(profile.Matches(SteeringAction.Group1, 49, SteeringModifiers.Control, true));
        Assert.False(profile.Matches(SteeringAction.Group1, 49, SteeringModifiers.None, true));
    }

    [Fact]
    public void AStandaloneModifierMatchesItsOwnHeldModifierBit()
    {
        var profile = new SteeringBindingProfile();
        Assert.True(profile.Matches(SteeringAction.Precision, 304, SteeringModifiers.Shift, false));
        Assert.False(profile.Matches(SteeringAction.Precision, 303, SteeringModifiers.Shift, false));
    }

    [Fact]
    public void AnExplicitPrecisionModifierOnAnAxisStillDrivesWhilePrecisionIsHeld()
    {
        var profile = new SteeringBindingProfile();
        Assert.True(profile.TrySet(SteeringAction.PitchUp, new(119, SteeringModifiers.Shift), out _));
        Assert.True(profile.Matches(SteeringAction.PitchUp, 119, SteeringModifiers.Shift, true));
        Assert.False(profile.Matches(SteeringAction.PitchUp, 119, SteeringModifiers.None, false));
    }

    [Fact]
    public void ConflictingEditIsAtomicAndExplicitSwapMovesTheOldBinding()
    {
        var profile = new SteeringBindingProfile();
        Assert.False(profile.TrySet(SteeringAction.PitchUp, new(115), out var conflict));
        Assert.Equal(SteeringAction.PitchDown, conflict);
        Assert.True(profile.Matches(SteeringAction.PitchUp, 119, SteeringModifiers.None, false));
        Assert.True(profile.TrySet(SteeringAction.PitchUp, new(115), out _, swap: true));
        Assert.True(profile.Matches(SteeringAction.PitchUp, 115, SteeringModifiers.None, false));
        Assert.True(profile.Matches(SteeringAction.PitchDown, 119, SteeringModifiers.None, false));
    }

    [Fact]
    public void PrecisionOverlayCannotBeAssignedToAnUnrelatedAction()
    {
        var profile = new SteeringBindingProfile();
        Assert.False(profile.TrySet(SteeringAction.Group1, new(119, SteeringModifiers.Shift), out var conflict));
        Assert.Equal(SteeringAction.PitchUp, conflict);
    }

    [Fact]
    public void RebindingPrecisionRevalidatesExistingOverlayConflicts()
    {
        var profile = new SteeringBindingProfile();
        Assert.True(profile.TrySet(SteeringAction.Group1, new(119, SteeringModifiers.Alt), out _));
        Assert.False(profile.TrySet(SteeringAction.Precision, new(308), out _));
        Assert.Equal(304, profile[SteeringAction.Precision].Key);
    }

    [Fact]
    public void SavedUnboundAndReassignedActionsSurviveRoundTripAndNewActionsDefault()
    {
        var profile = new SteeringBindingProfile();
        Assert.True(profile.TrySet(SteeringAction.PitchUp, new(105), out _));
        Assert.True(profile.TrySet(SteeringAction.Forward, default, out _));
        var restored = SteeringBindingProfile.Restore(profile.Serialize());
        Assert.True(restored.Matches(SteeringAction.PitchUp, 105, SteeringModifiers.None, false));
        Assert.False(restored[SteeringAction.Forward].IsBound);
        Assert.True(SteeringBindingProfile.Restore(new[] { "PitchUp:105:0" })
            .Matches(SteeringAction.Back, 102, SteeringModifiers.None, false));
    }

    [Fact]
    public void DamagedAndUnknownRecordsNormalizeWithoutLosingOtherBindings()
    {
        var profile = SteeringBindingProfile.Restore(new[]
        {
            "PitchUp:99999:0", "FutureAction:105:0", "PitchDown:119:0", "Forward:0:0", "Back:102:99"
        });
        Assert.True(profile.Matches(SteeringAction.PitchUp, 119, SteeringModifiers.None, false));
        Assert.False(profile[SteeringAction.PitchDown].IsBound);
        Assert.False(profile[SteeringAction.Forward].IsBound);
        Assert.True(profile.Matches(SteeringAction.Back, 102, SteeringModifiers.None, false));
        Assert.NotEmpty(profile.Issues);
    }

    [Theory]
    [InlineData(27)]
    [InlineData(323)]
    [InlineData(-1)]
    public void ReservedOrNonKeyboardChordsAreRejected(int key)
        => Assert.False(new SteeringBindingProfile().TrySet(SteeringAction.PitchUp, new(key), out _));

    [Fact]
    public void APrimaryModifierCannotAlsoRequireItsOwnModifierBit()
    {
        var profile = new SteeringBindingProfile();
        Assert.False(profile.TrySet(SteeringAction.Precision, new(304, SteeringModifiers.Shift), out _));
        var restored = SteeringBindingProfile.Restore(new[] { "Precision:304:1" });
        Assert.Equal(SteeringBindingProfile.Default(SteeringAction.Precision), restored[SteeringAction.Precision]);
        Assert.NotEmpty(restored.Issues);
    }

    [Fact]
    public void PrecisionChordsOverlayTheirModifiersOnlyWhilePrecisionIsActive()
    {
        var profile = new SteeringBindingProfile();
        Assert.True(profile.TrySet(SteeringAction.Precision, new(112, SteeringModifiers.Control), out _));
        Assert.True(profile.Matches(SteeringAction.PitchUp, 119, SteeringModifiers.Control, true));
        Assert.True(profile.Matches(SteeringAction.Forward, 114, SteeringModifiers.Control, true));
        Assert.False(profile.Matches(SteeringAction.PitchUp, 119, SteeringModifiers.Control, false));
        Assert.False(profile.TrySet(SteeringAction.Group1, new(119, SteeringModifiers.Control), out var conflict));
        Assert.Equal(SteeringAction.PitchUp, conflict);
    }

    [Fact]
    public void FocusReturnRequiresReleaseOfEachHeldKey()
    {
        var gate = new SteeringReleaseGate();
        gate.Update(Array.Empty<int>(), true);
        gate.Update(new[] { 119 }, true);
        Assert.True(gate.IsHeld(119));
        gate.Update(new[] { 119 }, false);
        gate.Update(new[] { 119, 97 }, true);
        Assert.False(gate.IsHeld(119));
        Assert.True(gate.IsHeld(97));
        gate.Update(Array.Empty<int>(), true);
        gate.Update(new[] { 119 }, true);
        Assert.True(gate.IsHeld(119));
    }

    [Fact]
    public void PointerDrivingNeedsANewPressAfterFocusOrPopupSuspension()
    {
        var pointer = new SteeringPointerHold();
        pointer.Update(false, true);
        pointer.Update(true, true);
        pointer.Press(SteeringAction.Forward);
        Assert.Equal(SteeringAction.Forward, pointer.Action);
        pointer.Update(true, false);
        Assert.Null(pointer.Action);
        pointer.Update(true, true);
        pointer.Press(SteeringAction.Forward);
        Assert.Null(pointer.Action);
        pointer.Update(false, true);
        pointer.Update(true, true);
        Assert.Null(pointer.Action);
        pointer.Press(SteeringAction.PitchUp);
        Assert.Equal(SteeringAction.PitchUp, pointer.Action);
        pointer.Cancel();
        Assert.Null(pointer.Action);
    }

    [Fact]
    public void InitialHeldKeysAndReloadedBindingsDoNotStartDriving()
    {
        var gate = new SteeringReleaseGate();
        gate.Update(new[] { 119 }, true);
        Assert.False(gate.IsHeld(119));
        gate.Update(Array.Empty<int>(), true);
        gate.Update(new[] { 119 }, true);
        Assert.True(gate.IsHeld(119));
        gate.Rearm();
        gate.Update(new[] { 119, 105 }, true);
        Assert.False(gate.IsHeld(119));
        Assert.False(gate.IsHeld(105));
    }

    [Fact]
    public void ModifierReleaseDoesNotReissueADigitUntilItsPrimaryKeyReleases()
    {
        var gate = new SteeringReleaseGate();
        gate.Update(Array.Empty<int>(), true);
        gate.Update(new[] { 306, 49 }, true);
        Assert.True(gate.TryPress(49));
        gate.Update(new[] { 49 }, true);
        Assert.False(gate.TryPress(49));
        Assert.True(gate.WasConsumed(49));
        gate.Update(Array.Empty<int>(), true);
        gate.Update(new[] { 49 }, true);
        Assert.True(gate.TryPress(49));
    }

    [Fact]
    public void AngularCommandNormalizesDiagonalsAndPrecisionWithoutExceedingCapability()
    {
        var command = SteeringMath.AngularCommand(1, 1, 1, 5, .25);
        Assert.Equal(1.25, command.magnitude, 10);
        Assert.True(command.x < 0 && command.y < 0 && command.z > 0);
        Assert.Equal(0, SteeringMath.AngularCommand(0, 0, 0, 5, 1).magnitude);
    }

    [Fact]
    public void AssistedReleaseDecaysSpinButInertialReleaseKeepsIt()
    {
        var spin = new DVector3(2, 0, 0);
        Assert.Equal(2 / Math.E, SteeringMath.ReleaseSpin(spin, true, .12).x, 12);
        Assert.Equal(2, SteeringMath.ReleaseSpin(spin, false, .12).x);
        var halves = SteeringMath.ReleaseSpin(SteeringMath.ReleaseSpin(spin, true, .06), true, .06);
        Assert.Equal(2 / Math.E, halves.x, 12);
        Assert.Equal(0, SteeringMath.ReleaseSpin(new(1e-8, 0, 0), true, .01).magnitude);
    }
}
