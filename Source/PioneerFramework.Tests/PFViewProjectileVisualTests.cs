using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml;
using HarmonyLib;
using PioneerFramework.Combat;
using PioneerFramework.ProjectileVisuals;
using UnityEngine;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    public class PFViewProjectileVisualTests
    {
        [Fact]
        public void SharedWeaponUsesTheProfileOfTheSelectedAmmo()
        {
            var pale = Profile("Pale", 1.2f);
            var warm = Profile("Warm", 2.1f);
            var resolver = new ProjectileVisualResolver(name => name == "Pale" ? pale : warm, _ => { });
            var weapon = new WeaponDef { modExtensions = [Binding("IgnoredWeaponProfile")] };

            Assert.Equal(1.2f, resolver.Resolve(Projectile(weapon, Binding("Pale"))).Value.WidthPixels);
            Assert.Equal(2.1f, resolver.Resolve(Projectile(weapon, Binding("Warm"))).Value.WidthPixels);
        }

        [Fact]
        public void EmptyProfileAndNonFlyingShotsStaySilent()
        {
            var resolver = new ProjectileVisualResolver(_ => new ProjectileVisualDef(), _ => { });
            var weapon = new WeaponDef { modExtensions = [Binding("Silent")] };
            Assert.Null(resolver.Resolve(new ProjectileInstance { weaponDef = weapon }));
            Assert.Null(resolver.Resolve(new ProjectileInstance { isWarhead = true }));

            var ray = new ShotDef { defName = "TestInstantRay", comps = [new ShotCompProperties_InstantRay()] };
            Assert.True(ray.TryCreateRuntime(1, 1, out var shot, out var error), error);
            Assert.Null(resolver.Resolve(new ProjectileInstance { shot = shot, weaponDef = weapon }));
        }

        [Fact]
        public void InvalidReferencesWarnOnceAndFallBackToClampedLegacyHints()
        {
            int warnings = 0;
            var invalid = Profile("Invalid", float.NaN);
            var resolver = new ProjectileVisualResolver(name => name == "Invalid" ? invalid : null, _ => warnings++);
            var projectile = Projectile(new WeaponDef(), Binding("Invalid"));
            projectile.shot.Profile.HasVisual = true;
            projectile.shot.Profile.Color = Color.red;
            projectile.shot.Profile.VisualSeconds = 3f;
            var first = resolver.Resolve(projectile).Value;
            var second = resolver.Resolve(projectile).Value;
            Assert.Equal(first, second);
            Assert.Equal(Color.red, first.Color);
            Assert.InRange(first.SampleSeconds, 0.001f, 0.08f);
            Assert.Equal(1, warnings);
            Assert.NotNull(resolver.Resolve(Projectile(new WeaponDef(), Binding("Missing"))));
            Assert.Equal(2, warnings);
        }

        [Fact]
        public void BuiltInProfilesBindToDifferentAmmoAndLoadWithNativeXml()
        {
            var prefs = AccessTools.Field(typeof(Prefs), "data");
            object previousPrefs = prefs.GetValue(null);
            prefs.SetValue(null, RuntimeHelpers.GetUninitializedObject(prefs.FieldType));
            try
            {
                AssertBuiltInProfiles();
            }
            finally
            {
                prefs.SetValue(null, previousPrefs);
            }
        }

        private static void AssertBuiltInProfiles()
        {
            var profileXml = new XmlDocument();
            profileXml.Load(Path.Combine(AppContext.BaseDirectory, "Defs", "Combat", "ProjectileVisualDefs.xml"));
            var elements = profileXml.DocumentElement.ChildNodes.OfType<XmlElement>().ToArray();
            Assert.All(elements, element => Assert.Equal(typeof(ProjectileVisualDef).FullName, element.Name));
            var profiles = elements
                .Select(element => DirectXmlToObject.ObjectFromXml<ProjectileVisualDef>(element, false))
                .ToDictionary(profile => profile.defName);
            Assert.Equal(ProjectileVisualKind.Streak, Assert.Single(profiles["PF_Visual_KineticAP"].flight).kind);
            Assert.Equal(ProjectileVisualKind.Streak, Assert.Single(profiles["PF_Visual_KineticHE"].flight).kind);
            Assert.Empty(profiles["PF_Visual_LaserContact"].flight);

            var ammoXml = new XmlDocument();
            ammoXml.Load(Path.Combine(AppContext.BaseDirectory, "Patches", "PFViewProjectileVisualBindings.xml"));
            var liveShots = new XmlDocument();
            liveShots.Load(Path.Combine(AppContext.BaseDirectory, "Defs", "Combat", "ShotDefs.xml"));
            ProjectileVisualExtension ReadBinding(string ammo)
            {
                var operation = ammoXml.SelectNodes("/Patch/Operation").OfType<XmlElement>()
                    .Single(node => node.SelectSingleNode("xpath").InnerText.EndsWith($"[defName='{ammo}']", StringComparison.Ordinal));
                Assert.Single(liveShots.SelectNodes(operation.SelectSingleNode("xpath").InnerText).OfType<XmlElement>());
                var extension = (XmlElement)operation.SelectSingleNode("value/modExtensions/li");
                Assert.Equal(typeof(ProjectileVisualExtension).FullName, extension.GetAttribute("Class"));
                extension.RemoveAttribute("Class");
                return DirectXmlToObject.ObjectFromXml<ProjectileVisualExtension>(extension, false);
            }

            var resolver = new ProjectileVisualResolver(name => profiles.TryGetValue(name, out var profile) ? profile : null, _ => { });
            var weapon = new WeaponDef();
            var ap = resolver.Resolve(Projectile(weapon, ReadBinding("PF_Shot_AP"))).Value;
            var he = resolver.Resolve(Projectile(weapon, ReadBinding("PF_Shot_HE"))).Value;
            Assert.NotEqual(ap.Color, he.Color);
            Assert.True(he.WidthPixels > ap.WidthPixels);
            var laser = Projectile(weapon, ReadBinding("PF_Shot_PulseLaser")).shot.Definition;
            var hit = default(CombatImpactEvent) with { Kind = CombatImpactKind.Contact, SourceDef = laser, IsInstant = true };
            Assert.Equal(ProjectileVisualKind.Flash, Assert.Single(resolver.ResolveImpact(hit).ToArray()).Kind);
        }

        [Fact]
        public void NearPlaneClippingAndLengthCapPreserveFiniteDepth()
        {
            var camera = new ProjectileProjection(1920, 1080, 540, 1, 1000);
            var style = new ProjectileStreakStyle(Color.white, 2, 48, 0.035f, 200_000);
            Assert.True(camera.TryBuild(new Vector3(1, 0, 10), new Vector3(-20, 0, -2), style, out var quad));
            Assert.InRange(quad.TailLeft.z, 1f, quad.HeadLeft.z);
            Assert.Equal(24f, quad.HalfLengthInRadii, 3);
            Assert.False(camera.TryBuild(new Vector3(0, 0, -1), new Vector3(0, 0, -2), style, out _));
            Assert.False(camera.TryBuild(new Vector3(100, 0, 10), new Vector3(101, 0, 10), style, out _));
        }

        [Fact]
        public void CoMovingAndHeadOnProjectilesProduceARoundedDot()
        {
            var sampledTail = new DVector3(0, 0, 10);
            var head = new DVector3(5, 0, 10);
            var tail = ProjectileProjection.AdjustTailForMotion(sampledTail, DVector3.zero, new DVector3(100, 0, 0), 0.05);
            Assert.Equal(head, tail);

            var camera = new ProjectileProjection(1920, 1080, 540, 1, 1000);
            var style = new ProjectileStreakStyle(Color.white, 2, 48, 0.035f, 200_000);
            Assert.True(camera.TryBuild(new Vector3(0, 0, 10), new Vector3(0, 0, 3), style, out var quad));
            Assert.Equal(0, quad.HalfLengthInRadii);
            Assert.Equal(10, quad.TailLeft.z);
            Assert.NotEqual(quad.HeadLeft, quad.HeadRight);
        }

        [Fact]
        public void HalfResolutionDotsStillCoverAWholePixelRadius()
        {
            var camera = new ProjectileProjection(960, 540, 270, 1, 1000);
            var style = new ProjectileStreakStyle(Color.white, 1.5f, 48, 0.035f, 200_000);
            var center = new Vector3(0, 0, 10);
            Assert.True(camera.TryBuild(center, center, style, out var quad));
            float radiusInPixels = (quad.HeadRight.y - quad.HeadLeft.y) * camera.FocalPixels / (2 * center.z);
            Assert.InRange(radiusInPixels, 0.99f, 1.01f);
            Assert.InRange(quad.Coverage, 0.37f, 0.38f);
        }

        [Fact]
        public void CarrierMotionUsesTheCommittedSlopeAtTheStartOfAnInterpolationInterval()
        {
            var previous = new DVector3(100, 0, 0);
            var current = new DVector3(200, 0, 0);
            var currentVelocity = new DVector3(95, 0, 0);
            Assert.Equal(new DVector3(100, 0, 0),
                ProjectileProjection.CarrierVelocity(previous, current, currentVelocity, 1, 2, 1));
            Assert.Equal(currentVelocity,
                ProjectileProjection.CarrierVelocity(previous, current, currentVelocity, 1, 2, 3));
        }

        [Fact]
        public void InstantContactsUseOnlyAFlashAndExplicitProfilesCanReserveEffectsForDetonation()
        {
            var blast = new ProjectileVisualDef
            {
                onDetonation = [new ProjectileVisualLayer { kind = ProjectileVisualKind.Burst }]
            };
            var resolver = new ProjectileVisualResolver(_ => blast, _ => { });
            var laser = default(CombatImpactEvent) with { Kind = CombatImpactKind.Contact, IsInstant = true };
            Assert.Equal(ProjectileVisualKind.Flash, Assert.Single(resolver.ResolveImpact(laser).ToArray()).Kind);
            var explosive = new ShotDef { defName = "VisualHE", modExtensions = [Binding("Blast")] };
            Assert.True(resolver.ResolveImpact(laser with { SourceDef = explosive }).IsEmpty);
            Assert.Equal(ProjectileVisualKind.Burst,
                Assert.Single(resolver.ResolveImpact(laser with { SourceDef = explosive, Kind = CombatImpactKind.Detonation }).ToArray()).Kind);
        }

        [Fact]
        public void ImpactsWaitForTheirPhysicsTickAndAgeOnlyWhileUnpaused()
        {
            var playback = new PFViewImpactPlayback();
            playback.Enqueue(default(CombatImpactEvent) with { PhysicsTick = 4, HostTick = 10 }, visibleGroupId: 42);
            playback.Advance(3.99, 10, 0.016f, false);
            Assert.Empty(playback.Active);
            playback.Advance(3.999, 11, 0.016f, false);
            Assert.Empty(playback.Active);
            playback.Advance(4, 12, 0.016f, false);
            var active = Assert.Single(playback.Active);
            Assert.Equal(42, active.VisibleGroupId);
            double started = active.Started;
            playback.Advance(4, 12, 0.5f, true);
            Assert.Equal(started, playback.DisplaySeconds);
            for (int i = 0; i < 8; i++) playback.Advance(5, 11, 0.1f, false);
            Assert.Empty(playback.Active);

            playback.Enqueue(default(CombatImpactEvent) with { PhysicsTick = 1, HostTick = 1 });
            playback.Advance(5, 100, 0, false);
            Assert.Empty(playback.Active);
        }

        [Fact]
        public void LosingTheFollowTargetKeepsImpactsForTheFrozenCameraPose()
        {
            var tracked = new PFViewImpactPlayback.View(PFViewFocusKind.Group, 42, 1, 1, false);
            var frozen = tracked with { Kind = PFViewFocusKind.Frozen, GroupId = -1 };
            Assert.True(frozen.ContinuesFrom(tracked));
            Assert.False((tracked with { GroupId = 43 }).ContinuesFrom(tracked));
            Assert.False((frozen with { SystemId = 2 }).ContinuesFrom(tracked));
        }

        private static ProjectileVisualDef Profile(string name, float width) => new()
        {
            defName = name,
            flight = [new ProjectileVisualLayer { kind = ProjectileVisualKind.Streak, widthPixels = width }]
        };

        private static ProjectileVisualExtension Binding(string name) => new() { visualProfile = name };

        private static ProjectileInstance Projectile(WeaponDef weapon, ProjectileVisualExtension binding)
        {
            var ammo = new ShotDef
            {
                defName = "TestBallisticAmmo",
                comps = [new ShotCompProperties_Ballistic()],
                modExtensions = [binding]
            };
            Assert.True(ammo.TryCreateRuntime(1, 1, out var shot, out var error), error);
            return new ProjectileInstance { shot = shot, weaponDef = weapon };
        }
    }
}
