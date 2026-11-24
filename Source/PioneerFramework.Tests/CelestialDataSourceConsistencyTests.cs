using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using PioneerFramework.Celestial;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

/// <summary>
/// P2 数据源单一化的回归面（2026-11-03）：
/// 1) 已发货**模板** body def 里“同时有物理 motion 与 environment”时，两处 dayLengthTicks 保持一致（模板卫生；运行期/生成实例以 physics 为准）；
/// 2) 视觉/投影的**物理大气顶**与**视觉壳厚度**是两个不同字段，不能混用；
/// 3) 自转周期解析优先级固定为 motion → environment → 0。
/// </summary>
public class CelestialDataSourceConsistencyTests
{
    private const string PhysicsBindingClass = "PioneerFramework.Celestial.CompProperties_ExternalPhysicsBinding";
    private const string EnvironmentClass = "PioneerFramework.Celestial.CompProperties_ExistingEnvironment";

    [Fact]
    public void ShippedBodiesKeepMotionAndEnvironmentDayLengthsEqual()
    {
        var environments = LoadEnvironmentDayLengths();
        var compared = new List<string>();
        foreach (string file in new[] { "CelestialBodies/CelestialBodies_SolarSystem.xml", "CelestialBodies/CelestialBodies_PlanetKinds.xml" })
            foreach (var body in XDocument.Load(CelestialDefaultContentTests.DataPath(file)).Root.Elements()
                .Where(element => element.Name == "PioneerFramework.CelestialBodyDef"))
            {
                string id = body.Element("defName")?.Value ?? "?";
                double? motionDayLength = DayLengthFromMotion(body, id);
                if (motionDayLength.HasValue) Assert.True(motionDayLength.Value > 0.0, id + ": motion day length must be positive");
                string environmentProfile = body.Element("comps")?.Elements("li")
                    .Where(comp => comp.Attribute("Class")?.Value == EnvironmentClass)
                    .Select(comp => comp.Element("profile")?.Value).SingleOrDefault();
                if (environmentProfile == null) continue;
                Assert.True(environments.TryGetValue(environmentProfile, out double environmentDayLength),
                    id + ": missing shipped environment profile " + environmentProfile);
                // 没有物理的天体允许 environment 作兼容 fallback；模板两者都有时保持一致（渲染/物理不能各取一侧）。
                // 程序化生成会按规则改写 motion spin，生成实例不要求与模板 environment 值相等（物理优先，见 CelestialEnvironmentRegressionTests）。
                if (!motionDayLength.HasValue) continue;
                Assert.Equal(environmentDayLength, motionDayLength.Value, 6);
                compared.Add(id);
            }
        // 已发货内容里同时具备 motion + environment 的天体：太阳系 2 个 + 行星种类 4 个。
        Assert.True(compared.Count >= 6, "expected at least 6 bodies with both motion and environment, got: " + string.Join(",", compared));
    }

    [Fact]
    public void RenderProjectionKeepsPhysicalTopAltitudeAndVisualShellSeparate()
    {
        var visuals = new[] {
            new CelestialVisualDescription("atmosphereVisual", CelestialVisualKind.Atmosphere,
                new CelestialVisualProfile("probeAtmosphere", .35, .55, .85, .8, null, null, 25000, 5000, 600000, 6000)) };
        var atmosphere = AtmosphereModel.Build(1.2, 8000, 100000);
        var projected = CelestialRenderProjection.Create(Description(), visuals, true, atmosphere, 60000);
        Assert.Equal(100000, projected.atmosphereThickness, 6);            // 物理大气顶 = air.topAltitude
        Assert.Equal(50000, projected.atmosphereVisualThickness, 6);       // 视觉壳厚度 = shellHeightMeters × 2
        Assert.Equal(60000, projected.dayLengthTicks, 6);

        var airless = CelestialRenderProjection.Create(Description(), Array.Empty<CelestialVisualDescription>(), true, default, 0);
        Assert.Equal(0.0, airless.atmosphereThickness);
        Assert.Equal(0.0, airless.atmosphereVisualThickness);
    }

