# NFexp — PFL 的内容附属包

> 2026-11-17：从空壳转为 **PFL 默认天体内容** 的持有者。PFL 的 `1.7/Defs` 已不再包含任何星系/天体。

## 1. 一句话

PFL 只做机制与骨架，NFexp 收那些「不该塞进框架」的具体填料：星系与天体、行星种类、生态/视觉档案、地图配方，以及后续的载荷 / 部件 / 建筑 / 研究 / 配方。

## 2. 为什么单独成一个 mod

| 理由 | 说明 |
|---|---|
| 框架不背内容 | PFL 的交付件与确定性闸门不应该被「加了一颗行星」牵动 |
| 调试代价 | DMS:NF 体量大、机制与 PFL 几乎无关；NFexp 只依赖 PFL，最小可跑 |
| 未来合流 | 目标形态是并进 DMS:NF，成为完整版的内容层；现在保持可独立演进 |

## 3. 边界（硬约束）

1. **不修改 PFL 的机制与后端**——只用它公开的 API 与 Def；PFL 的后端锁对这里不适用，但「不越界」同样适用；
2. **缺钩子不在这里绕**——需要 PFL 侧新 API 时，先在 PFL 走授权件，NFexp 不发明影子机制；
3. **独立 packageId**：`NewFrontierTeam.NFexp`，存档前缀不与 PFL / DMS:NF 混用。
4. **写 C# 之前先拿授权**（2026-11-28 补）：NFexp 出现第一个 `.cs`（即 `1.7/Assemblies/`）时，
   必须同时有一份 PFL 侧授权件写明它要接哪个公开 API。判据是"文件存在"，不是"以后再说"——
   第 2 条最容易在"这个 UI 很急"的时候被绕过，而载荷这批内容正好是最可能诱人越界的那类
   （11 款物品已在场，但装载入口 / 容量 / 部署在 PFL 侧都还没有，见 §9）。

## 4. 目录结构

```
NFexp/
├── About/About.xml                    依赖 PFL、loadAfter PFL、仅 1.7
├── LoadFolders.xml                    只声明 v1.7 → 1.7/
└── 1.7/
    ├── Defs/
    │   ├── CelestialBodies/
    │   │   ├── CelestialBodies_SolarSystem.xml     Sun / Earth / Moon / ZheSha
    │   │   └── CelestialBodies_PlanetKinds.xml     6 种行星预设
    │   ├── CelestialFramework/
    │   │   ├── CelestialSystems.xml                默认星系 PF_DefaultCelestialSystem
    │   │   ├── CelestialPopulation.xml             程序化星系策略（当前在系统 Def 里被注释停用）
    │   │   ├── CelestialProfiles.xml               地/月生态档
    │   │   ├── CelestialPlanetKindProfiles.xml     行星种类生态档
    │   │   ├── CelestialVisualProfiles.xml         地/月/赭砂/恒星视觉
    │   │   ├── CelestialPlanetKindVisuals.xml      行星种类视觉
    │   │   └── LocalMapRecipes.xml                 本地地图配方
    │   └── Things_item/SpaceProject/
    │       └── DMSNF_SpaceItem.xml                 11 款载荷物品（DMS:NF 冻结副本，契约状态见 §9）
    ├── Patches/                       （空）
    ├── Languages/{ChineseSimplified,English}/Keyed/   （空）
    └── Textures/Things/Resource/Payloads/            11 张载荷贴图（与上面那份 XML 一一对应）
```

暂无 `Assemblies/`：纯内容，不带 DLL。要写 C# 时再建 `Source/NFexp/` 工程并把产物落到 `1.7/Assemblies/`。

## 5. 加载顺序与依赖

```
Harmony → PioneerFrameworkLite → NFexp
```

**NFexp 现在是开局的必需品**：PFL 的 `CelestialWorldBootstrap.DefaultSystem` 仍指向 `PF_DefaultCelestialSystem`，而这个 Def 已经在本包里。

- 装了 NFexp：建世界时启用天体框架；
- 只装 PFL：`CelestialWorldBootstrapHooks.BeginFreshWorld` 找不到系统定义，会打一条警告并**跳过天体框架**（不再中断原版建世界）。

