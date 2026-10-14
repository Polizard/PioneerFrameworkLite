using System;
using UnityEngine;

namespace PioneerFramework.PFView
{
    /// <summary>
    /// Centralizes the projection matrix supplied to PFView outline command
    /// buffers so runtime rendering and Unity GPU validation use one rule.
    /// </summary>
    internal static class PFViewOutlineProjection
    {
        /// <summary>
        /// Returns the projection matrix used when a mask CommandBuffer
        /// redraws geometry submitted to the active PFView Camera.
        /// CommandBuffer.SetViewProjectionMatrices expects Unity's camera-to-
        /// clip matrix. Pre-converting it with GL.GetGPUProjectionMatrix would
        /// apply the RenderTexture Y adjustment twice on Direct3D.
        /// </summary>
        internal static Matrix4x4 GetForCommandBuffer(Camera camera)
        {
            ArgumentNullException.ThrowIfNull(camera);

            return camera.projectionMatrix;
        }
    }
}
