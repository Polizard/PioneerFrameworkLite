using System;
using System.Collections.Generic;
using System.Diagnostics;
using PioneerFramework.Celestial;
using Verse;

namespace PioneerFramework
{
    public partial class SpaceGameComp
    {
        private CelestialRuntime? sharedCelestialRuntime;
        private bool celestialShipStepFaulted;
        public IReadOnlyList<CelestialError> CelestialPhysicsErrors { get; private set; } = Array.Empty<CelestialError>();

        private CelestialResult<bool> AttachCelestialParticipants(CelestialRuntime runtime, TimeScaleManager clock, CelestialBodyManager manager)
        {
            if (ReferenceEquals(sharedCelestialRuntime, runtime)) return CelestialResult.Ok(true);
            // B（2026-11-02）：船退出天体事务。天体只提交自己的时钟/提供商区间；
            // 船在区间提交之后、以已提交区间在实时图上跑（没有 ShipParticipant / 2PC 副本）。
            var result = runtime.Coordinator.AttachHostParticipants(new[] { clock.CreateCelestialClockParticipant() });
            if (result.Success) sharedCelestialRuntime = runtime;
            return result;
        }

        private void AdjustPhysicsHoverCount(int delta) => hoverActiveCount += delta;

        /// <summary>诊断消息（P1-1：Release 编译期整体切除；调用点连插值参数都不求值）。</summary>
        [Conditional("PF_DIAG")]
        internal void PhysicsLogMessage(string message) => WritePhysicsLog(0, message);
        internal void PhysicsLogWarning(string message) => WritePhysicsLog(1, message);
        private void PhysicsLogError(string message) => WritePhysicsLog(2, message);
        private void PhysicsMessage(string text, MessageTypeDef type, bool historical = true)
            => Verse.Messages.Message(text, type, historical);
        private static void WritePhysicsLog(int kind, string message)
        {
            if (kind == 0) Log.Message(message);
            else if (kind == 1) Log.Warning(message);
            else Log.Error(message);
        }

        // Production shared boundary: the provider and the real clock publish the interval first.
        // Ships run afterwards, on the committed interval (B, 2026-11-02).
        internal CelestialResult<bool> AdvanceCelestialPhysicsInterval(CelestialRuntime runtime, TimeScaleManager clock)
        {
            var interval = clock.CurrentPhysicsStep;
            if (!interval.HasValue) return CelestialResult.Reject<bool>(new CelestialError("ClockIntervalMissing"));
            var result = runtime.Advance(new PhysicsIntervalRequest(runtime.WorldSessionId, runtime.CurrentStamp.TopologyRevision,
                interval.Value.StartTick, interval.Value.EndTick));
            if (!result.Success) clock.CancelCelestialPhysicsStep();
            return result;
        }
    }
}
