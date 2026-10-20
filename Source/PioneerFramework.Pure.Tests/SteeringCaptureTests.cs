using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class SteeringCaptureTests
{
    [Fact]
    public void OpeningKeysMustReleaseBeforeAReplacementCanBeCaptured()
    {
        var capture = new SteeringCapture();
        Assert.Null(capture.Update(new[] { 119 }, 119, true, false, 0));
        Assert.Null(capture.Update(Array.Empty<int>(), 119, false, true, 0));
        Assert.Equal(new SteeringChord(105), capture.Update(new[] { 105 }, 105, true, false, 0));
    }

    [Fact]
    public void AModifierReleaseCapturesItsStandaloneBinding()
    {
        var capture = new SteeringCapture();
        capture.Update(Array.Empty<int>(), 0, false, false, 0);
        Assert.Null(capture.Update(new[] { 304 }, 304, true, false, SteeringModifiers.Shift));
        Assert.Equal(new SteeringChord(304), capture.Update(Array.Empty<int>(), 304, false, true, 0));
    }

    [Fact]
    public void CombinationCapturesOnPrimaryDownWithoutCapturingTheModifier()
    {
        var capture = new SteeringCapture();
        capture.Update(Array.Empty<int>(), 0, false, false, 0);
        capture.Update(new[] { 306 }, 306, true, false, SteeringModifiers.Control);
        Assert.Equal(new SteeringChord(49, SteeringModifiers.Control),
            capture.Update(new[] { 306, 49 }, 49, true, false, SteeringModifiers.Control));
        Assert.Null(capture.Update(new[] { 306, 49 }, 49, true, false, SteeringModifiers.Control));
    }

    [Fact]
    public void EscapeCancelsAndMouseCannotBecomeADrivingKey()
    {
        var capture = new SteeringCapture();
        capture.Update(Array.Empty<int>(), 0, false, false, 0);
        Assert.Null(capture.Update(new[] { 323 }, 323, true, false, 0));
        Assert.Null(capture.Update(new[] { 27 }, 27, true, false, 0));
        Assert.True(capture.Cancelled);
    }
}
