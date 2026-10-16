# PioneerFrameworkLite 构建说明

RimWorld 1.7 使用 Waylith 和 .NET 10。主工程与三个测试工程均使用 net10.0，
SDK 固定为 **10.0.402**，与根目录 global.json 和 CI 一致。

在仓库根目录运行：

```powershell
# 本机准备了便携 SDK 时，先把它加入当前终端环境。
. ./tools/Use-ProjectDotnet.ps1
dotnet build Source/PioneerFramework/PioneerFramework.csproj -c Debug
dotnet build Source/PioneerFramework/PioneerFramework.csproj -c Release
powershell -ExecutionPolicy Bypass -File tools/test-all.ps1
```

Release 输出为 1.7/Assemblies/PioneerFrameworkLite.dll；Debug 输出留在主工程的 bin/Debug/。

依赖通过 Source/GameReferences.props 统一引用，位于 Source/PioneerFramework/Libs/：

- RimWorld/：从 1.7 游戏根目录复制的 Assembly-CSharp、UnityStubs、Waylith、
  Waylith.UnityCompat 和需要的 Brutal.Core.*。
- 0Harmony/0Harmony.dll：Harmony 模组 1.7/Assemblies/ 中的 Harmony 3。
- dependencies.json：游戏版本、Harmony 版本和每个 DLL 的 SHA256。

刷新与核对：

```powershell
powershell -ExecutionPolicy Bypass -File tools/update-game-dependencies.ps1
powershell -ExecutionPolicy Bypass -File tools/check-game-dependencies.ps1
```

游戏安装路径或 Harmony 路径不同时，给刷新脚本传入 -GamePath 和 -HarmonyPath。
游戏和引擎 DLL 用于编译与测试，由游戏提供运行时依赖。交付的 1.7/Assemblies/ 仅放本模组 DLL。

修改 C# 后运行 tools/rebuild-dll.ps1，再用 tools/check-committed-dll.ps1 核对 Release 产物。
自动验证覆盖编译、测试、XML、坐标系审计及确定性构建；实际游戏加载和图形效果需单独验收。
