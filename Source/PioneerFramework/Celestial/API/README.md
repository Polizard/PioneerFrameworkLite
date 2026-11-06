# 通用天体框架公开契约

契约状态为 2026-11-05 本地地图生成版。框架存档格式 v4（v1/v2/v3 存档明确拒绝，需新建世界）；默认解析提供者检查点载荷 v2；运动协议仍为 `PhysicsContractVersion(1,0)`。本文是可交付的正文，`Docs/CelestialFrameworkAPI.md` 由本文件复制生成。

## 范围与入口

XML 定义天然天体参数及显式 comp 能力。一个 Def 可以产生固定天体或同类的多个实例，允许运行中创建。框架负责身份、生命周期、地表、殖民地地图、现有环境和呈现描述；运动由 `ICelestialPhysicsBridge` 提供。环世界等巨构继续由独立框架负责，本轮不增加环境机制。

扩展通过 `CelestialWorldServices.TryGetCurrent()` 获取当前世界的服务。其 `Query` 为 `ICelestialQueryService`，`Bodies` 为 `ICelestialBodyService`。服务对象固定绑定原世界；读档或切换游戏后应重新获取。业务扩展不调用 `CelestialRuntime.Advance`、协调器或内部创建管线。

| 身份或版本 | 含义 |
| --- | --- |
| `CelestialInstanceId` | 本世界内的稳定实例身份，移除后保留占用历史 |
| DefName / `DefinitionId` | 模板来源，可供多个实例共享 |
| 兼容 handle | 现有业务的数值适配，不替代实例 ID |
| `WorldSessionId` | 当前运行会话；读档后变化 |
| Catalog revision | 创建、移除等业务请求的投递前置条件 |
| Descriptor revision | 指定实例描述的版本；命令的 `ExpectedRevision` 使用它 |
| `PhysicsSampleStamp` | 已提交的 clock/state/topology/time/frame 共同边界 |

延迟回调重新验证会话、实例身份和相应修订。读取 Catalog、回执或 Sample 不推进模拟。Blocked 实例可查询诊断，但不暴露有效业务能力。

## XML 与参数

可复用的荒芜、温带、温室、冰封、气态巨行星和冰巨行星模板见 [通用行星种类](PlanetKinds.md)。这些模板不改变默认地月系。

查看 [独立样例](../../../../Examples/CelestialCompSample/README.md) 和 `1.7/Defs/CelestialBodies/CelestialBodies_SolarSystem.xml`。

```xml
<li Class="PioneerFramework.Celestial.CompProperties_SphericalBody">
  <key>shape</key>
  <radiusMeters><min>250000</min><max>650000</max></radiusMeters>
</li>
```

固定值用 `<value>`；范围同时提供 `<min>` 和 `<max>`，上下界均包含。混用、缺边界、倒置和非法数值提前拒绝。生成种子、稳定生成路径、组件 key 和参数名决定采样。实例保存解析后的配置，恢复时不重采样 XML。key 必须唯一且发行后保持稳定。XML 继承要替换整个组件列表时使用 `Inherit="False"`。

category/tags 是元数据。几何、运动、surface、biomes、map.generation、大气、光源、现有环境和 visual.* 均通过组件明确提供。已注册的 capability 名称必须由对应接口实现；扩展应在首次世界初始化前使用 `CelestialCapabilityRegistry.Global.Register` 注册自身接口。未注册的扩展名称仅参与依赖图匹配。

系统条目支持固定/范围 count、父条目，以及 Global/Parent/Self/Siblings 初始化引用范围。多个候选需指定 index。`rootSurfaceEntry` 必须唯一选出带 surface 能力的主地表。动态创建传入 `CelestialCreateRequest` 或 `CelestialCreateBatchRequest`，内容均为数据；框架内部解析模板与工厂。`ParameterOverrides` 为纯数据列表，每项使用 CompKey、ParameterKey 和 Value 或 Min/Max 覆盖数值参数，仍通过原确定性采样器。整数参数要求 int32 整数边界；重复、无效形式与未消费的拼错参数拒绝。保存解析结果，不重新应用覆盖。

