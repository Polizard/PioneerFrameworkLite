using PioneerFramework;
using PioneerFramework.ShipSkin;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    public class ShipSkinFrameTests
    {
        // Exercise the material payload without constructing a GPU-backed MaterialPropertyBlock.
        private static (Vector4 origin, Quaternion inverseRotation) Frame(
            Vector3 origin, Quaternion rotation, float metersToView = 0.1f, float scale = 1f)
        {
            ShipSkinFrame.Build(origin, rotation, metersToView, scale, out Vector4 packed, out Vector4 q);
            return (packed, new Quaternion(q.x, q.y, q.z, q.w));
        }

        private static Vector3 Project(Vector3 point, (Vector4 origin, Quaternion inverseRotation) frame)
            => (frame.inverseRotation * (point - new Vector3(frame.origin.x, frame.origin.y, frame.origin.z)))
                * frame.origin.w;

        private static void EqualPoint(float x, float y, float z, Vector3 actual)
        {
            Assert.Equal(x, actual.x, 4);
            Assert.Equal(y, actual.y, 4);
            Assert.Equal(z, actual.z, 4);
        }

        [Fact]
        public void TranslationAndFloatingOriginDoNotMoveTheSkin()
        {
            var a = Frame(new Vector3(100f, -20f, 50f), Quaternion.identity);
            var b = Frame(new Vector3(-40f, 80f, -200f), Quaternion.identity);
            EqualPoint(20f, 30f, 40f, Project(new Vector3(102f, -17f, 54f), a));
            EqualPoint(20f, 30f, 40f, Project(new Vector3(-38f, 83f, -196f), b));
        }

        [Fact]
        public void ShipRotationKeepsTheSamePhysicalPlateCoordinates()
        {
            var frame = Frame(new Vector3(10f, 20f, 30f), new Quaternion(0f, 1f, 0f, 0f));
            EqualPoint(20f, 30f, 40f, Project(new Vector3(8f, 23f, 26f), frame));
        }

        [Fact]
        public void QuarterTurnUsesTheInverseRotation()
        {
            const float s = 0.70710677f;
            var frame = Frame(Vector3.zero, new Quaternion(0f, s, 0f, s));
            EqualPoint(20f, 0f, 0f, Project(new Vector3(0f, 0f, -2f), frame));
        }

        [Fact]
        public void DebugEnlargementDoesNotChangePlateDensityOnTheHull()
        {
            var frame = Frame(new Vector3(10f, 20f, 30f), Quaternion.identity, scale: 200f);
            EqualPoint(20f, 30f, 40f, Project(new Vector3(410f, 620f, 830f), frame));
        }

        [Fact]
        public void BatchedLocalNormalsCanBeRestoredToTheViewFrame()
        {
            const float s = 0.70710677f;
            var frame = Frame(Vector3.zero, new Quaternion(0f, s, 0f, s));
            Quaternion inverse = frame.inverseRotation;
            var forward = new Quaternion(-inverse.x, -inverse.y, -inverse.z, inverse.w);
            EqualPoint(0f, 0f, -1f, forward * new Vector3(1f, 0f, 0f));
        }

        [Fact]
        public void NonUnitQuaternionIsNormalizedForProjection()
        {
            var frame = Frame(Vector3.zero, new Quaternion(0f, 2f, 0f, 0f));
            EqualPoint(20f, 0f, 0f, Project(new Vector3(-2f, 0f, 0f), frame));
        }

        [Theory]
        [InlineData(0f, 1f)]
        [InlineData(0.1f, 0f)]
        [InlineData(float.NaN, float.PositiveInfinity)]
        public void InvalidScaleAndZeroRotationHaveFiniteDefaults(float metersToView, float scale)
        {
            var frame = Frame(Vector3.zero, new Quaternion(0f, 0f, 0f, 0f), metersToView, scale);
            Assert.True(float.IsFinite(frame.origin.w));
            Assert.True(frame.origin.w > 0f);
            EqualPoint(0f, 0f, 0f, Project(Vector3.zero, frame));
            Assert.Equal(1f, frame.inverseRotation.w);
        }
    }
}
