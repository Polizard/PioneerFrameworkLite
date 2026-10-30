namespace PioneerFramework.Celestial
{
    public sealed record CelestialSchedulerCursor(CelestialInstanceId InstanceId, string CompKey, double LastSeconds);
    public sealed record CelestialPendingEvent(CelestialInstanceId InstanceId, string? CompKey, CelestialEvent Message);
    public sealed record CelestialCommandRecord(CelestialCommand Command, CelestialOperation Receipt);
}
