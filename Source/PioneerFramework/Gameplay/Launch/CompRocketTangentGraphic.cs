using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.Launch
{
    public class CompRocketTangentGraphic : ThingComp
    {
        private Vector3 lastSampleDrawLoc;
        private int lastSampleGameTick = -1;
        private bool hasSample;
        private float lastTangentGraphicAngle;

        /// <summary>供尾迹等与机头朝向一致；与上一帧绘制所更新的切线角同步（可能落后一逻辑帧）。</summary>
        public float LastTangentGraphicAngle => lastTangentGraphicAngle;

        public bool HasTangentSample => hasSample;

        internal void ApplyTangentRotation(ref float extraRotation, Vector3 finalDrawLoc, Skyfaller skyfaller)
        {
            SkyfallerProperties sf = skyfaller.def.skyfaller;
            if (!sf.rotateGraphicTowardsDirection)
            {
                return;
            }

            int tick = Find.TickManager.TicksGame;
            float addRandom = skyfaller.RandomizeDirectionComp != null ? skyfaller.RandomizeDirectionComp.ExtraDrawAngle : 0f;
            float rotCurve = 0f;
            if (sf.rotationCurve != null)
            {
                rotCurve = sf.rotationCurve.Evaluate(TimeInAnimationFor(skyfaller));
            }

            if (!hasSample)
            {
                lastTangentGraphicAngle = skyfaller.angle;
                lastSampleDrawLoc = finalDrawLoc;
                lastSampleGameTick = tick;
                hasSample = true;
                extraRotation = lastTangentGraphicAngle + addRandom + rotCurve;
                return;
            }

            if (tick > lastSampleGameTick)
            {
                float dx = finalDrawLoc.x - lastSampleDrawLoc.x;
                float dz = finalDrawLoc.z - lastSampleDrawLoc.z;
                float sq = dx * dx + dz * dz;
                if (sq > 1e-8f)
                {
                    lastTangentGraphicAngle = HorizontalDeltaToGraphicAngle(dx, dz);
                }
                else if (skyfaller.def.skyfaller.reversed)
                {
                    lastTangentGraphicAngle = skyfaller.angle;
                }
                // 入轨末段 speedCurve 急刹时位移差分趋近 0；若改用房角会与切线不连续，贴图会在约最后 0.5s 瞬间翻转。

                lastSampleDrawLoc = finalDrawLoc;
                lastSampleGameTick = tick;
            }
            else
            {
                lastSampleDrawLoc = finalDrawLoc;
            }

            extraRotation = lastTangentGraphicAngle + addRandom + rotCurve;
        }

        private static float TimeInAnimationFor(Skyfaller s)
        {
            if (s.def.skyfaller.reversed)
            {
                return Mathf.Clamp01((float)s.ticksToImpact / Mathf.Max(1, s.LeaveMapAfterTicks));
            }

            int tMax = Mathf.Max(1, s.def.skyfaller.ticksToImpactRange.max);
            return Mathf.Clamp01(1f - (float)s.ticksToImpact / tMax);
        }

        /// <summary>
        /// 与 <see cref="Vector3Utility.FromAngleFlat(angle - 90f)"/> 位移方向一致：+Z 为 angle==0。
        /// </summary>
        private static float HorizontalDeltaToGraphicAngle(float dx, float dz)
        {
            return Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
        }
    }
}
