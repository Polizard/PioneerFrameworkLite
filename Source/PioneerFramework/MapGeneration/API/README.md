# 本地地图生成 API

适用于 RimWorld 1.7 和 PFL 框架存档 v4。公开类型位于 `PioneerFramework.MapGeneration`，独立 Mod 可引用 PFL 程序集。天体、地表请求与地图地址见 [通用天体 API](../../Celestial/API/README.md)。

## 地图来源和配方组合

PFL 先确认地块属于就绪的天体地表，且该实例具备唯一有效的 `ICelestialMapGenerationCapability`。能力提供冻结的 `LocalMapGenerationSpec`。自有组件可实现 `ICelestialLocalMapGenerationProperties` 并提供 `Specification`。

为 `CelestialMapProfileDef` 指定 `localRecipe`，按需配置 `purposeRecipes`。用途可取 `Colony`、`Landing`、`Encounter`、`Quest`。站点取 `Quest`，PFL 舰船落点取 `Landing`，玩家定居点和玩家殖民地生成器取 `Colony`，其余适用地图取 `Encounter`。每次生成合并天然配方、对应的一份用途配方和实际原生候选。天然配方可以为空，此时使用原生生成。以下引用需要启用相应的配方 Def 和处理器。

```xml
<PioneerFramework.Celestial.CelestialMapProfileDef>
  <defName>ExampleMaps</defName>
  <localRecipe>ExampleNatural</localRecipe>
  <purposeRecipes>
    <li><purpose>Landing</purpose><recipe>ExampleLandingContent</recipe></li>
  </purposeRecipes>
</PioneerFramework.Celestial.CelestialMapProfileDef>
```

原生 `MapGenerator.GenerateMap` 负责地图生命周期。PFL 接入天体拥有的普通行星地表，排除口袋图、地下图和渲染世界的生成器。对其他生成器可在注册期调用 `LocalMapSourceResolver.RegisterGenerator(id, participate)`，或通过冻结规格的 `NativeGeneratorId` 指定。注册不能扩大至上述排除范围。世界层生成、轨道和其他天体行为不经此 API。

## 模块注册与版本

在独立 Mod 的 `Verse.Mod` 构造函数中调用 `LocalMapGenerationModules.Register`，或向其 `Registry` 注册 `GenerationModuleRegistration`。游戏加载 Mod 类后才处理 Def XML，`LocalMapRecipeDef.ConfigErrors` 需要已注册的处理器。注册表在首个天体运行时创建时封存。注册包含稳定的 handler ID、配置版本、支持的算法版本、配置验证函数、工厂和可选端口契约。处理器按配置版本解释字节，算法版本参与种子派生。改变字节解释或改变生成算法时分别升级相应版本，并继续注册需要读取的版本。

每个 XML 步骤声明 `key`、`handlerId`、`configVersion`、`algorithmVersion`、`stage`、`domain`，以及 `properties Class="…"`。自有属性类派生自 `LocalMapModuleProperties`，通过 `FreezeConfiguration` 产生可验证、可保存的字节。可以接收 `CelestialGenerationContext` 使用实例生成参数。校验和工厂可能先于模块执行调用，工厂以及 `INativeMapPlanContributor.Contribute` 只提供对象或元数据，禁止修改 Map、世界及 Def。

`IMapGenerationModule.Execute` 通过 `MapGenerationContext` 获取冻结参数、种子和服务。原生适配器提供 `INativeMapGenerationServices.Map`。有收尾逻辑的模块实现 `IMapGenerationPostInitializer`，在成功路径中生成与收尾各调用一次，并复用同一个模块实例。样例包含启动期注册、XML 配置和读取实际 `Map` 的处理器。见 [CelestialCompSample](../../../../Examples/CelestialCompSample/README.md)。

## 阶段、排序与端口

| 阶段 | 常见职责 |
| --- | --- |
| `Fields` | 生成输入、海拔、肥力、洞穴数据 |
| `NaturalSurface` | 岩体、地形、道路和地表替换 |
| `Content` | 站点、结构和场景内容 |
| `Populate` | 植被、动物和地图收尾 |