## 动态创建、代次与重试

```csharp
var services = CelestialWorldServices.TryGetCurrent();
if (services == null) return;
var query = services.Query;
var opened = services.Bodies.OpenEpoch(query.WorldSessionId);
if (!opened.Success) return;
long epoch = opened.Value;
// parent/light 由调用者明确选择。完整可编译用例见 SampleCreation.cs。
var operation = SampleCreation.CreateProbe("create-probe-1", epoch, parent, light);
// 保存 operation.OperationId，轮询 Bodies.GetOperation；Applied 后使用 InstanceIds。
```

`CelestialOperationScope` 用原始请求名与 epoch 生成 `QualifiedOperationId`。创建之外的 `CelestialOperationContext` 可通过 `scope.ToContext()` 生成；命令使用 `scope.QualifiedOperationId` 并填写目标描述修订。同一开放代次、同一 ID、同一逻辑内容返回原回执，不同内容返回 `OperationConflict`。会话和投递修订用于合法性检查，不加入逻辑 hash；创建 hash 排除本地化 label，包含解析配置、故障策略、父关系和初始化引用。

| 每世界限制 | 值 |
| --- | --- |
| 同时开放代次 | 8（初始包含 epoch 0） |
| 每代次回执总数 | 4096，pending 与终态合计预留 |
| 每代次 pending | 256 |

业务方在任务组完成且没有 pending 后显式调用 `CloseEpoch(session, epoch)`。关闭会回收终态请求详情并保留高水位；旧请求返回 `OperationExpired`，读档后同样拒绝。**不能把过期的旧动作换成新代次自动重放。** 新代次仅用于用户或业务明确发起的新动作。代次用尽时先完成 pending，再关闭已完成代次；框架不静默淘汰开放代次。

长期组件在 Ready 后必须通过 `Context.SubmitNewAction(actionEpoch, command)` 明确指定新动作的开放代次；原始 command.OperationId 在该调用中保持未限定形式。业务方显式 OpenEpoch，并把作用域交给组件；关闭前 pending 检查仍生效。未限定的默认 Submit 只在 Registering 的创建通知阶段使用创建代次，Ready 后返回 OperationScopeRequired。因此关闭成功创建事务不影响后续显式新动作。旧动作查询/重试仍使用原完整 ID 与原代次，不自动换代。可编译用法见样例 SampleCreation.SubmitComponentAction。

不确定提交先按原 ID 查询或重交，保持内容一致。提供者短暂不可用时，创建/移除保留 Pending，在安全边界查询并重试同一个物理请求。已接受但响应丢失不会重复卸载资源。被阻碍的移除先重新检查地图、引用等条件，再继续。`DiscardFailedCreation` 只回收已标识的创建失败重对象；仍有成员关系、根身份或外部资源的实例受保护，ID/handle 不复用。

## 生命周期与组件

注册工厂、配置 codec 和状态 codec 到 `CelestialBuiltinCompRegistry.Global`；首次世界初始化后封表。工厂必须使用传入的不可变解析配置，并创建符合描述的能力接口。配置编码只在解析/恢复时冻结，按字段生成新的配置版本；运行状态在显式检查点采集，不要求扩展实现 dirty 标记。

内置组件只有一张登记表：`CelestialBuiltinComps`（API 纯表）是 handler、解析配置类型、最高配置版本与能力契约的唯一来源。能力注册表、`CompProperties.Create` 的类型校验以及 `CelestialBuiltinCompRegistry` 的 handler/版本集都从该表派生，不再分别维护三处清单。新增内置 comp = 加一行表，并在 `CelestialBuiltinCompRegistry` 的 decode/factory switch 补齐同一 handler；漏登会在 `Create()` 显式抛错，并由 `CelestialBuiltinCompTableTests` 锁死表/注册表/能力契约的集合一致性，不会静默退化成读档 MissingHandler。扩展组件仍按上段自行注册工厂与 codec，不进入内置表。

