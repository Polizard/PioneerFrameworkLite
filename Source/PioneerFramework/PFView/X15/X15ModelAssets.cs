using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView X-15 model family. Base and minimal variants share the same
    /// runtime loader; the minimal variant is selected by PFView's configured
    /// prefix and never falls back to the base high-detail asset.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class X15ModelAssets
    {
        internal const string PartDefName = "PF_ShipPart_X15";
        internal const float BoundsX = 6.812f;
        internal const float BoundsY = 3.9082f;
        internal const float BoundsZ = 15.251f;

        private static readonly PFModelAsset Asset = new(
            "X-15",
            @"Models\X15\PFL_X15_Hypersonic.obj",
            "PFView/Ships/X15/X15Albedo",
            "PFView/Ships/X15/X15Normal");

        internal static bool Available => Asset.Available;
        internal static string? Failure => Asset.Failure;

        internal static bool TryDraw(
            SpaceEntity entity,
            Vector3 viewPos,
            Quaternion groupRotation,
            float scale,
            int layer,
            MaterialPropertyBlock block,
            float metersToView)
            => Asset.TryDraw(PartDefName, entity, viewPos, groupRotation, scale,
                layer, block, metersToView);

        internal static void ApplyLighting(Vector3 toSun, Color sunColor, Vector3 cameraViewPos)
            => Asset.ApplyLighting(toSun, sunColor, cameraViewPos);

        internal static void Destroy() => Asset.Destroy();
    }
}
