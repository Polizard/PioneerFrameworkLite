using System.Collections.Generic;
using PioneerFramework.Combat;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **多 part 命中链（批 3 第一刀）**：实体 AABB 内逐 part slab 取最近命中件。
    /// 判据：最近的件赢、第二件可被命中、零件缝里穿过不算命中。
    /// **2026-10-30 用户裁定：射弹跳过残骸**（已毁件不参与选件；整船残骸 ⇒ 不命中、弹丸不被吃）。
    /// </summary>
    public class PartHitSelectionTests
    {
        private static SpaceEntity EntityWithParts(params SpacePart[] parts)
        {
            var e = new SpaceEntity(1, 0);
            e.SetParts(new List<SpacePart>(parts));
            return e;
        }

        private static SpacePart Box(int id, double y, double size = 10.0) => new()
        {
            partId = id,
            defName = "PF_ShipPart_Cube",
            positionShip = new DVector3(0, y, 0),
            boundsSizeShip = new DVector3(size, size, size),
            hp = 100f,
        };

        [Fact]
        public void PicksNearestPart_AlongRay()
        {
            var e = EntityWithParts(Box(1, 0), Box(2, 20));
            // 从 y=-20 向 +Y 扫 60 m：part1 的 [−5,5] 先到（t=0.25），part2 的 [15,25] 后到（t≈0.583）
            bool hit = CombatManager.TryPickHitPart(e, new DVector3(0, -20, 0), new DVector3(0, 1, 0), 60,
                out int idx, out var part, out double t);

            Assert.True(hit);
            Assert.Equal(0, idx);
            Assert.Equal(1, part.partId);
            Assert.Equal(15.0 / 60.0, t, 9);   // 起点到 part1 近面 15 m
        }

        [Fact]
        public void SecondPart_CanBeHit_WhenFirstIsNotOnRay()
        {
            // 射线从 y=0 向 +Y 扫 40 m：part1 在 y=-20（在射线起点后方，不命中），part2 在 y=20 ⇒ 命中第二件
            var e = EntityWithParts(Box(1, -20, size: 4), Box(2, 20, size: 4));
            bool hit = CombatManager.TryPickHitPart(e, new DVector3(0, 0, 0), new DVector3(0, 1, 0), 40,
                out int idx, out var part, out _);

            Assert.True(hit);
            Assert.Equal(1, idx);
            Assert.Equal(2, part.partId);
        }

        [Fact]
        public void GapBetweenParts_IsNotAHit()
        {
            // 两个小盒在 y=±20，中间 y∈[-18,18] 是缝；射线只扫 y∈[-10,10]
            var e = EntityWithParts(Box(1, -20, size: 4), Box(2, 20, size: 4));
            bool hit = CombatManager.TryPickHitPart(e, new DVector3(0, -10, 0), new DVector3(0, 1, 0), 20,
                out _, out _, out _);

            Assert.False(hit);
        }

        [Fact]
        public void DestroyedPart_IsSkipped_AlivePartBehindIsHit()
        {
            // 2026-10-30 用户裁定：射弹跳过残骸 ⇒ 前面的件已毁，命中的是后面还活着的那件。
            var first = Box(1, 0);
            first.isDestroyed = true;
            var e = EntityWithParts(first, Box(2, 20));
            bool hit = CombatManager.TryPickHitPart(e, new DVector3(0, -20, 0), new DVector3(0, 1, 0), 60,
                out int idx, out var part, out _);

            Assert.True(hit);
            Assert.Equal(1, idx);
            Assert.Equal(2, part.partId);
        }

        [Fact]
        public void AllPartsDestroyed_HulkDoesNotAbsorbShot()
        {
            // 整船都成残骸 ⇒ 不命中（弹丸穿过、不被吃）。这是"跳过残骸"的必然推论：
            // 打爆的船不再当盾牌。
            var a = Box(1, 0);
            var b = Box(2, 20);
            a.isDestroyed = true;
            b.isDestroyed = true;
            var e = EntityWithParts(a, b);

            bool hit = CombatManager.TryPickHitPart(e, new DVector3(0, -20, 0), new DVector3(0, 1, 0), 60,
                out _, out _, out _);
            Assert.False(hit);
        }
    }
}