| 阶段 | 约定 |
| --- | --- |
| `OnInitialized` | 仅新建初始化业务状态 |
| 状态恢复 → `OnAttached` | 恢复先解码状态，再绑定上下文及资源；AttachReason 为 Restored |
| `OnReady` | 新建物理注册确认后运行，恢复不重放 |
| `OnRestored` | 原生对象、物理检查点与上下文绑定完成后通知一次 |
| `Advance` | 已提交区间，按显式 Scheduled 间隔调度 |
| `Handle` | 明确事件；EventsOnly 不进入时间调度队列 |
| `CaptureState` | 捕获当前业务状态，保存失败保留原始记录与诊断 |
| `CanRemove` / `OnRelease` | 检查阻碍并释放独占资源；Release 最多执行一次，抛错也解除上下文 |

依赖图使用 provides/requires 与 before/after，独占槽防止冲突。默认 `failurePolicy=BlockBody`。显式 `DisableOptional` 仅允许安全的非关键依赖闭包停用；触及 geometry、physics.binding、surface 或默认关键依赖时阻断整颗天体。停用策略、故障诊断及原始载荷持久化；恢复不会重新激活已停用组件。缺失处理器或提供者造成的恢复挂起单独保存原生命周期，补回模块后可恢复；业务故障产生的 Blocked 保持原诊断。已明确停用的组件仅从活动排序图中移除相关排序边，完整原 descriptor 保留，拼错的未知排序目标继续拒绝。保存中的待补偿创建由独立控制记录恢复原注销请求，不激活失败组件。

## 共同边界与执行顺序

| 容器 | 职责及顺序 |
| --- | --- |
| Catalog | 实例身份与已发布描述；稳定 handle 顺序缓存 |
| Operations | 创建/移除/地表/地图幂等回执；按 ID 查询，不承诺跨类别 FIFO |
| References | 外部资源引用及会话释放凭据；按实例索引 |
| Commands | 组件副作用命令；pending 队列明确 FIFO |

宿主推进：捕获输入版本 → 锁外 Prepare 物理与参与者 → 版本复核 → 共同 Commit（或整体 Abort）→ 同步创建/移除 → 锁外 FIFO 命令 → 锁外组件调度/事件 → 原生工作队列。命令和调度器在短锁内选择、校验及发布结果，Execute/Advance/Handle 在锁外运行；组件顺序来自已解析的依赖图，没有跨类别请求的统一 FIFO 保证。回调中新投递的命令留待下次边界。保存只在无 prepared interval、无执行中回调、无 queued/running 原生工作的安全边界捕获，编码和压缩在发布锁外执行。

Prepare 期间查询读取旧的已提交描述、能力成员与版本；提交后回调读取回调开始时的已提交视图。此两阶段 CanRemove 返回 HostWorkPending blocker，避免执行实时资源与组件预检；真正 Remove 仍会重新校验。回调阶段拒绝创建、移除、关闭代次、保存和再次推进，允许命令及事件排队。能力对象引用不代表可跨线程修改组件内部状态。

原生建层/建图只在绑定的宿主线程执行；工作线程请求返回 Pending，宿主 GameComponentUpdate 或推进结束后泵送，暂停期间也能处理。宿主所有权仅在游戏生命周期入口显式交接，任意线程调用内部 Advance 不能重绑。作业重新验证会话、实例、描述修订及当前原生资源；不同请求 ID 排队到同一目标时复用已成功的层/地图，分别保留幂等回执。业务 facade 可排入请求；扩展不得在 worker 中直接操作 Verse/Unity 对象。工厂、生命周期附着与 CaptureState 仍需快速且有界，不能阻塞等待宿主可变服务。

## 运动、预测与诊断

`ICelestialPhysicsBridge` 提供 Prepare/Commit/Abort、查询和检查点。所有向量来自 Stamp.ReferenceFrameId 的统一惯性参考系；姿态从固连系旋入该系。坐标和半径用米、速度 m/s、GM m³/s²；当前兼容模型一个物理 tick 对应一秒。连接验证 ProviderId、契约版本、session、时间与 topology，不能混用不同来源的状态。

