using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>发射台点火 Fleck 调度：小型台两层全向地面烟；大型台台心 + 两侧导流槽柱状烟。</summary>
    public class MapComponent_LaunchBlastFX : MapComponent
    {
        private struct BlastWave
        {
            public IntVec3 center;
            public bool isLaunchpadL;

            /// <summary>小型台地面烟：台体朝向。</summary>
            public float padRotationDeg;

            public int effectStartTick;
            public int effectEndExclusive;

            public bool hasLaunchpadLDiversionCells;
            public IntVec3 launchpadLChannelLeft;
            public IntVec3 launchpadLChannelRight;
        }

        private List<BlastWave> waves = null!;

        public MapComponent_LaunchBlastFX(Map map)
            : base(map)
        {
        }

        /// <summary>小型台：台心 + 持续地面烟。大型台：台心 + 持续导流槽柱状烟。</summary>
        public void ScheduleBlast(Building launchpad)
        {
            if (launchpad == null || !launchpad.Spawned)
            {
                return;
            }

            IntVec3 center = FuelingPortUtility.GetFuelingPortCell(launchpad);

            IntVec3 chL = default;
            IntVec3 chR = default;
            bool hasChannels = LaunchPadBlastSpawner.TryGetLaunchPadLDiversionChannelCells(launchpad, out chL, out chR);
            bool isL = hasChannels;

            if (waves == null)
            {
                waves = new List<BlastWave>();
            }

            waves.Add(new BlastWave
            {
                center = center,
                isLaunchpadL = isL,
                padRotationDeg = launchpad.Rotation.AsAngle,
                effectStartTick = 0,
                effectEndExclusive = 0,
                hasLaunchpadLDiversionCells = hasChannels,
                launchpadLChannelLeft = chL,
                launchpadLChannelRight = chR
            });
        }

        public override void MapComponentTick()
        {
            if (waves == null || waves.Count == 0)
            {
                return;
            }

            int t = Find.TickManager.TicksGame;

            for (int i = waves.Count - 1; i >= 0; i--)
            {
                BlastWave w = waves[i];
                int dur = w.isLaunchpadL
                    ? LaunchPadBlastSpawner.LaunchPadLDiversionColumnDurationTicks
                    : LaunchPadBlastSpawner.LaunchPadSmallGroundSmokeDurationTicks;

                if (w.effectEndExclusive == 0)
                {
                    w.effectStartTick = t;
                    w.effectEndExclusive = t + dur;
                    if (!w.isLaunchpadL)
                    {
                        LaunchPadBlastSpawner.SpawnCenterBurstLaunchPadS(map, w.center);
                    }
                    else
                    {
                        LaunchPadBlastSpawner.SpawnCenterBurstLaunchPadL(map, w.center);
                    }

                    waves[i] = w;
                }

                if (t < w.effectEndExclusive)
                {
                    float tickProgress01 = dur > 1f ? (t - w.effectStartTick) / (dur - 1f) : 0f;
                    if (!w.isLaunchpadL)
                    {
                        LaunchPadBlastSpawner.SpawnLaunchPadSmallGroundSmokeTick(map, w.center, w.padRotationDeg, tickProgress01);
                    }
                    else if (w.hasLaunchpadLDiversionCells)
                    {
                        LaunchPadBlastSpawner.SpawnLaunchPadLDiversionColumnSmokeTick(
                            map,
                            w.center,
                            w.launchpadLChannelLeft,
                            w.launchpadLChannelRight,
                            tickProgress01);
                    }

                    waves[i] = w;
                }

                if (t >= w.effectEndExclusive)
                {
                    waves.RemoveAt(i);
                }
            }
        }
    }
}
