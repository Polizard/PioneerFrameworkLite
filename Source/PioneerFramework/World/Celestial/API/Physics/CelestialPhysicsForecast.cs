namespace PioneerFramework.Celestial
{
    /// <summary>Optional planning capability. Forecasts never authorize simulation advancement or extend committed history.</summary>
    public interface ICelestialPhysicsForecastProvider
    {
        CelestialResult<CelestialBodyForecast> Forecast(CelestialForecastRequest request);
    }
    public sealed record CelestialForecastRequest(System.Guid WorldSessionId, long ExpectedTopologyRevision,
        CelestialInstanceId InstanceId, long ExpectedDescriptorRevision, double PhysicalSeconds);
    public sealed record CelestialBodyForecast(PhysicsSampleStamp BasedOn, double PhysicalSeconds,
        CelestialPhysicsDescriptor Descriptor, CelestialInstanceId? ParentInstanceId, CelestialMotionState Motion,
        DVector3 PositionRelativeToParent, DVector3 VelocityRelativeToParent)
    {
        public CelestialForecastSource? Source { get; init; }
    }
}