Provider diagnostics 公开 Ready/Unavailable/Restoring/Faulted 和结构化 Errors。Unavailable/Restoring 阻止新推进，保留已有描述用于诊断；Faulted/一致性错误阻止推进。公开 Sample 只返回提供者允许的已提交/呈现采样。

可选 `ICelestialPhysicsForecastProvider` 返回独立预测，带 `BasedOn` 和 `Source`（ProviderId、ContractVersion、PredictionSchemaVersion=1）。规划器验证来源与依据版本，预测不能提交时间、消费燃料或替代业务权威状态。缺失时返回 ForecastUnavailable。默认 `PFL.ExistingEphemeris` / `existing.circular.v1` 复用既有解析公式，保留现有父系组合。

可选 `ICelestialPhysicsRetention` 接收终态 token 和已关闭代次确认。默认适配器回收内部记录并持久化过期高水位。扩展提供者未实现时，框架仍拒绝旧请求；提供者自行管理内部保留成本。清理失败保留诊断，不改变已经提交的物理结果。

错误以可扩展稳定 Code 为身份，Category 和 RetryDisposition 表达类别及重试条件；扩展自定义 Code 可用 CategoryOverride/RetryOverride 明确语义。`LocalizationKey` 返回稳定类别键，内建六类均有中英文本，调用方以 Code 作为第一个格式参数（例如 error.LocalizationKey.Translate(error.Code)）。Detail 供诊断，不从文案猜测错误性质。

| 常见 Code | 动作 |
| --- | --- |
| ProviderUnavailable / ProviderRestoring | 等待提供者就绪，保留原请求 ID |
| RevisionConflict / PublicationConflict | 获取当前边界，检查动作仍有效后重试原逻辑请求 |
| StaleSession | 重新获取服务；重新核对原任务和当前回执 |
| OperationExpired | 旧代次永久过期，不自动重放 |
| EpochHasPendingOperations | 完成或明确终结 pending 后关闭 |
| EpochReceiptLimitReached / EpochLimitReached | 手动关闭完成的代次或拆分新的明确任务 |
| NativePatchMissing / NativeContractMissing | 当前游戏签名或必需 hook 不匹配，阻止相关初始化 |
| HandlerMissing / UnsupportedStateVersion / UnsupportedStatePayload | 保留未知原始载荷，启用匹配模块；版本错误和载荷错误按各 codec 的 Code 区分 |

## 地表、地图与原生边界

主地表绑定 Scenario 明确的 surfaceLayer 字段及原生根层调用；按 WorldGrid/Game/World 精确定位生成会话。额外 Scenario 部件复用根 Def 不能取得根身份。地表构造头在原生 InitializeLayer 和世界绘制层构造前可用；恢复先读取头，再进入原生层恢复。必需 Harmony 目标和实际安装结果均预检，失败明确报告，保留原始生成异常。

Creation 在创建流程生成；OnDemand 需显式 RequestSurface。地图另经 RequestMap 的 Inspect/Settle/Land/Generate 行为请求。地址包含实例、层、tile 及可选 MapParent 身份。同一 Def 的多实例、多地图独立保存。查询与 Inspect 不触发生成。新建地图从有效的 ICelestialMapGenerationCapability 获取冻结的 LocalMapGenerationSpec，排队执行前和完成时再次核对能力和原生层对象身份，停用组件的历史配置不能授权新生成。已知对象缺失/替换返回身份错误；仅明确退役允许新的明确请求创建替代对象。地图、WorldObject、飞船、运输、巨构与扩展引用会阻止框架移除，主地表不能移除。天然配方、用途配方、原生候选、扩展注册和地图失败诊断见 [本地地图生成 API](../../MapGeneration/API/README.md)。

原生 WorldGrid 的删除通知会退役已关联的非主地表，仅清理该层关联，保留天体及其他层。生成期间删除的层不能发布为成功。历史地表回执保留 Applied 及 SurfaceRetired 诊断；再次提交该旧请求返回 Rejected/SurfaceRetired，保存重载后仍然有效，新的明确请求可以重新生成。直接丢失或替换层对象且没有原生删除通知时，仍拒绝保存与替代生成并报告 SurfaceIdentityMismatch。该通知不授权删除主地表，也不绕过原生对地图和世界对象的清理。

