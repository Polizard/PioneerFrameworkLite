# 通用行星框架：独立 Comp 示例

本目录展示一颗固定天体、一类范围天体，以及每个实例各自持有状态的 C# 组件。组件只响应 `SampleIncrement` 事件。示例提供几何、外观和运动绑定，地表与环境能力由其他组件按需配置。

## 1. 构建并安装示例

在 PioneerFrameworkLite 仓库根目录执行：

```powershell
. ./tools/Use-ProjectDotnet.ps1
dotnet build Examples/CelestialCompSample/CelestialCompSample.csproj -c Release
```

Release 输出为 `Examples/CelestialCompSample/Assemblies/PFL.CelestialCompSample.dll`。Debug 输出为 `Examples/CelestialCompSample/bin/Debug/`，不进入可加载目录。示例引用主工程，运行时使用 PFL 已交付的程序集。示例项目单独编译，只引用框架公开契约；其工程引用设为 `Private="false"`，运行时从已启用的 PFL 取得框架程序集。

将本目录的 `About`、`Assemblies`、`Defs`、`Patches`、`Languages` 放入游戏 `Mods/PFL_CelestialCompSample/`，在 Mod 列表中将示例排在 Harmony、PioneerFrameworkLite 后方，重启游戏并新建世界。PFL 自身应使用包含通用行星框架的同版本源码与 Release DLL。

## 2. 复制模板并配置参数

[SampleCelestialBodies.xml](Defs/SampleCelestialBodies.xml) 是完整可加载配置：

- `PF_CounterSampleBase` 是抽象 XML 模板。
- `PF_CounterSampleFixed` 继承固定半径 500000 米、计数初值 3。
- `PF_CounterSampleRanged` 使用闭区间半径 250000～650000 米、计数初值 1～9。

在现有 `<comps>` 内加入计数组件：

```xml
<li Class="PFL.CelestialCompSample.CompProperties_SampleCounter">
  <key>counter</key>
  <initialCount><value>3</value></initialCount>
</li>
```

配置一类实例时改为：

```xml
<initialCount><min>1</min><max>9</max></initialCount>
```

同一参数只采用一种形式：`value`，或同时提供 `min` 与 `max`。两种形式混用、缺少边界、倒置范围和负计数会被拒绝。采样上下界均包含。实例的生成种子、生成路径、组件 key 和参数名共同决定结果；保存的是解析后的数值，读档直接恢复。

替换完整组件列表时使用 `<comps Inherit="False">`，避免 XML 继承追加出重复 key。每个天体内的 key 必须唯一，并应在发行后保持稳定。`category` 与 `tags` 仅作元数据；需要的能力应显式加入对应 comp。

## 3. 将模板加入生成配置

[SampleSystem.xml](Patches/SampleSystem.xml) 向 `PF_DefaultCelestialSystem` 增加两项。`parentEntry` 表示父天体关系，初始化引用通过显式 key 绑定实际实例：

```xml
<li>
  <key>z_counterFixed</key>
  <bodyDef>PF_CounterSampleFixed</bodyDef>
  <count><value>1</value></count>
  <parentEntry>root</parentEntry>
  <initializationReferences>
    <li><key>frameOrigin</key><entry>root</entry><scope>Parent</scope></li>
    <li><key>primaryLight</key><entry>primary</entry><scope>Global</scope></li>
  </initializationReferences>
</li>
```

条目 `count` 也支持固定值或闭区间。父条目产生多个实例时，子条目分别在各父实例下展开。`Global`、`Parent`、`Self`、`Siblings` 控制引用的查找范围；存在多个候选时需指定 `index`。系统的 `rootSurfaceEntry` 选择唯一主地表实例，所选模板必须有 `surface` 能力。

主地表在原生世界初始化时生成。其他地表通过 `CompProperties_Surface.generationMode` 选择 `OnDemand` 或 `Creation`；访问才生成的地表需要显式调用 `RequestSurface`。殖民地地图通过 `RequestMap` 单独请求。

## 4. 在游戏中检查

