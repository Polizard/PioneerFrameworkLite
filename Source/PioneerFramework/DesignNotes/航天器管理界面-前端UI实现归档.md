# 航天器管理界面 — 前端 UI 实现归档

> 状态：已实施并归档
> 日期：2026-08-23
> 性质：前端 UI 当前实现状态存档。基于 [航天器管理界面-视觉风格规划.md](航天器管理界面-视觉风格规划.md) 与 [航天器管理界面实施计划.md](航天器管理界面实施计划.md) 落地。
> 范围：仅前端绘制与本地 UI 状态；后端业务仍通过 `OnXxx()` 占位虚方法接通。

---

## 1. 文件清单

| 文件 | 说明 |
|------|------|
| `Source/PioneerFramework/GroundAssembly/Window_SpacecraftManagement.cs` | 主窗口，继承 `Verse.Window` |
| `Source/PioneerFramework/GroundAssembly/SpacecraftUIPalette.cs` | 调色板常量 |
| `Source/PioneerFramework/GroundAssembly/SpacecraftUIDecor.cs` | 装饰元素绘制工具 |

另外在 `CompAssemblyPad.CompGetGizmosExtra` 中已加入「航天器管理」Gizmo 入口，仅构造窗口，不调用后端副作用。

---

## 2. 窗口结构

自上而下：

1. 主标题：42px，`GameFont.Medium`，银白，垂直居中。
2. Tab 栏：RimWorld 原版 `TabDrawer.DrawTabs`，6 个标签页。
3. 内容区：四角 L 形角标 + 当前标签页内容。

顶部 URL 栏已按需求删除。

### 2.1 关键几何常量

```csharp
private const float TitleHeight = 42f;
private const float TabGap = 34f;
private const float TabBottomOverlap = 1f;
private const float SectionGap = 8f;
private const float RowHeight = 26f;
private const float RowGap = 4f;
private const float ButtonHeight = 36f;
private const float ButtonGap = 12f;
private const float ContentTopPad = 6f;
private const float CardPad = 12f;
private const float LabelWidth = 180f;
```

### 2.2 TabDrawer 实际几何

通过反编译 `Verse.TabDrawer` 确认：

- Tab 按钮主体向上延伸 `31px`
- Tab 按钮底部只压住 `baseRect` `1px`
- 因此：
  - `TabGap >= 31px`，当前取 `34f`
  - 内容区从 `tabRect.yMax` 开始，避免按钮与内容之间产生 35px 空白

---

## 3. 已实现的视觉元素

- 窗口背景：暖调近黑 `#0D1018`
- L 形角标：四角程序化绘制
- SpecCard：3px 语义色左边条 + 面板底 + L 形角标 + 内部分割线
- 卡片分割线：`SpecDivider`，按 `CardPad` 左右内缩
- 网格线背景：轨迹页 40px 步进
- 网点纹理：载荷列表卡片
- 数字读数：等宽右对齐（`GameFont.Tiny` + `MiddleRight`）
- 主按钮 / 描边按钮：程序化绘制
- 单选发射方式：`●` / `○` + 语义色
- 状态指示：`✓` / `●` / `○` / `✗` / `⚠`

### 3.1 Tab 当前页语义色强调线

| 标签页 | 语义色 |
|--------|--------|
| 总览 | 暖铜 |
| 设计 | 银白 |
| 施工 | 军绿 |
| 舱内 | 冷青 |
| 发射 | 暖铜 |
| 轨迹 | 冷青 |

---

## 4. 六个标签页当前内容

| 标签页 | 内容 |
|--------|------|
| 总览 | 发射台状态、设计型号、舱室类型、舱内地图、设计摘要、发射方式、操作按钮 |
| 设计 | 只读展示设计规格、Part 列表、载荷列表 |
| 施工 | 状态机、火箭关联状态、施工进度、按钮 |
| 舱内 | 舱内地图卡片、人员/物品概况、进入舱内按钮 |
| 发射 | 当前火箭、发射台状态、发射方式、条件校验、发射按钮、dev 调试区 |
| 轨迹 | PFL 入轨参数、自动圆轨化、说明、应用轨迹参数按钮 |

---

## 5. 前后端边界

- 前端仅负责绘制和本地 UI 状态：
  - `selectedLaunchMode`
  - `altitudeInputBuffer`
  - `targetOrbitAltitude`
  - `autoCircularize`
- 业务按钮实现为 `protected virtual void OnXxx()`，当前已接通现有后端 API，同时保留下游 override 能力。
- 已接通：新建设计、确认生成框架、进入舱内、统一发射、强制发射、取消设计。
- 尚未接通：轨迹参数写回（当前仅记录本地 UI 状态）。
- 数据来源只读绑定：
  - `CompAssemblyPad`
  - `RocketDesignSpec`
  - `RocketLaunchContext`

---

## 6. 最近调整记录

1. 删除顶部 URL 栏。
2. 修复 Tab 按钮遮挡标题：`TabGap` 提升到 `34f`。
3. 修复内容区与 Tab 距离过远：内容区从 `tabRect.yMax` 开始，去掉多余的 `NavHeight=36f` 空白。
4. 页面标题改用 `RowHeight + MiddleLeft`，标题与 Tab 间距收紧。
5. 新增 Tab 当前页底部语义色强调线。
6. 状态文字补充 `●` / `○` / `✓` 指示。
7. 主要卡片增加内部分割线。

---

## 7. 构建状态

`dotnet build PioneerFramework.csproj`：0 错误，仅项目原有 nullable 警告。

---

## 8. 后续未启动项

- V4：引入 `ISpacecraftPage`，将 6 个 `DrawXxxTab` 重构为独立 Page 类。
- V5：指纹缓存策略。
- V6：override `PreOpen` / `PostClose` 管理生命周期。
- 轨迹数据写回与 `SpacecraftProject` 数据模型。
- 正式 `MapPortal` 登船/装载流程。
- 游戏内人工验证。
