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
        private const double ProjectileMotionSampleSeconds = 1.0 / 60.0;
        private readonly Dictionary<int, (DVector3 Position, DVector3 Velocity)> projectileFrameMotion = new();
        private readonly Dictionary<int, bool> projectileSourceVisibility = new();
        private ProjectileVisualResolver? projectileVisuals;
        private PFViewProjectileBatch? projectileBatch;

        private void DrawProjectiles(DVector3 origin, double tick)
        {
            var game = Current.Game;
            var combat = game?.GetComponent<CombatManager>();
            if (game == null || combat == null) return;
            var space = game.GetComponent<SpaceGameComp>();
            var playback = UpdateImpactPlayback(combat, space, tick);
            if (combat.ActiveProjectiles.Count != 0 || playback.Active.Count != 0)
            {
                projectileVisuals ??= new ProjectileVisualResolver(
                    static name => DefDatabase<ProjectileVisualDef>.GetNamedSilentFail(name),
                    static message => Log.Warning(message));
                projectileBatch ??= new PFViewProjectileBatch();
                projectileBatch.BeginFrame();
                projectileFrameMotion.Clear();
                projectileSourceVisibility.Clear();

                var sensor = game.GetComponent<SensorManager>();
                int? playerFaction = Faction.OfPlayer?.loadID;
                var transform = renderCamera.transform;
                Vector3 cameraPosition = transform.position;
                Quaternion cameraRotation = transform.rotation;
                Quaternion toEyeRotation = Quaternion.Inverse(cameraRotation);
                float focalPixels = renderTexture.height * 0.5f / Mathf.Tan(renderCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                var projection = new ProjectileProjection(renderTexture.width, renderTexture.height, focalPixels,
                    renderCamera.nearClipPlane, renderCamera.farClipPlane);
                DVector3 cameraVelocity = combat.ActiveProjectiles.Count == 0 ? default : ProjectileCameraCarrierVelocity(origin, tick);

                foreach (var projectile in combat.ActiveProjectiles)
                {
                    if (!projectile.IsVisibleAt(tick) ||
                        (projectile.factionId != playerFaction && !CanSeeProjectileSource(projectile.sourceGroupId, sensor)) ||
                        projectileVisuals.Resolve(projectile) is not { } style)
                        continue;

                    DVector3 position = projectile.RenderedPosition(tick);
                    var frame = ProjectileFrameMotionAt(projectile.systemBodyId, tick);
                    Vector3 head = toEyeRotation * (AbsToView(position + frame.Position, origin) - cameraPosition);
                    float distanceMeters = head.magnitude * ViewToMeters;
                    float fade = Mathf.Clamp01((style.FadeDistanceMeters - distanceMeters) / (0.35f * style.FadeDistanceMeters));
                    if (fade <= 0f) continue;

                    double sampleTick = Math.Max(projectile.spawnPhysicsTick, tick - style.SampleSeconds);
                    if (projectile.affectedByGravity)
                        sampleTick = Math.Clamp(projectile.prevUpdatePhysicsTick, sampleTick, tick);
                    var tailPosition = ProjectileProjection.AdjustTailForMotion(
                        projectile.RenderedPosition(sampleTick), frame.Velocity, cameraVelocity, tick - sampleTick);
                    Vector3 tail = toEyeRotation * (AbsToView(tailPosition + frame.Position, origin) - cameraPosition);
                    if (projection.TryBuild(head, tail, style, out var quad))
                    {
                        Color color = style.Color;
                        color.a *= fade * quad.Coverage;
                        projectileBatch.Add(quad, color, projectile.id);
                    }

#if DEBUG
                    TrackProjectileRender(projectile, position);
#endif
                }

                DrawCombatImpacts(projectileBatch, projectileVisuals, space, origin, tick,
                    cameraPosition, toEyeRotation, projection, playback);
                projectileBatch.Draw(renderCamera, cameraPosition, cameraRotation, SceneLayer);
            }

            DrawProjectileExplosions(combat, origin, tick);
#if DEBUG
            ClearExpiredProjectileProbes();
#endif
        }

        private bool CanSeeProjectileSource(int groupId, SensorManager? sensor)
        {
            if (projectileSourceVisibility.TryGetValue(groupId, out bool visible)) return visible;
            visible = CanInspectGroup(groupId) && sensor?.PlayerCanSee(groupId) == true;
            projectileSourceVisibility.Add(groupId, visible);
            return visible;
        }

        private DVector3 ProjectileCameraCarrierVelocity(DVector3 origin, double tick)
        {
            // Sample the same focus twice in simulation time. Orbit dragging, zoom and focus transitions
            // only affect the current projection and cannot create a spurious velocity spike.
            double earlierTick = tick - ProjectileMotionSampleSeconds;
            int groupId = GetFocusGroupId();
            if (Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(groupId) is { IsActive: true } group)
            {
                if (group.IsCircularOrbit)
                    return (origin - GetGroupPosAbs(groupId, earlierTick)) / ProjectileMotionSampleSeconds;

                var bodies = Current.Game?.GetComponent<CelestialBodyManager>();
                int frameId = bodies?.GetReferenceFrameId(group.CelestialBodyId) ?? -1;
                DVector3 velocity = ProjectileProjection.CarrierVelocity(group.physicsRenderPreviousPosition,
                    group.CurrentOrbit.positionSystem.v, group.CurrentOrbit.velocitySystem.v,
                    group.physicsRenderPreviousTick, group.physicsRenderCurrentTick, tick);
                return velocity + ProjectileFrameMotionAt(frameId, tick).Velocity;
            }
            if (focusKind == PFViewFocusKind.Body && focusBodyId >= 0)
                return ProjectileFrameMotionAt(focusBodyId, tick).Velocity;
            return DVector3.zero;
        }

        private (DVector3 Position, DVector3 Velocity) ProjectileFrameMotionAt(int frameId, double tick)
        {
            if (frameId < 0) return default;
            if (projectileFrameMotion.TryGetValue(frameId, out var result)) return result;
            DVector3 position = GetBodyPosAbs(frameId, tick);
            DVector3 velocity = (position - GetBodyPosAbs(frameId, tick - ProjectileMotionSampleSeconds)) /
                ProjectileMotionSampleSeconds;
            result = (position, velocity);
            projectileFrameMotion.Add(frameId, result);
            return result;
        }

        private void ReleaseProjectileVisualResources()
        {
            projectileBatch?.Release();
            projectileBatch = null;
            projectileVisuals = null;
            projectileFrameMotion.Clear();
            projectileSourceVisibility.Clear();
            ReleaseImpactPlayback();
#if DEBUG
            debugLastProjRender.Clear();
            debugLiveProjectileIds.Clear();
#endif
        }

#if DEBUG
        private void TrackProjectileRender(ProjectileInstance projectile, DVector3 position)
        {
            if (debugLastProjRender.TryGetValue(projectile.id, out var lastRender))
            {
                double jump = (position - lastRender).magnitude;
                double expectedStep = projectile.velocity.magnitude * (2.0 / 60.0);
                if (jump > expectedStep * 1.5 + 1.0 && Find.TickManager.TicksGame - debugJumpLogTick >= 60)
                {
                    debugJumpLogTick = Find.TickManager.TicksGame;
                    Log.Message($"[射弹渲染检测] proj={projectile.id} 帧间跳变 {jump:F0}m（预期步进 {expectedStep:F0}m）→ 疑似折线/闪现");
                }
            }
            debugLastProjRender[projectile.id] = position;
            debugLiveProjectileIds.Add(projectile.id);
        }

        private void ClearExpiredProjectileProbes()
        {
            if (debugLastProjRender.Count > debugLiveProjectileIds.Count)
            {
                debugDeadProjIds.Clear();
                foreach (var entry in debugLastProjRender)
                    if (!debugLiveProjectileIds.Contains(entry.Key)) debugDeadProjIds.Add(entry.Key);
                foreach (int id in debugDeadProjIds) debugLastProjRender.Remove(id);
            }
            debugLiveProjectileIds.Clear();
        }
#endif
    }
}
