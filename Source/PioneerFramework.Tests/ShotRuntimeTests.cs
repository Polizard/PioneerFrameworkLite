using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Combat;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    public class ShotRuntimeTests
    {
        internal static ShotDef Ballistic(params ShotCompProperties[] effects)
        {
            var d = new ShotDef { defName = "TestShot", label = "test shot" };
            d.comps.Add(new ShotCompProperties_Ballistic { referenceSpeed = 1000, lifetimeSeconds = 10 });
            d.comps.AddRange(effects);
            return d;
        }

        [Fact]
        public void MuzzleSpeedIncludesBothFactorsAndEachLaunchOwnsItsPenetrationState()
        {
            var definition = Ballistic(new ShotCompProperties_Penetration { initialBudget = 50, maxHits = 5 });
            Assert.True(definition.TryCreateRuntime(1.2f, 0.5f, out var a, out var error), error);
            Assert.True(definition.TryCreateRuntime(1, 1, out var b, out error), error);
            Assert.Equal(600, a.Profile.Speed, 4);
            Assert.True(a.TryPenetrate(30));
            Assert.False(a.TryPenetrate(30));
            Assert.Equal(1, b.DamageScale);
            Assert.True(b.TryPenetrate(30));
            Assert.Equal(0.75f, b.DamageScale);
        }

        [Fact]
        public void ThirdPartyCompFactoryRunsWithoutAddingBuiltInTypeOrNameSwitch()
        {
            var extension = new ExtraProperties();
            var definition = Ballistic(extension);
            Assert.True(definition.TryCreateRuntime(1, 1, out var shot, out var error), error);
            var component = Assert.IsType<ExtraComp>(shot.Components[1]);
            var hit = new ShotHitContext { Shot = shot };
            shot.Contact(hit);
            shot.Stop(hit);
            shot.Stop(hit);
            shot.Contact(hit);
            Assert.Equal(1, component.Contacts);
            Assert.Equal(1, component.Stops);
        }

        [Fact]
        public void DirectAndStopExplosionAreIndependentAndPreserveXmlDamageType()
        {
            var damageDef = new DamageDef { defName = "TestDamage" };
            var definition = Ballistic(new ShotCompProperties_DirectDamage { damageDef = damageDef, partDamage = 12, mapDamage = 9, armorPenetration = 0.4f },
                new ShotCompProperties_Explosion { damageDef = damageDef, mapRadiusCells = 3, mapCenterDamage = 25 });
            Assert.True(definition.TryCreateRuntime(1, 1, out var shot, out var error), error);
            int direct = 0, blasts = 0;
            var hit = new ShotHitContext
            {
                Shot = shot,
                ApplyDirect = (d, part, map, ap) =>
                { Assert.Same(damageDef, d); Assert.Equal(12, part); Assert.Equal(9, map); Assert.Equal(0.4f, ap); direct++; },
                ApplyExplosion = (p, scale) => { Assert.Equal(3, p.mapRadiusCells); Assert.Equal(1, scale); blasts++; }
            };
            shot.Contact(hit);
            Assert.Equal(0, blasts);
            shot.Stop(hit);
            shot.Stop(hit);
            Assert.Equal(1, direct);
            Assert.Equal(1, blasts);
        }

        [Fact]
        public void InvalidPropagationAndNonFiniteParametersFailBeforeRuntimeCreation()
        {
            var definition = Ballistic(new ShotCompProperties_InstantRay());
            Assert.False(definition.TryCreateRuntime(1, 1, out _, out _));
            definition = Ballistic();
            ((ShotCompProperties_Ballistic)definition.comps[0]).referenceSpeed = float.NaN;
            Assert.False(definition.TryCreateRuntime(1, 1, out _, out _));
            definition = Ballistic();
            Assert.False(definition.TryCreateRuntime(1, -1, out _, out _));
        }

        [Fact]
        public void MapVisualRequiresAnUnsaveableVisualProjectile()
        {
            var visual = new ThingDef { thingClass = typeof(PFProjectile3D), isSaveable = true };
            var properties = new ShotCompProperties_BeamVisual { mapVisualProjectile = visual };
            Assert.NotEmpty(properties.ConfigErrors());
            visual.isSaveable = false;
            Assert.Empty(properties.ConfigErrors());
            var definition = Ballistic(properties);
            Assert.True(definition.TryCreateRuntime(1, 1, out var runtime, out var error), error);
            Assert.True(runtime.Profile.HasVisual);
            Assert.Equal(properties.durationSeconds, runtime.Profile.VisualSeconds);
            Assert.Equal(properties.widthMeters, runtime.Profile.WidthMeters);
        }

        [Fact]
        public void InstantRayHasNoBallisticSpeedOrGravityDependency()
        {
            var definition = new ShotDef { defName = "Ray", label = "ray", comps = new() { new ShotCompProperties_InstantRay { rangeMeters = 2500 } } };
            Assert.True(definition.TryCreateRuntime(0, 0, out var shot, out var error), error);
            Assert.True(shot.Profile.Instant);
            Assert.Equal(2500, shot.Profile.RangeMeters);
            Assert.Equal(0, shot.Profile.Speed);
        }

        public sealed class ExtraProperties : ShotCompProperties
        {
            public override ShotComp CreateComp() => new ExtraComp();
        }
        public sealed class ExtraComp : ShotComp
        {
            public int Contacts, Stops;
            public override void OnContact(ShotHitContext hit) => Contacts++;
            public override void OnStop(ShotHitContext hit) => Stops++;
        }
    }
}
