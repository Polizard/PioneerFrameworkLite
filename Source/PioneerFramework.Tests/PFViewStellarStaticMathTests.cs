using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PioneerFramework.Tests;

public class PFViewStellarStaticMathTests
{
    [Theory]
    [InlineData(0, .91, .20, .37, .4, .24, .4)]
    [InlineData(1, -.84, -.18, .51, 2.1, .20, 1.8)]
    [InlineData(2, .21, .86, .46, 1.2, .14, 3.2)]
    [InlineData(3, -.18, -.93, .32, .2, .23, 4.7)]
    [InlineData(4, .27, .15, .95, 2.7, .15, 1.1)]
    [InlineData(5, -.62, .62, -.48, .8, .24, 5.6)]
    [InlineData(6, .61, -.35, -.71, 1.4, .19, 2.5)]
    [InlineData(7, -.09, .98, -.12, 2.2, .13, 3.9)]
    public void StaticTablesPreserveEachOriginalMagneticArch(
        int index, double x, double y, double z, double angle, double width, double phaseOffset)
        => AssertMagneticArchTables(ReadShader(), index, x, y, z, angle, width, phaseOffset);

    [Theory]
    // S3-a 只是换 march 的积分方式；已批准的磁环几何必须逐字沿用 legacy 的常量表。
    [InlineData(0, .91, .20, .37, .4, .24, .4)]
    [InlineData(1, -.84, -.18, .51, 2.1, .20, 1.8)]
    [InlineData(2, .21, .86, .46, 1.2, .14, 3.2)]
    [InlineData(3, -.18, -.93, .32, .2, .23, 4.7)]
    [InlineData(4, .27, .15, .95, 2.7, .15, 1.1)]
    [InlineData(5, -.62, .62, -.48, .8, .24, 5.6)]
    [InlineData(6, .61, -.35, -.71, 1.4, .19, 2.5)]
    [InlineData(7, -.09, .98, -.12, 2.2, .13, 3.9)]
    public void S3AShaderPreservesEachOriginalMagneticArch(
        int index, double x, double y, double z, double angle, double width, double phaseOffset)
        => AssertMagneticArchTables(ReadS3AShader(), index, x, y, z, angle, width, phaseOffset);

    [Theory]
    [InlineData("square", -.5f, .25)]
    [InlineData("square", 0f, 0d)]
    [InlineData("square", .25f, .0625)]
    [InlineData("cube", 0f, 0d)]
    [InlineData("cube", .5f, .125)]
    [InlineData("cube", 1f, 1d)]
    [InlineData("fifth", .1f, .00001)]
    [InlineData("fifth", .5f, .03125)]
    [InlineData("fifth", 1f, 1d)]
    public void ShippedIntegerPowerExpressionsKeepTheSameMathematicalCurve(string function, float input, double expected)
        => AssertIntegerPowerCurve(ReadShader(), function, input, expected);

    [Theory]
    // S3-a 的发射模型沿用 legacy 的整数幂表达式（square/cube/fifth），曲线不许漂移。
    [InlineData("square", -.5f, .25)]
    [InlineData("square", 0f, 0d)]
    [InlineData("square", .25f, .0625)]
    [InlineData("cube", 0f, 0d)]
    [InlineData("cube", .5f, .125)]
    [InlineData("cube", 1f, 1d)]
    [InlineData("fifth", .1f, .00001)]
    [InlineData("fifth", .5f, .03125)]
    [InlineData("fifth", 1f, 1d)]
    public void S3AShaderKeepsTheSameIntegerPowerCurves(string function, float input, double expected)
        => AssertIntegerPowerCurve(ReadS3AShader(), function, input, expected);

    [Fact]
    public void S3AShaderCopiesTheApprovedStaticGeometryAndPowerExpressionsVerbatim()
        => AssertApprovedStaticTokensCopiedVerbatim(ReadS3AShader());

    private static void AssertApprovedStaticTokensCopiedVerbatim(string candidate)
    {
        // "逐字沿用"是可选加速/近似档与"已保真重做方案"的分界线：常量表/幂表达式必须逐字节相同
        // （忽略排版空白），否则就是重新发明了一套视觉，必须走观感阈值重验。
        string legacy = ReadShader();

        foreach (string name in new[] { "archNormals", "archTangents", "archBinormals" })
            Assert.Equal(Compact(TableDeclaration(legacy, name)), Compact(TableDeclaration(candidate, name)));

        foreach (string name in new[] { "square", "cube", "fifth" })
            Assert.Equal(Compact(FunctionDeclaration(legacy, name)), Compact(FunctionDeclaration(candidate, name)));
    }