## 6. 部署

```powershell
# 先 PFL（构建 + 同步 1.7 包），再 NFexp
powershell -ExecutionPolicy Bypass -File tools/Deploy-LiteToGame.ps1 -Version 1.7
powershell -ExecutionPolicy Bypass -File tools/Deploy-NFexpToGame.ps1
```

目标目录：`Mods\PioneerFrameworkLite` 与 `Mods\NFexp`。两边都要在场，游戏里才有天体系统。

## 7. 测试与校验的路径约定

- C# 测试把两棵树的 Def XML 都拷进输出目录的 `Defs\`（`PioneerFramework.Tests.csproj` 里两条 `<None Include>`），所以测试代码继续按 `Defs\<相对路径>` 读；
- `CelestialDefaultContentTests.DataPath` 先找 `1.7/Defs`，找不到再找 `NFexp/1.7/Defs`；
- `tools/validate_config.py` 把 `NFexp/1.7/Defs` 加进了扫描列表（本体校验照做，About/Assemblies 这类包级项仍只对 PFL 的 1.6/1.7 生效）；
- Python 工具测试（`test_celestial_payload.py` / `test_map_generation_config.py`）用同样的「先 PFL 后 NFexp」查找。

## 8. 已知的临时状态

- `CelestialSystems.xml` 里的 `<populationPolicy>` 被注释掉了（2026-11-17，为测试方便只保留地月系与赭砂）；恢复 = 取消注释，`PF_DefaultCelestialPopulation` 定义仍在。

## 9. 载荷物品：契约状态与验收（2026-11-28）

11 款载荷（7 民用 + 4 军用）自 DMS:NF **逐字节复制**（XML 与 11 张贴图 md5 全等）。
它们是 PFL 载荷系统（`IPayloadCarrier` / `IPayloadDeployer` / F4-2 / F4-7）的**内容侧样本**，
不是"载荷玩法已经能用"。

**当前断点（都在 PFL 侧，不要在这里补）**：

| 环节 | PFL 现状 |
|---|---|
| 容量 | `IPayloadCarrier.PayloadCapacity` 零实现零读取；零件体系里已有 kg 口径 `ShipStats.cargoCapacity` |
| 过滤 | 无白名单/无类别过滤；`RocketPayloadSpec.defName` 是自由字符串，从不解析成 ThingDef |
| 装载入口 | 零 ITab、零装载 Job；装配器窗口无载荷页；`design.payloads` 除调试字面量外无人写入 |
| 发射消费 | `LaunchFromGround` 不读 `spec.payloads`；物品从未被搬过，谈不上"搬丢" |
| 部署 | `IPayloadDeployer` 零实现（`TryDeployPayload` 释放的是注册的**载荷设计**，语义不同） |

> 推论：**别在 NFexp 里造装载 UI** —— 那是 §3 第 2、4 条禁区。补钩子请走 PFL 授权件。

**实机验收清单**（本仓库无 1.7 自动化通道，目前只能手测；做完把结果记在这里）：

1. 部署 `tools/Deploy-NFexpToGame.ps1`，只装 `PioneerFrameworkLite` + `NFexp`（**不要同时装 DMS:NF**，见 About.xml 的 `incompatibleWith`）；
2. 启动后游戏日志无 `Adding duplicate`、无 `Config error`、无 `Could not load texture`；
3. DevMode 刷出 11 个物品，逐个确认：图标非白图、分类落在 Manufactured；
4. 确认 10 个"灰色占位图"物品（除 `UOSF_Payload` 外全部同图）在列表里靠名字可区分 —— 源仓库也只有占位图，换图属于内容变更，不在本批范围；
5. 存读档一次，确认 11 个 defName 原样回来（`DMSNF_` 前缀未变）。

**校验入口**：`python tools/validate_config.py`（贴图按包分流）、
`python tools/tests/test_nfexp_payload_items.py`（数值基线 / texPath 形态 / 禁引用 / 1.7 集合无重名）、
`python tools/tests/test_config_texture_roots.py`（PFL 看不到 NFexp 贴图、NFexp 看不到 PFL/1.6 贴图）。

