using System;
using System.Collections.Generic;
using PioneerFramework.Combat;

namespace PioneerFramework.ProjectileVisuals
{
    internal sealed class PFViewImpactPlayback
    {
        private const int Capacity = 128;
        private const int MaxHostTickAge = 30;
        private const double MaxDisplayAge = 0.6;
        private readonly List<(CombatImpactEvent Event, int VisibleGroupId)> pending = new(Capacity);
        private readonly List<ActiveImpact> active = new(Capacity);

        internal readonly record struct ActiveImpact(CombatImpactEvent Event, double Started, int VisibleGroupId);
        internal readonly record struct View(PFViewFocusKind Kind, int GroupId, int BodyId, int SystemId, bool Fixed)
        {
            internal bool ContinuesFrom(in View previous)
                => this == previous ||
                    (Kind == PFViewFocusKind.Frozen && previous.Kind == PFViewFocusKind.Group &&
                        BodyId == previous.BodyId && SystemId == previous.SystemId && Fixed == previous.Fixed);
        }

        internal IReadOnlyList<ActiveImpact> Active => active;
        internal double DisplaySeconds { get; private set; }

        internal void Enqueue(in CombatImpactEvent impact, int visibleGroupId = -1)
        {
            if (!double.IsFinite(impact.PhysicsTick)) return;
            if (pending.Count == Capacity) pending.RemoveAt(0);
            pending.Add((impact, visibleGroupId));
        }

        internal void Advance(double physicsTick, int hostTick, float displayDelta, bool paused)
        {
            if (!paused && float.IsFinite(displayDelta)) DisplaySeconds += Math.Clamp(displayDelta, 0f, 0.1f);
            int expired = 0;
            while (expired < active.Count && DisplaySeconds - active[expired].Started >= MaxDisplayAge) expired++;
            if (expired > 0) active.RemoveRange(0, expired);

            int keep = 0;
            for (int i = 0; i < pending.Count; i++)
            {
                var entry = pending[i];
                var impact = entry.Event;
                if ((long)hostTick - impact.HostTick > MaxHostTickAge) continue;
                if (impact.PhysicsTick > physicsTick)
                {
                    pending[keep++] = entry;
                    continue;
                }
                if (active.Count == Capacity) active.RemoveAt(0);
                active.Add(new(impact, DisplaySeconds, entry.VisibleGroupId));
            }
            if (keep < pending.Count) pending.RemoveRange(keep, pending.Count - keep);
        }

        internal void MarkVisible(int index, int groupId) => active[index] = active[index] with { VisibleGroupId = groupId };

        internal void Clear()
        {
            pending.Clear();
            active.Clear();
        }
    }
}
