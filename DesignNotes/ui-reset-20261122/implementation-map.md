# C 方案实现映射（设计阶段）

**日期**：2026-11-23  
**基线**：`refactor/chain-b-only-20261121` / `449e493`  
**性质**：决策记录 + 控件 → 现有 API 映射。**未改任何 `.cs` / `.xml` / 后端**。  
**输入**：本目录 12 张 SVG（含新增 `assembler-preview.svg`）、`_gen.py`、`Window_RocketAssembler.cs`（1108 行）、`CompRocketAssembler.cs`（1130 行）。  
**输出**：哪些是纯重绘、哪些是新增前端逻辑、哪些必须后端授权；以及不能悄悄丢掉的原功能归宿。

---

## 0. 结论（先看这 5 条）

1. **界面元素约 80% 是重排 + 重绘；新增前端逻辑集中在 6 个簇（约 1000 行）。** 现有窗口的 API 足够撑起 L0/L1/L2；不需要新的物理/时间/状态接口。
2. **唯一硬行为依赖是「已完工 → 编辑设计」。** `CanEditDesign` 在 `Complete` 为 `false`（`CompRocketAssembler.cs:75`），`StartNewRocket()` 会把设计清空（`:645`）。C 稿的“受阻 → 编辑设计”用现有 API 做不到，必须加 `ReopenDesign()`（B1）或接受“下一枚”会丢设计。
3. **推荐实现路线：新窗口类放在锁外，锁内只改一行 gizmo。** 新类 `Window_RocketAssemblerC` 放 `Presentation/Windows/`；`CompRocketAssembler.CompGetGizmosExtra` 只把 `new Window_RocketAssembler(this)` 改成 `new Window_RocketAssemblerC(this)`。旧窗口保留作回退。这样锁内 diff 是 1 行 + 可选的 `ReopenDesign`，不是 1100 行重写。
4. **原窗口有 9 组功能在 C 稿里没有落点。** 清空、示范、反转级号、三个级标签、径向复制、并联、径向对称、平移缩放、单件减一。第 5.4 节逐项给归宿；**不能默默丢**。
5. **C 稿的“查看全部检查”只画了 4 行，但发射门不止 4 项。** 需要分成 Build 检查与 Launch 检查两套适配器（第 4 节）；否则“可发射”会误报。

### 已拍（2026-11-23）

| # | 问题 | 决定 |
|---|---|---|
| D1 | 「动作按钮 / 选择器 / 微图标」三分法 | **确认**：动作按钮原版 `Widgets.ButtonText`；选择器扁平 1px 片；微图标无底板 |
| D2 | L2 的 `≡` 拖拽 | **一期就做**；与 `↑↓` 并存 |
| D3 | 「开始建造」放哪 | **L1 右栏底部固定 44px** + 一行状态 |
| D4 | 「受阻 → 编辑设计」 | **申请 `ReopenDesign()`**；改设计需重新建造，实现前仍需授权件 |

其余 D5–D12 有默认值，不阻塞设计定稿，但阻塞实现细节。

---

## 1. 边界与分类

- 本文不改代码，只做映射。实现必须等用户明确说“开始实现”。
- 后端锁仍然有效：`Gameplay/Launch|GroundAssembly|Combat|Landing`、`PhysicsEngine`、`1.7/Assemblies` 等。
- **注意：`Window_RocketAssembler.cs` 本身就在 `Gameplay/GroundAssembly/` 下。** 机器闸门按路径判，不按“它是不是 UI”判。即使只改窗口，`tools/check-backend-lock.ps1` 也会报后端变更。所以实现前必须有一份**明确列出 UI 文件**的授权件，或由仓库主人确认 UI 豁免。这份映射不签授权、不落盘授权件。
- 分类记号：

| 记号 | 含义 |
|---|---|
| `R` | 纯重绘 / 重排：只改画法与位置，API 调用与状态语义不变 |
| `F` | 新增前端逻辑：窗口内新状态、新命中测试、新视图适配 |
| `B` | 需要后端授权：改 `CompRocketAssembler` 或锁内文件 |
| `D` | 设计待决：需要用户拍板 |
| `X` | 退役：旧控件/旧字段删除或不再使用 |

---

## 2. 六个关键发现

### 2.1 `Complete` 不可编辑，「编辑设计」没有 API

- `CanEditDesign => state == Idle || state == Designing`（`CompRocketAssembler.cs:75`）。
- `Complete` 下所有编辑 setter 直接 `return false`。
- 唯一出口 `StartNewRocket()` 调 `ResetToIdle()`，把 `design` 换成新的空 spec（`:645-660`）。
- 所以 C 稿 L0 受阻态的「编辑设计」只有三条路：
  1. **B1（推荐）**：加 `public bool ReopenDesign()`，`Complete → Designing`，保留 `design`，清 `workDone/workRequired/launchTicksLeft` 和 `Design.fuelAmount`。
  2. 前端 hack：先保存 `comp.Design` 引用，`StartNewRocket()`，再把旧 `parts` 等字段塞回新 spec。**不推荐**：绕过状态机、`fuelAmount` 语义容易错、后续维护者会踩。
  3. 不改：受阻态主按钮改「下一枚」，明确告诉玩家会丢设计。**这是回退方案，不是 C 稿。**
- 不要在窗口里直接改 `comp.Design.parts` 来“绕过” `CanEditDesign`。那会让 `Complete` 状态和设计内容不一致，且 `fuelAmount` 过期。

### 2.2 边框色常量是 private

- `Widgets.WindowBGFillColor` 是 `public`（`Widgets.cs:3897`），`#15191D` 可用。
- `Widgets.WindowBGBorderColor` 是 `private`（`Widgets.cs:3894`），窗口里**引用不到**。
- 实现用 `new Color32(97, 108, 122, 255)`（`#616C7A`）并注释“= 原版 WindowBGBorderColor（1.6 私有）”。不要为了它去改原版。

### 2.3 TWR 现在固定 g=8，和实际发射不同源

