using System;
using System.Collections.Generic;
using System.Diagnostics;
using PioneerFramework;
using PioneerFramework.Combat;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>多接触收集/求解成本台架（2026-10-19）：只出读数，不判 PASS/FAIL。</summary>
    public class CollisionContactBench
    {
        private readonly ITestOutputHelper _out;
        public CollisionContactBench(ITestOutputHelper output) => _out = output;

        [Fact]
        public void CollectContacts_256x256_Cost()
        {
            var a = new List<CombatManager.PartAabb>();
            var b = new List<CombatManager.PartAabb>();
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
            for (int i = 1; i < a.Count; i++)
            {
                clipMin = DVector3.Min(clipMin, a[i].Min);
                clipMax = DVector3.Max(clipMax, a[i].Max);
            }
            var contacts = new List<CollisionContactPoint>(8);

            for (int w = 0; w < 5; w++) CombatManager.CollectPartContacts(a, b, clipMin, clipMax, contacts, 8);
            var sw = Stopwatch.StartNew();
            const int reps = 200;
            for (int i = 0; i < reps; i++)
                CombatManager.CollectPartContacts(a, b, clipMin, clipMax, contacts, 8);
            sw.Stop();
            double collectUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

            for (int w = 0; w < 5; w++)
                CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax, out _, out _, out _);
            sw.Restart();
            for (int i = 0; i < reps; i++)
                CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax, out _, out _, out _);
            sw.Stop();
            double deepestUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

            for (int w = 0; w < 5; w++)
                CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax, out _, out _, out _, forceSweep: true);
            sw.Restart();
            for (int i = 0; i < reps; i++)
                CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax, out _, out _, out _, forceSweep: true);
            sw.Stop();
            double sapUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

            _out.WriteLine($"[碰撞接触成本] 256×256：CollectPartContacts = {collectUs:F1} µs；最深对(生产/有序双指针) = {deepestUs:F1} µs；最深对(SAP) = {sapUs:F1} µs；contacts={contacts.Count}");
        }

        [Fact]
        public void Solver_8Contacts_6Iterations_Cost()
        {
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
            {
                contacts[i] = new CollisionContactPoint
                {
                    Point = new DVector3(1, i * 0.2 - 0.7, 0),
                    Normal = DVector3.right,
                    Penetration = 0.1,
                };
            }

            for (int w = 0; w < 5; w++) CollisionSolver.Resolve(ref a, ref b, contacts, 0.2, 0.25, 6);
            var sw = Stopwatch.StartNew();
            const int reps = 10000;
            for (int i = 0; i < reps; i++)
            {
                var aa = a;
                var bb = b;
                CollisionSolver.Resolve(ref aa, ref bb, contacts, 0.2, 0.25, 6);
            }
            sw.Stop();
            _out.WriteLine($"[碰撞接触成本] Solver 8 接触 × 6 迭代 = {sw.Elapsed.TotalMilliseconds * 1000.0 / reps:F2} µs/次");
        }
    }
}
