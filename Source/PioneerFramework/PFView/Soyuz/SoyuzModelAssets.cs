using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView Soyuz prism model family. Base and minimal variants share the
    /// same runtime loader; the minimal variant is selected by PFView's
    /// configured prefix and never falls back to the base high-detail asset.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class SoyuzModelAssets
    {
        internal const string PartDefName = "PF_ShipPart_Soyuz";

        private static readonly PFModelAsset Asset = new(
            "联盟号",
            @"Models\Soyuz\PFL_Soyuz_Prism.obj",
            "PFView/Ships/Soyuz/SoyuzAlbedo",
            "PFView/Ships/Soyuz/SoyuzNormal");

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