- 旧窗口：`weightKn = s.TotalMassKg * 8f / 1000f`（`Window_RocketAssembler.cs:341`）。`TotalMassKg` = 干重 + `tankInitialKg`，**不是满罐容量**；有些件的初装量小于容量，TWR 会被高估。
- 实际发射：`gSurface = target.SurfaceGravity`（`LaunchService.cs:242`），`thrustN <= weightN` 拒绝（`:258`）；`FinishBuild` 按容量满罐加注（`CompRocketAssembler.cs:1025`）。
- 新检查：`weightN = (s.dryMassKg + s.capacityKg) * gSite`，即满罐质量；不要用 `s.TotalMassKg`。
- 组装器可以建在月球/其它天体；固定 8 会把低重力站点误判成“推重比不足”。
- 可用公开只读 API：`LaunchSite.TryResolve(comp.parent.Map, out site, out _)`（`LaunchSite.cs:64`），`g = site.Mu / (site.Radius * site.Radius)`；解析失败再退 8。
- UI 仍标注“估计”：真正的门在 `LaunchService`，用的是发射后实体的质量/推力，不是设计口径。

### 2.4 C 稿没有「开始建造」的落点

- 六状态里没有“可建造”；L1 标题栏只有「清单 / 完成」；`完成` 按 L1 标注是回 L0，不是开工。
- 但 `StartBuild()` 必须有个可见入口，否则玩家永远停在设计态。
- 推荐：L1 右栏底部固定一条 **44px「开始建造」**（没选中件时也可见），上方一行状态（“设计有效” / “还差 1 项：引擎必须在级末”）。`完成` 只退出编辑。
- 这样 L0 仍是“看态”，建造是编辑层的动作，符合“L0 只放一个主按钮”。

### 2.5 4 行检查覆盖不了发射门

- C 稿 L0-steps 写的是 4 行：推重比 / 推进器 / 燃料 / 控制核心。
- `BeginLaunch` 实际还查：控制核心（`:764`）、引擎位置（`:770`）、级连续性（`:777`）、活动级 ready（`:783`）。
- 其中「引擎位置 / 级连续性」是设计期门，正常完工火箭不会带；但老档/示范/异常档可能带。检查窗口应**动态**：正常 4 行，检测到 legacy 错误时追加这两行。
- `Design.IsValid`（推进器/储罐）是**建造门**，不是 `BeginLaunch` 的直接门；放在 Build 检查，不要混进 Launch 的 4 行。

### 2.6 C 稿缺 9 组原功能归宿（16 个具体入口）

见 5.4。最危险的是「清空设计」「示范火箭」「反转级号」「级标签」「径向复制」——它们现在都有玩家入口，C 稿没有对应控件。

---

## 3. 状态解析（L0 六态 + 两个边界）

不要直接把 `comp.StateText` 当 UI 标签。`StateText` 是给 comp inspect / gizmo 用的（`CompRocketAssembler.cs:1071`）；L0 需要一个 `SurfaceState` 视图枚举。

| SurfaceState | 条件 | 灯 | 一句话 | 主按钮 | 次链接 | 数据源 |
|---|---|---|---|---|---|---|
| `IdleEmpty` | `State==Idle && parts.Count==0` | 灰 | 还没有设计。先放一个引擎和一个工质箱。 | 开始设计 | — | `State`, `Design.parts.Count` |
| `IdleWithDesign` | `State==Idle && parts.Count>0` | 灰 | 台子空着；这份设计还能再用一次。 | 继续设计 | — | 同上 |
| `Designing` | `State==Designing` | 金 | 设计有效，可以开始建造。 / 还差 1 项：… | 继续设计 | — | `State`, Build 检查 |
| `Building` | `State==Building` | 金 | 建造中 42% · 1,240 / 2,950 工时 | 取消建造 | — | `Progress01`, `WorkDone`, `WorkRequired` |
| `LaunchReady` | `State==Complete && LaunchBlockers==0` | 金 | 可以发射。倒计时 10 游戏秒。 | 发射 | 查看全部检查 | Launch 检查, `Props.launchDelayGameSeconds` |
| `LaunchBlocked` | `State==Complete && LaunchBlockers>0` | 红 | 还差 1 项：推重比 0.21（需要 >1）。 | 编辑设计（B1） | 查看全部检查 | Launch 检查 |
| `Launching` | `State==Launching` | 金 | 8.4 秒后发射。 | 取消发射 | — | `LaunchSecondsLeft`, `LaunchSecondsTotal` |

**两个边界：**

- `IdleWithDesign` 是真实存在的：`DoLaunch` 成功后 `state = Idle`，但 `design` 保留（`CompRocketAssembler.cs:850`）。此时不能显示“开始设计”，否则玩家会以为设计没了。主按钮用「继续设计」，次按钮允许直接再建。
- `LaunchBlocked` 在正常新档里通常只剩 TWR 一项（其余门在建前已过）。老档可能带引擎位置/级连续性错误；检查窗口要能显示。
- `StateText` 的“已完工”不进 L0 大标题；L0 用「可发射 / 受阻」语义标签。meta 行可写“已完工 · 1 项未通过”。

---

## 4. 检查适配器

### 4.1 Launch 检查（L0「查看全部检查」）

| # | 行 | 通过条件 | API | 失败短句 |
|---|---|---|---|---|
| 1 | 推重比 | `ThrustN(Design, null, ActiveStages) > (dryMassKg + capacityKg) * gSite` | `RocketSchematicBuilder.ThrustN`（`RocketSchematic.cs:274`）、`Build().dryMassKg/capacityKg`、`LaunchSite.TryResolve`（`LaunchSite.cs:64`）；失败退 `g=8` | 推重比 0.21 · 需要 >1（F 588 kN / W 2,800 kN） |
| 2 | 推进器 | 每个活动级 `EngineCountInStage(s) > 0` | `comp.ActiveStages`（`:506`）、`EngineCountInStage`（`:509`） | 活动级 0 没有引擎 |
| 3 | 燃料（工质箱） | 每个活动级 `TankCountInStage(s) > 0`，且 `FuelForLaunch(Design) > 0` | `TankCountInStage`（`:522`）、`FuelForLaunch`（`:747`） | 活动级 0 没有工质箱 / 燃料容量 0 |
| 4 | 控制核心 | `ControlCount > 0` | `ControlCount`（`:581`） | 缺少控制核心（必须 1 个） |
| 5* | 引擎位置 | `EnginePlacementError() == ""` | `EnginePlacementError`（`:451`），带 `offenders` 标行 | 第 3 行「…」排在级引擎之后 |
| 6* | 级连续性 | `StageContiguityError() == ""` | `StageContiguityError`（`:458`），带 `offenders` | 级 1 被别的级隔开 |

`*` 只在错误非空时出现（legacy / 异常档）。正常完工火箭就是 4 行，和 C 稿一致。

