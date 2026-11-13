using System;
using System.Collections.Generic;
using PioneerFramework.Combat;
using UnityEngine;
using Verse;

namespace PioneerFramework.ProjectileVisuals
{
    public sealed class ProjectileVisualDef : Def
    {
        public List<ProjectileVisualLayer> flight = new();
        public List<ProjectileVisualLayer> onContact = new();
        public List<ProjectileVisualLayer> onDetonation = new();
    }

    public sealed class ProjectileVisualExtension : DefModExtension
    {
        public string visualProfile = "";
    }

    public enum ProjectileVisualKind
    {
        Streak = 1,
        Flash,
        Sparks,
        Burst
    }

    public sealed class ProjectileVisualLayer
    {
        public ProjectileVisualKind kind;
        public Color color = new(1f, 0.9f, 0.7f, 0.9f);
        public float widthPixels = 1.6f;
        public float maxLengthPixels = 48f;
        public float sampleSeconds = 0.035f;
        public float fadeDistanceMeters = 200_000f;
        public float durationSeconds = 0.10f;
        public float maxRadiusPixels = 4f;

        internal bool IsValidStreak => kind == ProjectileVisualKind.Streak &&
            widthPixels is >= 0.5f and <= 8f && maxLengthPixels is >= 4f and <= 96f &&
            sampleSeconds is >= 0.001f and <= 0.08f && fadeDistanceMeters is >= 100f and <= 5_000_000f &&
            ProjectileVisualResolver.IsValidColor(color);

        internal bool IsValidImpact(bool detonation)
            => (kind == ProjectileVisualKind.Flash ||
                (detonation ? kind == ProjectileVisualKind.Burst : kind == ProjectileVisualKind.Sparks)) &&
                durationSeconds is >= 0.02f and <= 0.6f && maxRadiusPixels is >= 1f and <= 32f &&
                ProjectileVisualResolver.IsValidColor(color);
    }

    internal readonly record struct ProjectileStreakStyle(
        Color Color, float WidthPixels, float MaxLengthPixels, float SampleSeconds, float FadeDistanceMeters);

    internal readonly record struct ProjectileImpactStyle(
        ProjectileVisualKind Kind, Color Color, float DurationSeconds, float MaxRadiusPixels);

    internal sealed class ProjectileVisualResolver(Func<string, ProjectileVisualDef?> findProfile, Action<string> warn)
    {
        private static readonly ProjectileStreakStyle DefaultStreak =
            new(new Color(1f, 0.9f, 0.7f, 0.9f), 1.6f, 48f, 0.035f, 200_000f);

        private static readonly ProjectileImpactStyle[] DefaultFlash =
            [new(ProjectileVisualKind.Flash, new Color(1f, 0.94f, 0.82f), 0.10f, 4f)];
        private static readonly ProjectileImpactStyle[] DefaultSparks =
            [new(ProjectileVisualKind.Sparks, new Color(1f, 0.8f, 0.5f), 0.13f, 6f)];
        private static readonly ProjectileImpactStyle[] DefaultBurst =
            [new(ProjectileVisualKind.Burst, new Color(1f, 0.59f, 0.25f), 0.32f, 18f)];

        private sealed record ProfileResult(ProjectileStreakStyle? Streak,
            ProjectileImpactStyle[] Contact, ProjectileImpactStyle[] Detonation);
        private readonly Dictionary<Def, ProfileResult?> profilesByOwner = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, ProfileResult?> profilesByName = new(StringComparer.Ordinal);

        internal ProjectileStreakStyle? Resolve(ProjectileInstance projectile)
        {
            var shot = projectile.shot;
            if (projectile.isWarhead || (shot?.Profile.Instant ?? (projectile.weaponDef?.weaponType == WeaponType.Ray)))
                return null;

            Def? owner = (Def?)shot?.Definition ?? projectile.weaponDef;
            if (ResolveOwner(owner) is { } resolved) return resolved.Streak;

            return shot?.Profile is { HasVisual: true } legacy ? FromLegacy(legacy) : DefaultStreak;
        }

        internal ReadOnlySpan<ProjectileImpactStyle> ResolveImpact(in CombatImpactEvent impact)
        {
            bool detonation = impact.Kind == CombatImpactKind.Detonation;
            if (ResolveOwner(impact.SourceDef) is { } resolved)
                return detonation ? resolved.Detonation : resolved.Contact;
            if (detonation) return DefaultBurst;
            if (impact.IsInstant) return DefaultFlash;
            return impact.SourceDef is MissileDef ? [] : DefaultSparks;
        }

        private ProfileResult? ResolveOwner(Def? owner)
        {
            if (owner == null) return null;
            if (profilesByOwner.TryGetValue(owner, out var resolved)) return resolved;
            resolved = ReadProfile(owner.GetModExtension<ProjectileVisualExtension>()?.visualProfile);
            profilesByOwner.Add(owner, resolved);
            return resolved;
        }

        private ProfileResult? ReadProfile(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (profilesByName.TryGetValue(name, out var result)) return result;
            var profile = findProfile(name);
            if (profile != null && TryReadFlight(profile.flight, out var streak) &&
                ReadImpactLayers(profile.onContact, false) is { } contact &&
                ReadImpactLayers(profile.onDetonation, true) is { } detonation)
                result = new(streak, contact, detonation);
            else
                warn($"[PFL] PFView projectile visual profile '{name}' is missing or invalid. Using default effects.");

            profilesByName.Add(name, result);
            return result;
        }

        private static bool TryReadFlight(List<ProjectileVisualLayer>? layers, out ProjectileStreakStyle? streak)
        {
            streak = null;
            if (layers is { Count: 0 }) return true;
            if (layers is not { Count: 1 } || layers[0] is not { IsValidStreak: true } layer) return false;
            streak = new(layer.color, layer.widthPixels, layer.maxLengthPixels, layer.sampleSeconds, layer.fadeDistanceMeters);
            return true;
        }

        private static ProjectileImpactStyle[]? ReadImpactLayers(List<ProjectileVisualLayer>? layers, bool detonation)
        {
            if (layers is not { Count: <= 2 }) return null;
            if (layers.Count == 0) return [];
            var result = new ProjectileImpactStyle[layers.Count];
            for (int i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                if (layer == null || !layer.IsValidImpact(detonation)) return null;
                result[i] = new(layer.kind, layer.color, layer.durationSeconds, layer.maxRadiusPixels);
            }
            return result;
        }

        private static ProjectileStreakStyle FromLegacy(ShotProfile legacy)
        {
            float width = legacy.WidthMeters > 0f && float.IsFinite(legacy.WidthMeters)
                ? Math.Clamp(1f + MathF.Log10(1f + legacy.WidthMeters), 1f, 2f) : DefaultStreak.WidthPixels;
            return DefaultStreak with
            {
                Color = IsValidColor(legacy.Color) ? legacy.Color : DefaultStreak.Color,
                WidthPixels = width,
                SampleSeconds = float.IsFinite(legacy.VisualSeconds)
                    ? Math.Clamp(legacy.VisualSeconds, 0.001f, 0.08f) : DefaultStreak.SampleSeconds
            };
        }

        internal static bool IsValidColor(Color color) =>
            color.r is >= 0f and <= 1f && color.g is >= 0f and <= 1f &&
            color.b is >= 0f and <= 1f && color.a is >= 0f and <= 1f;
    }
}
