using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using PioneerFramework.GroundAssembly;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **载荷舱数据层**（2026-10-26 用户裁定 A 注册表 + 无 Map）。
    /// 这一层只验"能装出来、能存 id"；部署（生成新组）是下一步。
    /// </summary>
    public class PayloadBayDataTests
    {
        private static ShipPartDef Def(string name, string category) => new()
        {
            defName = name,
            label = name,
            category = category,
            massKg = 400f,
            boundsX = 3f,
            boundsY = 3f,
            boundsZ = 3f,
            stackLengthMeters = 3f,
            presets = new List<PartComponentPreset>(),
        };

        [Fact]
        public void BuildParts_WithoutCabin_StartsAtOrigin()
        {
            var def = Def("Test_Bay", "payload-bay");
            var spec = new RocketDesignSpec
            {
                parts = new List<DesignPartSpec>
                {
                    new() { defName = "Test_Bay", category = "payload-bay", quantity = 1 },
                },
            };

            var parts = DesignPartAssembler.BuildParts(spec, 10, 10, out _, _ => def,
                includeUnknownDefs: false, includeCabin: false);

            Assert.Single(parts);
            Assert.Equal(1, parts[0].partId);
            // 无舱盒 ⇒ Z 游标从 0 起（2026-10-28 轴改：堆叠/长度在 −Z），单件中心 = −stackLength/2
            Assert.Equal(-1.5, parts[0].positionShip.z, 6);
            Assert.NotNull(parts[0].GetComponent<PayloadBayComponentData>());
        }

        [Fact]
        public void PayloadBay_PresetAndRole()
        {
            var c = new PayloadBayComponentData();
            Assert.Equal(PartRole.Payload, c.Role);
            Assert.True(c.ApplyPreset("payloadDesignId", "7"));
            Assert.Equal(7, c.payloadDesignId);
            Assert.False(c.ApplyPreset("nope", "1"));
        }
    }
}
