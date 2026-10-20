using System.Collections.Generic;
using System;
using PioneerFramework;
using PioneerFramework.Combat;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>第二刀：多接触点收集的纯台架（生产尚未接线）。</summary>
    public class CollisionManifoldTests
    {
        private static CombatManager.PartAabb Box(double cx, double cy, double cz, double half = 1.0, int order = 0)
            => new(
                new DVector3(cx - half, cy - half, cz - half),
                new DVector3(cx + half, cy + half, cz + half),
                order);

        private static (DVector3 min, DVector3 max) Clip(List<CombatManager.PartAabb> a, List<CombatManager.PartAabb> b)
        {
            DVector3 amin = a[0].Min, amax = a[0].Max, bmin = b[0].Min, bmax = b[0].Max;
            for (int i = 1; i < a.Count; i++) { amin = DVector3.Min(amin, a[i].Min); amax = DVector3.Max(amax, a[i].Max); }
            for (int i = 1; i < b.Count; i++) { bmin = DVector3.Min(bmin, b[i].Min); bmax = DVector3.Max(bmax, b[i].Max); }
            return (DVector3.Max(amin, bmin), DVector3.Min(amax, bmax));
        }

        [Fact]
        public void Collect_AabbPair_ProducesContactWithNormalAndDepth()
        {
            var a = new List<CombatManager.PartAabb> { Box(0, 0, 0) };
            var b = new List<CombatManager.PartAabb> { Box(1.5, 0, 0) };
            var (clipMin, clipMax) = Clip(a, b);
            var contacts = new List<CollisionContactPoint>();

            CombatManager.CollectPartContacts(a, b, clipMin, clipMax, contacts, maxContacts: 8);

            Assert.Single(contacts);
            Assert.Equal(0.5, contacts[0].Penetration, 9);
            Assert.True(contacts[0].Normal.x > 0.99);
        }

        [Fact]
        public void Collect_LongStack_ReturnsMultipleContacts()
        {
            var a = new List<CombatManager.PartAabb>();
            var b = new List<CombatManager.PartAabb>();
            for (int i = 0; i < 5; i++)
            {
                a.Add(Box(0, i * 2.0, 0, 1.0, i));
                b.Add(Box(1.5, i * 2.0, 0, 1.0, i));
            }
            var (clipMin, clipMax) = Clip(a, b);
            var contacts = new List<CollisionContactPoint>();

            CombatManager.CollectPartContacts(a, b, clipMin, clipMax, contacts, maxContacts: 8);

            Assert.True(contacts.Count >= 3);
            Assert.True(contacts.Count <= 8);
        }

        [Fact]
        public void Collect_CapKeepsDeepestContacts()
        {
            var a = new List<CombatManager.PartAabb>();
            var b = new List<CombatManager.PartAabb>();
            for (int i = 0; i < 12; i++)
            {
                a.Add(Box(0, i * 2.0, 0, 1.0, i));
                // 越靠后的接触越深（0.1..1.1）
                b.Add(Box(1.9 - i * 0.1, i * 2.0, 0, 1.0, i));
            }
            var (clipMin, clipMax) = Clip(a, b);
            var contacts = new List<CollisionContactPoint>();

            CombatManager.CollectPartContacts(a, b, clipMin, clipMax, contacts, maxContacts: 4);

            Assert.Equal(4, contacts.Count);
            double min = double.MaxValue;
            for (int i = 0; i < contacts.Count; i++) min = Math.Min(min, contacts[i].Penetration);
            Assert.True(min >= 0.7);   // 只保留最深的一批
        }

        [Fact]
        public void Collect_ContainsDeepestPair()
        {
            var a = new List<CombatManager.PartAabb> { Box(0, 0, 0, 1.0), Box(0, 3, 0, 1.0) };
            var b = new List<CombatManager.PartAabb> { Box(0.5, 0, 0, 1.0), Box(0.2, 3, 0, 1.0) };
            var (clipMin, clipMax) = Clip(a, b);
            var contacts = new List<CollisionContactPoint>();
            CombatManager.CollectPartContacts(a, b, clipMin, clipMax, contacts, maxContacts: 8);

            Assert.True(CombatManager.TryFindDeepestPartPair(a, b, clipMin, clipMax,
                out _, out double deepest, out _, forceNested: true));
            double max = 0.0;
            for (int i = 0; i < contacts.Count; i++) max = Math.Max(max, contacts[i].Penetration);
            Assert.Equal(deepest, max, 9);
        }

        [Fact]
        public void ObbSat_IsSymmetric_UnderPairSwap()
        {
            var rotA = DQuaternion.AngleAxis(0.35, new DVector3(1, 1, 0).normalized);
            var rotB = DQuaternion.AngleAxis(-0.25, new DVector3(0, 1, 1).normalized);
            var a = new CombatManager.PartAabb(new DVector3(0, 0, 0), new DVector3(2, 1, 1), rotA, 0);
            var b = new CombatManager.PartAabb(new DVector3(1.6, 0.2, 0.1), new DVector3(1, 1, 2), rotB, 1);

            bool hitAB = PartGeometry.ObbObbSat(a.Center, a.Half, a.Rotation,
                b.Center, b.Half, b.Rotation, out DVector3 nAB, out double dAB, out _);
            bool hitBA = PartGeometry.ObbObbSat(b.Center, b.Half, b.Rotation,
                a.Center, a.Half, a.Rotation, out DVector3 nBA, out double dBA, out _);

            Assert.Equal(hitAB, hitBA);
            if (hitAB)
            {
                Assert.Equal(dAB, dBA, 9);
                Assert.True(DVector3.Dot(nAB, nBA) < -0.999);
            }
        }

        [Fact]
        public void SphereSphere_UsesAnalyticContact()
        {
            var a = new CombatManager.PartAabb(new DVector3(0, 0, 0), new DVector3(0.5, 0.5, 0.5),
                DQuaternion.identity, 0, InertiaTensor.Shape.Sphere);
            var b = new CombatManager.PartAabb(new DVector3(0.8, 0, 0), new DVector3(0.5, 0.5, 0.5),
                DQuaternion.identity, 1, InertiaTensor.Shape.Sphere);
            var boxesA = new List<CombatManager.PartAabb> { a };
            var boxesB = new List<CombatManager.PartAabb> { b };
            var (clipMin, clipMax) = Clip(boxesA, boxesB);
            var contacts = new List<CollisionContactPoint>();

            CombatManager.CollectPartContacts(boxesA, boxesB, clipMin, clipMax, contacts);

            Assert.Single(contacts);
            Assert.Equal(0.2, contacts[0].Penetration, 9);
            Assert.True(contacts[0].Normal.x > 0.99);
            Assert.Equal(0.5, contacts[0].Point.x, 9);
        }

        [Fact]
        public void SphereInsideBox_UsesNearestFaceNormal()
        {
            var sphere = new CombatManager.PartAabb(new DVector3(0.8, 0, 0), new DVector3(0.5, 0.5, 0.5),
                DQuaternion.identity, 0, InertiaTensor.Shape.Sphere);
            var box = new CombatManager.PartAabb(new DVector3(0, 0, 0), new DVector3(1, 1, 1),
                DQuaternion.identity, 1, InertiaTensor.Shape.Box);
            var boxesA = new List<CombatManager.PartAabb> { sphere };
            var boxesB = new List<CombatManager.PartAabb> { box };
            var (clipMin, clipMax) = Clip(boxesA, boxesB);
            var contacts = new List<CollisionContactPoint>();

            CombatManager.CollectPartContacts(boxesA, boxesB, clipMin, clipMax, contacts);

            Assert.Single(contacts);
            Assert.Equal(0.7, contacts[0].Penetration, 9);
            Assert.True(contacts[0].Normal.x < -0.99);   // sphere→box 指向盒心
        }

        [Fact]
        public void BoxSphereSwap_FlipsNormal()
        {
            var sphere = new CombatManager.PartAabb(new DVector3(1.2, 0, 0), new DVector3(0.5, 0.5, 0.5),
                DQuaternion.identity, 0, InertiaTensor.Shape.Sphere);
            var box = new CombatManager.PartAabb(new DVector3(0, 0, 0), new DVector3(1, 1, 1),
                DQuaternion.identity, 1, InertiaTensor.Shape.Box);
            var sa = new List<CombatManager.PartAabb> { sphere };
            var sb = new List<CombatManager.PartAabb> { box };
            var ba = new List<CombatManager.PartAabb> { box };
            var bb = new List<CombatManager.PartAabb> { sphere };
            var (c1min, c1max) = Clip(sa, sb);
            var (c2min, c2max) = Clip(ba, bb);
            var c1 = new List<CollisionContactPoint>();
            var c2 = new List<CollisionContactPoint>();

            CombatManager.CollectPartContacts(sa, sb, c1min, c1max, c1);
            CombatManager.CollectPartContacts(ba, bb, c2min, c2max, c2);

            Assert.Single(c1);
            Assert.Single(c2);
            Assert.True(DVector3.Dot(c1[0].Normal, c2[0].Normal) < -0.99);
        }

        [Fact]
        public void CylinderCylinder_UsesCapsuleApproximation()
        {
            var a = new CombatManager.PartAabb(new DVector3(0, 0, 0), new DVector3(1, 2, 1),
                DQuaternion.identity, 0, InertiaTensor.Shape.Cylinder);
            var b = new CombatManager.PartAabb(new DVector3(1.5, 0, 0), new DVector3(1, 2, 1),
                DQuaternion.identity, 1, InertiaTensor.Shape.Cylinder);
            var boxesA = new List<CombatManager.PartAabb> { a };
            var boxesB = new List<CombatManager.PartAabb> { b };
            var (clipMin, clipMax) = Clip(boxesA, boxesB);
            var contacts = new List<CollisionContactPoint>();

            CombatManager.CollectPartContacts(boxesA, boxesB, clipMin, clipMax, contacts);

            Assert.Single(contacts);
            Assert.Equal(0.5, contacts[0].Penetration, 9);
            Assert.True(contacts[0].Normal.x > 0.99);
        }

        [Fact]
        public void SphereCylinder_UsesCapsuleApproximation()
        {
            var sphere = new CombatManager.PartAabb(new DVector3(1.2, 0, 0), new DVector3(0.5, 0.5, 0.5),
                DQuaternion.identity, 0, InertiaTensor.Shape.Sphere);
            var cyl = new CombatManager.PartAabb(new DVector3(0, 0, 0), new DVector3(1, 2, 1),
                DQuaternion.identity, 1, InertiaTensor.Shape.Cylinder);
            var boxesA = new List<CombatManager.PartAabb> { sphere };
            var boxesB = new List<CombatManager.PartAabb> { cyl };
            var (clipMin, clipMax) = Clip(boxesA, boxesB);
            var contacts = new List<CollisionContactPoint>();

            CombatManager.CollectPartContacts(boxesA, boxesB, clipMin, clipMax, contacts);

            Assert.Single(contacts);
            Assert.Equal(0.3, contacts[0].Penetration, 9);
            Assert.True(contacts[0].Normal.x < -0.99);   // sphere → cylinder
        }

        [Fact]
        public void CylinderBox_UsesSampledCapsuleContact()
        {
            var cyl = new CombatManager.PartAabb(new DVector3(0, 0, 0), new DVector3(1, 2, 1),
                DQuaternion.identity, 0, InertiaTensor.Shape.Cylinder);
            var box = new CombatManager.PartAabb(new DVector3(1.5, 0, 0), new DVector3(1, 1, 1),
                DQuaternion.identity, 1, InertiaTensor.Shape.Box);
            var boxesA = new List<CombatManager.PartAabb> { cyl };
            var boxesB = new List<CombatManager.PartAabb> { box };
            var (clipMin, clipMax) = Clip(boxesA, boxesB);
            var contacts = new List<CollisionContactPoint>();

            CombatManager.CollectPartContacts(boxesA, boxesB, clipMin, clipMax, contacts);

            Assert.Single(contacts);
            Assert.Equal(0.5, contacts[0].Penetration, 9);
            Assert.True(contacts[0].Normal.x > 0.99);   // cylinder → box
        }
    }
}
