using System.Collections.Generic;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>按级号切组（2026-10-27）：抛掉最大 stageId 整级，父组留 0..max−1。</summary>
    public class StagePartitionTests
    {
        private static SpacePart P(int id, int stage) => new() { partId = id, defName = "p", stageId = stage };

        [Fact]
        public void MaxStage_AndSplit_TwoStageRocket()
        {
            var parts = new List<SpacePart> { P(1, 0), P(2, 0), P(3, 1), P(4, 1) };
            Assert.Equal(1, StagePartition.MaxStage(parts));

            StagePartition.Split(parts, 1, out var kept, out var jettisoned);

            Assert.Equal(2, kept.Count);
            Assert.Equal(2, jettisoned.Count);
            Assert.All(kept, p => Assert.Equal(0, p.stageId));
            Assert.All(jettisoned, p => Assert.Equal(1, p.stageId));
        }

        [Fact]
        public void SingleStage_HasNothingToJettison()
        {
            var parts = new List<SpacePart> { P(1, 0), P(2, 0) };
            Assert.Equal(0, StagePartition.MaxStage(parts));

            StagePartition.Split(parts, 0, out var kept, out var jettisoned);
            Assert.Equal(2, kept.Count);
            Assert.Empty(jettisoned);
        }

        [Fact]
        public void ThreeStages_JettisonsOnlyTheHighest()
        {
            var parts = new List<SpacePart> { P(1, 0), P(2, 1), P(3, 2) };
            StagePartition.Split(parts, 2, out var kept, out var jettisoned);

            Assert.Equal(2, kept.Count);
            Assert.Single(jettisoned);
            Assert.Equal(2, jettisoned[0].stageId);
        }
    }
}
