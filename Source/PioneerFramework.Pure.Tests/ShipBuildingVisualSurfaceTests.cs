using System.Numerics;
using PioneerFramework.PFView.ShipBuildingVisuals;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class ShipBuildingVisualSurfaceTests
{
    [Fact]
    public void FootprintSamplesActualRenderedBoxesAndHighestOverlappingSurface()
    {
        VisualSurfaceBox[] boxes = [new(-.5f, -.5f, .5f, .5f, 1), new(.5f, -.5f, 2.5f, .5f, 5)];
        Assert.True(VisualSurface.TryHeight(boxes, -.5f, -.5f, 1.5f, .5f, false, out float top));
        Assert.Equal(5, top);
        Assert.True(VisualSurface.TryHeight(boxes, -.5f, -.5f, 1.5f, .5f, true, out float bottom));
        Assert.Equal(-5, bottom);
        Assert.False(VisualSurface.TryHeight(boxes, 5, 5, 6, 6, false, out _));
    }

    [Fact]
    public void CoarsenedBoxHeightWinsOverOriginalCellCategory()
    {
        VisualSurfaceBox[] boxes = [new(-.5f, -.5f, 3.5f, 3.5f, 4.5f)];
        Assert.True(VisualSurface.TryHeight(boxes, 0, 0, 1, 1, false, out float height));
        Assert.Equal(4.5f, height);
    }

    [Fact]
    public void MapToShipAppliesOwnerTransformOnce()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * .5f);
        var owner = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(10, 20, 30);
        var mount = Matrix4x4.CreateTranslation(2, 5, 3) * owner;
        var point = Vector3.Transform(Vector3.Zero, mount);
        Assert.InRange(Vector3.Distance(new(5, 22, 33), point), 0, .0001f);
    }
}
