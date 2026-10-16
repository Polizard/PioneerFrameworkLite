using System;
using System.Runtime.CompilerServices;
using PioneerFramework.Combat;
using PioneerFramework.PFView;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    public class WaylithCompatibilityTests
    {
        [Theory]
        [InlineData(8f, 10f, 14f)]
        [InlineData(2f, 14f, 6f)]
        [InlineData(2f, 4f, -8f)]
        public void OrbitLineMesh_MapsItsEndpointsToTheOriginal3DPositions(float x, float y, float z)
        {
            var a = new Vector3(2f, 4f, 6f);
            var b = new Vector3(x, y, z);
            Assert.True(PFViewOrbitLineGeometry.TryBuildMatrix(a, b, 0.2f, out Matrix4x4 matrix));
            Assert.InRange(Vector3.Distance(a, matrix.MultiplyPoint3x4(new Vector3(0f, 0f, -0.5f))), 0f, 1e-4f);
            Assert.InRange(Vector3.Distance(b, matrix.MultiplyPoint3x4(new Vector3(0f, 0f, 0.5f))), 0f, 1e-4f);
            Assert.False(PFViewOrbitLineGeometry.TryBuildMatrix(a, a, 0.2f, out _));
        }

        [Theory]
        [InlineData(90f)]
        [InlineData(0.01f)]
        public void TurretRotation_AdvancesByBoundedStepAndDoesNotOvershoot(float targetDegrees)
        {
            var weapon = new WeaponInstance
            {
                def = new WeaponDef { turret = true, turretRotationSpeed = 60f },
                targetRotation = Quaternion.AngleAxis(targetDegrees, Vector3.up)
            };
            float step = weapon.def.turretRotationSpeed * 0.01745f;
            float before = Quaternion.Angle(weapon.turretRotation, weapon.targetRotation);
            weapon.Update(0);
            float after = Quaternion.Angle(weapon.turretRotation, weapon.targetRotation);
            Assert.InRange(after, 0f, Math.Max(0f, before - step) + 0.02f);
            Assert.InRange(Quaternion.Angle(Quaternion.identity, weapon.turretRotation), 0f, step + 0.02f);
        }

        [Fact]
        public void ReservedOutline_ReportsUnsupportedBackendWithoutAllocatingGpuResources()
        {
            var shader = (Shader)RuntimeHelpers.GetUninitializedObject(typeof(Shader));
            var provider = new PFViewFixedOutlineShaderProvider(new PFViewOutlineShaderSet(shader, shader, shader));
            using var renderer = new PFViewMaskOutlineRenderer(provider);
            Assert.False(renderer.IsReady);
            Assert.Equal(PFViewOutlineStatusCode.UnsupportedShaders, renderer.Status.Code);
            Assert.Contains("CommandBuffer", renderer.Status.Message);
            Assert.False(renderer.Composite());
        }
    }
}