- 第 2/3 行不要用全局 `CountInDesign` 代替：活动级是最大级 + 并联点火级（`RocketDesignRules.ActiveStages`，`:121`），只数全局会把“引擎全在下面级、活动级是空壳”误判为通过。
- 第 1 行是**估计**。tooltip 写明“按设计质量 × 站点重力估算；实际发射由 LaunchService 用发射后实体复核”。
- `Design.IsValid`（`RocketDesignSpec.cs:81`）不放进 Launch 4 行；它属于 Build 检查。

### 4.2 Build 检查（L1「开始建造」是否可点）

这一套必须和 `StartBuild` 同序同口径（`CompRocketAssembler.cs:692-730`）：

| 顺序 | 条件 | API |
|---|---|---|
| 1 | 可编辑（Idle/Designing） | `CanEditDesign`（`:75`） |
| 2 | 至少 1 推进器 + 1 储罐 | `Design.IsValid`（`RocketDesignSpec.cs:81`） |
| 3 | 控制核心 ≥1 | `ControlCount`（`:581`） |
| 4 | 引擎位置合法 | `EnginePlacementError`（`:451`） |
| 5 | 级连续 | `StageContiguityError`（`:458`） |
| 6 | 活动级 ready | `ActiveStageReady`（`:552`） |
| 7 | 工时 > 0 | `WorkFor(Design)`（`:665`） |

前端只用它决定按钮亮/灰和 tooltip 第一句；点击仍调 `StartBuild(out why)`，失败弹 `why`。**权威永远是 comp，不是窗口。**

### 4.3 「查看全部检查」小 Window

- 独立小 `Window`（`Launch` 约 420×220，`Build` 约 420×260），不是 in-window 弹层：`doWindowBackground=false`、`doCloseX=false`、`closeOnCancel=true`、`closeOnClickedOutside=true`、`absorbInputAroundWindow=false`、`draggable=false`、`resizeable=false`。
- 位置由父窗口传入的链接 rect 决定（优先上方，放不下放下方）。
- 两种模式，同一个窗口类：
  - `Launch`：标题「发射检查」，4 行（legacy 时 6 行），来自 4.1；L0 的 `LaunchReady`/`LaunchBlocked` 打开。
  - `Build`：标题「建造检查」，7 行，来自 4.2；L1 建造条打开。
- 每行：`✓/×` + 行名 + 一句原因（最多 30 字）；不写段落。
- 「点空白关闭 / Esc 关闭」都是 `Window` 原生行为，不需要在 L0 里做输入吞噬。
- `F`，约 120–150 行；无后端。

---

## 5. 控件 → API 总表

### 5.1 L0 看态

| 控件 | 动作 | API / 字段 | 类 | 备注 |
|---|---|---|---|---|
| 窗口底 + 1px 边 | 画 `WindowBGFillColor` + `#616C7A` | `Widgets.WindowBGFillColor`；边框常量 | R | 建议 `doWindowBackground=false`、`Margin=0`；见 D9 |
| 标题「火箭组装器」 | 文本 | — | R | 18/Medium；不做 `optionalTitle` |
| `×` | `Close()` | `Close()` | R | 16–20px 微图标热区，不用原版按钮底 |
| 画布 + 网格 + 取景角标 | 画火箭 | `RocketSchematicBuilder.Build`（`:122`）、`DrawStation`、`DrawCylinderBlock` | R | 调色板换成中性 + 金；不画旧蓝/橙 |
| 零件 tooltip | 悬停看规格 | `TooltipHandler.TipRegion` + `SchematicPiece` 字段 | R | 文案压缩，保留质量/容量/级/块 |
| 状态灯 + 状态词 | 6 态 | `SurfaceState`（F1） | F | 金=进行中/可发射，红=阻断，灰=空闲 |
| 状态一句 | 一句 | `StateText` + 检查适配器 + `Progress01`/`LaunchSecondsLeft`（按状态） | F | 见第 3 节 |
| meta 行 | 总高 · 干重 · 级数（+ TWR 状态） | `Build()` 的 `totalHeightM`/`TotalMassKg`/`stageCount`；TWR 检查 | R/F | 只一行；完整 4 项进检查窗口 |
| 主按钮（44px） | 6 态派发 | 见 5.1.1 | F | 原版 `Widgets.ButtonText` |
| 「查看全部检查」 | 开检查 Window（Launch） | 4.3；`Window_AssemblerChecks` | F | 仅 `LaunchReady`/`LaunchBlocked`；文本链接，独立小窗 |
| 窗口拖动/缩放 | 原版 | `draggable`、`resizeable` | R | 保留；布局按 flex 处理 |

> L0 没有次按钮：建造入口只在 L1 建造条（D3）。行/件数、工时预算不在 L0 常驻——行/件数进 L2 标题，工时进 L1 建造条/tooltip；L0 只保留约 12 个文本块。

#### 5.1.1 主按钮派发

| SurfaceState | 按钮 | 调用 | 成功后 |
|---|---|---|---|
| IdleEmpty | 开始设计 | `editMode = true` | L1 空设计 |
| IdleWithDesign / Designing | 继续设计 | `editMode = true` | L1 保留设计 |
| Building | 取消建造 | `comp.CancelBuild()` | Designing，L0 |
| LaunchReady | 发射 | `comp.BeginLaunch(out why)`；失败 `Messages.Message(why, RejectInput)` | Launching，L0 |
| LaunchBlocked | 编辑设计 | `comp.ReopenDesign()`（B1） | Designing，`editMode=true` |
| Launching | 取消发射 | `comp.CancelLaunch()` | Complete，L0 |

### 5.2 L1 编辑态

#### 5.2.1 标题栏

| 控件 | 动作 | API | 类 | 备注 |
|---|---|---|---|---|
| 标题「火箭组装器 · 设计」 | 文本 | — | R | 标题加后缀，不新开窗 |
| 「清单」28px | 打开 L2 Window | `Find.WindowStack.Add(new Window_RocketAssemblerList(comp, parent))` | F | 父窗口持引用；`PreClose` 一起关 |
| 「完成」28px | `editMode = false` | — | F | 只退出，不建造 |
| `×` | `Close()` | `Close()` | R | 同 L0 |

#### 5.2.2 级轨（左，88px）

| 控件 | 动作 | API | 类 | 备注 |
|---|---|---|---|---|
| 级行 0…N | 选“加入级” | `comp.CurrentStage`（`:78`）、`comp.SetCurrentStage(st)`（`:80`） | F | 只影响新件落到哪级，不是过滤 |
| 级行列表 | 显示设计里出现的级 + 当前级 | `RocketDesignRules.StagesOf(Design)`（`:103`） | F | 空设计至少显示级 0 |
| `+` | 选下一个未用级号 | `SetCurrentStage(next)`；`ClampStage`/`MaxStageId`（`:485`） | F | 5 级全用则灰；不创建空级 |
| 活动级标记 | 可选小金点 | `comp.ActiveStage`（`:503`） | R/F | 不写“活动”二字，省文本 |

