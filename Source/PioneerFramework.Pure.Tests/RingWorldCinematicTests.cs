using System;
using System.Linq;
using PioneerFramework;
using PioneerFramework.RingWorld;
using Xunit;

/// <summary>
/// 2026-10-17 电影感视觉升级的纯核回归测试：法线约定、UV 全带宽、完整环取景、
/// 环外装饰几何（预算/确定性/法线/不侵入可拾取面）。
/// </summary>
public class RingWorldCinematicTests
{
    [Fact]
    public void UvRepeatsKeepTexelAspectAcrossPresets()
    {
        var small = new RingGeometry(new RingTopology(256, 3), 320);
        var large = new RingGeometry(new RingTopology(1024, 12), 320);
        // 新贴图是 4:1 长条（内面 4096×1024、外壳 2048×512）：环向重复数应让 texel 接近方形，
        // 且 256×3 与 1024×12 两个预设的贴图世界尺度一致（旧公式在 1024×12 上会横向压 2.7–5.3×）。
        Assert.Equal(22, RingIndustrialGeometry.SurfaceRepeats(small));
        Assert.Equal(24, RingIndustrialGeometry.SurfaceRepeats(large));
        Assert.Equal(22, RingIndustrialGeometry.StructureRepeats(small));
        Assert.Equal(24, RingIndustrialGeometry.StructureRepeats(large));

        static double TexelAspect(RingGeometry geometry, int repeats, int textureWidth, int textureHeight)
        {
            double circumference = Math.Tau * geometry.Radius;
            double uPerUnit = (double)repeats * textureWidth / circumference;
            double vPerUnit = (double)textureHeight / (2 * geometry.HalfWidth);
            return uPerUnit / vPerUnit;
        }

        double smallAspect = TexelAspect(small, RingIndustrialGeometry.SurfaceRepeats(small), 4096, 1024);
        double largeAspect = TexelAspect(large, RingIndustrialGeometry.SurfaceRepeats(large), 4096, 1024);
        Assert.InRange(smallAspect, 0.9, 1.1);
        Assert.InRange(largeAspect, 0.9, 1.1);
        Assert.InRange(Math.Abs(smallAspect / largeAspect - 1.0), 0.0, 0.15);
    }

    [Fact]
    public void HalfWidthMatchesIndustrialGeneratorFormula()
    {
        var geometry = new RingGeometry(new RingTopology(256, 3), 280);
        double expected = geometry.RowSpacing * (geometry.Topology.Rows - 1) / 2
                          + geometry.Spacing / Math.Sqrt(3);
        Assert.Equal(expected, geometry.HalfWidth, 10);
        Assert.True(geometry.HalfWidth > 0);
    }

    [Fact]
    public void HabitatNormalsAreUnitAndPointTowardRingCenter()
    {
        var geometry = new RingGeometry(new RingTopology(256, 3), 280);
        for (int tile = 0; tile < geometry.Topology.TileCount; tile += 37)
        {
            foreach (DVector3 point in geometry.Vertices(tile).Append(geometry.Center(tile)))
            {
                DVector3 normal = RingIndustrialGeometry.HabitatNormal(point);
                Assert.Equal(1.0, normal.magnitude, 10);
                double radial = Math.Sqrt(point.x * point.x + point.z * point.z);
                DVector3 outward = new DVector3(point.x / radial, 0, point.z / radial);
                Assert.True(DVector3.Dot(normal, outward) < -0.999999,
                    $"tile {tile} habitat normal must point inward, dot={DVector3.Dot(normal, outward)}");
            }
        }
    }

    [Fact]
    public void HullFaceNormalsAreUnitAndPointOutward()
    {
        var geometry = new RingGeometry(new RingTopology(128, 6), 280);
        var hulls = RingIndustrialGeometry.Build(geometry)
            .Where(q => q.Material == RingVisualMaterial.Hull).ToArray();
        Assert.NotEmpty(hulls);
        foreach (RingVisualQuad quad in hulls)
        {
            DVector3 normal = RingIndustrialGeometry.FaceNormal(quad);
            Assert.Equal(1.0, normal.magnitude, 10);
            DVector3 center = (quad.A.Position + quad.B.Position + quad.C.Position + quad.D.Position) / 4;
            double radial = Math.Sqrt(center.x * center.x + center.z * center.z);
            DVector3 outward = new DVector3(center.x / radial, 0, center.z / radial);
            Assert.True(DVector3.Dot(normal, outward) > 0.99);
        }
    }

