using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.Launch
{
    /// <summary>
    /// 离港火箭尾焰（与 DMS 包 <c>CompEffecterColdLaunch.ThrowExhaust</c> 同级思路）；喷口朝向优先用相邻 tick 的 <see cref="Skyfaller.DrawPos"/> 差分。
    /// <see cref="ThrowExhaust"/> 可启用单束尾向火焰；<see cref="PostDraw"/> 锥形为可选叠层，主可见性以 Fleck 为准。
    /// </summary>
    public class CompRocketExhaustTrail : ThingComp
    {
        private CompProperties_RocketExhaustTrail PropsTyped => (CompProperties_RocketExhaustTrail)props;

        private int ticksSinceSpawn;

        private bool hasLastDrawPos;

        private Vector3 lastDrawPosForVelocity;

        /// <summary>上一 tick 有效喷焰时缓存，供 <see cref="PostDraw"/> 锥形使用（与 <c>ThrowExhaust</c> 不同步时勿单独依赖）。</summary>
        private Vector3 lastExhaustAnchor;
        private float lastExhaustRotationDeg;
        private float lastEvaluate;
        private float lastComp01;
        private bool hasExhaustSample;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            hasLastDrawPos = false;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref ticksSinceSpawn, "rocketExhaustTicksSinceSpawn", 0);
        }

        public override void CompTick()
        {
            base.CompTick();
            ticksSinceSpawn++;
            if (!parent.Spawned || parent.Map == null)
            {
                return;
            }

            Skyfaller? skyfaller = parent as Skyfaller;
            if (skyfaller == null)
            {
                return;
            }

            SyncAngleFromCurve(skyfaller);
            Vector3 drawPosNow = skyfaller.DrawPos;
            float exhaustRotationDeg = ResolveExhaustRotationDegrees(skyfaller, drawPosNow);

            if (ShouldShowExhaust(skyfaller) && skyfaller.Position.ShouldSpawnMotesAt(parent.MapHeld, true))
            {
                int denom = Mathf.Max(1, skyfaller.LeaveMapAfterTicks + PropsTyped.delayTicks);
                float evaluate = Mathf.Clamp01((float)ticksSinceSpawn / denom);
                float speed = CurrentSkyfallerSpeed(skyfaller);
                float comp01 = ResolveComp01(evaluate, speed);
                bool extendedTrail = ticksSinceSpawn > PropsTyped.delayTicks + PropsTyped.trailExtendDelayTicks;
                float backset = extendedTrail ? PropsTyped.extendedCenterToEngineBackset : PropsTyped.centerToEngineBackset;
                Vector3 anchor = AnchorForExhaust(drawPosNow, exhaustRotationDeg, backset);
                lastExhaustAnchor = anchor;
                lastExhaustRotationDeg = exhaustRotationDeg;
                lastEvaluate = evaluate;
                lastComp01 = comp01;
                hasExhaustSample = true;
                ThrowExhaust(anchor, exhaustRotationDeg, evaluate, speed, comp01, extendedTrail);
            }
            else
            {
                hasExhaustSample = false;
            }

            lastDrawPosForVelocity = drawPosNow;
            hasLastDrawPos = true;
        }

        /// <summary>
        /// 纯色双段锥形叠层（尾向）。离港 <see cref="Skyfaller"/> 上 Comp 的 PostDraw 调用链未必总与预期一致，故主焰必须在 <see cref="ThrowExhaust"/> 中保证。
        /// </summary>
        public override void PostDraw()
        {
            base.PostDraw();
            if (!PropsTyped.enableContinuousFlame || !hasExhaustSample || !parent.Spawned || parent.MapHeld == null)
            {
                return;
            }

            if (PropsTyped.onlyOnCurrentMap && parent.MapHeld != Find.CurrentMap)
            {
                return;
            }

            bool inInitialLateralPhase = lastEvaluate <= Mathf.Clamp01(PropsTyped.initialLateralPhasePct);
            if (inInitialLateralPhase)
            {
                return;
            }

            float startComp = Mathf.Clamp01(PropsTyped.continuousFlameStartComp01);
            float blend = Mathf.InverseLerp(startComp, 1f, lastComp01);
            float length = Mathf.Max(0.1f, PropsTyped.continuousFlameLength * Mathf.Lerp(0.9f, 1.15f, blend));
            float width = Mathf.Max(0.05f, PropsTyped.continuousFlameWidth * Mathf.Lerp(1f, 0.86f, blend));
            float alpha = Mathf.Lerp(
                Mathf.Clamp01(PropsTyped.continuousFlameMinAlpha),
                Mathf.Clamp01(PropsTyped.continuousFlameMaxAlpha),
                blend);
            Color flameColor = new Color(1f, 0.48f, 0.08f, alpha);
            Material mat = SolidColorMaterials.SimpleSolidColorMaterial(flameColor);
            float rad = lastExhaustRotationDeg * Mathf.Deg2Rad;
            Vector3 forward = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            Vector3 exhaustDir = -forward;
            Vector3 drawPos = lastExhaustAnchor + exhaustDir * (length * 0.5f);
            drawPos.y = AltitudeLayer.MoteOverhead.AltitudeFor();
            Quaternion rot = Quaternion.Euler(0f, lastExhaustRotationDeg + 180f, 0f);
            // 采用双段锥形：靠近喷口更粗，远端更细，避免出现反向“喇叭口”观感。
            float nearLength = length * 0.58f;
            float tailLength = Mathf.Max(0.06f, length - nearLength);
            float nearWidth = width;
            float tailWidth = Mathf.Max(0.03f, width * 0.48f);
            Vector3 nearPos = lastExhaustAnchor + exhaustDir * (nearLength * 0.5f);
            nearPos.y = drawPos.y;
            Vector3 tailPos = lastExhaustAnchor + exhaustDir * (nearLength + tailLength * 0.5f);
            tailPos.y = drawPos.y;
            Matrix4x4 nearMatrix = Matrix4x4.TRS(nearPos, rot, new Vector3(nearWidth, 1f, nearLength));
            Matrix4x4 tailMatrix = Matrix4x4.TRS(tailPos, rot, new Vector3(tailWidth, 1f, tailLength));
            Graphics.DrawMesh(MeshPool.plane10, nearMatrix, mat, 0);
            Graphics.DrawMesh(MeshPool.plane10, tailMatrix, mat, 0);
        }

        private bool ShouldShowExhaust(Skyfaller skyfaller)
        {
            if (!parent.Spawned || skyfaller.DrawPos.InBounds(parent.Map) == false)
            {
                return false;
            }

            if (ticksSinceSpawn < PropsTyped.delayTicks)
            {
                return false;
            }

            if (PropsTyped.onlyOnCurrentMap && parent.MapHeld != Find.CurrentMap)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 与 <see cref="Skyfaller.GetDrawPositionAndRotation"/> 中 angle 更新一致，保证本 tick 的 <see cref="Skyfaller.DrawPos"/> 与绘制/位移使用同一角度。
        /// </summary>
        private static void SyncAngleFromCurve(Skyfaller skyfaller)
        {
            SimpleCurve angleCurve = skyfaller.def.skyfaller.angleCurve;
            if (angleCurve != null)
            {
                skyfaller.angle = angleCurve.Evaluate(TimeInAnimationFor(skyfaller));
            }
        }

        private static float TimeInAnimationFor(Skyfaller s)
        {
            if (!s.def.skyfaller.reversed)
            {
                return 0f;
            }

            return Mathf.Clamp01((float)s.ticksToImpact / Mathf.Max(1, s.LeaveMapAfterTicks));
        }

        private float ResolveExhaustRotationDegrees(Skyfaller skyfaller, Vector3 drawPosNow)
        {
            if (hasLastDrawPos)
            {
                float dx = drawPosNow.x - lastDrawPosForVelocity.x;
                float dz = drawPosNow.z - lastDrawPosForVelocity.z;
                if (dx * dx + dz * dz > 1e-8f)
                {
                    return Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
                }
            }

            CompRocketTangentGraphic tangentComp = parent.GetComp<CompRocketTangentGraphic>();
            if (tangentComp != null && tangentComp.HasTangentSample)
            {
                return tangentComp.LastTangentGraphicAngle;
            }

            return skyfaller.angle;
        }

        private static Vector3 AnchorForExhaust(Vector3 drawPosNow, float rotationDeg, float backset)
        {
            if (backset <= 0.001f)
            {
                return drawPosNow;
            }

            float rad = rotationDeg * Mathf.Deg2Rad;
            Vector3 forward = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            return drawPosNow - forward * backset;
        }

        private static float CurrentSkyfallerSpeed(Skyfaller skyfaller)
        {
            SkyfallerProperties sf = skyfaller.def.skyfaller;
            if (sf.speedCurve == null)
            {
                return sf.speed;
            }

            float t = skyfaller.def.skyfaller.reversed
                ? Mathf.Clamp01((float)skyfaller.ticksToImpact / Mathf.Max(1, skyfaller.LeaveMapAfterTicks))
                : Mathf.Clamp01(1f - (float)skyfaller.ticksToImpact / Mathf.Max(1, sf.ticksToImpactRange.max));
            return sf.speedCurve.Evaluate(t) * sf.speed;
        }

        /// <summary>
        /// <paramref name="drawPos"/> 为喷口锚点；<paramref name="rotation"/> 为飞行朝向（度）。尾焰视觉沿 <c>rotation+180°</c> / <c>-forward</c> 延伸。
        /// <paramref name="comp01"/> 见 <see cref="ResolveComp01"/>。<paramref name="extendedTrail"/> 控制更长 offset 与更多叠火段。
        /// </summary>
        private void ThrowExhaust(Vector3 drawPos, float rotation, float evaluate, float speed, float comp01, bool extendedTrail)
        {
            float rad = rotation * Mathf.Deg2Rad;
            Vector3 forward = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            float offsetY = extendedTrail ? PropsTyped.extendedOffsetY : PropsTyped.offsetY;
            Vector3 vector = forward * offsetY;
            float density = Mathf.Lerp(PropsTyped.minDensity, 1f, 1f - evaluate);
            SimpleCurve exhaustCurve = PropsTyped.exhaustCurve;
            float exhaustBase = exhaustCurve != null ? exhaustCurve.Evaluate(evaluate) : 0f;
            SimpleCurve smokeCurve = PropsTyped.smokeCurve;
            float smokeBase = smokeCurve != null ? smokeCurve.Evaluate(evaluate) : 0f;
            int batches = Mathf.Clamp(PropsTyped.exhaustBatches, 1, 8);
            int plumeSegments = Mathf.Clamp(
                extendedTrail ? PropsTyped.extendedPlumeExtraSegments : PropsTyped.plumeExtraSegments,
                1,
                12);
            float w = PropsTyped.flameWidthMultiplier;
            Map map = parent.MapHeld;
            bool inInitialLateralPhase = evaluate <= Mathf.Clamp01(PropsTyped.initialLateralPhasePct);
            float secondStageStart = Mathf.Clamp01(PropsTyped.initialLateralPhasePct);
            float secondStageRampEnd = Mathf.Min(1f, secondStageStart + Mathf.Clamp(PropsTyped.secondStageFlameRampPct, 0.001f, 1f));
            float secondStageRamp01 = Mathf.InverseLerp(secondStageStart, secondStageRampEnd, evaluate);
            float flameScaleRamp = Mathf.Lerp(
                Mathf.Max(0.01f, PropsTyped.secondStageFlameMinScaleMultiplier),
                1f,
                secondStageRamp01);
            float flameLengthRamp = Mathf.Lerp(
                Mathf.Max(0.01f, PropsTyped.secondStageFlameMinLengthMultiplier),
                1f,
                secondStageRamp01);
            float highSpeed01 = Mathf.InverseLerp(
                Mathf.Clamp01(PropsTyped.highSpeedTaperStartPct),
                Mathf.Clamp01(PropsTyped.highSpeedTaperEndPct),
                evaluate);
            float flameLifetimeMul = Mathf.Lerp(1f, Mathf.Clamp(PropsTyped.highSpeedFlameLifetimeMultiplier, 0.1f, 1f), highSpeed01);
            float smokeLifetimeMul = Mathf.Lerp(1f, Mathf.Clamp(PropsTyped.highSpeedSmokeLifetimeMultiplier, 0.1f, 1f), highSpeed01);
            float highSpeedLengthMul = Mathf.Lerp(1f, Mathf.Clamp(PropsTyped.highSpeedLengthMultiplier, 0.1f, 1f), highSpeed01);
            float flameSpawnChance = Mathf.Lerp(1f, Mathf.Clamp01(PropsTyped.highSpeedFlameSpawnChance), comp01);
            float smokeSpawnChance = Mathf.Lerp(1f, Mathf.Clamp01(PropsTyped.highSpeedSmokeSpawnChance), comp01);
            float flameSizeMul = Mathf.Lerp(1f, Mathf.Clamp(PropsTyped.highSpeedFlameSizeMultiplier, 0.1f, 1f), comp01);
            flameLifetimeMul *= Mathf.Lerp(1f, 0.78f, comp01);
            smokeLifetimeMul *= Mathf.Lerp(1f, 0.85f, comp01);
            highSpeedLengthMul *= Mathf.Lerp(1f, 0.82f, comp01);
            // 第二阶段：强制每 tick 至少一批尾向单束，避免高速段概率压缩导致「看不见主焰」。
            bool continuousFlameActive = PropsTyped.enableContinuousFlame && !inInitialLateralPhase;
            float forcedFlameSpawnChance = continuousFlameActive ? 1f : flameSpawnChance;

            for (int i = 0; i < batches; i++)
            {
                float randSigned = Rand.RangeSeeded(-1f, 1f, GenTicks.TicksGame + i);
                FleckDef exhaustFleck = PropsTyped.exhaustFleck;
                if (exhaustFleck != null)
                {
                    if (!inInitialLateralPhase && Rand.Value <= forcedFlameSpawnChance)
                    {
                        // 单束：每 tick 只取 i==0 一批；尾向零速，叠火沿喷管 vector 步进。
                        bool singleBeamMode = continuousFlameActive || comp01 >= 0.45f;
                        if (singleBeamMode && i > 0)
                        {
                            continue;
                        }

                        float flameScale = Rand.Gaussian(exhaustBase, 1f) * density * w * flameScaleRamp;
                        if (continuousFlameActive)
                        {
                            // 连续模式不再叠静态链；对主束 scale 设下限，避免高斯抽到「极小火点」。
                            float minBeam = Mathf.Max(0.82f, 0.52f * Mathf.Max(1f, exhaustBase)) * density * w * flameScaleRamp;
                            flameScale = Mathf.Max(flameScale, minBeam);
                        }

                        FleckCreationData flameData = FleckMaker.GetDataStatic(drawPos + vector, map, exhaustFleck, 1f);
                        flameData.scale = flameScale * flameSizeMul;
                        float jitterAngle = continuousFlameActive ? 0.12f : Mathf.Lerp(5f, 0.35f, comp01);
                        flameData.velocityAngle = rotation + randSigned * jitterAngle;
                        flameData.velocitySpeed = Mathf.Clamp01(exhaustBase - flameScale) * speed * Mathf.Lerp(1f, 0.1f, comp01);
                        if (singleBeamMode)
                        {
                            flameData.velocityAngle = continuousFlameActive ? rotation + 180f : rotation;
                            flameData.velocitySpeed = 0f;
                        }
                        float speedRatio = speed > 1e-5f ? Mathf.Clamp01(flameData.velocitySpeed / speed) : 0f;
                        float invSpeed = 1f - speedRatio;
                        flameData.solidTimeOverride = 0.4f * (density + invSpeed) * flameLifetimeMul;
                        if (singleBeamMode)
                        {
                            flameData.solidTimeOverride *= 0.38f;
                        }
                        flameData.targetSize = flameScale * density * 3f * w * flameScaleRamp * flameSizeMul * Mathf.Lerp(1f, 0.85f, comp01);
                        map.flecks.CreateFleck(flameData);
                        Vector3 step = vector * PropsTyped.plumeSegmentScale * flameLengthRamp * highSpeedLengthMul;
                        int rampedSegments = Mathf.Clamp(
                            Mathf.RoundToInt(plumeSegments * flameLengthRamp * highSpeedLengthMul),
                            1,
                            12);
                        if (singleBeamMode && !continuousFlameActive)
                        {
                            rampedSegments = 0;
                        }
                        if (continuousFlameActive)
                        {
                            rampedSegments = Mathf.Max(rampedSegments, 3);
                        }
                        for (int j = 0; j < rampedSegments; j++)
                        {
                            flameData.spawnPosition += step;
                            map.flecks.CreateFleck(flameData);
                        }
                    }
                }

                FleckDef smokeFleck = PropsTyped.smokeFleck;
                if (smokeFleck != null)
                {
                    if (!inInitialLateralPhase && Rand.Value <= smokeSpawnChance)
                    {
                        FleckCreationData smokeData = FleckMaker.GetDataStatic(drawPos + vector, map, smokeFleck, 1f);
                        for (int k = 0; k < 2; k++)
                        {
                            float spread = randSigned * density * 180f - 180f;
                            float spreadAbs = 1f - Mathf.Abs(spread) / 180f;
                            smokeData.scale = Rand.Gaussian(smokeBase, 2f) * (spreadAbs + density);
                            smokeData.velocityAngle = rotation + spread + Rand.Gaussian(0f, 20f) * density;
                            smokeData.velocitySpeed = (smokeBase - smokeData.scale) * (spreadAbs + density);
                            smokeData.rotationRate = smokeData.velocitySpeed;
                            smokeData.solidTimeOverride = smokeData.scale * (density + spreadAbs) * smokeLifetimeMul;
                            map.flecks.CreateFleck(smokeData);
                        }
                    }
                }
            }
        }

        /// <summary>高速段 evaluate 与当前速度的归一化强度，用于寿命/尺寸/概率等收敛。</summary>
        private float ResolveComp01(float evaluate, float speed)
        {
            float highSpeed01 = Mathf.InverseLerp(
                Mathf.Clamp01(PropsTyped.highSpeedTaperStartPct),
                Mathf.Clamp01(PropsTyped.highSpeedTaperEndPct),
                evaluate);
            float speedComp01 = Mathf.InverseLerp(
                Mathf.Max(0f, PropsTyped.speedCompStart),
                Mathf.Max(PropsTyped.speedCompStart + 0.001f, PropsTyped.speedCompEnd),
                speed);
            return Mathf.Clamp01(Mathf.Max(highSpeed01, speedComp01));
        }
    }
}