#### 5.2.3 画布

| 控件 | 动作 | API | 类 | 备注 |
|---|---|---|---|---|
| 画火箭 | 同上 | `RocketSchematicBuilder.Build` | R | 选中件 2px 金框；悬停 1px 白 30% 可选 |
| 点零件 | 选中 | `pieceHits`（已有，`:40`/`:265`）→ `selectedRow = designRow` | F | 当前窗口没有画布选中，这是新逻辑 |
| 点空白 | 取消选中 | `selectedRow = -1` | F | 仅未 armed 时 |
| 拖动平移 | 平移 | `schematicPan`（`:63`） | R | 与“点选”冲突：见下 |
| 滚轮缩放 | 缩放 | `schematicZoom`（`:64`） | R | 0.25–8，保留 |
| 重置视图 | 归零 | `schematicPan/Zoom` | R | 微图标 `⟲` 或文字“重置”；字形要试 |
| armed 幽灵 | 跟随鼠标 | `armedDef` + `Event.current.mousePosition` | R | 改金色/白色，不用旧绿 |
| 放置点击 | 空白=追加；侧面=径向；顶/底=轴向 | `AddPart`（`:370`）、`AddPartRadial`（`:148`）、`AddPartAxialTo`（`:214`） | F | 删 `placeMode`，自动判定 |
| 点已选件取消 | `armedDef = null` | 现有 toggle（`:752`） | R | 保留 |
| 级间隔线 + 级号 | 画 | `SchematicPiece.stageId` | R | 级号标签保留 |
| 设计 meta 行 | 总高 · 干重 · TWR | `Build()` + TWR 检查 | R/F | 画布左下角一行；D6 |

**点选 vs 平移的冲突（必须处理）：** 当前 `HandleSchematicInput` 在 MouseDown 就 `dragging=true`；如果同时做“点零件选中”，拖动画布会误选。新逻辑：MouseDown 只记 `pressPos`；MouseDrag 超过 4px 才进入平移；MouseUp 未超阈值才做选中/放置。约 40 行。

#### 5.2.4 右栏检视（336px）

| 模式 | 内容 | API | 类 |
|---|---|---|---|
| 未选中 | 「点火箭上的零件」+ 设计状态一句 + 底部建造条 | `SurfaceState`、Build 检查 | F |
| 选中设计件 | 名称、级/件数、3 行规格、上移/下移/换级/删除、径向复制 | `selectedRow`、`Design.parts[i]`、`ShipPartDef` | F |
| armed 目录件 | 名称、“待放置”、规格、并联 1–7、放置提示 | `armedDef`、`CurrentParallel` | F |

选中件动作：

| 动作 | API | 类 | 备注 |
|---|---|---|---|
| 上移 | `comp.MovePart(selectedRow, -1)`（`:411`） | R/F | 成功后 `selectedRow--`；越界灰 |
| 下移 | `comp.MovePart(selectedRow, +1)` | R/F | 成功后 `selectedRow++` |
| 换级 | 弹 0–4 级片 → `comp.SetPartStage(selectedRow, st)`（`:462`） | F | 现有 API 未被窗口使用；比 +级/−级 更直接 |
| 删除 | `comp.RemovePartAt(selectedRow, row.quantity)`（`:620`） | F | 整行删；见 D8 |
| 径向复制 ×4 | `comp.RadialCopy(selectedRow, 4)`（`:256`） | F | 成功后 `selectedRow=-1`；放次按钮/“更多” |
| 规格行 | `ShipPartDef.massKg`/`tankCapacityKg`/`exhaustVelocity`/`massFlowRate`；`StackHeightOf`（`:229`） | R | 拆成 3 行 label/value，不再拼长串 |

> 规格行建议：容量 / 件重 / 高度（罐）；ve / 推力 / 件重（引擎）；件重 / 高度（其它）。最多 3 行。

#### 5.2.5 底部零件带（104px）

| 控件 | 动作 | API | 类 | 备注 |
|---|---|---|---|---|
| 分类片 | 过滤 | `catalogCache` + `ShipPartDef.category`；`CategoryLabel`（`:1076`） | F | 全部/引擎/工质箱/结构/控制/载荷 + 更多 |
| 零件卡 | `armedDef = def`（再点取消） | 现有 `armedDef`（`:39`） | R/F | 金框=待放置；卡片只写名字，不写规格 |
| 更多 `›` | 横向滚动/展开 | `shelfScroll`（新） | F | 不新开窗 |
| 并联 1–7 | 选台数 | `comp.CurrentParallel`（`:86`）、`SetCurrentParallel`（`:88`） | R/F | 扁平片；armed 时出现在检视栏或带首 |
| 放置提示 | 一句 | — | R | “空白=追加 · 侧面=径向×N · 再点取消” |

#### 5.2.6 建造条（检视栏底部，固定）

| 控件 | 动作 | API | 类 | 备注 |
|---|---|---|---|---|
| 状态一句 | “设计有效”/“还差 1 项：…” | Build 检查（4.2） | F | 红/金；只一句 |
| 开始建造 44px | 开工 | `comp.StartBuild(out why)`（`:692`） | F | 成功 `editMode=false` 回 L0 看进度；失败弹 `why` |
| 检查链接 | 开检查 Window（Build） | 4.3 | F | 可选；L1 看全量建造原因 |

### 5.3 L2 清单弹层（独立 Window，推荐）

**为什么不做 in-window overlay：** IMGUI 里后画的控件不一定先拿到鼠标事件；如果 L1 控件先画、弹层再画，点在弹层按钮上可能被下面的 L1 按钮吃掉。要 in-window 做对，必须在 `listOpen` 时**完全不画 L1 控件**（或给每个控件加 gate），侵入大且容易漏。用独立 `Window` 是原版窗口栈的正确做法：输入只给顶层窗口，Esc / 点外关闭也由 `Window` 原生处理。

