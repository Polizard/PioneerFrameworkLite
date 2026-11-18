using System;
using UnityEngine;

namespace PioneerFramework.PFView
{
    /// <summary>
    /// Describes the current lifecycle state of a PFView outline renderer.
    /// </summary>
    public enum PFViewOutlineStatusCode
    {
        /// <summary>The renderer has not started a frame.</summary>
        Uninitialized,

        /// <summary>The renderer has valid shaders and can start a frame.</summary>
        Ready,

        /// <summary>Outline rendering was disabled by the current settings.</summary>
        Disabled,

        /// <summary>The current Camera or destination RenderTexture is invalid.</summary>
        InvalidFrame,

        /// <summary>The shader provider could not supply all required shaders.</summary>
        MissingShaders,

        /// <summary>At least one supplied shader is unsupported on this device.</summary>
        UnsupportedShaders,

        /// <summary>No valid ship meshes were submitted for the current frame.</summary>
        NoGeometry,

        /// <summary>The current frame outline was composited successfully.</summary>
        Rendered,

        /// <summary>The renderer has released its owned resources.</summary>
        Disposed
    }

    /// <summary>
    /// Provides a status code and an optional diagnostic message without
    /// requiring the renderer to write a log entry every frame.
    /// </summary>
    public readonly struct PFViewOutlineStatus
    {
        /// <summary>Gets the machine-readable lifecycle state.</summary>
        public PFViewOutlineStatusCode Code { get; }

        /// <summary>Gets a human-readable diagnostic message, if available.</summary>
        public string Message { get; }

        /// <summary>
        /// Creates a status value.
        /// </summary>
        /// <param name="code">Lifecycle state.</param>
        /// <param name="message">Optional diagnostic detail.</param>
        public PFViewOutlineStatus(
            PFViewOutlineStatusCode code,
            string message = null)
        {
            Code = code;
            Message = message ?? string.Empty;
        }
    }

    /// <summary>
    /// Immutable per-frame options for a PFView silhouette outline.
    /// Width is measured in destination RenderTexture pixels.
    /// </summary>
    public readonly struct PFViewOutlineSettings
    {
        /// <summary>Smallest supported outline radius, in pixels.</summary>
        public const int MinWidthPixels = 1;

        /// <summary>Largest supported outline radius, in pixels.</summary>
        public const int MaxWidthPixels = 8;

        /// <summary>Gets whether the current frame should receive an outline.</summary>
        public bool Enabled { get; }

        /// <summary>Gets the clamped outline radius in destination pixels.</summary>
        public int WidthPixels { get; }

        /// <summary>Gets the outline color; its alpha controls opacity.</summary>
        public Color Color { get; }

        /// <summary>
        /// Gets enabled, opaque-black, two-pixel outline settings.
        /// </summary>
        public static PFViewOutlineSettings Default =>
            new PFViewOutlineSettings(true, 2, Color.black);

        /// <summary>
        /// Creates per-frame outline settings and clamps the requested width
        /// to the supported one-to-eight-pixel range.
        /// </summary>
        /// <param name="enabled">Whether outline rendering is enabled.</param>
        /// <param name="widthPixels">Requested destination-pixel radius.</param>
        /// <param name="color">Outline RGB color and opacity.</param>
        public PFViewOutlineSettings(
            bool enabled,
            int widthPixels,
            Color color)
        {
            Enabled = enabled;
            WidthPixels = Math.Max(
                MinWidthPixels,
                Math.Min(MaxWidthPixels, widthPixels));
            Color = color;
        }
    }

    /// <summary>
    /// Groups the three compiled shaders required by the mask outline core.
    /// The provider retains ownership of these Shader objects.
    /// </summary>
    public readonly struct PFViewOutlineShaderSet
    {
        /// <summary>Gets the visible-geometry mask shader.</summary>
        public Shader MaskShader { get; }

        /// <summary>Gets the circular screen-mask dilation shader.</summary>
        public Shader DilateShader { get; }

        /// <summary>Gets the outer-ring composite shader.</summary>
        public Shader CompositeShader { get; }

        /// <summary>
        /// Creates a complete or intentionally incomplete shader set.
        /// </summary>
        /// <param name="maskShader">Visible-geometry mask shader.</param>
        /// <param name="dilateShader">Separable dilation shader.</param>
        /// <param name="compositeShader">Outer-ring composite shader.</param>
        public PFViewOutlineShaderSet(
            Shader maskShader,
            Shader dilateShader,
            Shader compositeShader)
        {
            MaskShader = maskShader;
            DilateShader = dilateShader;
            CompositeShader = compositeShader;
        }

        /// <summary>Gets whether all three shader references are present.</summary>
        public bool IsComplete =>
            !ReferenceEquals(MaskShader, null) &&
            !ReferenceEquals(DilateShader, null) &&
            !ReferenceEquals(CompositeShader, null);
    }