    private static void AssertMagneticArchTables(
        string shader, int index, double x, double y, double z, double angle, double width, double phaseOffset)
    {
        double[] actualNormal = Table(shader, "archNormals")[index];
        double[] actualTangent = Table(shader, "archTangents")[index];
        double[] actualBinormal = Table(shader, "archBinormals")[index];
        var normal = new DVector3(x, y, z).normalized;
        var side = DVector3.Cross(normal, Math.Abs(normal.y) > .9 ? new DVector3(1, 0, 0) : new DVector3(0, 1, 0)).normalized;
        var tangent = side * Math.Cos(angle) + DVector3.Cross(normal, side) * Math.Sin(angle);
        var binormal = DVector3.Cross(normal, tangent);
        AssertComponents(actualNormal, normal, Math.Cos(width));
        AssertComponents(actualTangent, tangent, Math.Sin(width));
        AssertComponents(actualBinormal, binormal, phaseOffset);
        var n = new DVector3(actualNormal[0], actualNormal[1], actualNormal[2]);
        var t = new DVector3(actualTangent[0], actualTangent[1], actualTangent[2]);
        var b = new DVector3(actualBinormal[0], actualBinormal[1], actualBinormal[2]);
        Assert.InRange(Math.Abs(n.magnitude - 1), 0, 1e-6);
        Assert.InRange(Math.Abs(t.magnitude - 1), 0, 1e-6);
        Assert.InRange(Math.Abs(b.magnitude - 1), 0, 1e-6);
        Assert.InRange(Math.Abs(DVector3.Dot(n, t)), 0, 1e-6);
        Assert.InRange((DVector3.Cross(n, t) - b).magnitude, 0, 1e-6);
    }

    private static void AssertIntegerPowerCurve(string shader, string function, float input, double expected)
    {
        var match = Regex.Match(shader, @"float\s+" + function + @"\s*\(float\s+x\)\s*\{([^}]+)\}");
        Assert.True(match.Success, "Missing executable GLSL power function: " + function);
        using var evaluator = new DataTable { Locale = CultureInfo.InvariantCulture };
        var values = new Dictionary<string, float> { ["x"] = input };
        float Evaluate(string expression)
        {
            expression = Regex.Replace(expression, @"\b[A-Za-z_]\w*\b", token =>
                values.TryGetValue(token.Value, out float value) ? "(" + value.ToString("R", CultureInfo.InvariantCulture) + ")" : token.Value);
            return Convert.ToSingle(evaluator.Compute(expression, ""), CultureInfo.InvariantCulture);
        }
        foreach (Match assignment in Regex.Matches(match.Groups[1].Value, @"float\s+(\w+)\s*=\s*([^;]+);"))
            values[assignment.Groups[1].Value] = Evaluate(assignment.Groups[2].Value);
        var result = Regex.Match(match.Groups[1].Value, @"return\s+([^;]+);");
        Assert.True(result.Success);
        Assert.InRange(Math.Abs(Evaluate(result.Groups[1].Value) - expected), 0, 2e-7);
    }

    private static string ReadShader() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "PF_StellarVolume.frag.glsl"));

    private static string ReadS3AShader() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "PF_StellarVolumeS3A.frag.glsl"));

    private static string TableDeclaration(string shader, string name)
    {
        var table = Regex.Match(shader, @"const\s+vec4\s+" + name + @"\[8\]\s*=\s*vec4\[8\]\(([\s\S]*?)\);");
        Assert.True(table.Success, "Missing static magnetic geometry: " + name);
        return table.Value;
    }

    private static string FunctionDeclaration(string shader, string name)
    {
        var match = Regex.Match(shader, @"float\s+" + name + @"\s*\(float\s+x\)\s*\{[^}]+\}");
        Assert.True(match.Success, "Missing integer power function: " + name);
        return match.Value;
    }

    private static string Compact(string text) => Regex.Replace(text, @"\s+", "");

    private static double[][] Table(string shader, string name)
    {
        var table = Regex.Match(shader, @"const\s+vec4\s+" + name + @"\[8\]\s*=\s*vec4\[8\]\(([\s\S]*?)\);");
        Assert.True(table.Success, "Missing static magnetic geometry: " + name);
        var rows = Regex.Matches(table.Groups[1].Value, @"vec4\(([^)]+)\)").Cast<Match>()
            .Select(row => row.Groups[1].Value.Split(',').Select(value => (double)float.Parse(value, CultureInfo.InvariantCulture)).ToArray()).ToArray();
        Assert.Equal(8, rows.Length);
        Assert.All(rows, row => Assert.Equal(4, row.Length));
        return rows;
    }

    private static void AssertComponents(double[] actual, DVector3 direction, double fourth)
    {
        double[] expected = { direction.x, direction.y, direction.z, fourth };
        for (int i = 0; i < expected.Length; ++i)
            Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 3e-7);
    }
}