| 控件 | 动作 | API | 类 | 备注 |
|---|---|---|---|---|
| 子窗口 | 打开 | `Find.WindowStack.Add(new Window_RocketAssemblerList(comp, parent))` | F | 父窗口持引用；`PreClose` 里一起关，避免孤儿 |
| 窗口属性 | 640×560，居中于父窗口 | `InitialSize`；`SetInitialSizeAndPosition` 用父 `windowRect` | F | `doWindowBackground=false`、`doCloseX=false`、`closeOnCancel=true`、`closeOnClickedOutside=false`、`absorbInputAroundWindow=true`、`draggable=false`、`resizeable=false` |
| 68% 暗幕 | 压暗父窗口 | `ExtraOnGUI`：`Widgets.DrawBoxSolid(parent.windowRect, new Color(0,0,0,0.68f))` | F | C 稿是窗口内暗幕；要全屏压暗就把 rect 换成 `UI.screenWidth/Height` |
| 面板 640×560 | 画 | — | R | 1px 边；`drawShadow` 可关 |
| 标题 + 计数 + × | 关 | `Close()` | F | × 微图标 |
| 11 行 | 画 | `Design.parts`、`RocketDesignRules.PartCountOf` | R | #/级/零件/件数/序；块号/并联后缀保留 |
| 点行 | 选中 | 回调 `parent.SelectRow(i)` | F | 选中行同步；居中面板会挡住火箭中心，见 D12 |
| 违规行 | 红底/红点 | `placementOffenders`/`contiguityOffenders`（`:49`/`:55`） | R | 现有列表已有；L2 保留 |
| `≡` 拖 | 调序 | `MovePart(from, to-from)` | F | 一期做；拖 `≡` 手柄，与行点击分开；`↑↓` 并存 |
| `↑`/`↓` | 调序 | `MovePart(i, ∓1)` | R/F | 底部按钮作用于选中行 |
| `换级` | 0–4 级片 | `SetPartStage` | F | 只作用选中行 |
| `删除` | 删整行 | `RemovePartAt(i, row.quantity)` | F | 原版底 + 红字 |
| 滚动 | 滚动 | 自持 `Vector2`（原 `designScroll` 语义） | R | 行多时 |
| Esc / 点 × | 关 | `closeOnCancel=true`；`Close()` | R | 原生，不写 keyCode |
| 清空/示范/反转/级标签 | 见 5.4 | — | F | 需要归宿；建议底部第二行或表头溢出 |

> 若用户坚持“L2 必须是同一个 Window 的 overlay”，则必须在 overlay 打开时跳过 L1 全部控件的绘制（不是画在上面），否则输入会漏。这个代价比多一个 Window 大，不推荐。

### 5.4 原功能归宿（防丢清单）

| 原功能 | 现在在哪 | 新归宿（建议） | API | 类 |
|---|---|---|---|---|
| 清空设计 | 底栏 | L2 表头 `⋯` 或底部文字“清空” + 二次确认 | `ClearDesign()`（`:635`） | F |
| 示范火箭 | 底栏 | L2 `⋯` / 底部文字“示范”（tooltip 说明会替换设计） | `QuickAssembleDemoRocket`（`:865`） | F |
| 反转级号 | 设计清单 | L2 级设置行 / `⋯` | `ReverseStages()`（`:606`） | F |
| 不抛弃 | 设计清单 | L2 级设置（选中行的级） | `IsStageKeep`/`SetStageKeep`（`:91`） | F |
| 可回收 | 设计清单 | 同上 | `IsStageRecoverable`/`SetStageRecoverable`（`:93`） | F |
| 并联点火 | 设计清单 | 同上 | `IsStageParallel`/`SetStageParallel`（`:95`） | F |
| 径向复制 ×4 | 设计清单 | 检视栏次动作 | `RadialCopy`（`:256`） | F |
| 并联 1–7 | 目录头 | 检视栏 armed 模式 | `CurrentParallel`/`SetCurrentParallel` | R/F |
| 径向对称 2–6 | 目录头 | 与并联合并（D5）；径向时 `N=CurrentParallel`，N≥2 | `AddPartRadial(def, n, parent)` | F/D |
| 放置方式 自动/轴向/径向 | 目录头 | **退役**：自动手势覆盖 | — | X |
| 加入级 0–4 | 设计清单 | 级轨 | `SetCurrentStage` | F |
| 单件减一 `−` | 每行 | 退役或检视栏次按钮（D8） | `RemovePartAt(i,1)` | X/D |
| 平移/缩放/重置 | 画布 | 保留；重置做微图标 | `schematicPan/Zoom` | R |
| 下一枚 | 底栏 | B1 通过后退役；B1 被拒时作受阻回退 | `StartNewRocket()`（`:645`） | X/D |
| 调试完工 | 底栏 | 仅 DevMode，L1 调试行 | `ForceComplete()`（`:1011`） | R |
| 搜索 | A/B 稿 | C 稿没有；不做 | — | X |

**现有按钮逐项核对（防漏）：**
开始建造 / 取消建造 / 取消发射 / 发射 / 下一枚 / 清空设计 / 示范火箭 / 调试完工 / 重置视图 / 并联 1–7 / 放置 自动·轴向·径向 / 对称 2–6 / 加入级 0–4 / 反转 / 不抛弃 / 可回收 / 并联 / 径向复制×4 / ↑ / ↓ / +级 / −级 / − / 目录行 `+` / 设计行选中 / 关闭 × / 右键取消 / Esc 取消放置。
→ 新表里每一项都有归宿；**右键取消 / Esc 取消放置 / 单件减一 / 放置方式 / 下一枚** 是明确退役或待决项。

### 5.5 文本预算

| 层 | 常驻文本块 | 说明 |
|---|---|---|
| L0 | ≈12 | 标题 / 状态 / 一句 / 主按钮 / 检查链接 / meta / ×；行数与工时不在 L0 |
| L1 | ≈30–36 | 级轨 / 零件带 / 检视 / 建造条；装配清单不常驻（不含画布零件名与数据行） |
| L2 | 面板 chrome ≈10 + 11 行数据 | 只在打开时出现 |

> 验收时按“文本块”数，不按字符数；目标是删掉旧窗口 100+ 个常驻文本块，而不是把同样的字排得更好看。

---

## 6. 具名字段 / 方法逐项

