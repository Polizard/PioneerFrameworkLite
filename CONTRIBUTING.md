# 贡献指南

感谢你参与 PioneerFrameworkLite（PFL）开发。

## 开发环境

- Windows（RimWorld 1.6 目标为 .NET Framework 4.8）
- .NET SDK 8.0.100 或更高
- 推荐 Visual Studio 2022 或 VS Code + C# Dev Kit

## 常用命令

```powershell
# 构建
dotnet build Source/PioneerFramework/PioneerFramework.csproj -c Debug

# 运行游戏集成测试（net48）
dotnet test Source/PioneerFramework.Tests/PioneerFramework.Tests.csproj

# 运行纯数学测试（net8）
dotnet test Source/PioneerFramework.Pure.Tests/PioneerFramework.Pure.Tests.csproj

# 重建并提交 DLL
powershell -ExecutionPolicy Bypass -File tools/rebuild-dll.ps1

# 校验已提交 DLL 与 Release 构建一致
powershell -ExecutionPolicy Bypass -File tools/check-committed-dll.ps1
```

## 代码风格

- C# 使用 `LangVersion 12.0`，主工程启用 nullable。
- 保持 `Source/PioneerFramework/Libs/` 作为唯一依赖目录，不引用本机绝对路径。
- 轨道 / 数学逻辑优先放入纯 C# 核，便于单元测试。
- 修改 Defs 时同步更新 `tools/validate_config.py` 覆盖的校验规则。

## 测试要求

- 修改纯数学逻辑时，在 `Source/PioneerFramework.Pure.Tests` 补充/更新测试。
- 修改 RimWorld 集成逻辑时，在 `Source/PioneerFramework.Tests` 补充/更新测试。
- 提交前本地运行两个测试项目，确保全部通过。

## PR 流程

1. 从最新 `master` 创建功能分支。
2. 小步提交，每个提交只做一件事。
3. 如果修改了 C#，确保重新生成 `1.6/Assemblies/PioneerFrameworkLite.dll` 并提交。
4. 推送分支并创建 Pull Request。
5. CI 全绿后由维护者 review 合并。

## DLL 提交约定

`1.6/Assemblies/PioneerFrameworkLite.dll` 是已提交二进制，CI 会校验它必须等于固定 SDK 的 Release 构建结果。

提交 DLL 前请使用：

```powershell
powershell -ExecutionPolicy Bypass -File tools/rebuild-dll.ps1
```

不要手动替换 DLL。
