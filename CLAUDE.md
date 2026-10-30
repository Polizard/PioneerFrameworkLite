# 本项目 AI 守则入口（Claude / 通用 agent）

## ⛔ 第一件事：读 [BACKEND-LOCK.md](BACKEND-LOCK.md)

**后端 = 物理步 / 时间基 / 状态提交所在的代码**（`Base/**`、`Components/**`、`Combat/**`、
`Launch/**`、`GroundAssembly/**`、`Landing/**`、`Celestial/Adapters/Physics/**`、`1.7/Assemblies/**`）。

- 未经 `Source/PioneerFramework/DesignNotes/后端变更授权-*.md` 明确授权：**一个字节都不许改**。
- 你只能产出「补丁 + 影响面 + 判据 + 回退路径」；**落盘与提交由后端负责人做**。
- 拿不准某个文件算不算后端 ⇒ **停下问**，不许猜。
- 违反 = 事故：整批回退 + 交接件登记。

## 其余

- 工程约定见 `CONTRIBUTING.md`；构建 / 测试 / 部署命令在 `BACKEND-LOCK.md` 之后的第一节。
- 表现层（`PFView/**`、`1.7/Shaders`、`1.7/Textures`、`Celestial/**` 的表现面）不受后端锁限制，
  但**不许顺手改后端来给表现层让路**——需要的话提方案。