    [Fact]
    public void CapFaceNormalsAreUnitAndPointAlongAxialDirection()
    {
        var geometry = new RingGeometry(new RingTopology(128, 6), 280);
        int upper = 0, lower = 0;
        foreach (RingVisualQuad quad in RingIndustrialGeometry.Build(geometry))
        {
            double y = quad.A.Position.y;
            if (Math.Abs(quad.B.Position.y - y) > 1e-9 || Math.Abs(quad.C.Position.y - y) > 1e-9
                || Math.Abs(quad.D.Position.y - y) > 1e-9) continue; // 只要水平端面（全部 4 点同 y）
            bool touchesRingRadius = new[] { quad.A, quad.B, quad.C, quad.D }.Any(p =>
            {
                double radial = Math.Sqrt(p.Position.x * p.Position.x + p.Position.z * p.Position.z);
                return Math.Abs(radial - geometry.Radius) < 1e-9;
            });
            if (!touchesRingRadius) continue; // 只要半径处的环形端面（梁/管也有水平面）
            DVector3 normal = RingIndustrialGeometry.FaceNormal(quad);
            Assert.Equal(1.0, normal.magnitude, 10);
            if (y > 0) { Assert.True(normal.y > 0.99); upper++; }
            else { Assert.True(normal.y < -0.99); lower++; }
        }
        Assert.True(upper > 0 && lower > 0, $"expected upper/lower caps, got {upper}/{lower}");
    }

    [Fact]
    public void HabitatUvVSpansFullBandWidthForNewTextureLayout()
    {
        var geometry = new RingGeometry(new RingTopology(256, 3), 280);
        double min = double.MaxValue, max = double.MinValue;
        for (int tile = 0; tile < geometry.Topology.TileCount; tile++)
        {
            foreach (DVector3 point in geometry.Vertices(tile))
            {
                RingVisualPoint uv = RingIndustrialGeometry.SurfaceUV(geometry, tile, point);
                Assert.True(double.IsFinite(uv.U) && double.IsFinite(uv.V));
                min = Math.Min(min, uv.V);
                max = Math.Max(max, uv.V);
            }
        }
        Assert.InRange(min, 0.0, 1e-9);
        Assert.InRange(max, 1.0 - 1e-9, 1.0);
    }

    [Theory]
    [InlineData(16f / 9f)]
    [InlineData(21f / 9f)]
    [InlineData(32f / 9f)]
    public void PresetAltitudeFitsCompleteRingAtCommonAspects(float aspect)
    {
        var geometry = new RingGeometry(new RingTopology(256, 3), 320);
        float altitude = RingViewFraming.PresetAltitude(geometry, aspect);
        Assert.InRange(altitude, RingViewFraming.MinAltitude, RingViewFraming.MaxAltitude);
        Assert.True(RingViewFraming.RingFits(geometry.Radius, geometry.HalfWidth, altitude, aspect),
            $"complete ring must fit at aspect {aspect}, altitude {altitude}");
    }

    [Fact]
    public void PresetAltitudeClampsForFourThreeAspectAndDocumentsPartialFit()
    {
        var geometry = new RingGeometry(new RingTopology(256, 3), 320);
        float altitude = RingViewFraming.PresetAltitude(geometry, 4f / 3f);
        // 4:3 屏幕 + 20° FOV 下整环装不下（旧版"完整环"契约明确只对常见宽屏成立）；
        // 这里锁住"clamp 到上限、且不谎称能装下"的行为。
        Assert.Equal(RingViewFraming.MaxAltitude, altitude);
        Assert.False(RingViewFraming.RingFits(geometry.Radius, geometry.HalfWidth, altitude, 4f / 3f));
    }

