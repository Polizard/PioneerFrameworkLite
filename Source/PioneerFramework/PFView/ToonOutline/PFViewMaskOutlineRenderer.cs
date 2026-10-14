using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace PioneerFramework.PFView
{
    /// <summary>
    /// Builds a depth-tested union mask for submitted ship meshes, expands it
    /// in destination-pixel space, and composites only the resulting outer
    /// ring. The renderer does not modify PFView camera controls or body
    /// materials.
    /// </summary>
    /// <remarks>
    /// Keep one instance for the lifetime of the owning view. For each frame,
    /// call <see cref="BeginFrame"/>, submit the same mesh/matrix pairs used by
    /// the visible ship draws, invoke Camera.Render, then call
    /// <see cref="Composite"/> before drawing the RenderTexture in IMGUI.
    /// Keep the Camera transform and projection stable from BeginFrame through
    /// Camera.Render so the mask can reuse the rendered depth buffer.
    /// Call <see cref="Dispose"/> once when the owning view releases its
    /// resources. See <see cref="IPFViewOutlineRenderer"/> for a complete code
    /// example.
    /// </remarks>
    public sealed class PFViewMaskOutlineRenderer : IPFViewOutlineRenderer
    {
        private const string CommandBufferName =
            "PFView Silhouette Outline";

        private readonly IPFViewOutlineShaderProvider shaderProvider;
        private readonly List<DrawCommand> drawCommands =
            new List<DrawCommand>();

        private Camera camera;
        private RenderTexture destination;
        private PFViewOutlineSettings settings;
        private Matrix4x4 viewMatrix;
        private Matrix4x4 projectionMatrix;

        private CommandBuffer commandBuffer;
        private Material maskMaterial;
        private Material dilateMaterial;
        private Material compositeMaterial;
        private PFViewOutlineShaderSet shaderSet;

        private int maskId;
        private int horizontalDistanceId;
        private int dilatedId;
        private int radiusId;
        private int originalMaskId;
        private int outlineColorId;
        private bool propertyIdsInitialized;

        private bool frameActive;
        private bool shadersResolved;
        private bool gpuResourcesCreated;
        private bool disposed;
        private PFViewOutlineStatus status;

        /// <summary>
        /// Creates an inactive renderer and immediately validates the supplied
        /// shader provider. GPU resources are only created for a complete,
        /// supported shader set.
        /// </summary>
        /// <param name="shaderProvider">
        /// Provider for shaders compiled into an AssetBundle for the active
        /// RimWorld Unity version.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="shaderProvider"/> is null.
        /// </exception>
        public PFViewMaskOutlineRenderer(
            IPFViewOutlineShaderProvider shaderProvider)
        {
            this.shaderProvider = shaderProvider ??
                throw new ArgumentNullException(nameof(shaderProvider));
            status = new PFViewOutlineStatus(
                PFViewOutlineStatusCode.Uninitialized);
            TryResolveShaderSet();
        }

        /// <inheritdoc />
        public bool IsReady =>
            !disposed &&
            shadersResolved &&
            gpuResourcesCreated;

        /// <inheritdoc />
        public PFViewOutlineStatus Status => status;

        /// <inheritdoc />
        public void BeginFrame(
            Camera camera,
            RenderTexture destination,
            PFViewOutlineSettings settings)
        {
            ResetFrameReferences();

            if (disposed)
            {
                SetStatus(
                    PFViewOutlineStatusCode.Disposed,
                    "The PFView outline renderer has been disposed.");
                return;
            }

            this.settings = settings;
            if (!settings.Enabled)
            {
                SetStatus(PFViewOutlineStatusCode.Disabled);
                return;
            }

            if (ReferenceEquals(camera, null) ||
                ReferenceEquals(destination, null))
            {
                SetStatus(
                    PFViewOutlineStatusCode.InvalidFrame,
                    "A Camera targeting a depth-enabled RenderTexture is required.");
                return;
            }

            if (!shadersResolved && !TryResolveShaderSet())
            {
                return;
            }

            BeginUnityFrame(camera, destination);
        }

        /// <inheritdoc />
        public void Submit(
            Mesh mesh,
            Matrix4x4 matrix,
            int subMeshIndex = 0)
        {
            if (!frameActive ||
                mesh == null ||
                subMeshIndex < 0 ||
                subMeshIndex >= mesh.subMeshCount)
            {
                return;
            }

            drawCommands.Add(
                new DrawCommand(mesh, matrix, subMeshIndex));
        }

        /// <inheritdoc />
        public bool Composite()
        {
            if (disposed)
            {
                SetStatus(
                    PFViewOutlineStatusCode.Disposed,
                    "The PFView outline renderer has been disposed.");
                return false;
            }

            if (!frameActive)
            {
                return false;
            }

            if (drawCommands.Count == 0)
            {
                SetStatus(
                    PFViewOutlineStatusCode.NoGeometry,
                    "No valid ship meshes were submitted for this frame.");
                ResetFrameReferences();
                return false;
            }

            return CompositeUnityFrame();
        }

        /// <inheritdoc />
        public void CancelFrame()
        {
            if (disposed)
            {
                SetStatus(PFViewOutlineStatusCode.Disposed);
                return;
            }

            PFViewOutlineStatus previousStatus = status;
            ResetFrameReferences();
            if (IsReady)
            {
                SetStatus(PFViewOutlineStatusCode.Ready);
            }
            else if (previousStatus.Code ==
                     PFViewOutlineStatusCode.MissingShaders ||
                     previousStatus.Code ==
                     PFViewOutlineStatusCode.UnsupportedShaders)
            {
                status = previousStatus;
            }
            else
            {
                SetStatus(PFViewOutlineStatusCode.Uninitialized);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            ResetFrameReferences();
            if (gpuResourcesCreated)
            {
                ReleaseOwnedGpuResources();
            }
            SetStatus(PFViewOutlineStatusCode.Disposed);
        }

        private bool TryResolveShaderSet()
        {
            if (shadersResolved)
            {
                return true;
            }

            if (disposed)
            {
                SetStatus(PFViewOutlineStatusCode.Disposed);
                return false;
            }

            string failureReason;
            try
            {
                if (!shaderProvider.TryGetShaderSet(
                        out PFViewOutlineShaderSet shaders,
                        out failureReason))
                {
                    SetStatus(
                        PFViewOutlineStatusCode.MissingShaders,
                        failureReason);
                    return false;
                }

                shaderSet = shaders;
            }
            catch (Exception exception)
            {
                SetStatus(
                    PFViewOutlineStatusCode.MissingShaders,
                    "The PFView outline shader provider failed: " +
                    exception.Message);
                return false;
            }

            if (!shaderSet.IsComplete)
            {
                SetStatus(
                    PFViewOutlineStatusCode.MissingShaders,
                    "Mask, dilate, and composite shaders are required.");
                return false;
            }

            if (typeof(CommandBuffer).Assembly.GetName().Name == "UnityStubs")
            {
                SetStatus(
                    PFViewOutlineStatusCode.UnsupportedShaders,
                    "RimWorld 1.7 UnityStubs CommandBuffer is not implemented. The reserved outline renderer requires a Waylith backend.");
                return false;
            }

            shadersResolved = true;
            SetStatus(PFViewOutlineStatusCode.Uninitialized);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void BeginUnityFrame(
            Camera frameCamera,
            RenderTexture frameDestination)
        {
            if (frameDestination.width < 1 ||
                frameDestination.height < 1 ||
                frameDestination.depth < 1 ||
                frameCamera.targetTexture != frameDestination)
            {
                SetStatus(
                    PFViewOutlineStatusCode.InvalidFrame,
                    "A Camera targeting a depth-enabled RenderTexture is required.");
                return;
            }

            if (!TryEnsureGpuResources())
            {
                return;
            }

            camera = frameCamera;
            destination = frameDestination;
            viewMatrix = frameCamera.worldToCameraMatrix;
            projectionMatrix =
                PFViewOutlineProjection.GetForCommandBuffer(frameCamera);
            frameActive = true;
            SetStatus(PFViewOutlineStatusCode.Ready);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool TryEnsureGpuResources()
        {
            if (gpuResourcesCreated)
            {
                return true;
            }

            if (!shadersResolved)
            {
                SetStatus(
                    PFViewOutlineStatusCode.MissingShaders,
                    "Mask, dilate, and composite shaders are required.");
                return false;
            }

            if (shaderSet.MaskShader == null ||
                shaderSet.DilateShader == null ||
                shaderSet.CompositeShader == null)
            {
                shadersResolved = false;
                SetStatus(
                    PFViewOutlineStatusCode.MissingShaders,
                    "Mask, dilate, and composite shaders are required.");
                return false;
            }

            if (!shaderSet.MaskShader.isSupported ||
                !shaderSet.DilateShader.isSupported ||
                !shaderSet.CompositeShader.isSupported)
            {
                SetStatus(
                    PFViewOutlineStatusCode.UnsupportedShaders,
                    "One or more PFView outline shaders are unsupported.");
                return false;
            }

            try
            {
                maskMaterial = CreateOwnedMaterial(shaderSet.MaskShader);
                dilateMaterial = CreateOwnedMaterial(
                    shaderSet.DilateShader);
                compositeMaterial =
                    CreateOwnedMaterial(shaderSet.CompositeShader);
                commandBuffer = new CommandBuffer
                {
                    name = CommandBufferName
                };
                InitializePropertyIds();
                gpuResourcesCreated = true;
            }
            catch (Exception exception)
            {
                ReleaseOwnedGpuResources();
                SetStatus(
                    PFViewOutlineStatusCode.UnsupportedShaders,
                    "PFView outline resources could not be created: " +
                    exception.Message);
                return false;
            }

            SetStatus(PFViewOutlineStatusCode.Ready);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool CompositeUnityFrame()
        {
            try
            {
                RecordCommands();
                Graphics.ExecuteCommandBuffer(commandBuffer);
                SetStatus(PFViewOutlineStatusCode.Rendered);
                return true;
            }
            catch (Exception exception)
            {
                SetStatus(
                    PFViewOutlineStatusCode.InvalidFrame,
                    "PFView outline composition failed: " +
                    exception.Message);
                return false;
            }
            finally
            {
                commandBuffer.Clear();
                ResetFrameReferences();
            }
        }

        private void RecordCommands()
        {
#if NET10_0_OR_GREATER
            // Keep the original Unity recording path for reference; never report a
            // successful composite through Waylith's no-op CommandBuffer methods.
            throw new NotSupportedException("UnityStubs CommandBuffer cannot execute the reserved outline pass.");
#else
            RenderTextureFormat maskFormat =
                SystemInfo.SupportsRenderTextureFormat(
                    RenderTextureFormat.R8)
                    ? RenderTextureFormat.R8
                    : RenderTextureFormat.ARGB32;

            commandBuffer.Clear();
            commandBuffer.SetViewProjectionMatrices(
                viewMatrix,
                projectionMatrix);

            commandBuffer.GetTemporaryRT(
                maskId,
                destination.width,
                destination.height,
                0,
                FilterMode.Point,
                maskFormat);
            commandBuffer.GetTemporaryRT(
                horizontalDistanceId,
                destination.width,
                destination.height,
                0,
                FilterMode.Point,
                maskFormat);
            commandBuffer.GetTemporaryRT(
                dilatedId,
                destination.width,
                destination.height,
                0,
                FilterMode.Point,
                maskFormat);

            commandBuffer.SetRenderTarget(
                new RenderTargetIdentifier(maskId),
                new RenderTargetIdentifier(destination));
            commandBuffer.ClearRenderTarget(
                false,
                true,
                Color.clear);

            foreach (DrawCommand drawCommand in drawCommands)
            {
                commandBuffer.DrawMesh(
                    drawCommand.Mesh,
                    drawCommand.Matrix,
                    maskMaterial,
                    drawCommand.SubMeshIndex,
                    0);
            }

            commandBuffer.SetGlobalInt(
                radiusId,
                settings.WidthPixels);
            commandBuffer.Blit(
                new RenderTargetIdentifier(maskId),
                new RenderTargetIdentifier(horizontalDistanceId),
                dilateMaterial,
                0);
            commandBuffer.Blit(
                new RenderTargetIdentifier(horizontalDistanceId),
                new RenderTargetIdentifier(dilatedId),
                dilateMaterial,
                1);
            commandBuffer.SetGlobalTexture(
                originalMaskId,
                new RenderTargetIdentifier(maskId));
            commandBuffer.SetGlobalColor(
                outlineColorId,
                settings.Color);
            commandBuffer.Blit(
                new RenderTargetIdentifier(dilatedId),
                new RenderTargetIdentifier(destination),
                compositeMaterial,
                0);

            commandBuffer.ReleaseTemporaryRT(dilatedId);
            commandBuffer.ReleaseTemporaryRT(horizontalDistanceId);
            commandBuffer.ReleaseTemporaryRT(maskId);
#endif
        }

        private void InitializePropertyIds()
        {
            if (propertyIdsInitialized)
            {
                return;
            }

            maskId = Shader.PropertyToID("_PFViewOutlineMask");
            horizontalDistanceId = Shader.PropertyToID(
                "_PFViewOutlineHorizontalDistance");
            dilatedId = Shader.PropertyToID("_PFViewOutlineDilated");
            radiusId = Shader.PropertyToID("_PFViewOutlineRadius");
            originalMaskId = Shader.PropertyToID(
                "_PFViewOriginalMask");
            outlineColorId = Shader.PropertyToID(
                "_PFViewOutlineColor");
            propertyIdsInitialized = true;
        }

        private void ResetFrameReferences()
        {
            frameActive = false;
            drawCommands.Clear();
            camera = null;
            destination = null;
            viewMatrix = default;
            projectionMatrix = default;
        }

        private void ReleaseOwnedGpuResources()
        {
            if (commandBuffer != null)
            {
                commandBuffer.Release();
                commandBuffer = null;
            }

            DestroyOwnedMaterial(ref maskMaterial);
            DestroyOwnedMaterial(ref dilateMaterial);
            DestroyOwnedMaterial(ref compositeMaterial);
            gpuResourcesCreated = false;
        }

        private static Material CreateOwnedMaterial(Shader shader)
        {
            return new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        private static void DestroyOwnedMaterial(ref Material material)
        {
            if (material == null)
            {
                return;
            }

            UnityEngine.Object.Destroy(material);
            material = null;
        }

        private void SetStatus(
            PFViewOutlineStatusCode code,
            string message = null)
        {
            status = new PFViewOutlineStatus(code, message);
        }

        private readonly struct DrawCommand
        {
            public Mesh Mesh { get; }
            public Matrix4x4 Matrix { get; }
            public int SubMeshIndex { get; }

            public DrawCommand(
                Mesh mesh,
                Matrix4x4 matrix,
                int subMeshIndex)
            {
                Mesh = mesh;
                Matrix = matrix;
                SubMeshIndex = subMeshIndex;
            }
        }
    }
}
