using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld.Planet;
using UnityEngine;
using UnityStubs.Rendering;
using Verse;
using Waylith;
using Waylith.Rendering;
using Material = UnityEngine.Material;
using RenderTexture = UnityEngine.RenderTexture;
using Shader = UnityEngine.Shader;
using Texture2D = UnityEngine.Texture2D;

namespace PioneerFramework
{
    internal static class PFViewStellarPipelineWarmup
    {
        internal static void Run(Mesh mesh, int sceneLayer, int starLayer)
        {
            if (EngineApplication.Instance is not { } engine ||
                CameraTextureRenderer.Current is not { } baker)
                return;

            Material? surface = null;
            Material? halo = null;
            try
            {
                MainTabWindow_PFView.SelectStellarShaders(out var surfaceShader, out var haloShader, out bool fast, out _);
                if (surfaceShader == null || haloShader == null) return;
                surface = new Material(surfaceShader) { renderQueue = PFViewLayers.PlanetAndShips };
                halo = new Material(haloShader) { renderQueue = PFViewLayers.StellarHalo };
                PFViewStellarHeatCache? heat = null;
                var eye = new Vector3(0f, 0f, -3f);
                MainTabWindow_PFView.ApplyStellarVisual(surface, halo, eye, Vector3.zero, 1f, Quaternion.identity, 0, fast, false, ref heat);

                // ExecuteWhenFinished rejoins the main thread after the loader. Drain submitted
                // frames before touching either native renderer's caches from this callback.
                engine.WaitForRenderThreadIdle();
                try { PrewarmStellarOffscreen(baker, mesh, surface, halo, eye, sceneLayer, starLayer); }
                catch (Exception error) { Log.Warning("[PFL Stellar Warmup] PFView: " + error.GetBaseException().Message); }

                try { PrewarmStellarWorld(engine, mesh, surface, halo); }
                catch (Exception error) { Log.Warning("[PFL Stellar Warmup] World: " + error.GetBaseException().Message); }
            }
            catch (Exception error)
            {
                Log.Warning("[PFL Stellar Warmup] " + error.GetBaseException().Message);
            }
            finally
            {
                if (surface != null) UnityEngine.Object.Destroy(surface);
                if (halo != null) UnityEngine.Object.Destroy(halo);
            }
        }

        private static void PrewarmStellarOffscreen(ICameraTextureRenderer baker, Mesh mesh,
            Material surface, Material halo, Vector3 eye, int sceneLayer, int starLayer)
        {
            if (!PFViewStellarProbe.SurfacePass && !PFViewStellarProbe.HaloPass) return;
            var target = new RenderTexture(8, 8, 24);
            var cameraObject = new GameObject("PFL Stellar Warmup");
            Material? compositeSurface = null;
            Material? compositeHalo = null;
            try
            {
                cameraObject.SetActive(false);
                var camera = cameraObject.AddComponent<Camera>();
                camera.targetTexture = target;
                camera.transform.position = eye;
                camera.fieldOfView = 45f;
                camera.aspect = 1f;
                var view = CameraViewFactory.GetView(camera);

                // CameraTextureBaker reuses the render pass for all RTs of this format.
                // Submit only these commands to the baker, outside the shared frame draw list.
                var surfaceCompositeShader = PFViewStellarProbe.S3BEnabled ? Shader.Find("PF_StellarSurfaceComposite") : null;
                var haloCompositeShader = PFViewStellarProbe.S3BEnabled ? Shader.Find("PF_StellarHaloComposite") : null;
                bool composite = surfaceCompositeShader != null && haloCompositeShader != null;
                baker.Render(target, StellarWarmupCommands(mesh, surface, halo, composite ? starLayer : sceneLayer),
                    view, camera.rect, Color.clear);
                if (composite)
                {
                    compositeSurface = new Material(surfaceCompositeShader) { renderQueue = PFViewLayers.PlanetAndShips };
                    compositeHalo = new Material(haloCompositeShader) { renderQueue = PFViewLayers.StellarHalo };
                    // A different texture supplies the descriptor while the same RT is being written.
                    compositeSurface.SetTexture("_StellarStarTex", Texture2D.whiteTexture);
                    compositeHalo.SetTexture("_StellarStarTex", Texture2D.whiteTexture);
                    baker.Render(target, StellarWarmupCommands(mesh, compositeSurface, compositeHalo, sceneLayer),
                        view, camera.rect, Color.clear);
                }
            }
            finally
            {
                target.Release();
                UnityEngine.Object.Destroy(target);
                UnityEngine.Object.Destroy(cameraObject);
                if (compositeSurface != null) UnityEngine.Object.Destroy(compositeSurface);
                if (compositeHalo != null) UnityEngine.Object.Destroy(compositeHalo);
            }
        }

