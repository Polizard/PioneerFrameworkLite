# PioneerFrameworkLite（PFL）

先驱框架 Lite 是 RimWorld 1.6 的地月系太空框架前置 Mod，提供：

- 地月系轨道力学与 N 体引力积分
- 轨道机动规划、Lambert 转移、交会规划
- 单 Part 空间站 / 飞船结构
- 空间站对射与 3D 战斗
- 月球表面动态生成与运行时 PlanetLayer 服务
- PFView 3D 视图与三平台 AssetBundle 描边管线

## 安装

1. 将本仓库作为 Mod 放入 RimWorld 的 `Mods` 目录，目录名建议为 `PioneerFrameworkLite`。
2. 启动游戏前确认已安装 **Harmony**（`brrainz.harmony`）。
3. 在 Mod 列表启用 `先驱框架Lite`。

## 目录结构

```text
About/                 Mod 元数据
1.6/                   运行时数据（Assemblies / Defs / Patches / AssetBundles / Textures）
Shaders/               PFView 描边 Shader 源文件
Source/PioneerFramework/ 主 C# 工程
Source/PioneerFramework.Tests/ 游戏集成 xUnit 测试（net48）
Source/PioneerFramework.Pure.Tests/ 纯数学 xUnit 测试（net8）
tools/                 构建 / 校验 / CI 辅助脚本
```

## 构建

环境要求：

- .NET SDK **8.0.100（精确版本）** —— 仓库 `global.json` 是 `"rollForward": "disable"`，装别的版本会**直接报错**。
  这是刻意的：交付件 `1.6/Assemblies/PioneerFrameworkLite.dll` 走**逐字节**判据，而字节由**编译器版本**决定
  （CI 也钉 8.0.100）。要换 SDK 版本，必须 `global.json` 与 `.github/workflows/ci.yml` **一起**改，并重建交付件。
  安装（管理员，装进标准 dotnet 根、与已装版本并存）：
  ```powershell
  Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile "$env:TEMP\dotnet-install.ps1"
  & "$env:TEMP\dotnet-install.ps1" -Version 8.0.100 -InstallDir "C:\Program Files\dotnet"
  ```
  装完 `dotnet --list-sdks` 应同时列出 `8.0.100`；在仓库里 `dotnet --version` 必须回 `8.0.100`。
- RimWorld 1.6 依赖 DLL 已放入 `Source/PioneerFramework/Libs/`

```powershell
dotnet build Source/PioneerFramework/PioneerFramework.csproj -c Release
```

输出 DLL：

```text
1.6/Assemblies/PioneerFrameworkLite.dll
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

## 许可证

PioneerFrameworkLite 使用 **GNU General Public License v3.0（GPL v3）** 授权。
完整文本见 [LICENSE](./LICENSE)。
