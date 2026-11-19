using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests;

public sealed class WorldGroupLabelTests
{
    [Fact]
    public void ShiftPinsToggleIndependentlyOfTheCurrentSelection()
    {
        var pins = new WorldGroupLabelPins();
        pins.Toggle(7);
        pins.Toggle(12);
        pins.Toggle(7);
        Assert.Equal(new[] { 12 }, pins.Ids);
        pins.Clear();
        Assert.Empty(pins.Ids);
    }

    [Fact]
    public void HeightAndSpeedUseTheViewedBodyAndCancelCommonTranslationAndVelocity()
    {
        var position = new DVector3(5000, 80, 110);
        var center = new DVector3(1000, 80, 110);
        var velocity = new DVector3(50, 2030, -10);
        var centerVelocity = new DVector3(50, 30, -10);
        var first = WorldGroupLabelCore.Measure(position, velocity, center, centerVelocity, 3000);
        var shift = new DVector3(1e10, -2e10, 3e10);
        var drift = new DVector3(50000, 60000, -70000);
        var second = WorldGroupLabelCore.Measure(position + shift, velocity + drift, center + shift,
            centerVelocity + drift, 3000);
        Assert.Equal(1000, first.Height);
        Assert.Equal(2000, first.Speed);
        Assert.Equal(first, second);
    }

    [Fact]
    public void LabelsAvoidPanelsAndEachOtherAndStayInsideTheViewport()
    {
        var viewport = new Rect(8, 80, 944, 600);
        var occupied = new List<Rect> { new(20, 180, 320, 420), new(640, 155, 300, 397) };
        Assert.True(WorldGroupLabelCore.TryPlace(new Vector2(450, 300), new Vector2(150, 54),
            viewport, occupied, out var first));
        occupied.Add(first);
        Assert.True(WorldGroupLabelCore.TryPlace(new Vector2(452, 300), new Vector2(150, 54),
            viewport, occupied, out var second));
        Assert.False(first.Overlaps(second));
        foreach (var label in new[] { first, second })
        {
            Assert.True(viewport.Contains(label.min));
            Assert.True(label.xMax <= viewport.xMax && label.yMax <= viewport.yMax);
            Assert.False(label.Overlaps(occupied[0]));
            Assert.False(label.Overlaps(occupied[1]));
        }
    }

    [Fact]
    public void CrowdedViewDoesNotPlaceTextOverAnExistingPanel()
    {
        var viewport = new Rect(0, 0, 200, 100);
        Assert.False(WorldGroupLabelCore.TryPlace(new Vector2(100, 50), new Vector2(120, 54),
            viewport, new[] { viewport }, out _));
    }

    [Fact]
    public void RetainedMarkersRemainPickableWithoutALiveGroup()
    {
        var hits = new[]
        {
            new WorldGroupLabelCore.Hit(12, new Vector2(400, 300), new Rect(420, 270, 150, 57))
        };
        Assert.Equal(12, WorldGroupLabelCore.Pick(new Vector2(404, 302), hits));
        Assert.Equal(12, WorldGroupLabelCore.Pick(new Vector2(450, 290), hits));
        Assert.Equal(-1, WorldGroupLabelCore.Pick(new Vector2(200, 100), hits));
    }

    [Fact]
    public void IdentificationLossPreservesTheLastKnownSnapshotAndMarksItStale()
    {
        var previous = new WorldGroupLabelCore.Observation(4, new DVector3(200, 300, 400),
            "G12 · ship", "20 km", "2 km/s", false, true);
        var unknown = new WorldGroupLabelCore.Observation(4, new DVector3(10, 20, 30),
            "G??? · ???", "???", "???", true, false);
        var retained = WorldGroupLabelCore.PreserveKnown(previous, unknown);
        Assert.Equal(previous with { Stale = true }, retained);
        Assert.Equal(unknown, WorldGroupLabelCore.PreserveKnown(null, unknown));
        Assert.Equal(unknown with { BodyId = 5 }, WorldGroupLabelCore.PreserveKnown(previous, unknown with { BodyId = 5 }));
    }
}
