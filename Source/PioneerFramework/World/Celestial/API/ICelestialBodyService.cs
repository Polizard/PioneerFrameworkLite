namespace PioneerFramework.Celestial
{
    /// <summary>数据式动作入口；请求带会话、修订及显式代次，副作用在宿主安全边界执行，可能返回 Pending。</summary>
    public interface ICelestialBodyService
    {
        CelestialOperation Create(CelestialCreateRequest request);
        CelestialOperation CreateBatch(CelestialCreateBatchRequest request);
        CelestialOperation GetOperation(string operationId);
        CelestialResult<long> OpenEpoch(System.Guid worldSessionId);
        CelestialResult<bool> CloseEpoch(System.Guid worldSessionId, long epoch);
        CelestialResult<bool> DiscardFailedCreation(System.Guid worldSessionId, CelestialInstanceId id);
        CelestialResult<CelestialBodySnapshot> Query(CelestialInstanceId id);
        CelestialRemovalCheck CanRemove(CelestialInstanceId id);
        CelestialOperation Remove(RemoveBodyRequest request);
        CelestialResult<string> RegisterReference(CelestialReference reference);
        CelestialResult<bool> ReleaseReference(string referenceId);
        CelestialSurfaceOperation RequestSurface(CelestialSurfaceRequest request);
        CelestialMapOperation RequestMap(CelestialMapRequest request);
        CelestialResult<bool> EnqueueEvent(System.Guid session, CelestialInstanceId id, CelestialEvent message, string? compKey = null);
        CelestialResult<string> Submit(CelestialCommand command);
    }
}