内建球面 subdivisions 只允许 0～10，原生半径与物理半径分开保存。此上限是资源保护，10000 天体是受控台架规模，均不构成 FPS 或高细分生成性能承诺。更大地表需自有能力和明确适配。父子关系及初始化引用的移除检查仍扫描目录，不承诺常数时间。本轮保留原数值判据、浮点归约顺序与物理算法。

## 存档与版本

v4 使用独立固定字段 DTO。冻结配方和地图来源、阶段结果、失败身份与报告摘要随存档保存。外层包含 formatVersion、brotli 标识、decodedLength、SHA-256、块数及按 index 排列的块；压缩块最多 256 KiB，未压缩上限 128 MiB。缺字段、未知核心字段、重复键、截断、重复/调序块、哈希不符、尾随流和解压越界均拒绝。未知组件配置/状态与命名空间扩展 byte bag 原样保留；未知内容不推断默认值。

创建/命令/物理/移除逻辑 hash schema 独立为 2/1/1/1，与存档 v4 分开版本化；按固定二进制字段顺序及确定数值格式编码。内建 profile 配置字段和顺序固定，已知版本的 JSON 配置同样拒绝缺字段、未知字段和重复字段；损坏载荷随诊断原样保留。已解码的原始配置字节冻结保留。默认物理检查点载荷 v2 同时保存开放代次与过期高水位。

v1/v2/v3 文档一律返回 `UnsupportedSaveVersion`，需要新建测试世界。不自动迁移、不覆盖旧用户存档。原生地图身份、组件状态和物理检查点来自同一共同边界。

全局恢复失败会暂停太空模拟、记录去重错误并向玩家提示。修复依赖后可由提示框、开发菜单或 CelestialBodyManager.RetryCelestialRestore 显式重试；成功后才允许保存。正常保存入口在 SafeSaver 接触目标文件前拒绝未恢复或不安全边界，防止 Scribe_Deep 捕获组件异常后仍写出部分文档。加载期 adapter 与未恢复 runtime 的 CaptureCheckpoint 仅供诊断保留，ExposeData 不会将其写成新存档。组件级缺失仍可保留原始配置/状态为 Blocked，依照故障策略完成其余世界的恢复。

命名空间 Extensions 字节袋当前用于保留及往返未知存档数据，未提供运行时公共写入口；扩展的可变业务状态使用 comp 状态契约。category/tags 保持开放元数据，旧消费者需要的 Star/Planet/Moon 兼容角色来自明确的物理 comp 初始化或视觉/父子关系投影。

## 行星参数与作者示例

[PlanetAuthoring.md](PlanetAuthoring.md) 是数值字段、完整有效区间、覆盖路径、组件版本与实际消费者的完整表，包含气体注册、typed列表/组件替换与手工验收边界。[PlanetKinds.md](PlanetKinds.md) 说明现有六个可选模板、开发者变体与检查面板。默认太阳/地球/月球与历史实例保持原行为；新增能力均由明确组件启用。

## 构建与验证

固定 SDK 10.0.402；主交付仅 `1.7/Assemblies/PioneerFrameworkLite.dll`。样例 Debug 输出 bin/Debug，独立 Mod Release 输出自身 Assemblies；样例引用的框架编译隔离到 bin，避免重写真实交付件。构建及验证见根 README、tools/test-all.ps1 和样例说明。

冻结 v1/v2 fixtures 与实际发货 XML 回归共同使用；v1/v2 fixtures 仅用作旧格式拒绝回归，由原历史版本冻结保存。台架记录 CPU 延迟、分配、状态/配置编码次数与存档体积。自动测试覆盖纯核、托管原生入口和 Scribe 顺序，无法证明真实渲染、GPU 成本、交战手感或游戏内存读档；这些项目见本机人工验收单，由人工记录结果。
