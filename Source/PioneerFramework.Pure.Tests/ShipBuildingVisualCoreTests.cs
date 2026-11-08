using System.Numerics;
using PioneerFramework.PFView.ShipBuildingVisuals;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class ShipBuildingVisualCoreTests
{
    [Fact]
    public void ComposedScaleMustRemainInvertible()
    {
        var parent = new VisualNode("parent") { Scale = new Vector3(1e-8f) };
        var child = new VisualNode("child") { ParentId = parent.Id, Scale = new Vector3(1e-8f) };
        Assert.Throws<ArgumentException>(() => CompiledVisualProfile.Compile([parent, child]));
    }

    [Fact]
    public void VisualClockKeepsTheLastRenderedFractionAcrossPause()
    {
        var clock = new VisualRenderClock();
        Assert.Equal(12.5, clock.Sample(12.5, paused: false));
        Assert.Equal(12.5, clock.Sample(12, paused: true));
        Assert.Equal(12.5, clock.Sample(12.2, paused: false));
        Assert.Equal(13, clock.Sample(13, paused: false));
        clock.Reset();
        Assert.Equal(1, clock.Sample(1, paused: true));
    }

    [Fact]
    public void CompileSortsParentsAndCopiesInput()
    {
        var nodes = new[] { new VisualNode("child") { ParentId = "base" }, new VisualNode("base") };
        var profile = CompiledVisualProfile.Compile(nodes);
        nodes[1] = new VisualNode("changed");
        Assert.Equal("base", profile.Nodes[0].Id);
        Assert.Equal("child", profile.Nodes[1].Id);
    }

    [Fact]
    public void InvalidGraphsAndNonFiniteTransformsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => CompiledVisualProfile.Compile([new("a") { ParentId = "b" }, new("b") { ParentId = "a" }]));
        Assert.Throws<ArgumentException>(() => CompiledVisualProfile.Compile([new("a"), new("a")]));
        Assert.Throws<ArgumentException>(() => CompiledVisualProfile.Compile([new("a") { ParentId = "missing" }]));
        Assert.Throws<ArgumentException>(() => CompiledVisualProfile.Compile([new("a") { Scale = new(-1, 1, 1) }]));
        Assert.Throws<ArgumentException>(() => CompiledVisualProfile.Compile([new("a") { Position = new(float.NaN, 0, 0) }]));
        Assert.Throws<ArgumentException>(() => CompiledVisualProfile.Compile(Enumerable.Range(0, 33).Select(i => new VisualNode(i.ToString())).ToArray()));
    }

    [Fact]
    public void PivotAndParentTransformAreAppliedExactlyOnce()
    {
        var profile = CompiledVisualProfile.Compile([
            new("root") { Position = new(10, 0, 0) },
            new("head") { ParentId = "root", Pivot = new(1, 0, 0), BindYaw = true }
        ]);
        var matrices = new Matrix4x4[2];
        profile.Evaluate(new VisualState { Yaw = 90, Channels = VisualChannels.Yaw }, matrices);
        var point = Vector3.Transform(new Vector3(2, 0, 0), matrices[1]);
        Near(new(11, 0, -1), point);
        Near(new(11, 0, 0), Vector3.Transform(new Vector3(1, 0, 0), matrices[1]));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(0, 0, 1, 1, .5f, .5f)]
    [InlineData(2, 4, 4, 5, 3, 4.5f)]
    public void OccupiedRectCenterRetainsHalfCells(int x0, int z0, int x1, int z1, float x, float z)
        => Near(new(x - 10, 0, z - 20), VisualMath.FootprintCenter(x0, z0, x1, z1, 10, 20));

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(90, 90, 0)]
    [InlineData(90, 0, 90)]
    public void ShipYawIsConvertedToMountSpace(float shipYaw, float mountYaw, float expected)
    {
        var mount = Quaternion.CreateFromAxisAngle(Vector3.UnitY, mountYaw * MathF.PI / 180);
        var direction = VisualMath.YawDirection(shipYaw);
        Assert.Equal(VisualChannels.Yaw | VisualChannels.Pitch, VisualMath.LocalAngles(direction, mount, out float yaw, out _));
        Assert.InRange(MathF.Abs(VisualMath.DeltaAngle(expected, yaw)), 0, .001f);
    }

    [Fact]
    public void DegenerateDirectionIsExplicitAndBottomHasPositiveScale()
    {
        Assert.Equal(VisualChannels.None, VisualMath.LocalAngles(Vector3.Zero, Quaternion.Identity, out _, out _));
        Assert.Equal(VisualChannels.Pitch, VisualMath.LocalAngles(Vector3.UnitY, Quaternion.Identity, out _, out float pitch));
        Assert.Equal(-90, pitch, 4);
        Near(-Vector3.UnitY, Vector3.Transform(Vector3.UnitY, VisualMath.MountRotation(true, 0)));
        Assert.Equal(2, VisualMath.DeltaAngle(359, 1), 4);
    }

    [Fact]
    public void LodUsesHysteresisAndRejectsNonFinitePixels()
    {
        Assert.Equal(VisualLod.Detailed, VisualMath.SelectLod(9, VisualLod.Detailed, 8, 2));
        Assert.Equal(VisualLod.Simplified, VisualMath.SelectLod(7, VisualLod.Detailed, 8, 2));
        Assert.Equal(VisualLod.Simplified, VisualMath.SelectLod(8.5f, VisualLod.Simplified, 8, 2));
        Assert.Equal(VisualLod.Detailed, VisualMath.SelectLod(10, VisualLod.Simplified, 8, 2));
        Assert.Equal(VisualLod.Hidden, VisualMath.SelectLod(float.NaN, VisualLod.Detailed, 8, 2));
    }

    [Theory]
    [InlineData(VisualPrimitive.Box)]
    [InlineData(VisualPrimitive.Cylinder)]
    [InlineData(VisualPrimitive.Dish)]
    public void PrimitiveAttributesAndOutwardWindingAreComplete(VisualPrimitive primitive)
    {
        var mesh = VisualGeometry.Build(primitive);
        Assert.Equal(mesh.Positions.Length, mesh.Normals.Length);
        Assert.Equal(mesh.Positions.Length, mesh.Uvs.Length);
        Assert.InRange(mesh.Indices.Length / 3, 1, 200);
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            int a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            var face = Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
            var normal = mesh.Normals[a] + mesh.Normals[b] + mesh.Normals[c];
            Assert.True(Vector3.Dot(face, normal) > 0, $"{primitive}: triangle {i / 3}");
        }
    }

    [Fact]
    public void MotionBoundsIncludeLongOffsetChildAtAnyYaw()
    {
        var profile = CompiledVisualProfile.Compile([new("base") { BindYaw = true }, new("barrel") { ParentId = "base", Position = new(0, 0, 10), Scale = new(1, 1, 5) }]);
        Assert.True(profile.MotionRadius >= 12.5f);
    }

    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, .0001f);
}
