using System;
using System.Linq;

namespace PioneerFramework.Celestial
{
    /// <summary>Only explicit v2 profiles provide this capability. Physical lighting is sampled at one committed boundary.</summary>
    public sealed class CompPlanetEnvironment(CelestialCompDescriptor descriptor) : CompExistingEnvironment(descriptor), ICelestialSurfaceEnvironmentCapability
    {
        public override CelestialEnvironmentSample Evaluate(CelestialEnvironmentQuery query)
        {
            var legacy = base.Evaluate(query);
            var climate = ((ExistingEnvironmentResolvedProperties)Context.Properties).Profile.Climate;
            if (climate == null) return legacy;
            var actual = SampleSurface(new(query.DisplayTicksAbs, query.LongitudeDegrees, query.LatitudeDegrees, 0, query.BiomeId));
            return legacy with { SunGlow = (float)Math.Clamp(actual.SunGlow, 0, 1) };
        }

        public CelestialSurfaceEnvironmentSample SampleSurface(CelestialSurfaceEnvironmentQuery query)
        {
            if (!double.IsFinite(query.DisplayTicksAbs) || !double.IsFinite(query.LongitudeDegrees) ||
                !double.IsFinite(query.LatitudeDegrees) || Math.Abs(query.LatitudeDegrees) > 90 || !double.IsFinite(query.AltitudeMeters))
                throw new ArgumentOutOfRangeException(nameof(query));
            var profile = ((ExistingEnvironmentResolvedProperties)Context.Properties).Profile;
            var climate = profile.Climate;
            var legacy = base.Evaluate(new(query.DisplayTicksAbs, (float)query.LongitudeDegrees, (float)query.LatitudeDegrees, query.BiomeId));
            double physicalSeconds = Context.PhysicalSeconds;
            double sunEast = 0, sunNorth = 0, sunUp = legacy.SunGlow, quadrature = 0, angularSpeed = 0;
            double intensity = 1;
            double? gravity = null;
            double? authoredPhase = null;
            // Temperature mode does not select the light clock: explicit physical motion owns lighting in both modes.
            bool usesPhysicalLight = climate != null && (climate.Mode == CelestialClimateMode.CommittedIllumination ||
                Context.GetCapabilities<ICelestialPhysicsBinding>().Count == 1);
            if (usesPhysicalLight || profile.Exposure?.GravityStats == true)
            {
                if (!Context.TryGetService<ICelestialQueryService>(out var service)) throw new InvalidOperationException("EnvironmentPhysicsUnavailable");
                var stamp = service.CurrentStamp;
                var result = service.Sample(new(service.WorldSessionId, stamp.TopologyRevision, new[] { Context.InstanceId, PrimaryLight() }));
                if (!result.Success) throw new InvalidOperationException("EnvironmentPhysicsUnavailable");
                using var snapshot = result.Snapshot!;
                if (!snapshot.TryGetBodyState(Context.InstanceId, out var body) || !snapshot.TryGetBodyState(PrimaryLight(), out var light) ||
                    !snapshot.TryGetBodyDescriptor(Context.InstanceId, out var geometry)) throw new InvalidOperationException("EnvironmentPhysicsUnavailable");
                physicalSeconds = snapshot.Stamp.PhysicalSeconds;
                if (usesPhysicalLight &&
                    Context.TryGetService<ICelestialCapabilityResolver>(out var resolver))
                    intensity = resolver.GetCapabilities<ICelestialLightSourceCapability>(PrimaryLight()).Single().GetDescription().Intensity;
                if (geometry.GravitationalParameter.HasValue)
                    gravity = geometry.GravitationalParameter.Value / (geometry.RadiusMeters * geometry.RadiusMeters);
                var delta = light.Position - body.Position;
                if (delta.magnitude <= 0) throw new InvalidOperationException("EnvironmentLightCoincident");
                var q = body.Orientation;
                var direction = DVector3.Rotate(delta.normalized, new DQuaternion(-q.x, -q.y, -q.z, q.w));
                double lon = query.LongitudeDegrees * Math.PI / 180, lat = query.LatitudeDegrees * Math.PI / 180;
                var normal = new DVector3(Math.Sin(lon) * Math.Cos(lat), Math.Sin(lat), -Math.Cos(lon) * Math.Cos(lat));
                var east = new DVector3(Math.Cos(lon), 0, Math.Sin(lon));
                var north = new DVector3(-Math.Sin(lon) * Math.Sin(lat), Math.Cos(lat), Math.Cos(lon) * Math.Sin(lat));
                sunUp = DVector3.Dot(direction, normal); sunEast = DVector3.Dot(direction, east); sunNorth = DVector3.Dot(direction, north);
                // Authored 温度相位对齐物理太阳：小时角 0 = 本地正午（2026-11-07 P1-1）。
                // 有物理绑定时光照已是物理快照，日温度若仍用日历 tick 会与太阳错相；无物理时 authoredPhase 保持 null，退回日历相位。
                if (climate?.Mode == CelestialClimateMode.Authored)
                    authoredPhase = lon - Math.Atan2(direction.x, -direction.z);
                var relative = light.LinearVelocity - body.LinearVelocity;
                var orbitalAngular = DVector3.Cross(delta, relative) / delta.sqrMagnitude;
                var localAngular = DVector3.Rotate(body.AngularVelocity - orbitalAngular, new DQuaternion(-q.x, -q.y, -q.z, q.w));
                angularSpeed = localAngular.magnitude;
                if (angularSpeed > 0) quadrature = DVector3.Dot(DVector3.Cross(localAngular / angularSpeed, normal), direction);
            }
            bool vacuum = legacy.VacuumBiomeId != null;
            var atmospheres = Context.GetCapabilities<ICelestialAtmosphereCompositionCapability>();
            CelestialAtmosphereSample? air = atmospheres.Count == 1 ? atmospheres[0].Sample(query.AltitudeMeters) : null;
            double? pressure = air?.PressurePascals ?? (vacuum ? 0d : null);
            double? oxygen = air?.OxygenPartialPressurePascals ?? (vacuum ? 0d : null);
            double toxicity = profile.Exposure?.ToxicityPerDay ?? 0;
            if (air != null && profile.Exposure != null)
                toxicity += Math.Max(0, air.CarbonDioxidePartialPressurePascals / profile.Exposure.CarbonDioxideThresholdPascals - 1) * profile.Exposure.ToxicityPerDay;
            bool hasAir = air != null ? air.DensityKilogramsPerCubicMeter > 0 : Context.GetCapabilities<ICelestialAtmosphereCapability>().Any(p => p.GetProfile().IsActive);
            var climateQuery = climate?.Mode == CelestialClimateMode.CommittedIllumination ? query with { DisplayTicksAbs = physicalSeconds } : query;
            double rainfall = climate?.RainfallMillimeters ?? 0, humidity = climate?.Humidity ?? 0;
            var generation = Context.GetCapabilities<ICelestialBiomeGenerationCapability>().SingleOrDefault()?.GetProfile();
            if (generation?.Terrain is { } terrain)
            {
                // 与参数化生成 tile 共用同一降雨/湿度公式；fallback 取 biomes profile 的 Rainfall/Swampiness。
                var field = CelestialPlanetTerrain.Sample(terrain, query.LongitudeDegrees, query.LatitudeDegrees, terrain.FieldSeed);
                (rainfall, humidity) = CelestialPlanetTerrain.SurfaceMoisture(terrain, generation.Rainfall, generation.Swampiness, field.Moisture);
            }
            return new(climate == null ? null : CelestialClimateModel.Temperature(climate, climateQuery, DayLength(profile), sunUp * intensity, quadrature * intensity, angularSpeed, authoredPhase),
                Math.Max(0, sunUp) * intensity * (climate?.LightMultiplier ?? 1), hasAir ? climate?.WindSpeed ?? 1 : 0,
                rainfall, humidity, pressure, oxygen, toxicity,
                profile.Exposure?.RadiationPerDay ?? 0, gravity, sunEast, sunNorth, sunUp, physicalSeconds, vacuum, profile.Exposure,
                usesPhysicalLight, climate != null, hasAir,
                climate == null ? null : CelestialClimateModel.SeasonalTemperature(climate, climateQuery));
        }
    }
}
