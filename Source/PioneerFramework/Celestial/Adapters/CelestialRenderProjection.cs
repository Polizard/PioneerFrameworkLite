using System.Linq;
using System.Collections.Generic;
using UnityEngine;

namespace PioneerFramework.Celestial
{
    /// <summary>只供既有原生绘制 helper 使用的独立输入对象，不登记进 DefDatabase。</summary>
    internal static class CelestialRenderProjection
    {
        internal static Color ColorOf(CelestialVisualProfile profile) => new((float)profile.Red, (float)profile.Green, (float)profile.Blue, (float)profile.Alpha);
        internal static CelestialBodyInfo Create(CelestialViewSnapshot snapshot, AtmosphereProfile atmosphere, double dayLengthTicks)
            => Create(snapshot.Description, snapshot.Visuals, snapshot.NativeDisplayRadius.HasValue, atmosphere, dayLengthTicks);
        internal static CelestialBodyInfo Create(CelestialBodyDescription description, IReadOnlyList<CelestialVisualDescription> visuals, bool hasSurface, AtmosphereProfile atmosphere, double dayLengthTicks)
        {
            var surface = visuals.SingleOrDefault(visual => visual.Kind == CelestialVisualKind.Surface);
            var stellar = visuals.SingleOrDefault(visual => visual.Kind == CelestialVisualKind.Stellar);
            var air = visuals.SingleOrDefault(visual => visual.Kind == CelestialVisualKind.Atmosphere);
            var color = surface?.Profile ?? stellar?.Profile;
            return new CelestialBodyInfo {
                InstanceId = description.Id, DescriptorRevision = description.DescriptorRevision, defName = description.DefinitionId, label = description.DefinitionId, bodyId = description.Handle,
                radius = description.RadiusMeters, gravitationalParameter = description.GravitationalParameter ?? 0,
                bodyType = stellar == null ? CelestialBodyType.Planet : CelestialBodyType.Star,
                hasSurface = hasSurface, baseSphereColor = color == null ? Color.clear : ColorOf(color),
                atmosphereThickness = air == null ? 0 : air.Profile.ShellHeightMeters * 2,
                atmosphereRho0 = atmosphere.rho0, atmosphereScaleHeight = atmosphere.scaleHeight,
                atmosphereColor = air == null ? Color.clear : ColorOf(air.Profile), dayLengthTicks = dayLengthTicks };
        }
    }
}
