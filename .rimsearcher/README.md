# RimSearcher Project Environment

- Status: Complete — `defs.db` 3.2.0 / 14,876 defs 已验证；DecompilerServer MCP 已接入 DSH（2026-10-20）。

## Tool Versions

- Project CLI: 3.2.0（`.rimsearcher/rimsearcher.exe`）
- Database Version: 3.2.0（`PRAGMA user_version = 30200`；CLI 支持 3.1.5–3.2.0）
- DecompilerServer: 1.4.0（stdio MCP；DSH `serverName = decompiler`）

## Game Locations

- Game Root: `C:\Program Files (x86)\Steam\steamapps\common\RimWorld`（Version.txt: 1.7.4982 rev1280）
- Game DLL Directory: 游戏根目录（1.7 起改为扁平布局，**无** `RimWorldWin64_Data\Managed\`）
- Core Assembly Assembly-CSharp.dll: `D:\RimRef\Assembly-CSharp.dll`（15,632,896 字节，程序集版本 1.7.9787.38423，SHA256 `69CF3559…`；2026-10-20 从游戏根刷新，旧件备份为 `Assembly-CSharp.dll.bak-20261020`）
- DataMod Installation Directory: `C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\RimSearcher_DataMod`

## Dependent Mods

- Mod Directories: 游戏内 `Mods\`（本项目安装于 `Mods\PioneerFrameworkLite`）；工坊 `C:\Program Files (x86)\Steam\steamapps\workshop\content\294100`
- Relevant Assembly DLLs: `...\Mods\PioneerFrameworkLite\1.7\Assemblies\PioneerFrameworkLite.dll`（按需再拷到 `D:\RimRef`，不复制未完工的本仓库产物）

## Def Snapshot

- Project Database: `.rimsearcher/defs.db`（291,987,456 字节；SHA256 `D9AE2E60…`；14,876 defs；`PRAGMA quick_check` = ok）
- Export Source Path: `C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\RimSearcher_DataMod\defs.db`
- Export Timestamp: 2026-10-20 22:44:44
- Game Version, DLCs, Active Mods & Load Order: RimWorld 1.7.4982；Core / Royalty / Ideology / Biotech / Odyssey / 先驱框架Lite（`mods` 另报 `Unknown` 1,266 defs；Harmony 无 Def）。加载顺序以当时 `ModsConfig.xml` 为准。

## Notes

- 1.7 把 `Assembly-CSharp.dll` 放在游戏根目录，`references/setup.md` 里的默认探测路径（`RimWorldWin64_Data\Managed\`）不适用本机。
- 参考副本统一放 `D:\RimRef`，避免直接指向 Program Files 下随更新变动的文件。
- **CLI 启动（本机 DSH 实测）**：直接跑 `rimsearcher.exe` 会因 `%TEMP%\.net` 不可写报 `Failed to create directory ... Error code: 5`。先设
  `$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = 'D:\PioneerFrameworkLite\.rimsearcher\bundle-cache'` 再运行；缓存约 2.3 MB。
- **DataMod 兼容性（实测）**：`About.xml` 只写 1.6，但在 1.7.4982 下实际可加载并成功导出；若后续 1.7 更新后失效，先查这里。
- **DecompilerServer MCP（已接入）**：二进制 `D:\RimRef\tools\decompiler-server\DecompilerServer.exe`；DSH profile 的 `cordis.patch.yml` 用 `insert:` 注册 `mcp-decompiler`（serverName = decompiler，1.4.0 / 44 tools）。已验证 `ping`、`status`、`load_assembly`、`get_decompiled_source`。
- **MCP 快速加载（每个 DSH 进程一次）**：`load_assembly('D:\RimRef\Assembly-CSharp.dll', alias='rw17')` → `load_assembly('...\PioneerFrameworkLite.dll', alias='pfl')`。上下文是进程内的，重启 DSH 后需重新加载；不要持久化临时 context id。
