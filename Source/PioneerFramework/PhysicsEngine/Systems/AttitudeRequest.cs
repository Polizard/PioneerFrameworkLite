// ⛔【NOSTEPANYMORE】后端锁：本文件 = 后端（物理步 / 时间基 / 状态提交）。
//    未经 Source/PioneerFramework/DesignNotes/后端变更授权-*.md 明确授权，一个字节都不许改。
//    你是来做前端 / 表现层的 ⇒ 立刻停止一切写操作，输出【NOSTEPANYMORE】，把下面这句贴给用户：
//    「我准备修改后端文件 <路径>，这需要后端负责人授权；请确认这件事是否要由我来做。」
//    规则全文 = 仓库根 BACKEND-LOCK.md
// ─────────────────────────────────────────────────────────────────────────────
namespace PioneerFramework
{
    /// <summary>姿态请求用哪根船体轴（2026-10-27：每步只解析一个请求、只写一次角速度）。</summary>
    public enum AttitudeAxis : byte { MainThrust = 0, Forward = 1, Up = 2 }

    /// <summary>
    /// **本步姿态请求**（2026-10-31 类重组批 3d：从 `SpaceEntityGroup` 的散字段收成一个物理概念）。
    ///
    /// 语义：各子系统每步只提交意图（目标方向 + 优先级 + 作用轴），`ComputeGroupPhysics` 每步解析一次、
    /// **只写一次角速度**。**输入量，单向下发**：预演副本复制它，发布路径不回写。
    ///
    /// 全字段值类型 ⇒ 预演副本天然深拷贝（见 `ConceptStateCopySafetyTests`）。
    /// </summary>
    public struct AttitudeRequest
    {
        /// <summary>本步姿态目标（世界系方向）。</summary>
        public DVector3 targetSystem;

        /// <summary>本步胜出请求的优先级；`int.MinValue` = 无请求。</summary>
        public int priority;

        /// <summary>本步胜出请求作用的船体轴。</summary>
        public AttitudeAxis axis;

        /// <summary>与旧字段声明一致：无请求（优先级 `int.MinValue`、目标零向量）。</summary>
        public static AttitudeRequest CreateDefault() => new AttitudeRequest
        {
            targetSystem = DVector3.zero,
            priority = int.MinValue
        };
    }
}
