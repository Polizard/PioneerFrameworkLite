using System.Linq;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 星座纯核心单测（ConstellationCore / ConstellationIndex，2026-09-10 随组件首版落地）。
    /// 覆盖：入编判定优先级（星座不存在 → 群组不存在 → 已入编 → 资格不符）、准入真值表、
    /// 锚点世界位置（天体世界位置 + R·dir）、多归属反查索引的增删对称与空集清理。
    /// 组件的编排部分（读 groups / 存档 / 事件收尸）无法脱离 RimWorld 运行，归游戏内自检场景。
    /// </summary>
    public class ConstellationCoreTests
    {
        // ── 准入：圆轨 或 锚点 ──────────────────────────────────

        [Theory]
        [InlineData(true, false, true)]   // 圆轨卫星：准入
        [InlineData(false, true, true)]   // 地表锚点：准入
        [InlineData(true, true, true)]    // 两者皆是（异常态但无害）：准入
        [InlineData(false, false, false)] // 深空自由飞行体 / 导弹 / 机动中飞船：拒绝
        public void IsAdmissible_TruthTable(bool isCircularOrbit, bool isSurfaceAnchor, bool expected)
        {
            Assert.Equal(expected, ConstellationCore.IsAdmissible(isCircularOrbit, isSurfaceAnchor));
        }

        // ── 入编判定：结果码 + 判定优先级 ──────────────────────────

        [Fact]
        public void CheckAdd_Ok_WhenCircularOrbit()
        {
            var r = ConstellationCore.CheckAdd(constellationExists: true, groupExists: true,
                isCircularOrbit: true, isSurfaceAnchor: false, alreadyMember: false);
            Assert.Equal(ConstellationAddResult.Ok, r);
        }

        [Fact]
        public void CheckAdd_Ok_WhenSurfaceAnchor()
        {
            var r = ConstellationCore.CheckAdd(true, true, isCircularOrbit: false, isSurfaceAnchor: true, alreadyMember: false);
            Assert.Equal(ConstellationAddResult.Ok, r);
        }

        [Fact]
        public void CheckAdd_NotAdmissible_WhenNeitherOrbitNorAnchor()
        {
            var r = ConstellationCore.CheckAdd(true, true, false, false, false);
            Assert.Equal(ConstellationAddResult.NotAdmissible, r);
        }

        [Fact]
        public void CheckAdd_AlreadyMember_WhenDuplicateInsideSameConstellation()
        {
            var r = ConstellationCore.CheckAdd(true, true, true, false, alreadyMember: true);
            Assert.Equal(ConstellationAddResult.AlreadyMember, r);
        }

        [Fact]
        public void CheckAdd_GroupMissing_WhenGroupIdNotRegistered()
        {
            var r = ConstellationCore.CheckAdd(true, groupExists: false, isCircularOrbit: true, isSurfaceAnchor: true, alreadyMember: false);
            Assert.Equal(ConstellationAddResult.GroupMissing, r);
        }

        [Fact]
        public void CheckAdd_ConstellationNotFound_WhenIdUnknown()
        {
            var r = ConstellationCore.CheckAdd(constellationExists: false, groupExists: false,
                isCircularOrbit: false, isSurfaceAnchor: false, alreadyMember: true);
            Assert.Equal(ConstellationAddResult.ConstellationNotFound, r);
        }

        [Fact]
        public void CheckAdd_ReasonOrder_IsConstellationThenGroupThenDuplicateThenAdmissibility()
        {
            // 判定优先级 = 枚举顺序：给定全部故障，应报最靠前的一条（部署链据此定位问题）
            Assert.Equal(ConstellationAddResult.ConstellationNotFound,
                ConstellationCore.CheckAdd(false, false, false, false, true));
            Assert.Equal(ConstellationAddResult.GroupMissing,
                ConstellationCore.CheckAdd(true, false, false, false, true));
            Assert.Equal(ConstellationAddResult.AlreadyMember,
                ConstellationCore.CheckAdd(true, true, false, false, true));
            Assert.Equal(ConstellationAddResult.NotAdmissible,
                ConstellationCore.CheckAdd(true, true, false, false, false));
        }

        // ── 锚点位置：天体在群组系中的位置 + R_body × dir(tile) ──────

        [Fact]
        public void AnchorWorldPosition_PlacesPointOnSurfaceAlongTileDirection()
        {
            var p = ConstellationCore.AnchorWorldPosition(
                bodyFrameOffset: DVector3.zero,
                tileCenterDir: DVector3.up,
                bodyRadiusMeters: 5_000_000.0);

            Assert.Equal(0.0, p.x, 6);
            Assert.Equal(5_000_000.0, p.y, 6);
            Assert.Equal(0.0, p.z, 6);
        }

        [Fact]
        public void AnchorWorldPosition_NormalizesNonUnitTileDirection()
        {
            // GetTileCenter 返回渲染网格顶点（半径≠1）→ 必须归一化后再乘物理半径，否则锚点浮在地表外
            var p = ConstellationCore.AnchorWorldPosition(DVector3.zero, new DVector3(30, 40, 0), 1000.0);

            Assert.Equal(600.0, p.x, 6);
            Assert.Equal(800.0, p.y, 6);
            Assert.Equal(1000.0, p.magnitude, 6);
        }

        [Fact]
        public void AnchorWorldPosition_OffsetByBodyFrameOffset()
        {
            // 月球据点：锚点 = 月球**在群组系中**的位置（绕地 80,000 km，PFL 口径）+ R_body × 固连方向。
            // 群组系 = 根行星（地球）为原点——用绝对位置（GetBodyWorldPosition）会把地球公转偏移串进来。
            var moonFrameOffset = new DVector3(80_000_000.0, 0.0, 0.0);
            var p = ConstellationCore.AnchorWorldPosition(moonFrameOffset, DVector3.up, 1_500_000.0);

            Assert.Equal(80_000_000.0, p.x, 3);
            Assert.Equal(1_500_000.0, p.y, 3);
        }

        [Fact]
        public void AnchorWorldPosition_DegenerateDirection_FallsBackToBodyCenter()
        {
            // 退化输入（未初始化 tile / 网格缺失）不得产出 NaN
            var bodyOffset = new DVector3(100.0, 200.0, 300.0);
            var p = ConstellationCore.AnchorWorldPosition(bodyOffset, DVector3.zero, 5_000_000.0);

            Assert.Equal(100.0, p.x, 9);
            Assert.Equal(200.0, p.y, 9);
            Assert.Equal(300.0, p.z, 9);
        }

        // ── 多归属反查索引 ───────────────────────────────────────

        [Fact]
        public void Index_Add_IsIdempotent()
        {
            var index = new ConstellationIndex();
            index.Add(7, 1);
            index.Add(7, 1);
            index.Add(7, 1);

            Assert.Single(index.Of(7));
            Assert.Equal(1, index.GroupCount);
        }

        [Fact]
        public void Index_Of_ReturnsEveryConstellationOfGroup_MultiHome()
        {
            var index = new ConstellationIndex();
            index.Add(7, 1);
            index.Add(7, 2);
            index.Add(9, 2);

            var homes = index.Of(7).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { 1, 2 }, homes);
            Assert.Equal(new[] { 2 }, index.Of(9).ToArray());
            Assert.Equal(2, index.GroupCount);
        }

        [Fact]
        public void Index_Remove_KeepsOtherHomes()
        {
            var index = new ConstellationIndex();
            index.Add(7, 1);
            index.Add(7, 2);

            index.Remove(7, 1);

            Assert.Equal(new[] { 2 }, index.Of(7).ToArray());
            Assert.False(index.IsUnreferenced(7));
            Assert.Equal(1, index.GroupCount);
        }

        [Fact]
        public void Index_Remove_LastHome_DropsEntryAndBecomesUnreferenced()
        {
            var index = new ConstellationIndex();
            index.Add(7, 1);
            index.Add(7, 2);

            index.Remove(7, 1);
            index.Remove(7, 2);

            // 空集必须整条移除：残留空集会让群组销毁收尸误以为"仍有归属"（锚点永不释放）
            Assert.True(index.IsUnreferenced(7));
            Assert.Empty(index.Of(7));
            Assert.Equal(0, index.GroupCount);
        }

        [Fact]
        public void Index_Remove_UnknownPair_IsNoOp()
        {
            var index = new ConstellationIndex();
            index.Add(7, 1);

            index.Remove(7, 2);   // 未登记的归属
            index.Remove(8, 1);   // 未知群组

            Assert.Equal(new[] { 1 }, index.Of(7).ToArray());
            Assert.Equal(1, index.GroupCount);
        }

        /// <summary>
        /// C4：**Remove 之后再 Add 同一对**的往返一致性（原缺）。
        /// 现有用例都止步于 Remove（或连 Remove 到底），没有"移除后重新加入"这条路径——
        /// 而它在真实玩法里会发生（成员先退编再入编、锚点回收后重建）。
        /// </summary>
        [Fact]
        public void Index_RemoveThenReAdd_IsConsistent()
        {
            var index = new ConstellationIndex();
            index.Add(7, 1);
            index.Add(7, 2);

            index.Remove(7, 1);
            Assert.Equal(new[] { 2 }, index.Of(7).ToArray());

            index.Add(7, 1);          // 重新加入刚移除的那一对
            Assert.Equal(new[] { 1, 2 }, index.Of(7).OrderBy(x => x).ToArray());
            Assert.False(index.IsUnreferenced(7));
            Assert.Equal(1, index.GroupCount);   // 仍是同一个群组条目，不得变成两条

            // 反复往返不得泄漏条目
            for (int i = 0; i < 8; i++) { index.Remove(7, 1); index.Add(7, 1); }
            Assert.Equal(1, index.GroupCount);
            Assert.Equal(new[] { 1, 2 }, index.Of(7).OrderBy(x => x).ToArray());
        }

        /// <summary>
        /// C4：**大 N 索引回归**（原缺；设计文档称"星座规模小（典型 &lt;50 成员）"，但没有用例守住规模）。
        /// 一个群组归 N 个星座、以及 N 个群组归 1 个星座，两个方向都要保持 O(1) 语义下的正确计数。
        /// </summary>
        [Fact]
        public void Index_LargeN_StaysConsistent()
        {
            const int n = 100;
            var index = new ConstellationIndex();

            for (int c = 1; c <= n; c++) index.Add(7, c);          // 1 组归 n 个星座
            Assert.Equal(n, index.Of(7).Count);
            Assert.Equal(1, index.GroupCount);

            for (int g = 1; g <= n; g++) index.Add(g, 500);        // n 个组归 1 个星座
            // GroupCount 数的是**去重后的群组数**：group 7 上面已在索引里，故这里是 n 而不是 n+1
            Assert.Equal(n, index.GroupCount);
            Assert.Equal(n + 1, index.Of(7).Count);                // 而 group 7 现在有 n+1 个归属（1..n 加上 500）

            // 逐个退编（1..n）：group 7 还剩归属 500，故**不应**被判为无归属、条目也不得消失
            for (int c = 1; c <= n; c++) index.Remove(7, c);
            Assert.False(index.IsUnreferenced(7));
            Assert.Equal(new[] { 500 }, index.Of(7).ToArray());
            Assert.Equal(n, index.GroupCount);

            // 再退掉最后一个归属 → 空集必须整条移除（残留空集会让收尸误判"仍有归属"）
            index.Remove(7, 500);
            Assert.True(index.IsUnreferenced(7));
            Assert.Empty(index.Of(7));
            Assert.Equal(n - 1, index.GroupCount);
        }

        /// <summary>
        /// C4：`AnchorWorldPosition` 的 `radius = 0` 退化（原缺；L64 的 `sqrMagnitude &lt; 1e-18`
        /// 只处理"方向退化"，半径为零走的是正常分支，必须有断言钉住它退化为纯偏移）。
        /// </summary>
        [Fact]
        public void AnchorWorldPosition_ZeroRadius_ReturnsBodyFrameOffset()
        {
            var offset = new DVector3(1200.0, -300.0, 45.0);
            var dir = new DVector3(0.0, 1.0, 0.0);

            var p = ConstellationCore.AnchorWorldPosition(offset, dir, 0.0);

            Assert.Equal(offset.x, p.x, 9);
            Assert.Equal(offset.y, p.y, 9);
            Assert.Equal(offset.z, p.z, 9);

            // 方向为零向量（退化）且半径为零：两者同时退化也必须返回偏移本身，不得产生 NaN
            var degenerate = ConstellationCore.AnchorWorldPosition(offset, DVector3.zero, 0.0);
            Assert.Equal(offset.x, degenerate.x, 9);
            Assert.False(double.IsNaN(degenerate.x) || double.IsNaN(degenerate.y) || double.IsNaN(degenerate.z));
        }

        [Fact]
        public void Index_Of_UnknownGroup_ReturnsEmptyNonNullCollection()
        {
            var index = new ConstellationIndex();
            var cids = index.Of(12345);

            Assert.NotNull(cids);
            Assert.Empty(cids);
            Assert.True(index.IsUnreferenced(12345));
        }

        [Fact]
        public void Index_Clear_DropsEverything()
        {
            var index = new ConstellationIndex();
            index.Add(7, 1);
            index.Add(9, 2);

            index.Clear();

            Assert.Equal(0, index.GroupCount);
            Assert.True(index.IsUnreferenced(7));
        }
    }
}
