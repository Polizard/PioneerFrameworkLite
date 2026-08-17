using System;
using System.IO;
using PioneerFramework.PFView;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    public class PFViewOutlineCoreTests
    {
        private static string ShaderPath(string name) =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Shaders", name);

        private static string ReadShader(string name) =>
            File.ReadAllText(ShaderPath(name));

        [Fact]
        public void DefaultSettings_AreEnabledTwoPixelOpaqueBlack()
        {
            var settings = PFViewOutlineSettings.Default;

            Assert.True(settings.Enabled);
            Assert.Equal(2, settings.WidthPixels);
            Assert.True(Math.Abs(settings.Color.r) < 0.0001f);
            Assert.True(Math.Abs(settings.Color.g) < 0.0001f);
            Assert.True(Math.Abs(settings.Color.b) < 0.0001f);
            Assert.True(Math.Abs(settings.Color.a - 1f) < 0.0001f);
        }

        [Fact]
        public void Settings_ClampWidthToOneAndEight()
        {
            var low = new PFViewOutlineSettings(true, -10, Color.black);
            var high = new PFViewOutlineSettings(true, 99, Color.black);

            Assert.Equal(1, low.WidthPixels);
            Assert.Equal(8, high.WidthPixels);
        }

        [Fact]
        public void ShaderFiles_ExistWithRequiredContract()
        {
            foreach (var name in new[]
            {
                "PFView_OutlineMask.shader",
                "PFView_OutlineDilate.shader",
                "PFView_OutlineComposite.shader"
            })
            {
                Assert.True(File.Exists(ShaderPath(name)), $"Missing shader: {name}");
            }

            var mask = ReadShader("PFView_OutlineMask.shader");
            Assert.Contains("Shader \"Hidden/PioneerFramework/PFView/OutlineMask\"", mask);
            Assert.Contains("ZTest LEqual", mask);
            Assert.Contains("ZWrite Off", mask);
            Assert.Contains("ColorMask R", mask);

            var dilate = ReadShader("PFView_OutlineDilate.shader");
            Assert.Contains("Shader \"Hidden/PioneerFramework/PFView/OutlineDilate\"", dilate);
            Assert.Contains("Name \"HORIZONTAL_DISTANCE\"", dilate);
            Assert.Contains("Name \"VERTICAL_DISTANCE\"", dilate);
            Assert.Contains("for (int offset = -radius; offset <= radius; offset++)", dilate);
            Assert.Contains("offset * offset * PFVIEW_DISTANCE_SCALE", dilate);
            Assert.Contains("_PFViewOutlineRadius", dilate);
            Assert.DoesNotContain("for (int offsetY = -8; offsetY <= 8; offsetY++)", dilate);

            var composite = ReadShader("PFView_OutlineComposite.shader");
            Assert.Contains("Shader \"Hidden/PioneerFramework/PFView/OutlineComposite\"", composite);
            Assert.Contains("_PFViewOriginalMask", composite);
            Assert.Contains("saturate(dilated - original)", composite);
            Assert.Contains("radius * radius", composite);
            Assert.Contains("Blend SrcAlpha OneMinusSrcAlpha", composite);
            Assert.Contains("ColorMask RGB", composite);
        }

        [Fact]
        public void FixedShaderProvider_RejectsIncompleteShaderSet()
        {
            var provider = new PFViewFixedOutlineShaderProvider(default(PFViewOutlineShaderSet));

            bool success = provider.TryGetShaderSet(
                out var shaderSet,
                out string failureReason);

            Assert.False(success);
            Assert.False(shaderSet.IsComplete);
            Assert.False(string.IsNullOrWhiteSpace(failureReason));
        }

        [Fact]
        public void Renderer_ReportsMissingShadersForEmptyProvider()
        {
            var provider = new PFViewFixedOutlineShaderProvider(default(PFViewOutlineShaderSet));
            using (var renderer = new PFViewMaskOutlineRenderer(provider))
            {
                Assert.False(renderer.IsReady);
                Assert.Equal(PFViewOutlineStatusCode.MissingShaders, renderer.Status.Code);
            }
        }

        [Fact]
        public void Renderer_DisabledShortCircuitsBeforeFrameValidation()
        {
            var provider = new PFViewFixedOutlineShaderProvider(default(PFViewOutlineShaderSet));
            using (var renderer = new PFViewMaskOutlineRenderer(provider))
            {
                renderer.BeginFrame(null, null, new PFViewOutlineSettings(false, 2, Color.black));
                Assert.Equal(PFViewOutlineStatusCode.Disabled, renderer.Status.Code);
            }
        }

        [Fact]
        public void Renderer_NullFrameReportsInvalidFrame()
        {
            var provider = new PFViewFixedOutlineShaderProvider(default(PFViewOutlineShaderSet));
            using (var renderer = new PFViewMaskOutlineRenderer(provider))
            {
                renderer.BeginFrame(null, null, PFViewOutlineSettings.Default);
                Assert.Equal(PFViewOutlineStatusCode.InvalidFrame, renderer.Status.Code);
                Assert.False(renderer.Composite());
            }
        }

        [Fact]
        public void Renderer_DisposeIsIdempotentAndReportsDisposed()
        {
            var provider = new PFViewFixedOutlineShaderProvider(default(PFViewOutlineShaderSet));
            var renderer = new PFViewMaskOutlineRenderer(provider);
            renderer.Dispose();
            renderer.Dispose();
            Assert.Equal(PFViewOutlineStatusCode.Disposed, renderer.Status.Code);
        }
    }
}
