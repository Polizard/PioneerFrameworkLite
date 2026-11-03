using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PioneerFramework.Celestial
{
    public interface ICelestialAtmosphereCompositionCapability
    {
        CelestialAtmosphereSample Sample(double altitudeMeters);
    }
    public sealed record CelestialAtmosphereSample(double PressurePascals, double DensityKilogramsPerCubicMeter,
        double OxygenPartialPressurePascals, double CarbonDioxidePartialPressurePascals, double TemperatureKelvin);
    public sealed record CelestialGasFraction(string Gas, double MoleFraction);
    public sealed record CelestialResolvedGasFraction(string Gas, double MoleFraction, double MolarMassKilogramsPerMole);
    public sealed partial class AtmosphereResolvedProperties
    {
        public const double GasConstant = 8.31446261815324;
        public double? PressurePascals { get; }
        public double? TemperatureKelvin { get; }
        public double? SurfaceGravityMetersPerSecondSquared { get; }
        public IReadOnlyList<CelestialGasFraction> Composition { get; } = Array.Empty<CelestialGasFraction>();
        public IReadOnlyList<CelestialResolvedGasFraction> ResolvedComposition { get; } = Array.Empty<CelestialResolvedGasFraction>();
        private readonly double oxygenFraction;
        private readonly double carbonDioxideFraction;
        public static double MolarMass(string gas) => CelestialAtmosphereGasRegistry.Global.TryGetMolarMass(gas, out double mass) ? mass :
            throw new ArgumentException("UnknownAtmosphereGas: " + gas);
        private static CelestialResolvedGasFraction[] ResolveComposition(IReadOnlyList<CelestialGasFraction> composition)
        {
            if (composition == null || composition.Any(item => item == null)) throw new ArgumentException("InvalidAtmosphereComposition");
            return composition.Select(item => new CelestialResolvedGasFraction(item.Gas, item.MoleFraction, MolarMass(item.Gas))).ToArray();
        }
        public static double ValidateComposition(IReadOnlyList<CelestialGasFraction> composition) => ValidateResolvedComposition(ResolveComposition(composition));
        private static double ValidateResolvedComposition(IReadOnlyList<CelestialResolvedGasFraction> composition)
        {
            if (composition == null || composition.Count == 0 || composition.Any(item => item == null) ||
                composition.Select(item => item.Gas).Distinct(StringComparer.Ordinal).Count() != composition.Count)
                throw new ArgumentException("InvalidAtmosphereComposition");
            double sum = 0, mass = 0;
            foreach (var item in composition)
            {
                if (!CelestialAtmosphereGasRegistry.ValidId(item.Gas) || !double.IsFinite(item.MoleFraction) || item.MoleFraction < 0 || item.MoleFraction > 1 ||
                    !double.IsFinite(item.MolarMassKilogramsPerMole) || item.MolarMassKilogramsPerMole <= 0)
                    throw new ArgumentException("InvalidAtmosphereComposition");
                sum += item.MoleFraction; mass += item.MolarMassKilogramsPerMole * item.MoleFraction;
            }
            if (Math.Abs(sum - 1) > 1e-12 || !double.IsFinite(mass) || mass <= 0) throw new ArgumentException("AtmosphereCompositionNotNormalized");
            return mass;
        }
        public AtmosphereResolvedProperties(string key, double pressurePascals, double temperatureKelvin,
            double topAltitude, double surfaceGravityMetersPerSecondSquared, IReadOnlyList<CelestialGasFraction> composition)
            : this(key, pressurePascals, temperatureKelvin, topAltitude, surfaceGravityMetersPerSecondSquared, ResolveComposition(composition)) { }
        private AtmosphereResolvedProperties(string key, double pressurePascals, double temperatureKelvin,
            double topAltitude, double surfaceGravityMetersPerSecondSquared, IReadOnlyList<CelestialResolvedGasFraction> composition)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (!double.IsFinite(pressurePascals) || pressurePascals < 0 || !double.IsFinite(temperatureKelvin) || temperatureKelvin <= 0 ||
                !double.IsFinite(topAltitude) || topAltitude <= 0 || !double.IsFinite(surfaceGravityMetersPerSecondSquared) || surfaceGravityMetersPerSecondSquared <= 0)
                throw new ArgumentException("InvalidPressureAtmosphere");
            double mass = ValidateResolvedComposition(composition);
            Key = key; PressurePascals = pressurePascals; TemperatureKelvin = temperatureKelvin; TopAltitude = topAltitude;
            SurfaceGravityMetersPerSecondSquared = surfaceGravityMetersPerSecondSquared;
            ResolvedComposition = Array.AsReadOnly(composition.ToArray());
            Composition = Array.AsReadOnly(composition.Select(item => new CelestialGasFraction(item.Gas, item.MoleFraction)).ToArray());
            oxygenFraction = composition.SingleOrDefault(item => item.Gas == "Oxygen")?.MoleFraction ?? 0;
            carbonDioxideFraction = composition.SingleOrDefault(item => item.Gas == "CarbonDioxide")?.MoleFraction ?? 0;
            Rho0 = pressurePascals * mass / (GasConstant * temperatureKelvin);
            ScaleHeight = GasConstant * temperatureKelvin / (mass * surfaceGravityMetersPerSecondSquared);
            if (!double.IsFinite(Rho0) || !double.IsFinite(ScaleHeight) || ScaleHeight <= 0) throw new ArgumentException("InvalidPressureAtmosphere");
        }
        public CelestialAtmosphereSample Sample(double altitudeMeters)
        {
            if (!double.IsFinite(altitudeMeters)) throw new ArgumentOutOfRangeException(nameof(altitudeMeters));
            if (!PressurePascals.HasValue) throw new InvalidOperationException("AtmosphereCompositionUnavailable");
            double factor = altitudeMeters >= TopAltitude ? 0 : Math.Exp(-Math.Max(0, altitudeMeters) / ScaleHeight);
            double pressure = PressurePascals.Value * factor;
            return new(pressure, Rho0 * factor, pressure * oxygenFraction, pressure * carbonDioxideFraction, TemperatureKelvin!.Value);
        }
        private ReadOnlyMemory<byte> SerializePressure() => JsonSerializer.SerializeToUtf8Bytes(new PressureConfiguration(
            PressurePascals!.Value, TemperatureKelvin!.Value, TopAltitude, SurfaceGravityMetersPerSecondSquared!.Value, Rho0, ScaleHeight, ResolvedComposition));
        private sealed record PressureConfiguration(double PressurePascals, double TemperatureKelvin, double TopAltitude,
            double SurfaceGravityMetersPerSecondSquared, double Rho0, double ScaleHeight, IReadOnlyList<CelestialResolvedGasFraction> Composition);
        private static CelestialResult<AtmosphereResolvedProperties> DeserializePressure(string key, ReadOnlyMemory<byte> payload)
        {
            try
            {
                var data = CelestialResolvedJson.Read<PressureConfiguration>(payload);
                var properties = new AtmosphereResolvedProperties(key, data.PressurePascals, data.TemperatureKelvin,
                    data.TopAltitude, data.SurfaceGravityMetersPerSecondSquared, data.Composition);
                if (BitConverter.DoubleToInt64Bits(properties.Rho0) != BitConverter.DoubleToInt64Bits(data.Rho0) ||
                    BitConverter.DoubleToInt64Bits(properties.ScaleHeight) != BitConverter.DoubleToInt64Bits(data.ScaleHeight))
                    throw new ArgumentException("InconsistentResolvedAtmosphere");
                return CelestialResult.Ok(properties);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            { return CelestialResult.Reject<AtmosphereResolvedProperties>(new CelestialError("InvalidConfigurationPayload", CompKey: key, Detail: exception.Message)); }
        }
    }
}
