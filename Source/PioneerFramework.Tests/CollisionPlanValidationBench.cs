using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using PioneerFramework;
using PioneerFramework.Combat;
using Verse;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 方案验证台架（2026-10-19）：只回答“这些优化方案到底行不行”，不改生产路径。
    /// · 相对几何缓存：逐字段逐位对拍 + 成本；
    /// · solver 预计算世界逆惯量：数值差异 + 成本；
    /// · 同格 256 艘：EnumerateShipPairs 的真实规模成本。
    /// </summary>
    public class CollisionPlanValidationBench
    {
        private readonly ITestOutputHelper _out;
        public CollisionPlanValidationBench(ITestOutputHelper output) => _out = output;

        // ── 测试用世界（与 CollisionPairEnumerationTests 同款）──
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

        // ═══════════════════════════════════════════════════════════
        //  1. 相对几何缓存：逐位等价 + 成本
        // ═══════════════════════════════════════════════════════════

        private struct CachedPart
        {
            public bool Identity;
            public DVector3 RelMin, RelMax;       // 实体系（identity 路径）
            public DVector3 CenterOffset;        // 实体系：Rotate(positionShip, entityRot)
            public DVector3 Half;
            public DVector3 ProjectedHalf;        // 已投影半尺寸（OBB 路径）
            public DQuaternion WorldRot;
            public int Order;
            public InertiaTensor.Shape Shape;
        }

        private sealed class PartGeomCache
        {
            public readonly List<CachedPart> Parts = new();
            public DVector3 EntityPosition;

            public PartGeomCache(SpaceEntity entity)
            {
                EntityPosition = entity.Position;
                DQuaternion entityRot = PartGeometry.EffectiveRotation(entity.Rotation);
                for (int i = 0; i < entity.Parts.Count; i++)
                {
                    var part = entity.Parts[i];
                    if (part == null || part.noCollide) continue;
                    if (PartGeometry.IsIdentityOrZero(part.rotationShip))
                    {
                        entity.GetPartAabbSystem(i, out DVector3 relMin, out DVector3 relMax);
                        Parts.Add(new CachedPart
                        {
                            Identity = true,
                            RelMin = relMin,
                            RelMax = relMax,
                            Order = i,
                            Shape = part.shape,
                        });
                    }
                    else
                    {
                        DVector3 centerOffset = DVector3.Rotate(part.positionShip, entityRot);
                        DVector3 half = part.boundsSizeShip * 0.5;
                        DQuaternion worldRot = entityRot * PartGeometry.EffectiveRotation(part.rotationShip);
                        Parts.Add(new CachedPart
                        {
                            Identity = false,
                            CenterOffset = centerOffset,
                            Half = half,
                            ProjectedHalf = PartGeometry.ProjectedHalfExtents(worldRot, half),
                            WorldRot = worldRot,
                            Order = i,
                            Shape = part.shape,
                        });
                    }
                }
            }

            public void Collect(DVector3 groupPos, List<CombatManager.PartAabb> into)
            {
                into.Clear();
                DVector3 entityPos = groupPos + EntityPosition;
                for (int i = 0; i < Parts.Count; i++)
                {
                    var p = Parts[i];
                    if (p.Identity)
                        into.Add(new CombatManager.PartAabb(entityPos + p.RelMin, entityPos + p.RelMax,
                            p.Order, p.Shape));
                    else
                    {
                        // 注意：生产 PartAabb 的 OBB 构造器会重算 ProjectedHalfExtents；
                        // 这里为了“同算式逐位”也走它。缓存 ProjectedHalf 供未来无重算构造器使用。
                        DVector3 center = entityPos + p.CenterOffset;
                        into.Add(new CombatManager.PartAabb(center, p.Half, p.WorldRot,
                            p.Order, p.Shape));
                    }
                }
            }
        }

        private static void CollectProduction(SpaceEntity entity, DVector3 groupPos,
            List<CombatManager.PartAabb> into)
        {
            into.Clear();
            DVector3 entityPos = groupPos + entity.Position;
            for (int i = 0; i < entity.Parts.Count; i++)
            {
                var part = entity.Parts[i];
                if (part == null || part.noCollide) continue;
                if (PartGeometry.IsIdentityOrZero(part.rotationShip))
                {
                    if (!entity.GetPartAabbSystem(i, out DVector3 relMin, out DVector3 relMax)) continue;
                    into.Add(new CombatManager.PartAabb(entityPos + relMin, entityPos + relMax, i, part.shape));
                }
                else
                {
                    DQuaternion entityRot = PartGeometry.EffectiveRotation(entity.Rotation);
                    DVector3 center = entityPos + DVector3.Rotate(part.positionShip, entityRot);
                    DQuaternion worldRot = entityRot * PartGeometry.EffectiveRotation(part.rotationShip);
                    into.Add(new CombatManager.PartAabb(center, part.boundsSizeShip * 0.5, worldRot, i, part.shape));
                }
            }
        }

        private static SpaceEntity Make256Ship(int rotatedEvery)
        {
            var entity = new SpaceEntity(1, 0);
            var parts = new List<SpacePart>(256);
            for (int i = 0; i < 256; i++)
            {
                var p = new SpacePart
                {
                    partId = i + 1,
                    defName = "PF_ShipPart_Cube",
                    positionShip = new DVector3((i % 8) * 4, (i / 8) * 4, 0),
                    boundsSizeShip = new DVector3(4, 4, 4),
                    hp = 100f,
                };
                if (rotatedEvery > 0 && i % rotatedEvery == 0)
                    p.rotationShip = DQuaternion.AngleAxis(0.31, new DVector3(0.2, 0.5, 0.84).normalized);
                if (i % 37 == 0) p.noCollide = true;   // 验证缓存过滤与生产一致
                parts.Add(p);
            }
            entity.SetParts(parts);
            entity.SetRotation(DQuaternion.AngleAxis(0.73, new DVector3(0.33, 0.57, 0.75).normalized));
            return entity;
        }

        [Fact]
        public void WarmupCollisionPaths_DoesNotThrow()
        {
            // 加载期预热是纯静态、无组/实体/地图状态；这里只做冒烟，确认分支都能执行。
            CombatManager.WarmupCollisionPaths();
        }

        [Fact]
        public void PartAabb_CachedProjectedHalf_IsBitwiseExact()
        {
            var rng = new Random(20261019);
            for (int i = 0; i < 1000; i++)
            {
                var half = new DVector3(
                    0.1 + rng.NextDouble() * 5,
                    0.1 + rng.NextDouble() * 5,
                    0.1 + rng.NextDouble() * 5);
                var center = new DVector3(
                    (rng.NextDouble() - 0.5) * 1e6,
                    (rng.NextDouble() - 0.5) * 1e6,
                    (rng.NextDouble() - 0.5) * 1e6);
                var rot = DQuaternion.AngleAxis(
                    rng.NextDouble() * Math.PI * 2,
                    new DVector3(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5).normalized);
                var rh = PartGeometry.ProjectedHalfExtents(rot, half);
                var a = new CombatManager.PartAabb(center, half, rot, i, InertiaTensor.Shape.Box);
                var b = new CombatManager.PartAabb(center, half, rot, rh, i, InertiaTensor.Shape.Box);
                Assert.True(a.Min == b.Min && a.Max == b.Max, $"trial {i} Min/Max 不一致");
                Assert.True(a.Center == b.Center && a.Half == b.Half, $"trial {i} Center/Half 不一致");
                Assert.True(a.UseObb && b.UseObb);
            }
        }

        private static List<CombatManager.PartAabb> FreshParts(SpaceEntityGroup group, SpaceGameComp sg, DVector3 pos)
        {
            var fresh = new List<CombatManager.PartAabb>();
            CombatManager.CollectPartAabbs(group, pos, sg, fresh);
            return fresh;
        }

        private static void AssertPartsExact(List<CombatManager.PartAabb> expected, List<CombatManager.PartAabb> actual)
        {
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                var a = expected[i];
                var b = actual[i];
                Assert.True(a.Min == b.Min && a.Max == b.Max, $"part {i} Min/Max 不一致");
                Assert.True(a.Center == b.Center && a.Half == b.Half, $"part {i} Center/Half 不一致");
                Assert.True(a.Order == b.Order && a.UseObb == b.UseObb && a.Shape == b.Shape,
                    $"part {i} Order/UseObb/Shape 不一致");
                Assert.True(a.Rotation == b.Rotation, $"part {i} Rotation 不一致");
            }
        }

        [Fact]
        public void CollectPartsAt_Cost_vs_FullHoisted()
        {
            using var world = new World();
            var group = new SpaceEntityGroup(103)
            {
                IsActive = true,
                IsCollidable = true,
                IsMovable = true,
                CurrentOrbit = new OrbitState
                {
                    positionSystem = (SystemVec3)new DVector3(1000, 0, 0),
                    velocitySystem = (SystemVec3)DVector3.zero,
                    rotation = DQuaternion.identity,
                },
            };
            world.SpaceGame.RegisterGroup(group);
            var entity = new SpaceEntity(103) { GroupId = 103 };
            var parts = new List<SpacePart>(256);
            for (int i = 0; i < 256; i++)
            {
                parts.Add(new SpacePart
                {
                    partId = i + 1,
                    positionShip = new DVector3((i % 8) * 4, (i / 8) * 4, 0),
                    boundsSizeShip = new DVector3(4, 4, 4),
                    hp = 100,
                });
            }
            entity.SetParts(parts);
            entity.SetRotation(DQuaternion.AngleAxis(0.3, new DVector3(0.1, 0.6, 0.79).normalized));
            world.SpaceGame.RegisterEntity(entity);

            var cache = new CombatManager.GroupCollisionCache { pos = group.CurrentOrbit.positionSystem.v };
            CombatManager.EnsurePartBoxes(cache, group, world.SpaceGame);
            var fresh = new List<CombatManager.PartAabb>(256);
            var translated = new List<CombatManager.PartAabb>(256);
            for (int i = 0; i < 5; i++)
            {
                var pos = new DVector3(1000 + i * 37.5, -20 + i, 3.25 * i);
                cache.CollectPartsAt(pos, translated);
                CombatManager.CollectPartAabbs(group, pos, world.SpaceGame, fresh);
                AssertPartsExact(fresh, translated);
            }
            for (int i = 0; i < 20; i++)
            {
                cache.CollectPartsAt(new DVector3(1000 + i * 0.01, 0, 0), fresh);
                CombatManager.CollectPartAabbs(group, new DVector3(1000 + i * 0.01, 0, 0), world.SpaceGame, fresh);
            }

            const int reps = 300;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < reps; i++)
                cache.CollectPartsAt(new DVector3(1000 + i * 0.01, 0, 0), fresh);
            sw.Stop();
            double translateUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

            sw.Restart();
            for (int i = 0; i < reps; i++)
                CombatManager.CollectPartAabbs(group, new DVector3(1000 + i * 0.01, 0, 0), world.SpaceGame, fresh);
            sw.Stop();
            double fullUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

            _out.WriteLine($"[方案验证] P1 平移 {translateUs:F1} µs/次 vs 完整 hoisted {fullUs:F1} µs/次");
        }

        [Fact]
        public void ClipCollect_Cost_LongStack256()
        {
            using var world = new World();
            var group = new SpaceEntityGroup(106)
            {
                IsActive = true, IsCollidable = true, IsMovable = true,
                CurrentOrbit = new OrbitState
                {
                    positionSystem = (SystemVec3)new DVector3(1000, 0, 0),
                    velocitySystem = (SystemVec3)DVector3.zero,
                    rotation = DQuaternion.identity,
                },
            };
            world.SpaceGame.RegisterGroup(group);
            var entity = new SpaceEntity(106) { GroupId = 106 };
            var parts = new List<SpacePart>(256);
            for (int i = 0; i < 256; i++)
            {
                parts.Add(new SpacePart
                {
                    partId = i + 1,
                    positionShip = new DVector3(0, i * 2.0, 0),
                    boundsSizeShip = new DVector3(2, 2, 2),
                    hp = 100,
                });
            }
            entity.SetParts(parts);
            entity.SetRotation(DQuaternion.AngleAxis(0.2, new DVector3(0.1, 0.6, 0.79).normalized));
            world.SpaceGame.RegisterEntity(entity);

            var full = new List<CombatManager.PartAabb>(256);
            var clip = new List<CombatManager.PartAabb>(32);
            var clipMin = new DVector3(998, -2, -2);
            var clipMax = new DVector3(1002, 22, 2);
            for (int i = 0; i < 20; i++)
            {
                CombatManager.CollectPartAabbs(group, group.CurrentOrbit.positionSystem.v, world.SpaceGame, full);
                CombatManager.CollectPartAabbsInClip(group, group.CurrentOrbit.positionSystem.v,
                    world.SpaceGame, clipMin, clipMax, clip);
            }

            const int reps = 300;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < reps; i++)
                CombatManager.CollectPartAabbs(group, group.CurrentOrbit.positionSystem.v, world.SpaceGame, full);
            sw.Stop();
            double fullUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

            sw.Restart();
            for (int i = 0; i < reps; i++)
                CombatManager.CollectPartAabbsInClip(group, group.CurrentOrbit.positionSystem.v,
                    world.SpaceGame, clipMin, clipMax, clip);
            sw.Stop();
            double clipUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

            _out.WriteLine($"[方案验证] 256 长条：完整收集 {fullUs:F1} µs/次｜clip 收集 {clipUs:F1} µs/次（候选 {clip.Count} 件）｜省 {(fullUs - clipUs) / fullUs * 100.0:F1}%");
        }

        [Fact]
        public void Sap_MatchesNested_OnRandomContainment()
        {
            var rng = new Random(20261020);
            var clipMin = new DVector3(-1000, -1000, -1000);
            var clipMax = new DVector3(1000, 1000, 1000);
            for (int trial = 0; trial < 20; trial++)
            {
                var a = new List<CombatManager.PartAabb>(64);
                var b = new List<CombatManager.PartAabb>(64);
                for (int i = 0; i < 64; i++)
                {
                    double len = 1 + rng.NextDouble() * 30;   // 长短混杂，制造包含关系
                    double cx = (i % 8) * 6 + rng.NextDouble() * 4;
                    double cy = ((i / 8) % 4) * 6;
                    double cz = (i / 32) * 4;
                    var center = new DVector3(cx, cy, cz);
                    var half = new DVector3(len, 2, 2);
                    if (i % 5 == 0)
                    {
                        var rot = DQuaternion.AngleAxis(0.1 + i * 0.03,
                            new DVector3(0.2, 0.5, 0.84).normalized);
                        a.Add(new CombatManager.PartAabb(center, half, rot, i));
                    }
                    else a.Add(new CombatManager.PartAabb(center - half, center + half, i));

                    double cxB = cx + (rng.NextDouble() - 0.5) * 5;
                    var centerB = new DVector3(cxB, cy + 0.5, cz);
                    if (i % 5 == 0)
                    {
                        var rot = DQuaternion.AngleAxis(0.2 + i * 0.02,
                            new DVector3(0.7, 0.2, 0.68).normalized);
                        b.Add(new CombatManager.PartAabb(centerB, half, rot, i));
                    }
                    else b.Add(new CombatManager.PartAabb(centerB - half, centerB + half, i));
                }

                bool okDefault = CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax,
                    out DVector3 nDef, out double dDef, out DVector3 cDef);
                bool okNested = CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax,
                    out DVector3 nNes, out double dNes, out DVector3 cNes, forceNested: true);
                bool okSweep = CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax,
                    out DVector3 nSw, out double dSw, out DVector3 cSw, forceSweep: true);
                Assert.Equal(okNested, okDefault);
                Assert.Equal(okNested, okSweep);
                if (okNested)
                {
                    Assert.True(nDef == nNes && dDef == dNes && cDef == cNes,
                        $"trial {trial} default vs nested 不一致");
                    Assert.True(nSw == nNes && dSw == dNes && cSw == cNes,
                        $"trial {trial} SAP vs nested 不一致");
                }

                var cd = new List<CollisionContactPoint>();
                var cn = new List<CollisionContactPoint>();
                CombatManager.CollectPartContacts(a, b, clipMin, clipMax, cd, 8);
                CombatManager.CollectPartContacts(a, b, clipMin, clipMax, cn, 8, null, null, forceNested: true);
                Assert.Equal(cn.Count, cd.Count);
                for (int k = 0; k < cn.Count; k++)
                    Assert.True(cd[k].Point == cn[k].Point && cd[k].Normal == cn[k].Normal
                        && cd[k].Penetration == cn[k].Penetration,
                        $"trial {trial} 接触 {k} 不一致");
            }
        }

        [Fact]
        public void SapContacts_256_SteadyCost()
        {
            var a = new List<CombatManager.PartAabb>(256);
            var b = new List<CombatManager.PartAabb>(256);
            for (int i = 0; i < 256; i++)
            {
                double y = -i * 2.0;
                a.Add(new CombatManager.PartAabb(new DVector3(-20, y - 1, -20), new DVector3(20, y + 1, 20), i));
                b.Add(new CombatManager.PartAabb(new DVector3(-14, y - 1, -20), new DVector3(26, y + 1, 20), i));
            }
            var clipMin = new DVector3(-20, -511, -20);
            var clipMax = new DVector3(26, 1, 20);
            var contacts = new List<CollisionContactPoint>(8);
            for (int i = 0; i < 200; i++)
                CombatManager.CollectPartContacts(a, b, clipMin, clipMax, contacts, 8);

            const int reps = 1000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < reps; i++)
                CombatManager.CollectPartContacts(a, b, clipMin, clipMax, contacts, 8);
            sw.Stop();
            double collectUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

            for (int i = 0; i < 200; i++)
                CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax, out _, out _, out _);
            sw.Restart();
            for (int i = 0; i < reps; i++)
                CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax, out _, out _, out _);
            sw.Stop();
            double deepestUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;
            var filterA = new List<CombatManager.PartAabb>(64);
            var filterB = new List<CombatManager.PartAabb>(64);
            var clipMinSmall = new DVector3(-20, -20, -20);
            var clipMaxSmall = new DVector3(26, 0, 20);
            for (int i = 0; i < 200; i++)
                CombatManager.CollectPartContacts(a, b, clipMinSmall, clipMaxSmall, contacts, 8, filterA, filterB);
            sw.Restart();
            for (int i = 0; i < reps; i++)
                CombatManager.CollectPartContacts(a, b, clipMinSmall, clipMaxSmall, contacts, 8, filterA, filterB);
            sw.Stop();
            double filteredUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;
            _out.WriteLine($"[方案验证] SAP 热态 256×256：接触收集 {collectUs:F1} µs/次｜最深对 {deepestUs:F1} µs/次｜clip 预过滤接触 {filteredUs:F1} µs/次（候选 {filterA.Count}/{filterB.Count}）");
        }

        [Fact]
        public void SortedSweep_ContainmentCase_MatchesNested()
        {
            // 复现“双指针在区间包含时漏对”：A0/B0 都是长区间，A1/B1 是短区间；
            // 按 (0,10),(1,2) 的推进会让 A0/B1 永远不被考虑。
            var a = new List<CombatManager.PartAabb>(64);
            var b = new List<CombatManager.PartAabb>(64);
            a.Add(new CombatManager.PartAabb(new DVector3(0, 0, 0), new DVector3(10, 10, 10), 0));
            b.Add(new CombatManager.PartAabb(new DVector3(0, 0, 0), new DVector3(10, 10, 10), 0));
            a.Add(new CombatManager.PartAabb(new DVector3(1, 0, 0), new DVector3(2, 2, 2), 1));
            b.Add(new CombatManager.PartAabb(new DVector3(1, 0, 0), new DVector3(2, 2, 2), 1));
            for (int i = 2; i < 64; i++)
            {
                a.Add(new CombatManager.PartAabb(
                    new DVector3(100 + i * 2, 100, 0), new DVector3(100 + i * 2 + 1, 101, 1), i));
                b.Add(new CombatManager.PartAabb(
                    new DVector3(100 + i * 2, 100, 0), new DVector3(100 + i * 2 + 1, 101, 1), i));
            }
            var clipMin = new DVector3(-100, -100, -100);
            var clipMax = new DVector3(1000, 1000, 1000);

            bool okDefault = CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax,
                out DVector3 nDef, out double dDef, out DVector3 cDef);
            bool okNested = CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax,
                out DVector3 nNes, out double dNes, out DVector3 cNes, forceNested: true);
            Assert.Equal(okNested, okDefault);
            if (okNested)
                Assert.True(nDef == nNes && dDef == dNes && cDef == cNes,
                    $"最深对不一致：default d={dDef} nested d={dNes}");

            var contactsDefault = new List<CollisionContactPoint>();
            var contactsNested = new List<CollisionContactPoint>();
            CombatManager.CollectPartContacts(a, b, clipMin, clipMax, contactsDefault, 8);
            CombatManager.CollectPartContacts(a, b, clipMin, clipMax, contactsNested, 8, null, null, forceNested: true);
            Assert.Equal(contactsNested.Count, contactsDefault.Count);
            for (int i = 0; i < contactsNested.Count; i++)
                Assert.True(contactsDefault[i].Point == contactsNested[i].Point
                    && contactsDefault[i].Normal == contactsNested[i].Normal
                    && contactsDefault[i].Penetration == contactsNested[i].Penetration,
                    $"接触 {i} 不一致");
        }

        [Fact]
        public void ClipAwareCollection_MatchesFull_OnCollisionResults()
        {
            using var world = new World();
            var groupA = new SpaceEntityGroup(104)
            {
                IsActive = true, IsCollidable = true, IsMovable = true,
                CurrentOrbit = new OrbitState
                {
                    positionSystem = (SystemVec3)new DVector3(0, 0, 0),
                    velocitySystem = (SystemVec3)DVector3.zero,
                    rotation = DQuaternion.identity,
                },
            };
            var groupB = new SpaceEntityGroup(105)
            {
                IsActive = true, IsCollidable = true, IsMovable = true,
                CurrentOrbit = new OrbitState
                {
                    positionSystem = (SystemVec3)new DVector3(3, 0, 0),
                    velocitySystem = (SystemVec3)DVector3.zero,
                    rotation = DQuaternion.identity,
                },
            };
            world.SpaceGame.RegisterGroup(groupA);
            world.SpaceGame.RegisterGroup(groupB);

            var eA = new SpaceEntity(104) { GroupId = 104 };
            var eB = new SpaceEntity(105) { GroupId = 105 };
            var partsA = new List<SpacePart>();
            var partsB = new List<SpacePart>();
            for (int i = 0; i < 64; i++)
            {
                partsA.Add(new SpacePart
                {
                    partId = i + 1,
                    positionShip = new DVector3(0, i * 2.0, (i % 4) * 2.0),
                    boundsSizeShip = new DVector3(2, 2, 2),
                    hp = 100,
                });
                partsB.Add(new SpacePart
                {
                    partId = i + 1,
                    positionShip = new DVector3(0, i * 2.0, (i % 4) * 2.0),
                    boundsSizeShip = new DVector3(2, 2, 2),
                    hp = 100,
                });
            }
            eA.SetParts(partsA);
            eB.SetParts(partsB);
            world.SpaceGame.RegisterEntity(eA);
            world.SpaceGame.RegisterEntity(eB);

            var fullA = new List<CombatManager.PartAabb>();
            var fullB = new List<CombatManager.PartAabb>();
            var clipA = new List<CombatManager.PartAabb>();
            var clipB = new List<CombatManager.PartAabb>();
            var rng = new Random(20261020);
            for (int trial = 0; trial < 20; trial++)
            {
                if (trial > 0)
                {
                    eA.SetRotation(DQuaternion.AngleAxis(rng.NextDouble() * 0.8,
                        new DVector3(0.2, 0.5, 0.84).normalized));
                    eB.SetRotation(DQuaternion.AngleAxis(rng.NextDouble() * 0.8,
                        new DVector3(0.7, 0.2, 0.68).normalized));
                }
                CombatManager.CollectPartAabbs(groupA, groupA.CurrentOrbit.positionSystem.v,
                    world.SpaceGame, fullA);
                CombatManager.CollectPartAabbs(groupB, groupB.CurrentOrbit.positionSystem.v,
                    world.SpaceGame, fullB);

                var clipMin = new DVector3(-2, 0, -2);
                var clipMax = new DVector3(6, 18, 6);
                CombatManager.CollectPartAabbsInClip(groupA, groupA.CurrentOrbit.positionSystem.v,
                    world.SpaceGame, clipMin, clipMax, clipA);
                CombatManager.CollectPartAabbsInClip(groupB, groupB.CurrentOrbit.positionSystem.v,
                    world.SpaceGame, clipMin, clipMax, clipB);

                bool okFull = CombatManager.TryFindDeepestPartPair(fullA, fullB, clipMin, clipMax,
                    out DVector3 nFull, out double dFull, out DVector3 cFull, forceNested: true);
                bool okClip = CombatManager.TryFindDeepestPartPair(clipA, clipB, clipMin, clipMax,
                    out DVector3 nClip, out double dClip, out DVector3 cClip, forceNested: true);
                Assert.Equal(okFull, okClip);
                if (okFull)
                {
                    Assert.True(nFull == nClip && dFull == dClip && cFull == cClip,
                        $"trial {trial} 最深对不一致：full n={nFull} d={dFull} c={cFull} count={fullA.Count}/{fullB.Count}；clip n={nClip} d={dClip} c={cClip} count={clipA.Count}/{clipB.Count}");
                }

                // 负结果门的安全性：clip 小列表的嵌套穷举说“没有 pair” ⇒ 原全量路径也不能有。
                bool fullDefault = CombatManager.TryFindDeepestPartPair(fullA, fullB, clipMin, clipMax,
                    out _, out _, out _);
                if (!okClip)
                    Assert.False(fullDefault, $"trial {trial} clip 判无 pair 但全量默认算法找到了 pair");
            }
        }

        [Fact]
        public void HoistedCollect_MatchesPartAabbApi_OnRandomRotations()
        {
            using var world = new World();
            var group = new SpaceEntityGroup(102)
            {
                IsActive = true,
                IsCollidable = true,
                IsMovable = true,
                CurrentOrbit = new OrbitState
                {
                    positionSystem = (SystemVec3)new DVector3(500, 0, 0),
                    velocitySystem = (SystemVec3)DVector3.zero,
                    rotation = DQuaternion.identity,
                },
            };
            world.SpaceGame.RegisterGroup(group);

            var entity = new SpaceEntity(102) { GroupId = 102 };
            var parts = new List<SpacePart>();
            for (int i = 0; i < 64; i++)
            {
                var p = new SpacePart
                {
                    partId = i + 1,
                    positionShip = new DVector3((i % 8) * 4, (i / 8) * 4, 0),
                    boundsSizeShip = new DVector3(4, 2, 1),
                    hp = 100,
                };
                if (i % 5 == 0)
                    p.rotationShip = DQuaternion.AngleAxis(0.1 + i * 0.02,
                        new DVector3(0.2, 0.5, 0.84).normalized);
                parts.Add(p);
            }
            entity.SetParts(parts);
            world.SpaceGame.RegisterEntity(entity);

            var prod = new List<CombatManager.PartAabb>();
            var oldApi = new List<CombatManager.PartAabb>();
            var rng = new Random(20261019);
            for (int trial = 0; trial < 40; trial++)
            {
                entity.SetRotation(DQuaternion.AngleAxis(
                    rng.NextDouble() * Math.PI * 2,
                    new DVector3(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5).normalized));
                var pos = new DVector3(rng.NextDouble() * 1e6, rng.NextDouble() * 1e6, rng.NextDouble() * 1e6);
                CombatManager.CollectPartAabbs(group, pos, world.SpaceGame, prod);
                CollectProduction(entity, pos, oldApi);
                AssertPartsExact(oldApi, prod);
            }
        }

        [Fact]
        public void RelativeCache_IsBitwiseExact()
        {
            var entity = Make256Ship(rotatedEvery: 16);
            var cache = new PartGeomCache(entity);
            var prod = new List<CombatManager.PartAabb>(256);
            var fast = new List<CombatManager.PartAabb>(256);

            var rng = new Random(20261019);
            for (int trial = 0; trial < 20; trial++)
            {
                DVector3 groupPos = new(
                    (rng.NextDouble() - 0.5) * 1e6,
                    (rng.NextDouble() - 0.5) * 1e6,
                    (rng.NextDouble() - 0.5) * 1e6);
                CollectProduction(entity, groupPos, prod);
                cache.Collect(groupPos, fast);
                Assert.Equal(prod.Count, fast.Count);
                for (int i = 0; i < prod.Count; i++)
                {
                    var a = prod[i];
                    var b = fast[i];
                    Assert.True(a.Min.x == b.Min.x && a.Min.y == b.Min.y && a.Min.z == b.Min.z,
                        $"trial {trial} part {i} Min 不一致");
                    Assert.True(a.Max.x == b.Max.x && a.Max.y == b.Max.y && a.Max.z == b.Max.z,
                        $"trial {trial} part {i} Max 不一致");
                    Assert.True(a.Center.x == b.Center.x && a.Center.y == b.Center.y && a.Center.z == b.Center.z,
                        $"trial {trial} part {i} Center 不一致");
                    Assert.True(a.Half.x == b.Half.x && a.Half.y == b.Half.y && a.Half.z == b.Half.z,
                        $"trial {trial} part {i} Half 不一致");
                }
            }

            _out.WriteLine("[方案验证] 相对几何缓存：20 个随机组位置 × 256 件，Min/Max/Center/Half 全部逐位一致");
        }

        [Fact]
        public void RelativeCache_Cost()
        {
            var entity = Make256Ship(rotatedEvery: 0);   // 主场景：全部 identity
            var cache = new PartGeomCache(entity);
            var prod = new List<CombatManager.PartAabb>(256);
            var fast = new List<CombatManager.PartAabb>(256);

            for (int i = 0; i < 20; i++)
            {
                CollectProduction(entity, new DVector3(i, 0, 0), prod);
                cache.Collect(new DVector3(i, 0, 0), fast);
            }

            const int reps = 500;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < reps; i++)
                CollectProduction(entity, new DVector3(i * 0.01, 0, 0), prod);
            sw.Stop();
            double prodUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

            sw.Restart();
            for (int i = 0; i < reps; i++)
                cache.Collect(new DVector3(i * 0.01, 0, 0), fast);
            sw.Stop();
            double cacheUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

            sw.Restart();
            var cache2 = new PartGeomCache(entity);
            sw.Stop();
            double buildUs = sw.Elapsed.TotalMilliseconds * 1000.0;

            _out.WriteLine($"[方案验证] 256 件全 identity：生产收集 {prodUs:F1} µs/次（{prodUs * 1000 / 256:F1} ns/件）｜缓存收集 {cacheUs:F1} µs/次（{cacheUs * 1000 / 256:F1} ns/件）｜缓存构建 {buildUs:F1} µs");
        }

        [Fact]
        public void RelativeCache_Collision6Collect_Estimate()
        {
            var entity = Make256Ship(rotatedEvery: 0);
            var cache = new PartGeomCache(entity);
            var prod = new List<CombatManager.PartAabb>(256);
            var fast = new List<CombatManager.PartAabb>(256);

            for (int i = 0; i < 20; i++)
            {
                CollectProduction(entity, new DVector3(i, 0, 0), prod);
                cache.Collect(new DVector3(i, 0, 0), fast);
            }

            const int reps = 200;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < reps; i++)
            {
                // 生产：6 次全量（Ensure 2 + 位置投影 2 轮 × 2 组）
                for (int k = 0; k < 6; k++)
                    CollectProduction(entity, new DVector3(k, 0, 0), prod);
            }
            sw.Stop();
            double prod6Us = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

            sw.Restart();
            for (int i = 0; i < reps; i++)
            {
                // 方案：1 次构建 + 5 次平移
                var localCache = new PartGeomCache(entity);
                for (int k = 0; k < 6; k++)
                    localCache.Collect(new DVector3(k, 0, 0), fast);
            }
            sw.Stop();
            double cache6Us = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

            _out.WriteLine($"[方案验证] 一次 256 件接触的 6 次收集：生产 {prod6Us:F1} µs｜相对缓存（含构建）{cache6Us:F1} µs｜节省 {(prod6Us - cache6Us) / prod6Us * 100.0:F1}%");
        }

        // ═══════════════════════════════════════════════════════════
        //  2. Solver：预计算世界逆惯量（数值差异 + 成本）
        // ═══════════════════════════════════════════════════════════

        private struct WorldInv
        {
            public double m00, m01, m02, m11, m12, m22;
            public bool Has;

            public DVector3 Multiply(in DVector3 v) => new(
                m00 * v.x + m01 * v.y + m02 * v.z,
                m01 * v.x + m11 * v.y + m12 * v.z,
                m02 * v.x + m12 * v.y + m22 * v.z);
        }

        private static WorldInv ComputeWorldInv(in InertiaTensor.InertiaResult r, DQuaternion rotation)
        {
            if (!r.HasData || !(r.p1 > 0.0) || !(r.p2 > 0.0) || !(r.p3 > 0.0)) return default;

            PartGeometry.Axes(rotation, out DVector3 ax, out DVector3 ay, out DVector3 az);
            // 主轴矩阵的列 = r 的列
            DVector3 px = new(r.r00, r.r10, r.r20);
            DVector3 py = new(r.r01, r.r11, r.r21);
            DVector3 pz = new(r.r02, r.r12, r.r22);

            // 世界系主轴列 = R_body · (主轴列)
            DVector3 wx = ax * px.x + ay * px.y + az * px.z;
            DVector3 wy = ax * py.x + ay * py.y + az * py.z;
            DVector3 wz = ax * pz.x + ay * pz.y + az * pz.z;

            double i1 = 1.0 / r.p1, i2 = 1.0 / r.p2, i3 = 1.0 / r.p3;
            return new WorldInv
            {
                Has = true,
                m00 = i1 * wx.x * wx.x + i2 * wy.x * wy.x + i3 * wz.x * wz.x,
                m01 = i1 * wx.x * wx.y + i2 * wy.x * wy.y + i3 * wz.x * wz.y,
                m02 = i1 * wx.x * wx.z + i2 * wy.x * wy.z + i3 * wz.x * wz.z,
                m11 = i1 * wx.y * wx.y + i2 * wy.y * wy.y + i3 * wz.y * wz.y,
                m12 = i1 * wx.y * wx.z + i2 * wy.y * wy.z + i3 * wz.y * wz.z,
                m22 = i1 * wx.z * wx.z + i2 * wy.z * wy.z + i3 * wz.z * wz.z,
            };
        }

        private static DVector3 RelativeVelocityPre(in CollisionBodyState a, in CollisionBodyState b,
            DVector3 rA, DVector3 rB)
        {
            DVector3 vA = a.Velocity + DVector3.Cross(a.AngularVelocity, rA);
            DVector3 vB = b.Velocity + DVector3.Cross(b.AngularVelocity, rB);
            return vB - vA;
        }

        private static void ApplyImpulsePre(ref CollisionBodyState a, ref CollisionBodyState b,
            in WorldInv ia, in WorldInv ib, DVector3 rA, DVector3 rB, DVector3 impulse,
            double invMassA, double invMassB)
        {
            a.Velocity -= impulse * invMassA;
            b.Velocity += impulse * invMassB;
            a.AngularVelocity -= ia.Multiply(DVector3.Cross(rA, impulse));
            b.AngularVelocity += ib.Multiply(DVector3.Cross(rB, impulse));
        }

        private static double EffectiveMassPre(in CollisionBodyState a, in CollisionBodyState b,
            in WorldInv ia, in WorldInv ib, double invMassA, double invMassB,
            DVector3 rA, DVector3 rB, DVector3 axis)
        {
            double k = invMassA + invMassB;
            if (a.Inertia.HasData)
            {
                DVector3 i = ia.Multiply(DVector3.Cross(rA, axis));
                k += DVector3.Dot(DVector3.Cross(i, rA), axis);
            }
            if (b.Inertia.HasData)
            {
                DVector3 i = ib.Multiply(DVector3.Cross(rB, axis));
                k += DVector3.Dot(DVector3.Cross(i, rB), axis);
            }
            return k;
        }

        private static DVector3 TangentialDirectionPre(DVector3 rA, DVector3 rB, DVector3 normal,
            in CollisionBodyState a, in CollisionBodyState b)
        {
            DVector3 rel = RelativeVelocityPre(a, b, rA, rB);
            DVector3 tangent = rel - normal * DVector3.Dot(rel, normal);
            if (tangent.sqrMagnitude > 1e-18) return tangent.normalized;
            DVector3 axis = Math.Abs(DVector3.Dot(normal, DVector3.up)) < 0.9 ? DVector3.up : DVector3.right;
            tangent = DVector3.Cross(normal, axis);
            return tangent.sqrMagnitude > 1e-18 ? tangent.normalized : DVector3.right;
        }

        // 旧生产实现的逐字参考（P3 改造前）：只用于对拍/测速，不进生产。
        private static DVector3 InverseInertiaWorldLegacy(in CollisionBodyState body, DVector3 world)
        {
            if (!body.Inertia.HasData) return DVector3.zero;
            DQuaternion inv = DQuaternion.Inverse(body.Rotation);
            DVector3 local = DVector3.Rotate(world, inv);
            DVector3 result = InertiaTensor.InverseInertiaMultiply(body.Inertia, local.x, local.y, local.z);
            return DVector3.Rotate(result, body.Rotation);
        }

        private static void ApplyImpulseLegacy(ref CollisionBodyState a, ref CollisionBodyState b,
            DVector3 rA, DVector3 rB, DVector3 impulse, double invMassA, double invMassB)
        {
            a.Velocity -= impulse * invMassA;
            b.Velocity += impulse * invMassB;
            a.AngularVelocity -= InverseInertiaWorldLegacy(a, DVector3.Cross(rA, impulse));
            b.AngularVelocity += InverseInertiaWorldLegacy(b, DVector3.Cross(rB, impulse));
        }

        private static double EffectiveMassLegacy(in CollisionBodyState a, in CollisionBodyState b,
            double invMassA, double invMassB, DVector3 rA, DVector3 rB, DVector3 axis)
        {
            double k = invMassA + invMassB;
            if (a.Inertia.HasData)
            {
                DVector3 i = InverseInertiaWorldLegacy(a, DVector3.Cross(rA, axis));
                k += DVector3.Dot(DVector3.Cross(i, rA), axis);
            }
            if (b.Inertia.HasData)
            {
                DVector3 i = InverseInertiaWorldLegacy(b, DVector3.Cross(rB, axis));
                k += DVector3.Dot(DVector3.Cross(i, rB), axis);
            }
            return k;
        }

        private static void ResolveLegacy(ref CollisionBodyState a, ref CollisionBodyState b,
            ReadOnlySpan<CollisionContactPoint> contacts, double restitution, double friction, int iterations)
        {
            if (contacts.Length == 0 || iterations <= 0) return;
            if (restitution < 0.0) restitution = 0.0;
            if (friction < 0.0) friction = 0.0;

            double invMassA = a.Mass > 0.0 ? 1.0 / a.Mass : 0.0;
            double invMassB = b.Mass > 0.0 ? 1.0 / b.Mass : 0.0;
            if (invMassA <= 0.0 && invMassB <= 0.0) return;

            int n = contacts.Length;
            Span<double> normalImpulse = stackalloc double[n];
            Span<double> tangentImpulse = stackalloc double[n];
            Span<double> normalMass = stackalloc double[n];
            Span<double> tangentMass = stackalloc double[n];
            Span<DVector3> rA = stackalloc DVector3[n];
            Span<DVector3> rB = stackalloc DVector3[n];
            Span<DVector3> normals = stackalloc DVector3[n];
            Span<DVector3> tangents = stackalloc DVector3[n];

            DVector3 comA = a.CenterOfMassWorld;
            DVector3 comB = b.CenterOfMassWorld;
            for (int i = 0; i < n; i++)
            {
                DVector3 normal = contacts[i].Normal;
                double len2 = normal.sqrMagnitude;
                normal = len2 > 1e-18 ? normal * (1.0 / Math.Sqrt(len2)) : DVector3.up;
                normals[i] = normal;
                rA[i] = contacts[i].Point - comA;
                rB[i] = contacts[i].Point - comB;
                DVector3 tangent = TangentialDirectionPre(rA[i], rB[i], normal, a, b);
                tangents[i] = tangent;
                normalMass[i] = EffectiveMassLegacy(a, b, invMassA, invMassB, rA[i], rB[i], normal);
                tangentMass[i] = tangent.sqrMagnitude > 0.5
                    ? EffectiveMassLegacy(a, b, invMassA, invMassB, rA[i], rB[i], tangent)
                    : 0.0;
            }

            for (int iter = 0; iter < iterations; iter++)
            {
                for (int i = 0; i < n; i++)
                {
                    DVector3 normal = normals[i];
                    if (!(normalMass[i] > 1e-18)) continue;
                    DVector3 rel = RelativeVelocityPre(a, b, rA[i], rB[i]);
                    double vn = DVector3.Dot(rel, normal);
                    if (vn < 0.0)
                    {
                        double target = -restitution * vn;
                        double jn = (target - vn) / normalMass[i];
                        double old = normalImpulse[i];
                        double acc = Math.Max(old + jn, 0.0);
                        jn = acc - old;
                        normalImpulse[i] = acc;
                        if (jn != 0.0)
                            ApplyImpulseLegacy(ref a, ref b, rA[i], rB[i], normal * jn, invMassA, invMassB);
                    }
                    if (friction > 0.0 && tangentMass[i] > 1e-18)
                    {
                        rel = RelativeVelocityPre(a, b, rA[i], rB[i]);
                        DVector3 tangent = tangents[i];
                        double vt = DVector3.Dot(rel, tangent);
                        double jt = -vt / tangentMass[i];
                        double max = friction * normalImpulse[i];
                        double old = tangentImpulse[i];
                        double acc = Math.Max(-max, Math.Min(max, old + jt));
                        jt = acc - old;
                        tangentImpulse[i] = acc;
                        if (jt != 0.0)
                            ApplyImpulseLegacy(ref a, ref b, rA[i], rB[i], tangent * jt, invMassA, invMassB);
                    }
                }
            }
        }

        private static void ResolvePrecomputed(ref CollisionBodyState a, ref CollisionBodyState b,
            ReadOnlySpan<CollisionContactPoint> contacts, double restitution, double friction, int iterations)
        {
            if (contacts.Length == 0 || iterations <= 0) return;
            if (restitution < 0.0) restitution = 0.0;
            if (friction < 0.0) friction = 0.0;

            double invMassA = a.Mass > 0.0 ? 1.0 / a.Mass : 0.0;
            double invMassB = b.Mass > 0.0 ? 1.0 / b.Mass : 0.0;
            if (invMassA <= 0.0 && invMassB <= 0.0) return;

            WorldInv ia = ComputeWorldInv(a.Inertia, a.Rotation);
            WorldInv ib = ComputeWorldInv(b.Inertia, b.Rotation);

            int n = contacts.Length;
            Span<double> normalImpulse = stackalloc double[n];
            Span<double> tangentImpulse = stackalloc double[n];
            Span<double> normalMass = stackalloc double[n];
            Span<double> tangentMass = stackalloc double[n];
            Span<DVector3> rA = stackalloc DVector3[n];
            Span<DVector3> rB = stackalloc DVector3[n];
            Span<DVector3> normals = stackalloc DVector3[n];
            Span<DVector3> tangents = stackalloc DVector3[n];

            DVector3 comA = a.CenterOfMassWorld;
            DVector3 comB = b.CenterOfMassWorld;

            for (int i = 0; i < n; i++)
            {
                DVector3 normal = contacts[i].Normal;
                double len2 = normal.sqrMagnitude;
                normal = len2 > 1e-18 ? normal * (1.0 / Math.Sqrt(len2)) : DVector3.up;
                normals[i] = normal;
                rA[i] = contacts[i].Point - comA;
                rB[i] = contacts[i].Point - comB;

                DVector3 tangent = TangentialDirectionPre(rA[i], rB[i], normal, a, b);
                tangents[i] = tangent;
                normalMass[i] = EffectiveMassPre(a, b, ia, ib, invMassA, invMassB, rA[i], rB[i], normal);
                tangentMass[i] = tangent.sqrMagnitude > 0.5
                    ? EffectiveMassPre(a, b, ia, ib, invMassA, invMassB, rA[i], rB[i], tangent)
                    : 0.0;
            }

            for (int iter = 0; iter < iterations; iter++)
            {
                for (int i = 0; i < n; i++)
                {
                    DVector3 normal = normals[i];
                    if (!(normalMass[i] > 1e-18)) continue;

                    DVector3 rel = RelativeVelocityPre(a, b, rA[i], rB[i]);
                    double vn = DVector3.Dot(rel, normal);
                    if (vn < 0.0)
                    {
                        double target = -restitution * vn;
                        double jn = (target - vn) / normalMass[i];
                        double old = normalImpulse[i];
                        double acc = Math.Max(old + jn, 0.0);
                        jn = acc - old;
                        normalImpulse[i] = acc;
                        if (jn != 0.0)
                            ApplyImpulsePre(ref a, ref b, ia, ib, rA[i], rB[i], normal * jn, invMassA, invMassB);
                    }

                    if (friction > 0.0 && tangentMass[i] > 1e-18)
                    {
                        rel = RelativeVelocityPre(a, b, rA[i], rB[i]);
                        DVector3 tangent = tangents[i];
                        double vt = DVector3.Dot(rel, tangent);
                        double jt = -vt / tangentMass[i];
                        double max = friction * normalImpulse[i];
                        double old = tangentImpulse[i];
                        double acc = Math.Max(-max, Math.Min(max, old + jt));
                        jt = acc - old;
                        tangentImpulse[i] = acc;
                        if (jt != 0.0)
                            ApplyImpulsePre(ref a, ref b, ia, ib, rA[i], rB[i], tangent * jt, invMassA, invMassB);
                    }
                }
            }
        }

        private static CollisionBodyState MakeBody(Random rng, DVector3 pos, DVector3 vel)
        {
            var principalRot = DQuaternion.AngleAxis(
                rng.NextDouble() * Math.PI * 2,
                new DVector3(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5).normalized);
            PartGeometry.Axes(principalRot, out DVector3 px, out DVector3 py, out DVector3 pz);
            var inertia = new InertiaTensor.InertiaResult
            {
                HasData = true,
                mass = 1000,
                p1 = 500 + rng.NextDouble() * 500,
                p2 = 500 + rng.NextDouble() * 500,
                p3 = 500 + rng.NextDouble() * 500,
                // 主轴矩阵的列 = px/py/pz（与 InverseInertiaMultiply 的 r00/r10/r20 同口径）
                r00 = px.x, r10 = px.y, r20 = px.z,
                r01 = py.x, r11 = py.y, r21 = py.z,
                r02 = pz.x, r12 = pz.y, r22 = pz.z,
            };
            return new CollisionBodyState
            {
                Mass = 100 + rng.NextDouble() * 900,
                Inertia = inertia,
                Rotation = DQuaternion.AngleAxis(
                    rng.NextDouble() * Math.PI * 2,
                    new DVector3(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5).normalized),
                Position = pos,
                Velocity = vel,
                AngularVelocity = new DVector3(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5),
            };
        }

        [Fact]
        public void Solver_PrecomputedWorldInertia_NumericDiff()
        {
            var rng = new Random(20261019);
            double maxAbs = 0.0, maxRel = 0.0;
            Span<CollisionContactPoint> contactBuffer = stackalloc CollisionContactPoint[8];
            for (int trial = 0; trial < 2000; trial++)
            {
                var a0 = MakeBody(rng, DVector3.zero, new DVector3(rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2));
                var b0 = MakeBody(rng, new DVector3(2, 0, 0), new DVector3(rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2));
                int n = 1 + rng.Next(8);
                Span<CollisionContactPoint> contacts = contactBuffer.Slice(0, n);
                for (int i = 0; i < n; i++)
                {
                    DVector3 normal = new(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
                    contacts[i] = new CollisionContactPoint
                    {
                        Point = new DVector3(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1),
                        Normal = normal.normalized,
                        Penetration = 0.1,
                    };
                }

                var a1 = a0; var b1 = b0;
                ResolveLegacy(ref a1, ref b1, contacts, 0.2, 0.25, 6);
                var a2 = a0; var b2 = b0;
                CollisionSolver.Resolve(ref a2, ref b2, contacts, 0.2, 0.25, 6);

                DVector3 dv = a1.Velocity - a2.Velocity;
                DVector3 dw = a1.AngularVelocity - a2.AngularVelocity;
                DVector3 dvb = b1.Velocity - b2.Velocity;
                DVector3 dwb = b1.AngularVelocity - b2.AngularVelocity;
                double abs = Math.Max(Math.Max(dv.magnitude, dw.magnitude), Math.Max(dvb.magnitude, dwb.magnitude));
                double scale = Math.Max(1.0, Math.Max(a1.Velocity.magnitude, a1.AngularVelocity.magnitude));
                maxAbs = Math.Max(maxAbs, abs);
                maxRel = Math.Max(maxRel, abs / scale);
            }

            _out.WriteLine($"[方案验证] solver 预计算世界逆惯量：2000 随机例，最大绝对差 {maxAbs:E3}，最大相对差 {maxRel:E3}");
            // 只报告，不判 PASS/FAIL（口径由用户裁定）
        }

        [Fact]
        public void Solver_PrecomputedWorldInertia_Cost()
        {
            var a = MakeBody(new Random(1), DVector3.zero, new DVector3(4, 0, 0));
            var b = MakeBody(new Random(2), new DVector3(2, 0, 0), new DVector3(-4, 0, 0));
            Span<CollisionContactPoint> contacts = stackalloc CollisionContactPoint[8];
            for (int i = 0; i < contacts.Length; i++)
                contacts[i] = new CollisionContactPoint
                {
                    Point = new DVector3(1, i * 0.2 - 0.7, 0),
                    Normal = DVector3.right,
                    Penetration = 0.1,
                };

            for (int i = 0; i < 100; i++)
            {
                var aa = a; var bb = b;
                CollisionSolver.Resolve(ref aa, ref bb, contacts, 0.2, 0.25, 6);
                ResolveLegacy(ref aa, ref bb, contacts, 0.2, 0.25, 6);
            }

            const int reps = 20000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < reps; i++)
            {
                var aa = a; var bb = b;
                ResolveLegacy(ref aa, ref bb, contacts, 0.2, 0.25, 6);
            }
            sw.Stop();
            double origUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

            sw.Restart();
            for (int i = 0; i < reps; i++)
            {
                var aa = a; var bb = b;
                CollisionSolver.Resolve(ref aa, ref bb, contacts, 0.2, 0.25, 6);
            }
            sw.Stop();
            double preUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

            _out.WriteLine($"[方案验证] solver 8 接触 × 6 迭代：原实现 {origUs:F2} µs｜预计算 {preUs:F2} µs｜加速 {origUs / preUs:F2}×");
        }

        [Fact]
        public void Solver_PrecomputedWorldInertia_Cost_UnitInertia()
        {
            // 与 CollisionContactBench 同口径：单位惯量、identity 姿态。
            var a = new CollisionBodyState
            {
                Mass = 1000,
                Inertia = new InertiaTensor.InertiaResult
                {
                    HasData = true, mass = 1000,
                    p1 = 1000, p2 = 1000, p3 = 1000,
                    r00 = 1, r11 = 1, r22 = 1,
                },
                Rotation = DQuaternion.identity,
                Position = DVector3.zero,
                Velocity = new DVector3(4, 0, 0),
            };
            var b = a;
            b.Position = new DVector3(2, 0, 0);
            b.Velocity = new DVector3(-4, 0, 0);

            Span<CollisionContactPoint> contacts = stackalloc CollisionContactPoint[8];
            for (int i = 0; i < contacts.Length; i++)
                contacts[i] = new CollisionContactPoint
                {
                    Point = new DVector3(1, i * 0.2 - 0.7, 0),
                    Normal = DVector3.right,
                    Penetration = 0.1,
                };

            for (int i = 0; i < 100; i++)
            {
                var aa = a; var bb = b;
                CollisionSolver.Resolve(ref aa, ref bb, contacts, 0.2, 0.25, 6);
                ResolveLegacy(ref aa, ref bb, contacts, 0.2, 0.25, 6);
            }

            const int reps = 20000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < reps; i++)
            {
                var aa = a; var bb = b;
                ResolveLegacy(ref aa, ref bb, contacts, 0.2, 0.25, 6);
            }
            sw.Stop();
            double origUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

            sw.Restart();
            for (int i = 0; i < reps; i++)
            {
                var aa = a; var bb = b;
                CollisionSolver.Resolve(ref aa, ref bb, contacts, 0.2, 0.25, 6);
            }
            sw.Stop();
            double preUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

            _out.WriteLine($"[方案验证] solver 单位惯量 8 接触 × 6 迭代：原 {origUs:F2} µs｜预计算 {preUs:F2} µs｜加速 {origUs / preUs:F2}×");
        }

        // ═══════════════════════════════════════════════════════════
        //  3. CCD 的 O(nA×nB) 扫描成本（PartsOverlap 的最坏情形）
        // ═══════════════════════════════════════════════════════════

        private static bool AabbOverlap(in DVector3 aMin, in DVector3 aMax, in DVector3 bMin, in DVector3 bMax)
            => aMin.x <= bMax.x && aMax.x >= bMin.x
            && aMin.y <= bMax.y && aMax.y >= bMin.y
            && aMin.z <= bMax.z && aMax.z >= bMin.z;

        private static bool PartsOverlapWorst(List<CombatManager.PartAabb> a, List<CombatManager.PartAabb> b)
        {
            for (int i = 0; i < a.Count; i++)
            {
                var pa = a[i];
                for (int j = 0; j < b.Count; j++)
                {
                    var pb = b[j];
                    if (AabbOverlap(pa.Min, pa.Max, pb.Min, pb.Max)) return true;
                }
            }
            return false;
        }

        [Fact]
        public void PartsOverlap_WorstCase_Cost()
        {
            foreach (int n in new[] { 64, 128, 256 })
            {
                var a = new List<CombatManager.PartAabb>(n);
                var b = new List<CombatManager.PartAabb>(n);
                for (int i = 0; i < n; i++)
                {
                    double x = i * 10.0;
                    a.Add(new CombatManager.PartAabb(
                        new DVector3(x - 1, -1, -1), new DVector3(x + 1, 1, 1), i));
                    // 整体错开 5m，保证全不重叠（最坏：扫完所有对）
                    b.Add(new CombatManager.PartAabb(
                        new DVector3(x + 5 - 1, -1, -1), new DVector3(x + 5 + 1, 1, 1), i));
                }

                Assert.False(PartsOverlapWorst(a, b));
                const int reps = 2000;
                var sw = Stopwatch.StartNew();
                for (int r = 0; r < reps; r++)
                    PartsOverlapWorst(a, b);
                sw.Stop();
                double us = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;
                _out.WriteLine($"[方案验证] PartsOverlap 最坏 {n}×{n} = {us:F1} µs/次（{us * 1000 / (n * n):F2} ns/对）；64 次采样 ≈ {us * 64:F0} µs");
            }
        }

        private static int[] AxisOrder(List<CombatManager.PartAabb> parts, int axis)
        {
            var idx = new int[parts.Count];
            var keys = new double[parts.Count];
            for (int i = 0; i < idx.Length; i++)
            {
                idx[i] = i;
                var p = parts[i];
                keys[i] = axis == 0 ? p.Min.x : axis == 1 ? p.Min.y : p.Min.z;
            }
            Array.Sort(keys, idx);
            return idx;
        }

        [Fact]
        public void PartsOverlapSweep_MatchesBruteForce_OnRandomLists()
        {
            var rng = new Random(12345);
            for (int trial = 0; trial < 300; trial++)
            {
                int na = rng.Next(1, 65);
                int nb = rng.Next(1, 65);
                var a = new List<CombatManager.PartAabb>(na);
                var b = new List<CombatManager.PartAabb>(nb);
                for (int i = 0; i < na; i++)
                {
                    double cx = rng.NextDouble() * 100.0 - 50.0;
                    double cy = rng.NextDouble() * 100.0 - 50.0;
                    double cz = rng.NextDouble() * 100.0 - 50.0;
                    double h = 0.2 + rng.NextDouble() * 5.0;
                    a.Add(new CombatManager.PartAabb(
                        new DVector3(cx - h, cy - h, cz - h),
                        new DVector3(cx + h, cy + h, cz + h), i));
                }
                for (int i = 0; i < nb; i++)
                {
                    double cx = rng.NextDouble() * 100.0 - 50.0;
                    double cy = rng.NextDouble() * 100.0 - 50.0;
                    double cz = rng.NextDouble() * 100.0 - 50.0;
                    double h = 0.2 + rng.NextDouble() * 5.0;
                    b.Add(new CombatManager.PartAabb(
                        new DVector3(cx - h, cy - h, cz - h),
                        new DVector3(cx + h, cy + h, cz + h), i));
                }

                bool expected = PartsOverlapWorst(a, b);
                for (int axis = 0; axis < 3; axis++)
                {
                    var oa = AxisOrder(a, axis);
                    var ob = AxisOrder(b, axis);
                    Assert.Equal(expected, CombatManager.PartsOverlapSweep(a, b, oa, ob, axis));
                }
            }
        }

        [Fact]
        public void CollisionCache_AxisOrders_IncludeEntityPosition()
        {
            var cache = new CombatManager.GroupCollisionCache();
            // part0 所在实体整体在 x=100（RelMin=0）；part1 实体在 0、RelMin=10。
            // 只按 RelMin 排序会得到 [0,1]；正确世界顺序必须是 [1（x=10）,0（x=100）]。
            cache.relParts.Add(new CombatManager.PartRelGeom
            {
                EntityPosition = new DVector3(100, 0, 0),
                Identity = true,
                RelMin = new DVector3(0, -1, -1),
                RelMax = new DVector3(2, 1, 1),
                Order = 0
            });
            cache.relParts.Add(new CombatManager.PartRelGeom
            {
                EntityPosition = DVector3.zero,
                Identity = true,
                RelMin = new DVector3(10, -1, -1),
                RelMax = new DVector3(12, 1, 1),
                Order = 1
            });
            cache.RebuildAxisOrders();
            Assert.Equal(1, cache.orderX![0]);
            Assert.Equal(0, cache.orderX[1]);
        }

        [Fact]
        public void PartsOverlapCore_MatchesBruteForce_WithCacheOrders()
        {
            var rng = new Random(54321);
            for (int trial = 0; trial < 200; trial++)
            {
                int na = rng.Next(9, 65);
                int nb = rng.Next(9, 65);
                var ca = new CombatManager.GroupCollisionCache();
                var cb = new CombatManager.GroupCollisionCache();
                Fill(ca, na);
                Fill(cb, nb);

                var la = new List<CombatManager.PartAabb>();
                var lb = new List<CombatManager.PartAabb>();
                ca.CollectPartsAt(new DVector3(rng.NextDouble() * 200 - 100, 0, 0), la);
                cb.CollectPartsAt(new DVector3(rng.NextDouble() * 200 - 100, 0, 0), lb);

                bool expected = PartsOverlapWorst(la, lb);
                Assert.Equal(expected, CombatManager.PartsOverlapCore(ca, cb, la, lb));

                void Fill(CombatManager.GroupCollisionCache cache, int count)
                {
                    for (int i = 0; i < count; i++)
                    {
                        var ent = new DVector3(
                            rng.NextDouble() * 40 - 20,
                            rng.NextDouble() * 40 - 20,
                            rng.NextDouble() * 40 - 20);
                        var center = new DVector3(
                            rng.NextDouble() * 100 - 50,
                            rng.NextDouble() * 100 - 50,
                            rng.NextDouble() * 100 - 50);
                        var half = new DVector3(
                            0.2 + rng.NextDouble() * 5,
                            0.2 + rng.NextDouble() * 5,
                            0.2 + rng.NextDouble() * 5);
                        if (rng.Next(4) == 0)
                        {
                            cache.relParts.Add(new CombatManager.PartRelGeom
                            {
                                EntityPosition = ent,
                                Identity = false,
                                CenterOffset = center,
                                Half = half,
                                ProjectedHalf = half,
                                WorldRot = DQuaternion.identity,
                                Order = i,
                                Shape = InertiaTensor.Shape.Box
                            });
                        }
                        else
                        {
                            cache.relParts.Add(new CombatManager.PartRelGeom
                            {
                                EntityPosition = ent,
                                Identity = true,
                                RelMin = center - half,
                                RelMax = center + half,
                                Order = i,
                                Shape = InertiaTensor.Shape.Box
                            });
                        }
                    }
                    cache.RebuildAxisOrders();
                }
            }
        }

        [Fact]
        public void CcdPartRefine_Allows256x256()
        {
            // 护栏必须允许 256×256 进入 part 级细化（三轴 SAP 已能承接）。
            Assert.True(CombatManager.CcdPartRefineMaxPartPairs >= 256L * 256L);
        }

        [Fact]
        public void PartsOverlapCore_256x256_NoOverlap_And_OneOverlap()
        {
            (CombatManager.GroupCollisionCache ca, CombatManager.GroupCollisionCache cb,
                List<CombatManager.PartAabb> la, List<CombatManager.PartAabb> lb) Build(bool overlap)
            {
                const int n = 256;
                var ca = new CombatManager.GroupCollisionCache();
                var cb = new CombatManager.GroupCollisionCache();
                for (int i = 0; i < n; i++)
                {
                    double x = i * 10.0;
                    ca.relParts.Add(new CombatManager.PartRelGeom
                    {
                        EntityPosition = DVector3.zero,
                        Identity = true,
                        RelMin = new DVector3(x - 1, -1, -1),
                        RelMax = new DVector3(x + 1, 1, 1),
                        Order = i,
                        Shape = InertiaTensor.Shape.Box
                    });
                    double bx = overlap && i == 128 ? x : x + 5.0;
                    cb.relParts.Add(new CombatManager.PartRelGeom
                    {
                        EntityPosition = DVector3.zero,
                        Identity = true,
                        RelMin = new DVector3(bx - 1, -1, -1),
                        RelMax = new DVector3(bx + 1, 1, 1),
                        Order = i,
                        Shape = InertiaTensor.Shape.Box
                    });
                }
                ca.RebuildAxisOrders();
                cb.RebuildAxisOrders();
                var la = new List<CombatManager.PartAabb>();
                var lb = new List<CombatManager.PartAabb>();
                ca.CollectPartsAt(DVector3.zero, la);
                cb.CollectPartsAt(DVector3.zero, lb);
                return (ca, cb, la, lb);
            }

            var no = Build(false);
            Assert.False(PartsOverlapWorst(no.la, no.lb));
            Assert.False(CombatManager.PartsOverlapCore(no.ca, no.cb, no.la, no.lb));

            // 轴序缺失时必须按需重建，而不是退回 brute：
            no.ca.orderX = no.ca.orderY = no.ca.orderZ = null;
            no.cb.orderX = no.cb.orderY = no.cb.orderZ = null;
            Assert.False(CombatManager.PartsOverlapCore(no.ca, no.cb, no.la, no.lb));
            Assert.NotNull(no.ca.orderX);
            Assert.NotNull(no.cb.orderX);

            var one = Build(true);
            Assert.True(PartsOverlapWorst(one.la, one.lb));
            Assert.True(CombatManager.PartsOverlapCore(one.ca, one.cb, one.la, one.lb));
        }

        [Fact]
        public void PartAabb_Translated_MatchesFreshCollect_AndOverlap()
        {
            var rng = new Random(2468);
            var ca = new CombatManager.GroupCollisionCache();
            var cb = new CombatManager.GroupCollisionCache();
            Fill(ca);
            Fill(cb);

            var p0 = new DVector3(1000, -500, 200);
            var p1 = p0 + new DVector3(123.456, -78.9, 45.6);
            var freshA = new List<CombatManager.PartAabb>();
            var freshB = new List<CombatManager.PartAabb>();
            var workA = new List<CombatManager.PartAabb>();
            var workB = new List<CombatManager.PartAabb>();
            ca.CollectPartsAt(p1, freshA);
            cb.CollectPartsAt(p1, freshB);
            ca.CollectPartsAt(p0, workA);
            cb.CollectPartsAt(p0, workB);
            CombatManager.TranslatePartsInPlace(workA, p1 - p0);
            CombatManager.TranslatePartsInPlace(workB, p1 - p0);

            double maxDiff = 0;
            for (int i = 0; i < freshA.Count; i++)
            {
                var f = freshA[i];
                var w = workA[i];
                maxDiff = Math.Max(maxDiff, (f.Min - w.Min).magnitude);
                maxDiff = Math.Max(maxDiff, (f.Max - w.Max).magnitude);
                maxDiff = Math.Max(maxDiff, (f.Center - w.Center).magnitude);
            }
            _out.WriteLine($"[方案验证] 平移 vs 重收集：256 件最大坐标差 = {maxDiff:E3} m");
            Assert.True(maxDiff < 1e-9, $"平移误差 {maxDiff:E3} m");
            Assert.Equal(
                CombatManager.PartsOverlapCore(ca, cb, freshA, freshB),
                CombatManager.PartsOverlapCore(ca, cb, workA, workB));

            void Fill(CombatManager.GroupCollisionCache cache)
            {
                for (int i = 0; i < 256; i++)
                {
                    var center = new DVector3(
                        rng.NextDouble() * 100 - 50,
                        rng.NextDouble() * 100 - 50,
                        rng.NextDouble() * 100 - 50);
                    var half = new DVector3(
                        0.2 + rng.NextDouble() * 2,
                        0.2 + rng.NextDouble() * 2,
                        0.2 + rng.NextDouble() * 2);
                    bool obb = rng.Next(4) == 0;
                    cache.relParts.Add(new CombatManager.PartRelGeom
                    {
                        EntityPosition = DVector3.zero,
                        Identity = !obb,
                        RelMin = center - half,
                        RelMax = center + half,
                        CenterOffset = center,
                        Half = half,
                        ProjectedHalf = half,
                        WorldRot = DQuaternion.identity,
                        Order = i,
                        Shape = InertiaTensor.Shape.Box
                    });
                }
                cache.RebuildAxisOrders();
            }
        }

        [Fact]
        public void TranslatePartsInPlace_256_Cost()
        {
            var parts = new List<CombatManager.PartAabb>(256);
            for (int i = 0; i < 256; i++)
            {
                double x = i * 2.2;
                parts.Add(new CombatManager.PartAabb(
                    new DVector3(x - 1, -1, -1), new DVector3(x + 1, 1, 1), i));
            }
            var delta = new DVector3(0.5, 0.25, -0.125);
            const int reps = 20000;
            var sw = Stopwatch.StartNew();
            for (int r = 0; r < reps; r++)
                CombatManager.TranslatePartsInPlace(parts, delta);
            sw.Stop();
            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;
            _out.WriteLine($"[方案验证] 256 件原位平移 = {us:F2} µs/次；53 次采样 ≈ {us * 53:F1} µs（原 CollectPartsAt 25.2µs × 53 ≈ 1336µs）");
        }

        [Fact]
        public void HasSweptAxisOverlap_MatchesBruteForce()
        {
            var rng = new Random(97531);
            for (int trial = 0; trial < 300; trial++)
            {
                int na = rng.Next(1, 65);
                int nb = rng.Next(1, 65);
                var a = new List<CombatManager.PartAabb>(na);
                var b = new List<CombatManager.PartAabb>(nb);
                for (int i = 0; i < na; i++)
                {
                    double x = rng.NextDouble() * 200 - 100;
                    double h = 0.2 + rng.NextDouble() * 5;
                    a.Add(new CombatManager.PartAabb(
                        new DVector3(x - h, -1, -1), new DVector3(x + h, 1, 1), i));
                }
                for (int i = 0; i < nb; i++)
                {
                    double x = rng.NextDouble() * 200 - 100;
                    double h = 0.2 + rng.NextDouble() * 5;
                    b.Add(new CombatManager.PartAabb(
                        new DVector3(x - h, -1, -1), new DVector3(x + h, 1, 1), i));
                }
                double delta = rng.NextDouble() * 220 - 110;
                bool expected = false;
                for (int i = 0; i < na && !expected; i++)
                    for (int j = 0; j < nb; j++)
                    {
                        double bMin = b[j].Min.x + Math.Min(0.0, delta);
                        double bMax = b[j].Max.x + Math.Max(0.0, delta);
                        if (a[i].Min.x <= bMax && bMin <= a[i].Max.x)
                        {
                            expected = true;
                            break;
                        }
                    }

                var oa = AxisOrder(a, 0);
                var ob = AxisOrder(b, 0);
                Assert.Equal(expected,
                    CombatManager.HasSweptAxisOverlap(a, b, oa, ob, 0, delta));
            }
        }

        [Fact]
        public void PartsOverlapSweep_WorstCase_Cost()
        {
            foreach (int n in new[] { 64, 128, 256 })
            {
                var a = new List<CombatManager.PartAabb>(n);
                var b = new List<CombatManager.PartAabb>(n);
                for (int i = 0; i < n; i++)
                {
                    double x = i * 10.0;
                    a.Add(new CombatManager.PartAabb(
                        new DVector3(x - 1, -1, -1), new DVector3(x + 1, 1, 1), i));
                    b.Add(new CombatManager.PartAabb(
                        new DVector3(x + 5 - 1, -1, -1), new DVector3(x + 5 + 1, 1, 1), i));
                }
                var oa = AxisOrder(a, 0);
                var ob = AxisOrder(b, 0);
                Assert.False(PartsOverlapWorst(a, b));
                Assert.False(CombatManager.PartsOverlapSweep(a, b, oa, ob, 0));

                int reps = n <= 64 ? 20000 : 2000;
                var sw = Stopwatch.StartNew();
                for (int r = 0; r < reps; r++)
                    CombatManager.PartsOverlapSweep(a, b, oa, ob, 0);
                sw.Stop();
                double us = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;
                _out.WriteLine($"[方案验证] PartsOverlapSweep 最坏 {n}×{n} = {us:F2} µs/次；64 次采样 ≈ {us * 64:F1} µs");
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  4. OBB 窄相占比（P2 SAT 轴预计算值不值得做）
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public void ObbNarrowphase_Cost()
        {
            foreach (int n in new[] { 32, 64, 128 })
            {
                var aAabb = new List<CombatManager.PartAabb>(n);
                var bAabb = new List<CombatManager.PartAabb>(n);
                var aObb = new List<CombatManager.PartAabb>(n);
                var bObb = new List<CombatManager.PartAabb>(n);
                for (int i = 0; i < n; i++)
                {
                    // 全部挤在 0..8m 的密集团里：AABB 大量重叠，SAP 候选多。
                    double x = (i % 4) * 1.5;
                    double y = ((i / 4) % 4) * 1.5;
                    double z = (i / 16) * 1.5;
                    var center = new DVector3(x, y, z);
                    var half = new DVector3(0.8, 0.8, 0.8);
                    aAabb.Add(new CombatManager.PartAabb(center - half, center + half, i));
                    bAabb.Add(new CombatManager.PartAabb(center + new DVector3(0.4, 0.2, 0.1) - half,
                        center + new DVector3(0.4, 0.2, 0.1) + half, i));
                    var ra = DQuaternion.AngleAxis(0.2 + i * 0.03, new DVector3(0.2, 0.5, 0.84).normalized);
                    var rb = DQuaternion.AngleAxis(0.5 + i * 0.02, new DVector3(0.7, 0.2, 0.68).normalized);
                    aObb.Add(new CombatManager.PartAabb(center, half, ra, i));
                    bObb.Add(new CombatManager.PartAabb(center + new DVector3(0.4, 0.2, 0.1), half, rb, i));
                }

                var hugeMin = new DVector3(-1e300, -1e300, -1e300);
                var hugeMax = new DVector3(1e300, 1e300, 1e300);
                for (int w = 0; w < 5; w++)
                {
                    CombatManager.TryFindDeepestPartPair(aAabb, bAabb, hugeMin, hugeMax, out _, out _, out _, forceSweep: true);
                    CombatManager.TryFindDeepestPartPair(aObb, bObb, hugeMin, hugeMax, out _, out _, out _, forceSweep: true);
                }

                const int reps = 200;
                var sw = Stopwatch.StartNew();
                for (int r = 0; r < reps; r++)
                    CombatManager.TryFindDeepestPartPair(aAabb, bAabb, hugeMin, hugeMax, out _, out _, out _, forceSweep: true);
                sw.Stop();
                double aabbUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

                sw.Restart();
                for (int r = 0; r < reps; r++)
                    CombatManager.TryFindDeepestPartPair(aObb, bObb, hugeMin, hugeMax, out _, out _, out _, forceSweep: true);
                sw.Stop();
                double obbUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

                _out.WriteLine($"[方案验证] OBB 窄相 {n}×{n} 密集团：AABB {aabbUs:F1} µs｜OBB {obbUs:F1} µs｜×{obbUs / aabbUs:F2}");
            }
        }

        [Fact]
        public void ObbSat_AxesShare()
        {
            var ra = DQuaternion.AngleAxis(0.7, new DVector3(0.2, 0.5, 0.84).normalized);
            var rb = DQuaternion.AngleAxis(1.1, new DVector3(0.7, 0.2, 0.68).normalized);
            var half = new DVector3(1, 1, 1);
            var ca = DVector3.zero;
            var cb = new DVector3(1.2, 0.3, 0.2);

            const int warm = 1000;
            for (int i = 0; i < warm; i++)
            {
                PartGeometry.Axes(ra, out _, out _, out _);
                PartGeometry.ObbObbSat(ca, half, ra, cb, half, rb, out _, out _, out _);
            }

            const int reps = 2_000_000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < reps; i++)
                PartGeometry.Axes(ra, out _, out _, out _);
            sw.Stop();
            double axesNs = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / reps;

            sw.Restart();
            for (int i = 0; i < reps; i++)
                PartGeometry.ObbObbSat(ca, half, ra, cb, half, rb, out _, out _, out _);
            sw.Stop();
            double satNs = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / reps;

            _out.WriteLine($"[方案验证] OBB SAT：Axes {axesNs:F1} ns/次｜完整 SAT {satNs:F1} ns/对｜Axes 两根占 {axesNs * 2 / satNs * 100.0:F1}%");
        }

        // ═══════════════════════════════════════════════════════════
        //  5. 纯函数首次调用 JIT（P0 预热能覆盖多少）
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public void ColdCall_PureCollisionFunctions()
        {
            var a = new List<CombatManager.PartAabb>(256);
            var b = new List<CombatManager.PartAabb>(256);
            for (int i = 0; i < 256; i++)
            {
                double y = -i * 2.0;
                a.Add(new CombatManager.PartAabb(
                    new DVector3(-20, y - 1, -20), new DVector3(20, y + 1, 20), i));
                b.Add(new CombatManager.PartAabb(
                    new DVector3(-14, y - 1, -20), new DVector3(26, y + 1, 20), i));
            }
            var clipMin = DVector3.Max(a[0].Min, b[0].Min);
            var clipMax = DVector3.Min(a[0].Max, b[0].Max);
            var contacts = new List<CollisionContactPoint>(8);

            var sw = Stopwatch.StartNew();
            CombatManager.CollectPartContacts(a, b, clipMin, clipMax, contacts, 8);
            sw.Stop();
            double collectFirst = sw.Elapsed.TotalMilliseconds * 1000.0;
            sw.Restart();
            CombatManager.CollectPartContacts(a, b, clipMin, clipMax, contacts, 8);
            sw.Stop();
            double collectSecond = sw.Elapsed.TotalMilliseconds * 1000.0;

            sw.Restart();
            CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax, out _, out _, out _);
            sw.Stop();
            double deepestFirst = sw.Elapsed.TotalMilliseconds * 1000.0;
            sw.Restart();
            CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax, out _, out _, out _);
            sw.Stop();
            double deepestSecond = sw.Elapsed.TotalMilliseconds * 1000.0;

            var bodyA = new CollisionBodyState
            {
                Mass = 1000,
                Inertia = new InertiaTensor.InertiaResult
                {
                    HasData = true, mass = 1000,
                    p1 = 1000, p2 = 1000, p3 = 1000,
                    r00 = 1, r11 = 1, r22 = 1,
                },
                Rotation = DQuaternion.identity,
                Position = DVector3.zero,
                Velocity = new DVector3(4, 0, 0),
            };
            var bodyB = bodyA;
            bodyB.Position = new DVector3(2, 0, 0);
            bodyB.Velocity = new DVector3(-4, 0, 0);
            Span<CollisionContactPoint> one = stackalloc CollisionContactPoint[1];
            one[0] = new CollisionContactPoint
            {
                Point = new DVector3(1, 0, 0),
                Normal = DVector3.right,
                Penetration = 0.1,
            };

            sw.Restart();
            CollisionSolver.Resolve(ref bodyA, ref bodyB, one, 0.2, 0.25, 6);
            sw.Stop();
            double solverFirst = sw.Elapsed.TotalMilliseconds * 1000.0;
            sw.Restart();
            CollisionSolver.Resolve(ref bodyA, ref bodyB, one, 0.2, 0.25, 6);
            sw.Stop();
            double solverSecond = sw.Elapsed.TotalMilliseconds * 1000.0;

            double solverMax = 0.0;
            int solverMaxAt = -1;
            for (int i = 0; i < 200; i++)
            {
                var aa = bodyA; var bb = bodyB;
                sw.Restart();
                CollisionSolver.Resolve(ref aa, ref bb, one, 0.2, 0.25, 6);
                sw.Stop();
                double us = sw.Elapsed.TotalMilliseconds * 1000.0;
                if (us > solverMax) { solverMax = us; solverMaxAt = i; }
            }

            _out.WriteLine($"[方案验证] 首次调用 vs 热态：CollectPartContacts {collectFirst:F1} / {collectSecond:F1} µs｜TryFindDeepestPartPair {deepestFirst:F1} / {deepestSecond:F1} µs｜CollisionSolver {solverFirst:F1} / {solverSecond:F1} µs｜预热 200 次内最大 {solverMax:F1} µs@第{solverMaxAt}次");
        }

        // ═══════════════════════════════════════════════════════════
        //  5. 同格 256 艘：配对枚举的真实规模
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public void DenseCell_PairEnumeration_Cost()
        {
            foreach (int n in new[] { 64, 128, 256 })
            {
                using var world = new World();
                var ships = new List<SpaceEntityGroup>(n);
                for (int i = 0; i < n; i++)
                {
                    // 全部落在同一 20 km 格内且完全重合（最坏情况：C(n,2) 对全相交）
                    var s = Ship(i + 1, 10_000);
                    ships.Add(s);
                    world.Grid.AddShip(s);
                }

                // 预热
                int count = 0;
                for (int w = 0; w < 3; w++)
                {
                    count = 0;
                    foreach (var _ in world.Grid.EnumerateShipPairs()) count++;
                }

                const int reps = 20;
                var sw = Stopwatch.StartNew();
                for (int r = 0; r < reps; r++)
                {
                    count = 0;
                    foreach (var _ in world.Grid.EnumerateShipPairs()) count++;
                }
                sw.Stop();
                double us = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;
                _out.WriteLine($"[方案验证] 同格 {n} 艘：枚举 {count} 对 / {us:F1} µs（{us * 1000 / Math.Max(1, count):F1} ns/对）");
            }
        }

        [Fact]
        public void DenseCell_SingleCellFastPath_IsUniqueAndComplete()
        {
            using var world = new World();
            const int n = 256;
            // 全部同点 ⇒ 精确扫掠 AABB 两两相交，SAP 必须产出完整 C(256,2)。
            for (int i = 0; i < n; i++)
                world.Grid.AddShip(Ship(i + 1, 10_000));

            var keys = new HashSet<long>();
            int count = 0;
            foreach (var (a, b) in world.Grid.EnumerateShipPairs())
            {
                Assert.NotEqual(a.GroupId, b.GroupId);
                int min = Math.Min(a.GroupId, b.GroupId);
                int max = Math.Max(a.GroupId, b.GroupId);
                long key = ((long)min << 32) | (uint)max;
                Assert.True(keys.Add(key), $"重复对 {min}-{max}");
                count++;
            }
            Assert.Equal(n * (n - 1) / 2, count);
            Assert.Equal(count, keys.Count);
        }

        [Fact]
        public void DenseCell_NonOverlappingShips_YieldNoPairs()
        {
            using var world = new World();
            const int n = 256;
            // 同一 20km 格内、x 间距 10m 的点（零尺寸 AABB）：格内原始对 C(256,2)=32,640，
            // 但精确扫掠 AABB 两两不交 ⇒ 候选必须为 0（旧实现全送进逐对几何判定）。
            for (int i = 0; i < n; i++)
                world.Grid.AddShip(Ship(i + 1, 10_000 + i * 10.0));

            int count = 0;
            for (int w = 0; w < 3; w++)
            {
                count = 0;
                foreach (var _ in world.Grid.EnumerateShipPairs()) count++;
            }
            const int reps = 30;
            var sw = Stopwatch.StartNew();
            for (int r = 0; r < reps; r++)
            {
                count = 0;
                foreach (var _ in world.Grid.EnumerateShipPairs()) count++;
            }
            sw.Stop();
            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;
            _out.WriteLine(
                $"[方案验证] 同格 256 艘不重叠（原始对 {world.Grid.DebugRawCellPairCount()}）：" +
                $"SAP 候选 {count} 对 / {us:F1} µs");
            Assert.Equal(32640, world.Grid.DebugRawCellPairCount());
            Assert.Equal(0, count);
        }

        [Fact]
        public void DenseCell_NonOverlapping_WithMultiCellShipInSameCell_StaysFast()
        {
            using var world = new World();
            const int n = 256;
            // 同一 20km 格内的 256 艘点船，x 间距 10m、互不重叠。
            for (int i = 0; i < n; i++)
                world.Grid.AddShip(Ship(i + 1, 10_000 + i * 10.0));

            // 格内还有一艘跨格船（19,950 → 20,050，扫掠覆盖格0∪格1），
            // 但它的扫掠 AABB 远离同格群 ⇒ 混合格也必须走 SAP，不能回退 O(n²)。
            var multi = Ship(9999, 19_950);
            world.Grid.AddShip(multi);
            multi.CurrentOrbit.positionSystem = (SystemVec3)new DVector3(20_050, 0, 0);
            world.Grid.UpdateShipPosition(multi, new DVector3(19_950, 0, 0));

            Assert.Equal(1, world.Grid.DebugMultiCellShipCount());

            int count = 0;
            for (int w = 0; w < 3; w++)
            {
                count = 0;
                foreach (var _ in world.Grid.EnumerateShipPairs()) count++;
            }
            const int reps = 30;
            var sw = Stopwatch.StartNew();
            for (int r = 0; r < reps; r++)
            {
                count = 0;
                foreach (var _ in world.Grid.EnumerateShipPairs()) count++;
            }
            sw.Stop();
            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;
            _out.WriteLine(
                $"[方案验证] 同格 256 不重叠 + 格内跨格船：候选 {count} 对 / {us:F1} µs");
            Assert.Equal(0, count);
        }

        [Fact]
        public void DenseCell_WithUnrelatedMultiCellShip_StillFast()
        {
            using var world = new World();
            const int n = 256;
            for (int i = 0; i < n; i++)
                world.Grid.AddShip(Ship(i + 1, 10_000));

            // 一个跨格扫掠船（格2→格3，远离同格群）：让全单格全局快门失效，
            // 但同格群所在格仍是“格内全单格”，应走按格快速段。
            var multi = Ship(9999, 50_000);
            world.Grid.AddShip(multi);
            multi.CurrentOrbit.positionSystem = (SystemVec3)new DVector3(70_000, 0, 0);
            world.Grid.UpdateShipPosition(multi, new DVector3(50_000, 0, 0));

            int count = 0;
            for (int w = 0; w < 3; w++)
            {
                count = 0;
                foreach (var _ in world.Grid.EnumerateShipPairs()) count++;
            }
            const int reps = 30;
            var sw = Stopwatch.StartNew();
            for (int r = 0; r < reps; r++)
            {
                count = 0;
                foreach (var _ in world.Grid.EnumerateShipPairs()) count++;
            }
            sw.Stop();
            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;
            _out.WriteLine($"[方案验证] 同格 256 + 跨格船：枚举 {count} 对 / {us:F1} µs（混合路径）");
            // 跨格船在格2/3，同格群在格0：只有 32,640 个同格对。
            Assert.Equal(32640, count);
        }

        [Fact]
        public void DenseCell_FarUnrelatedMultiCellShip_CostIndependentOfDistance()
        {
            // 同一条混合路径，把“无关跨格船”从 50 km 挪到 1e9 m（约 100 万 km）：
            // 只应增加该船自己 2 个格的处理，不能随距离放大。
            (int count, double us, int multiCells) Measure(double baseMeters, bool addMulti = true)
            {
                using var world = new World();
                const int n = 256;
                for (int i = 0; i < n; i++)
                    world.Grid.AddShip(Ship(i + 1, 10_000));

                if (addMulti)
                {
                    // 初始在格内，随后跨一个格边界 ⇒ 扫掠覆盖 2 格（不是 O(距离) 的长盒）。
                    var multi = Ship(9999, baseMeters + 1_000);
                    world.Grid.AddShip(multi);
                    multi.CurrentOrbit.positionSystem =
                        (SystemVec3)new DVector3(baseMeters + 21_000, 0, 0);
                    world.Grid.UpdateShipPosition(multi, new DVector3(baseMeters + 1_000, 0, 0));
                }

                int count = 0;
                for (int w = 0; w < 3; w++)
                {
                    count = 0;
                    foreach (var _ in world.Grid.EnumerateShipPairs()) count++;
                }
                const int reps = 30;
                var sw = Stopwatch.StartNew();
                for (int r = 0; r < reps; r++)
                {
                    count = 0;
                    foreach (var _ in world.Grid.EnumerateShipPairs()) count++;
                }
                sw.Stop();
                return (count, sw.Elapsed.TotalMilliseconds * 1000.0 / reps,
                    world.Grid.DebugMultiCellShipCount());
            }

            var control = Measure(0, addMulti: false);
            var near = Measure(50_000);
            var far = Measure(1_000_000_000);

            _out.WriteLine(
                $"[方案验证] 无无关跨格船（全局快速路径）：{control.count} 对 / {control.us:F1} µs");
            _out.WriteLine(
                $"[方案验证] 无关跨格船距离 50km：{near.count} 对 / {near.us:F1} µs（多格船={near.multiCells}）");
            _out.WriteLine(
                $"[方案验证] 无关跨格船距离 1e9m：{far.count} 对 / {far.us:F1} µs（多格船={far.multiCells}）");

            Assert.Equal(32640, control.count);
            Assert.Equal(32640, near.count);
            Assert.Equal(32640, far.count);
            Assert.Equal(0, control.multiCells);
            Assert.Equal(1, near.multiCells); // 只有那艘无关船跨格
            Assert.Equal(1, far.multiCells);
        }

        [Fact]
        public void DenseCell_ArrayNested_Cost()
        {
            foreach (int n in new[] { 64, 128, 256 })
            {
                var ids = new int[n];
                for (int i = 0; i < n; i++) ids[i] = i + 1;

                const int reps = 200;
                int count = 0;
                var sw = Stopwatch.StartNew();
                for (int r = 0; r < reps; r++)
                {
                    count = 0;
                    for (int i = 0; i < n; i++)
                        for (int j = i + 1; j < n; j++)
                            count++;
                }
                sw.Stop();
                double plainUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

                var set = new HashSet<long>();
                sw.Restart();
                for (int r = 0; r < reps; r++)
                {
                    set.Clear();
                    count = 0;
                    for (int i = 0; i < n; i++)
                        for (int j = i + 1; j < n; j++)
                        {
                            long key = ((long)ids[i] << 32) | (uint)ids[j];
                            if (set.Add(key)) count++;
                        }
                }
                sw.Stop();
                double setUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

                _out.WriteLine($"[方案验证] 同格 {n} 艘纯数组：{count} 对｜裸双重循环 {plainUs:F1} µs｜+HashSet {setUs:F1} µs");
            }
        }
    }
}
