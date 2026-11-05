namespace PioneerFramework.Celestial
{
    public enum CelestialErrorCategory { Validation, Conflict, Availability, Component, Consistency, Expired }
    public enum CelestialRetryDisposition { Never, Manual, WhenProviderReady, NextSafeBoundary }
    public sealed record CelestialProviderDiagnostics(string ProviderId, PhysicsContractVersion ContractVersion,
        PhysicsBridgeStatus Status, PhysicsSampleStamp CommittedStamp)
    {
        public System.Collections.Generic.IReadOnlyList<CelestialError> Errors { get; init; } = System.Array.Empty<CelestialError>();
    }
    public sealed record CelestialForecastSource(string ProviderId, PhysicsContractVersion ContractVersion, int PredictionSchemaVersion = 1)
    {
        public const int CurrentPredictionSchemaVersion = 1;
    }
}