模块在阶段内可声明 `before`、`after`，值为步骤 key。端口依赖也会产生排序边。用途模块必须在 `Content` 或 `Populate`，不能移除天然节点、提供天然网格或设置初始生成覆盖。原生自动适配节点的 key 形如 `native/Terrain/0`。引用原生节点前应确认该节点在所有目标生成器的最终候选中存在。缺失节点、排序环、跨阶段冲突、重复输出和不匹配的类型均会拒绝编译。

端口契约同时声明逻辑 `name`、稳定 `typeId` 和 C# 运行时类型。XML 的 `inputs` 和 `outputs` 使用 `name`、`key`、`typeId` 将逻辑端口绑定到会话资源。一个资源只能发布一次，读取方用 `context.Read<T>(name)`，输出方用 `context.Publish<T>(name, value)`。输入端口必须由**同一组合 recipe 内某个步骤的输出**提供：静态校验（配置/冻结阶段）即拒绝没有提供者的输入（`Unresolved recipe input`），原生地图宿主不会注入外部资源；`MapGenerationCompiler.Compile` 的 `externalInputs` 形参只保留给纯宿主/测试 API，XML 与原生地图路径不可用。如下输出将本模块的 `mask` 端口绑定至资源 `landing.mask`。

```xml
<outputs>
  <li><name>mask</name><key>landing.mask</key><typeId>Example.CellMask</typeId></li>
</outputs>
```

## 原生候选和初始输入

- `PFL.LocalMap.NativeRules` 在 `Fields` 声明可排除的原生节点。选择器支持 typed `steps`、可选精确 `optionalNames`、`prefixes`、可赋值的 `types`，以及限定用的 `biome`。停用 DLC 导致可选名称未命中时可以正常加载。填错可选名称也会未命中，扩展应测试实际候选。会话父对象为 `Site` 时，`GenStep_ScenParts` 与 `GenStep_FindPlayerStartSpot` 被标记为站点必需内容：NativeRules 或用途选择器移除它们、或原生 `preventsGenSteps` 移除它们，都会以 `RequiredNativeStepConflict` 拒绝最终计划。
- `PFL.LocalMap.Native` 封装明确引用的 `GenStepDef`，可声明 `replaces`。强类型引用必须可解析，使用可选名称的替换仍须通过目标校验。原生 `preventsGenSteps` 约束按有效候选检查。未知原生步骤在计划中标记为 opaque，保留其相对顺序，并使用单步 Rand 作用域。
- `PFL.LocalMap.TerrainReplacement` 在 `NaturalSurface` 末段处理已经生成的地形。配置 `terrains`、`target` 及可选 `rockBiomes`。它会参与殖民地、着陆、遭遇和任务地图的适用计划。
- `PFL.LocalMap.InitialInputs` 在会话内设置岩石、矿物、土壤、植被、动物及洞穴许可等输入。也可在地图 profile 设置 `useDiversityAsInitialInputs=true`，将同一份解析后的 `diversity` 投影为初始模块，与显式初始输入模块二选一。投影在冻结时完成。初始输入只在串行、不可嵌套的目标地图生成会话中生效，且只覆盖该会话里的地图，退出后恢复原生工作数据和作用域。模块配置不会回写原生 Def、层缓存或共享地质数据。已有天气、事件和长期生态仍从原有冻结 profile 读取，世界地表的洞穴许可继续使用相同的世界侧配置。

静态校验在分配原生地图前执行，最终候选依赖和顺序在原生初始化提供完整候选后、首个内容步骤前校验。已适配的原生节点会提供已知的依赖约束。opaque 扩展原生步骤的其他依赖仍由扩展负责。

## 种子、失败与迁移

初始宿主种子在原生构建组件、初始化地块 mutator 与初始天气之前设置。根种子由天体稳定标识、地表生成种子、地块和地图尺寸派生，单步另使用 key、算法版本和天然或用途域。天然模块在不同用途共享相同种子，用途模块将用途纳入派生。原生分配的层 ID、地图 ID 和整个配方的哈希不参与种子派生。模块和原生回调的 Rand 状态通过作用域恢复。对未知 GenStep，只能依赖已声明的顺序与 Rand 边界。外部全局状态、其他随机源、原生版本、内容变化和地图旧布局均在确定性承诺之外。

