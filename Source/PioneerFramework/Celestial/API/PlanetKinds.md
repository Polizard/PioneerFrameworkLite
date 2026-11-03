# 通用行星种类

适用 RimWorld 1.7 / 天体框架存档 v2。六类均为可以反复实例化的 `CelestialBodyDef`，已随主 Mod 加载。默认 `PF_DefaultCelestialSystem` 仍只生成太阳、地球和月球。

| DefName | 种类 | 覆盖示例 | 默认地表 |
| --- | --- | --- | --- |
| `PF_BarrenPlanet` | 荒芜行星 | 水星、火星一类荒芜地表；也适用于月球 | 岩原、尘原、极区冰，默认真空 |
| `PF_TemperatePlanet` | 温带行星 | 地球一类具有多种气候带的岩质天体 | 原版地形、生态与殖民地地图 |
| `PF_GreenhousePlanet` | 温室行星 | 金星一类浓厚大气天体 | 灼热干燥地表，代表温度 460°C |
| `PF_FrozenPlanet` | 冰封行星 | 冰卫星、外太阳系冰质小天体 | 冰原与岩石露头，默认真空，代表温度 -160°C |
| `PF_GasGiant` | 气态巨行星 | 木星、土星一类天体 | 无常规可登陆地表 |
| `PF_IceGiant` | 冰巨行星 | 天王星、海王星一类天体 | 无常规可登陆地表 |

表中是宽泛的游戏内容分类。“温带”模板同时包含热带、沙漠与极地；`IceGiant` 描述组分，不表示可以登陆冰面。太阳、卫星、矮行星等轨道或天文学身份独立于这组环境分类。卫星可复用荒芜或冰封模板，提供者的 `bodyType` 与父子关系由作者明确配置。

## 使用入口

### 开发者菜单测试

重新启动游戏加载本次 DLL，开启开发者模式，在调试菜单的 **PFL 测试 → 77 场景 · 行星种类** 中使用：

- **行星种类测试面板**：单独选择一种模板创建，显示测试实例的地表、地图、大气能力和请求结果。
- **创建六类测试天体**：打开面板并一次创建六类。测试实例围绕当前根天体布置在分离轨道上，重复创建会向外扩展；原模板和默认天体保持不变。
- **天体实例**：原有通用检查窗口，可以查看整个世界的实例。

创建后若游戏暂停，点击 **在当前时刻处理挂起请求**。此操作提交请求，物理时间保持不变。每个实例可使用 **在 PFView 中查看**、**生成地表**、**生成地面地图** 和 **移除天体**。生成地表后会切到世界视图；先选中该地表上的一个地块，再重新打开面板生成 250×250 地图。两种巨行星显示“无可登陆地表”，地表和地图按钮禁用。

测试面板会显示实际 **PlanetLayer #编号**；世界地图的 **查看天体** 菜单也使用同一编号。菜单在每次点击时重新读取当前已生成的层，已存在的层可直接选择。若生成失败，面板保留错误代码与完整原因，并写入游戏日志，避免旧创建回执的 Applied 掩盖后续失败。PFView 中显示实例与实际生成地表仍是两个明确步骤。

**清理测试天体** 只处理本面板创建并带有专用标记的实例，仍执行框架的引用保护。存在地图、飞船或其他引用的实例会保留并显示原因。关闭后重新打开同一世界的面板可继续观察等待请求，不会自动重新创建；完成的请求释放其专用代次。测试天体会进入正常存档，建议使用测试世界。

本入口提供人工验收操作；真实 UI、外观和实际地图/存档行为仍需游戏内核对。

### XML 与 API

游戏开发者天体窗口的创建菜单会列出这六个 Def。创建只产生天体实例；四种固体天体通过显式请求按需生成地表，殖民地地图仍需另行请求。两种巨行星没有 surface、biomes、map.generation 能力，地表请求会被拒绝。

第三方系统可直接使用 `<bodyDef>PF_BarrenPlanet</bodyDef>` 等模板。每个系统条目可用 `compOverrides` 按稳定组件 key 替换或增加组件，并显式填写父条目与初始化引用。创建多个条目时应分别配置轨道，避免共用预设相位而重叠。完整系统结构见 [公开 API](README.md) 与实际 `1.7/Defs/CelestialFramework/CelestialSystems.xml`。

动态创建使用现有数据 API，例如：

```csharp
public static CelestialOperation CreateBarrenPlanet(
    string operationId, long epoch, CelestialInstanceId parent,
    CelestialInstanceId frameOrigin, CelestialInstanceId primaryLight)
{
    var services = CelestialWorldServices.TryGetCurrent()
        ?? throw new InvalidOperationException("Celestial services unavailable.");
    var query = services.Query;
    var scope = new CelestialOperationScope(operationId, query.WorldSessionId,
        query.CaptureCatalog().Revision, epoch);
    return services.Bodies.Create(new(scope, "PF_BarrenPlanet", "barren",
        ParentId: parent,
        InitializationReferences: new Dictionary<string, CelestialInstanceId> {
            ["frameOrigin"] = frameOrigin, ["primaryLight"] = primaryLight },
        ParameterOverrides: new[] {
            new CelestialParameterOverride("shape", "radiusMeters", Value: 2500000),
            new CelestialParameterOverride("motion", "gravitationalParameter", Value: 4e12) }));
}
```

调用方先 `Bodies.OpenEpoch` 获取开放代次；保留回执 ID 并查询到终态，在该代次没有 pending 后再显式关闭。`frameOrigin` 应与当前系统的既有提供者配置一致；`primaryLight` 必须指向有 light.source 能力的实例。不要根据界面名称猜测身份或在结果未明确时更换代次重放。

