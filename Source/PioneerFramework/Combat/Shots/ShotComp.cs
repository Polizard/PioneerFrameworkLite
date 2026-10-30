using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace PioneerFramework.Combat
{
    /// <summary>Custom space-simulation component. Has no Thing parent and is never driven by CompTick.</summary>
    public abstract class ShotComp
    {
        public virtual void Configure(ShotProfile profile) { }
        public virtual void OnContact(ShotHitContext context) { }
        public virtual void OnStop(ShotHitContext context) { }
        public virtual bool ProvidesPenetration => false;
        public virtual float ResistanceFor(Thing? thing, SpacePart? part) => 1;
        public virtual bool TryPenetrate(float resistance, ref float damageScale) => false;
    }

    public sealed class ShotProfile
    {
        public bool Instant;
        public double Speed;
        public float MassKg;
        public float LifetimeSeconds;
        public bool Gravity;
        public double RangeMeters;
        public Color Color = new(1, 0.3f, 0.1f);
        public float WidthMeters = 0.1f;
        public float VisualSeconds = 1;
        public bool HasVisual;
        public ThingDef? MapVisualProjectile;
        public float MinimumViewWidth = 0.15f;
    }

    public sealed class ShotRuntime
    {
        public ShotDef Definition { get; }
        public ShotProfile Profile { get; } = new();
        public float DamageScale = 1;
        public bool Stopped { get; private set; }
        public Thing? Instigator;
        public ThingDef? WeaponThingDef;
        public readonly HashSet<Thing> HitThings = new();
        public readonly HashSet<SpacePart> HitParts = new();
        private readonly List<ShotComp> components = new();
        public IReadOnlyList<ShotComp> Components => components;
        internal ShotRuntime(ShotDef definition) { Definition = definition; }
        internal void Add(ShotComp component) { components.Add(component); component.Configure(Profile); }

        public void Contact(ShotHitContext context)
        {
            if (Stopped) return;
            foreach (var component in components) component.OnContact(context);
        }

        public void Stop(ShotHitContext context)
        {
            if (Stopped) return;
            Stopped = true;
            foreach (var component in components) component.OnStop(context);
        }

        public bool TryPenetrate(float resistance)
        {
            if (Stopped) return false;
            foreach (var component in components)
                if (component.ProvidesPenetration) return component.TryPenetrate(resistance, ref DamageScale);
            return false;
        }

        public bool TryPenetrate(Thing? thing, SpacePart? part)
        {
            foreach (var component in components)
                if (component.ProvidesPenetration)
                    return !Stopped && component.TryPenetrate(component.ResistanceFor(thing, part), ref DamageScale);
            return false;
        }
    }

    public sealed class ShotHitContext
    {
        public ShotRuntime Shot = null!;
        public double DistanceMeters;
        public Action<DamageDef?, float, float, float> ApplyDirect = null!;
        public Action<ShotCompProperties_Explosion, float> ApplyExplosion = null!;
    }
}