    [Fact]
    public void DayLengthPrefersPhysicsMotionThenEnvironmentThenZero()
    {
        var motion = new CelestialMotionXml { rotation = new CelestialRotationXml { periodSeconds = 317900 } }.Compile();
        var physics = new ExternalPhysicsResolvedProperties("motion", null, motion);
        var environment = new ExistingEnvironmentResolvedProperties("environment",
            new CelestialEnvironmentProfile("probe", 60000, "primaryLight", Array.Empty<string>()));
        Assert.Equal(317900, CelestialDayLength.ResolveFrom(new ICelestialResolvedProperties[] { environment, physics }), 6);
        Assert.Equal(60000, CelestialDayLength.ResolveFrom(new ICelestialResolvedProperties[] { environment }), 6);
        // 外部 provider 无法用既有星历解析 ⇒ 按“无物理”处理，回落到 environment。
        var foreign = new ExternalPhysicsResolvedProperties("motion", null,
            new PhysicsInitialization("third.party", "opaque", 1, ReadOnlySpan<byte>.Empty));
        Assert.Equal(60000, CelestialDayLength.ResolveFrom(new ICelestialResolvedProperties[] { foreign, environment }), 6);
        Assert.Equal(0.0, CelestialDayLength.ResolveFrom(Array.Empty<ICelestialResolvedProperties>()));
    }

    private static CelestialBodyDescription Description()
        => new(CelestialInstanceId.Parse("probe:body"), "Probe", 1, 1, "Planet", 1000000);

    private static readonly double DefaultEnvironmentDayLength = new CelestialEnvironmentProfileDef().dayLengthTicks;

    private static Dictionary<string, double> LoadEnvironmentDayLengths()
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string file in new[] { "CelestialFramework/CelestialProfiles.xml", "CelestialFramework/CelestialPlanetKindProfiles.xml" })
            foreach (var element in XDocument.Load(CelestialDefaultContentTests.DataPath(file)).Root.Elements()
                .Where(element => element.Name == "PioneerFramework.Celestial.CelestialEnvironmentProfileDef"))
                // 省略 <dayLengthTicks> 时按 Def 字段默认值比较（不在这里复制常量）。
                result.Add(element.Element("defName")!.Value, element.Element("dayLengthTicks") is { } day
                    ? double.Parse(day.Value, CultureInfo.InvariantCulture)
                    : DefaultEnvironmentDayLength);
        return result;
    }

    /// <summary>用生产解析器取物理 motion 的 dayLengthTicks（v1 冻结载荷或 readable motion v2）；没有 motion 返 null。</summary>
    private static double? DayLengthFromMotion(System.Xml.Linq.XElement body, string id)
    {
        var physics = body.Element("comps")?.Elements("li").SingleOrDefault(comp => comp.Attribute("Class")?.Value == PhysicsBindingClass);
        if (physics == null) return null;
        PhysicsInitialization initialization;
        if (physics.Element("motion") is { } readable)
        {
            // 走真实 XML 读取（LoadDataFromXmlCustom）+ 生产 v2 编译，避免测试自己复刻字段语义。
            var document = new XmlDocument();
            document.LoadXml(readable.ToString());
            initialization = DirectXmlToObject.ObjectFromXml<CelestialMotionXml>(document.DocumentElement, false).Compile();
        }
        else if (physics.Element("initializationPayload") is { } payload)
        {
            initialization = new PhysicsInitialization(physics.Element("providerId")!.Value, physics.Element("modeId")!.Value,
                int.Parse(physics.Element("initializationVersion")?.Value ?? "1", CultureInfo.InvariantCulture), Convert.FromBase64String(payload.Value));
        }
        else return null;
        var parsed = ExistingEphemerisParameters.Parse(initialization);
        Assert.True(parsed.Success, id + ": motion initialization must parse: " + string.Join(",", parsed.Errors.Select(error => error.Code)));
        return parsed.Value.DayLengthTicks;
    }
}
