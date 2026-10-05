using PioneerFramework;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    public class ShipSurfaceTransferLayoutTests
    {
        [Fact]
        public void MapsCellsFromMinimumCoordinatesRegardlessOfInputOrder()
        {
            var cells = new[] { new IntVec3(12, 0, 24), new IntVec3(10, 0, 20), new IntVec3(11, 0, 20) };
            Assert.True(ShipSurfaceTransferLayout.TryCreate(cells, new IntVec2(40, 40),
                new IntVec2(80, 80), new IntVec3(30, 0, 40), out var layout, out _));
            Assert.Equal(new IntVec3(30, 0, 40), layout.MapCell(new IntVec3(10, 0, 20)));
            Assert.Equal(new IntVec3(32, 0, 44), layout.MapCell(new IntVec3(12, 0, 24)));
            Assert.Equal(3, layout.SourceCells.Count);
        }

        [Fact]
        public void RejectsEmptyAndOutOfBoundsSelections()
        {
            Assert.False(ShipSurfaceTransferLayout.TryCreate(new IntVec3[0], new IntVec2(10, 10),
                new IntVec2(10, 10), IntVec3.Zero, out _, out _));
            Assert.False(ShipSurfaceTransferLayout.TryCreate(new[] { new IntVec3(9, 0, 9) },
                new IntVec2(10, 10), new IntVec2(10, 10), new IntVec3(10, 0, 9), out _, out _));
            Assert.False(ShipSurfaceTransferLayout.TryCreate(new[] { new IntVec3(-1, 0, 0) },
                new IntVec2(10, 10), new IntVec2(10, 10), IntVec3.Zero, out _, out _));
        }

        [Fact]
        public void DetectsIncompleteMultiCellFootprint()
        {
            Assert.True(ShipSurfaceTransferLayout.TryCreate(new[] { new IntVec3(4, 0, 4) },
                new IntVec2(10, 10), new IntVec2(10, 10), IntVec3.Zero, out var layout, out _));
            Assert.False(layout.ContainsFootprint(new CellRect(4, 4, 2, 1)));
        }
    }
}