1. 启用开发者模式，新建带示例 Mod 的世界，打开 PFView，检查“定值计数天体”和“范围计数天体”两个条目。
2. 打开开发者动作菜单，搜索分类 `PFL Celestial Sample`，执行 `Report counters`，在日志中查看实际实例 ID、组件 key 和计数。固定实例应为 3。
3. 执行 `Increment first counter`，取消暂停并推进片刻，再执行 `Report counters`。按句柄顺序选出的第一个计数组件增加 1，其余实例保持原值。
4. 保存、退出并读档，再报告计数。计数应保持保存时的值。计数更新只由明确事件触发。

关闭示例 Mod 后加载含其组件的存档，会保留未知配置与状态载荷，并报告受影响天体的处理器缺失。重新启用相同版本示例、重启并加载保留该载荷的存档，可继续恢复。框架旧格式存档不在本版兼容范围内。

## 5. C# 组件生命周期与保存

| 文件 | 责任 |
| --- | --- |
| [SampleCounterComp.cs](SampleCounterComp.cs) | XML 属性、生成时采样、不可变配置、实例状态与事件处理 |
| [SampleCounterCodec.cs](SampleCounterCodec.cs) | 公开工厂、配置解码、状态恢复、启动期注册 |
| [SampleCounterDebugActions.cs](SampleCounterDebugActions.cs) | 使用真实服务排入事件、读取实际计数 |

属性继承 `CelestialCompProperties`；组件继承 `CelestialBodyComp`。`Resolve` 收到已经按组件 key 划定范围的 `CelestialGenerationContext`，通过 `SampleInt` / `SampleDouble` 采样，返回独立 `ICelestialResolvedProperties`。

`OnInitialized` 仅初始化新实例；恢复先创建组件并恢复状态，再用 `OnAttached` 绑定资源与上下文，最后通知一次 `OnRestored`。`OnReady` 在物理注册确认后运行。`Handle` 处理明确事件；`Advance` 接收已提交区间。`CanRemove` 可提供额外移除阻碍，`OnRelease` 应释放组件独占资源。

本例提供能力 `sample.counter`，采用 `EventsOnly`。可用 `requires` 声明能力依赖，`before` / `after` 声明组件 key 的执行顺序。缺少能力、重复 key、依赖环与独占槽冲突会被拒绝。框架按照声明组装组件。

注册使用稳定处理器名 `PFL.Sample.Counter`。配置版本和状态 schema 版本均为 1，两个载荷分别保存 `InitialCount` 和 `Counter`。增加不兼容字段时应显式处理版本；未知或不支持的载荷由框架保留，组件不能用默认值覆盖它们。

第三方扩展应在首次世界初始化或读档恢复前注册到 `CelestialBuiltinCompRegistry.Global`。注册表封闭后拒绝新增处理器；保存恢复通过处理器工厂创建实际组件，无需重新采样 XML。

## 6. 服务、身份与动态创建

```csharp
var services = CelestialWorldServices.TryGetCurrent();
if (services == null) return;
var query = services.Query;
var epoch = services.Bodies.OpenEpoch(query.WorldSessionId);
if (!epoch.Success) return;
// parent、light 由业务明确选定。SampleCreation.cs 提供可编译的完整创建函数。
var operation = SampleCreation.CreateProbe("create-probe-1", epoch.Value, parent, light);
// 保存 OperationId 并轮询 GetOperation；Applied 后才使用 InstanceIds。
```

批量创建使用 `CelestialCreateBatchRequest` 与数据条目 `CelestialCreateItem`，指定模板、稳定 key、父实例及初始化引用。框架在内部解析模板和组件工厂。正式契约见 [API 正文](../../Source/PioneerFramework/Celestial/API/README.md)。

每世界最多 8 个开放代次（初始包含 0），每代次最多 4096 个 pending+终态回执、256 个 pending。任务组完成后用 `CloseEpoch(session, epoch)` 显式关闭。存在 pending 时拒绝关闭。关闭后旧请求永久返回 `OperationExpired`，读档后仍拒绝；不能换成新代次自动重放旧动作。新代次用于新发起的明确任务。创建通知阶段的默认命令使用创建代次；Ready 后的新动作必须显式调用 `Context.SubmitNewAction(actionEpoch, command)`。`SampleCreation.SubmitComponentAction` 展示完整调用，不自动换代重放旧动作。

以 `CelestialInstanceId` 关联天体；兼容句柄仅用于本世界现有业务适配。模板 `defName` 可以被多个实例共享。实例 ID 与句柄在移除后均保留占用历史。`WorldSessionId` 在读档后改变，延迟动作应重新验证会话、目标 ID 与描述修订。

