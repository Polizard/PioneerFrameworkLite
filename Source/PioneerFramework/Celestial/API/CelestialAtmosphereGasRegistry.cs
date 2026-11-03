using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    /// <summary>Explicit stable gas identifiers. Registrations are append-only; each resolved atmosphere freezes its own molar masses.</summary>
    public sealed class CelestialAtmosphereGasRegistry
    {
        private readonly object gate = new();
        private readonly Dictionary<string, double> gases = new(StringComparer.Ordinal) {
            ["Nitrogen"] = .0280134, ["Oxygen"] = .0319988, ["CarbonDioxide"] = .0440095,
            ["Argon"] = .039948, ["Hydrogen"] = .00201588, ["Helium"] = .004002602,
            ["Methane"] = .0160425, ["WaterVapor"] = .01801528 };
        public static CelestialAtmosphereGasRegistry Global { get; } = new();
        internal static bool ValidId(string id) => !string.IsNullOrWhiteSpace(id) && !id.Any(char.IsWhiteSpace);
        public CelestialResult<bool> Register(string stableId, double molarMassKilogramsPerMole)
        {
            if (!ValidId(stableId) || !double.IsFinite(molarMassKilogramsPerMole) || molarMassKilogramsPerMole <= 0)
                return CelestialResult.Reject<bool>(new CelestialError("InvalidAtmosphereGasRegistration", Detail: stableId));
            lock (gate) return gases.TryAdd(stableId, molarMassKilogramsPerMole) ? CelestialResult.Ok(true) :
                CelestialResult.Reject<bool>(new CelestialError("DuplicateAtmosphereGas", Detail: stableId));
        }
        public bool TryGetMolarMass(string stableId, out double molarMassKilogramsPerMole)
        {
            lock (gate)
            {
                molarMassKilogramsPerMole = 0;
                return ValidId(stableId) && gases.TryGetValue(stableId, out molarMassKilogramsPerMole);
            }
        }
    }
}
