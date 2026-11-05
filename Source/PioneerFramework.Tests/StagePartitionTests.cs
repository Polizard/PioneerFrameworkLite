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

        // ── 块号（2026-11-03）：同一 stage 内可有多块 ──────────────
        [Fact]
        public void BlocksOf_ReturnsSortedDistinct()
        {
            var parts = new List<SpacePart> { P(1, 1), P(2, 1), P(3, 0) };
            parts[0].blockId = 2;
            parts[1].blockId = 0;
            parts[2].blockId = 1;

            Assert.Equal(new[] { 0, 2 }, StagePartition.BlocksOf(parts, 1));
            Assert.Empty(StagePartition.BlocksOf(parts, 9));
        }

        [Fact]
        public void SplitBlock_IsolatesOnlyThatBlock()
        {
            var parts = new List<SpacePart> { P(1, 1), P(2, 1), P(3, 1), P(4, 0) };
            parts[0].blockId = 1;
            parts[1].blockId = 2;
            parts[2].blockId = 2;
            parts[3].blockId = 1;

            StagePartition.SplitBlock(parts, 1, 2, out var kept, out var jettisoned);

            Assert.Equal(2, jettisoned.Count);
            Assert.All(jettisoned, p => Assert.Equal(2, p.blockId));
            Assert.Equal(2, kept.Count);
            Assert.Contains(kept, p => p.stageId == 0 && p.blockId == 1);
            Assert.Contains(kept, p => p.stageId == 1 && p.blockId == 1);
        }

        // ── 级标签 / 父级规则（2026-11-03）────────────────────────
        [Fact]
        public void Disposition_And_AttachedChildStage_Rules()
        {
            var parts = new List<SpacePart> { P(1, 0), P(2, 1) };
            parts[1].stageDisposition = StageDisposition.Keep;

            Assert.Equal(StageDisposition.Jettison, StagePartition.DispositionOf(parts, 0));
            Assert.Equal(StageDisposition.Keep, StagePartition.DispositionOf(parts, 1));
            Assert.True(StagePartition.IsKeptStage(parts, 1));
            Assert.False(StagePartition.IsKeptStage(parts, 0));
            Assert.False(StagePartition.HasAttachedChildStage(parts, 1));

            parts[0].parentStageId = 1;   // 级 0 挂到级 1 上 ⇒ 级 1 有在场子级
            Assert.True(StagePartition.HasAttachedChildStage(parts, 1));
            Assert.False(StagePartition.HasAttachedChildStage(parts, 0));
        }
    }
}
