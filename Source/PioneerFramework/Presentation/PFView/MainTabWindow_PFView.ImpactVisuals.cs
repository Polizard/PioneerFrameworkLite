using System;
using System.Collections.Generic;
using PioneerFramework.Combat;
using PioneerFramework.ProjectileVisuals;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
        private PFViewImpactPlayback? impactPlayback;
        private readonly CombatImpactEvent[] impactReadBuffer = new CombatImpactEvent[128];
        private readonly HashSet<(int EntityId, int X, int Y)> flashCells = new(128);
        private HashSet<int> impactVisibleGroups = new();
        private HashSet<int> previousImpactVisibleGroups = new();
        private PFViewImpactPlayback.View impactFocus;
        private CombatManager? impactCombat;
        private long impactCursor;

        private void BeginImpactVisibility()
        {
            var focus = new PFViewImpactPlayback.View(focusKind, focusGroupId, focusBodyId, currentSystemBodyId, viewFrameIsFixed);
            (impactVisibleGroups, previousImpactVisibleGroups) = (previousImpactVisibleGroups, impactVisibleGroups);
            impactVisibleGroups.Clear();
            bool sameView = focus.ContinuesFrom(impactFocus);
            impactFocus = focus;
            if (sameView) return;
            previousImpactVisibleGroups.Clear();
            impactPlayback?.Clear();
            if (impactCombat != null) impactCursor = impactCombat.LatestImpactSequence;
        }

        private PFViewImpactPlayback UpdateImpactPlayback(CombatManager combat, SpaceGameComp? space, double tick)
        {
            var playback = impactPlayback ??= new PFViewImpactPlayback();
            if (!ReferenceEquals(impactCombat, combat))
            {
                impactCombat = combat;
                impactCursor = combat.LatestImpactSequence;
                playback.Clear();
                previousImpactVisibleGroups.Clear();
            }
            for (int batch = 0; batch < 4; batch++)
            {
                int count = combat.CopyImpactEvents(impactCursor, impactReadBuffer);
                if (count == 0) break;
                for (int i = 0; i < count; i++)
                {
                    var impact = impactReadBuffer[i];
                    int knownGroup = -1;
                    if (space != null)
                    {
                        if (ImpactGroupVisible(impact.TargetGroupId, -1, space))
                            knownGroup = impact.TargetGroupId;
                        else if (impact.Kind == CombatImpactKind.Detonation && ImpactGroupVisible(impact.SourceGroupId, -1, space))
                            knownGroup = impact.SourceGroupId;
                    }
                    playback.Enqueue(impact, knownGroup);
                }
                impactCursor = impactReadBuffer[count - 1].Sequence;
                Array.Clear(impactReadBuffer, 0, count);
                if (count < impactReadBuffer.Length) break;
            }
            playback.Advance(tick, Find.TickManager.TicksGame, RealTime.deltaTime,
                Find.TickManager.CurTimeSpeed == TimeSpeed.Paused);
            return playback;
        }

        private void DrawCombatImpacts(PFViewProjectileBatch batch, ProjectileVisualResolver visuals,
            SpaceGameComp? space, DVector3 origin, double tick, Vector3 cameraPosition, Quaternion toEye,
            ProjectileProjection projection, PFViewImpactPlayback playback)
        {
            if (space == null) return;
            flashCells.Clear();
            for (int i = playback.Active.Count - 1; i >= 0; i--)
            {
                var active = playback.Active[i];
                var impact = active.Event;
                var layers = visuals.ResolveImpact(impact);
                if (layers.IsEmpty || !TryResolveImpactAnchor(active, space, tick, out var anchor, out int visibleGroup)) continue;
                playback.MarkVisible(i, visibleGroup);
                Vector3 eye = toEye * (AbsToView(anchor, origin) - cameraPosition);
                if (!float.IsFinite(eye.x) || !float.IsFinite(eye.y) || !float.IsFinite(eye.z)) continue;
                if (eye.z <= projection.Near || eye.z >= projection.Far) continue;
                // A tiny eye-space bias keeps a surface hit from fighting the ship's depth.
                eye.z = Math.Max(projection.Near, eye.z - 0.02f * MetersToView);
                float age = (float)(playback.DisplaySeconds - active.Started);
                Vector3 incoming = toEye * (viewFrameRot * (Vector3)impact.DirectionSystem.v.normalized);
                foreach (var layer in layers)
                {
                    float progress = age / layer.DurationSeconds;
                    if (progress >= 1f) continue;
                    if (impact.IsInstant && layer.Kind == ProjectileVisualKind.Flash &&
                        impact.Kind == CombatImpactKind.Contact && !ClaimFlashCell(impact.TargetEntityId, projection, eye))
                        continue;
                    DrawImpactLayer(batch, projection, impact, layer, eye, incoming, progress);
                }
            }
        }

        private bool ClaimFlashCell(int entityId, ProjectileProjection projection, Vector3 eye)
        {
            const float cellPixels = 3f;
            float scale = projection.FocalPixels * 1080f / (eye.z * projection.Height * cellPixels);
            return flashCells.Add((entityId, (int)MathF.Floor(eye.x * scale), (int)MathF.Floor(eye.y * scale)));
        }

        private bool TryResolveImpactAnchor(PFViewImpactPlayback.ActiveImpact active, SpaceGameComp space, double tick,
            out DVector3 anchor, out int visibleGroup)
        {
            var impact = active.Event;
            anchor = default;
            visibleGroup = impact.TargetGroupId;
            if (impact.Kind == CombatImpactKind.Contact)
            {
                var entity = space.GetEntityById(impact.TargetEntityId);
                if (entity != null)
                {
                    if (!impactVisibleGroups.Contains(entity.GroupId)) return false;
                    visibleGroup = entity.GroupId;
                    if (impact.TargetLocalPoint is { } local && space.GetGroupById(entity.GroupId) is { } group)
                    {
                        anchor = GetGroupPosAbs(group, tick) + entity.Position + DVector3.Rotate(local, group.CurrentOrbit.rotation);
                        return true;
                    }
                }
                else if (!ImpactGroupVisible(visibleGroup, active.VisibleGroupId, space)) return false;
            }
            else if (!ImpactGroupVisible(visibleGroup, active.VisibleGroupId, space))
            {
                visibleGroup = impact.SourceGroupId;
                if (!ImpactGroupVisible(visibleGroup, active.VisibleGroupId, space)) return false;
            }

            anchor = impact.PointSystem.v + ProjectileFrameMotionAt(impact.FrameId, tick).Position;
            return true;
        }

        private bool ImpactGroupVisible(int id, int previouslyVisible, SpaceGameComp space)
            => id >= 0 && (impactVisibleGroups.Contains(id) ||
                (space.GetGroupById(id)?.IsActive != true &&
                    (id == previouslyVisible || previousImpactVisibleGroups.Contains(id))));

        private static void DrawImpactLayer(PFViewProjectileBatch batch, ProjectileProjection projection,
            in CombatImpactEvent impact, ProjectileImpactStyle layer, Vector3 center, Vector3 incoming, float progress)
        {
            Color color = layer.Color;
            long id = impact.Sequence;
            float fade = 1f - progress;
            color.a *= fade * fade;
            if (layer.Kind == ProjectileVisualKind.Flash)
            {
                AddGlow(center, center, layer.MaxRadiusPixels * (0.65f + 0.35f * fade), color);
                return;
            }

            bool burst = layer.Kind == ProjectileVisualKind.Burst;
            float radius = layer.MaxRadiusPixels;
            if (burst)
            {
                float projectedRadius = (float)(impact.RadiusMeters * MetersToView) * projection.FocalPixels / center.z;
                radius = Math.Clamp(projectedRadius, radius * 0.5f, radius);
                Color core = Color.Lerp(color, Color.white, 0.55f * fade);
                core.a = color.a;
                AddGlow(center, center, radius * (0.45f + 0.55f * progress), core);
            }

            int count = burst ? 6 : 3;
            float scale = projection.Height / 1080f * center.z / projection.FocalPixels;
            Vector2 direction = new Vector2(incoming.x, incoming.y).normalized;
            float angle = (impact.Sequence % 31) * 0.31f;
            for (int i = 0; i < count; i++, angle += 2.399963f)
            {
                Vector2 radial = new(MathF.Cos(angle), MathF.Sin(angle));
                Vector2 motion = burst ? radial : radial * 0.55f + direction * 0.45f;
                Vector3 step = new Vector3(motion.x, motion.y, 0) * (scale * radius);
                Vector3 head = center + step * progress;
                Vector3 tail = head - step * Math.Min(progress, 0.2f);
                AddGlow(head, tail, burst ? 0.9f : 0.7f, color);
            }

            void AddGlow(Vector3 head, Vector3 tail, float size, Color tint)
            {
                if (!projection.TryBuildImpact(head, tail, size, out var quad)) return;
                tint.a *= quad.Coverage;
                batch.Add(quad, tint, id, impact: true);
            }
        }

        private void ReleaseImpactPlayback()
        {
            impactCombat = null;
            impactCursor = 0;
            impactPlayback = null;
            impactVisibleGroups.Clear();
            previousImpactVisibleGroups.Clear();
        }
    }
}
