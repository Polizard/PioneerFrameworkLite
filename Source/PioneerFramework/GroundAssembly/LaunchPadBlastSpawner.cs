using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>发射台点火 Fleck：小型台两层全向地面烟；大型台台心效果 + 两侧导流槽柱状烟。尺度约 0.6×。</summary>
    internal static class LaunchPadBlastSpawner
    {
        /// <summary>小型台地面烟持续 tick。</summary>
        internal const int LaunchPadSmallGroundSmokeDurationTicks = 90;

        /// <summary>大型台导流槽柱状烟持续 tick（较小型台多 90）。</summary>
        internal const int LaunchPadLDiversionColumnDurationTicks = LaunchPadSmallGroundSmokeDurationTicks + 90;

        /// <summary>导流槽烟水平速度倍率。</summary>
        private const float LaunchPadLChannelColumnSpeedMul = 0.58f;

        /// <summary>导流槽烟实体时间倍率（越小消散越快）。</summary>
        private const float LaunchPadLChannelColumnSolidTimeMul = 0.32f;

        /// <summary>曲线采样：进度上限 75%。</summary>
        private const float LaunchPadGroundSmokeProgressCap = 0.75f;

        /// <summary>偏移与粒子尺度倍率（0.6）。</summary>
        private const float LaunchPadGroundSmokeRangeScale = 0.6f;

        /// <summary>频率曲线换算每 tick 生成数时的除数。</summary>
        private const float LaunchPadGroundSmokeFreqDivisor = 22f;

        /// <summary>导流槽柱状烟速度角扰动（度），保持窄束。</summary>
        private const float LaunchPadLChannelColumnAngleJitterDeg = 4f;

        internal const int CenterSmokePuffs = 22;

        /// <summary>大型台台心烟雾发数（仅燃料口格，不铺满占格）。</summary>
        private const int LaunchPadLCenterSmokePuffs = 18;

        internal static void SpawnCenterBurst(Map map, IntVec3 center)
        {
            if (map == null)
            {
                return;
            }

            SpawnCenterFlashHeatAndFire(map, center);

            Vector3 c = center.ToVector3Shifted();
            for (int i = 0; i < CenterSmokePuffs; i++)
            {
                Vector3 p = c + Gen.RandomHorizontalVector(0.65f);
                FleckMaker.ThrowSmoke(p, map, Rand.Range(2.05f, 2.75f));
            }

            for (int j = 0; j < 5; j++)
            {
                FleckMaker.ThrowDustPuffThick(c + Gen.RandomHorizontalVector(0.38f), map, 4.6f, Color.Lerp(Color.gray, new Color(0.35f, 0.35f, 0.42f), Rand.Value));
            }
        }

        /// <summary>小型台台心：闪光/热光/火与厚尘；地面烟由 <see cref="MapComponent_LaunchBlastFX"/> 调度。</summary>
        internal static void SpawnCenterBurstLaunchPadS(Map map, IntVec3 center)
        {
            if (map == null)
            {
                return;
            }

            SpawnCenterFlashHeatAndFire(map, center);

            Vector3 c = center.ToVector3Shifted();
            for (int j = 0; j < 5; j++)
            {
                FleckMaker.ThrowDustPuffThick(c + Gen.RandomHorizontalVector(0.38f), map, 4.6f, Color.Lerp(Color.gray, new Color(0.35f, 0.35f, 0.42f), Rand.Value));
            }
        }

        /// <summary>小型台单 tick 混合地面烟（仅 <c>Smoke</c>）：全向底层烟 + 全向浓团烟；<paramref name="tickProgress01"/> 0～1。柱状喷流由大型台导流槽负责。</summary>
        internal static void SpawnLaunchPadSmallGroundSmokeTick(Map map, IntVec3 center, float rotationDeg, float tickProgress01)
        {
            if (map == null)
            {
                return;
            }

            float u = Mathf.Clamp01(tickProgress01) * LaunchPadGroundSmokeProgressCap;
            Vector3 baseCenter = center.ToVector3Shifted();
            if (!baseCenter.ShouldSpawnMotesAt(map))
            {
                return;
            }

            float sc = LaunchPadGroundSmokeRangeScale;

            // --- 1) 全向底层烟，局部偏移 (1,2) ---
            float fOmni = SampleCurveLinear(PadIgnitionSmoke_SmokeOmniFrequency, u);
            int nOmni = FreqToSpawnCount(fOmni);
            if (nOmni > 0)
            {
                float sz = SampleCurveLinear(PadIgnitionSmoke_SmokeOmniSize, u) * sc;
                float spd = 2.4f * sc;
                float rotR = SampleCurveLinear(PadIgnitionSmoke_SmokeOmniRotationRate, u);
                Vector3 spawnOff = PadLocalXZToWorldOffset(rotationDeg, 1f * sc, 2f * sc);
                for (int i = 0; i < nOmni; i++)
                {
                    Vector3 p = baseCenter + spawnOff + Gen.RandomHorizontalVector(0.38f * sc);
                    ThrowOneSmokeFleck(map, p, Rand.Range(0f, 360f), spd * Rand.Range(0.85f, 1.15f), sz * Rand.Range(0.88f, 1.12f), rotR);
                }
            }

            // --- 2) 全向浓团（Smoke 放大尺度/速度）---
            float fEx = SampleCurveLinear(PadIgnitionSmoke_RocketExhaustFrequency, u);
            int nEx = FreqToSpawnCount(fEx);
            if (nEx > 0)
            {
                float sz = SampleCurveLinear(PadIgnitionSmoke_RocketExhaustSize, u) * sc;
                float spd = SampleCurveLinear(PadIgnitionSmoke_RocketExhaustSpeed, u) * sc;
                if (spd >= 0.05f)
                {
                    float rotR = SampleCurveLinear(PadIgnitionSmoke_RocketExhaustRotationRate, u);
                    Vector3 spawnOff = PadLocalXZToWorldOffset(rotationDeg, 1f * sc, 2f * sc);
                    for (int i = 0; i < nEx; i++)
                    {
                        Vector3 p = baseCenter + spawnOff + Gen.RandomHorizontalVector(0.32f * sc);
                        ThrowOneSmokeFleck(map, p, Rand.Range(0f, 360f), spd * Rand.Range(0.9f, 1.1f), sz * Rand.Range(0.9f, 1.1f), rotR);
                    }
                }
            }
        }

        private static readonly (float t, float v)[] PadIgnitionSmoke_SmokeOmniFrequency =
        {
            (0f, 200f), (0.2f, 300f), (0.3f, 0f), (0.4f, 0f), (0.6f, 0f)
        };

        private static readonly (float t, float v)[] PadIgnitionSmoke_SmokeOmniSize =
        {
            (0f, 6f), (0.25f, 4f), (0.75f, 4f), (1f, 3f)
        };

        private static readonly (float t, float v)[] PadIgnitionSmoke_SmokeOmniRotationRate =
        {
            (0f, 3f), (1f, 1f)
        };

        private static readonly (float t, float v)[] PadIgnitionSmoke_RocketExhaustFrequency =
        {
            (0f, 100f), (0.1f, 200f), (0.2f, 300f), (0.3f, 200f), (0.4f, 0f), (0.5f, 0f)
        };

        private static readonly (float t, float v)[] PadIgnitionSmoke_RocketExhaustSize =
        {
            (0f, 6f), (0.25f, 5f), (0.75f, 5f), (1f, 5f)
        };

        private static readonly (float t, float v)[] PadIgnitionSmoke_RocketExhaustSpeed =
        {
            (0f, 7f), (0.2f, 7f), (0.3f, 0f), (0.4f, 0f), (1f, 0f)
        };

        private static readonly (float t, float v)[] PadIgnitionSmoke_RocketExhaustRotationRate =
        {
            (0f, 3f), (1f, 1f)
        };

        private static readonly (float t, float v)[] PadIgnitionSmoke_RocketExhaustShortFrequency =
        {
            (0.2f, 0f), (0.3f, 250f), (0.4f, 250f), (1f, 250f)
        };

        private static readonly (float t, float v)[] PadIgnitionSmoke_RocketExhaustShortSize =
        {
            (0f, 3.5f), (1f, 3.5f)
        };

        private static readonly (float t, float v)[] PadIgnitionSmoke_RocketExhaustShortSpeed =
        {
            (0.2f, 30f), (0.75f, 40f), (1f, 40f)
        };

        private static readonly (float t, float v)[] PadIgnitionSmoke_RocketExhaustShortRotationRate =
        {
            (0f, 3f), (1f, 1f)
        };

        private static float SampleCurveLinear((float t, float v)[] pts, float u)
        {
            if (pts == null || pts.Length == 0)
            {
                return 0f;
            }

            if (u <= pts[0].t)
            {
                return pts[0].v;
            }

            for (int i = 1; i < pts.Length; i++)
            {
                if (u <= pts[i].t)
                {
                    float t0 = pts[i - 1].t;
                    float t1 = pts[i].t;
                    float blend = t1 > t0 ? (u - t0) / (t1 - t0) : 0f;
                    return Mathf.Lerp(pts[i - 1].v, pts[i].v, blend);
                }
            }

            return pts[pts.Length - 1].v;
        }

        private static int FreqToSpawnCount(float frequency)
        {
            if (frequency <= 1e-3f)
            {
                return 0;
            }

            return Mathf.Clamp(Mathf.RoundToInt(frequency / LaunchPadGroundSmokeFreqDivisor), 0, 18);
        }

        private static Vector3 PadLocalXZToWorldOffset(float rotationDeg, float localX, float localZ)
        {
            float rad = rotationDeg * Mathf.Deg2Rad;
            Vector3 forward = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            Vector3 right = new Vector3(Mathf.Cos(rad), 0f, -Mathf.Sin(rad));
            return right * localX + forward * localZ;
        }

        private static void ThrowOneSmokeFleck(Map map, Vector3 position, float velocityAngleDeg, float velocitySpeed, float scale, float rotationRate)
        {
            if (!position.ShouldSpawnMotesAt(map))
            {
                return;
            }

            FleckCreationData data = FleckMaker.GetDataStatic(position, map, FleckDefOf.Smoke, scale);
            data.velocityAngle = velocityAngleDeg;
            data.velocitySpeed = velocitySpeed;
            data.rotationRate = rotationRate * Rand.Range(0.85f, 1.15f);
            data.solidTimeOverride = data.scale * Rand.Range(1.65f, 2.45f);
            map.flecks.CreateFleck(data);
        }

        /// <summary>
        /// 解析大型台（长边 ≥15）两侧导流槽代表格；与建筑旋转后的 <see cref="Building.OccupiedRect"/> 一致。
        /// </summary>
        internal static bool TryGetLaunchPadLDiversionChannelCells(Building building, out IntVec3 leftCell, out IntVec3 rightCell)
        {
            leftCell = IntVec3.Invalid;
            rightCell = IntVec3.Invalid;
            if (building == null || !building.Spawned || building.Map == null)
            {
                return false;
            }

            CellRect rect = building.OccupiedRect();
            if (Mathf.Max(rect.Width, rect.Height) < 15)
            {
                return false;
            }

            bool longAlongX = rect.Width >= rect.Height;
            leftCell = DiversionChannelRepresentativeCell(rect, longAlongX, leftSide: true);
            rightCell = DiversionChannelRepresentativeCell(rect, longAlongX, leftSide: false);
            return true;
        }

        /// <summary>大型台单 tick：两侧导流槽柱状高速窄角烟；方向自燃料口经槽位向外。</summary>
        internal static void SpawnLaunchPadLDiversionColumnSmokeTick(
            Map map,
            IntVec3 fuelPortCenter,
            IntVec3 leftChannelCell,
            IntVec3 rightChannelCell,
            float tickProgress01)
        {
            if (map == null)
            {
                return;
            }

            float u = Mathf.Clamp01(tickProgress01) * LaunchPadGroundSmokeProgressCap;
            float f = SampleCurveLinear(PadIgnitionSmoke_RocketExhaustShortFrequency, u);
            int nTotal = FreqToSpawnCount(f);
            if (nTotal <= 0)
            {
                return;
            }

            float sc = LaunchPadGroundSmokeRangeScale;
            float spd = SampleCurveLinear(PadIgnitionSmoke_RocketExhaustShortSpeed, u) * sc * LaunchPadLChannelColumnSpeedMul;
            if (spd < 0.05f)
            {
                return;
            }

            float sz = SampleCurveLinear(PadIgnitionSmoke_RocketExhaustShortSize, u) * sc;
            float rotR = SampleCurveLinear(PadIgnitionSmoke_RocketExhaustShortRotationRate, u);

            Vector3 c = fuelPortCenter.ToVector3Shifted();
            Vector3 pL = leftChannelCell.ToVector3Shifted();
            Vector3 pR = rightChannelCell.ToVector3Shifted();

            float angL = HorizontalOutflowAngleDegrees(c, pL);
            float angR = HorizontalOutflowAngleDegrees(c, pR);

            int nLeft = (nTotal + 1) / 2;
            int nRight = nTotal - nLeft;
            float jitter = LaunchPadLChannelColumnAngleJitterDeg;

            ThrowChannelColumnPuffs(map, pL, c, angL, nLeft, spd, sz, rotR, sc, jitter);
            ThrowChannelColumnPuffs(map, pR, c, angR, nRight, spd, sz, rotR, sc, jitter);
        }

        private static void ThrowLaunchPadLChannelSmokeFleck(
            Map map,
            Vector3 position,
            float velocityAngleDeg,
            float velocitySpeed,
            float scale,
            float rotationRate)
        {
            if (!position.ShouldSpawnMotesAt(map))
            {
                return;
            }

            FleckCreationData data = FleckMaker.GetDataStatic(position, map, FleckDefOf.Smoke, scale);
            data.velocityAngle = velocityAngleDeg;
            data.velocitySpeed = velocitySpeed;
            data.rotationRate = rotationRate * Rand.Range(0.85f, 1.15f);
            data.solidTimeOverride = data.scale * Rand.Range(1.65f, 2.45f) * LaunchPadLChannelColumnSolidTimeMul;
            map.flecks.CreateFleck(data);
        }

        private static void ThrowChannelColumnPuffs(
            Map map,
            Vector3 channelWorld,
            Vector3 fuelWorld,
            float baseAngleDeg,
            int count,
            float speed,
            float scale,
            float rotationRate,
            float rangeScale,
            float angleJitterDeg)
        {
            if (count <= 0 || !channelWorld.ShouldSpawnMotesAt(map))
            {
                return;
            }

            Vector3 outward = channelWorld - fuelWorld;
            outward.y = 0f;
            if (outward.sqrMagnitude < 1e-5f)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                Vector3 p = channelWorld + Gen.RandomHorizontalVector(0.16f * rangeScale);
                p += outward.normalized * (Rand.Range(0f, 0.12f) * rangeScale);
                float ang = baseAngleDeg + Rand.Range(-angleJitterDeg, angleJitterDeg);
                ThrowLaunchPadLChannelSmokeFleck(map, p, ang, speed * Rand.Range(0.92f, 1.08f), scale * Rand.Range(0.92f, 1.08f), rotationRate);
            }
        }

        /// <summary>水平面内自燃料口经导流槽向外的 Fleck 速度角（度）。</summary>
        private static float HorizontalOutflowAngleDegrees(Vector3 fuelWorldShifted, Vector3 channelWorldShifted)
        {
            Vector3 d = channelWorldShifted - fuelWorldShifted;
            d.y = 0f;
            if (d.sqrMagnitude < 1e-6f)
            {
                return 0f;
            }

            d.Normalize();
            return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        private static IntVec3 DiversionChannelRepresentativeCell(CellRect rect, bool longAlongX, bool leftSide)
        {
            if (longAlongX)
            {
                int x = leftSide ? rect.minX + 1 : rect.minX + 12;
                int z = (rect.minZ + rect.maxZ) / 2;
                return new IntVec3(x, 0, z);
            }

            int zCol = leftSide ? rect.minZ + 1 : rect.minZ + 12;
            int xMid = (rect.minX + rect.maxX) / 2;
            return new IntVec3(xMid, 0, zCol);
        }

        /// <summary>
        /// 大型台台心：与小型台相同的闪光/热光/火；烟雾与厚尘仅在燃料口所在格附近，不占用整面占格。
        /// </summary>
        internal static void SpawnCenterBurstLaunchPadL(Map map, IntVec3 center)
        {
            if (map == null)
            {
                return;
            }

            SpawnCenterFlashHeatAndFire(map, center);

            Vector3 c = center.ToVector3Shifted();
            for (int i = 0; i < LaunchPadLCenterSmokePuffs; i++)
            {
                FleckMaker.ThrowSmoke(c + Gen.RandomHorizontalVector(0.55f), map, Rand.Range(2.0f, 2.75f));
            }

            for (int j = 0; j < 5; j++)
            {
                FleckMaker.ThrowDustPuffThick(c + Gen.RandomHorizontalVector(0.38f), map, 4.6f, Color.Lerp(Color.gray, new Color(0.35f, 0.35f, 0.42f), Rand.Value));
            }
        }

        private static void SpawnCenterFlashHeatAndFire(Map map, IntVec3 center)
        {
            Vector3 c = center.ToVector3Shifted();
            if (c.ShouldSpawnMotesAt(map))
            {
                for (int e = 0; e < 4; e++)
                {
                    FleckMaker.ThrowExplosionInterior(c + Gen.RandomHorizontalVector(0.22f), map, FleckDefOf.ExplosionFlash);
                }

                FleckMaker.ThrowHeatGlow(center, map, 0.95f);
                for (int h = 0; h < 3; h++)
                {
                    FleckMaker.ThrowHeatGlow((center.ToVector3Shifted() + Gen.RandomHorizontalVector(0.35f)).ToIntVec3(), map, Rand.Range(0.55f, 0.75f));
                }

                for (int f = 0; f < 5; f++)
                {
                    FleckMaker.ThrowFireGlow(c + Gen.RandomHorizontalVector(0.2f), map, Rand.Range(0.55f, 0.85f));
                }
            }
        }
    }
}
