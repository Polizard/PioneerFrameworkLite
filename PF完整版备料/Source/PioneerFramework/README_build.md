# PioneerFramework 构建说明

本文说明如何在本机编译本模组。依赖都放在项目内的 `Libs` 里，不依赖每个人电脑上的 Steam 安装路径。

## 环境要求

- Visual Studio 2022（或带 MSBuild 的 Build Tools）
- .NET Framework 4.8 开发包（Target Framework）
- 本机已安装对应版本的 RimWorld（用于从游戏目录拷贝依赖 DLL）

打开工程请用：

- `Source/PioneerFramework/PioneerFramework.sln`

## 依赖放在哪里

编译引用统一走 `Libs`，路径写在 `PioneerFramework.csproj` 里，都是相对路径。

当前需要这些文件：

| 路径 | 用途 |
|---|---|
| `Libs/0Harmony/0Harmony.dll` | Harmony 补丁 |
| `Libs/RimWorld/Assembly-CSharp.dll` | 游戏主程序集 |
| `Libs/Unity/UnityEngine.dll` | Unity 基础 |
| `Libs/Unity/UnityEngine.CoreModule.dll` | Unity 核心 |
| `Libs/Unity/UnityEngine.IMGUIModule.dll` | IMGUI |
| `Libs/Unity/UnityEngine.TextRenderingModule.dll` | 文字渲染 |
| `Libs/Unity/UnityEngine.InputLegacyModule.dll` | 旧版输入模块 |
| `Libs/Unity/UnityEngine.PhysicsModule.dll` | 物理（碰撞体 / Raycast 等） |

游戏相关 DLL 从这里拷：

`\<你的 RimWorld 安装目录\>\RimWorldWin64_Data\Managed\`

`0Harmony.dll` 从 Harmony 模组的 `Assemblies` 目录拷，或沿用团队已准备好的那份。

注意：

- 游戏更新后，若编译或运行异常，优先用当前游戏的 Managed 重新覆盖 `Libs/RimWorld` 与 `Libs/Unity`。
- 以后要加新依赖，放在 `Libs/<包名>/` 下，并在 csproj 里用相对路径引用；不要写本机绝对路径。
- `Private=False`，编译产物不会把依赖 DLL 再复制进输出目录。

## 怎么编译

任选一种即可：

1. Visual Studio：打开 `PioneerFramework.sln`，选 Debug 或 Release，生成解决方案。
2. 命令行（示例）：

```bat
msbuild PioneerFramework.csproj /p:Configuration=Debug
```

Debug 与 Release 的输出目录相同：

- `1.6/Assemblies/PioneerFramework.dll`

也就是模组根目录下的 `1.6/Assemblies/`。编完一般不用再手动拷贝，游戏加载该模组时会直接读这里。

同目录可能生成 `PioneerFramework.pdb`（调试符号）。本地调试可以留着；以后建仓库时建议忽略 `*.pdb`，不要提交。

## 目录约定（和编译相关的部分）

```
Source/PioneerFramework/
├── Libs/                     编译依赖（不要删）
├── Base/ Combat/ Ships3D/ …  业务源码
├── DesignNotes/              设计文档与统筹计划（见「设计文档在哪」）
├── Properties/
├── PioneerFramework.csproj
├── PioneerFramework.sln
└── README_build.md           本说明
```

模组运行时内容在仓库/模组根的 `About/`、`1.6/` 等目录；本文件只覆盖「怎么把 C# 编成 DLL」。

## 设计文档在哪

设计与计划文档统一放在 `DesignNotes/` 目录下，按需阅读：

- [MasterPlan.md](DesignNotes/MasterPlan.md) — **统筹计划，建议从这里开始**：项目进度的唯一入口，记录每项工作「做到哪一步、接下来做什么」，并链接到各篇设计文档
- [PioneerFramework设计与计划.md](DesignNotes/PioneerFramework设计与计划.md) — 项目总设计：模块架构、Phase 规划、扩展接口、性能规范
- [架构审计-20260726.md](DesignNotes/架构审计-20260726.md) — 架构审计问题清单与修复优先级
- [太空战斗系统-设计思路.md](DesignNotes/太空战斗系统-设计思路.md) — 战斗系统详细设计
- [3D飞船系统-技术思路.md](DesignNotes/3D飞船系统-技术思路.md) — 飞船装配系统技术思路
- [ToDoList.md](DesignNotes/ToDoList.md) — 战斗系统改造专项执行清单

先读 MasterPlan.md 了解全貌，需要具体细节时再跳转到对应的设计文档。

## 常见问题

**Libs 缺文件或路径不对**  
先对照上面的表格检查文件是否齐全，再确认 csproj 里的 `HintPath` 仍指向 `Libs\...`。

**换了电脑 / 换了 RimWorld 安装盘符**  
只要 `Libs` 完整，一般不用改工程。不要再把引用改回 `C:\Program Files\...` 这类绝对路径。

**不该放进工程目录的东西**  
`bin/`、`obj/`、`.vs/`、散落在 `Source` 根目录的游戏 DLL、无关的 `package.json` 等都不需要。依赖只保留在 `Libs` 里。
