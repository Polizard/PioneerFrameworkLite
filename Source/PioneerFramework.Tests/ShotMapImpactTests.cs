using System;
using System.Reflection;
using PioneerFramework.Combat;
using UnityEngine;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    public class ShotMapImpactTests
    {
        [Theory]
        [InlineData(11, 10.5f, 10, 10)]
        [InlineData(10.5f, 11, 10, 10)]
        [InlineData(10, 10.5f, 10, 10)]
        [InlineData(10.5f, 10, 10, 10)]
        public void ContactOnEitherBoundaryUsesTheStruckStructuresCell(float x, float z, int expectedX, int expectedZ)
        {
            var method = typeof(CombatManager).GetMethod("MapImpactCell", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var cell = (IntVec3)method.Invoke(null, new object[] { new CellRect(10, 10, 1, 1), new Vector3(x, 0, z) });
            Assert.Equal(new IntVec3(expectedX, 0, expectedZ), cell);
        }
    }
}
