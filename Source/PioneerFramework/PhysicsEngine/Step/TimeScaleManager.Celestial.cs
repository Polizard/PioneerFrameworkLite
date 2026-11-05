using System;
using System.Linq;
using PioneerFramework.Celestial;

namespace PioneerFramework
{
    public partial class TimeScaleManager
    {
        private CelestialRuntime? renderCelestialRuntime;
        private long renderCelestialTopology = -1;
        private double renderCelestialFloor;

        private (double Minimum, double Maximum)? GetCelestialRenderBounds()
        {
            if (celestialRef.Get()?.TryGetCelestialRuntime(out var runtime) != true)
            {
                renderCelestialRuntime = null;
                return null;
            }
            var stamp = runtime.CurrentStamp;
            if (!ReferenceEquals(renderCelestialRuntime, runtime) || renderCelestialTopology != stamp.TopologyRevision)
            {
                renderCelestialRuntime = runtime;
                renderCelestialTopology = stamp.TopologyRevision;
                // A restored provider or a changed topology may retain only this committed instant.
                // Discard a cached time from the previous history before any consumer samples it.
                renderCelestialFloor = stamp.PhysicalSeconds;
                renderClockFrame = int.MinValue;
            }
            return (renderCelestialFloor, stamp.PhysicalSeconds);
        }

        private static double BoundRenderTime(double seconds, (double Minimum, double Maximum)? bounds)
            => bounds.HasValue ? Math.Clamp(seconds, bounds.Value.Minimum, bounds.Value.Maximum) : seconds;

        internal CelestialResult<bool> InitializeCelestialWorldClock(double seconds)
        {
            if (batchActive || activeStep.HasValue || timeline != null && timeline.CommittedPhysicsTick != seconds)
                return CelestialResult.Reject<bool>(new CelestialError("ClockStateMismatch"));
            EnsureCalendarBaseline();
            timeline ??= new PhysicsTimeLine(Verse.Find.TickManager.TicksGame, seconds, currentLevel);
            return CelestialResult.Ok(true);
        }
        internal ICelestialStepParticipant CreateCelestialClockParticipant() => new CelestialClockParticipant(this);
        internal void CancelCelestialPhysicsStep() => activeStep = null;

        private sealed class CelestialClockParticipant(TimeScaleManager owner) : ICelestialStepParticipant
        {
            public string Id => "PFL.PhysicsClock";
            public CelestialStepParticipantPhase Phase => CelestialStepParticipantPhase.Clock;
            public CelestialResult<ICelestialPreparedStepPart> Prepare(CelestialPreparedStepContext context)
            {
                owner.EnsureTimeline();
                var current = owner.timeline!;
                var request = context.Request;
                if (request.StartSeconds != current.CommittedPhysicsTick) return Reject("ClockStateMismatch");
                if (request.StartSeconds == request.EndSeconds)
                {
                    if (owner.activeStep.HasValue) return Reject("ClockIntervalMismatch");
                    return CelestialResult.Ok<ICelestialPreparedStepPart>(new ClockPart(owner, null, null));
                }
                var step = owner.activeStep;
                if (!owner.batchActive || !step.HasValue || step.Value.StartTick != request.StartSeconds || step.Value.EndTick != request.EndSeconds)
                    return Reject("ClockIntervalMismatch");
                // 预先执行全部时间账本校验和历史裁剪；发布只交换已准备好的对象。
                var next = new PhysicsTimeLine(current.Epochs.ToArray(), current.CommittedGameTick, current.CommittedPhysicsTick);
                next.Commit(step.Value);
                return CelestialResult.Ok<ICelestialPreparedStepPart>(new ClockPart(owner, next, step));
            }
            private static CelestialResult<ICelestialPreparedStepPart> Reject(string code)
                => CelestialResult.Reject<ICelestialPreparedStepPart>(new CelestialError(code));
        }
        private sealed class ClockPart(TimeScaleManager owner, PhysicsTimeLine? prepared, PhysicsStep? expected) : ICelestialPreparedStepPart
        {
            public void Commit()
            {
                if (prepared == null) return;
                owner.timeline = prepared;
                owner.activeStep = null;
            }
            public void Abort()
            {
                if (owner.activeStep == expected) owner.activeStep = null;
            }
        }
    }
}