| 现有 | 现在的作用 | 新窗口中的角色 | 类 | 注意 |
|---|---|---|---|---|
| `selectedRow`（`:36`） | 设计清单选中行 | 画布选中 + 检视栏 + L2 共用 | F | 新增画布命中；删除/移动后要 clamp |
| `armedDef`（`:39`） | 目录里点中的件 | 零件带“待放置” | R | 再点取消已有；画布/检视栏读它 |
| `pieceHits`（`:40`） | 放置命中矩形 | 放置 + 选中 + L2 联动 | F | 每帧重建（`:265`）；要区分点击/拖拽 |
| `HandlePlacementClick`（`:360`） | 右键/Esc 取消 + 三种放置 | 只保留自动手势；右键/Esc 分支删 | F | 见 5.2.3；`placeMode` 删 |
| `CanEditDesign`（`:75`） | 所有编辑 setter 的总闸 | L1 入口 + 编辑设计按钮的总闸 | B/D4 | `Complete` 为 false；没有重开 API |
| `StateText`（`:1071`） | 状态词 | 只作底稿；L0 用 `SurfaceState` | F | 不要把“已完工”直接当 L0 标签 |
| `ActiveStageReady`（`:552`） | 建造/发射门 | 检查行 + 按钮 enable | R | 每个活动级都要引擎+罐 |
| `EnginePlacementError`（`:451`） | 状态条 + 标行 | 检查行 + L1 建造条 + L2 红行 | R | 带 `List<int> offenders` |
| `StageContiguityError`（`:458`） | 状态条 + 标行 | 同上 | R | 带 offenders |
| `Design.parts`（`RocketDesignSpec.cs:38`） | 设计数据 | 画布/L2/检查/计数 | R | **只读遍历；编辑走 comp setter**，不要直接改 |
| `comp.Design.fuelAmount` | 加注读数 | “燃料”检查参考 | R | 完工时由 `FinishBuild` 写；容量用 `FuelForLaunch`；TWR 用 `dryMassKg + capacityKg`，不用 `TotalMassKg` |
| `comp.Progress01/WorkDone/WorkRequired` | 进度条 | L0 建造一句 + 进度条 | R | 保留 |
| `comp.LaunchSecondsLeft/Total` | 倒计时 | L0 倒计时句 | R | 游戏秒，不是物理秒 |
| `comp.ActiveStage(s)` | 活动级 | 检查行 + 级轨点 | R | `ActiveStages` 是 HashSet，每帧会 new |
| `comp.CurrentStage` | 加入级 | 级轨选中 | F | `SetCurrentStage` 只夹 0–4 |
| `comp.CurrentParallel` | 并联 1–7 | 扁平选择器 | R | `SetCurrentParallel` 夹 1–7 |
| `comp.RadialCopy` | 径向复制×4 | 检视栏次动作 | F | 成功后行被替换，选中要清 |
| `comp.MovePart` | ↑/↓ | 检视栏/ L2 | R | 支持任意 delta；父索引会重映射 |
| `comp.RemovePartAt` | `−` 减一件 | 删整行 | F | `qty=row.quantity`；见 D8 |
| `comp.ShiftPartStage` | +级/−级 | 可退役，改 `SetPartStage` | F | `SetPartStage` 更直接 |
| `comp.ClearDesign` | 清空 | L2 溢出 | F | 仅 `CanEditDesign` 时有效 |
| `comp.StartNewRocket` | 下一枚 | 退役/回退 | X/D4 | 会清设计 |
| `comp.QuickAssembleDemoRocket` | 示范 | L2 溢出 | F | 会替换当前设计，需提示 |
| `comp.StartBuild` | 底栏 | L1 建造条 | R | 权威校验仍在 comp |
| `comp.CancelBuild` | 底栏 | L0 主按钮 | R | Building→Designing |
| `comp.BeginLaunch` | 底栏 | L0 主按钮 | R | 失败弹 `why` |
| `comp.CancelLaunch` | 底栏 | L0 主按钮 | R | Launching→Complete |
| `comp.ForceComplete` | 调试 | DevMode 行 | R | 仅 DevMode |
| `catalogCache` | 目录 | 零件带 | R | 保留排序；分类过滤新增 |
| `schematicPan/Zoom` | 平移/缩放 | 保留 | R | 点击/拖拽冲突要处理 |
| `placementOffenders/contiguityOffenders` | 标红 | L1/L2 标红 | R | 每帧重算，O(n) |

---

## 7. R / F / B / X 三分清单

### 7.1 R — 纯重绘（不改行为）

- 窗口底/边/标题/×；`doWindowBackground=false` + `Margin=0` 的扁平板式（D9）。
- 调色板：`WindowBGFillColor`、`#616C7A`、`ColorLibrary.Gold`、`ColorLibrary.RedReadable`、白 4/7/10% 中性阶；删旧 9 色。
- 画布：火箭块、圆头罐、引擎喷口、级间隔线、级号、引线标签、幽灵、图例、网格、取景角标。
- 画布块配色：罐 = 白 7% 底 + 白 20% 边；引擎 = 金 10–12% 叠底（读作暖棕，不是金色高亮）+ 金 60% 边 + 黑喷口；其它 = 白 4% 底 + 白 14% 边；级间隔线 = 白 22%。金色高亮仍只给选中/进行中/可发射。
- 所有面板/分隔线/行底/选中底/悬停底/禁用压暗。
- 动作按钮：原版 `Widgets.ButtonText`，主 44 / 次 32 / 标题 28 / L2 30；主/次靠尺寸+字体层级，不靠颜色。
- 选择器：扁平 1px 片（级轨/分类/并联/级号），金 14% 底 + 金框；不用原版按钮底。
- 微图标：`↑↓×≡⟲` 无底板热区，16–20px。
- L 形角标 / 网格可复用 `SpacecraftUIDecor` 的纯几何方法（`DrawCornerLBrackets`/`DrawLightGrid`），但颜色必须显式传原版值；**禁止 `SpacecraftUIPalette`**（军绿/冷青/暖铜）。
- 文本：`GameFont` 三级 + 颜色/间距；数字右对齐固定列（不是真等宽字体）。
- Tooltip 文案压缩；滚动视图；进度条；状态灯。
- L0/L1 的 12 列 / 8px 基线布局与 flex 重排；L2 是 640×560 的独立栅格。

### 7.2 F — 新增前端逻辑

