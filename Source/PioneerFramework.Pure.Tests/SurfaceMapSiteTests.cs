using PioneerFramework.Megastructures;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    public sealed class SurfaceMapSiteTests
    {
        [Fact]
        public void CellAddressAndParentIdentitySurviveReorderedMaps()
        {
            var address=StructureMapSiteAddress.ForCell(new(new(new("structure","surface"),"habitat"),17));
            var document=new FrameworkDocument{MapSites={new(){Address=address,MapParentId=37,MapId=99}}};
            var restored=MegastructureCodec.Decode(MegastructureCodec.Encode(document));
            Assert.Equal(address,Assert.Single(restored.MapSites).Address);
            Assert.Equal(37,restored.MapSites[0].MapParentId);
            Assert.Equal("cell:habitat:17",address.SiteKey);
        }
    }
}
