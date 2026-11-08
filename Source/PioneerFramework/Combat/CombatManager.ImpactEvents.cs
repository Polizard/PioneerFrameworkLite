using System;
using Verse;

namespace PioneerFramework.Combat
{
    public enum CombatImpactKind
    {
        Contact,
        Detonation
    }

    /// <summary>Transient snapshot of an accepted weapon contact or an executed space detonation.</summary>
    public readonly record struct CombatImpactEvent(
        CombatImpactKind Kind, int ProjectileId, Def? SourceDef, bool IsInstant,
        int SourceGroupId, int SourceEntityId, int TargetGroupId, int TargetEntityId,
        int FrameId, SystemVec3 PointSystem, DVector3? TargetLocalPoint, SystemVec3 DirectionSystem,
        double RadiusMeters, double PhysicsTick, int HostTick)
    {
        public long Sequence { get; internal init; }
    }

    internal sealed class CombatImpactBuffer(int capacity)
    {
        private readonly CombatImpactEvent[] events = new CombatImpactEvent[capacity];
        private readonly object sync = new();
        private long latest;

        internal long LatestSequence
        {
            get { lock (sync) return latest; }
        }

        internal void Append(in CombatImpactEvent impact)
        {
            lock (sync)
            {
                long sequence = ++latest;
                events[(int)((sequence - 1) % events.Length)] = impact with { Sequence = sequence };
            }
        }

        internal int CopyAfter(long sequence, Span<CombatImpactEvent> destination)
        {
            lock (sync)
            {
                if (sequence >= latest || destination.IsEmpty) return 0;
                long first = Math.Max(sequence + 1, Math.Max(1L, latest - events.Length + 1));
                int count = (int)Math.Min(latest - first + 1, destination.Length);
                for (int i = 0; i < count; i++)
                    destination[i] = events[(int)((first + i - 1) % events.Length)];
                return count;
            }
        }
    }

    public partial class CombatManager
    {
        private readonly CombatImpactBuffer impactEvents = new(512);

        /// <summary>Current cursor for this game instance. Capture it when starting a live reader.</summary>
        public long LatestImpactSequence => impactEvents.LatestSequence;

        /// <summary>
        /// Copies retained events in publication order without consuming them. Advance a private cursor to the
        /// last copied sequence. Gaps indicate that older events fell out of the transient buffer.
        /// </summary>
        public int CopyImpactEvents(long afterSequence, Span<CombatImpactEvent> destination)
            => impactEvents.CopyAfter(afterSequence, destination);

        private void RecordProjectileContact(ProjectileInstance projectile, SpaceEntityGroup target, SpaceEntity entity,
            DVector3 point, DVector3 localPoint)
        {
            RecordContact(projectile.id, (Def?)projectile.impactMissileDef ?? projectile.shot?.Definition ?? (Def?)projectile.weaponDef,
                projectile.shot?.Profile.Instant == true, projectile.sourceGroupId, projectile.impactSourceEntityId,
                target, entity, projectile.systemBodyId, point, localPoint, (projectile.position - projectile.prevPosition).v);
        }

        private void RecordContact(int projectileId, Def? source, bool instant, int sourceGroupId, int sourceEntityId,
            SpaceEntityGroup target, SpaceEntity entity, int frameId, DVector3 point, DVector3 localPoint, DVector3 direction)
        {
            if (!point.IsFinite()) return;
            impactEvents.Append(new(CombatImpactKind.Contact, projectileId, source, instant, sourceGroupId, sourceEntityId,
                target.GroupId, entity.EntityId, frameId, (SystemVec3)point, localPoint.IsFinite() ? localPoint : null,
                (SystemVec3)direction, 0, TimeScaleManager.GetPhysicsTickSafe(), Find.TickManager?.TicksGame ?? 0));
        }

        private bool RecordDetonation(Def? source, int sourceGroupId, int sourceEntityId, int projectileId,
            int frameId, DVector3 point, double radius, int targetGroupId = -1, int targetEntityId = -1, bool instant = false)
        {
            if (!point.IsFinite() || !double.IsFinite(radius) || radius <= 0) return false;
            impactEvents.Append(new(CombatImpactKind.Detonation, projectileId, source, instant, sourceGroupId, sourceEntityId,
                targetGroupId, targetEntityId, frameId, (SystemVec3)point, null, default,
                radius, TimeScaleManager.GetPhysicsTickSafe(), Find.TickManager?.TicksGame ?? 0));
            return true;
        }
    }
}