| # | 逻辑 | 估行 | 风险 |
|---|---|---|---|
| F1 | `SurfaceState` 解析 + 六态主按钮派发 + 一句文案 | 150 | 低；全是读 |
| F2 | 画布选中 + 点击/拖拽阈值 + 空白取消 | 80 | 中；与平移冲突 |
| F3 | 检视栏三模式 + 选中件规格/动作 + 换级片 | 220 | 中；索引随删除/移动变 |
| F4 | 零件带分类过滤 + 卡片 + 并联片 + 更多滚动 | 160 | 低 |
| F5 | L2 独立 Window：暗幕/行/选中/底部动作/滚动/原生 Esc | 240 | 中；父子窗口生命周期 |
| F6 | 检查独立 Window + Build/Launch 适配器 + 站点 g | 150 | 低；口径要对 |
| F7 | `≡` 拖拽调序（一期） | 60 | 中；与 `↑↓` 并存 |
| F8 | 建造条（状态句 + 44px 按钮 + 检查链接） | 80 | 低 |
| F9 | L1 设计 meta 一行（总高/干重/TWR） | 30 | 低；D6 |

合计约 1000–1200 行；和旧窗口 1108 行同量级，但职责分成 L0/L1/L2 三块，可拆 partial。

### 7.3 B — 需要后端授权

| # | 变更 | 文件 | 为什么必须 |
|---|---|---|---|
| B1 | `public bool ReopenDesign()`：`Complete → Designing`，保留 design，清工时/倒计时/`fuelAmount` | `CompRocketAssembler.cs` | C 稿“受阻→编辑设计”唯一干净路径 |
| B2 | gizmo 一行：`new Window_RocketAssemblerC(this)` | `CompRocketAssembler.cs:1113` | 新窗口入口；锁内最小 diff |
| B3 | 可选：`HasWorkerNearby` 改 public | 同上 | 只有想在 L0 写“等待工人”才需要；否则不做 |

**锁的路径问题：** 新窗口文件放 `Presentation/Windows/` 不触发锁；`CompRocketAssembler.cs` 一行会触发。授权件必须点名这一行和 B1，不要用“UI 不算后端”口头解释代替。

### 7.4 X — 退役

- `placeMode`（`:37`）与「自动/轴向/径向」三按钮（`:689-704`）。
- `radialCount`（`:38`）与「对称 2–6」（`:709-718`）——若 D5 合并。
- `Accent` 青、`EngineColor` 橙、`TankColor` 蓝、`OtherColor` 灰、`Muted` 蓝灰、`Bg/Panel/RowBg` 旧 9 色（`:68-76`）。
- `HandlePlacementClick` 里的右键取消（`:364`）和 `KeyCode.Escape`（`:365`）。
- 三块右侧上下叠 + 底栏一排 5–7 个按钮的旧布局。
- 每行 `↑/↓/+级/−级/−` 五个小按钮（改到检视栏/L2 底部；L2 只留 `≡`）。
- A/B 稿的搜索框、模式 Tab、卡片双列（C 稿不采用）。
- `下一枚`（若 B1 通过且 D4 确认退役）。

---

## 8. 实现路线（推荐：新类 + 一行 gizmo）

### 8.1 为什么不是原地重写

- 旧窗口 1108 行，三块布局和状态/目录/清单深度耦合；原地重写会把整份文件都变成锁内 diff。
- 新类放锁外，旧窗口保留可回退；锁内只改 1 行入口（+ 可选 `ReopenDesign`）。
- 新类可以按 L0/L1/L2 拆 partial，单文件 200–300 行，review 容易。

### 8.2 文件布局

```
Source/PioneerFramework/Presentation/Windows/
  Window_RocketAssemblerC.cs           # 构造、窗口属性、DoWindowContents 分派
  Window_RocketAssemblerC.View.cs      # L0
  Window_RocketAssemblerC.Edit.cs      # L1
  Window_RocketAssemblerC.Checks.cs    # Build/Launch 检查口径（纯读）
  Window_RocketAssemblerC.Draw.cs      # 画布/控件/调色板 helpers
  Window_RocketAssemblerList.cs        # L2 独立 Window（行/拖拽/底部动作）
  Window_AssemblerChecks.cs            # 检查小 Window（4/6 行）
```

- 命名空间可用 `PioneerFramework.GroundAssembly`（少写 using）或 `PioneerFramework.Presentation`；类名带 `C` 避免与旧类冲突。
- 旧 `Window_RocketAssembler.cs` 不动；不删、不改。等新窗口验收后由仓库主人决定删除。
- 主窗口 `PreClose` 里关闭 List/Checks 子窗口；子窗口每帧也检查 `comp.parent == null || comp.parent.Destroyed` 自保，避免孤儿。

### 8.3 分期

| 阶段 | 内容 | 文件 | 验收 | 回退 |
|---|---|---|---|---|
| P0 | D1–D4 已拍；D5–D12 给默认；拿 B1/B2 授权件 | 本文、授权件 | 决策记录 + 授权落盘 | — |
| P1 | 窗口壳：1200×720、扁平板式、标题、L0/L1 切换、调色板；gizmo 换新窗口 | `...C.cs`、`.Draw.cs`、`CompRocketAssembler.cs`（B2 一行） | 游戏内开窗；两种模式可切；无异常 | 旧窗口仍可开 |
| P2 | L0：状态解析、主按钮（除 B1）、meta、检查 Window | `.View.cs`、`.Checks.cs`、`Window_AssemblerChecks.cs` | 六态走查；4/6 行检查对 | 主按钮可回旧窗口 |
| P3 | L1：级轨、画布选中、检视栏、零件带、建造条 | `.Edit.cs` | 加/选/移/删/换级/径向复制；建造门不误放 | 旧窗口对比 |
| P4 | L2 Window：暗幕、行、选中、↑↓/≡/换级/删除、滚动、原生 Esc | `Window_RocketAssemblerList.cs` | 11 行调序（含拖拽）/删除；点暗幕不关；× 关 | 关 L2 回 L1 |
| P5 | B1：`ReopenDesign` + 单测 | `CompRocketAssembler.cs`、`RocketAssemblerTests.cs` | 受阻→编辑→改→再建→完工；fuel 归零再满 | revert 方法 |
| P6 | 打磨：平移/缩放、拖拽手感、文案/字形审计 | `.Draw.cs`、`Window_RocketAssemblerList.cs` | `≡/⟲` 字形可显示；拖拽落点准 | 维持 P4 版本 |

### 8.4 测试口径

