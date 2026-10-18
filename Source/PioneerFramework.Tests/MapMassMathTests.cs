using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **Map 质量口径**（2026-10-17 用户裁定）：建筑 + 屋顶 + 工质；
    /// 不知道多重 ⇒ 1 kg（无 Def 的建筑、无质量字段的屋顶=1 kg/格）。
    /// 事件接线与真实 Thing 的质量在游戏内一键验收（`MultipartAcceptanceRunner.RunMapMassChecks`）。
    /// </summary>
    public class MapMassMathTests
    {
        [Fact]
        public void UnknownBuilding_DefaultsToOneKg()
        {
            Assert.Equal(1.0, SpaceMapComp.BuildingMassKgOf(null), 12);
        }

        [Fact]
        public void RoofCellMass_IsOneKgPerCell()
        {
            Assert.Equal(1.0, SpaceMapComp.RoofCellMassKg, 12);
        }
    }
}
