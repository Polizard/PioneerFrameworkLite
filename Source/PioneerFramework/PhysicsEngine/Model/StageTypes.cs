namespace PioneerFramework
{
    /// <summary>
    /// **级的抛弃标签**（设计期量，2026-11-03；装配时烘到 <see cref="SpacePart.stageDisposition"/>）。
    /// 默认 `Jettison` = 旧行为（抛级后成为碎片/被销毁）。
    /// </summary>
    public enum StageDisposition
    {
        /// <summary>抛弃、不可回收 → 退役时记太空垃圾。</summary>
        Jettison = 0,

        /// <summary>抛弃、可回收（占位：DMS:NF 未接时只退役、不记垃圾）。</summary>
        JettisonRecoverable = 1,

        /// <summary>不抛弃：该级成为最大级时拒绝抛级，父组继续完整工作。</summary>
        Keep = 2,
    }

    /// <summary>
    /// **组的碎片策略**（运行期量，2026-11-03）。`None` = 普通组；
    /// `IsDebris` 谓词 = `debrisExpireTick >= 0`，不要散读字段。
    /// </summary>
    public enum DebrisPolicy
    {
        None = 0,
        Scrap = 1,
        Recoverable = 2,
    }
}
