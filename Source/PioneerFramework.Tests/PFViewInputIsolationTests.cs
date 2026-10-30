using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class PFViewInputIsolationTests
{
    private static Type PatchType()
    {
        var type = typeof(MainTabWindow_PFView).Assembly.GetType("PioneerFramework.Patch_PFView_BlockHostKeyboard");
        Assert.NotNull(type);
        return type;
    }

    private static void Consume(Event input, bool viewOpen)
    {
        var method = PatchType().GetMethod("ConsumeHostKeyboard", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(null, new object[] { input, viewOpen });
    }

    [Theory]
    [InlineData(EventType.KeyDown, KeyCode.W, EventModifiers.None)]
    [InlineData(EventType.KeyUp, KeyCode.W, EventModifiers.None)]
    [InlineData(EventType.KeyDown, KeyCode.F, EventModifiers.None)]
    [InlineData(EventType.KeyDown, KeyCode.Tab, EventModifiers.None)]
    [InlineData(EventType.KeyDown, KeyCode.Alpha1, EventModifiers.Control)]
    [InlineData(EventType.KeyDown, KeyCode.Space, EventModifiers.None)]
    [InlineData(EventType.KeyDown, KeyCode.Escape, EventModifiers.None)]
    [InlineData(EventType.KeyDown, KeyCode.Return, EventModifiers.None)]
    public void UnhandledKeyboardCannotReachHostShortcuts(EventType type, KeyCode key, EventModifiers modifiers)
    {
        var input = new Event { type = type, rawType = type, keyCode = key, modifiers = modifiers };
        Consume(input, true);
        Assert.Equal(EventType.Used, input.type);
    }

    [Theory]
    [InlineData(EventType.MouseDown)]
    [InlineData(EventType.MouseUp)]
    [InlineData(EventType.MouseDrag)]
    [InlineData(EventType.ScrollWheel)]
    [InlineData(EventType.Layout)]
    [InlineData(EventType.Repaint)]
    [InlineData(EventType.Used)]
    public void HostGatePreservesBottomTabClicksAndNonKeyboardEvents(EventType type)
    {
        var input = new Event { type = type, rawType = type };
        Consume(input, true);
        Assert.Equal(type, input.type);
    }

    [Theory]
    [InlineData(EventType.KeyDown)]
    [InlineData(EventType.KeyUp)]
    public void ClosingPFViewRestoresHostKeyboard(EventType type)
    {
        var input = new Event { type = type, rawType = type, keyCode = KeyCode.W };
        Consume(input, false);
        Assert.Equal(type, input.type);
    }

    [Fact]
    public void MissingEventIsHarmless() => Consume(null, true);

    [Fact]
    public void GuardRunsBeforeAllHostPlayUiShortcuts()
    {
        var patch = Assert.Single(PatchType().GetCustomAttributes<HarmonyPatch>());
        Assert.Equal(typeof(UIRoot_Play), patch.info.declaringType);
        Assert.Equal(nameof(UIRoot_Play.UIRootOnGUI), patch.info.methodName);
        Assert.NotNull(PatchType().GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static));
    }

    [Fact]
    public void ConsumingGuiEventPreservesPublishedSteeringUntilKeyRelease()
    {
        var profile = new SteeringBindingProfile();
        SteeringController.Open(71);
        try
        {
            SteeringInputHost.ProcessSnapshot(profile, Array.Empty<int>(), 0, true);
            SteeringInputHost.ProcessSnapshot(profile, new[] { (int)KeyCode.W }, 0, true);
            var input = new Event { type = EventType.KeyDown, rawType = EventType.KeyDown, keyCode = KeyCode.W };
            Consume(input, true);
            Assert.Equal(EventType.Used, input.type);
            Assert.True(SteeringController.KeyPitchU);

            SteeringInputHost.ProcessSnapshot(profile, Array.Empty<int>(), 0, true);
            var release = new Event { type = EventType.KeyUp, rawType = EventType.KeyUp, keyCode = KeyCode.W };
            Consume(release, true);
            Assert.Equal(EventType.Used, release.type);
            Assert.False(SteeringController.KeyPitchU);
        }
        finally
        {
            SteeringController.Close();
        }
    }
}
