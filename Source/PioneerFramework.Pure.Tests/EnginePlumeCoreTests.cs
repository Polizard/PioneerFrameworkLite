using System;
using System.Collections.Generic;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class EnginePlumeCoreTests
{
    private static EnginePlumePartInput Part(
        int partId = 1,
        bool hasEngine = true,
        bool destroyed = false,
        int stageId = 0,
        bool isRcs = false,
        double px = 0, double py = 0, double pz = 0,
        double bx = 2, double by = 2, double bz = 4,
        DQuaternion rotation = default,
        double ax = 0, double ay = 0, double az = -1,
        float ve = 3000f,
        float flow = 10f,
        string? propellant = "PF_Propellant_Chemical")
        => new(partId, hasEngine, destroyed, stageId, isRcs,
            new DVector3(px, py, pz), new DVector3(bx, by, bz),
            rotation == default ? DQuaternion.identity : rotation,
            new DVector3(ax, ay, az), ve, flow, propellant);

    [Fact]
    public void ResolveRolePrefersBurnAndSplitsMainAndRcs()
    {
        Assert.Equal(EnginePlumeRole.Main, EnginePlumeCore.ResolveRole(true, false, false));
        Assert.Equal(EnginePlumeRole.Rcs, EnginePlumeCore.ResolveRole(true, true, false));
        Assert.Equal(EnginePlumeRole.Rcs, EnginePlumeCore.ResolveRole(false, false, true));
        Assert.Equal(EnginePlumeRole.None, EnginePlumeCore.ResolveRole(false, false, false));
        // 已点火时 hover 不覆盖：burn 的判据优先。
        Assert.Equal(EnginePlumeRole.Main, EnginePlumeCore.ResolveRole(true, false, true));
    }

    [Fact]
    public void ActiveStageIgnoresDestroyedAndNonEngineParts()
    {
        var parts = new List<EnginePlumePartInput>
        {
            Part(partId: 1, stageId: 0),
            Part(partId: 2, stageId: 2),
            Part(partId: 3, stageId: 5, destroyed: true),
            Part(partId: 4, stageId: 7, hasEngine: false),
        };
        // 与 RocketHullMesh 同规则：活动级 = 全部**存活件**（含非引擎件）的最大 stageId。
        Assert.Equal(7, EnginePlumeCore.ResolveActiveStage(parts));
        Assert.Equal(0, EnginePlumeCore.ResolveActiveStage(Array.Empty<EnginePlumePartInput>()));
    }

    [Fact]
    public void BackEngineNozzleSitsAtTheRearFaceAndUsesTheExhaustDirection()
    {
        var part = Part();   // Back = −Z，20 kg/s * 3000 m/s
        Assert.True(EnginePlumeCore.TryBuildSpec(part, activeStage: 0, EnginePlumeRole.Main, out var spec));
        Assert.Equal(-2.05, spec.nozzleShip.z, 9);          // 半长 2.0 + 外移 0.05
        Assert.Equal(0.0, spec.nozzleShip.x, 9);
        Assert.Equal(-1.0, spec.exhaustShip.z, 9);          // 火焰沿 −Z 喷
        Assert.Equal(1.0, spec.nozzleRadiusMeters, 9);
        Assert.Equal(EnginePlumeCore.BucketChemical, spec.colorBucket);
    }

    [Fact]
    public void PartRotationRotatesBothNozzleOffsetAndExhaustDirection()
    {
        var rotation = DQuaternion.AngleAxis(Math.PI * 0.5, new DVector3(0, 1, 0));
        var part = Part(rotation: rotation);
        Assert.True(EnginePlumeCore.TryBuildSpec(part, 0, EnginePlumeRole.Main, out var spec));
        Assert.Equal(-2.05, spec.nozzleShip.x, 9);
        Assert.Equal(0.0, spec.nozzleShip.z, 9);
        Assert.Equal(-1.0, spec.exhaustShip.x, 9);
        Assert.Equal(0.0, spec.exhaustShip.z, 9);
    }

    [Fact]
    public void RoleAndStageFiltersRejectMismatches()
    {
        Assert.False(EnginePlumeCore.TryBuildSpec(Part(isRcs: true), 0, EnginePlumeRole.Main, out _));
        Assert.False(EnginePlumeCore.TryBuildSpec(Part(isRcs: false), 0, EnginePlumeRole.Rcs, out _));
        Assert.True(EnginePlumeCore.TryBuildSpec(Part(isRcs: true), 0, EnginePlumeRole.Rcs, out _));
        Assert.False(EnginePlumeCore.TryBuildSpec(Part(stageId: 1), 0, EnginePlumeRole.Main, out _));
        Assert.False(EnginePlumeCore.TryBuildSpec(Part(destroyed: true), 0, EnginePlumeRole.Main, out _));
        Assert.False(EnginePlumeCore.TryBuildSpec(Part(hasEngine: false), 0, EnginePlumeRole.Main, out _));
        // 轴向退化：零向量不能编出方向。
        Assert.False(EnginePlumeCore.TryBuildSpec(Part(ax: 0, ay: 0, az: 0), 0, EnginePlumeRole.Main, out _));
    }

    [Theory]
    [InlineData(0.0, 3)]
    [InlineData(4_999.0, 3)]
    [InlineData(5_000.0, 2)]
    [InlineData(29_999.0, 2)]
    [InlineData(30_000.0, 1)]
    [InlineData(299_999.0, 1)]
    [InlineData(300_000.0, 0)]
    [InlineData(1_000_000.0, 0)]
    public void LodKeepsDotOnlyForLongRange(double distance, int expected)
        => Assert.Equal(expected, EnginePlumeCore.LodQuadCount(distance));

    [Theory]
    [InlineData(0.0, 0.0, 1.0, 0.0, 0.0, 2.05)]
    [InlineData(0.0, 0.0, -1.0, 0.0, 0.0, -2.05)]
    [InlineData(-1.0, 0.0, 0.0, -1.05, 0.0, 0.0)]
    [InlineData(1.0, 0.0, 0.0, 1.05, 0.0, 0.0)]
    [InlineData(0.0, 1.0, 0.0, 0.0, 1.05, 0.0)]
    [InlineData(0.0, -1.0, 0.0, 0.0, -1.05, 0.0)]
    public void AllSixAxesProduceTheNozzleOnTheCorrectFace(
        double ax, double ay, double az, double ex, double ey, double ez)
    {
        var part = Part(ax: ax, ay: ay, az: az);
        Assert.True(EnginePlumeCore.TryBuildSpec(part, 0, EnginePlumeRole.Main, out var spec));
        Assert.Equal(ex, spec.nozzleShip.x, 9);
        Assert.Equal(ey, spec.nozzleShip.y, 9);
        Assert.Equal(ez, spec.nozzleShip.z, 9);
        Assert.Equal(ax, spec.exhaustShip.x, 9);
        Assert.Equal(ay, spec.exhaustShip.y, 9);
        Assert.Equal(az, spec.exhaustShip.z, 9);
    }

    [Theory]
    [InlineData(0.0, 1)]
    [InlineData(3.9, 1)]
    [InlineData(4.0, 2)]
    [InlineData(13.9, 2)]
    [InlineData(14.0, 3)]
    [InlineData(26.0, 3)]
    [InlineData(200.0, 3)]
    public void ProjectedLengthLodKeepsTheFlameVisibleAtCommonCameraDistances(
        double lengthPixels, int expected)
        => Assert.Equal(expected, EnginePlumeCore.LodQuadCountForProjectedLength(lengthPixels));

    [Fact]
    public void PlumeLengthUsesNozzleDiameterAndAtmosphere()
    {
        double vacuumMain = EnginePlumeCore.PlumeLengthMeters(1.0, 1.0, inAtmosphere: false);
        double vacuumRcs = EnginePlumeCore.PlumeLengthMeters(1.0, 0.45, inAtmosphere: false);
        double atmosphere = EnginePlumeCore.PlumeLengthMeters(1.0, 1.0, inAtmosphere: true);
        Assert.Equal(20.0, vacuumMain, 9);
        Assert.Equal(20.0 * (0.55 + 0.45 * 0.45), vacuumRcs, 9);
        Assert.Equal(24.0, atmosphere, 9);
        // 小喷口的下限：直径 0.3 m 仍给 6 m 基准。
        Assert.Equal(6.0, EnginePlumeCore.PlumeLengthMeters(0.15, 1.0, false), 9);
    }

    [Fact]
    public void FlickerIsDeterministicAndBounded()
    {
        double a = EnginePlumeCore.Flicker(42, 123.456);
        double b = EnginePlumeCore.Flicker(42, 123.456);
        Assert.Equal(a, b, 12);
        Assert.InRange(a, 0.92, 1.08);
        Assert.NotEqual(EnginePlumeCore.Flicker(42, 0.0), EnginePlumeCore.Flicker(43, 0.0));
        // 暂停冻结：同一时间格内不变（30 Hz 量化）。
        Assert.Equal(EnginePlumeCore.Flicker(7, 1.000), EnginePlumeCore.Flicker(7, 1.020), 12);
    }

    [Theory]
    [InlineData("PF_Propellant_Chemical", EnginePlumeCore.BucketChemical)]
    [InlineData("PF_Propellant_Hydrogen", EnginePlumeCore.BucketHydrogen)]
    [InlineData("PF_Propellant_Xenon", EnginePlumeCore.BucketXenon)]
    [InlineData("PF_Propellant_Hydrazine", EnginePlumeCore.BucketHydrazine)]
    [InlineData("PF_Propellant_IntakeAir", EnginePlumeCore.BucketIntakeAir)]
    [InlineData("PF_Propellant_Unknown", EnginePlumeCore.BucketUnknown)]
    [InlineData(null, EnginePlumeCore.BucketUnknown)]
    public void PropellantBucketMappingIsStable(string? defName, int expected)
        => Assert.Equal(expected, EnginePlumeCore.ColorBucketFromPropellant(defName));
}