using System.Numerics;
using PioneerFramework.PFView.ShipBuildingVisuals;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class ShipBuildingVisualBatchTests
{
    [Fact]
    public void AppendingGeometryPreservesUvAndTransformsNormalsByInverseTranspose()
    {
        var data = new VisualMeshData([new(0,0,0),new(1,0,0),new(0,1,0)],
            [Vector3.Normalize(new(1,1,0)),Vector3.Normalize(new(1,1,0)),Vector3.Normalize(new(1,1,0))],
            [new(.2f,.3f),new(.4f,.5f),new(.6f,.7f)], [0,1,2]);
        var batch = new VisualGeometryBuffer();
        Assert.True(batch.TryAppend(data, Matrix4x4.CreateScale(2, 1, 1) * Matrix4x4.CreateRotationY(MathF.PI * .5f)));
        Assert.Equal(new(.2f, .3f), batch.Uvs[0]);
        var expected = Vector3.Normalize(new(0, 1, -.5f));
        Assert.InRange(Vector3.Distance(expected, batch.Normals[0]), 0, .0001f);
        Assert.Equal(new(0, 1, 0), batch.Positions[2]);
    }

    [Fact]
    public void InvalidTransformLeavesExistingBatchIntact()
    {
        var batch = new VisualGeometryBuffer();
        var mesh = VisualGeometry.Build(VisualPrimitive.Box);
        Assert.True(batch.TryAppend(mesh, Matrix4x4.Identity));
        int count = batch.Positions.Count;
        Assert.False(batch.TryAppend(mesh, Matrix4x4.CreateScale(1e-20f)));
        Assert.Equal(count, batch.Positions.Count);
        Assert.Equal(count, batch.Normals.Count);
        Assert.Equal(mesh.Indices.Length, batch.Indices.Count);
    }

    [Fact]
    public void FrameSlotsNeverWrapAt64AndRepeatedCallsShareOneFrameGeneration()
    {
        var slots = new VisualFrameSlots();
        var used = new HashSet<int>();
        for (int i = 0; i < 150; i++) Assert.True(used.Add(slots.Acquire(1)));
        Assert.Equal(150, used.Count);
        int next = slots.Acquire(2);
        Assert.DoesNotContain(next, used);
        Assert.Equal(used.Min(), slots.Acquire(3));
    }
}
