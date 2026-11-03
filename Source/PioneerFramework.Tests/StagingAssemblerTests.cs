using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.GroundAssembly;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 分级标号（2026-10-27 用户裁定）：**没有分离器件**，`stageId` 由设计标定；
    /// 0 = 上面级，数字越大越靠下；装配器只透传，不自己加级。
    /// </summary>
    public class StagingAssemblerTests
    {
        private static ShipPartDef Def(string name, string category) => new()
        {
            defName = name,
            label = name,
            category = category,
            massKg = 100f,
            boundsX = 2f,
            boundsY = 2f,
            boundsZ = 2f,
            stackLengthMeters = 2f,
            presets = new List<PartComponentPreset>(),
        };

        private static List<SpacePart> Build(params DesignPartSpec[] entries)
        {
            var spec = new RocketDesignSpec { parts = new List<DesignPartSpec>(entries) };
            return DesignPartAssembler.BuildParts(spec, 1, 1, out _, name =>
                name switch
                {
                    "T" => Def("T", "thruster"),
                    "K" => Def("K", "tank"),
                    _ => null,
                }, includeUnknownDefs: false, includeCabin: false);
        }

        [Fact]
        public void Default_IsAllStageZero()
        {
            var parts = Build(
                new DesignPartSpec { defName = "T", category = "thruster" },
                new DesignPartSpec { defName = "K", category = "tank" });

            Assert.All(parts, p => Assert.Equal(0, p.stageId));
        }

        [Fact]
        public void ExplicitStageId_IsCopiedPerEntry_NotInherited()
        {
            var parts = Build(
                new DesignPartSpec { defName = "T", category = "thruster" },
                new DesignPartSpec { defName = "K", category = "tank", stageId = 1 },
                new DesignPartSpec { defName = "T", category = "thruster" });

            Assert.Equal(0, parts[0].stageId);
            Assert.Equal(1, parts[1].stageId);
            Assert.Equal(0, parts[2].stageId);   // 逐条透传：不会被上一条的级号带跑
        }

        [Fact]
        public void ThreeStages_ExplicitNumbers()
        {
            var parts = Build(
                new DesignPartSpec { defName = "T", category = "thruster", stageId = 0 },
                new DesignPartSpec { defName = "K", category = "tank", stageId = 1 },
                new DesignPartSpec { defName = "T", category = "thruster", stageId = 2 });

            Assert.Equal(0, parts[0].stageId);
            Assert.Equal(1, parts[1].stageId);
            Assert.Equal(2, parts[2].stageId);
        }

        [Fact]
        public void Quantity_AppliesStageToAllUnitsOfEntry()
        {
            var parts = Build(
                new DesignPartSpec { defName = "K", category = "tank", quantity = 3, stageId = 1 });

            Assert.Equal(3, parts.Count);
            Assert.All(parts, p => Assert.Equal(1, p.stageId));
        }
    }
}
