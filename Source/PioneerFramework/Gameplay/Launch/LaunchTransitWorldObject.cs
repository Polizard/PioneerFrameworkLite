using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.Launch
{
    public enum LaunchTransitStatus
    {
        InFlight,
        Failed,
        Completed
    }

    /// <summary>起飞过场的存档载体。Tile 保持起飞地块，DrawPos 在世界视图中移动。</summary>
    public class LaunchTransitWorldObject : WorldObject
    {
        private int spacecraftMapId = -1;
        private string spacecraftId = string.Empty;
        private int bodyId = -1;
        private int factionId = -1;
        private SystemVec3 bodyCenterSystem;
        private SystemVec3 startPositionSystem;
        private OrbitState targetOrbit;
        private int startTick;
        private int durationTicks;
        private LaunchTransitStatus status;
        private string failureReason = string.Empty;
        private double stoppedProgress;
        private SystemVec3 stoppedPositionSystem;

        public int SpacecraftMapId => spacecraftMapId;
        public string SpacecraftId => spacecraftId;
        public int BodyId => bodyId;
        public int FactionId => factionId;
        public OrbitState TargetOrbit => targetOrbit;
        public LaunchTransitStatus Status => status;
        public string FailureReason => failureReason;

        public void Initialize(int mapId, string shipId, int centralBodyId, int ownerFactionId,
            DVector3 bodyCenter, DVector3 startPosition, OrbitState orbit, int tick, int ticks)
        {
            spacecraftMapId = mapId;
            spacecraftId = shipId;
            bodyId = centralBodyId;
            factionId = ownerFactionId;
            bodyCenterSystem = (SystemVec3)bodyCenter;
            startPositionSystem = (SystemVec3)startPosition;
            targetOrbit = orbit;
            startTick = tick;
            durationTicks = ticks;
            status = LaunchTransitStatus.InFlight;
            failureReason = string.Empty;
            stoppedProgress = 0.0;
            stoppedPositionSystem = (SystemVec3)startPosition;
        }

        public double ProgressAt(int tick) => status == LaunchTransitStatus.Failed
            ? stoppedProgress
            : LaunchTransitPath.Progress(startTick, durationTicks, tick);

        public DVector3 PositionAt(int tick)
        {
            DVector3 center = Current.Game?.GetComponent<CelestialBodyManager>() == null
                ? bodyCenterSystem.v
                : ViewFrame.BodySystemPosition(bodyId, TimeScaleManager.GetPhysicsTickSafe());
            return PositionAt(tick, center);
        }

        public DVector3 PositionAt(int tick, DVector3 currentBodyCenter) =>
            status == LaunchTransitStatus.Failed ? stoppedPositionSystem.v
                : LaunchTransitPath.PositionFromMovingBody(bodyCenterSystem.v, currentBodyCenter,
                    startPositionSystem.v, targetOrbit.positionSystem.v, ProgressAt(tick));

        public bool IsDue(int tick) => status == LaunchTransitStatus.InFlight &&
            LaunchTransitPath.Progress(startTick, durationTicks, tick) >= 1.0;

        public void MarkFailed(string reason, int tick)
        {
            stoppedPositionSystem = (SystemVec3)PositionAt(tick);
            stoppedProgress = LaunchTransitPath.Progress(startTick, durationTicks, tick);
            failureReason = reason ?? string.Empty;
            status = LaunchTransitStatus.Failed;
        }

        internal void MarkCompleted() => status = LaunchTransitStatus.Completed;

        public override Vector3 DrawPos
        {
            get
            {
                int tick = Find.TickManager?.TicksGame ?? startTick;
                return ViewFrame.ToViewUnity(PositionAt(tick), TimeScaleManager.GetPhysicsTickSafe());
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref spacecraftMapId, "spacecraftMapId", -1);
            Scribe_Values.Look(ref spacecraftId, "spacecraftId", string.Empty);
            Scribe_Values.Look(ref bodyId, "bodyId", -1);
            Scribe_Values.Look(ref factionId, "factionId", -1);
            SystemVec3.Scribe("bodyCenterSystem", ref bodyCenterSystem);
            SystemVec3.Scribe("startPositionSystem", ref startPositionSystem);
            Scribe_Deep.Look(ref targetOrbit, "targetOrbit");
            Scribe_Values.Look(ref startTick, "startTick", 0);
            Scribe_Values.Look(ref durationTicks, "durationTicks", 0);
            Scribe_Values.Look(ref status, "status", LaunchTransitStatus.InFlight);
            Scribe_Values.Look(ref failureReason, "failureReason", string.Empty);
            Scribe_Values.Look(ref stoppedProgress, "stoppedProgress", 0.0);
            SystemVec3.Scribe("stoppedPositionSystem", ref stoppedPositionSystem);
        }
    }
}
