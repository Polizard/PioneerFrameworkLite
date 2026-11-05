using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.Launch
{
    public class CompRocketLandingBurn : ThingComp
    {
        private CompProperties_RocketLandingBurn PropsTyped => (CompProperties_RocketLandingBurn)props;

        public override void CompTick()
        {
            base.CompTick();
            if (!parent.Spawned || parent.Map == null)
            {
                return;
            }

            Skyfaller? skyfaller = parent as Skyfaller;
            if (skyfaller == null || skyfaller.def.skyfaller.reversed)
            {
                return;
            }

            if (PropsTyped.onlyOnCurrentMap && parent.MapHeld != Find.CurrentMap)
            {
                return;
            }

            int burnTicks = Mathf.Max(1, PropsTyped.burnDurationSeconds.SecondsToTicks());
            if (skyfaller.ticksToImpact > burnTicks)
            {
                return;
            }

            if (!skyfaller.Position.ShouldSpawnMotesAt(parent.MapHeld, true))
            {
                return;
            }

            SyncAngleForIncoming(skyfaller);
            Map map = parent.MapHeld;
            float u = 1f - (float)skyfaller.ticksToImpact / burnTicks;
            float thrustIntensity = Mathf.Lerp(0.55f, 1f, u * u);

            Vector3 forward = Vector3Utility.FromAngleFlat(skyfaller.angle - 90f).normalized;
            Vector3 engineAnchor = skyfaller.DrawPos - forward * PropsTyped.engineBackset;
            engineAnchor.y += PropsTyped.engineAnchorAltitudeOffset;

            for (int s = 0; s < PropsTyped.smokePuffsPerTick; s++)
            {
                Vector3 p = engineAnchor + Gen.RandomHorizontalVector(PropsTyped.spawnJitter);
                FleckMaker.ThrowSmoke(p, map, PropsTyped.smokeScale * Rand.Range(0.9f, 1.2f) * thrustIntensity);
            }

            FleckDef flameDef = PropsTyped.flameFleck;
            if (flameDef != null)
            {
                for (int f = 0; f < PropsTyped.flameBurstsPerTick; f++)
                {
                    Vector3 p = engineAnchor + Gen.RandomHorizontalVector(PropsTyped.spawnJitter * 0.6f);
                    FleckCreationData data = FleckMaker.GetDataStatic(p, map, flameDef, 1f);
                    data.scale = PropsTyped.flameScale * Rand.Range(0.85f, 1.25f) * thrustIntensity;
                    data.velocityAngle = skyfaller.angle + 180f + Rand.Range(-25f, 25f);
                    data.velocitySpeed = Rand.Range(0.35f, 1.1f) * thrustIntensity;
                    data.solidTimeOverride = 0.12f + 0.2f * thrustIntensity;
                    data.targetSize = data.scale * 2.4f;
                    map.flecks.CreateFleck(data);
                }
            }

            if (PropsTyped.spawnDustPuffs && Rand.Chance(0.4f * thrustIntensity))
            {
                FleckMaker.ThrowDustPuffThick(
                    engineAnchor + Gen.RandomHorizontalVector(0.25f),
                    map,
                    Rand.Range(2.4f, 3.4f),
                    Color.Lerp(Color.gray, new Color(0.45f, 0.45f, 0.5f), Rand.Value));
            }
        }

        private static int TicksToImpactMax(Skyfaller s)
        {
            IntRange r = s.def.skyfaller.ticksToImpactRange;
            return Mathf.Max(1, r.max);
        }

        private static float TimeInAnimationIncoming(Skyfaller s)
        {
            return Mathf.Clamp01(1f - (float)s.ticksToImpact / TicksToImpactMax(s));
        }

        private static void SyncAngleForIncoming(Skyfaller skyfaller)
        {
            SimpleCurve angleCurve = skyfaller.def.skyfaller.angleCurve;
            if (angleCurve != null)
            {
                skyfaller.angle = angleCurve.Evaluate(TimeInAnimationIncoming(skyfaller));
            }
        }
    }
}
