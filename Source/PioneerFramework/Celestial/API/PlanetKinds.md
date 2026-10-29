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

这些是框架尺度下的代表预设。精确制作某个真实天体时需覆盖其物理参数与运动初始化。默认圆轨道半径为 120,000,000 m、周期 58,400 s、相位 0；没有按太阳系八颗行星的真实轨道排列，也没有自动加入默认世界。当前解析提供者沿用既有圆轨道协议。

组件 key：`shape`、`motion`、`surface`、`biomes`、`maps`、`environment`、`air`、`visual`、`atmosphereVisual`、`clouds`。只有实际具备的能力才提供对应组件。`ParameterOverrides` 可修改组件显式支持的数值参数；轨道载荷通过系统 `compOverrides` 的完整 `motion` 组件修改，可用 `tools/celestial_payload.py` 编解码。调整自转时需同步 motion 载荷的 `dayLengthTicks` 与环境 profile。

每个模板同时提供 `Name="PF_<种类Def后缀>Base"` 作为 XML 继承名，例如 `PF_BarrenPlanetBase`、`PF_IceGiantBase`。派生 Def 可覆盖 label、category、tags；替换 XML `comps` 列表使用 `Inherit="False"` 并提供完整列表，列表不按 key 自动合并。系统条目的 `compOverrides` 则由框架按 key 处理。派生新 Def 需增加自己的中英文 DefInjected 文本。

## 环境能力与边界

- 荒芜和冰封预设使用专用真空群落。制作有稀薄大气的荒芜行星时，需要同时选择合适的群落/环境 profile，并加入大气组件；只添加空气阻力参数不会改变原生真空规则。
- 温室预设提供固定高温地表、指数大气阻力和云层。现有框架尚未实现压强伤害、二氧化碳窒息或腐蚀性云层伤害，这些能力需要额外环境组件。
- 四种新增专用群落均 `generatesNaturally=false`，原版地球不会自然选中它们。地图配置过滤植物、动物、河流及部分人造遗迹步骤，替换土壤和砂砾；矿物与岩体继续使用既有地图生成规则。
- `category`/`tags` 只作开放元数据，能力以组件为准。真空、寒冷、浓厚大气等标签描述当前预设；覆盖组件后需同步调整标签。火山、地下海洋、行星环、潮汐锁定可由扩展组合，不会因标签自动产生效果。
- 外观使用已有颜色、大气壳与原版云图；当前未新增条带风暴、行星环或专用贴图。真实游戏中的视觉、登陆和存档重载需另行验收。

## 文件

- `1.7/Defs/CelestialBodies/CelestialBodies_PlanetKinds.xml`：六种可创建、可继承的模板。
- `1.7/Defs/CelestialFramework/CelestialPlanetKindProfiles.xml`：三个外星地表/地图/环境组合。
- `1.7/Defs/CelestialFramework/CelestialPlanetKindVisuals.xml`：地表、大气和云层外观。
- `1.7/Defs/BiomesDefs/Biome_PlanetKinds.xml`：专用群落与原生 XML 继承基类。
- `1.7/Languages/{ChineseSimplified,English}/DefInjected/*/PlanetKinds.xml`：完整配套本地化。