    [Fact]
    public void PresetViewDirectionIsUnitAndInclinedAboveRingPlane()
    {
        DVector3 direction = RingViewFraming.PresetViewDirection();
        Assert.Equal(0, direction.x, 10);
        Assert.Equal(1, direction.magnitude, 10);
        Assert.InRange(direction.y, 0.4, 0.8); // 35° 俯仰
        Assert.True(direction.z > 0.5);
    }

    [Fact]
    public void ShellHasRaisedReliefModulesAcrossAxialBands()
    {
        // 2026-10-17 用户反馈：移除 X 形交叉斜撑，改为凹凸装甲块 + 纵向脊梁。
        // 这里钉住"外壳确实存在明显凸出结构、且分布在多个轴向高度"（不是一圈平板）。
        var geometry = new RingGeometry(new RingTopology(256, 3), 280);
        double back = geometry.Radius + geometry.Spacing * .08;
        double halfWidth = geometry.HalfWidth;
        RingVisualQuad[] quads = RingIndustrialGeometry.Build(geometry).ToArray();
        RingVisualQuad[] raised = quads.Where(q => new[] { q.A, q.B, q.C, q.D }
            .All(p => Radial(p.Position) > back + geometry.Spacing * .05)).ToArray();
        Assert.NotEmpty(raised);
        int bands = raised.Select(q => Math.Round(
                (q.A.Position.y + q.B.Position.y + q.C.Position.y + q.D.Position.y) / 4 / (halfWidth * .5)))
            .Distinct().Count();
        Assert.True(bands >= 3, $"expected relief modules across >=3 axial bands, got {bands}");

        double maxRadial = quads.SelectMany(q => new[] { q.A, q.B, q.C, q.D })
            .Max(p => Radial(p.Position));
        Assert.True(maxRadial > geometry.Radius + geometry.Spacing * .4,
            $"expected pronounced raised geometry, max radial {maxRadial}");

        static double Radial(DVector3 p) => Math.Sqrt(p.x * p.x + p.z * p.z);
    }

    [Fact]
    public void ShellHasVerticalGantryAboveBandEdge()
    {
        // 新装饰不再用交叉斜撑，而是沿垂直环平面方向（±Y）伸出 L 形吊臂/立柱，强调上下结构。
        var geometry = new RingGeometry(new RingTopology(256, 3), 280);
        double halfWidth = geometry.HalfWidth;
        Assert.Contains(RingIndustrialGeometry.Build(geometry), q =>
        {
            DVector3[] points = { q.A.Position, q.B.Position, q.C.Position, q.D.Position };
            if (!points.All(p => p.y > halfWidth + 1e-9)) return false;
            return points.Any(p => Math.Sqrt(p.x * p.x + p.z * p.z) > geometry.Radius + geometry.Spacing * .1);
        });
    }

    [Fact]
    public void NoFullBandDiagonalCrossBracesOnShell()
    {
        // 用户 2026-10-17 反馈：外侧 X 形钢架不美观。旧的交叉斜撑由"跨整个带宽 + 两端 Y 中心不同"
        // 的斜条四边形组成；这里同时按"y 跨度≈2×halfWidth"和"u 两端平均 Y 有明显偏移"识别斜撑。
        // 正带的横向肋骨虽然也跨整个带宽，但两端 Y 中心相同（矩形），不应被误判。
        var geometry = new RingGeometry(new RingTopology(256, 3), 280);
        double back = geometry.Radius + geometry.Spacing * .08;
        int braceLike = RingIndustrialGeometry.Build(geometry).Count(q =>
        {
            if (q.Material != RingVisualMaterial.Frame) return false;
            RingVisualPoint[] p = { q.A, q.B, q.C, q.D };
            double yRange = p.Max(v => v.Position.y) - p.Min(v => v.Position.y);
            if (yRange <= geometry.HalfWidth * 1.8) return false;
            double minRadial = p.Min(v => Math.Sqrt(v.Position.x * v.Position.x + v.Position.z * v.Position.z));
            if (minRadial <= back + geometry.Spacing * .005) return false;
            double minU = p.Min(v => v.U), maxU = p.Max(v => v.U);
            double meanAtMinU = p.Where(v => Math.Abs(v.U - minU) < 1e-9).Average(v => v.Position.y);
            double meanAtMaxU = p.Where(v => Math.Abs(v.U - maxU) < 1e-9).Average(v => v.Position.y);
            return Math.Abs(meanAtMinU - meanAtMaxU) > geometry.HalfWidth * .25;
        });
        Assert.Equal(0, braceLike);
    }

