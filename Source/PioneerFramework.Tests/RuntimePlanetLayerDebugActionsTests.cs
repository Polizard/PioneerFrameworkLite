using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PioneerFramework;
using RimWorld;
using RimWorld.Planet;
using Xunit;

namespace PioneerFramework.Tests
{
    public class RuntimePlanetLayerDebugActionsTests
    {
        [Theory]
        [InlineData("PF_DebugPlanetLayer", "Surface", false, true)]
        [InlineData("Surface", "PF_DebugPlanetLayer", false, true)]
        [InlineData("Moon", "PF_DebugPlanetLayer", false, true)]
        [InlineData("PF_DebugPlanetLayer", "PF_DebugPlanetLayer", true, false)]
        [InlineData("PF_DebugPlanetLayer", "PF_DebugPlanetLayer", false, true)]
        [InlineData("Surface", "Moon", false, false)]
        [InlineData("Moon", "Surface", false, false)]
        [InlineData("PF_DebugPlanetLayer", null, false, true)]
        [InlineData("Surface", null, false, false)]
        public void TestLayerVisibility_IsIsolatedWithoutChangingNormalLayers(
            string candidateDefName, string selectedDefName, bool sameInstance, bool expectedHidden)
        {
            // 隐藏测试层的条件或对其他地表的过滤失效时，这些用例会失败。
            var type = typeof(RuntimePlanetLayerService).Assembly.GetType(
                "PioneerFramework.RuntimePlanetLayerDebugActions");
            Assert.NotNull(type);
            var method = type.GetMethod("ShouldHideLayer", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            PlanetLayer candidate = MakeLayer(candidateDefName);
            PlanetLayer selected = sameInstance ? candidate : MakeLayer(selectedDefName);

            bool hidden = (bool)method.Invoke(null, new object[] { candidate, selected });

            Assert.Equal(expectedHidden, hidden);
        }

        private static PlanetLayer MakeLayer(string defName)
        {
            if (defName == null) return null;
            // 不运行依赖游戏原生图形环境的地表构造函数；使用实际 PlanetLayer/Def 属性。
            var layer = (PlanetLayer)RuntimeHelpers.GetUninitializedObject(typeof(SurfaceLayer));
            AccessTools.Field(typeof(PlanetLayer), "def").SetValue(
                layer, new PlanetLayerDef { defName = defName });
            return layer;
        }
    }
}
