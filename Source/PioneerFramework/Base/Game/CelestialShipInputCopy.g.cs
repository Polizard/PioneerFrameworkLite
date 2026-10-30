// Explicit input-copy inventory, manually maintained from the fixed-SDK metadata snapshot.
// Field additions must satisfy CelestialShipFieldCoverageTests (actual IL reads/writes), not only the schema list.
// Readonly host/component references retain their first MemberwiseClone binding; private buffers retain their own identity.
// Per-field lifetime policies and publication cache-reset reasons are documented in the compiled-IL tests.
// No reflection executes in production.
namespace PioneerFramework
{
    public partial class SpaceGameComp
    {
        internal const string CelestialInputFieldSchema = "sharedCelestialRuntime|<CelestialPhysicsErrors>k__BackingField|physicsPreparation|pendingCelestialCompletion|entities|groups|nextGroupId|nextEntityId|planetGrids|groupLastCoarsePos|mapComponents|lastUpdateTick|<LastPhysicsStepTick>k__BackingField|movableGroupsScratch|physicsResultsScratch|groupsToCheckScratch|steeringSliceScratch|timeScaleRef|sensorMgrRef|combatMgrRef|avmRef|celestialRef|alignStateByGroup|nextAlignLogTick|nextSteerLogTick|<ActiveRestoreOperation>k__BackingField|<ActiveArchiveOperation>k__BackingField|trackedManeuvers|GroupDestroyed|GroupLanded|nextEntityRegistryProbeTick|projTechAcc|projRadarAcc|projJamAcc|gravityScratchByIndex|combatRebuildScratch|combatInterpTickScratch|hoverScratch|hoverSliceScratch|burnAuditLinesThisStep|burnAuditTruncatedThisStep|guidanceRecoverNextTick|ladderPrefetch|lastLandingQuotedDeltaV|lastLandingConsumedKg|pendingGroupBirthCount|invalidGroupBirths";
        private void CopyCelestialInputFieldsTo(SpaceGameComp target)
        {
            target.sharedCelestialRuntime = sharedCelestialRuntime;
            target.CelestialPhysicsErrors = CelestialPhysicsErrors;
            target.physicsPreparation = physicsPreparation;
            target.pendingCelestialCompletion = pendingCelestialCompletion;
            target.entities = entities;
            target.groups = groups;
            target.nextGroupId = nextGroupId;
            target.nextEntityId = nextEntityId;
            target.planetGrids = planetGrids;
            target.groupLastCoarsePos = groupLastCoarsePos;
            target.mapComponents = mapComponents;
            target.lastUpdateTick = lastUpdateTick;
            target.LastPhysicsStepTick = LastPhysicsStepTick;
            target.movableGroupsScratch = movableGroupsScratch;
            target.physicsResultsScratch = physicsResultsScratch;
            target.groupsToCheckScratch = groupsToCheckScratch;
            target.steeringSliceScratch = steeringSliceScratch;
            target.alignStateByGroup = alignStateByGroup;
            target.nextAlignLogTick = nextAlignLogTick;
            target.nextSteerLogTick = nextSteerLogTick;
            target.ActiveRestoreOperation = ActiveRestoreOperation;
            target.ActiveArchiveOperation = ActiveArchiveOperation;
            target.trackedManeuvers = trackedManeuvers;
            target.GroupDestroyed = GroupDestroyed;
            target.GroupLanded = GroupLanded;
            target.nextEntityRegistryProbeTick = nextEntityRegistryProbeTick;
            target.projTechAcc = projTechAcc;
            target.projRadarAcc = projRadarAcc;
            target.projJamAcc = projJamAcc;
            target.gravityScratchByIndex = gravityScratchByIndex;
            target.combatRebuildScratch = combatRebuildScratch;
            target.combatInterpTickScratch = combatInterpTickScratch;
            target.hoverScratch = hoverScratch;
            target.hoverSliceScratch = hoverSliceScratch;
            target.burnAuditLinesThisStep = burnAuditLinesThisStep;
            target.burnAuditTruncatedThisStep = burnAuditTruncatedThisStep;
            target.guidanceRecoverNextTick = guidanceRecoverNextTick;
            target.ladderPrefetch = ladderPrefetch;
            target.lastLandingQuotedDeltaV = lastLandingQuotedDeltaV;
            target.lastLandingConsumedKg = lastLandingConsumedKg;
            target.pendingGroupBirthCount = pendingGroupBirthCount;
            target.invalidGroupBirths = invalidGroupBirths;
        }
    }
    public partial class SpaceEntityGroup
    {
#if DEBUG
        internal const string CelestialInputFieldSchema = "preparedPhysicsHost|LandingTaskId|GroupId|FactionId|IsActive|IsMovable|physicsBirthTick|physicsBirthSourceId|physicsBirthOffsetShip|physicsBirthMuzzleSystem|physicsRenderPreviousPosition|physicsRenderPreviousTick|physicsRenderCurrentTick|IsCollidable|HasGravityWell|IsCrippled|IsStealth|activePing|jamming|revealUntilPhysics|buildingJamBonus|cachedJamStrength|buildingTechBonus|radarCap|radarAim|radarManualDir|radarTrackGroupId|cachedSignal|cachedDetect|derivedDirty|CurrentOrbit|OrbitDirty|entityIds|entitySetVersion|cachedPropulsionAggregator|cachedPropulsionStamp|cachedMapPropulsionAggregator|cachedMapPropulsionStamp|thrustSliceScratch|IsCircularOrbit|circularRadius|circularAngularVelocity|circularStartAngle|circularStartTick|circularOrbitNormal|circularRadialRef|circularCentralBodyId|HasPendingCircularize|pendingCircularizeBodyId|<LastCellCoord>k__BackingField|<IsSpatialDirty>k__BackingField|CelestialBodyId|exemptPlanetCollision|IsSurfaceAnchor|anchorWorldObjectId|HasOrbitKeeping|orbitKeepingWhitelist|ignoredGravityBodyIds|fieldRestoreLatched|HasActiveBurn|BurnCommandVersion|burnDirectionSystem|remainingBurnDV|appliedBurnDV|burnStartMass|burnConsumedPropellant|burnUseRcs|burnIsCorrection|pendingAlignDir|aeroAlignToAirflow|intakeFlameoutLogged|envelopeRoutedOffLogged|hoverActive|hoverAnalytic|hoverLatRad|hoverLonRad0|hoverAltitudeMeters|debugInfinitePropellant|hoverResumeAfterTransfer|hoverResumeAnalytic|transferActive|transferFromPos|transferFromVel|transferToPos|transferToVel|transferDvTotal|transferStartTick|transferDuration|transferArrivalErrorMeters|transferMidLogged|transferArriveLogged|hoverShortfallLogged|hoverLastDvCmd|hoverLastDvApplied|hoverLastThrustN|hoverLogAccumSeconds|hoverLogErrMin|hoverLogErrMax|hoverLogErrSum|hoverLogCount|hoverLogVSum|hoverLogRadius0|pendingImpulse|burnEnabledNetworks|currentNetwork|plannedManeuvers|CombatCache|renderedPosition|hasRenderedPosition|cachedPosTick|cachedPos|cachedVelTick|cachedVel|debugPosCacheHits|debugPosCacheMisses|debugVelCacheHits|debugVelCacheMisses|ladderPlanEpoch|angularVelocity|torqueFreePhysics|burnDiagR|burnDiagV|burnDiagTick|burnDiagRemaining|burnDbgF|burnDbgMf|burnDbgMass|burnDiagRuns|burnArcDvAdapter|burnArcNodeUt|burnArcSeconds|burnReversedStopCount|burnNodeWindowAborts|burnDeficitIgnoredCount|burnTargetV|burnHasTargetV|burnTargetReached|burnLastDeficit|burnLastVLiveUt|burnLastDeficitDir|burnLastAppliedDv|burnActualStartTick|burnActualEndTick|burnRadial|burnTangential|burnNormal|burnComponentsInit|burnPreBurnOrbit|burnHasPreBurnOrbit|burnThrustDt|burnFlowDt|burnCommandAccel|burnThrottleCap|burnPhaseSwept|burnHeadStepsLogged|trimTargetA|trimTargetH|trimTargetHVec|trimHasTarget|trimEnabled|trimFired|trimLastDv|trimMainBurnDv|abFreezeDirection|abFullThrottle|abNoPreAlign|abForcePhaseShift|abOneShotImpulse|abNodeTick|burnLastCmdDv|appliedDvAudit|burnStepAuditCount|burnPhaseShift|maxAngularRate|HasRendezvousGuidance|rendezvousArmed|rendezvousIsApproach|rendezvousTargetId|rendezvousTargetOffset|rendezvousArrivalTick|rendezvousPrograde|rendezvousUseNetworks|rendezvousLastCheckTick|rendezvousArriveDist|rendezvousMatchDist|trimAllowedByRung|burnIgnitionAlignWaitTick|rendezvousArriveRelVel|rendezvousGoal|rendezvousTerminalFraction|ladderEnabled|ladderLastPlanTick|ladderMinReplanSeconds|ladderOwnsFarField|rendezvousLastBurnTick|terminalLawEnabled|terminalMode|terminalNextTick|cachedInertia|inertiaStamp|inertiaScratch";
#else
        internal const string CelestialInputFieldSchema = "preparedPhysicsHost|LandingTaskId|GroupId|FactionId|IsActive|IsMovable|physicsBirthTick|physicsBirthSourceId|physicsBirthOffsetShip|physicsBirthMuzzleSystem|physicsRenderPreviousPosition|physicsRenderPreviousTick|physicsRenderCurrentTick|IsCollidable|HasGravityWell|IsCrippled|IsStealth|activePing|jamming|revealUntilPhysics|buildingJamBonus|cachedJamStrength|buildingTechBonus|radarCap|radarAim|radarManualDir|radarTrackGroupId|cachedSignal|cachedDetect|derivedDirty|CurrentOrbit|OrbitDirty|entityIds|entitySetVersion|cachedPropulsionAggregator|cachedPropulsionStamp|cachedMapPropulsionAggregator|cachedMapPropulsionStamp|thrustSliceScratch|IsCircularOrbit|circularRadius|circularAngularVelocity|circularStartAngle|circularStartTick|circularOrbitNormal|circularRadialRef|circularCentralBodyId|HasPendingCircularize|pendingCircularizeBodyId|<LastCellCoord>k__BackingField|<IsSpatialDirty>k__BackingField|CelestialBodyId|exemptPlanetCollision|IsSurfaceAnchor|anchorWorldObjectId|HasOrbitKeeping|orbitKeepingWhitelist|ignoredGravityBodyIds|fieldRestoreLatched|HasActiveBurn|BurnCommandVersion|burnDirectionSystem|remainingBurnDV|appliedBurnDV|burnStartMass|burnConsumedPropellant|burnUseRcs|burnIsCorrection|pendingAlignDir|aeroAlignToAirflow|intakeFlameoutLogged|envelopeRoutedOffLogged|hoverActive|hoverAnalytic|hoverLatRad|hoverLonRad0|hoverAltitudeMeters|debugInfinitePropellant|hoverResumeAfterTransfer|hoverResumeAnalytic|transferActive|transferFromPos|transferFromVel|transferToPos|transferToVel|transferDvTotal|transferStartTick|transferDuration|transferArrivalErrorMeters|transferMidLogged|transferArriveLogged|hoverShortfallLogged|hoverLastDvCmd|hoverLastDvApplied|hoverLastThrustN|hoverLogAccumSeconds|hoverLogErrMin|hoverLogErrMax|hoverLogErrSum|hoverLogCount|hoverLogVSum|hoverLogRadius0|pendingImpulse|burnEnabledNetworks|currentNetwork|plannedManeuvers|CombatCache|renderedPosition|hasRenderedPosition|cachedPosTick|cachedPos|cachedVelTick|cachedVel|ladderPlanEpoch|angularVelocity|torqueFreePhysics|burnDiagR|burnDiagV|burnDiagTick|burnDiagRemaining|burnDbgF|burnDbgMf|burnDbgMass|burnDiagRuns|burnArcDvAdapter|burnArcNodeUt|burnArcSeconds|burnReversedStopCount|burnNodeWindowAborts|burnDeficitIgnoredCount|burnTargetV|burnHasTargetV|burnTargetReached|burnLastDeficit|burnLastVLiveUt|burnLastDeficitDir|burnLastAppliedDv|burnActualStartTick|burnActualEndTick|burnRadial|burnTangential|burnNormal|burnComponentsInit|burnPreBurnOrbit|burnHasPreBurnOrbit|burnThrustDt|burnFlowDt|burnCommandAccel|burnThrottleCap|burnPhaseSwept|burnHeadStepsLogged|trimTargetA|trimTargetH|trimTargetHVec|trimHasTarget|trimEnabled|trimFired|trimLastDv|trimMainBurnDv|abFreezeDirection|abFullThrottle|abNoPreAlign|abForcePhaseShift|abOneShotImpulse|abNodeTick|burnLastCmdDv|appliedDvAudit|burnStepAuditCount|burnPhaseShift|maxAngularRate|HasRendezvousGuidance|rendezvousArmed|rendezvousIsApproach|rendezvousTargetId|rendezvousTargetOffset|rendezvousArrivalTick|rendezvousPrograde|rendezvousUseNetworks|rendezvousLastCheckTick|rendezvousArriveDist|rendezvousMatchDist|trimAllowedByRung|burnIgnitionAlignWaitTick|rendezvousArriveRelVel|rendezvousGoal|rendezvousTerminalFraction|ladderEnabled|ladderLastPlanTick|ladderMinReplanSeconds|ladderOwnsFarField|rendezvousLastBurnTick|terminalLawEnabled|terminalMode|terminalNextTick|cachedInertia|inertiaStamp|inertiaScratch";
#endif
        private void CopyCelestialInputFieldsTo(SpaceEntityGroup target)
        {
            target.preparedPhysicsHost = preparedPhysicsHost;
            target.LandingTaskId = LandingTaskId;
            target.GroupId = GroupId;
            target.FactionId = FactionId;
            target.IsActive = IsActive;
            target.IsMovable = IsMovable;
            target.physicsBirthTick = physicsBirthTick;
            target.physicsBirthSourceId = physicsBirthSourceId;
            target.physicsBirthOffsetShip = physicsBirthOffsetShip;
            target.physicsBirthMuzzleSystem = physicsBirthMuzzleSystem;
            target.physicsRenderPreviousPosition = physicsRenderPreviousPosition;
            target.physicsRenderPreviousTick = physicsRenderPreviousTick;
            target.physicsRenderCurrentTick = physicsRenderCurrentTick;
            target.IsCollidable = IsCollidable;
            target.HasGravityWell = HasGravityWell;
            target.IsCrippled = IsCrippled;
            target.IsStealth = IsStealth;
            target.activePing = activePing;
            target.jamming = jamming;
            target.revealUntilPhysics = revealUntilPhysics;
            target.buildingJamBonus = buildingJamBonus;
            target.cachedJamStrength = cachedJamStrength;
            target.buildingTechBonus = buildingTechBonus;
            target.radarCap = radarCap;
            target.radarAim = radarAim;
            target.radarManualDir = radarManualDir;
            target.radarTrackGroupId = radarTrackGroupId;
            target.cachedSignal = cachedSignal;
            target.cachedDetect = cachedDetect;
            target.derivedDirty = derivedDirty;
            target.CurrentOrbit = CurrentOrbit;
            target.OrbitDirty = OrbitDirty;
            target.entityIds = entityIds;
            target.entitySetVersion = entitySetVersion;
            target.cachedPropulsionAggregator = cachedPropulsionAggregator;
            target.cachedPropulsionStamp = cachedPropulsionStamp;
            target.cachedMapPropulsionAggregator = cachedMapPropulsionAggregator;
            target.cachedMapPropulsionStamp = cachedMapPropulsionStamp;
            target.thrustSliceScratch = thrustSliceScratch;
            target.IsCircularOrbit = IsCircularOrbit;
            target.circularRadius = circularRadius;
            target.circularAngularVelocity = circularAngularVelocity;
            target.circularStartAngle = circularStartAngle;
            target.circularStartTick = circularStartTick;
            target.circularOrbitNormal = circularOrbitNormal;
            target.circularRadialRef = circularRadialRef;
            target.circularCentralBodyId = circularCentralBodyId;
            target.HasPendingCircularize = HasPendingCircularize;
            target.pendingCircularizeBodyId = pendingCircularizeBodyId;
            target.LastCellCoord = LastCellCoord;
            target.IsSpatialDirty = IsSpatialDirty;
            target.CelestialBodyId = CelestialBodyId;
            target.exemptPlanetCollision = exemptPlanetCollision;
            target.IsSurfaceAnchor = IsSurfaceAnchor;
            target.anchorWorldObjectId = anchorWorldObjectId;
            target.HasOrbitKeeping = HasOrbitKeeping;
            target.orbitKeepingWhitelist = orbitKeepingWhitelist;
            target.ignoredGravityBodyIds = ignoredGravityBodyIds;
            target.fieldRestoreLatched = fieldRestoreLatched;
            target.HasActiveBurn = HasActiveBurn;
            target.BurnCommandVersion = BurnCommandVersion;
            target.burnDirectionSystem = burnDirectionSystem;
            target.remainingBurnDV = remainingBurnDV;
            target.appliedBurnDV = appliedBurnDV;
            target.burnStartMass = burnStartMass;
            target.burnConsumedPropellant = burnConsumedPropellant;
            target.burnUseRcs = burnUseRcs;
            target.burnIsCorrection = burnIsCorrection;
            target.pendingAlignDir = pendingAlignDir;
            target.aeroAlignToAirflow = aeroAlignToAirflow;
            target.intakeFlameoutLogged = intakeFlameoutLogged;
            target.envelopeRoutedOffLogged = envelopeRoutedOffLogged;
            target.hoverActive = hoverActive;
            target.hoverAnalytic = hoverAnalytic;
            target.hoverLatRad = hoverLatRad;
            target.hoverLonRad0 = hoverLonRad0;
            target.hoverAltitudeMeters = hoverAltitudeMeters;
            target.debugInfinitePropellant = debugInfinitePropellant;
            target.hoverResumeAfterTransfer = hoverResumeAfterTransfer;
            target.hoverResumeAnalytic = hoverResumeAnalytic;
            target.transferActive = transferActive;
            target.transferFromPos = transferFromPos;
            target.transferFromVel = transferFromVel;
            target.transferToPos = transferToPos;
            target.transferToVel = transferToVel;
            target.transferDvTotal = transferDvTotal;
            target.transferStartTick = transferStartTick;
            target.transferDuration = transferDuration;
            target.transferArrivalErrorMeters = transferArrivalErrorMeters;
            target.transferMidLogged = transferMidLogged;
            target.transferArriveLogged = transferArriveLogged;
            target.hoverShortfallLogged = hoverShortfallLogged;
            target.hoverLastDvCmd = hoverLastDvCmd;
            target.hoverLastDvApplied = hoverLastDvApplied;
            target.hoverLastThrustN = hoverLastThrustN;
            target.hoverLogAccumSeconds = hoverLogAccumSeconds;
            target.hoverLogErrMin = hoverLogErrMin;
            target.hoverLogErrMax = hoverLogErrMax;
            target.hoverLogErrSum = hoverLogErrSum;
            target.hoverLogCount = hoverLogCount;
            target.hoverLogVSum = hoverLogVSum;
            target.hoverLogRadius0 = hoverLogRadius0;
            target.pendingImpulse = pendingImpulse;
            target.burnEnabledNetworks = burnEnabledNetworks;
            target.currentNetwork = currentNetwork;
            target.plannedManeuvers = plannedManeuvers;
            target.CombatCache = CombatCache;
            target.renderedPosition = renderedPosition;
            target.hasRenderedPosition = hasRenderedPosition;
            target.cachedPosTick = cachedPosTick;
            target.cachedPos = cachedPos;
            target.cachedVelTick = cachedVelTick;
            target.cachedVel = cachedVel;
#if DEBUG
            target.debugPosCacheHits = debugPosCacheHits;
            target.debugPosCacheMisses = debugPosCacheMisses;
            target.debugVelCacheHits = debugVelCacheHits;
            target.debugVelCacheMisses = debugVelCacheMisses;
#endif
            target.ladderPlanEpoch = ladderPlanEpoch;
            target.angularVelocity = angularVelocity;
            target.torqueFreePhysics = torqueFreePhysics;
            target.burnDiagR = burnDiagR;
            target.burnDiagV = burnDiagV;
            target.burnDiagTick = burnDiagTick;
            target.burnDiagRemaining = burnDiagRemaining;
            target.burnDbgF = burnDbgF;
            target.burnDbgMf = burnDbgMf;
            target.burnDbgMass = burnDbgMass;
            target.burnDiagRuns = burnDiagRuns;
            target.burnArcDvAdapter = burnArcDvAdapter;
            target.burnArcNodeUt = burnArcNodeUt;
            target.burnArcSeconds = burnArcSeconds;
            target.burnReversedStopCount = burnReversedStopCount;
            target.burnNodeWindowAborts = burnNodeWindowAborts;
            target.burnDeficitIgnoredCount = burnDeficitIgnoredCount;
            target.burnTargetV = burnTargetV;
            target.burnHasTargetV = burnHasTargetV;
            target.burnTargetReached = burnTargetReached;
            target.burnLastDeficit = burnLastDeficit;
            target.burnLastVLiveUt = burnLastVLiveUt;
            target.burnLastDeficitDir = burnLastDeficitDir;
            target.burnLastAppliedDv = burnLastAppliedDv;
            target.burnActualStartTick = burnActualStartTick;
            target.burnActualEndTick = burnActualEndTick;
            target.burnRadial = burnRadial;
            target.burnTangential = burnTangential;
            target.burnNormal = burnNormal;
            target.burnComponentsInit = burnComponentsInit;
            target.burnPreBurnOrbit = burnPreBurnOrbit;
            target.burnHasPreBurnOrbit = burnHasPreBurnOrbit;
            target.burnThrustDt = burnThrustDt;
            target.burnFlowDt = burnFlowDt;
            target.burnCommandAccel = burnCommandAccel;
            target.burnThrottleCap = burnThrottleCap;
            target.burnPhaseSwept = burnPhaseSwept;
            target.burnHeadStepsLogged = burnHeadStepsLogged;
            target.trimTargetA = trimTargetA;
            target.trimTargetH = trimTargetH;
            target.trimTargetHVec = trimTargetHVec;
            target.trimHasTarget = trimHasTarget;
            target.trimEnabled = trimEnabled;
            target.trimFired = trimFired;
            target.trimLastDv = trimLastDv;
            target.trimMainBurnDv = trimMainBurnDv;
            target.abFreezeDirection = abFreezeDirection;
            target.abFullThrottle = abFullThrottle;
            target.abNoPreAlign = abNoPreAlign;
            target.abForcePhaseShift = abForcePhaseShift;
            target.abOneShotImpulse = abOneShotImpulse;
            target.abNodeTick = abNodeTick;
            target.burnLastCmdDv = burnLastCmdDv;
            target.appliedDvAudit = appliedDvAudit;
            target.burnStepAuditCount = burnStepAuditCount;
            target.burnPhaseShift = burnPhaseShift;
            target.maxAngularRate = maxAngularRate;
            target.HasRendezvousGuidance = HasRendezvousGuidance;
            target.rendezvousArmed = rendezvousArmed;
            target.rendezvousIsApproach = rendezvousIsApproach;
            target.rendezvousTargetId = rendezvousTargetId;
            target.rendezvousTargetOffset = rendezvousTargetOffset;
            target.rendezvousArrivalTick = rendezvousArrivalTick;
            target.rendezvousPrograde = rendezvousPrograde;
            target.rendezvousUseNetworks = rendezvousUseNetworks;
            target.rendezvousLastCheckTick = rendezvousLastCheckTick;
            target.rendezvousArriveDist = rendezvousArriveDist;
            target.rendezvousMatchDist = rendezvousMatchDist;
            target.trimAllowedByRung = trimAllowedByRung;
            target.burnIgnitionAlignWaitTick = burnIgnitionAlignWaitTick;
            target.rendezvousArriveRelVel = rendezvousArriveRelVel;
            target.rendezvousGoal = rendezvousGoal;
            target.rendezvousTerminalFraction = rendezvousTerminalFraction;
            target.ladderEnabled = ladderEnabled;
            target.ladderLastPlanTick = ladderLastPlanTick;
            target.ladderMinReplanSeconds = ladderMinReplanSeconds;
            target.ladderOwnsFarField = ladderOwnsFarField;
            target.rendezvousLastBurnTick = rendezvousLastBurnTick;
            target.terminalLawEnabled = terminalLawEnabled;
            target.terminalMode = terminalMode;
            target.terminalNextTick = terminalNextTick;
            target.cachedInertia = cachedInertia;
            target.inertiaStamp = inertiaStamp;
            target.inertiaScratch = inertiaScratch;
        }
    }
    public partial class SpaceEntity
    {
        internal const string CelestialInputFieldSchema = "preparedPhysicsHost|entityId|groupId|factionId|CanInteract|IsSelectable|HasMap|IsMapArchived|IsMovable|IsAffectedByGravity|IsCollidable|BoundsSize|cachedAabbTick|aabbDirty|cachedAabbMin|cachedAabbMax|cachedRotInvTick|cachedRotInv|positionMeters|velocityMeters|rotation|MapId|ArchivedMapId|ArchivedPartId|archivedMapIds|archivedPartIds|parts|nextPartId|shipStats|statsDirty|partSetVersion|tankMassVersion|mapLinkVersion|cachedPartLinkedMapIds|cachedMapIdsVersion|fuelPlanVersion|fuelPlans|propulsion|mainAxisShip|cachedPropulsionNetworks|propulsionDirty|totalPropulsionMass|totalPropulsionCapacity|<PropulsionVersion>k__BackingField";
        private void CopyCelestialInputFieldsTo(SpaceEntity target)
        {
            target.preparedPhysicsHost = preparedPhysicsHost;
            target.entityId = entityId;
            target.groupId = groupId;
            target.factionId = factionId;
            target.CanInteract = CanInteract;
            target.IsSelectable = IsSelectable;
            target.HasMap = HasMap;
            target.IsMapArchived = IsMapArchived;
            target.IsMovable = IsMovable;
            target.IsAffectedByGravity = IsAffectedByGravity;
            target.IsCollidable = IsCollidable;
            target.BoundsSize = BoundsSize;
            target.cachedAabbTick = cachedAabbTick;
            target.aabbDirty = aabbDirty;
            target.cachedAabbMin = cachedAabbMin;
            target.cachedAabbMax = cachedAabbMax;
            target.cachedRotInvTick = cachedRotInvTick;
            target.cachedRotInv = cachedRotInv;
            target.positionMeters = positionMeters;
            target.velocityMeters = velocityMeters;
            target.rotation = rotation;
            target.MapId = MapId;
            target.ArchivedMapId = ArchivedMapId;
            target.ArchivedPartId = ArchivedPartId;
            target.archivedMapIds = archivedMapIds;
            target.archivedPartIds = archivedPartIds;
            target.parts = parts;
            target.nextPartId = nextPartId;
            target.shipStats = shipStats;
            target.statsDirty = statsDirty;
            target.partSetVersion = partSetVersion;
            target.tankMassVersion = tankMassVersion;
            target.mapLinkVersion = mapLinkVersion;
            target.cachedPartLinkedMapIds = cachedPartLinkedMapIds;
            target.cachedMapIdsVersion = cachedMapIdsVersion;
            target.fuelPlanVersion = fuelPlanVersion;
            target.fuelPlans = fuelPlans;
            target.propulsion = propulsion;
            target.mainAxisShip = mainAxisShip;
            target.cachedPropulsionNetworks = cachedPropulsionNetworks;
            target.propulsionDirty = propulsionDirty;
            target.totalPropulsionMass = totalPropulsionMass;
            target.totalPropulsionCapacity = totalPropulsionCapacity;
            target.PropulsionVersion = PropulsionVersion;
        }
    }
    public partial class SpacePart
    {
        internal const string CelestialInputFieldSchema = "partId|defName|positionShip|boundsSizeShip|rotationShip|shape|stageId|parentId|hp|isDestroyed|destroyHandled|isIndestructible|noCollide|fuelGroupId|fuelPriority|linkedMapId|isSealed|components|cachedRoomTypeDef|cachedStats|statsDirty|statsVersion";
        private void CopyCelestialInputFieldsTo(SpacePart target)
        {
            target.partId = partId;
            target.defName = defName;
            target.positionShip = positionShip;
            target.boundsSizeShip = boundsSizeShip;
            target.rotationShip = rotationShip;
            target.shape = shape;
            target.stageId = stageId;
            target.parentId = parentId;
            target.hp = hp;
            target.isDestroyed = isDestroyed;
            target.destroyHandled = destroyHandled;
            target.isIndestructible = isIndestructible;
            target.noCollide = noCollide;
            target.fuelGroupId = fuelGroupId;
            target.fuelPriority = fuelPriority;
            target.linkedMapId = linkedMapId;
            target.isSealed = isSealed;
            target.components = components;
            target.cachedRoomTypeDef = cachedRoomTypeDef;
            target.cachedStats = cachedStats;
            target.statsDirty = statsDirty;
            target.statsVersion = statsVersion;
        }
    }
    public partial class PropulsionData
    {
        internal const string CelestialInputFieldSchema = "propellant|ve|massFlow|mass|capacity|densityExponent|machCurve|gasIntakes|isVectoredRcs";
        private void CopyCelestialInputFieldsTo(PropulsionData target)
        {
            target.propellant = propellant;
            target.ve = ve;
            target.massFlow = massFlow;
            target.mass = mass;
            target.capacity = capacity;
            target.densityExponent = densityExponent;
            target.machCurve = machCurve;
            target.gasIntakes = gasIntakes;
            target.isVectoredRcs = isVectoredRcs;
        }
    }
    public partial class SpaceMapComp
    {
        internal const string CelestialInputFieldSchema = "isPreparedPhysicsCopy|preparedThrustersBurning|stationName|ownerEntityId|spaceWeapons|ownerPartId|disabledSystems|defaultFuelGroupId|defaultFuelPriority|<NetworksVersion>k__BackingField|rocketSpec|hasVirtualPropulsion|virtualPropellantDefName|virtualVe|virtualMassFlow|virtualFuelMass|virtualFuelCapacity|virtualPropulsionRegistered|propellantMass|propellantCapacity|totalPropellantMass|totalPropellantCapacity|sensorArrays|jammers|activeRadars|networks|networkList|thrusters|intakes|totalThrustByAxis|rcsTotalThrust|mainThrustAxisIndex|armedGroupsMask|buildingMassKg|roofedCellCount|roofCounted|ownerEntity|reliefClasses|reliefSolid|reliefVersion|reliefDirty|reliefHasContent";
        private void CopyCelestialInputFieldsTo(SpaceMapComp target)
        {
            target.isPreparedPhysicsCopy = isPreparedPhysicsCopy;
            target.preparedThrustersBurning = preparedThrustersBurning;
            target.stationName = stationName;
            target.ownerEntityId = ownerEntityId;
            target.ownerPartId = ownerPartId;
            target.disabledSystems = disabledSystems;
            target.defaultFuelGroupId = defaultFuelGroupId;
            target.defaultFuelPriority = defaultFuelPriority;
            target.NetworksVersion = NetworksVersion;
            target.rocketSpec = rocketSpec;
            target.hasVirtualPropulsion = hasVirtualPropulsion;
            target.virtualPropellantDefName = virtualPropellantDefName;
            target.virtualVe = virtualVe;
            target.virtualMassFlow = virtualMassFlow;
            target.virtualFuelMass = virtualFuelMass;
            target.virtualFuelCapacity = virtualFuelCapacity;
            target.virtualPropulsionRegistered = virtualPropulsionRegistered;
            target.propellantMass = propellantMass;
            target.propellantCapacity = propellantCapacity;
            target.totalPropellantMass = totalPropellantMass;
            target.totalPropellantCapacity = totalPropellantCapacity;
            target.networkList = networkList;
            target.rcsTotalThrust = rcsTotalThrust;
            target.mainThrustAxisIndex = mainThrustAxisIndex;
            target.armedGroupsMask = armedGroupsMask;
            target.buildingMassKg = buildingMassKg;
            target.roofedCellCount = roofedCellCount;
            target.roofCounted = roofCounted;
            target.ownerEntity = ownerEntity;
            target.reliefClasses = reliefClasses;
            target.reliefSolid = reliefSolid;
            target.reliefVersion = reliefVersion;
            target.reliefDirty = reliefDirty;
            target.reliefHasContent = reliefHasContent;
        }
    }
}