- UI 没有自动化测试；用游戏内冒烟矩阵：`IdleEmpty → 加件 → Designing → StartBuild → Building → ForceComplete → Complete → 发射/取消 → Launching → 取消/成功 → IdleWithDesign`。
- 用 `QuickAssembleDemoRocket` 快速到 Complete；用 DevMode `ForceComplete` 跳过建造。
- 检查门回归：造一个 TWR<1 的 Complete（重罐+小机），确认 L0 显示受阻、按钮是编辑设计；确认 `BeginLaunch` 仍会被 LaunchService 拒（后端权威不变）。
- B1 单测：Complete 下 `ReopenDesign` 返回 true；`state==Designing`；`workDone/workRequired/launchTicksLeft==0`；`Design.fuelAmount==0`；`parts` 保留；`CanEditDesign==true`。Building/Launching 下返回 false。
- `tools/check-backend-lock.ps1 -Fail`：P0 应绿；P1 起会报 `CompRocketAssembler.cs` 的 gizmo 一行（B2），P5 再加 `ReopenDesign`（B1）——两处都必须被同一份授权件点名。P2–P4 新增文件在锁外，不会增加后端路径。

---

## 9. 待决项

| # | 问题 | 决定 / 推荐 | 影响 | 状态 |
|---|---|---|---|---|
| D1 | 三分法（动作按钮原版底 / 选择器扁平 / 微图标无底） | **已定**：按 3.5 锁定 | 全局控件画法 | 已解（P1） |
| D2 | L2 `≡` 拖拽 | **已定**：一期就做，与 ↑↓ 并存 | L2 交互/60 行 | 已解（P4） |
| D3 | 「开始建造」放哪 | **已定**：L1 右栏底部固定 44px + 状态句 | L1 布局 | 已解（P3） |
| D4 | 受阻→编辑设计 | **已定**：申请 `ReopenDesign()`；改设计需重建；实现前仍需授权件 | L0 受阻态 | 已解（P5，待授权） |
| D5 | 并联/径向对称是否合并 | 合并为 1–7；径向 N≥2，N=1 时侧面=追加 | 放置逻辑/选择器数量 | 待定（阻塞 P3） |
| D6 | L1 是否保留一行设计读数 | 保留 1 行（总高/干重/TWR），不恢复旧三行 | L1 文本预算 | 待定（不阻塞） |
| D7 | 清空/示范/反转/级标签归宿 | L2 底部第二行或表头 `⋯`；清空二次确认 | L2 布局 | 待定（阻塞 P4） |
| D8 | 删除语义 | 删整行；单件减一退役 | 数据操作 | 待定（不阻塞） |
| D9 | 窗口框/边距/缩放 | `doWindowBackground=false`、`Margin=0`、保留 `resizeable`，画布 flex | P1 壳 | 待定（阻塞 P1） |
| D10 | L2 / 检查弹层是 in-window overlay 还是独立 Window | 独立 Window：输入只给顶层、Esc/点外原生；父窗口 `PreClose` 关子窗。若坚持单 Window，则 overlay 打开时必须跳过 L1 全部控件绘制 | L2/检查输入 | 待定（阻塞 P2/P4） |
| D11 | L0 状态词用「已完工」还是「受阻」 | 用「受阻」；meta 写“已完工 · 1 项未通过” | 文案 | 待定（不阻塞） |
| D12 | L2 居中面板挡住左图，「点行→左图高亮」看不到 | 保持居中，去掉“左图高亮”承诺；L2 只做行选中 + 底部动作。若必须左图高亮，面板右移或缩窄 | L2 布局/交互 | 待定（阻塞 P4） |

**D4 语义补充（重要）：** `ReopenDesign()` 会把状态退回 `Designing`，已完工进度和加注清零；「编辑设计」实际是**改设计并重新建造**。按钮文案/确认（比如“编辑设计（需重新建造）”）必须说清。若用户要的是“完工后原地改设计、不重建”，那是另一套状态语义，不在本映射范围，需要单独设计，不能靠 B1 糊过去。

**D10 补充：** 选独立 `Window` 不是为了多开窗，而是因为 IMGUI 的输入顺序：in-window overlay 画在 L1 之后，鼠标事件可能先被下面的 L1 控件吃掉；要修就得在 overlay 打开时完全不画 L1（或逐控件 gate）。独立 `Window` 让窗口栈负责输入隔离，Esc/点外关闭也都是原版行为，不写任何 `keyCode` 分支。

---

## 10. 授权草案（只在本文件里，不落盘）

> 按 `BACKEND-LOCK.md` §4，允许提补丁片段/影响面/判据/回退；不允许自己签授权件。
> 特别提醒：`check-backend-lock.ps1` 只要发现**任意** `后端变更授权-*.md` 就整体放行。所以在 `Source/PioneerFramework/DesignNotes/` 里放“草稿”是危险的——等于给所有后端改动开白名单。本文只做草案，落盘由仓库主人执行。

### B1 草案（未应用）

```csharp
/// <summary>
/// 已完工但发射受阻时，回到设计态改这一枚（保留设计，清工时/加注）。
/// 只改状态与读数，不碰物理/时间基。
/// </summary>
public bool ReopenDesign()
{
    if (state != AssemblerState.Complete) return false;
    state = AssemblerState.Designing;
    workDone = 0f;
    workRequired = 0f;
    launchTicksLeft = 0;
    Design.fuelAmount = 0f;
    return true;
}
```

- 影响面：`Complete` 状态机多一条出边；`CanEditDesign` 不变（仍只认 Idle/Designing），不会误开其它编辑入口。
- 判据：单测 5 条（见 8.4）；游戏内“受阻→编辑→再建→完工→加注满”走通。
- 回退：删方法 + 删调用；旧行为（只能下一枚）恢复。

### B2 草案（未应用）

```csharp
// CompRocketAssembler.CompGetGizmosExtra
action = () => Find.WindowStack.Add(new Window_RocketAssemblerC(this)),
```

- 影响面：只换窗口类型；不动 comp 状态/物理。
- 判据：gizmo 打开新窗口；旧窗口仍可手动 `new Window_RocketAssembler(this)` 回退。
- 回退：一行改回。

---

## 11. 验收（设计阶段结束条件）

- [ ] 每个新控件都能在本表找到 API 或 `D` 项；没有“画了但不知道接哪”的控件。
- [ ] 5.4 的 16 个入口逐项有归宿；退役项有明确理由。
- [x] D1–D4 已拍（2026-11-23；见 §0/§9）；D5–D12 有默认值。
- [ ] 未改任何 `.cs` / `.xml` / 后端；`git status` 仍只有设计目录和无关的 `11.22发帖吧要用/`。
- [ ] `tools/check-backend-lock.ps1`（不带 `-Fail`）在当前树上报告 0 个后端路径（设计文档在根 `DesignNotes/`，不在锁内）。
- [ ] 若批准实现：先拿到点名 `CompRocketAssembler.cs` 的授权件，再开 P1；不先改后报告。
