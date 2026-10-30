using System;

namespace PioneerFramework.Celestial
{
    /// <summary>延迟业务保存实际目标身份与描述版本；执行时通过当前宿主重新校验。</summary>
    public sealed record CelestialTargetToken(Guid WorldSessionId, CelestialInstanceId InstanceId, long DescriptorRevision, int Handle);
}
