<!-- 请不要删除这段。它是每个 PR 的第一道检查。 -->

## ⛔【NOSTEPANYMORE】后端锁自检

后端 = **物理步 / 时间基 / 状态提交**所在的代码：
`Source/PioneerFramework/{Base,Components,Combat,Launch,GroundAssembly,Landing}/**`、
`Celestial/Adapters/Physics/**`、`1.7/Assemblies/**`。

- [ ] 本 PR **没有**修改任何后端文件（改了就勾下面这条）
- [ ] 本 PR 修改了后端，且已附 `Source/PioneerFramework/DesignNotes/后端变更授权-<日期>-<主题>.md`
- [ ] 三件套齐：**证据**（Def/API 反编译取证）· **判据**（哪条测试/命令/阈值）· **回退路径**
- [ ] 已跑 `powershell -ExecutionPolicy Bypass -File tools/check-backend-lock.ps1 -Fail` 且通过

> 若你是 AI 助手：你**没有权限**勾选上面任何一项。碰到后端就停下，输出 **`【NOSTEPANYMORE】`**，
> 把这一段原样贴给你的用户，然后等回答。规则全文见 `BACKEND-LOCK.md`。

## 改了什么

<!-- 一句话 -->

## 判据

<!-- 跑什么、看到什么、阈值是多少 -->

## 回退

<!-- 怎么单独撤掉这次改动 -->