生成失败会停止执行，记录 owner、tile、阶段、步骤 key、版本、种子和异常，地图请求返回失败并保留身份。PFL 额外日志摘要采用消息级别，原生错误仍按游戏自身规则处理。失败对象没有成功句柄，也不会隐式重建。v4 保存冻结配方、地图身份、生成状态与报告摘要，不保存中间网格或恢复点。生成期间移动的父对象仍保留原始生成地址，恢复和复用检查按失败记录的父对象及已登记地图身份核对。再次请求或存读档不会把同一失败对象判为成功。

失败结算会回收半成品地图：原生在内容与收尾之前已把 Map 注册进 `Game.Maps`，PFL 在算出失败原因后先 best-effort 移除本次会话注册的那张 Map（不触碰其他 Map），再把失败写入站点记录。因此失败身份的 `MapId` 为空，`Failure` 与失败 Generation 保留，operation 不进入退役历史；父对象保留，同地址再次请求仍以 `MapGenerationIncomplete` 拒绝，保存重载后仍然有效。移除失败（原生 deinit 抛错或 Map 仍在）不掩盖原始异常：清理诊断追加在原始失败之后，记录保留当时已知的 `MapId`。

日志归因只覆盖 PFL 外包给原生回调的窗口：PFL 用 `Log.Error` 前缀观察原生失败，但只有在自身包住的原生回调窗口内（`ConstructComponents`、mutator `Init`、`FinalizeInit`、`MapComponent.MapGenerated`、`Scenario/MapParent.PostMapGenerate`、`InitialWeather`、`ExtraInit`，以及 PFL 自身的 `RockNoises.Init` 调用）与被包装的原生 GenStep 执行期间、且发生在宿主线程上的 `Log.Error` 才记为本次生成失败。窗口外的日志（模块自身代码、其他 Mod、异步或迟到日志、生成结束之后）一律放行，不再 poison 地图请求。窗口只覆盖 PFL 自己编译进计划的 native 节点；第三方若在自有 handler 里直接调用 `INativeMapGenerationServices.GenerateNative`，不获得该归因语义。PFL 自己的失败摘要仍用消息级别输出，原生错误按游戏自身规则记录。

外层存档 v1/v2/v3 返回 `UnsupportedSaveVersion`，需要新世界。内建 maps 组件配置版本为 4：`CelestialMapProfileDef` 只接受 `localRecipe`（+ 可选 `purposeRecipes`/`diversity`/`mapGenerator`），旧的 `skipStepNames/skipStepPrefixes/skipStepTypes/replaceTerrains/replacementTerrain/biomeRules` 作者字段已删除，必须改写为 `PFL.LocalMap.NativeRules` / `PFL.LocalMap.TerrainReplacement` / `PFL.LocalMap.InitialInputs` 配方步骤。旧 v4 档中的 maps 配置 v1/v2/v3 不再解码，恢复时以 `UnsupportedConfigurationVersion` 显式拒绝（本版不迁移旧档）。旧 `PF_MoonSurfaceTerrain` 步骤和 Base_Player 注入已退役。默认物理提供者检查点载荷 v2 和运动协议 `PhysicsContractVersion(1,0)` 延续原有版本。

地形数值语义另有独立版本锚：`CelestialPlanetTerrain.AlgorithmVersion` 唯一决定 Sample/SelectBiome/SurfaceMoisture/SelectOceanTiles/Field 的语义。地表构造头与 v4 的 surface 记录都持久化该版本并在读档时校验：不匹配即 `UnsupportedTerrainAlgorithmVersion`（框架 v4 侧由 codec 返回 `InvalidSaveDocument`），世界层生成/再生成前也会先比对 header 版本。引入该字段之前写出的 v4 档缺少此字段，按 `LegacyVersionWithoutHeaderField` 读取（当前版本仍为 1 时旧档可读）；一旦这些公式的数值语义变化必须 +1，此后旧档被显式拒绝，而不是按新公式静默重算。恢复以原生 Header 的保存值为准，并与框架 v4 的 surface 记录交叉校验，不假设当前常量；该字段使 v4 payload 新增一个 JSON 属性，新构建可读旧 v4 档（缺字段按 Legacy），旧构建读新档会因未知字段显式拒绝（单向兼容）。

