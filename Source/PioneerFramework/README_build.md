# PioneerFrameworkLite 构建说明

## 环境要求

- .NET SDK 8.0.100 或更高（仓库 `global.json` 固定 8.0.100）
- RimWorld 1.6 依赖 DLL 已放在 `Libs/` 中

## 构建

```powershell
dotnet build Source/PioneerFramework/PioneerFramework.csproj -c Debug
dotnet build Source/PioneerFramework/PioneerFramework.csproj -c Release
```

输出：

```text
1.6/Assemblies/PioneerFrameworkLite.dll
```

## 测试

```powershell
dotnet test Source/PioneerFramework.Tests/PioneerFramework.Tests.csproj
dotnet test Source/PioneerFramework.Pure.Tests/PioneerFramework.Pure.Tests.csproj
```

## 依赖

依赖统一放在 `Source/PioneerFramework/Libs/`：

- `Libs/RimWorld/Assembly-CSharp.dll`
- `Libs/RimWorld/netstandard.dll`
- `Libs/Unity/*.dll`
- `Libs/0Harmony/0Harmony.dll`

游戏更新后，如编译或运行异常，优先用当前游戏 `RimWorldWin64_Data/Managed/` 覆盖 `Libs/RimWorld` 与 `Libs/Unity`。

## 提交 DLL

CI 会校验 `1.6/Assemblies/PioneerFrameworkLite.dll` 与 Release 构建一致。修改 C# 后请运行：

```powershell
powershell -ExecutionPolicy Bypass -File tools/rebuild-dll.ps1
```
