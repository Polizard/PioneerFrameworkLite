using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using PioneerFramework.Landing;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class SurfaceTouchdownTests
{
    [Theory]
    [InlineData(true, "PFL_LandingUnsupportedDestination", "PFL_TouchdownCrashLanding")]
    [InlineData(false, "PFL_LandingUnsupportedDestination", "PFL_LandingUnsupportedDestination")]
    [InlineData(true, "PFL_LandingWrongLayer", "PFL_LandingWrongLayer")]
    [InlineData(true, "PFL_LandingSourceUnavailable", "PFL_LandingSourceUnavailable")]
    public void UnsupportedPhysicalTouchdownReportsAccidentWithoutReclassifyingOtherFailures(bool physical, string reason, string expected)
    {
        var task = new LandingTaskData { GroupId = 7, Status = LandingTransitStatus.AwaitingMapRequest,
            Touchdown = physical ? new SurfaceTouchdownData { GroundSpeed = 20 } : null };
        var original = LandingReport.Reject(reason, "original diagnostic");
        var report = SurfaceTouchdownService.DescribeDestinationFailure(task, original);
        Assert.False(report.Success);
        Assert.Equal(expected, report.ReasonKey);
        if (expected == "PFL_TouchdownCrashLanding") Assert.Equal(7, Assert.Single(report.Arguments));
        else Assert.Same(original, report);
        Assert.Equal(LandingTransitStatus.AwaitingMapRequest, task.Status);
        Assert.Equal(-1, task.DestinationMapId);
    }

    // Appended state value: old saves retain the existing 0..7 meanings.
    private const LandingTransitStatus WaitingForMap = (LandingTransitStatus)8;
    private static Faction collisionPlayer;
    private static string collisionNotice;
    private static Action queuedPreparation;
    private static Action<Exception> queuedFailure;
    private static bool RejectLandingPolicy(ref LandingReport __result)
    { __result = LandingReport.Reject("PFL_LandingUnsupportedDestination"); return false; }
    private static bool SupplyCollisionPlayer(ref Faction __result) { __result = collisionPlayer; return false; }
    private static bool SkipMessage() => false;
    private static bool CaptureNotice(string text) { collisionNotice = text; return false; }
    private static bool CapturePreparation(Action action, Action<Exception> exceptionHandler)
    { queuedPreparation = action; queuedFailure = exceptionHandler; return false; }

    [Theory]
    [InlineData(true, "PFL_TouchdownCrashLanding")]
    [InlineData(false, "PFL_LandingUnsupportedDestination")]
    public void DestinationPreparationReportsAccidentOnlyForPhysicalTouchdown(bool physical, string expected)
    {
        using var fixture = new CaptureFixture();
        var task = SurfaceTouchdownService.Capture(fixture.Space, fixture.Manager, fixture.Group, new SurfaceTouchdownData { GroundSpeed = 20 });
        if (!physical) task.Touchdown = null;
        task.Status = LandingTransitStatus.PreparingDestination;
        LandingReport failure = null;
        var bootstrap = new Harmony("PFL.Touchdown.DestinationFailureText");
        bootstrap.Patch(AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.QueueLongEvent),
            new[] { typeof(Action), typeof(string), typeof(bool), typeof(Action<Exception>), typeof(bool), typeof(bool), typeof(Action) }),
            prefix: new HarmonyMethod(typeof(SurfaceTouchdownTests), nameof(CapturePreparation)));
        bootstrap.Patch(AccessTools.Method(typeof(LandingDestinationService), nameof(LandingDestinationService.Check)),
            prefix: new HarmonyMethod(typeof(SurfaceTouchdownTests), nameof(RejectLandingPolicy)));
        try
        {
            LandingDestinationService.BeginResolve(task, _ => throw new InvalidOperationException("Unsupported map generated"), report => failure = report);
            queuedPreparation();
            Assert.NotNull(failure);
            Assert.Equal(expected, failure.ReasonKey);
            Assert.Same(fixture.Map, Assert.Single(Current.Game.Maps));
            Assert.Equal(-1, task.DestinationMapId);
        }
        finally { bootstrap.UnpatchAll(bootstrap.Id); queuedPreparation = null; queuedFailure = null; }
    }

    [Fact]
    public void QueuedDestinationRechecksSourceIdentityBeforeResolvingTile()
    {
        using var fixture = new CaptureFixture();
        var task = SurfaceTouchdownService.Capture(fixture.Space, fixture.Manager, fixture.Group, new SurfaceTouchdownData { GroundSpeed = 20 });
        task.Status = LandingTransitStatus.PreparingDestination;
        LandingReport failure = null;
        var bootstrap = new Harmony("PFL.Touchdown.DelayedSource");
        bootstrap.Patch(AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.QueueLongEvent),
            new[] { typeof(Action), typeof(string), typeof(bool), typeof(Action<Exception>), typeof(bool), typeof(bool), typeof(Action) }),
            prefix: new HarmonyMethod(typeof(SurfaceTouchdownTests), nameof(CapturePreparation)));
        try
        {
            LandingDestinationService.BeginResolve(task, _ => throw new InvalidOperationException("Changed source generated map"), r => failure = r);
            fixture.Entity.MapId = 43; fixture.Entity.NotifyMapLinksChanged();
            queuedPreparation();
            Assert.NotNull(failure);
            Assert.Equal("PFL_LandingSourceUnavailable", failure.ReasonKey);
            Assert.Equal(-1, task.DestinationMapId);
        }
        finally { bootstrap.UnpatchAll(bootstrap.Id); queuedPreparation = null; queuedFailure = null; }
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void QueuedDestinationCannotActAfterGameOrStageChanges(bool changeGame)
    {
        using var fixture = new CaptureFixture();
        var task = SurfaceTouchdownService.Capture(fixture.Space, fixture.Manager, fixture.Group, new SurfaceTouchdownData { GroundSpeed = 20 });
        task.Status = LandingTransitStatus.PreparingDestination;
        int callbacks = 0;
        var bootstrap = new Harmony("PFL.Touchdown.DelayedPreparation");
        bootstrap.Patch(AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.QueueLongEvent),
            new[] { typeof(Action), typeof(string), typeof(bool), typeof(Action<Exception>), typeof(bool), typeof(bool), typeof(Action) }),
            prefix: new HarmonyMethod(typeof(SurfaceTouchdownTests), nameof(CapturePreparation)));
        try
        {
            LandingDestinationService.BeginResolve(task, _ => callbacks++, _ => callbacks++);
            Assert.NotNull(queuedPreparation);
            if (changeGame) Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            else task.Status = LandingTransitStatus.Blocked;
            queuedPreparation();
            queuedFailure(new InvalidOperationException("Stale queued error"));
            Assert.Equal(0, callbacks);
            Assert.Equal(-1, task.DestinationMapId);
        }
        finally { bootstrap.UnpatchAll(bootstrap.Id); queuedPreparation = null; queuedFailure = null; }
    }

    [Fact]
    public void DuplicateTouchdownNotificationCannotChangeSavedTaskOrReportAgain()
    {
        using var fixture = new CaptureFixture();
        collisionPlayer = (Faction)RuntimeHelpers.GetUninitializedObject(typeof(Faction)); collisionPlayer.loadID = 9;
        fixture.Group.FactionId = 9;
        var snapshot = new SurfaceTouchdownData { GroundSpeed = 20, PhysicsTick = 123 };
        var task = SurfaceTouchdownService.Capture(fixture.Space, fixture.Manager, fixture.Group, snapshot);
        var bootstrap = new Harmony("PFL.Touchdown.DuplicateNotification");
        bootstrap.Patch(AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayer)),
            prefix: new HarmonyMethod(typeof(SurfaceTouchdownTests), nameof(SupplyCollisionPlayer)));
        bootstrap.Patch(AccessTools.Method(typeof(Messages), nameof(Messages.Message), new[] { typeof(string), typeof(MessageTypeDef), typeof(bool) }),
            prefix: new HarmonyMethod(typeof(SurfaceTouchdownTests), nameof(CaptureNotice)));
        collisionNotice = null;
        try
        {
            Assert.True(SurfaceTouchdownService.TryHandle(fixture.Space, fixture.Group, new DVector3(1, 2, 3), DVector3.zero, 999, 40, 0.5, 1.5));
            Assert.Same(snapshot, task.Touchdown);
            Assert.Equal(WaitingForMap, task.Status);
            Assert.Null(collisionNotice);
            Assert.Single(fixture.Manager.Tasks);
        }
        finally { bootstrap.UnpatchAll(bootstrap.Id); collisionPlayer = null; collisionNotice = null; }
    }

    [Fact]
    public void RealCollisionEntryRetainsMappedPlayerShipWhenNoDestinationLayerIsAvailable()
    {
        using var fixture = new CaptureFixture();
        collisionPlayer = (Faction)RuntimeHelpers.GetUninitializedObject(typeof(Faction)); collisionPlayer.loadID = 9;
        fixture.Group.FactionId = 9;
        var position = new DVector3(SpaceConstants.PlanetRadiusMeters, 0, 0);
        var velocity = SurfaceFrame.SurfaceVelocity(position, 2 * Math.PI / 60000) - new DVector3(20, 0, 0);
        fixture.Group.CurrentOrbit = new OrbitState { positionSystem = (SystemVec3)position, velocitySystem = (SystemVec3)velocity };
        var bootstrap = new Harmony("PFL.Touchdown.CollisionEntry");
        bootstrap.Patch(AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayer)),
            prefix: new HarmonyMethod(typeof(SurfaceTouchdownTests), nameof(SupplyCollisionPlayer)));
        bootstrap.Patch(AccessTools.Method(typeof(Messages), nameof(Messages.Message), new[] { typeof(string), typeof(MessageTypeDef), typeof(bool) }),
            prefix: new HarmonyMethod(typeof(SurfaceTouchdownTests), nameof(CaptureNotice)));
        bootstrap.Patch(AccessTools.Method(typeof(DefOfHelper), nameof(DefOfHelper.EnsureInitializedInCtor)),
            prefix: new HarmonyMethod(typeof(SurfaceTouchdownTests), nameof(SkipMessage)));
        try
        {
            AccessTools.Method(typeof(SpaceGameComp), "CheckPlanetCollision").Invoke(fixture.Space, new object[] { fixture.Group, 123d });
            Assert.Same(fixture.Group, fixture.Space.GetGroupById(7));
            Assert.Same(fixture.Entity, fixture.Space.GetEntityById(11));
            Assert.Same(fixture.Map, Assert.Single(Current.Game.Maps));
            var task = Assert.Single(fixture.Manager.Tasks);
            Assert.Equal(LandingTransitStatus.Blocked, task.Status);
            Assert.Equal(20, task.Touchdown.GroundSpeed, 6);
            Assert.Equal(123, task.Touchdown.PhysicsTick);
            Assert.Equal(-1, task.DestinationMapId);
            Assert.False(fixture.Group.IsActive);
            Assert.Equal(task.FailureReason, collisionNotice);
        }
        finally { bootstrap.UnpatchAll(bootstrap.Id); collisionPlayer = null; }
    }

    [Fact]
    public void CaptureProtectsMainMapEvenWhenAPartPointsToAnotherMap()
    {
        using var fixture = new CaptureFixture();
        fixture.Entity.SetParts(new List<SpacePart> { new SpacePart { partId = 1, linkedMapId = 43 } });
        var task = SurfaceTouchdownService.Capture(fixture.Space, fixture.Manager, fixture.Group, new SurfaceTouchdownData { GroundSpeed = 20 });
        Assert.Contains(task.SourceMaps, s => s.MapId == 42);
        Assert.Contains(task.SourceMaps, s => s.MapId == 43);
    }

    [Fact]
    public void NonFiniteSnapshotCannotBeRegistered()
    {
        using var fixture = new CaptureFixture();
        Assert.Throws<InvalidOperationException>(() => SurfaceTouchdownService.Capture(fixture.Space, fixture.Manager,
            fixture.Group, new SurfaceTouchdownData { GroundSpeed = 20, PhysicsTick = double.NaN }));
        Assert.Empty(fixture.Manager.Tasks);
    }

    [Fact]
    public void FailedWaitingTaskResumeCannotImplicitlyDispatchArrival()
    {
        Game previousGame = Current.Game;
        var previousArrival = LandingTransitService.Arrived;
        var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        var manager = new LandingTransitManager(game);
        var task = new LandingTaskData { TaskId = "failed-wait", Status = LandingTransitStatus.FailedRecoverable, ResumeStage = WaitingForMap };
        manager.Register(task); game.components = new List<GameComponent> { manager };
        LandingTransitService.Arrived = _ => throw new InvalidOperationException("Implicit arrival after failure");
        try
        {
            Current.Game = game;
            Assert.False(LandingTransitService.TryResume(task.TaskId, out _));
            Assert.Equal(WaitingForMap, task.Status);
        }
        finally { Current.Game = previousGame; LandingTransitService.Arrived = previousArrival; }
    }

    [Fact]
    public void OnlyMappedPlayerShipsUseTouchdownCaptureIncludingArchivedSources()
    {
        using var fixture = new CaptureFixture();
        fixture.Group.FactionId = 5;
        Assert.True(SurfaceTouchdownService.CanHandle(fixture.Space, fixture.Group, 5));
        Assert.False(SurfaceTouchdownService.CanHandle(fixture.Space, fixture.Group, 6));
        fixture.Entity.MapId = -1; fixture.Entity.HasMap = false;
        fixture.Entity.NotifyMapLinksChanged();
        Assert.False(SurfaceTouchdownService.CanHandle(fixture.Space, fixture.Group, 5));
        fixture.Entity.IsMapArchived = true;
        Assert.True(SurfaceTouchdownService.CanHandle(fixture.Space, fixture.Group, 5));
    }

    [Fact]
    public void ChangedGameCannotUseOldMapRequestCallback()
    {
        using var fixture = new CaptureFixture();
        var oldGame = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        Assert.False(SurfaceTouchdownService.TryRequestDestinationMap(oldGame, "missing", out var report));
        Assert.False(report.Success);
    }

    [Fact]
    public void CapturePreservesShipAndMapWithoutCreatingDestinationOrDebitingFuel()
    {
        using var fixture = new CaptureFixture();
        var snapshot = new SurfaceTouchdownData { PhysicsTick = 1234.5, GroundSpeed = 61.25,
            PositionBody = new DVector3(5000000, 0, 0), VelocityBody = new DVector3(-61.25, 0, 500), LatitudeRad = 0, LongitudeRad = 1.25 };
        var task = SurfaceTouchdownService.Capture(fixture.Space, fixture.Manager, fixture.Group, snapshot);
        Assert.Equal(WaitingForMap, task.Status);
        Assert.Same(snapshot, task.Touchdown);
        Assert.False(fixture.Group.IsActive);
        Assert.False(fixture.Group.IsMovable);
        Assert.False(fixture.Group.IsCollidable);
        Assert.Equal(task.TaskId, fixture.Group.LandingTaskId);
        Assert.Same(fixture.Entity, fixture.Space.GetEntityById(11));
        Assert.Same(fixture.Map, Assert.Single(Current.Game.Maps));
        Assert.Equal(42, Assert.Single(task.SourceMaps).MapId);
        Assert.Equal(-1, task.DestinationMapId);
        Assert.Equal(-1, task.DestinationParentId);
        Assert.Empty(task.FuelReceipt.Debits);
        Assert.Equal(0, task.FuelReceipt.ConsumedKg);
        Assert.Same(task, SurfaceTouchdownService.Capture(fixture.Space, fixture.Manager, fixture.Group, snapshot));
        Assert.Single(fixture.Manager.Tasks);
    }

    [Theory]
    [InlineData(100.001)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void UnsafeOrInvalidSpeedCannotEnterSafeCapture(double speed)
    {
        using var fixture = new CaptureFixture();
        Assert.Throws<InvalidOperationException>(() => SurfaceTouchdownService.Capture(fixture.Space, fixture.Manager,
            fixture.Group, new SurfaceTouchdownData { GroundSpeed = speed }));
        Assert.Empty(fixture.Manager.Tasks);
        Assert.True(fixture.Group.IsActive);
    }

    [Fact]
    public void ExplicitMapRequestClaimsStateBeforeDispatchAndRejectsDuplicate()
    {
        var task = new LandingTaskData { Status = WaitingForMap, Touchdown = new SurfaceTouchdownData() };
        int requests = 0;
        Assert.True(SurfaceTouchdownService.BeginMapRequest(task, () => LandingReport.Ok(), t =>
        {
            requests++;
            Assert.Equal(LandingTransitStatus.PreparingDestination, t.Status);
        }, out _));
        Assert.False(SurfaceTouchdownService.BeginMapRequest(task, () => throw new InvalidOperationException("Duplicate preflight"),
            _ => throw new InvalidOperationException("Duplicate map"), out _));
        Assert.Equal(1, requests);
    }

    [Fact]
    public void RejectedManualPreflightPreservesWaitingShipWithoutDispatch()
    {
        var task = new LandingTaskData { Status = WaitingForMap, Touchdown = new SurfaceTouchdownData() };
        Assert.False(SurfaceTouchdownService.BeginMapRequest(task, () => LandingReport.Reject("PFL_LandingSourceUnavailable"),
            _ => throw new InvalidOperationException("Map created despite failure"), out var report));
        Assert.False(report.Success);
        Assert.Equal(WaitingForMap, task.Status);
        Assert.Equal(-1, task.DestinationMapId);
    }

    [Fact]
    public void MarkerAndTicksIgnoreInvalidTileWithoutGeneratingAMap()
    {
        using var fixture = new CaptureFixture();
        var task = SurfaceTouchdownService.Capture(fixture.Space, fixture.Manager, fixture.Group,
            new SurfaceTouchdownData { GroundSpeed = 20 });
        Assert.Null(LandingTransitService.EnsureMarker(task));
        LandingTransitService.Reconcile(fixture.Manager);
        for (int i = 0; i < 600; i++) fixture.Manager.GameComponentTick();
        Assert.Equal(WaitingForMap, task.Status);
        Assert.Same(fixture.Map, Assert.Single(Current.Game.Maps));
    }

    private sealed class CaptureFixture : IDisposable
    {
        private readonly Game previous = Current.Game;
        internal readonly SpaceGameComp Space;
        internal readonly LandingTransitManager Manager;
        internal readonly SpaceEntityGroup Group;
        internal readonly SpaceEntity Entity;
        internal readonly Map Map;
        internal CaptureFixture()
        {
            var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            AccessTools.Field(typeof(Game), "tickManager").SetValue(game, RuntimeHelpers.GetUninitializedObject(typeof(TickManager)));
            Space = new SpaceGameComp(game);
            Manager = new LandingTransitManager(game);
            game.components = new List<GameComponent> { Space, Manager };
            var world = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
            world.worldObjects = new WorldObjectsHolder();
            AccessTools.Field(typeof(Game), "worldInt").SetValue(game, world);
            Map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map)); Map.uniqueID = 42;
            AccessTools.Field(typeof(Map), "components").SetValue(Map, new List<MapComponent>());
            AccessTools.Field(typeof(Game), "maps").SetValue(game, new List<Map> { Map });
            Entity = new SpaceEntity(11) { MapId = 42, HasMap = true, GroupId = 7 };
            Group = new SpaceEntityGroup(7) { IsActive = true, IsMovable = true, IsCollidable = true, CelestialBodyId = 1 };
            Group.GetEntityIds().Add(11);
            ((Dictionary<int, SpaceEntity>)AccessTools.Field(typeof(SpaceGameComp), "entities").GetValue(Space)).Add(11, Entity);
            ((Dictionary<int, SpaceEntityGroup>)AccessTools.Field(typeof(SpaceGameComp), "groups").GetValue(Space)).Add(7, Group);
            Current.Game = game;
        }
        public void Dispose() => Current.Game = previous;
    }

    [Fact]
    public void WaitingForMapCanPrepareButCannotReturnToFlight()
    {
        var task = new LandingTaskData { Status = WaitingForMap };
        Assert.False(task.TryTransition(LandingTransitStatus.InFlight, out _));
        Assert.True(task.TryTransition(LandingTransitStatus.PreparingDestination, out _));
    }

    [Fact]
    public void WaitingForMapNeverAdvancesWithTime()
    {
        var task = new LandingTaskData { Status = WaitingForMap, StartTick = -10000 };
        var previous = LandingTransitService.Arrived;
        LandingTransitService.Arrived = _ => throw new InvalidOperationException("Implicit arrival");
        try
        {
            LandingTransitService.Advance(task);
            Assert.Equal(WaitingForMap, task.Status);
            Assert.Equal(-1, task.DestinationMapId);
        }
        finally { LandingTransitService.Arrived = previous; }
    }

    [Fact]
    public void GeneralResumeCannotRequestFirstMap()
    {
        Game previousGame = Current.Game;
        var previousArrival = LandingTransitService.Arrived;
        var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        var manager = new LandingTransitManager(game);
        var task = new LandingTaskData { TaskId = "waiting", Status = WaitingForMap };
        manager.Register(task);
        game.components = new List<GameComponent> { manager };
        LandingTransitService.Arrived = _ => throw new InvalidOperationException("Implicit map request");
        try
        {
            Current.Game = game;
            Assert.False(LandingTransitService.TryResume(task.TaskId, out var report));
            Assert.False(report.Success);
            Assert.Equal(WaitingForMap, task.Status);
            Assert.Equal(-1, task.DestinationMapId);
        }
        finally { Current.Game = previousGame; LandingTransitService.Arrived = previousArrival; }
    }
}
