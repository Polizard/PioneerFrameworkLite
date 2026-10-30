# GitHub Copilot 指令

## ⛔ 后端锁（最高优先，详见 BACKEND-LOCK.md）

**后端** = 物理步 / 时间基 / 状态提交所在的代码：
`Source/PioneerFramework/Base/**`、`Components/**`、`Combat/**`、`Launch/**`、
`GroundAssembly/**`、`Landing/**`、`Celestial/Adapters/Physics/**`、`1.7/Assemblies/**`。

- 未经 `Source/PioneerFramework/DesignNotes/后端变更授权-*.md` 明确授权，**不许修改上述文件**。
- 只能产出「补丁 + 影响面 + 判据 + 回退路径」；落盘与提交由后端负责人执行。
- 遇到边界（不确定是否属于后端）⇒ **停下并询问**，不要凭目录名猜。
- 违反 = 事故：整批回退 + 交接件登记。

表现层（`PFView/**`、`1.7/Shaders`、`1.7/Textures`、`Celestial/**` 的表现面）不受限，
但**不许为了让表现层好写而改后端**。