    [Fact]
    public void SignalPillarsExtendAboveAndBelowRingBand()
    {
        // 用户要求：垂直于环平面（±Y）增加"信号柱"以拓展上下结构；上下两侧都应有明显延伸。
        var geometry = new RingGeometry(new RingTopology(256, 3), 280);
        var quads = RingOrnamentGeometry.Build(geometry);
        double maxY = quads.SelectMany(q => new[] { q.A, q.B, q.C, q.D }).Max(p => p.y);
        double minY = quads.SelectMany(q => new[] { q.A, q.B, q.C, q.D }).Min(p => p.y);
        Assert.True(maxY > geometry.HalfWidth + 5,
            $"expected signal pillar above band edge ({geometry.HalfWidth + 5}), maxY={maxY}");
        Assert.True(minY < -geometry.HalfWidth - 5,
            $"expected signal pillar below band edge ({-geometry.HalfWidth - 5}), minY={minY}");
    }

    [Fact]
    public void OrnamentLayoutHasNewReliefAndPillarGroups()
    {
        var small = RingOrnamentGeometry.Layout(new RingGeometry(new RingTopology(256, 3), 280));
        Assert.Equal(12, small.Pillars);
        Assert.InRange(small.Pods, 8, 24);
        Assert.Equal(8, small.Panels);
        var large = RingOrnamentGeometry.Layout(new RingGeometry(new RingTopology(1024, 12), 280));
        Assert.True(large.Pillars > small.Pillars);
        Assert.True(large.Pods > small.Pods);
        Assert.True(large.Panels > small.Panels);
        Assert.Equal(34, large.Pillars);
        Assert.Equal(16, large.Pods);
        Assert.Equal(21, large.Panels);
    }

    [Fact]
    public void OrnamentGeometryIsDeterministicAndWithinBudget()
    {
        foreach ((int columns, int rows) in new[] { (256, 3), (1024, 12), (4096, 32) })
        {
            var geometry = new RingGeometry(new RingTopology(columns, rows), 280);
            var first = RingOrnamentGeometry.Build(geometry);
            var second = RingOrnamentGeometry.Build(geometry);
            Assert.InRange(first.Count, 1, RingOrnamentGeometry.MaxQuads);
            Assert.Equal(first.Count, second.Count);
            for (int i = 0; i < first.Count; i++)
            {
                RingOrnamentQuad a = first[i], b = second[i];
                Assert.Equal(a.Material, b.Material);
                Assert.Equal(a.A, b.A);
                Assert.Equal(a.B, b.B);
                Assert.Equal(a.C, b.C);
                Assert.Equal(a.D, b.D);
                Assert.Equal(a.Normal, b.Normal);
            }
        }
    }

    [Fact]
    public void OrnamentQuadsAreFiniteNonDegenerateAndOutsideHabitableSurface()
    {
        var geometry = new RingGeometry(new RingTopology(256, 3), 280);
        var quads = RingOrnamentGeometry.Build(geometry);
        Assert.Contains(quads, q => q.Material == RingOrnamentMaterial.Metal);
        Assert.Contains(quads, q => q.Material == RingOrnamentMaterial.Glow);
        foreach (RingOrnamentQuad quad in quads)
        {
            foreach (DVector3 point in new[] { quad.A, quad.B, quad.C, quad.D })
            {
                Assert.True(double.IsFinite(point.x) && double.IsFinite(point.y) && double.IsFinite(point.z));
                double radial = Math.Sqrt(point.x * point.x + point.z * point.z);
                Assert.True(radial > geometry.Radius + 0.5,
                    $"ornament must stay outside habitable surface, radial={radial}");
            }
            DVector3 cross = DVector3.Cross(quad.B - quad.A, quad.C - quad.A);
            Assert.True(cross.magnitude > 1e-9);
            Assert.Equal(1.0, quad.Normal.magnitude, 10);
            Assert.True(DVector3.Dot(cross.normalized, quad.Normal) > 0.99);
            Assert.InRange(quad.Alpha, 0.0, 1.0);
        }
    }

