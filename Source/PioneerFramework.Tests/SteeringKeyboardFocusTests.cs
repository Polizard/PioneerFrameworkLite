using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class SteeringKeyboardFocusTests : IDisposable
{
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo RootField = typeof(Current).GetField("rootInt", BindingFlags.Static | BindingFlags.NonPublic);
    private readonly object previousRoot = RootField.GetValue(null);
    private readonly PioneerFrameworkLiteSettings previousSettings = PioneerFrameworkLiteMod.Settings;
    private readonly Window_Steering steering = new(false, false);
    private readonly List<Window> windows;

    public SteeringKeyboardFocusTests()
    {
        var root = (Root_Play)RuntimeHelpers.GetUninitializedObject(typeof(Root_Play));
        var ui = (UIRoot_Play)RuntimeHelpers.GetUninitializedObject(typeof(UIRoot_Play));
        ui.windows = new WindowStack();
        root.uiRoot = ui;
        RootField.SetValue(null, root);
        windows = (List<Window>)typeof(WindowStack).GetField("windows", Flags).GetValue(ui.windows);
        windows.Add(steering);
        PioneerFrameworkLiteMod.Settings = new();
        SteeringController.Open(71);
    }

    public void Dispose()
    {
        SteeringController.Close();
        PioneerFrameworkLiteMod.Settings = previousSettings;
        RootField.SetValue(null, previousRoot);
        GC.SuppressFinalize(this);
    }

    private bool WindowCanReadKeyboard()
        => (bool)typeof(Window_Steering).GetMethod("CanReadKeyboard", Flags).Invoke(steering, null);

    [Theory]
    [InlineData(SasTargetKind.Prograde)]
    [InlineData(SasTargetKind.Retrograde)]
    [InlineData(SasTargetKind.None)]
    public void PassiveTooltipKeepsRotationAndThrustHeldAcrossSteeringModes(SasTargetKind mode)
    {
        Assert.True(WindowCanReadKeyboard());
        Assert.True(SteeringInputHost.CanReadKeyboard());
        var profile = new SteeringBindingProfile();
        SteeringInputHost.ProcessSnapshot(profile, Array.Empty<int>(), 0, true);
        SteeringController.SasKind = mode;
        windows.Add(new ImmediateWindow { layer = WindowLayer.Super });

        SteeringInputHost.ProcessSnapshot(profile, new[] { (int)KeyCode.W, (int)KeyCode.R }, 0, WindowCanReadKeyboard());
        SteeringInputHost.EnsureInputSafety();

        Assert.True(SteeringController.KeyPitchU);
        Assert.True(SteeringController.KeyForward);
        Assert.Equal(mode, SteeringController.SasKind);
        windows.RemoveAt(windows.Count - 1);
        SteeringInputHost.ProcessSnapshot(profile, new[] { (int)KeyCode.W, (int)KeyCode.R }, 0, WindowCanReadKeyboard());
        Assert.True(SteeringController.KeyPitchU);
        Assert.True(SteeringController.KeyForward);
    }

    [Fact]
    public void RealDialogUnderTooltipStillSuspendsAndRequiresKeyRelease()
    {
        var profile = new SteeringBindingProfile();
        SteeringInputHost.ProcessSnapshot(profile, Array.Empty<int>(), 0, true);
        SteeringInputHost.ProcessSnapshot(profile, new[] { (int)KeyCode.W }, 0, true);
        windows.Add(new KeyboardDialog());
        windows.Add(new ImmediateWindow { layer = WindowLayer.Super });
        Assert.False(WindowCanReadKeyboard());
        Assert.False(SteeringInputHost.CanReadKeyboard());
        SteeringInputHost.ProcessSnapshot(profile, new[] { (int)KeyCode.W }, 0, WindowCanReadKeyboard());
        Assert.False(SteeringController.KeyPitchU);

        windows.RemoveRange(1, 2);
        SteeringInputHost.ProcessSnapshot(profile, new[] { (int)KeyCode.W }, 0, WindowCanReadKeyboard());
        Assert.False(SteeringController.KeyPitchU);
        SteeringInputHost.ProcessSnapshot(profile, Array.Empty<int>(), 0, WindowCanReadKeyboard());
        SteeringInputHost.ProcessSnapshot(profile, new[] { (int)KeyCode.W }, 0, WindowCanReadKeyboard());
        Assert.True(SteeringController.KeyPitchU);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ImmediateWindowThatClaimsInputStillSuspendsSteering(bool absorbsInput, bool takesFocus)
    {
        windows.Add(new ImmediateWindow { absorbInputAroundWindow = absorbsInput, focusWhenOpened = takesFocus });
        Assert.False(WindowCanReadKeyboard());
        Assert.False(SteeringInputHost.CanReadKeyboard());
    }

    private sealed class KeyboardDialog : Window
    {
        public override void DoWindowContents(Rect inRect) { }
    }
}
