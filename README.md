# PioneerFrameworkLite（PFL）

先驱框架 Lite 是 RimWorld 1.7 的地月系太空框架前置 Mod，提供：

- 地月系轨道力学与 N 体引力积分
- 轨道机动规划、Lambert 转移、交会规划
- 单 Part 空间站 / 飞船结构
- 空间站对射与 3D 战斗
- 月球表面动态生成与运行时 PlanetLayer 服务
- XML 固定/范围天体模板、独立 Comp、动态创建/移除和版本化实例保存
- PFView 3D 视图及预留的独立描边模块（Waylith 后端待迁移）

## 安装

1. 将本仓库作为 Mod 放入 RimWorld 的 `Mods` 目录，目录名建议为 `PioneerFrameworkLite`。
2. 启动游戏前确认已安装 **Harmony**（`brrainz.harmony`）。
3. 在 Mod 列表启用 `先驱框架Lite`。

## 目录结构

```text
About/                 Mod 元数据
1.7/                   运行时数据（Assemblies / Defs / Patches / AssetBundles / Textures）
Shaders/               PFView 描边 Shader 源文件
Source/PioneerFramework/ 主 C# 工程
Source/PioneerFramework.Tests/ 游戏集成 xUnit 测试（net10.0）
Source/PioneerFramework.Pure.Tests/ 纯数学 xUnit 测试（net10.0）
tools/                 构建 / 校验 / CI 辅助脚本
```

## 1.7 依赖迁移

当前目标为 RimWorld 1.7、Waylith 与 .NET 10。`Source/GameReferences.props` 统一主工程和集成测试引用。
游戏 DLL 使用 `Assembly-CSharp`、`UnityStubs`、`Waylith` 等，Harmony 使用 1.7 版本（程序集 3.0.0.0）。
刷新本机依赖后会记录版本和 SHA256：

```powershell
powershell -ExecutionPolicy Bypass -File tools/update-game-dependencies.ps1
powershell -ExecutionPolicy Bypass -File tools/check-game-dependencies.ps1
```

如本机已在 `.zcode/dotnet10/` 准备便携 SDK，构建/校验脚本会自动使用它；手动调用 `dotnet` 前可运行
`. ./tools/Use-ProjectDotnet.ps1`。新克隆请安装下列精确 SDK。

保留的独立屏幕描边模块依赖 Unity CommandBuffer，当前 1.7 的兼容层只提供占位实现。
该预留模块现明确返回不可用状态；主 PFView 原有流程未接入这个模块。自定义渲染、Shader 和游戏内功能仍需实际游戏验收。

## 构建

环境要求：

- .NET SDK **10.0.402（精确版本）** —— 仓库 `global.json` 是 `"rollForward": "disable"`，装别的版本会**直接报错**。
  这是刻意的：交付件 `1.7/Assemblies/PioneerFrameworkLite.dll` 走**逐字节**判据，而字节由**编译器版本**决定
  （CI 也钉 10.0.402）。要换 SDK 版本，必须 `global.json` 与 `.github/workflows/ci.yml` **一起**改，并重建交付件。
  安装（管理员，装进标准 dotnet 根、与已装版本并存）：
  ```powershell
  Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile "$env:TEMP\dotnet-install.ps1"
  & "$env:TEMP\dotnet-install.ps1" -Version 10.0.402 -InstallDir "C:\Program Files\dotnet"
  ```
  装完 `dotnet --list-sdks` 应同时列出 `10.0.402`；在仓库里 `dotnet --version` 必须回 `10.0.402`。
- RimWorld 1.7 依赖 DLL 已放入 `Source/PioneerFramework/Libs/`

```powershell
dotnet build Source/PioneerFramework/PioneerFramework.csproj -c Release
```

输出 DLL：

```text
1.7/Assemblies/PioneerFrameworkLite.dll
```

## 测试

```powershell
dotnet test Source/PioneerFramework.Tests/PioneerFramework.Tests.csproj
dotnet test Source/PioneerFramework.Pure.Tests/PioneerFramework.Pure.Tests.csproj
```

## CI

仓库包含 GitHub Actions 工作流 `.github/workflows/ci.yml`，会执行：

- Debug / Release 构建
- 确定性构建哈希校验
- 已提交 DLL 与 CI Release 构建一致性校验
- xUnit 测试
- Defs / DefOf / 运行时资产校验

## 行星框架扩展

公开契约以 [通用天体 API](Source/PioneerFramework/Celestial/API/README.md) 为准（存档 v2，要求新建测试世界）。从 [CelestialCompSample 使用说明](Examples/CelestialCompSample/README.md) 开始：示例包含可加载的固定/范围 XML、独立 C# 组件、保存 codec、生成配置 patch 和开发者检查动作。按说明构建并作为独立 Mod 启用后，可检查多个实例的计数与保存恢复。

## 许可证

PioneerFrameworkLite 使用 **GNU General Public License v3.0（GPL v3）** 授权。
完整文本见 [LICENSE](./LICENSE)。
