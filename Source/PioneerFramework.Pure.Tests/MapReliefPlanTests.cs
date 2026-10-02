using System.Collections.Generic;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 地图浮雕的纯逻辑（分类网格 → 矩形盒体）：
    /// 钉住四件事——**不重叠且不漏格**（覆盖性）、**同类才合并**、**确定性**、**超限降采样只增不减**。
    /// 口径见 `MapReliefPlan`（用户 2026-10-02 裁定：结构梁 / 舱室 / 装甲 三档）。
    /// </summary>
    public class MapReliefPlanTests
    {
        private const byte None = MapReliefPlan.None;
        private const byte Beam = MapReliefPlan.Beam;
        private const byte Cabin = MapReliefPlan.Cabin;
        private const byte Armor = MapReliefPlan.Armor;

        /// <summary>网格 → 每格覆盖计数（用于"恰好一次"断言）。strict = 盒体只准覆盖同类格
        /// （1 格精度下的不变式；降采样后块内取最大分类，会把空格也覆盖进来，只能放宽）。</summary>
        private static int[] Coverage(byte[] cls, int w, int h, List<MapReliefPlan.Box> boxes, bool strict)
        {
            var cover = new int[w * h];
            foreach (var b in boxes)
            {
                Assert.True(b.x0 >= 0 && b.z0 >= 0 && b.x1 < w && b.z1 < h && b.x1 >= b.x0 && b.z1 >= b.z0,
                    $"盒体越界/退化: ({b.x0},{b.z0})-({b.x1},{b.z1}) cls={b.cls}");
                Assert.True(b.cls >= Beam && b.cls <= Armor, $"未知分类 {b.cls}");
                for (int z = b.z0; z <= b.z1; z++)
                    for (int x = b.x0; x <= b.x1; x++)
                    {
                        cover[z * w + x]++;
                        if (strict)
                            Assert.Equal(cls[z * w + x], b.cls);   // 盒体只覆盖同类格
                    }
            }
            return cover;
        }

        [Fact]
        public void Merge_CoversEveryBuiltCellExactlyOnce()
        {
            const int w = 37, h = 23;   // 非正方形 + 非整块，顺带钉边界
            var cls = new byte[w * h];
            var rng = new System.Random(20261002);
            for (int i = 0; i < cls.Length; i++)
            {
                int r = rng.Next(100);
                cls[i] = r < 45 ? None : r < 75 ? Beam : r < 90 ? Cabin : Armor;
            }

            var boxes = MapReliefPlan.Build(cls, w, h, maxBoxes: int.MaxValue);
            int[] cover = Coverage(cls, w, h, boxes, strict: true);

            for (int i = 0; i < cls.Length; i++)
            {
                if (cls[i] == None) Assert.Equal(0, cover[i]);      // 空格的覆盖必须是 0
                else Assert.Equal(1, cover[i]);                     // 有内容的格恰好被一个盒体覆盖
            }
        }

        [Fact]
        public void Merge_DoesNotMixClasses()
        {
            // 一行三格：结构梁 | 舱室 | 装甲 —— 必须三个盒体，绝不合并
            var cls = new byte[] { Beam, Cabin, Armor };
            var boxes = MapReliefPlan.Build(cls, 3, 1, maxBoxes: int.MaxValue);
            Assert.Equal(3, boxes.Count);
        }

        [Fact]
        public void Merge_SingleRow_ProducesOneBoxPerRun()
        {
            // 结构梁 3 格 + 空 1 格 + 结构梁 2 格 ⇒ 2 个盒体
            var cls = new byte[] { Beam, Beam, Beam, None, Beam, Beam };
            var boxes = MapReliefPlan.Build(cls, 6, 1, maxBoxes: int.MaxValue);
            Assert.Equal(2, boxes.Count);
            Assert.Contains(boxes, b => b.x0 == 0 && b.x1 == 2 && b.z0 == 0 && b.z1 == 0 && b.cls == Beam);
            Assert.Contains(boxes, b => b.x0 == 4 && b.x1 == 5 && b.z0 == 0 && b.z1 == 0 && b.cls == Beam);
        }

        [Fact]
        public void Merge_SolidRectangle_IsOneBox()
        {
            const int w = 12, h = 8;
            var cls = new byte[w * h];
            for (int i = 0; i < cls.Length; i++) cls[i] = Armor;
            var boxes = MapReliefPlan.Build(cls, w, h, maxBoxes: int.MaxValue);
            Assert.Single(boxes);
            Assert.Equal(w * h, (boxes[0].x1 - boxes[0].x0 + 1) * (boxes[0].z1 - boxes[0].z0 + 1));
        }

        [Fact]
        public void Coarsening_MeetsCapAndKeepsCoverage()
        {
            // 棋盘格：1 格精度下盒体数 = 非空格数（合并无从下手）⇒ 必须靠降采样压到上限内
            const int w = 64, h = 64;
            var cls = new byte[w * h];
            int built = 0;
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                    if (((x + z) & 1) == 0) { cls[z * w + x] = Cabin; built++; }

            const int cap = 50;
            var boxes = MapReliefPlan.Build(cls, w, h, maxBoxes: cap);
            Assert.True(boxes.Count <= cap, $"降采样后盒体数仍超上限：{boxes.Count} > {cap}");

            // 覆盖只增不减：所有原本有内容的格仍被覆盖（可能被更大的块覆盖多次）
            int[] cover = Coverage(cls, w, h, boxes, strict: false);
            for (int i = 0; i < cls.Length; i++)
                if (cls[i] != None)
                    Assert.True(cover[i] >= 1, $"降采样把有内容的格弄丢了：idx={i}");
        }

        [Fact]
        public void Build_IsDeterministic()
        {
            const int w = 21, h = 17;
            var cls = new byte[w * h];
            var rng = new System.Random(7);
            for (int i = 0; i < cls.Length; i++)
                cls[i] = (byte)(rng.Next(4));

            List<MapReliefPlan.Box> a = MapReliefPlan.Build(cls, w, h, maxBoxes: 40);
            List<MapReliefPlan.Box> b = MapReliefPlan.Build(cls, w, h, maxBoxes: 40);
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].x0, b[i].x0);
                Assert.Equal(a[i].z0, b[i].z0);
                Assert.Equal(a[i].x1, b[i].x1);
                Assert.Equal(a[i].z1, b[i].z1);
                Assert.Equal(a[i].cls, b[i].cls);
            }
        }

        [Fact]
        public void Build_EdgeCases_ReturnEmpty()
        {
            Assert.Empty(MapReliefPlan.Build(null!, 4, 4));
            Assert.Empty(MapReliefPlan.Build(new byte[0], 0, 0));
            Assert.Empty(MapReliefPlan.Build(new byte[16], 4, 4));          // 全 0
            Assert.Empty(MapReliefPlan.Build(new byte[3], 8, 8));           // 长度不足 ⇒ 拒绝而不是越界
        }

        [Fact]
        public void Build_OneByOne()
        {
            var boxes = MapReliefPlan.Build(new byte[] { Beam }, 1, 1, maxBoxes: int.MaxValue);
            Assert.Single(boxes);
            Assert.Equal(0, boxes[0].x0);
            Assert.Equal(0, boxes[0].x1);
            Assert.Equal(Beam, boxes[0].cls);
        }

        // ════════════════════════════════════════════════════
        //  墙归房（PropagateRoomClassToStructure）——用户口径：
        //  "参与形成房间的墙体也计入房间渲染模式"
        // ════════════════════════════════════════════════════

        /// <summary>造一块网格：中心 3×3 装甲房间 + 一圈"墙"（solid=1），返回 (classes, solid)。</summary>
        private static (byte[] cls, byte[] solid, int w, int h) RoomWithWallRing(int wallThickness, byte roomCls = Armor)
        {
            const int w = 11, h = 11;
            var cls = new byte[w * h];
            var solid = new byte[w * h];
            for (int z = 4; z <= 6; z++)
                for (int x = 4; x <= 6; x++)
                    cls[z * w + x] = roomCls;             // 房间（3×3）
            for (int z = 4 - wallThickness; z <= 6 + wallThickness; z++)
                for (int x = 4 - wallThickness; x <= 6 + wallThickness; x++)
                {
                    bool inRoom = z >= 4 && z <= 6 && x >= 4 && x <= 6;
                    if (inRoom) continue;
                    cls[z * w + x] = Beam;                // 墙：先记成结构梁
                    solid[z * w + x] = 1;                 // 不可通行
                }
            return (cls, solid, w, h);
        }

        [Fact]
        public void Walls_SingleThick_BelongToRoom()
        {
            var (cls, solid, w, h) = RoomWithWallRing(1);
            MapReliefPlan.PropagateRoomClassToStructure(cls, solid, w, h);

            for (int z = 3; z <= 7; z++)
                for (int x = 3; x <= 7; x++)
                    Assert.Equal(Armor, cls[z * w + x]);          // 房间 + 一圈墙 = 全装甲
            Assert.Equal(None, cls[0]);                            // 远处不动
        }

        [Fact]
        public void Walls_TwoThick_AlsoBelongToRoom()
        {
            var (cls, solid, w, h) = RoomWithWallRing(2);
            MapReliefPlan.PropagateRoomClassToStructure(cls, solid, w, h);

            // 2 格厚的舱壁：外层也归房（这正是"参与形成房间的墙体"要覆盖的情形）
            for (int z = 2; z <= 8; z++)
                for (int x = 2; x <= 8; x++)
                    Assert.Equal(Armor, cls[z * w + x]);
            Assert.Equal(None, cls[0]);
        }

        [Fact]
        public void Walls_DoNotSpreadOntoPassableDeck()
        {
            // 房间 | 2 格厚墙 | 甲板（可通行地板，solid=0，结构梁）
            // 甲板段必须**保持结构梁**——"归房"只能沿不可通行格走，不能顺甲板蔓延。
            const int w = 9, h = 1;
            var cls = new byte[w * h];
            var solid = new byte[w * h];
            cls[0] = Cabin;                                        // 房间
            cls[1] = Beam; solid[1] = 1;                           // 墙
            cls[2] = Beam; solid[2] = 1;                           // 墙（2 格厚）
            cls[3] = Beam; solid[3] = 0;                           // 甲板（可通行）
            cls[4] = Beam; solid[4] = 0;                           // 甲板

            MapReliefPlan.PropagateRoomClassToStructure(cls, solid, w, h);

            Assert.Equal(Cabin, cls[1]);
            Assert.Equal(Cabin, cls[2]);
            Assert.Equal(Beam, cls[3]);                            // 甲板未被带跑
            Assert.Equal(Beam, cls[4]);
        }

        [Fact]
        public void Walls_SharedBetweenRooms_TakeNearestRoomAndHigherOnTie()
        {
            // 装甲房 | 一道 3 格厚的墙 | 舱室房。
            // 语义：每个墙格归**最近**的房间；到两边等距（正中间那格）取高档。
            const int w = 5, h = 1;
            var cls = new byte[] { Armor, Beam, Beam, Beam, Cabin };
            var solid = new byte[] { 0, 1, 1, 1, 0 };
            MapReliefPlan.PropagateRoomClassToStructure(cls, solid, w, h);

            Assert.Equal(Armor, cls[0]);   // 装甲房：不变
            Assert.Equal(Armor, cls[1]);   // 贴着装甲房 ⇒ 装甲
            Assert.Equal(Armor, cls[2]);   // 正中间：两房等距 ⇒ 取高档
            Assert.Equal(Cabin, cls[3]);   // 贴着舱室房 ⇒ 舱室
            Assert.Equal(Cabin, cls[4]);   // 舱室房本身：不变
        }

        [Fact]
        public void Walls_PropagationIsMirrorInvariant()
        {
            // 与扫描方向无关：把网格水平镜像后跑，结果也应镜像一致
            var (clsA, solidA, w, h) = RoomWithWallRing(2);
            var clsB = new byte[w * h];
            var solidB = new byte[w * h];
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    clsB[z * w + (w - 1 - x)] = clsA[z * w + x];
                    solidB[z * w + (w - 1 - x)] = solidA[z * w + x];
                }

            MapReliefPlan.PropagateRoomClassToStructure(clsA, solidA, w, h);
            MapReliefPlan.PropagateRoomClassToStructure(clsB, solidB, w, h);

            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                    Assert.Equal(clsA[z * w + x], clsB[z * w + (w - 1 - x)]);
        }

        [Fact]
        public void Walls_EdgeCases_NoThrow()
        {
            MapReliefPlan.PropagateRoomClassToStructure(null!, new byte[4], 2, 2);
            MapReliefPlan.PropagateRoomClassToStructure(new byte[4], null!, 2, 2);
            MapReliefPlan.PropagateRoomClassToStructure(new byte[3], new byte[3], 2, 2);   // 长度不足 ⇒ 直接返回
        }
    }
}
