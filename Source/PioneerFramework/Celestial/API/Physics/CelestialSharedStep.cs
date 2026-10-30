namespace PioneerFramework.Celestial
{
    public enum CelestialStepParticipantPhase { State, Clock }
    public interface ICelestialStepParticipant
    {
        string Id { get; }
        CelestialStepParticipantPhase Phase { get; }
        CelestialResult<ICelestialPreparedStepPart> Prepare(CelestialPreparedStepContext context);
    }
    /// <summary>Prepare 中完成校验与分配。有效令牌 Commit 只发布准备好的状态，不调用外部事件，不允许业务失败。</summary>
    public interface ICelestialPreparedStepPart
    {
        void Commit();
        void Abort();
    }
    public sealed record CelestialPreparedStepContext(PhysicsIntervalRequest Request,
        ICelestialPhysicsIntervalSampler Physics, PhysicsSampleStamp EndStamp);
}