确定性的派生值由自动化基线看守：根种子、每步种子、冻结配方的 canonical 编码与快照哈希、地形采样冻结为字面量（`PioneerFramework.Pure.Tests/LocalMapGoldenTests.cs`、`PioneerFramework.Tests/MapGenerationGoldenBaselineTests.cs`），改动派生公式或字段顺序会使回归变红；此时必须先升相应版本（地形算法版本、地图记录版本或组件配置版本）并有意更新基线，不能直接改期望值。

## 已知限制与有意取舍（2026-11-06 P2 收口）

本小节记录 P2 收口的最终状态：**P2-2/P2-4/P2-6/P2-7 已修复**（写明边界与代价）；**P2-8/P2-9/P2-10 仍是当前实现有意保留的行为边界**，不是待办缺陷。改动任一条前先读对应代码与人工验收项。

- **原生步骤按语义阶段重排（P2-2，已修复·2026-11-06 第二批）**：`GenStep_RockChunks`（vanilla order 970）与 `GenStep_ScatterGeysers`（950）原先被归入 `NaturalSurface`，现改归 `Populate`，跨阶段顺序因此回到 vanilla（`Content` → `Plants` → `Geysers` → `Chunks` → …）；同阶段内仍由"上一原生节点"链保持相对顺序，未适配步骤继续标记为 opaque 并保留各自原生相对顺序。**有意副作用**：这两步现在的阶段高于 `NaturalSurface`，故其域为 `Purpose`，单步种子随用途派生——地图布局相对旧构建会变化（由原生计划顺序与用途域种子的定向用例看守）。Earth/Temperate 的"纯原生配方"与 vanilla 调度仍可能有细节差异，游戏内对照验收保持 **UNCHECKED**。
- **diversity 双通道的一致性校验（P2-4，已修复·2026-11-06 第二批）**：组件 profile 的 `diversity` 仍是长期生态的单真源；`useDiversityAsInitialInputs=true` 或显式 `PFL.LocalMap.InitialInputs` 仍只是会话内生成期输入的投影。新增静态校验：profile 有 `diversity` 时，显式 `InitialInputs` 模块的冻结字节必须与 profile 投影**完全一致**，否则 `CelestialMapProfileDef.ToProfile()` 以 `InvalidMapProfile` 拒绝（自动投影 key `profile.initial-inputs`/`legacy.initial-inputs` 跳过）；没有 `diversity` 时任意 `InitialInputs` 仍允许。双份冻结仍存在（profile 长期真源 + 会话输入），但不再允许静默分叉。`weather`/`conditions`/`incidents` 永远只走 profile：写进初始输入模块会被显式拒绝。
- **配方与记录版本独立（P2-6，已修复·2026-11-06 第二批）**：`LocalMapGenerationRecord.CurrentVersion = 2`（`LegacyVersion = 1`）。v2 的 `RecipeHash` 是对冻结 `FrozenRecipe` 字符串 UTF-8 字节的 SHA-256，**不再依赖 recipe codec 重编码**；`Validate` 按版本分支：v1 记录仍走 `Decode` + canonical `SnapshotHash` 复算（旧 v4 档可读的代价），v2 记录只比较上述字符串哈希，未知版本拒绝。`LocalMapGenerationCodec.CurrentVersion`（canonical 编码格式）仍独立版本化；v4 envelope 字段集与编码未变。旧构建读 v2 记录会显式拒绝（单向兼容）；配方在组件载荷与逐站点 record 各存一份仍属 provenance 设计（不保存中间网格或恢复点）。
- **站点跨 tile 迁移（P2-7，已修复·2026-11-06 第二批）**：`CelestialMapSiteIndex.Bind` 现在支持"父对象移动后的同一站点迁移"：同实例、同层、同 `MapParentId` 且 `MapId` 兼容（旧记录无 `MapId` 或双方 `MapId` 相等）时，把旧 key 的站点迁到新地址，合并 `OperationIds`/`RetiredOperationIds` 并继承 `Generation`/`Failure`。顺序保证：先在忽略被迁移记录的前提下做跨 key 唯一性预检，通过后才移除旧 key（拒绝时索引保持原样，无半迁移）。判据有意收紧：旧站点仍有 live `MapId` 而新记录 `MapId` 为空时**不迁移**（保留"地图未通知丢失必须拒绝保存"的防线）；跨实例、跨层或 `MapId` 不同仍拒绝。`CelestialNativeMapLifecycle` 的"先换 tile 后移除地图"时序仍按旧地址早退（既有边界）。Odyssey 重力飞船换点落地场景的游戏内验收保持 **UNCHECKED**。
- **启动契约预检的范围（P2-8，保留；用户已放弃修复）**：`CelestialNativeCompatibility.ExpectedPatches()` 覆盖 bootstrap、原生层生命周期、保存入口与本地地图 Hook；`CelestialPlanetMapHooks` / `CelestialPlanetMapEcology` 在类型初始化时直接安装补丁，目标缺失即在加载期抛错（fail-fast），**不在 `ExpectedPatches` 表内**，因此 `ValidateNativeContracts()` 的报告看不到它们——它们的完整性由加载期失败保证，而不是由报告项保证。
- **嵌套原生生成一律拒绝（P2-9，保留；未实现放行）**：PFL 会话期间再次进入原生 `MapGenerator.GenerateMap`（含 pocket/underground 等平时由 `LocalMapSourceResolver` 放行的地图）会抛 `NestedNativeMapGeneration`；这是当前的有意单例保护，不是可通过配置绕过的限制。是否放行嵌套调用需单独评估（待用户决定），未改动。
- **请求入口优先在宿主线程（P2-10，保留；未改代码）**：`RequestMap` 的准备阶段会读取原生 `World`/`WorldObjects`；工作线程调用返回 Pending 并由宿主泵送（可用），但调用方应优先在宿主线程发起请求，避免把跨线程时序问题当成可重试的瞬时错误。

