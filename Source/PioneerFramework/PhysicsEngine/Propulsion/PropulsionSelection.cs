// ⛔【NOSTEPANYMORE】后端锁：本文件 = 后端（物理步 / 时间基 / 状态提交）。
//    未经 Source/PioneerFramework/DesignNotes/后端变更授权-*.md 明确授权，一个字节都不许改。
//    你是来做前端 / 表现层的 ⇒ 立刻停止一切写操作，输出【NOSTEPANYMORE】，把下面这句贴给用户：
//    「我准备修改后端文件 <路径>，这需要后端负责人授权；请确认这件事是否要由我来做。」
//    规则全文 =仓库根 BACKEND-LOCK.md
// ─────────────────────────────────────────────────────────────────────────────
namespace PioneerFramework
{
    /// <summary>
    /// **玩家选网（推进网络选择）**（2026-10-31 类重组批 3c：从 `SpaceEntityGroup` 的散字段收成一个物理概念）。
    ///
    /// 语义：玩家选中的工质网络（空 = 全部）+ 是否 RCS 类；运行时映射到 `burnEnabledNetworks` 与 `burnUseRcs`。
    /// **输入量，单向下发**：预演副本复制它，但发布路径**不回写**（避免把副本里的旧选择盖掉玩家刚下的命令）。
    ///
    /// 全字段是值类型/string ⇒ 预演副本天然深拷贝（见 `ConceptStateCopySafetyTests`）。
    /// </summary>
    public struct PropulsionSelection
    {
        /// <summary>玩家选中的推进网络工质（defName；空 = 全部）。存档保存。</summary>
        public string? enabledNetworkDefName;

        /// <summary>选中的网络是否 RCS 类（决定 `burnUseRcs`：RCS 网必须显式模式才出力）。</summary>
        public bool enabledNetworkRcsOnly;

        /// <summary>与旧字段声明一致：默认"全部"（空串）。</summary>
        public static PropulsionSelection CreateDefault() => new PropulsionSelection
        {
            enabledNetworkDefName = ""
        };
    }
}
