// Explicit input-copy inventory, manually maintained from the fixed-SDK metadata snapshot.
// Field additions must satisfy CelestialShipFieldCoverageTests (actual IL reads/writes), not only the schema list.
// Readonly host/component references retain their first MemberwiseClone binding; private buffers retain their own identity.
// Per-field lifetime policies and publication cache-reset reasons are documented in the compiled-IL tests.
// No reflection executes in production.
namespace PioneerFramework
{
    public partial class SpaceGameComp
    {
        internal const string CelestialInputFieldSchema = "<ActiveArchiveOperation>k__BackingField|<ActiveRestoreOperation>k__BackingField|<CelestialPhysicsErrors>k__BackingField|<LastPhysicsStepTick>k__BackingField|alignStateByGroup|autoStagePlans|avmRef|burnAuditLinesThisStep|burnAuditTruncatedThisStep|celestialRef|combatInterpTickScratch|combatMgrRef|combatRebuildScratch|entities|gravityScratchByIndex|GroupDestroyed|GroupLanded|groupLastCoarsePos|groups|groupsToCheckScratch|guidanceRecoverNextTick|hoverScratch|hoverSliceScratch|invalidGroupBirths|ladderPlanCalls|ladderPrefetch|ladderWarmBg|lastLandingConsumedKg|lastLandingQuotedDeltaV|lastUpdateTick|mapComponents|movableGroupsScratch|nextAlignLogTick|nextEntityId|nextEntityRegistryProbeTick|nextGroupId|nextPayloadDesignId|nextSteerLogTick|payloadDesigns|pendingCelestialCompletion|pendingGroupBirthCount|physicsPreparation|physicsResultsScratch|planetGrids|projJamAcc|projRadarAcc|projTechAcc|sensorMgrRef|sharedCelestialRuntime|steeringSliceScratch|timeScaleRef|trackedManeuvers";
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
        internal const string CelestialInputFieldSchema = "<burnEnabledNetworks>k__BackingField|<IsSpatialDirty>k__BackingField|<LastCellCoord>k__BackingField|abForcePhaseShift|abFreezeDirection|abFullThrottle|abNodeTick|abNoPreAlign|abOneShotImpulse|activePing|aeroAlignToAirflow|anchorWorldObjectId|angularVelocity|appliedBurnDV|appliedDvAudit|attitudeAxis|attitudePriority|attitudeRateScale|attitudeTargetSystem|autoStageEnabled|autoStageFuelFraction|autoStageMinSeconds|Brownout|BrownoutPropulsion|BrownoutSensor|BrownoutShield|BrownoutWeapon|buildingJamBonus|buildingTechBonus|burnActualEndTick|burnActualStartTick|burnArcDvAdapter|burnArcNodeUt|burnArcSeconds|burnCommandAccel|BurnCommandVersion|burnComponentsInit|burnConsumedPropellant|burnDbgF|burnDbgMass|burnDbgMf|burnDeficitIgnoredCount|burnDiagR|burnDiagRemaining|burnDiagRuns|burnDiagTick|burnDiagV|burnDirectionSystem|burnDrySteps|BurnEndedByPower|burnFlowDt|burnHasPreBurnOrbit|burnHasTargetV|burnHeadStepsLogged|burnIgnitionAlignWaitTick|BurnIgnitionBlockedByPower|BurnIgnitionBlockedNoControl|burnIsCorrection|burnLastAppliedDv|burnLastCmdDv|burnLastDeficit|burnLastDeficitDir|burnLastVLiveUt|burnNodeWindowAborts|burnNormal|burnPhaseShift|burnPhaseSwept|burnPreBurnOrbit|burnRadial|burnReversedStopCount|burnStartMass|burnStepAuditCount|burnTangential|burnTargetReached|burnTargetV|burnThrottleCap|burnThrustDt|burnUseRcs|burnZeroDvSteps|cachedDetect|cachedHeatDissipation|cachedHeatProduction|cachedInertia|cachedJamStrength|cachedMapPropulsionAggregator|cachedMapPropulsionStamp|cachedPos|cachedPosTick|cachedPowerChargeLimit|cachedPowerDischargeLimit|cachedPowerDrawPropulsion|cachedPowerDrawSensor|cachedPowerDrawShield|cachedPowerDrawWeapon|cachedPowerGen|cachedPowerStorage|cachedPropulsionAggregator|cachedPropulsionStamp|cachedSignal|cachedVel|cachedVelTick|CelestialBodyId|circularAngularVelocity|circularCentralBodyId|circularOrbitNormal|circularRadialRef|circularRadius|circularStartAngle|circularStartTick|CombatCache|currentNetwork|CurrentOrbit|debugInfinitePropellant|debugPosCacheHits|debugPosCacheMisses|debugVelCacheHits|debugVelCacheMisses|derivedDirty|enabledNetworkDefName|enabledNetworkRcsOnly|entityIds|entitySetVersion|envelopeRoutedOffLogged|exemptPlanetCollision|FactionId|fieldRestoreLatched|GroupId|HasActiveBurn|HasGravityWell|HasIntactControlCore|HasOrbitKeeping|HasPendingCircularize|HasPowerOrHeat|hasRenderedPosition|HasRendezvousGuidance|hoverActive|hoverAltitudeMeters|hoverAnalytic|hoverLastDvApplied|hoverLastDvCmd|hoverLastThrustN|hoverLatRad|hoverLogAccumSeconds|hoverLogCount|hoverLogErrMax|hoverLogErrMin|hoverLogErrSum|hoverLogRadius0|hoverLogVSum|hoverLonRad0|hoverResumeAfterTransfer|hoverResumeAnalytic|hoverShortfallLogged|ignoredGravityBodyIds|impulseProbeSteps|inertiaScratch|inertiaStamp|intakeFlameoutLogged|IsActive|IsCircularOrbit|IsCollidable|IsCrippled|IsMovable|IsStealth|IsSurfaceAnchor|jamming|ladderEnabled|ladderLastPlanTick|ladderMinReplanSeconds|ladderOwnsFarField|ladderPlanEpoch|landed|landedFixedX|landedFixedY|landedFixedZ|LandingTaskId|lastPowerInputs|maskSource|maskVersion|maxAngularRate|networkMask|networkSelectionVersion|OrbitDirty|orbitKeepingWhitelist|Overheat|pendingAlignDir|pendingCircularizeBodyId|pendingImpulse|physicsBirthMuzzleSystem|physicsBirthOffsetShip|physicsBirthSourceId|physicsBirthTick|physicsRenderCurrentTick|physicsRenderPreviousPosition|physicsRenderPreviousTick|plannedManeuvers|playerEnabledNetworks|PowerDemandW|PowerSupplyW|PowerUsedW|preparedPhysicsHost|PropulsionPowered|radarAim|radarCap|radarManualDir|radarTrackGroupId|remainingBurnDV|renderedPosition|rendezvousArmed|rendezvousArrivalTick|rendezvousArriveDist|rendezvousArriveRelVel|rendezvousGoal|rendezvousIsApproach|rendezvousLastBurnTick|rendezvousLastCheckTick|rendezvousMatchDist|rendezvousPrograde|rendezvousTargetId|rendezvousTargetOffset|rendezvousTerminalFraction|rendezvousUseNetworks|RequiresControlCore|revealUntilPhysics|StoredEnergyJ|terminalLawEnabled|terminalMode|terminalNextTick|thrustSliceScratch|torqueFreePhysics|transferActive|transferArrivalErrorMeters|transferArriveLogged|transferDuration|transferDvTotal|transferFromPos|transferFromVel|transferMidLogged|transferStartTick|transferToPos|transferToVel|trimAllowedByRung|trimEnabled|trimFired|trimHasTarget|trimLastDv|trimMainBurnDv|trimTargetA|trimTargetH|trimTargetHVec|WheelHasMomentum";
#else
        internal const string CelestialInputFieldSchema = "<burnEnabledNetworks>k__BackingField|<IsSpatialDirty>k__BackingField|<LastCellCoord>k__BackingField|abForcePhaseShift|abFreezeDirection|abFullThrottle|abNodeTick|abNoPreAlign|abOneShotImpulse|activePing|aeroAlignToAirflow|anchorWorldObjectId|angularVelocity|appliedBurnDV|appliedDvAudit|attitudeAxis|attitudePriority|attitudeRateScale|attitudeTargetSystem|autoStageEnabled|autoStageFuelFraction|autoStageMinSeconds|Brownout|BrownoutPropulsion|BrownoutSensor|BrownoutShield|BrownoutWeapon|buildingJamBonus|buildingTechBonus|burnActualEndTick|burnActualStartTick|burnArcDvAdapter|burnArcNodeUt|burnArcSeconds|burnCommandAccel|BurnCommandVersion|burnComponentsInit|burnConsumedPropellant|burnDbgF|burnDbgMass|burnDbgMf|burnDeficitIgnoredCount|burnDiagR|burnDiagRemaining|burnDiagRuns|burnDiagTick|burnDiagV|burnDirectionSystem|burnDrySteps|BurnEndedByPower|burnFlowDt|burnHasPreBurnOrbit|burnHasTargetV|burnHeadStepsLogged|burnIgnitionAlignWaitTick|BurnIgnitionBlockedByPower|BurnIgnitionBlockedNoControl|burnIsCorrection|burnLastAppliedDv|burnLastCmdDv|burnLastDeficit|burnLastDeficitDir|burnLastVLiveUt|burnNodeWindowAborts|burnNormal|burnPhaseShift|burnPhaseSwept|burnPreBurnOrbit|burnRadial|burnReversedStopCount|burnStartMass|burnStepAuditCount|burnTangential|burnTargetReached|burnTargetV|burnThrottleCap|burnThrustDt|burnUseRcs|burnZeroDvSteps|cachedDetect|cachedHeatDissipation|cachedHeatProduction|cachedInertia|cachedJamStrength|cachedMapPropulsionAggregator|cachedMapPropulsionStamp|cachedPos|cachedPosTick|cachedPowerChargeLimit|cachedPowerDischargeLimit|cachedPowerDrawPropulsion|cachedPowerDrawSensor|cachedPowerDrawShield|cachedPowerDrawWeapon|cachedPowerGen|cachedPowerStorage|cachedPropulsionAggregator|cachedPropulsionStamp|cachedSignal|cachedVel|cachedVelTick|CelestialBodyId|circularAngularVelocity|circularCentralBodyId|circularOrbitNormal|circularRadialRef|circularRadius|circularStartAngle|circularStartTick|CombatCache|currentNetwork|CurrentOrbit|debugInfinitePropellant|derivedDirty|enabledNetworkDefName|enabledNetworkRcsOnly|entityIds|entitySetVersion|envelopeRoutedOffLogged|exemptPlanetCollision|FactionId|fieldRestoreLatched|GroupId|HasActiveBurn|HasGravityWell|HasIntactControlCore|HasOrbitKeeping|HasPendingCircularize|HasPowerOrHeat|hasRenderedPosition|HasRendezvousGuidance|hoverActive|hoverAltitudeMeters|hoverAnalytic|hoverLastDvApplied|hoverLastDvCmd|hoverLastThrustN|hoverLatRad|hoverLogAccumSeconds|hoverLogCount|hoverLogErrMax|hoverLogErrMin|hoverLogErrSum|hoverLogRadius0|hoverLogVSum|hoverLonRad0|hoverResumeAfterTransfer|hoverResumeAnalytic|hoverShortfallLogged|ignoredGravityBodyIds|impulseProbeSteps|inertiaScratch|inertiaStamp|intakeFlameoutLogged|IsActive|IsCircularOrbit|IsCollidable|IsCrippled|IsMovable|IsStealth|IsSurfaceAnchor|jamming|ladderEnabled|ladderLastPlanTick|ladderMinReplanSeconds|ladderOwnsFarField|ladderPlanEpoch|landed|landedFixedX|landedFixedY|landedFixedZ|LandingTaskId|lastPowerInputs|maskSource|maskVersion|maxAngularRate|networkMask|networkSelectionVersion|OrbitDirty|orbitKeepingWhitelist|Overheat|pendingAlignDir|pendingCircularizeBodyId|pendingImpulse|physicsBirthMuzzleSystem|physicsBirthOffsetShip|physicsBirthSourceId|physicsBirthTick|physicsRenderCurrentTick|physicsRenderPreviousPosition|physicsRenderPreviousTick|plannedManeuvers|playerEnabledNetworks|PowerDemandW|PowerSupplyW|PowerUsedW|preparedPhysicsHost|PropulsionPowered|radarAim|radarCap|radarManualDir|radarTrackGroupId|remainingBurnDV|renderedPosition|rendezvousArmed|rendezvousArrivalTick|rendezvousArriveDist|rendezvousArriveRelVel|rendezvousGoal|rendezvousIsApproach|rendezvousLastBurnTick|rendezvousLastCheckTick|rendezvousMatchDist|rendezvousPrograde|rendezvousTargetId|rendezvousTargetOffset|rendezvousTerminalFraction|rendezvousUseNetworks|RequiresControlCore|revealUntilPhysics|StoredEnergyJ|terminalLawEnabled|terminalMode|terminalNextTick|thrustSliceScratch|torqueFreePhysics|transferActive|transferArrivalErrorMeters|transferArriveLogged|transferDuration|transferDvTotal|transferFromPos|transferFromVel|transferMidLogged|transferStartTick|transferToPos|transferToVel|trimAllowedByRung|trimEnabled|trimFired|trimHasTarget|trimLastDv|trimMainBurnDv|trimTargetA|trimTargetH|trimTargetHVec|WheelHasMomentum";
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
            target.attitudeAxis = attitudeAxis;
            target.attitudePriority = attitudePriority;
            target.attitudeRateScale = attitudeRateScale;
            target.attitudeTargetSystem = attitudeTargetSystem;
            target.autoStageEnabled = autoStageEnabled;
            target.autoStageFuelFraction = autoStageFuelFraction;
            target.autoStageMinSeconds = autoStageMinSeconds;
            target.Brownout = Brownout;
            target.BrownoutPropulsion = BrownoutPropulsion;
            target.BrownoutSensor = BrownoutSensor;
            target.BrownoutShield = BrownoutShield;
            target.BrownoutWeapon = BrownoutWeapon;
            target.burnDrySteps = burnDrySteps;
            target.BurnEndedByPower = BurnEndedByPower;
            target.BurnIgnitionBlockedByPower = BurnIgnitionBlockedByPower;
            target.BurnIgnitionBlockedNoControl = BurnIgnitionBlockedNoControl;
            target.burnZeroDvSteps = burnZeroDvSteps;
            target.cachedHeatDissipation = cachedHeatDissipation;
            target.cachedHeatProduction = cachedHeatProduction;
            target.cachedPowerChargeLimit = cachedPowerChargeLimit;
            target.cachedPowerDischargeLimit = cachedPowerDischargeLimit;
            target.cachedPowerDrawPropulsion = cachedPowerDrawPropulsion;
            target.cachedPowerDrawSensor = cachedPowerDrawSensor;
            target.cachedPowerDrawShield = cachedPowerDrawShield;
            target.cachedPowerDrawWeapon = cachedPowerDrawWeapon;
            target.cachedPowerGen = cachedPowerGen;
            target.cachedPowerStorage = cachedPowerStorage;
            target.enabledNetworkDefName = enabledNetworkDefName;
            target.enabledNetworkRcsOnly = enabledNetworkRcsOnly;
            target.HasIntactControlCore = HasIntactControlCore;
            target.HasPowerOrHeat = HasPowerOrHeat;
            target.impulseProbeSteps = impulseProbeSteps;
            target.landed = landed;
            target.landedFixedX = landedFixedX;
            target.landedFixedY = landedFixedY;
            target.landedFixedZ = landedFixedZ;
            target.lastPowerInputs = lastPowerInputs;
            target.Overheat = Overheat;
            target.playerEnabledNetworks = playerEnabledNetworks;
            target.PowerDemandW = PowerDemandW;
            target.PowerSupplyW = PowerSupplyW;
            target.PowerUsedW = PowerUsedW;
            target.PropulsionPowered = PropulsionPowered;
            target.RequiresControlCore = RequiresControlCore;
            target.StoredEnergyJ = StoredEnergyJ;
            target.WheelHasMomentum = WheelHasMomentum;
        }
    }
    public partial class SpaceEntity
    {
        internal const string CelestialInputFieldSchema = "<PropulsionVersion>k__BackingField|aabbDirty|ArchivedMapId|archivedMapIds|ArchivedPartId|archivedPartIds|BoundsSize|cachedAabbMax|cachedAabbMin|cachedAabbTick|cachedMapIdsVersion|cachedPartLinkedMapIds|cachedPropulsionNetworks|cachedRotInv|cachedRotInvTick|CanInteract|entityId|factionId|fuelPlans|fuelPlanVersion|groupId|HasIntactControlCore|HasMap|IsAffectedByGravity|IsCollidable|IsMapArchived|IsMovable|IsSelectable|mainAxisShip|MapId|mapLinkVersion|nextPartId|parts|partSetVersion|positionMeters|preparedPhysicsHost|propulsion|propulsionDirty|PropulsionFromParts|RequiresControlCore|rotation|shipStats|statsDirty|tankMassVersion|totalPropulsionCapacity|totalPropulsionMass|velocityMeters|wheelCapacity|wheelMomentum";
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
            target.RequiresControlCore = RequiresControlCore;
            target.HasIntactControlCore = HasIntactControlCore;
            target.PropulsionFromParts = PropulsionFromParts;
            target.wheelMomentum = wheelMomentum;
            target.wheelCapacity = wheelCapacity;
        }
    }
    public partial class SpacePart
    {
        internal const string CelestialInputFieldSchema = "armorMaterial|armorThicknessMm|boundsSizeShip|cachedRoomTypeDef|cachedStats|components|condition|defName|destroyHandled|fuelGroupId|fuelPriority|hp|isDestroyed|isIndestructible|isSealed|linkedMapId|noCollide|parentId|partId|positionShip|rotationShip|shape|stageId|statsDirty|statsVersion";
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
            target.condition = condition;
            target.armorThicknessMm = armorThicknessMm;
            target.armorMaterial = armorMaterial;
        }
    }
    public partial class PropulsionData
    {
        internal const string CelestialInputFieldSchema = "capacity|densityExponent|gasIntakes|isVectoredRcs|machCurve|mass|massFlow|propellant|ve";
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
        internal const string CelestialInputFieldSchema = "<NetworksVersion>k__BackingField|activeRadars|armedGroupsMask|buildingMassKg|defaultFuelGroupId|defaultFuelPriority|disabledSystems|hasVirtualPropulsion|intakes|isPreparedPhysicsCopy|jammers|mainThrustAxisIndex|networkList|networks|ownerEntity|ownerEntityId|ownerPartId|preparedThrustersBurning|propellantCapacity|propellantMass|rcsTotalThrust|reliefClasses|reliefDirty|reliefHasContent|reliefSolid|reliefVersion|rocketSpec|roofCounted|roofedCellCount|sensorArrays|spaceWeapons|stationName|thrusters|totalPropellantCapacity|totalPropellantMass|totalThrustByAxis|virtualFuelCapacity|virtualFuelMass|virtualMassFlow|virtualPropellantDefName|virtualPropulsionRegistered|virtualVe";
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
