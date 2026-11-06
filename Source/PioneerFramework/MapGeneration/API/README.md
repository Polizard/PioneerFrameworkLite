# 本地地图生成 API

适用于 RimWorld 1.7 和 PFL 框架存档 v4。公开类型位于 `PioneerFramework.MapGeneration`，独立 Mod 可引用 PFL 程序集。天体、地表请求与地图地址见 [通用天体 API](../../Celestial/API/README.md)。

## 地图来源和配方组合

PFL 先确认地块属于就绪的天体地表，且该实例具备唯一有效的 `ICelestialMapGenerationCapability`。能力提供冻结的 `LocalMapGenerationSpec`。自有组件可实现 `ICelestialLocalMapGenerationProperties` 并提供 `Specification`，兼容世界与运行时消费者的 `CompatibilityProfile` 可省略。

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

端口契约同时声明逻辑 `name`、稳定 `typeId` 和 C# 运行时类型。XML 的 `inputs` 和 `outputs` 使用 `name`、`key`、`typeId` 将逻辑端口绑定到会话资源。一个资源只能发布一次，读取方用 `context.Read<T>(name)`，输出方用 `context.Publish<T>(name, value)`。如下输出将本模块的 `mask` 端口绑定至资源 `landing.mask`。

```xml
<outputs>
  <li><name>mask</name><key>landing.mask</key><typeId>Example.CellMask</typeId></li>
</outputs>
```

## 原生候选和初始输入

- `PFL.LocalMap.NativeRules` 在 `Fields` 声明可排除的原生节点。选择器支持 typed `steps`、可选精确 `optionalNames`、`prefixes`、可赋值的 `types`，以及限定用的 `biome`。停用 DLC 导致可选名称未命中时可以正常加载。填错可选名称也会未命中，扩展应测试实际候选。应保留任务或站点要求的步骤，命中必需步骤的排除规则会拒绝最终计划。
- `PFL.LocalMap.Native` 封装明确引用的 `GenStepDef`，可声明 `replaces`。强类型引用必须可解析，使用可选名称的替换仍须通过目标校验。原生 `preventsGenSteps` 约束按有效候选检查。未知原生步骤在计划中标记为 opaque，保留其相对顺序，并使用单步 Rand 作用域。
- `PFL.LocalMap.TerrainReplacement` 在 `NaturalSurface` 末段处理已经生成的地形。配置 `terrains`、`target` 及可选 `rockBiomes`。它会参与殖民地、着陆、遭遇和任务地图的适用计划。
- `PFL.LocalMap.InitialInputs` 在会话内设置岩石、矿物、土壤、植被、动物及洞穴许可等输入。也可在地图 profile 设置 `useDiversityAsInitialInputs=true`，将同一份解析后的 `diversity` 投影为初始模块，与显式初始输入模块二选一。投影在冻结时完成。初始输入只在串行、不可嵌套的目标地图生成会话中生效，且只覆盖该会话里的地图，退出后恢复原生工作数据和作用域。模块配置不会回写原生 Def、层缓存或共享地质数据。已有天气、事件和长期生态仍从原有冻结 profile 读取，世界地表的洞穴许可继续使用相同的世界侧配置。

静态校验在分配原生地图前执行，最终候选依赖和顺序在原生初始化提供完整候选后、首个内容步骤前校验。已适配的原生节点会提供已知的依赖约束。opaque 扩展原生步骤的其他依赖仍由扩展负责。

## 种子、失败与迁移

初始宿主种子在原生构建组件、初始化地块 mutator 与初始天气之前设置。根种子由天体稳定标识、地表生成种子、地块和地图尺寸派生，单步另使用 key、算法版本和天然或用途域。天然模块在不同用途共享相同种子，用途模块将用途纳入派生。原生分配的层 ID、地图 ID 和整个配方的哈希不参与种子派生。模块和原生回调的 Rand 状态通过作用域恢复。对未知 GenStep，只能依赖已声明的顺序与 Rand 边界。外部全局状态、其他随机源、原生版本、内容变化和地图旧布局均在确定性承诺之外。

生成失败会停止执行，记录 owner、tile、阶段、步骤 key、版本、种子和异常，地图请求返回失败并保留身份。PFL 额外日志摘要采用消息级别，原生错误仍按游戏自身规则处理。失败对象没有成功句柄，也不会隐式重建。v4 保存冻结配方、地图身份、生成状态与报告摘要，不保存中间网格或恢复点。生成期间移动的父对象仍保留原始生成地址，恢复和复用检查按失败记录的父对象及已登记地图身份核对。再次请求或存读档不会把同一失败对象判为成功。

外层存档 v1/v2/v3 返回 `UnsupportedSaveVersion`，需要新世界。内建 maps 组件使用独立的配置版本。兼容的旧 XML 地图配置仍可冻结为旧 schema 并在内部生成配方，显式 `localRecipe` 使用 maps 配置 v3。迁移时将旧筛选和地形替换放入配方，将 `diversity` 留在 profile。旧 `PF_MoonSurfaceTerrain` 步骤和 Base_Player 注入已退役。默认物理提供者检查点载荷 v2 和运动协议 `PhysicsContractVersion(1,0)` 延续原有版本。

## 游戏内验收矩阵

以下各项均为 **UNCHECKED**，需要用当前 DLL 启动 RimWorld 1.7。先新建测试世界，分别启用和停用 Odyssey。已安装原生 XML 的无界面候选编译、真实地形网格写入和源隔离回归各有独立测试记录。

| 场景 | 游戏中应观察的结果 |
| --- | --- |
| 殖民地 | 地球和 Temperate 使用各自的天然布局。月球、Barren、Frozen、Greenhouse 按各自配方替换地形并可进入地图。 |
| 舰船着陆 | 普通天体地表正确应用天然配方。启用示例 Mod 后，着陆用途模块根据实际地图中心记录会话内工作数据，可用调试器查看该值。启用 Odyssey 时检查重力飞船保留区和标记。停用时重复常规着陆并检查可选步骤缺失时的表现。 |
| 遭遇 | 在月球或干燥地表确认替换后的地形、进入位置及预期的低生命或无废墟配置。 |
| 任务站点 | 验证任务必需生成内容保留。使用明确冲突的测试配方时，确认失败诊断标识被要求的步骤。 |
| v4 保存与读取 | 成功生成后保存并重载同一世界，检查地图身份与来源报告恢复，以及已有天气、事件和长期生态。 |
| 失败只记日志 | 使用会抛异常的测试模块，检查失败结果、日志身份和阶段。再次请求同一地址并保存重载，确认失败身份仍然有效。检查框架是否遵循只记日志的行为。 |

