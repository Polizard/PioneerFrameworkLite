using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **出生种类策略表的判据矩阵**（2026-10-31 后端裁定，见 `DesignNotes/交接-20261031-出生协议定案.md`）。
    ///
    /// 钉住三件事（它们是"正确性 + 统一性"的最小骨架）：
    /// 1. 只有 `Instantiated` 不需要母体——切分与发射都必须能指出状态出处；
    /// 2. 只有 `Emitted` 需要出生对账——**抛级/载荷不得被拖进"等一拍"**（这是本文件最主要的一条）；
    /// 3. 未定义的枚举值一律非法——不做"默认当新建"的兜底（兜底会把编程错误变成静默行为）。
    /// </summary>
    public class GroupBirthPolicyTests
    {
        [Fact]
        public void AllThreeKindsAreDefined()
        {
            Assert.True(GroupBirthPolicy.IsDefined(GroupBirthKind.Instantiated));
            Assert.True(GroupBirthPolicy.IsDefined(GroupBirthKind.Continuation));
            Assert.True(GroupBirthPolicy.IsDefined(GroupBirthKind.Emitted));
        }

        [Theory]
        [InlineData(GroupBirthKind.Instantiated, false)]
        [InlineData(GroupBirthKind.Continuation, true)]
        [InlineData(GroupBirthKind.Emitted, true)]
        public void OnlyInstantiatedNeedsNoSource(GroupBirthKind kind, bool expected)
            => Assert.Equal(expected, GroupBirthPolicy.RequiresSource(kind));

        [Theory]
        [InlineData(GroupBirthKind.Instantiated, false)]
        [InlineData(GroupBirthKind.Continuation, false)]
        [InlineData(GroupBirthKind.Emitted, true)]
        public void OnlyEmittedNeedsReconciliation(GroupBirthKind kind, bool expected)
            => Assert.Equal(expected, GroupBirthPolicy.RequiresReconciliation(kind));

        [Theory]
        [InlineData(GroupBirthKind.Instantiated, true)]
        [InlineData(GroupBirthKind.Continuation, true)]
        [InlineData(GroupBirthKind.Emitted, false)]
        public void StageSplitAndPayloadEnterWorldOnBirthFrame(GroupBirthKind kind, bool expected)
            => Assert.Equal(expected, GroupBirthPolicy.EntersWorldAtBirth(kind));

        [Theory]
        [InlineData(int.MinValue)]
        [InlineData(-1)]
        [InlineData(3)]
        [InlineData(int.MaxValue)]
        public void UndefinedKindIsRejectedAndNeverEntersWorld(int raw)
        {
            var kind = (GroupBirthKind)raw;
            Assert.False(GroupBirthPolicy.IsDefined(kind));
            Assert.False(GroupBirthPolicy.EntersWorldAtBirth(kind));   // 未知值不得被当成"当帧可入世界"
            Assert.Contains("Unknown", GroupBirthPolicy.Describe(kind)); // 报错文本必须带上原始数值
        }

        [Fact]
        public void DescribeCoversEveryDefinedKindDistinctly()
        {
            var texts = new[]
            {
                GroupBirthPolicy.Describe(GroupBirthKind.Instantiated),
                GroupBirthPolicy.Describe(GroupBirthKind.Continuation),
                GroupBirthPolicy.Describe(GroupBirthKind.Emitted),
            };
            Assert.Equal(3, System.Linq.Enumerable.Distinct(texts).Count());
            Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t)));
        }
    }
}
