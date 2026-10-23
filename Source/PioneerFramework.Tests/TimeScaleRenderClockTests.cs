using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class TimeScaleRenderClockTests
{
    [Theory]
    [InlineData(TimeScaleLevel.Combat, 1.0 / 60.0)]
    [InlineData(TimeScaleLevel.Normal, 1.0)]
    [InlineData(TimeScaleLevel.Fast, 8.0)]
    public void RepeatedReadsInOneFrameDoNotAdvanceCelestialPositions(TimeScaleLevel level, double scale)
    {
        WithClock((clock, _) =>
        {
            AccessTools.Field(typeof(TimeScaleManager), "currentLevel").SetValue(clock, level);
            clock.GetPhysicsTick();
            AccessTools.Field(typeof(TickManager), "ticksGameInt").SetValue(Find.TickManager, 2);
            double start = clock.GetRenderTick();
            RealTime.frameCount++;
            double first = clock.GetRenderTick();
            Assert.Equal(start + 0.25 * scale, first, 6);

            // 相机原点、防穿入、行星与射弹必须能反复取同一帧的时间。
            for (int i = 0; i < 8; i++)
                Assert.Equal(first, clock.GetRenderTick());

            RealTime.frameCount++;
            Assert.Equal(start + 0.5 * scale, clock.GetRenderTick(), 6);
        });
    }

    [Fact]
    public void RepeatedReadsCannotMoveAPlanetThroughTheGuardedCamera()
    {
        WithClock((clock, _) =>
        {
            AccessTools.Field(typeof(TimeScaleManager), "currentLevel").SetValue(clock, TimeScaleLevel.Fast);
            clock.GetPhysicsTick();
            AccessTools.Field(typeof(TickManager), "ticksGameInt").SetValue(Find.TickManager, 2);
            clock.GetRenderTick();
            RealTime.frameCount++;

            double cameraTick = clock.GetRenderTick();
            const float radiusView = 500000f;
            Vector3 camera = PFViewCameraCore.ClampCameraOutsideSphere(
                new Vector3(radiusView, 0f, 0f), Vector3.zero, radiusView);
            float near = PFViewCameraCore.NearClipForCamera(100f,
                camera.x - radiusView, 0.01f, 5000f);

            // 20 km/s 的径向运动；相机原点仍锚在 cameraTick。
            // 旧时钟再读一次会前进 2 秒：球心移近 40 km，越过 10 km 防穿入余量。
            double sceneTick = clock.GetRenderTick();
            Vector3 drawnCenter = new Vector3((float)((sceneTick - cameraTick) * 20000.0 * 0.1), 0f, 0f);
            float gap = Vector3.Distance(camera, drawnCenter) - radiusView;
            Assert.True(gap > near, $"绘制时表面余量 {gap} 必须大于近裁 {near}");
        });
    }

    [Fact]
    public void PausedFramesKeepTheRenderClockAtThePhysicsTick()
    {
        WithClock((clock, ticks) =>
        {
            AccessTools.Field(typeof(TickManager), "curTimeSpeed").SetValue(ticks, TimeSpeed.Paused);
            double start = clock.GetPhysicsTick();
            for (int i = 0; i < 5; i++)
            {
                RealTime.frameCount++;
                Assert.Equal(start, clock.GetRenderTick());
            }
        });
    }

    private static void WithClock(Action<TimeScaleManager, TickManager> check)
    {
        Game previousGame = Current.Game;
        int previousFrame = RealTime.frameCount;
        float previousDelta = RealTime.deltaTime;
        var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        var ticks = (TickManager)RuntimeHelpers.GetUninitializedObject(typeof(TickManager));
        AccessTools.Field(typeof(Game), "tickManager").SetValue(game, ticks);
        AccessTools.Field(typeof(TickManager), "curTimeSpeed").SetValue(ticks, TimeSpeed.Normal);
        var clock = new TimeScaleManager(game);
        game.components = new List<GameComponent> { clock };
        try
        {
            Current.Game = game;
            RealTime.frameCount = 10000;
            RealTime.deltaTime = 1f / 240f;
            check(clock, ticks);
        }
        finally
        {
            Current.Game = previousGame;
            RealTime.frameCount = previousFrame;
            RealTime.deltaTime = previousDelta;
        }
    }
}
