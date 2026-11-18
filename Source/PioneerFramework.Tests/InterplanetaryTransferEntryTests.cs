using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 行星际粗转移**入口**的前置闸门测试（2026-11-18，授权件 `后端变更授权-20261118-行星际粗转移`）。
    ///
    /// headless 世界壳没有真实 `CelestialRuntime`，所以这里钉的是"拒绝路径"：
    /// 空群组、已有转移、天体框架未就绪都必须**明确拒绝并给中文原因**，且不改任何状态。
    /// 成功路径（真实星历 + 进 SOI 捕获）在集成测试里做。
    /// </summary>
    public class InterplanetaryTransferEntryTests
    {
        [Fact]
        public void RejectsNullGroup()
        {
            using var host = new SpaceAiRuntimeTestHost();
            Assert.False(host.Space.TryBeginInterplanetaryTransfer(null!, 0, out string reason));
            Assert.Equal("群组不存在", reason);
        }

        [Fact]
        public void RejectsGroupThatIsAlreadyTransferring()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var group = host.AddShip();
            group.interplanetaryTargetBodyId = 42;
            group.interplanetaryPhase = SpaceGameComp.InterplanetaryPhaseOutbound;

            Assert.False(host.Space.TryBeginInterplanetaryTransfer(group, 42, out string reason));
            Assert.Equal("已有进行中的行星际转移", reason);
            Assert.Equal(42, group.interplanetaryTargetBodyId);   // 拒绝时不许改状态
        }

        [Fact]
        public void RejectsWhenCelestialRuntimeIsNotReady()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var group = host.AddShip();

            Assert.False(host.Space.TryBeginInterplanetaryTransfer(group, 42, out string reason));
            Assert.Equal("天体框架未就绪", reason);
            Assert.Equal(-1, group.interplanetaryTargetBodyId);
            Assert.Equal(SpaceGameComp.InterplanetaryPhaseNone, group.interplanetaryPhase);
        }
    }
}