## 代表性参数与覆盖

| 模板 | 半径 m | GM m³/s² | 自转周期 tick |
| --- | ---: | ---: | ---: |
| 荒芜 | 2,500,000 | 4e12 | 60,000 |
| 温带 | 5,000,000 | 2e14 | 60,000 |
| 温室 | 5,000,000 | 2e14 | 1,200,000 |
| 冰封 | 1,200,000 | 8e11 | 60,000 |
| 气态巨行星 | 12,000,000 | 2e15 | 25,000 |
| 冰巨行星 | 8,000,000 | 5e14 | 40,000 |

这些是框架尺度下的代表预设。精确制作某个真实天体时需覆盖其物理参数与运动初始化。默认圆轨道半径为 120,000,000 m、周期 58,400 s、相位 0；没有按太阳系八颗行星的真实轨道排列，也没有自动加入默认世界。六个可选模板现使用同一提供者的 readable motion v2，并保留旧圆轨道提供者兼容。

组件 key：`shape`、`motion`、`surface`、`biomes`、`maps`、`environment`、`air`、`visual`、`atmosphereVisual`、`clouds`。只有实际具备的能力才提供对应组件。`ParameterOverrides` 可修改组件显式支持的数值参数；readable motion 数字支持 `motion.FIELD` / `motion.rotation.FIELD` 参数范围与覆盖；字符串/布尔/引用通过完整 typed motion 组件替换。精确字段、单位、有效区间与codec版本见 [作者表](PlanetAuthoring.md)。

每个模板同时提供 `Name="PF_<种类Def后缀>Base"` 作为 XML 继承名，例如 `PF_BarrenPlanetBase`、`PF_IceGiantBase`。派生 Def 可覆盖 label、category、tags；替换 XML `comps` 列表使用 `Inherit="False"` 并提供完整列表，列表不按 key 自动合并。系统条目的 `compOverrides` 则由框架按 key 处理。派生新 Def 需增加自己的中英文 DefInjected 文本。

## 环境能力与边界

- 荒芜和冰封预设使用专用真空群落。制作有稀薄大气的荒芜行星时，需要同时选择合适的群落/环境 profile，并加入大气组件；只添加空气阻力参数不会改变原生真空规则。
- 温室预设提供固定高温地表、指数大气阻力和云层。本模板显式配置 pressure/composition 与 exposure，具有高压、缺氧和 CO2 毒性危险；云图本身只提供外观。
- 四种新增专用群落均 `generatesNaturally=false`，原版地球不会自然选中它们。地图配置过滤植物、动物、河流及部分人造遗迹步骤，替换土壤和砂砾；矿物与岩体继续使用既有地图生成规则。
- `category`/`tags` 只作开放元数据，能力以组件为准。真空、寒冷、浓厚大气等标签描述当前预设；覆盖组件后需同步调整标签。火山、地下海洋、行星环、潮汐锁定可由扩展组合，不会因标签自动产生效果。
- 外观使用已有颜色、大气壳与原版云图；两巨行星显式选择程序条带，不生成专用贴图。真实游戏中的视觉、登陆和存档重载需另行验收。

## 文件

- `1.7/Defs/CelestialBodies/CelestialBodies_PlanetKinds.xml`：六种可创建、可继承的模板。
- `1.7/Defs/CelestialFramework/CelestialPlanetKindProfiles.xml`：三个外星地表/地图/环境组合。
- `1.7/Defs/CelestialFramework/CelestialPlanetKindVisuals.xml`：地表、大气和云层外观。
- `1.7/Defs/BiomesDefs/Biome_PlanetKinds.xml`：专用群落与原生 XML 继承基类。
- `1.7/Languages/{ChineseSimplified,English}/DefInjected/*/PlanetKinds.xml`：完整配套本地化。

## 参数化内容与实际调试变体

六个 Def 身份保持不变，默认系统仍只创建太阳、地球、月球。四固体模板显式启用冻结的climate/exposure、Parameterized地形与map diversity；温带使用独立profile，原Earth profile保持原样。温带海洋覆盖率范围0.45–0.7；起伏与矿物丰度各实例独立解析。温室压强9.2MPa、温度733K、CO2比例0.965；温带101325Pa、288K、O2比例0.21。两巨行星有pressure/composition与v2条带/云外观，始终没有常规surface/biomes/maps能力。

创建菜单在既有窗口中提供 Default、ThinAir（荒芜600Pa氮气）、Synchronous（荒芜对父体同步）、Ocean（温带0.8海洋/1500mm雨量）、Arid（温带0.02海洋/100mm雨量）、Atmospheric（冰封150kPa氮气）选项。气态/冰巨行星及温室有各自Default。改变大气存在性的变体通过显式组件组合，同时选择非真空原生群落；不修改共享Def。氮气并不提供氧气。同步变体显式带上目标初始化引用，真实提供者保留v2北轴、相位和方向设置。

面板在原有请求/地表导航下显示当前实例半径、密度、GM、重力、逃逸速度、运动、组分、冻结气候、地形、地图候选、视觉与未防护每日风险；查询样本显示坐标、群落、显示tick和物理时刻。缺能力明确标未知。当前Def半径范围仅作作者参考，修改XML不能更改已保存实例的展示值。查询只读取ready能力与现有层；不自动建层或地图。

详细的全部字段、固定/范围/权重语法、ParameterOverrides、完整列表/组件替换、DLC条件、滤毒室内模型和人工验收清单见 [PlanetAuthoring.md](PlanetAuthoring.md)。GPU画面、完整地图、设备手感和真实存读档仍为UNCHECKED。