操作 ID 对应一次明确请求。相同 ID、相同内容返回原回执；相同 ID、不同内容报告 `OperationConflict`。读取描述、回执和物理快照不会主动推进模拟。

地图地址由实例 ID、层 ID、tile ID 及可选 MapParent ID 组成。已知地图或父对象缺失时会返回身份错误，避免替换已有内容。移除前应查询 `CanRemove`：主地表、子天体、地图、飞船、运输任务、巨构锚点及扩展引用均会阻止移除。

扩展持有外部关联时，调用 `RegisterReference` 登记稳定引用名、实际实例 ID、引用种类和 owner。保留其返回的会话释放凭据，解除关联时调用 `ReleaseReference`；旧会话凭据不能解除新关联。

## 7. 内置组件与边界

| XML 属性类 | 用途 |
| --- | --- |
| `CompProperties_SphericalBody` | 米制物理半径，支持固定/范围值 |
| `CompProperties_ExternalPhysicsBinding` | 物理提供者、模式、版本、初始化载荷与 GM 参数 |
| `CompProperties_Surface` | 球形地表层、细分、尺度和生成时机 |
| `CompProperties_BiomeGeneration` / `CompProperties_MapGeneration` | 世界生成和地面地图 profile |
| `CompProperties_Atmosphere` / `CompProperties_LightSource` / `CompProperties_ExistingEnvironment` | 现有大气、光源及昼夜/真空规则 |
| `CompProperties_CelestialVisual` / `CompProperties_AtmosphereVisual` / `CompProperties_CloudsVisual` / `CompProperties_StellarVisual` | 显式外观能力 |

上表类位于 `PioneerFramework.Celestial`。尺寸使用米，GM 使用 m³/s²，大气密度使用 kg/m³；当前物理模型沿用 1 个物理 tick = 1 秒。原生地表显示半径、地图格数与物理半径分别保存，显示尺度不会反写物理描述。

本例的物理模式为 `PFL.ExistingEphemeris` / `existing.circular.v1`，复用 PFL 原解析模型。`initializationPayload` 为提供者定义的版本化 UTF-8 JSON 经 Base64 编码后的字符串；示例中两颗天体使用不同轨道初始化，GM 明确为 0。其他提供者解释自己的载荷，并通过 `ICelestialPhysicsBridge` 提交同一参考系下的位置、速度、姿态和角速度。组件通过服务采样运动状态。

默认提供者的载荷可在仓库根目录使用作者工具转换：

```powershell
python tools/celestial_payload.py decode orbit.base64 --output orbit.json
python tools/celestial_payload.py encode orbit.json --output orbit.base64
```

把 XML 中 `initializationPayload` 的内容复制到 `orbit.base64`；编辑解码后的 JSON，再将编码结果放回该元素。工具默认校验 `existing.circular.v1` 的 `orbitRadius`、`orbitPeriod`、`initialPhase`、三维非零 `orbitNormal` 和正数 `dayLengthTicks`，可选 `bodyType` 与 `frameOriginReference` 保留为提供者字符串。错误包含具体字段名；未知字段、重复字段、非有限数值及不合法组合会被拒绝。半径单位为米，周期为秒，相位为弧度。JSON 的空白、字段顺序和原始 UTF-8 字节均保留，转换不自动重写 XML，也不更新其他文件；只有 `--output` 指定的文件会写入，省略它则输出到标准输出，输入 `-` 表示读取标准输入。

其他提供者的载荷保持独立格式。使用 `--mode opaque` 可对任意二进制文件执行相同的 `encode` / `decode` 转换，工具不解释或校验其内容；该选项不替代提供者自己的验证。

地表可登陆能力、环境能力与外观能力分别配置。当前框架沿用已有环境规则；环世界等巨构继续使用其独立框架。

需要未来星历的规划可查询提供者的可选 `ICelestialPhysicsForecastProvider`，或调用 `CelestialBodyManager.ForecastBody(handle, seconds)`。返回的 `CelestialBodyForecast.BasedOn` 标明预测依据的已提交版本；预测保持独立于正式状态采样，不能用于提交物理时间或结算燃料。缺失预测能力返回 `ForecastUnavailable`。默认适配器沿用现有解析公式；挂起注册和私有候选成员不会进入公开预测。

