using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests;

/// <summary>操舵捕获应隔离后来的实时输入，同时两条入口始终调用同一份数值运算。</summary>
public sealed class CelestialSteeringStepTests : IDisposable
{
    private readonly PioneerFrameworkLiteSettings previous = PioneerFrameworkLiteMod.Settings;
    private const BindingFlags Methods = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public CelestialSteeringStepTests()
    {
        PioneerFrameworkLiteMod.Settings = new() { pfviewInvertPitch = false, pfviewAssistStop = true, pfviewPrecisionScale = .25f };
        SteeringController.Open(71);
    }

    public void Dispose() { SteeringController.Close(); PioneerFrameworkLiteMod.Settings = previous; }

    [Theory]
    [InlineData("GetTranslationWorldDir")]
    [InlineData("GetRotationWorldAngularVelocity")]
    [InlineData("ApplyManualRotation")]
    [InlineData("ScaleManualThrust")]
    [InlineData("GetSasTarget")]
    public void LiveAndCapturedEntryPointsCallTheSameSharedCalculationWithoutAllocatingInputObjects(string name)
    {
        var shared = typeof(SteeringController).Assembly.GetType("PioneerFramework.SteeringStepMath");
        Assert.NotNull(shared);
        foreach (var type in new[] { typeof(SteeringController), typeof(SteeringController.StepInput) })
        {
            var method = type.GetMethod(name, Methods);
            Assert.NotNull(method);
            var instructions = PatchProcessor.GetOriginalInstructions(method).ToArray();
            Assert.Contains(instructions, instruction => instruction.operand is MethodInfo called && called.DeclaringType == shared && called.Name == name);
            var capturePath = EntryPointInstructions(method, new[] { typeof(SteeringController), typeof(SteeringController.StepInput), shared }, new HashSet<MethodBase>());
            Assert.DoesNotContain(capturePath, instruction => instruction.opcode == OpCodes.Newobj &&
                instruction.operand is ConstructorInfo constructor && !constructor.DeclaringType.IsValueType);
        }
    }

    [Fact]
    public void CapturedTranslationRotationPrecisionAndSasUseFrozenInputsUntilPublication()
    {
        var group = Group();
        SteeringController.KeyForward = SteeringController.KeyPitchU = true;
        SteeringController.PrecisionHeld = true;
        SteeringController.SasKind = SasTargetKind.Prograde;
        var frozen = SteeringController.CaptureStepInput();

        SteeringController.KeyForward = SteeringController.KeyPitchU = false;
        SteeringController.KeyBack = SteeringController.KeyYawL = true;
        SteeringController.PrecisionHeld = false;
        SteeringController.SasKind = SasTargetKind.Retrograde;
        Assert.Equal(DVector3.forward, frozen.GetTranslationWorldDir(group));
        Assert.Equal(DVector3.back, SteeringController.GetTranslationWorldDir(group));
        Assert.Equal(-(double).02f * .25f, frozen.GetRotationWorldAngularVelocity(group).x);
        Assert.Equal(0, frozen.GetRotationWorldAngularVelocity(group).y);
        Assert.Equal(0, SteeringController.GetRotationWorldAngularVelocity(group).x);
        Assert.Equal(-(double).02f, SteeringController.GetRotationWorldAngularVelocity(group).y);
        Assert.Equal(DVector3.right, frozen.GetSasTarget(group));
        Assert.Equal(DVector3.left, SteeringController.GetSasTarget(group));
        float frozenForce = 800, frozenFlow = 20, liveForce = 800, liveFlow = 20;
        frozen.ScaleManualThrust(ref frozenForce, ref frozenFlow);
        SteeringController.ScaleManualThrust(ref liveForce, ref liveFlow);
        Assert.Equal(200, frozenForce); Assert.Equal(5, frozenFlow);
        Assert.Equal(800, liveForce); Assert.Equal(20, liveFlow);

        frozen.ApplyManualRotation(group, .12);
        Assert.Equal(SasTargetKind.None, frozen.SasKind);
        Assert.True(frozen.TakeoverNotice);
        Assert.Equal(SasTargetKind.Retrograde, SteeringController.SasKind);
        Assert.False(SteeringController.TakeoverNotice);
        frozen.Publish();
        Assert.Equal(SasTargetKind.None, SteeringController.SasKind);
        Assert.True(SteeringController.TakeoverNotice);
    }

    [Fact]
    public void AllKeyCombinationsKeepLiveAndFrozenDirectionResultsBitwiseEqual()
    {
        var group = Group();
        for (int mask = 0; mask < 64; mask++)
        {
            SteeringController.KeyForward = SteeringController.KeyRollL = (mask & 1) != 0;
            SteeringController.KeyBack = SteeringController.KeyRollR = (mask & 2) != 0;
            SteeringController.KeyLeft = SteeringController.KeyYawL = (mask & 4) != 0;
            SteeringController.KeyRight = SteeringController.KeyYawR = (mask & 8) != 0;
            SteeringController.KeyUp = SteeringController.KeyPitchU = (mask & 16) != 0;
            SteeringController.KeyDown = SteeringController.KeyPitchD = (mask & 32) != 0;
            foreach (bool precision in new[] { false, true })
            {
                SteeringController.PrecisionHeld = precision;
                var frozen = SteeringController.CaptureStepInput();
                AssertBits(SteeringController.GetTranslationWorldDir(group), frozen.GetTranslationWorldDir(group));
                AssertBits(SteeringController.GetRotationWorldAngularVelocity(group), frozen.GetRotationWorldAngularVelocity(group));
            }
        }
    }

    private static SpaceEntityGroup Group() => new(71) {
        CurrentOrbit = new OrbitState { rotation = DQuaternion.identity, velocitySystem = (SystemVec3)new DVector3(3, 0, 0) }, maxAngularRate = .02f
    };

    private static IEnumerable<CodeInstruction> EntryPointInstructions(MethodBase method, Type[] owners, HashSet<MethodBase> visited)
    {
        if (!visited.Add(method)) yield break;
        foreach (var instruction in PatchProcessor.GetOriginalInstructions(method))
        {
            yield return instruction;
            if (instruction.operand is MethodBase called && owners.Contains(called.DeclaringType))
                foreach (var nested in EntryPointInstructions(called, owners, visited)) yield return nested;
        }
    }

    private static void AssertBits(DVector3 expected, DVector3 actual)
    {
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.x), BitConverter.DoubleToInt64Bits(actual.x));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.y), BitConverter.DoubleToInt64Bits(actual.y));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.z), BitConverter.DoubleToInt64Bits(actual.z));
    }
}