    [Fact]
    public void OrnamentHullUvsKeepShellTexelScale()
    {
        var method = typeof(RingOrnamentGeometry).GetMethod("HullUV");
        Assert.NotNull(method);
        foreach ((int columns, int rows) in new[] { (256, 3), (1024, 12), (4096, 32) })
        {
            var geometry = new RingGeometry(new RingTopology(columns, rows), 320);
            double expected = 512 / (2 * geometry.HalfWidth);
            foreach (var q in RingOrnamentGeometry.Build(geometry).Where(q => q.Material == RingOrnamentMaterial.Metal))
            {
                var a = ((double U, double V))method.Invoke(null, new object[] { geometry, q, q.U0, q.V0 })!;
                var c = ((double U, double V))method.Invoke(null, new object[] { geometry, q, q.U1, q.V1 })!;
                Assert.Equal(expected, 2048 * (c.U - a.U) / (q.D - q.A).magnitude, 9);
                Assert.Equal(expected, 512 * (c.V - a.V) / (q.B - q.A).magnitude, 9);
            }
        }
    }

    [Fact]
    public void AxialPillarsAndPanelsHaveTerminalGlow()
    {
        var geometry = new RingGeometry(new RingTopology(256, 3), 320);
        var layout = RingOrnamentGeometry.Layout(geometry);
        var lights = RingOrnamentGeometry.Build(geometry)
            .Where(q => q.Material.ToString() == "TerminalGlow").ToArray();
        Assert.True(lights.Length >= 2 * (layout.Pillars + layout.Panels),
            "each axial pillar and antenna panel needs two terminal glow faces");
        Assert.All(lights, q => Assert.True(Math.Abs((q.A.y + q.B.y + q.C.y + q.D.y) / 4) > geometry.HalfWidth + 4));
        Assert.True(lights.Select(q => q.Alpha).Distinct().Count() > 4, "terminal lights need staggered phases");
    }

    [Fact]
    public void TerminalLightHasClosedEmissiveCore()
    {
        var geometry = new RingGeometry(new RingTopology(256, 3), 320);
        var layout = RingOrnamentGeometry.Layout(geometry);
        var cores = RingOrnamentGeometry.Build(geometry).Where(q => q.Material.ToString() == "TerminalCore").ToArray();
        Assert.True(cores.Length >= 6 * (layout.Pillars + layout.Panels));
        Assert.Equal(0, cores.Length % 6);
        foreach (var solid in cores.Chunk(6))
        {
            var center = solid.SelectMany(q => new[] { q.A, q.B, q.C, q.D })
                .Aggregate(DVector3.zero, (sum, p) => sum + p) / 24;
            Assert.All(solid, q => Assert.True(DVector3.Dot(q.Normal, (q.A + q.B + q.C + q.D) / 4 - center) > 0));
        }
    }

    [Fact]
    public void TerminalHaloHasReadableWorldWidth()
    {
        var geometry = new RingGeometry(new RingTopology(256, 3), 320);
        var halos = RingOrnamentGeometry.Build(geometry).Where(q => q.Material == RingOrnamentMaterial.TerminalGlow).ToArray();
        Assert.NotEmpty(halos);
        Assert.All(halos, q =>
        {
            double a = (q.B - q.A).magnitude, b = (q.D - q.A).magnitude;
            Assert.Equal(10.5, Math.Max(a, b), 9);
            Assert.True(Math.Min(a, b) >= 2.1, "axial halo depth must also expand while staying outside the ring");
        });
    }

