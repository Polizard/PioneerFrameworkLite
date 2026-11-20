using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class PFViewThirdPersonTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const float SqrtHalf = .7071067812f;

    [Theory]
    [InlineData(0f, 0f, 0f, 1f, 0f, 25.881905f, -96.59258f)]
    [InlineData(0f, SqrtHalf, 0f, SqrtHalf, -96.59258f, 25.881905f, 0f)]
    [InlineData(0f, 0f, SqrtHalf, SqrtHalf, -25.881905f, 0f, -96.59258f)]
    public void TailViewUsesShipHeadingAndRoll(float qx, float qy, float qz, float qw, float x, float y, float z)
    {
        var frame = PFViewCameraCore.GroupOrbitFrame(new(qx, qy, qz, qw));
        var rotation = PFViewCameraCore.OrbitRotation(0, 15, frame);
        Vector3 offset = rotation * Vector3.forward * 100;
        Assert.Equal(x, offset.x, 3);
        Assert.Equal(y, offset.y, 3);
        Assert.Equal(z, offset.z, 3);
        Assert.Equal(0f, Vector3.Dot(offset.normalized, rotation * Vector3.up), 4);
    }

    [Fact]
    public void CameraAndShipUseSameViewFrameRotation()
    {
        var viewFrame = new Quaternion(0, SqrtHalf, 0, SqrtHalf);
        var ship = new Quaternion(0, SqrtHalf, 0, SqrtHalf);
        var rotation = PFViewCameraCore.OrbitRotation(0, 0, PFViewCameraCore.GroupOrbitFrame(viewFrame * ship));
        Vector3 offset = rotation * Vector3.forward;
        Assert.Equal(0f, offset.x, 4);
        Assert.Equal(1f, offset.z, 4);
    }

    [Fact]
    public void SwitchingEquivalentWorldAndTailPosesDoesNotMakeAFullTurn()
    {
        var from = PFViewCameraCore.OrbitRotation(-180, 0, Quaternion.identity);
        var to = PFViewCameraCore.OrbitRotation(0, 0, PFViewCameraCore.GroupOrbitFrame(Quaternion.identity));
        for (int step = 0; step <= 10; step++)
        {
            var pose = PFViewCameraCore.InterpolateOrbit(from, 100, to, 100, step / 10f);
            Assert.Equal(-100f, pose.Offset.z, 3);
            Assert.Equal(1f, pose.Up.y, 4);
        }
    }

    [Fact]
    public void QuaternionSignDoesNotCreateRotationAndRollingTransitionRemainsOrthogonal()
    {
        var from = new Quaternion(0, 0, SqrtHalf, SqrtHalf);
        var same = new Quaternion(0, 0, -SqrtHalf, -SqrtHalf);
        var middle = PFViewCameraCore.InterpolateOrbit(from, 100, same, 200, .5f);
        Assert.Equal(150f, middle.Offset.z, 3);
        Assert.Equal(-1f, middle.Up.x, 4);
        for (int step = 0; step <= 100; step++)
        {
            var pose = PFViewCameraCore.InterpolateOrbit(Quaternion.identity, 100, from, 100, step / 100f);
            Assert.Equal(100f, pose.Offset.magnitude, 3);
            Assert.Equal(0f, Vector3.Dot(pose.Offset.normalized, pose.Up), 4);
            Assert.Equal(1f, pose.Up.magnitude, 4);
        }
    }

    [Theory]
    [InlineData(PFViewGroupCameraMode.Free)]
    [InlineData(PFViewGroupCameraMode.ThirdPersonHull)]
    [InlineData(PFViewGroupCameraMode.ThirdPersonVelocity)]
    public void ModPreferenceRoundTripKeepsFocusDefaultAndExistingSkyPreference(PFViewGroupCameraMode mode)
    {
        var settings = new PioneerFrameworkLiteSettings { pfviewFocusCameraMode = mode, replaceWorldSky = false };
        string path = Path.Combine(Path.GetTempPath(), "pfl-camera-" + Guid.NewGuid() + ".xml");
        var prefs = AccessTools.Field(typeof(Prefs), "data");
        object previous = prefs.GetValue(null);
        prefs.SetValue(null, RuntimeHelpers.GetUninitializedObject(prefs.FieldType));
        try
        {
            Scribe.saver.InitSaving(path, "camera-settings"); settings.ExposeData(); Scribe.saver.FinalizeSaving();
            var restored = new PioneerFrameworkLiteSettings { pfviewFocusCameraMode = PFViewGroupCameraMode.Free };
            Scribe.loader.InitLoading(path); restored.ExposeData(); Scribe.loader.FinalizeLoading();
            Assert.Equal(mode, restored.pfviewFocusCameraMode);
            Assert.False(restored.replaceWorldSky);
        }
        finally { Scribe.ForceStop(); prefs.SetValue(null, previous); if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("False", PFViewGroupCameraMode.Free)]
    [InlineData("True", PFViewGroupCameraMode.ThirdPersonHull)]
    public void LegacyThirdPersonOnFocusBooleanMigratesToTheMatchingMode(string legacyValue, PFViewGroupCameraMode expected)
    {
        string path = Path.Combine(Path.GetTempPath(), "pfl-camera-legacy-" + Guid.NewGuid() + ".xml");
        var prefs = AccessTools.Field(typeof(Prefs), "data");
        object previous = prefs.GetValue(null);
        prefs.SetValue(null, RuntimeHelpers.GetUninitializedObject(prefs.FieldType));
        try
        {
            File.WriteAllText(path,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<camera-settings>\n"
                + "  <pfviewThirdPersonOnFocus>" + legacyValue + "</pfviewThirdPersonOnFocus>\n"
                + "</camera-settings>");
            var restored = new PioneerFrameworkLiteSettings { pfviewFocusCameraMode = PFViewGroupCameraMode.ThirdPersonVelocity };
            Scribe.loader.InitLoading(path); restored.ExposeData(); Scribe.loader.FinalizeLoading();
            Assert.Equal(expected, restored.pfviewFocusCameraMode);
        }
        finally { Scribe.ForceStop(); prefs.SetValue(null, previous); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ExplicitFocusReadsSettingWhileInspectionRemainsCameraIndependent()
    {
        var focus = PFViewUiIntegrationTests.ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("FocusGroup", Flags)).ToArray();
        Assert.Contains(focus, m => m.Name == "DefaultGroupCameraMode");
        var inspect = PFViewUiIntegrationTests.ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("SelectGroupForInspection", Flags)).ToArray();
        Assert.DoesNotContain(inspect, m => m.Name == "FocusGroup" || m.Name == "SwitchGroupCameraMode");
        var defaults = PFViewUiIntegrationTests.ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("DefaultGroupCameraMode", Flags)).ToArray();
        Assert.Contains(defaults, m => m.DeclaringType == typeof(PioneerFrameworkLiteSettings) && m.Name == "pfviewFocusCameraMode");
    }

    [Fact]
    public void CameraModeSwitchKeepsInspectionControlAndTargetAndHasVisibleToolbarEntry()
    {
        var change = typeof(MainTabWindow_PFView).GetMethod("SwitchGroupCameraMode", Flags);
        Assert.NotNull(change);
        var members = PFViewUiIntegrationTests.ReferencedMembers(change).ToArray();
        Assert.DoesNotContain(members, m => m.DeclaringType == typeof(SteeringController)
            || m.Name == "targetGroupId" || m.Name == "selectedGroupId");
        Assert.Contains(members, m => m.Name == "SaveCameraMemory");
        Assert.Contains(members, m => m.Name == "TryLoadPose");
        Assert.Contains(PFViewUiIntegrationTests.ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("DrawToolbar", Flags)),
            m => m.Name == "ShowCameraModeMenu");
    }

    [Fact]
    public void FollowFrameChecksVisibilityBeforeReadingShipAttitude()
    {
        var method = typeof(MainTabWindow_PFView).GetMethod("ResolveOrbitFrame", Flags);
        Assert.NotNull(method);
        var members = PFViewUiIntegrationTests.ReferencedMembers(method).ToList();
        Assert.True(members.FindIndex(m => m.Name == "CanInspectEntity") >= 0);
        Assert.True(members.FindIndex(m => m.Name == "CanInspectEntity") < members.FindIndex(m => m.Name == "CurrentOrbit"));
        Assert.Contains(members, m => m.Name == "viewFrameRot");
        Assert.Contains(members, m => m.Name == "GroupOrbitFrame");
    }

    [Fact]
    public void SessionResetExplicitlyCancelsOldWorldTransition()
    {
        // The window static constructor creates Unity resources, so inspect compiled reset routing.
        var reset = typeof(MainTabWindow_PFView).GetMethod("ResetCameraSession", Flags);
        var instructions = PatchProcessor.GetOriginalInstructions(reset).ToList();
        var active = typeof(MainTabWindow_PFView).GetField("cameraTransitioning", Flags);
        int store = instructions.FindIndex(i => i.opcode == OpCodes.Stfld && Equals(i.operand, active));
        Assert.True(store > 0, "Session reset must invalidate the previous world's active transition.");
        Assert.Equal(OpCodes.Ldc_I4_0, instructions[store - 1].opcode);
    }

    [Fact]
    public void PerModeMemoryAndViewHistoryCarryModeWhileLossUsesDisplayedPose()
    {
        var saved = PFViewUiIntegrationTests.ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("SaveCameraMemory", Flags)).ToArray();
        Assert.Contains(saved, m => m.Name == "Remember" && m.DeclaringType.Name.StartsWith("PFViewGroupCameraMemory", StringComparison.Ordinal));
        var capture = PFViewUiIntegrationTests.ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("CapturePose", Flags)).ToArray();
        Assert.Contains(capture, m => m.Name == "Mode");
        var restore = PFViewUiIntegrationTests.ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("RestorePreviousView", Flags)).ToArray();
        Assert.Contains(restore, m => m.Name == "ApplyCameraPose");
        var freeze = PFViewUiIntegrationTests.ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("FreezeFocus", Flags)).ToArray();
        Assert.Contains(freeze, m => m.Name == "LockFrozenCamera");
    }

    [Fact]
    public void VelocityModeRoutesTheCameraFrameThroughVelocityWhileHullModeKeepsShipAttitude()
    {
        var method = typeof(MainTabWindow_PFView).GetMethod("ResolveOrbitFrame", Flags);
        Assert.NotNull(method);
        var members = PFViewUiIntegrationTests.ReferencedMembers(method).ToList();
        Assert.Contains(members, m => m.Name == "GroupOrbitFrame");
        Assert.Contains(members, m => m.Name == "GroupVelocityOrbitFrame");
        Assert.Contains(members, m => m.Name == "velocitySystem");
        Assert.Contains(members, m => m.Name == "CurrentOrbit");
        Assert.Contains(members, m => m.Name == "viewFrameRot");
        int inspect = members.FindIndex(m => m.Name == "CanInspectEntity");
        int orbit = members.FindIndex(m => m.Name == "CurrentOrbit");
        Assert.True(inspect >= 0, "ResolveOrbitFrame must check CanInspectEntity before reading ship attitude.");
        Assert.True(inspect < orbit);
    }

    [Fact]
    public void ResetAndUpdateCameraShareOneThirdPersonBranchForBothThirdPersonModes()
    {
        // 两种第三人称共用 IsThirdPersonView / ThirdPersonDistances ⇒ ResetCamera 与 UpdateCamera
        // 不能只认某一种模式（否则速度正向会走自由视角的距离/俯仰夹取）。
        var reset = PFViewUiIntegrationTests.ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("ResetCamera", Flags)).ToList();
        Assert.Contains(reset, m => m.Name == "get_IsThirdPersonView");
        Assert.Contains(reset, m => m.Name == "ThirdPersonDistances");

        var update = PFViewUiIntegrationTests.ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("UpdateCamera", Flags)).ToList();
        Assert.Contains(update, m => m.Name == "get_IsThirdPersonView");
        Assert.Contains(update, m => m.Name == "ThirdPersonDistances");
        Assert.Contains(update, m => m.Name == "LookYaw");
        // 2026-11-20：第三种人俯仰不再夹在 ±85°，与自由视角共用整圈归一化。
        Assert.Contains(update, m => m.DeclaringType == typeof(PFViewCameraCore) && m.Name == "ResolvePitch");
        Assert.DoesNotContain(update, m => m.Name == "LookPitch" || m.Name == "ClampPitch");
    }

    [Fact]
    public void VelocityFrameAimsTheCameraAlongVelocityAndKeepsHullUp()
    {
        // 船头 +Z、速度 +X：最小旋转 = 绕 +Y 90°，船体上方向应原样保留。
        var frame = PFViewCameraCore.GroupVelocityOrbitFrame(Quaternion.identity, Vector3.right);
        AssertVector(Vector3.right, frame * Vector3.back, 4);
        AssertVector(Vector3.up, frame * Vector3.up, 4);
    }

    [Fact]
    public void VelocityFrameFlipsOverHullUpWhenVelocityIsOppositeTheNose()
    {
        // 船头 +Z、速度 −Z：180° 反平行 ⇒ 绕船体上方向翻，上方向不变、前向取 −Z。
        var frame = PFViewCameraCore.GroupVelocityOrbitFrame(Quaternion.identity, Vector3.back);
        AssertVector(Vector3.back, frame * Vector3.back, 4);
        AssertVector(Vector3.up, frame * Vector3.up, 4);
    }

    [Fact]
    public void ZeroOrInvalidVelocityFallsBackToTheHullFrame()
    {
        var hull = new Quaternion(0, SqrtHalf, 0, SqrtHalf);
        var expected = PFViewCameraCore.GroupOrbitFrame(hull);
        AssertSameRotation(expected, PFViewCameraCore.GroupVelocityOrbitFrame(hull, Vector3.zero));
        AssertSameRotation(expected, PFViewCameraCore.GroupVelocityOrbitFrame(hull, new Vector3(float.NaN, 0f, 0f)));
        AssertSameRotation(expected, PFViewCameraCore.GroupVelocityOrbitFrame(hull, new Vector3(1f, float.NaN, 0f)));
        AssertSameRotation(expected, PFViewCameraCore.GroupVelocityOrbitFrame(hull, new Vector3(0f, 0f, float.NaN)));
        AssertSameRotation(expected, PFViewCameraCore.GroupVelocityOrbitFrame(hull, new Vector3(float.PositiveInfinity, 0f, 0f)));
        AssertSameRotation(expected, PFViewCameraCore.GroupVelocityOrbitFrame(hull, new Vector3(0f, float.NegativeInfinity, 0f)));
    }

    [Fact]
    public void VelocityAlreadyAlongTheNoseKeepsTheHullFrame()
    {
        var hull = new Quaternion(0, SqrtHalf, 0, SqrtHalf);
        var nose = hull * Vector3.forward;
        AssertSameRotation(PFViewCameraCore.GroupOrbitFrame(hull), PFViewCameraCore.GroupVelocityOrbitFrame(hull, nose * 7800f));
    }

    [Fact]
    public void VelocityFrameStaysOrthonormalForArbitraryAttitudesAndVelocities()
    {
        // 任意姿态用**单位**四元数（轴/角构造）——非单位四元数不属生产输入（生产是 Unity 旋转）。
        float half = 0.55f;
        Vector3 axis = new Vector3(1f, 2f, 3f).normalized;
        var tilted = new Quaternion(axis.x * Mathf.Sin(half), axis.y * Mathf.Sin(half), axis.z * Mathf.Sin(half), Mathf.Cos(half));
        var hulls = new[]
        {
            Quaternion.identity,
            new Quaternion(0, SqrtHalf, 0, SqrtHalf),
            tilted,
        };
        var velocities = new[]
        {
            new Vector3(1f, 2f, 3f),
            new Vector3(-3000f, 0f, 120f),
            new Vector3(0.001f, -2f, 4f),
            new Vector3(12000f, -8000f, 3000f),
        };
        foreach (var hull in hulls)
        foreach (var velocity in velocities)
        {
            var frame = PFViewCameraCore.GroupVelocityOrbitFrame(hull, velocity);
            Vector3 forward = frame * Vector3.back;
            Vector3 up = frame * Vector3.up;
            Vector3 right = frame * Vector3.right;
            AssertVector(velocity.normalized, forward, 4);
            Assert.Equal(0f, Vector3.Dot(forward, up), 4);
            Assert.Equal(0f, Vector3.Dot(forward, right), 4);
            Assert.Equal(0f, Vector3.Dot(up, right), 4);
            Assert.Equal(1f, up.magnitude, 4);
            Assert.Equal(1f, right.magnitude, 4);
            // 最小旋转只把前向搬走 ⇒ 上方向与船体上方向不反向（滚转不会被无意义地翻）。
            Assert.True(Vector3.Dot(up, hull * Vector3.up) > 0f);
        }
    }

    private static void AssertVector(Vector3 expected, Vector3 actual, int digits)
    {
        Assert.Equal(expected.x, actual.x, digits);
        Assert.Equal(expected.y, actual.y, digits);
        Assert.Equal(expected.z, actual.z, digits);
    }

    private static void AssertSameRotation(Quaternion expected, Quaternion actual)
    {
        AssertVector(expected * Vector3.forward, actual * Vector3.forward, 4);
        AssertVector(expected * Vector3.up, actual * Vector3.up, 4);
        AssertVector(expected * Vector3.right, actual * Vector3.right, 4);
    }
}
