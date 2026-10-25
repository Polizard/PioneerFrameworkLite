using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView Crew Dragon model family. Base and minimal variants share the
    /// same runtime loader; the minimal variant is selected by PFView's
    /// configured prefix and never falls back to the base high-detail asset.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class CrewDragonModelAssets
    {
        internal const string PartDefName = "PF_ShipPart_CrewDragon";
        internal const float BoundsX = 4.084576f;
        internal const float BoundsY = 4.167594f;
        internal const float BoundsZ = 7.721946f;

        private static readonly PFModelAsset Asset = new(
            "龙飞船",
            @"Models\CrewDragon\PFL_CrewDragon.obj",
            "PFView/Ships/CrewDragon/CrewDragonAlbedo",
            "PFView/Ships/CrewDragon/CrewDragonNormal");

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