        private static List<MeshDrawCommand> StellarWarmupCommands(Mesh sphere, Material surface, Material halo, int layer)
        {
            var mesh = sphere.EngineMesh;
            byte passId = LayerPassRouter.Resolve(layer);
            var matrix = System.Numerics.Matrix4x4.Identity;
            var draws = new List<MeshDrawCommand>(2);
            if (PFViewStellarProbe.SurfacePass)
                draws.Add(new MeshDrawCommand(mesh, surface.engineMaterial, default, matrix, 0, passId, layer: layer));
            if (PFViewStellarProbe.HaloPass)
                draws.Add(new MeshDrawCommand(mesh, halo.engineMaterial, default, matrix, 0, passId, layer: layer));
            return draws;
        }

        private static void PrewarmStellarWorld(EngineApplication engine, Mesh mesh, Material surface, Material halo)
        {
            var world = engine.FrameRenderer.Features.FirstOrDefault(
                feature => feature.GetType().FullName == "RimWorldWaylith.Rendering.WorldRenderFeature");
            if (world == null || Find.WorldCamera is not { } camera) return;
            var pass = world.Passes[0];
            var skybox = WorldCameraManager.WorldSkyboxCamera;
            bool skyboxBeforeWorld = skybox != null && skybox.enabled && skybox.gameObject.activeSelf &&
                skybox.targetTexture == null && skybox.depth < camera.depth;
            bool hdr = camera.allowHDR || (skyboxBeforeWorld && skybox!.allowHDR);

            // The stock skybox precedes the world camera. Each slot owns a separate renderer and
            // later slots use the load attachment. Populate that cache without drawing to the screen.
            var renderers = (IReadOnlyList<MeshBatchRenderer>)ReadProperty(pass, "Renderers");
            var renderer = renderers[skyboxBeforeWorld ? 1 : 0];
            var targets = ReadField(pass, hdr ? "hdrTargets" : "sceneTargets");
            var attachment = ReadProperty(targets, skyboxBeforeWorld ? "LoadAttachment" : "Attachment");
            var renderPass = ReadProperty(attachment, "RenderPass");
            var sampleCount = ReadProperty(attachment, "SampleCount");
            var cache = ReadField(renderer, "pipelines");
            var frameLayout = ReadField(renderer, "frameDescriptorSetLayout");
            var textureLayout = ReadField(renderer, "globalTextureDescriptorSetLayout");
            var create = typeof(MeshPipelineCache).GetMethod("GetOrCreatePipeline")
                ?? throw new MissingMethodException(typeof(MeshPipelineCache).FullName, "GetOrCreatePipeline");
            foreach (var material in new[] { surface, halo })
            {
                var command = new MeshDrawCommand(mesh.EngineMesh, material.engineMaterial, default,
                    System.Numerics.Matrix4x4.Identity, 0, pass.PassId, layer: WorldCameraManager.WorldLayer);
                var draw = MeshDrawSubmission.TryMakeDrawItem(in command, Shader.GlobalKeywordKey)
                    ?? throw new InvalidOperationException("The world stellar draw could not be resolved.");
                try
                {
                    // The tracked game references omit raw Vulkan structs. Use the engine's
                    // descriptor layouts and attachment handles without unpacking them.
                    var pipeline = create.Invoke(cache, new object?[] { frameLayout, textureLayout,
                        draw.Binding, draw.Mesh.Layout, draw.Mesh.Topology, draw.Binding.PipelineState,
                        renderPass, sampleCount, 1, draw.PassId, "PFL stellar warmup", false });
                    if (pipeline == null || pipeline.Equals(Activator.CreateInstance(create.ReturnType)))
                        throw new InvalidOperationException("The world stellar pipeline could not be created.");
                }
                finally { FinalDrawItem.Return(draw); }
            }
        }

        private static object ReadField(object owner, string name) =>
            owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
                ?? throw new MissingFieldException(owner.GetType().FullName, name);

        private static object ReadProperty(object owner, string name) =>
            owner.GetType().GetProperty(name)?.GetValue(owner)
                ?? throw new MissingMemberException(owner.GetType().FullName, name);
    }
}
