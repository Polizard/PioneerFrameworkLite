using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using PioneerFramework.Combat;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 船-船配对完备性（2026-10-19）：<see cref="CombatGrid.EnumerateShipPairs"/> 必须把
    /// 「本步扫掠路径经过同一格」的船对也枚举出来，而不只是「当前 AABB 共享格」。
    ///
    /// 缺口场景：A 从格 0 走到格 1、B 从格 1 走到格 0，一步相对位移 20 km（10 km/s × 2 s）。
    /// 两条路径在中途相撞，但步末两个 AABB 分处相邻格 ⇒ 只按当前覆盖格配对的实现会整对漏掉，
    /// 后续的球快筛/扫掠盒/TOI 都没有机会执行。
    /// </summary>
    public class CollisionPairEnumerationTests
    {
        private sealed class World : IDisposable
        {
            private readonly Game previous;
            public readonly Game Game;
            public readonly SpaceGameComp SpaceGame;
            public readonly CombatGrid Grid;

            public World()
            {
                previous = Current.Game;
                Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
                SpaceGame = new SpaceGameComp(Game);
                Grid = new CombatGrid(Game);
                Game.components = new List<GameComponent> { SpaceGame, Grid };
                Current.Game = Game;
            }

            public void Dispose() => Current.Game = previous;
        }

        private static SpaceEntityGroup Ship(int id, double x)
        {
            return new SpaceEntityGroup(id)
            {
                CurrentOrbit = new OrbitState
                {
                    positionSystem = (SystemVec3)new DVector3(x, 0, 0),
                    velocitySystem = (SystemVec3)DVector3.zero,
                    rotation = DQuaternion.identity
                }
            };
        }

        private static void MoveTo(SpaceEntityGroup group, double x)
        {
            group.CurrentOrbit.positionSystem = (SystemVec3)new DVector3(x, 0, 0);
        }

        private static List<(SpaceEntityGroup a, SpaceEntityGroup b)> Pairs(CombatGrid grid)
        {
            return grid.EnumerateShipPairs().ToList();
        }

        [Fact]
        public void AdjacentCell_SweptCrossing_IsPaired()
        {
            using var world = new World();

            // 20 km 格：格 0 = [0,20k)，格 1 = [20k,40k)。
            var a = Ship(1, 12_000);
            var b = Ship(2, 32_000);
            world.Grid.AddShip(a);
            world.Grid.AddShip(b);

            // 起点：两个船在相邻格，当前覆盖格不共享 ⇒ 不配对（正确）。
            Assert.Empty(Pairs(world.Grid));

            // 一步（dt = 2 s）各走 10 km，相对位移 20 km = 10 km/s：路径在中途相交，
            // 但步末 A 在格 1、B 在格 0，仍然分处相邻格。
            MoveTo(a, 22_000);
            MoveTo(b, 12_000);
            world.Grid.UpdateShipPosition(a, new DVector3(12_000, 0, 0));
            world.Grid.UpdateShipPosition(b, new DVector3(32_000, 0, 0));

            // 当前实现（只注册当前覆盖格）在这里返回 0 对 ⇒ 红。
            Assert.Single(Pairs(world.Grid));
        }

        [Fact]
        public void SettledStationaryShip_DropsDepartureCells()
        {
            using var world = new World();

            var a = Ship(1, 10_000);   // 格 0
            var b = Ship(2, 30_000);   // 格 1
            world.Grid.AddShip(a);
            world.Grid.AddShip(b);
            Assert.Empty(Pairs(world.Grid));

            // 一步从格 0 跳到格 2：本步扫掠覆盖 0..2，路径格 1 上的 B 必须进入候选。
            MoveTo(a, 50_000);
            world.Grid.UpdateShipPosition(a, new DVector3(10_000, 0, 0));
            Assert.Single(Pairs(world.Grid));

            // 下一步 A 原地不动：同步器调用 SettleSweptBox，离开的格 0/1 必须被移除。
            world.Grid.SettleSweptBox(a);
            Assert.Empty(Pairs(world.Grid));
        }

        [Fact]
        public void Teleport_DoesNotRegisterIntermediateCells()
        {
            using var world = new World();

            var a = Ship(1, 10_000);     // 格 0
            var mid = Ship(2, 50_000);   // 格 2
            world.Grid.AddShip(a);
            world.Grid.AddShip(mid);
            Assert.Empty(Pairs(world.Grid));

            // 一步跳 100,000 km（5000 格）：地图转移/调试传送，不是物理飞行路径。
            // 若把整条矩形路径注册进来，路径上的 mid 会被误配（且一次注册 5001 格）。
            MoveTo(a, 10_000 + 100_000_000);
            world.Grid.UpdateShipPosition(a, new DVector3(10_000, 0, 0));
            Assert.Empty(Pairs(world.Grid));
        }

        [Fact]
        public void SweptRegistration_DoesNotAccumulateOldCells()
        {
            using var world = new World();

            var a = Ship(1, 10_000);   // 格 0
            var observer = Ship(2, 10_000); // 格 0
            world.Grid.AddShip(a);
            world.Grid.AddShip(observer);

            // 第一步：格 0 → 格 2，本步扫掠覆盖 0..2；observer 仍在格 0 ⇒ 本步应配对。
            MoveTo(a, 50_000);
            world.Grid.UpdateShipPosition(a, new DVector3(10_000, 0, 0));
            Assert.Single(Pairs(world.Grid));

            // 第二步：格 2 → 格 4。新扫掠 = 旧当前格(2) ∪ 新当前格(4)，不得把上一步的 0/1 累积进来。
            MoveTo(a, 90_000);
            world.Grid.UpdateShipPosition(a, new DVector3(50_000, 0, 0));
            Assert.Empty(Pairs(world.Grid));
        }
    }
}
