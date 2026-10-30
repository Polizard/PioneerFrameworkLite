using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    public partial class SpaceGameComp
    {
        private CelestialRuntime? sharedCelestialRuntime;
        public IReadOnlyList<CelestialError> CelestialPhysicsErrors { get; private set; } = Array.Empty<CelestialError>();
        private CelestialResult<bool> AttachCelestialParticipants(CelestialRuntime runtime, TimeScaleManager clock, CelestialBodyManager manager)
        {
            if (ReferenceEquals(sharedCelestialRuntime, runtime)) return CelestialResult.Ok(true);
            var result = runtime.Coordinator.AttachHostParticipants(new[] { CreateCelestialShipParticipant(clock, manager), clock.CreateCelestialClockParticipant() });
            if (result.Success) sharedCelestialRuntime = runtime;
            return result;
        }
        private ShipPreparation? physicsPreparation;
        internal ICelestialStepParticipant CreateCelestialShipParticipant(TimeScaleManager clock, CelestialBodyManager manager)
            => new ShipParticipant(this, clock, manager);

        private sealed class ShipPreparation(SpaceGameComp owner)
        {
            internal readonly SpaceGameComp Owner = owner;
            internal readonly SteeringController.StepInput Steering = SteeringController.CaptureStepInput();
            internal readonly ConcurrentQueue<(int Kind, string Text)> Diagnostics = new();
            internal readonly List<Action> Messages = new();
            internal int HoverCount = hoverActiveCount;
        }

        private void AdjustPhysicsHoverCount(int delta)
        {
            if (physicsPreparation == null) hoverActiveCount += delta;
            else physicsPreparation.HoverCount += delta;
        }

        internal void PhysicsLogMessage(string message) => PhysicsLog(0, message);
        internal void PhysicsLogWarning(string message) => PhysicsLog(1, message);
        private void PhysicsLogError(string message) => PhysicsLog(2, message);
        private void PhysicsLog(int kind, string message)
        {
            if (physicsPreparation != null) physicsPreparation.Diagnostics.Enqueue((kind, message));
            else WritePhysicsLog(kind, message);
        }
        private void PhysicsMessage(string text, MessageTypeDef type, bool historical = true)
        {
            if (physicsPreparation == null) Verse.Messages.Message(text, type, historical);
            else physicsPreparation.Messages.Add(() => Verse.Messages.Message(text, type, historical));
        }
        private static void WritePhysicsLog(int kind, string message)
        {
            if (kind == 0) Log.Message(message);
            else if (kind == 1) Log.Warning(message);
            else Log.Error(message);
        }

        private CelestialShipStepBuffers? celestialStepBuffers;
        private SpaceGameComp CopyForCelestialStep()
        {
            CelestialCostCounters.ShipGraphPrepared();
            var buffers = celestialStepBuffers ??= new();
            var prepared = buffers.Prepared ??= (SpaceGameComp)MemberwiseClone();
            var containers = buffers.Containers;
            var old_groups = prepared.groups;
            var old_entities = prepared.entities;
            var old_trackedManeuvers = prepared.trackedManeuvers;
            var old_alignStateByGroup = prepared.alignStateByGroup;
            var old_groupLastCoarsePos = prepared.groupLastCoarsePos;
            var old_planetGrids = prepared.planetGrids;
            var old_mapComponents = prepared.mapComponents;
            var old_movableGroupsScratch = prepared.movableGroupsScratch;
            var old_groupsToCheckScratch = prepared.groupsToCheckScratch;
            var old_steeringSliceScratch = prepared.steeringSliceScratch;
            var old_projTechAcc = prepared.projTechAcc;
            var old_projRadarAcc = prepared.projRadarAcc;
            var old_projJamAcc = prepared.projJamAcc;
            var old_combatRebuildScratch = prepared.combatRebuildScratch;
            var old_combatInterpTickScratch = prepared.combatInterpTickScratch;
            var old_hoverSliceScratch = prepared.hoverSliceScratch;
            var old_guidanceRecoverNextTick = prepared.guidanceRecoverNextTick;
            var old_ladderPrefetch = prepared.ladderPrefetch;
            var old_invalidGroupBirths = prepared.invalidGroupBirths;
            var old_hoverScratch = prepared.hoverScratch;
            CopyCelestialInputFieldsTo(prepared);
            prepared.physicsPreparation = new ShipPreparation(this);
            prepared.groups = containers.Map("groups", groups, old_groups, (value, old) => value.CopyForCelestialStep(prepared, old));
            prepared.entities = containers.Map("entities", entities, old_entities, (value, old) => value.CopyForCelestialStep(prepared, old));
            prepared.trackedManeuvers = containers.Map("trackedManeuvers", trackedManeuvers, old_trackedManeuvers);
            prepared.alignStateByGroup = containers.Map("alignStateByGroup", alignStateByGroup, old_alignStateByGroup);
            prepared.groupLastCoarsePos = containers.Map("groupLastCoarsePos", groupLastCoarsePos, old_groupLastCoarsePos);
            prepared.projTechAcc = containers.Map("projTechAcc", projTechAcc, old_projTechAcc);
            prepared.projRadarAcc = containers.Map("projRadarAcc", projRadarAcc, old_projRadarAcc);
            prepared.projJamAcc = containers.Map("projJamAcc", projJamAcc, old_projJamAcc);
            prepared.guidanceRecoverNextTick = containers.Map("guidanceRecoverNextTick", guidanceRecoverNextTick, old_guidanceRecoverNextTick);
            prepared.ladderPrefetch = containers.Map("ladderPrefetch", ladderPrefetch, old_ladderPrefetch);
            prepared.movableGroupsScratch = containers.List("movableGroupsScratch", movableGroupsScratch, old_movableGroupsScratch);
            prepared.groupsToCheckScratch = containers.List("groupsToCheckScratch", groupsToCheckScratch, old_groupsToCheckScratch);
            prepared.steeringSliceScratch = containers.List("steeringSliceScratch", steeringSliceScratch, old_steeringSliceScratch);
            prepared.combatRebuildScratch = containers.List("combatRebuildScratch", combatRebuildScratch, old_combatRebuildScratch);
            prepared.combatInterpTickScratch = containers.List("combatInterpTickScratch", combatInterpTickScratch, old_combatInterpTickScratch);
            prepared.hoverSliceScratch = containers.List("hoverSliceScratch", hoverSliceScratch, old_hoverSliceScratch);
            prepared.invalidGroupBirths = containers.List("invalidGroupBirths", invalidGroupBirths, old_invalidGroupBirths);
            prepared.hoverScratch = ReferenceEquals(old_hoverScratch, hoverScratch) ? new GravityScratch() : old_hoverScratch;
            prepared.planetGrids = containers.Map("grids", planetGrids, old_planetGrids, (grid, old) => grid.CopyForCelestialStep(entity =>
                entity is SpaceEntityGroup group ? prepared.groups[group.GroupId] : entity, old));
            buffers.SourceMaps.Clear();
            foreach (var pair in mapComponents) buffers.SourceMaps[pair.Key] = pair.Value;
            foreach (var entity in entities.Values)
                foreach (var map in entity.GetMapComps()) buffers.SourceMaps[map.map.uniqueID] = map;
            prepared.mapComponents = containers.Map("maps", buffers.SourceMaps, ReferenceEquals(old_mapComponents, mapComponents) ? null : old_mapComponents, (value, old) =>
                value is SpaceMapComp map ? map.CopyForCelestialStep(prepared, old as SpaceMapComp) : value);
            return prepared;
        }

        private sealed class ShipParticipant(SpaceGameComp owner, TimeScaleManager clock, CelestialBodyManager manager) : ICelestialStepParticipant
        {
            public string Id => "PFL.Ships";
            public CelestialStepParticipantPhase Phase => CelestialStepParticipantPhase.State;
            public CelestialResult<ICelestialPreparedStepPart> Prepare(CelestialPreparedStepContext context)
            {
                if (context.Request.StartSeconds == context.Request.EndSeconds)
                    return CelestialResult.Ok<ICelestialPreparedStepPart>(new EmptyShipPart());
                if (owner.pendingCelestialCompletion != null)
                    return CelestialResult.Reject<ICelestialPreparedStepPart>(new CelestialError("ShipCompletionPending"));
                var interval = clock.CurrentPhysicsStep;
                if (!interval.HasValue || interval.Value.StartTick != context.Request.StartSeconds || interval.Value.EndTick != context.Request.EndSeconds)
                    return CelestialResult.Reject<ICelestialPreparedStepPart>(new CelestialError("ClockIntervalMismatch"));
                var prepared = owner.CopyForCelestialStep();
                using (!manager.TryGetCelestialRuntime(out _) ? null : manager.UseCelestialInterval(context.Physics))
                    prepared.UpdateAllMovements(clock);
                prepared.LastPhysicsStepTick = context.Request.EndSeconds;
                var groupAssignments = owner.groups.Select(pair => (Target: pair.Value, Prepared: prepared.groups[pair.Key])).ToArray();
                var entityAssignments = owner.entities.Select(pair => pair.Value.PrepareCelestialPublication(prepared.entities[pair.Key])).ToArray();
                var maps = prepared.mapComponents.Where(pair => pair.Value is SpaceMapComp).Select(pair =>
                    (Target: owner.GetMapComponent<SpaceMapComp>(pair.Key) ?? owner.entities.Values.Select(e => e.GetMapComp(pair.Key)).First(map => map != null)!,
                     Prepared: (SpaceMapComp)pair.Value)).ToArray();
                var mapAssignments = maps.Select(pair => pair.Target.PrepareCelestialPublication(pair.Prepared)).ToArray();
                var buffers = owner.celestialStepBuffers!;
                var publicationTarget = buffers.Containers.Choose("publicationTarget", owner.planetGrids, buffers.PublishedGrids, () => new());
                var grids = buffers.Containers.Map("publishedGrids", prepared.planetGrids, publicationTarget,
                    (grid, old) => grid.CopyForCelestialStep(entity => entity is SpaceEntityGroup group ? owner.groups[group.GroupId] : entity, old));
                buffers.PublishedGrids = grids;
                return CelestialResult.Ok<ICelestialPreparedStepPart>(new ShipPart(owner, prepared, groupAssignments, entityAssignments, maps, mapAssignments, grids));
            }
        }
        private sealed class EmptyShipPart : ICelestialPreparedStepPart
        {
            public void Commit() { }
            public void Abort() { }
        }
        private sealed class ShipPart(SpaceGameComp owner, SpaceGameComp prepared,
            (SpaceEntityGroup Target, SpaceEntityGroup Prepared)[] groups, Action[] entities,
            (SpaceMapComp Target, SpaceMapComp Prepared)[] maps, Action[] mapAssignments,
            Dictionary<int, SpatialGrid3D<ISpatialEntity>> grids) : ICelestialPreparedStepPart
        {
            private bool published;
            public void Commit()
            {
                if (published) return;
                foreach (var assignment in entities) assignment();
                foreach (var assignment in mapAssignments) assignment();
                foreach (var pair in groups) pair.Target.PublishCelestialStep(pair.Prepared);
                owner.trackedManeuvers = prepared.trackedManeuvers;
                owner.alignStateByGroup = prepared.alignStateByGroup;
                owner.nextAlignLogTick = prepared.nextAlignLogTick;
                owner.nextSteerLogTick = prepared.nextSteerLogTick;
                owner.LastPhysicsStepTick = prepared.LastPhysicsStepTick;
                owner.planetGrids = grids;
                owner.groupLastCoarsePos = prepared.groupLastCoarsePos;
                owner.guidanceRecoverNextTick = prepared.guidanceRecoverNextTick;
                owner.ladderPrefetch = prepared.ladderPrefetch;
                hoverActiveCount = prepared.physicsPreparation!.HoverCount;
                prepared.physicsPreparation.Steering.Publish();
                owner.pendingCelestialCompletion = this;
                published = true;
            }
            // Prepared ship/map buffers have no published side effects. Discarding this part aborts the step;
            // the next Prepare copies all live inputs again. The abort/retry regression also checks single publication.
            public void Abort() { }
            internal void Complete()
            {
                foreach (var pair in maps) pair.Target.PublishCelestialVisuals(pair.Prepared);
                prepared.physicsPreparation!.Steering.Complete();
                while (prepared.physicsPreparation.Diagnostics.TryDequeue(out var entry)) WritePhysicsLog(entry.Kind, entry.Text);
                foreach (var message in prepared.physicsPreparation.Messages) message();
            }
        }

        private ShipPart? pendingCelestialCompletion;
        internal void CompleteCelestialShipStep(double physicsTick, CelestialBodyManager manager)
        {
            var pending = pendingCelestialCompletion;
            pendingCelestialCompletion = null;
            pending?.Complete();
            CompleteMovementStep(physicsTick, manager);
        }
    }
}
