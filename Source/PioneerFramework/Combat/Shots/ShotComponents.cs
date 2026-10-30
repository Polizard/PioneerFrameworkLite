using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.Combat
{
    public class ShotCompProperties_Ballistic : ShotCompProperties
    {
        public float referenceSpeed = 5000;
        public float massKg = 0.5f;
        public float lifetimeSeconds = 60;
        public bool affectedByGravity;
        public override bool ProvidesPropagation => true;
        public override ShotComp CreateComp() => new Ballistic(this);
        public override IEnumerable<string> ConfigErrors()
        {
            if (!ShotMath.Positive(referenceSpeed) || !ShotMath.Positive(massKg) || !ShotMath.Positive(lifetimeSeconds))
                yield return "ballistic speed, mass and lifetime must be finite and positive";
        }
        private sealed class Ballistic(ShotCompProperties_Ballistic p) : ShotComp
        {
            public override void Configure(ShotProfile profile)
            { profile.Speed = p.referenceSpeed; profile.MassKg = p.massKg; profile.LifetimeSeconds = p.lifetimeSeconds; profile.Gravity = p.affectedByGravity; }
        }
    }

    public class ShotCompProperties_InstantRay : ShotCompProperties
    {
        public float rangeMeters = 50000;
        public override bool ProvidesPropagation => true;
        public override ShotComp CreateComp() => new Ray(this);
        public override IEnumerable<string> ConfigErrors()
        { if (!ShotMath.Positive(rangeMeters)) yield return "ray range must be finite and positive"; }
        private sealed class Ray(ShotCompProperties_InstantRay p) : ShotComp
        {
            public override void Configure(ShotProfile profile) { profile.Instant = true; profile.RangeMeters = p.rangeMeters; }
        }
    }

    public class ShotCompProperties_DirectDamage : ShotCompProperties
    {
        public DamageDef damageDef = null!;
        public float partDamage = 40;
        public float mapDamage = 40;
        public float armorPenetration = 0.3f;
        public float referenceDistance;
        public float falloffExponent;
        public override ShotComp CreateComp() => new Direct(this);
        public override IEnumerable<string> ConfigErrors()
        {
            if (damageDef == null && mapDamage > 0) yield return "direct map damage requires damageDef";
            if (!ShotMath.Nonnegative(partDamage) || !ShotMath.Nonnegative(mapDamage) || !ShotMath.Nonnegative(armorPenetration) ||
                !ShotMath.Nonnegative(referenceDistance) || !ShotMath.Nonnegative(falloffExponent)) yield return "invalid direct damage parameters";
            if (falloffExponent > 0 && referenceDistance <= 0) yield return "distance falloff requires a positive referenceDistance";
        }
        private sealed class Direct(ShotCompProperties_DirectDamage p) : ShotComp
        {
            public override void OnContact(ShotHitContext context)
            {
                float scale = context.Shot.DamageScale;
                if (p.falloffExponent > 0 && context.DistanceMeters > p.referenceDistance)
                    scale *= (float)System.Math.Pow(p.referenceDistance / context.DistanceMeters, p.falloffExponent);
                context.ApplyDirect(p.damageDef, p.partDamage * scale, p.mapDamage * scale, p.armorPenetration);
            }
        }
    }

    public class ShotCompProperties_Penetration : ShotCompProperties
    {
        public override string? ExclusiveSlot => "penetration";
        public float initialBudget = 100;
        public float defaultResistance = 25;
        public StatDef? resistanceStat;
        public float damageRetention = 0.75f;
        public float minimumDamageScale = 0.05f;
        public int maxHits = 8;
        public override ShotComp CreateComp() => new Penetration(this);
        public override IEnumerable<string> ConfigErrors()
        {
            if (!ShotMath.Positive(initialBudget) || !ShotMath.Positive(defaultResistance) || !ShotMath.Positive(damageRetention) ||
                damageRetention > 1 || !ShotMath.Positive(minimumDamageScale) || minimumDamageScale > 1 || maxHits < 1)
                yield return "invalid penetration budget, resistance, retention or maxHits";
        }
        private sealed class Penetration(ShotCompProperties_Penetration p) : ShotComp
        {
            private float budget = p.initialBudget;
            private int hits;
            public override bool ProvidesPenetration => true;
            public override float ResistanceFor(Thing? thing, SpacePart? part) =>
                thing != null && p.resistanceStat != null ? thing.GetStatValue(p.resistanceStat) : p.defaultResistance;
            public override bool TryPenetrate(float resistance, ref float scale)
            {
                if (!ShotMath.Positive(resistance)) return false;
                budget -= resistance;
                hits++;
                float retained = scale * p.damageRetention;
                bool pass = budget > 0 && hits < p.maxHits && retained >= p.minimumDamageScale;
                if (pass) scale = retained;
                return pass;
            }
        }
    }

    public enum ShotTrigger { Contact, Stop }

    public class ShotCompProperties_Explosion : ShotCompProperties
    {
        public ShotTrigger trigger = ShotTrigger.Stop;
        public DamageDef damageDef = null!;
        public float mapRadiusCells = 4;
        public float mapCenterDamage = 60;
        public float spaceRadiusMeters;
        public float partCenterDamage;
        public float armorPenetration;
        public float falloffExponent = 1;
        public bool affectBuildings = true;
        public bool affectPawns = true;
        public override ShotComp CreateComp() => new Explosion(this);
        public override IEnumerable<string> ConfigErrors()
        {
            if (!System.Enum.IsDefined(trigger)) yield return "invalid explosion trigger";
            if (damageDef == null && mapCenterDamage > 0) yield return "map explosion requires damageDef";
            if (!ShotMath.Nonnegative(mapRadiusCells) || !ShotMath.Nonnegative(mapCenterDamage) || !ShotMath.Nonnegative(spaceRadiusMeters) ||
                !ShotMath.Nonnegative(partCenterDamage) || !ShotMath.Nonnegative(armorPenetration) || !ShotMath.Positive(falloffExponent))
                yield return "invalid explosion parameters";
            if (mapCenterDamage > 0 && mapRadiusCells <= 0 || partCenterDamage > 0 && spaceRadiusMeters <= 0)
                yield return "positive explosion damage requires positive radius";
        }
        private sealed class Explosion(ShotCompProperties_Explosion p) : ShotComp
        {
            public override void OnContact(ShotHitContext context) { if (p.trigger == ShotTrigger.Contact) Emit(context); }
            public override void OnStop(ShotHitContext context) { if (p.trigger == ShotTrigger.Stop) Emit(context); }
            private void Emit(ShotHitContext context) => context.ApplyExplosion(p, context.Shot.DamageScale);
        }
    }

    public class ShotCompProperties_BeamVisual : ShotCompProperties
    {
        public Color color = new(1, 0.3f, 0.1f);
        public float widthMeters = 0.1f;
        public float durationSeconds = 0.15f;
        public float minimumViewWidth = 0.15f;
        public ThingDef? mapVisualProjectile;
        public override ShotComp CreateComp() => new Visual(this);
        public override IEnumerable<string> ConfigErrors()
        {
            if (!ShotMath.Positive(widthMeters) || !ShotMath.Positive(durationSeconds) || !ShotMath.Nonnegative(minimumViewWidth) ||
                !ShotMath.Nonnegative(color.r) || !ShotMath.Nonnegative(color.g) || !ShotMath.Nonnegative(color.b) || !ShotMath.Nonnegative(color.a))
                yield return "invalid visual width, duration or color";
            if (mapVisualProjectile != null && (mapVisualProjectile.isSaveable || mapVisualProjectile.thingClass == null ||
                !typeof(PFProjectile3D).IsAssignableFrom(mapVisualProjectile.thingClass)))
                yield return "mapVisualProjectile must be an unsaveable PFProjectile3D";
        }
        private sealed class Visual(ShotCompProperties_BeamVisual p) : ShotComp
        {
            public override void Configure(ShotProfile profile)
            { profile.HasVisual = true; profile.Color = p.color; profile.WidthMeters = p.widthMeters; profile.VisualSeconds = p.durationSeconds;
                profile.MinimumViewWidth = p.minimumViewWidth; profile.MapVisualProjectile = p.mapVisualProjectile; }
        }
    }
}