    [Fact]
    public void OrnamentMetalHasClosedOutwardWoundSurfaces()
    {
        // A reversed box side or a single metal sheet leaves unmatched directed edges.
        var geometry = new RingGeometry(new RingTopology(256, 3), 320);
        var edges = new System.Collections.Generic.Dictionary<(DVector3, DVector3), int>();
        foreach (var quad in RingOrnamentGeometry.Build(geometry).Where(q => q.Material == RingOrnamentMaterial.Metal
            || q.Material == RingOrnamentMaterial.TerminalCore))
        {
            var points = new[] { quad.A, quad.B, quad.C, quad.D };
            for (int i = 0; i < 4; i++)
            {
                var edge = (points[i], points[(i + 1) % 4]);
                edges.TryGetValue(edge, out int count);
                edges[edge] = count + 1;
            }
        }
        foreach (var edge in edges)
        {
            edges.TryGetValue((edge.Key.Item2, edge.Key.Item1), out int reverse);
            Assert.True(reverse == edge.Value,
                $"metal edge must have opposite-wound neighbor: {edge.Key}, forward={edge.Value}, reverse={reverse}");
        }

        var firstBox = RingOrnamentGeometry.Build(geometry).Take(6).ToArray();
        var center = firstBox.SelectMany(q => new[] { q.A, q.B, q.C, q.D })
            .Aggregate(DVector3.zero, (sum, p) => sum + p) / 24;
        foreach (var q in firstBox)
            Assert.True(DVector3.Dot(q.Normal, (q.A + q.B + q.C + q.D) / 4 - center) > 0,
                "box normal must face away from its volume center");
    }

    [Fact]
    public void OrnamentLightCentersStayOutsideOpaqueHousings()
    {
        var geometry = new RingGeometry(new RingTopology(256, 3), 320);
        var quads = RingOrnamentGeometry.Build(geometry);
        var solids = quads.Where(q => q.Material == RingOrnamentMaterial.Metal || q.Material == RingOrnamentMaterial.TerminalCore).Chunk(6).ToArray();
        foreach (var light in quads.Where(q => q.Material == RingOrnamentMaterial.Glow || q.Material == RingOrnamentMaterial.TerminalGlow))
        {
            var lightCenter = (light.A + light.B + light.C + light.D) / 4;
            foreach (var solid in solids)
                Assert.False(solid.All(face => DVector3.Dot(face.Normal, lightCenter - face.A) < 1e-6),
                    "light center must stay outside opaque housing so depth testing does not hide its brightest point");
        }
    }

    [Fact]
    public void OrnamentMetalUvHasUniformWorldScale()
    {
        foreach ((int columns, int rows) in new[] { (256, 3), (1024, 12), (4096, 32) })
        {
            var geometry = new RingGeometry(new RingTopology(columns, rows), 320);
            foreach (var quad in RingOrnamentGeometry.Build(geometry).Where(q => q.Material == RingOrnamentMaterial.Metal))
            {
                double uDensity = 1024 * Math.Abs(quad.U1 - quad.U0) / (quad.D - quad.A).magnitude;
                double vDensity = 1024 * Math.Abs(quad.V1 - quad.V0) / (quad.B - quad.A).magnitude;
                Assert.InRange(uDensity, 24, 64);
                Assert.InRange(vDensity, 24, 64);
                Assert.InRange(uDensity / vDensity, 0.99, 1.01);
            }
        }
    }

    [Fact]
    public void OrnamentLayoutScalesWithColumnsButStaysCapped()
    {
        var small = RingOrnamentGeometry.Layout(new RingGeometry(new RingTopology(256, 3), 280));
        var large = RingOrnamentGeometry.Layout(new RingGeometry(new RingTopology(1024, 12), 280));
        Assert.Equal(16, small.Towers);
        Assert.Equal(64, large.Towers);
        Assert.True(small.Fins <= 32 && small.Beacons <= 48);
        Assert.True(large.Fins <= 32 && large.Beacons <= 48);
    }
}