## 游戏内验收矩阵

以下各项均为 **UNCHECKED**，需要用当前 DLL 启动 RimWorld 1.7。先新建测试世界，分别启用和停用 Odyssey。已安装原生 XML 的无界面候选编译、真实地形网格写入和源隔离回归各有独立测试记录。

| 场景 | 游戏中应观察的结果 |
| --- | --- |
| 殖民地 | 地球和 Temperate 使用各自的天然布局。月球、Barren、Frozen、Greenhouse 按各自配方替换地形并可进入地图。 |
| 舰船着陆 | 普通天体地表正确应用天然配方。启用示例 Mod 后，着陆用途模块根据实际地图中心记录会话内工作数据，可用调试器查看该值。启用 Odyssey 时检查重力飞船保留区和标记。停用时重复常规着陆并检查可选步骤缺失时的表现。 |
| 遭遇 | 在月球或干燥地表确认替换后的地形、进入位置及预期的低生命或无废墟配置。 |
| 任务站点 | 验证任务必需生成内容保留。使用明确冲突的测试配方时，确认失败诊断标识被要求的步骤。 |
| 失败地图清理 | 用会抛异常的测试模块让生成在注册地图之后失败：半成品地图不应留在世界中，父对象保留，站点保持失败身份；再次请求同地址仍被拒绝，保存重载后仍为失败身份。 |
| 保存预检 | 人为制造地图或地表身份不一致（例如移除已登记的原生地图、替换层对象）后按保存：应在写档前被拒绝并提示具体错误码，目标文件不被部分写入；解除不一致后保存成功。 |
| 地形算法版本 | 正常新世界与新档无感。把 header 或 v4 surface 记录里的 terrainAlgorithmVersion 改成非当前值后读档，应显式拒绝（`UnsupportedTerrainAlgorithmVersion`），不得按新公式静默重算世界层、地图地质或水面判定；当前版本仍为 1，缺该字段的旧 v4 档应可读。 |
| 确定性基线 | 非游戏内项：根种子、每步种子、配方 canonical 编码与快照哈希、地形采样由自动测试以字面量冻结；改公式或字段顺序会使回归变红，需按版本策略有意更新。 |
| v4 保存与读取 | 成功生成后保存并重载同一世界，检查地图身份与来源报告恢复，以及已有天气、事件和长期生态。 |
| 失败只记日志 | 使用会抛异常的测试模块，检查失败结果、日志身份和阶段。再次请求同一地址并保存重载，确认失败身份仍然有效。检查框架是否遵循只记日志的行为。 |
| 日志窗口 | 窗口内错误（组件构建、mutator 初始化、FinalizeInit、MapGenerated、PostMapGenerate、初始天气、extraInit）应导致本次生成失败；窗口外（模块自身代码、其他 Mod、异步或生成结束后的 `Log.Error`）不应使本次地图请求失败。 |