    /// <summary>
    /// Supplies shaders compiled for the active RimWorld Unity version.
    /// Implementations can load them from an AssetBundle without coupling the
    /// outline core to a particular bundle layout or loading policy.
    /// </summary>
    public interface IPFViewOutlineShaderProvider
    {
        /// <summary>
        /// Attempts to supply the three shaders required by the renderer.
        /// </summary>
        /// <param name="shaderSet">Resolved shader references on success.</param>
        /// <param name="failureReason">Diagnostic detail on failure.</param>
        /// <returns>True when the returned set is complete.</returns>
        bool TryGetShaderSet(
            out PFViewOutlineShaderSet shaderSet,
            out string failureReason);
    }

    /// <summary>
    /// Simple provider for callers that already loaded all three shaders.
    /// </summary>
    public sealed class PFViewFixedOutlineShaderProvider :
        IPFViewOutlineShaderProvider
    {
        private readonly PFViewOutlineShaderSet shaderSet;

        /// <summary>Creates a provider around an already loaded shader set.</summary>
        /// <param name="shaderSet">Compiled shaders owned by the caller.</param>
        public PFViewFixedOutlineShaderProvider(
            PFViewOutlineShaderSet shaderSet)
        {
            this.shaderSet = shaderSet;
        }

        /// <inheritdoc />
        public bool TryGetShaderSet(
            out PFViewOutlineShaderSet result,
            out string failureReason)
        {
            result = shaderSet;
            if (!shaderSet.IsComplete)
            {
                failureReason =
                    "Mask, dilate, and composite shaders are required.";
                return false;
            }

            failureReason = string.Empty;
            return true;
        }
    }

    /// <summary>
    /// Collects ship meshes and composites one constant-pixel outer silhouette.
    /// All submitted meshes share one mask, so touching ship parts do not
    /// create internal outline seams.
    /// </summary>
    /// <remarks>
    /// Create one renderer for the owning PFView window and reuse it across
    /// frames. Raw <c>.shader</c> files must first be compiled into an
    /// AssetBundle for the RimWorld Unity version and supplied through
    /// <see cref="IPFViewOutlineShaderProvider"/>.
    /// </remarks>
    /// <example>
    /// The required call order is BeginFrame and Submit before Camera.Render,
    /// followed by Composite before the RenderTexture is drawn to the GUI.
    /// Keep the Camera transform and projection unchanged between BeginFrame
    /// and Camera.Render so the submitted mask matches the scene depth.
    /// <code>
    /// IPFViewOutlineRenderer outline =
    ///     new PFViewMaskOutlineRenderer(shaderProvider);
    ///
    /// outline.BeginFrame(
    ///     renderCamera,
    ///     renderTexture,
    ///     PFViewOutlineSettings.Default);
    /// foreach (ShipPartDrawData part in visibleShipParts)
    /// {
    ///     Graphics.DrawMesh(
    ///         part.Mesh,
    ///         part.Matrix,
    ///         part.Material,
    ///         sceneLayer,
    ///         renderCamera,
    ///         part.SubMeshIndex);
    ///     outline.Submit(
    ///         part.Mesh,
    ///         part.Matrix,
    ///         part.SubMeshIndex);
    /// }
    ///
    /// renderCamera.Render();
    /// outline.Composite();
    /// GUI.DrawTexture(viewRect, renderTexture);
    ///
    /// // Call once when the owning window closes.
    /// outline.Dispose();
    /// </code>
    /// </example>
    public interface IPFViewOutlineRenderer : IDisposable
    {
        /// <summary>
        /// Gets whether supported GPU resources are ready. Remains false until
        /// the first valid BeginFrame creates GPU resources.
        /// </summary>
        bool IsReady { get; }

        /// <summary>Gets the latest lifecycle state and diagnostic message.</summary>
        PFViewOutlineStatus Status { get; }

        /// <summary>
        /// Starts collection for one frame. Call before submitting ship meshes
        /// and before invoking <see cref="Camera.Render"/>. Do not change the
        /// Camera transform or projection until that render completes.
        /// </summary>
        /// <param name="camera">Camera that will render the PFView scene.</param>
        /// <param name="destination">
        /// Camera target containing a readable depth buffer.
        /// </param>
        /// <param name="settings">Outline options for this frame.</param>
        void BeginFrame(
            Camera camera,
            RenderTexture destination,
            PFViewOutlineSettings settings);

        /// <summary>
        /// Adds one ship submesh to the shared silhouette mask. Call before
        /// <see cref="Camera.Render"/> using the same Mesh and matrix as the
        /// visible body draw.
        /// </summary>
        /// <param name="mesh">Ship-part mesh.</param>
        /// <param name="matrix">Object-to-world matrix used by PFView.</param>
        /// <param name="subMeshIndex">Submesh to draw into the mask.</param>
        void Submit(
            Mesh mesh,
            Matrix4x4 matrix,
            int subMeshIndex = 0);

        /// <summary>
        /// Redraws submitted meshes against the rendered scene depth, expands
        /// the union mask, and composites its outer ring. Call after
        /// <see cref="Camera.Render"/> and before GUI.DrawTexture.
        /// </summary>
        /// <returns>True only when an outline was composited.</returns>
        bool Composite();

        /// <summary>
        /// Drops all submissions for the active frame without touching the
        /// destination RenderTexture.
        /// </summary>
        void CancelFrame();
    }
}
