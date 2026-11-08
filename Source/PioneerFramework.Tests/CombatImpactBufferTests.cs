using PioneerFramework.Combat;
using Xunit;

namespace PioneerFramework.Tests
{
    public class CombatImpactBufferTests
    {
        [Fact]
        public void ReadersAdvanceIndependentlyAcrossWraparoundAndShortBuffers()
        {
            var buffer = new CombatImpactBuffer(3);
            for (int tick = 1; tick <= 5; tick++) buffer.Append(default(CombatImpactEvent) with { HostTick = tick });

            var first = new CombatImpactEvent[2];
            Assert.Equal(2, buffer.CopyAfter(0, first));
            Assert.Equal(3, first[0].Sequence);
            Assert.Equal(4, first[1].Sequence);
            Assert.Equal(1, buffer.CopyAfter(first[1].Sequence, first));
            Assert.Equal(5, first[0].Sequence);
            Assert.Equal(5, first[0].HostTick);
            Assert.Equal(5, buffer.LatestSequence);

            var second = new CombatImpactEvent[3];
            Assert.Equal(3, buffer.CopyAfter(0, second));
            Assert.Equal(3, second[0].Sequence);
            Assert.Equal(5, second[2].Sequence);
            Assert.Equal(0, buffer.CopyAfter(buffer.LatestSequence, second));
        }
    }
}
