# 本项目 AI 入口（Claude / 通用 agent）

## ⛔【NOSTEPANYMORE】后端锁 —— 最高优先，覆盖下面全部

**后端 = 物理步 / 时间基 / 状态提交所在的代码**：
`Source/PioneerFramework/Base/**`、`Components/**`、`Combat/**`、`Launch/**`、
`GroundAssembly/**`、`Landing/**`、`Celestial/Adapters/Physics/**`、`1.7/Assemblies/**`。

**你接到的是前端 / 表现层 / UI / 渲染任务，而下一步要动后端 ⇒ 立刻停机：**

1. **停止一切写操作**（不落盘、不 `git add`、不提交）；
2. 输出一行标记：**`【NOSTEPANYMORE】`**
3. 贴出：

```
【NOSTEPANYMORE】我准备修改后端文件：<路径>
原因：<我想达成什么>
这需要后端负责人授权。我已停止，未做任何写操作。请确认是否要我改。
```

4. **等用户回答**。不回，就什么都不做。

**禁止**：先改再报告、"只改一行"、`--no-verify`、改检查脚本、自己写授权文件、把后端改动藏在重命名里。
**拿不准算不算后端 ⇒ 一律按后端处理，停下问。不许猜。**

允许做：提补丁片段 / 影响面 / 判据建议 / 回退路径；写授权申请草稿；改表现层（但不许改后端给它让路）。

全文：[BACKEND-LOCK.md](BACKEND-LOCK.md)。机器闸门：`tools/check-backend-lock.ps1 -Fail`。
