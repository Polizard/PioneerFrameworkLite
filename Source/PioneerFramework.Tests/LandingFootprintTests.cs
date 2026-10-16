using System.Linq;
using PioneerFramework.Landing;
using Verse;
using Xunit;
namespace PioneerFramework.Tests;
public class LandingFootprintTests
{
    [Fact]
    public void EvenFootprintUsesDefinedCenterOffset()
    {
        var shape = new LandingFootprint(new CellRect(10, 20, 4, 2).Cells);
        Assert.Equal(new IntVec3(19, 0, 30), shape.TargetOrigin(new IntVec3(20, 0, 30)));
    }
    [Fact]
    public void IrregularShapeKeepsHolesAndIgnoresInputOrder()
    {
        var cells = new[] { new IntVec3(12, 0, 20), new IntVec3(10, 0, 20), new IntVec3(10, 0, 22) };
        var first = new LandingFootprint(cells); var second = new LandingFootprint(cells.Reverse());
        Assert.Equal(first.TargetCells(IntVec3.Zero), second.TargetCells(IntVec3.Zero));
        Assert.Equal(3, first.TargetCells(IntVec3.Zero).Count);
        Assert.DoesNotContain(IntVec3.Zero, first.TargetCells(IntVec3.Zero));
    }
}
