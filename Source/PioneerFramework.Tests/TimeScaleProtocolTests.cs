using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PioneerFramework.Combat;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class TimeScaleProtocolTests
{
    private static int messages;

    [Fact]
    public void SwitchingSpeedDoesNotEmitSynchronizationMessages()
    {
        WithClock((host, clock) =>
        {
            clock.SetFastMode();
            clock.SetNormalMode();
            Assert.Equal(0, messages);
        });
    }

    [Fact]
    public void CombatAndTerminalRestrictionsRestoreTheLatestPreferenceWhenTheyEnd()
    {
        WithClock((host, clock) =>
        {
            var source = host.AddShip();
            var target = new SpaceEntityGroup(11, 2) { CelestialBodyId = 0 };
            host.Space.RegisterGroup(target);
            source.HasRendezvousGuidance = true;
            source.rendezvousTargetId = target.GroupId;
            clock.SetFastMode();
            Assert.Equal(TimeScaleLevel.Combat, clock.CurrentLevel);
            Assert.True(clock.Restrictions.HasFlag(TimeScaleRestriction.TerminalPrecision));
            AddProjectile(host);
            clock.RefreshConstraints();
            source.HasRendezvousGuidance = false;
            clock.SetNormalMode();
            Assert.Equal(TimeScaleLevel.Combat, clock.CurrentLevel);
            ((List<ProjectileInstance>)AccessTools.Field(typeof(CombatManager), "activeProjectiles")
                .GetValue(host.Game.GetComponent<CombatManager>())).Clear();
            clock.RefreshConstraints();
            Assert.Equal(TimeScaleLevel.Normal, clock.CurrentLevel);
            Assert.Equal(TimeScaleRestriction.None, clock.Restrictions);
        });
    }
    [Fact]
    public void SpeedPreferenceDoesNotWaitForAnotherGameTick()
    {
        WithClock((host, clock) =>
        {
            clock.SetFastMode();
            Assert.Equal(TimeScaleLevel.Fast, clock.CurrentLevel);
            Assert.Equal(100, clock.GetPhysicsTick());
            clock.SetNormalMode();
            Assert.Equal(TimeScaleLevel.Normal, clock.CurrentLevel);
        });
    }

    [Theory]
    [InlineData(TimeScaleLevel.Normal)]
    [InlineData(TimeScaleLevel.Fast)]
    public void CombatRestrictionCannotBeOverriddenByPreferenceOrTimeout(TimeScaleLevel request)
    {
        WithClock((host, clock) =>
        {
            AddProjectile(host);
            clock.SetCombatModeImmediate();
            clock.SetTimeScale(request);
            SetTick(701);
            clock.GameComponentTick();
            Assert.Equal(TimeScaleLevel.Combat, clock.CurrentLevel);
        });
    }

    [Fact]
    public void ImmediateCombatCannotBeOverwrittenByAnOlderFastRequest()
    {
        WithClock((host, clock) =>
        {
            clock.SetFastMode();
            AddProjectile(host);
            SetTick(101);
            clock.SetCombatModeImmediate();
            SetTick(102);
            clock.GameComponentTick();
            Assert.Equal(TimeScaleLevel.Combat, clock.CurrentLevel);
        });
    }

    [Fact]
    public void PausedPreferenceIsRecordedWithoutAdvancingPhysics()
    {
        WithClock((host, clock) =>
        {
            AccessTools.Field(typeof(TickManager), "curTimeSpeed").SetValue(Find.TickManager, TimeSpeed.Paused);
            clock.SetFastMode();
            Assert.Equal(100, clock.GetPhysicsTick());
            Assert.Equal(TimeScaleLevel.Fast, clock.CurrentLevel);
        });
    }

    [Fact]
    public void SwitchingToCombatCannotMoveRenderedTimeBackwards()
    {
        WithClock((host, clock) =>
        {
            clock.SetFastMode();
            clock.GetRenderTick();
            SetTick(102);
            clock.GameComponentTick();
            RealTime.frameCount++;
            clock.GetRenderTick();
            RealTime.frameCount++;
            double before = clock.GetRenderTick();
            clock.SetCombatModeImmediate();
            Assert.True(clock.GetRenderTick() >= before);
            RealTime.frameCount++;
            Assert.True(clock.GetRenderTick() >= before);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReentrantRequestsReachAStableRateWhilePaused(bool combat)
    {
        WithClock((host, clock) =>
        {
            AccessTools.Field(typeof(TickManager), "curTimeSpeed").SetValue(Find.TickManager, TimeSpeed.Paused);
            int events = 0;
            clock.OnTimeScaleChanged += (_, level) =>
            {
                events++;
                if (level != TimeScaleLevel.Fast) return;
                if (combat) clock.SetCombatModeImmediate();
                else clock.SetNormalMode();
            };
            clock.SetFastMode();
            Assert.Equal(combat ? TimeScaleLevel.Combat : TimeScaleLevel.Normal, clock.CurrentLevel);
            Assert.Equal(2, events);
            Assert.Equal(100, clock.GetPhysicsTick());
        });
    }

    internal static void SetTick(int tick)
        => AccessTools.Field(typeof(TickManager), "ticksGameInt").SetValue(Find.TickManager, tick);

    private static void AddProjectile(SpaceAiRuntimeTestHost host)
        => ((List<ProjectileInstance>)AccessTools.Field(typeof(CombatManager), "activeProjectiles")
            .GetValue(host.Game.GetComponent<CombatManager>())).Add(new ProjectileInstance());

    internal static void WithClock(Action<SpaceAiRuntimeTestHost, TimeScaleManager> check, int start = 100)
    {
        using var host = new SpaceAiRuntimeTestHost();
        var patch = new Harmony("PFL.TimeScaleProtocolTests");
        foreach (var method in typeof(Messages).GetMethods().Where(m => m.Name == nameof(Messages.Message)))
            patch.Patch(method, prefix: new HarmonyMethod(typeof(TimeScaleProtocolTests), nameof(SkipMessage)));
        int previousFrame = RealTime.frameCount;
        float previousDelta = RealTime.deltaTime;
        try
        {
            SetTick(start);
            AccessTools.Field(typeof(TickManager), "curTimeSpeed").SetValue(Find.TickManager, TimeSpeed.Normal);
            var clock = new TimeScaleManager(host.Game);
            host.Game.components.Add(clock);
            RealTime.frameCount = 10000;
            RealTime.deltaTime = 1f / 240f;
            clock.GetPhysicsTick();
            messages = 0;
            check(host, clock);
        }
        finally
        {
            RealTime.frameCount = previousFrame;
            RealTime.deltaTime = previousDelta;
            patch.UnpatchAll(patch.Id);
        }
    }

    private static bool SkipMessage() { messages++; return false; }
}
