using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 认知对象与融合单测（ContactCore：Contact / ContactFusion）。
    /// 覆盖：身份字段互斥不变式、保真度的两个自变量（来源等级 / 不确定度）、
    /// 替换的防抖与严格性、过期判定、双向索引的增删对称与空集清理。
    ///
    /// 不能覆盖的部分归游戏内自检场景：免疫的**局部性**（S9：观测者级结算而非阵营级）——
    /// 那需要真实的观测者位置与探测结果，无法用纯函数证明。
    ///
    /// ⚠ [D13] 2026-09-16：`VisibleIndex` 已随"取消反向索引"一并删除，其 6 条用例随之移除
    /// （反向索引的三个用途全部有更便宜的正向做法，见实施计划 §1.8 [E]）。
    /// </summary>
    public class ContactCoreTests
    {
        // ── 身份字段互斥（[N1]/[D5] 的核心不变式）───────────────────────────

        [Fact]
        public void Contact_IdentityFieldsAreMutuallyExclusive()
        {
            var real = new Contact { targetGroupId = 42, decoyId = -1 };
            var forged = new Contact { targetGroupId = -1, decoyId = 7 };

            Assert.True(real.IsConsistent);
            Assert.True(forged.IsConsistent);
            Assert.False(real.IsForged);
            Assert.True(forged.IsForged);
        }

        [Theory]
        [InlineData(-1, -1)]   // 两个都没有：无身份的接触（非法）
        [InlineData(3, 9)]     // 两个都有：既真又假（非法）
        public void Contact_InvalidIdentity_IsRejectedByInvariant(int gid, int decoyId)
        {
            var bad = new Contact { targetGroupId = gid, decoyId = decoyId };
            Assert.False(bad.IsConsistent, "真/假身份必须恰好有一个，缺一或多一都是构造错误");
        }

        // ── 保真度：两个自变量各自单调 ─────────────────────────────────────

        [Fact]
        public void Fidelity_LowerSigmaIsMoreTrue_ForSameSource()
        {
            var tight = new Contact { source = ContactSource.OwnSensor, positionSigma = 100.0 };
            var loose = new Contact { source = ContactSource.OwnSensor, positionSigma = 20000.0 };

            Assert.True(ContactFusion.Fidelity(tight) > ContactFusion.Fidelity(loose));
        }

        [Fact]
        public void Fidelity_SourceRankOrders_ForEqualSigma()
        {
            var own = new Contact { source = ContactSource.OwnSensor, positionSigma = 1000.0 };
            var shared = new Contact { source = ContactSource.FactionShare, positionSigma = 1000.0 };
            var es = new Contact { source = ContactSource.PassiveES, positionSigma = 1000.0 };
            var decoy = new Contact { source = ContactSource.Decoy, positionSigma = 1000.0 };

            Assert.True(ContactFusion.Fidelity(own) > ContactFusion.Fidelity(shared));
            Assert.True(ContactFusion.Fidelity(shared) > ContactFusion.Fidelity(es));
            Assert.True(ContactFusion.Fidelity(es) > ContactFusion.Fidelity(decoy));
        }

        /// <summary>公开自报与盟友共享并列（都是"非自舰测量"），靠 σ 分胜负——单点语义，登记在此。</summary>
        [Fact]
        public void Fidelity_PublicAndSharedTie_OnEqualSigma()
        {
            var pub = new Contact { source = ContactSource.Public, positionSigma = 500.0 };
            var shared = new Contact { source = ContactSource.FactionShare, positionSigma = 500.0 };
            Assert.Equal(ContactFusion.Fidelity(shared), ContactFusion.Fidelity(pub), 12);
        }

        // ── 融合替换：严格大于才换（防抖 + 防记忆退化为永久全知）────────────

        [Fact]
        public void ShouldReplace_IsStrict_SoEqualFidelityDoesNotPingPong()
        {
            var a = new Contact { source = ContactSource.FactionShare, positionSigma = 800.0 };
            var b = new Contact { source = ContactSource.FactionShare, positionSigma = 800.0 };

            Assert.False(ContactFusion.ShouldReplace(a, b));
            Assert.False(ContactFusion.ShouldReplace(b, a));
        }

        [Fact]
        public void ShouldReplace_TakesTheMoreTrue_AndKeepsTheBetter()
        {
            var good = new Contact { source = ContactSource.OwnSensor, positionSigma = 200.0 };
            var worse = new Contact { source = ContactSource.FactionShare, positionSigma = 5000.0 };
            var better = new Contact { source = ContactSource.OwnSensor, positionSigma = 50.0 };

            Assert.False(ContactFusion.ShouldReplace(good, worse), "坏报告不得覆盖好报告");
            Assert.True(ContactFusion.ShouldReplace(worse, good));
            Assert.True(ContactFusion.ShouldReplace(good, better));
        }

        // ── 过期：记忆的本质是信息缺失 ─────────────────────────────────────

        [Fact]
        public void Contact_StalesAfterMemoryWindow()
        {
            var c = new Contact { lastUpdateTick = 1000 };
            Assert.False(c.IsStaleAt(1000 + 599, 600));
            Assert.False(c.IsStaleAt(1000 + 600, 600));
            Assert.True(c.IsStaleAt(1000 + 601, 600));
        }

    }
}