## 8. 校验与常见错误

在仓库根目录运行 `python tools/validate_config.py` 校验运行内容与示例，使用固定 SDK 构建示例，并执行 `CelestialExternalCompTests`。自动化覆盖公开程序集、原生 XML 继承、固定/范围数值、独立状态与载荷恢复；PFView 外观、开发者菜单操作和实际存档重载仍需游戏内检查。

`python -m unittest discover -s tools/tests -v` 运行 Python 工具回归；本地 `tools/test-all.ps1` 与 CI 均调用此命令。本地缺少 Python 或使用 `-SkipConfig` 时，该门禁明确记为 WARN / UNCHECKED。

| 错误码 | 排查方式 |
| --- | --- |
| `MissingCompKey` / `DuplicateCompKey` | 检查每个天体内的组件 key 与 XML 继承结果 |
| `InvalidCounterInitialRange` | 检查 value 或 min/max 形式、边界与非负整数要求 |
| `MissingCapability` / `CompDependencyCycle` | 补齐显式组件与依赖顺序 |
| `ProviderUnavailable` | 检查物理提供者是否在初始化前注册 |
| `RevisionConflict` / `StaleSession` | 获取当前会话和修订，再为仍有效的原逻辑请求使用原 ID 重试；新明确动作才分配新 ID |
| `HandlerMissing` / `UnsupportedStateVersion` | 启用匹配扩展或提供显式版本迁移，保留原载荷 |
| `MapIdentityMismatch` / `SurfaceIdentityMismatch` | 检查实际实例、原生层和保存对象的关联 |

关于主工程门禁、Release DLL 和冻结 1.6 的规则，见仓库根目录的构建说明。

原生地图正常退出会留下明确的身份退役记录，旧地图请求不能绑定后续替代地图。保存或读档时发现对象缺失仍按身份不匹配处理，不据此自动生成。缺失模块导致的 Blocked 会另外保存受阻前生命周期；模块恢复后，经原生绑定、物理检查点与组件恢复完成再恢复该状态。业务失败产生的 Blocked 保留原诊断。

将纬度带天体选为主地表时，可用 biome profile 的 `rootOnlySteps` 明确列出 Factions/Roads/Features 等原生全局步骤；这些步骤只在主地表执行，额外同模板天体不会重复执行。

框架存档现为 v4，旧 v1/v2/v3 明确拒绝（`UnsupportedSaveVersion`，不迁移、不重写旧档）；默认解析提供者检查点载荷 v2。`failurePolicy` 默认 BlockBody，显式 DisableOptional 仅隔离安全的非关键闭包；已停用状态和原始载荷保存后不会自动激活。预测除 BasedOn 外还带 Source，调用者须验证 ProviderId、契约版本及 PredictionSchemaVersion（当前为1）。

`SampleCreation.CreateProbe` 的可选 radiusMeters 参数演示纯数据 ParameterOverrides。也可用 `new CelestialParameterOverride("shape", "radiusMeters", Min: 250000, Max: 650000)` 覆盖范围；参数名必须对应组件的显式采样键。内建已知配置版本严格拒绝缺失、拼错及重复字段，原始错误载荷保留。

## 8. 本地地图模块

[SampleLandingModule.cs](SampleLandingModule.cs) 展示配置版本 1 和算法版本 1 的用途模块。样例 Mod 构造时注册验证器与工厂，以便游戏加载 [配方 Def](Defs/LocalMapRecipe.xml) 时可校验 handler。XML 字段 `eastOfCenter` 冻结为四字节整数，模块在 `Content` 阶段从实际 `Map.Center` 计算指定格，验证地图边界后写入本次生成的原生工作数据 `PFL.CelestialCompSample.landingCell`。工作数据在会话结束后清除，适合作为后续步骤的输入。样例不改变地图外观。

样例的 [着陆配方 patch](Patches/LocalMapRecipe.xml) 仅在安装并启用独立样例 Mod 时，为 `PF_EarthMaps` 绑定 `Landing` 用途。要观察完整流程，可在新测试世界触发 PFL 舰船降落并用调试器检查该工作数据，或在集成测试中运行 `IndependentSampleLandingRecipeExecutesAgainstTheNativeMap`。完整扩展契约见 [本地地图生成 API](../../Source/PioneerFramework/MapGeneration/API/README.md)。
