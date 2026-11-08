# PFView 舰船建筑外观 API v1

在舰船地图上建成炮塔、主动雷达或传感器阵列后，拉近 PFView 即可看到对应模型。通过 PFView 工具栏的显示菜单可以切换「舰船建筑模型」。炮塔读取船体系的水平转向，主动雷达的扫描条读取编队当前波束，相同编队的雷达共用该波束。

扩展方通过 `ThingDef.modExtensions` 添加 `PioneerFramework.PFView.ShipBuildingVisuals.ShipBuildingVisualExtension`，再在其 `mounts` 列表里引用一个 `ShipBuildingVisualDef`。一栋建筑可以拥有多个不同 `id` 的挂载。可以从 [扩展示例](../../../../../Examples/ShipBuildingVisualSample/README.md) 复制完整 XML 和 C# 片段。

| 挂载字段 | 含义 |
| --- | --- |
| `id` | 单个扩展内唯一的稳定名称，默认 `main` |
| `visual` | 必填，引用外观 Def |
| `surface` | `Top` 或 `Bottom` 从本次绘制的浮雕或舱盒确定表面。 `Explicit` 使用船体系坐标 |
| `surfaceOffset` | 沿表面向外的间隙，默认 0.03 米 |
| `offset` | 按安装方向旋转的本地位移，单位米 |
| `explicitPositionShip` | `Explicit` 的船体系米坐标 |
| `rotation` | 本地欧拉角，按 X/Y/Z 填俯仰、偏航、滚转，单位度 |
| `followBuildingRotation` | 默认沿用建筑 Rot4 |
| `stateProvider` | 状态提供者 ID，默认 `PFL.Static.v1` |

浮雕位置从建筑占地中心开始计算，偶数占地保留半格偏移，然后应用当前地图所属舱段的位置与旋转。封闭舱在 PFView 使用舱盒。若当前船壳无法提供可验证的表面，自动挂载会跳过该实例，可用 `Explicit` 指定位置。封存地图的模型随地图退出视图，解封后按当前建筑重建。

外观 Def 的 `formatVersion` 目前为 1，最多 32 个节点。每个节点用唯一的 `id` 和可选的 `parentId` 构成层级。局部轴为 +Y 朝外、+Z 朝前。网格原点为本地原点，`position`、`pivot` 用米，`rotation` 用度，`scale` 三轴均为正数。空 `mesh` 用作关节或坐标节点。子节点继承父节点的变换，包括缩放。

| 节点字段 | 含义 |
| --- | --- |
| `meshProvider` 和 `mesh` | 默认 `PFL.Primitives.v1` 提供 `box`、`cylinder`、`dish` |
| `color` | 节点 RGBA 色，分量范围 0 到 1 |
| `bindYaw` 和 `bindPitch` | 将安装局部角度应用于节点，偏航绕 +Y，俯仰绕 +X |
| `bindDeploy` 和 `bindScan` | 用 0 到 1 的比例乘节点平移，两者均存在时扫描通道优先 |
| `activeOnly` | 节点及其子树仅在存在 Active 通道且为 true 时可见 |
| `detailedOnly` | 节点及其子树仅在详细档可见 |

`detailedPixels` 和 `hiddenPixels` 默认分别为 8 和 2。渲染器按各挂载的相机深度估计屏幕尺寸，并在阈值附近留有滞回。简化档省去详细节点，偏航和俯仰使用默认姿态。 `factionTint` 可将节点颜色乘阵营色，损伤色按 16 个区间变化。

注册扩展代码时，在 `[StaticConstructorOnStartup]` 中调用 `ShipBuildingVisualRegistry.Default.RegisterStateProvider` 或 `RegisterMeshProvider`，ID 使用带命名空间和版本的名称。第一次解析外观配置时注册表封闭。状态提供者用 `VisualState.Channels` 标记本次实际返回的值。 `RenderTick` 来自 PFView 渲染时间，暂停时保留最后一帧的相位。提供者可以读取传入的建筑和编队，也应遵守只读契约，避免改变武器、雷达、地图或物理状态。

`PFL.AssetBundle.v1` 可用 `packageId::assets/models/antenna.asset#0` 形式读取已加载的 Mod AssetBundle。末尾数字是可选的子网格编号，默认 0。导出未压缩的三角网格，位置限制在以原点为中心的单位立方体内，附带每个顶点的法线和 UV。多个子网格可分别绑定节点和颜色。运行时复制 CPU 网格数据，游戏继续管理原 AssetBundle 和 Mesh 的生命周期。其他来源可实现 `IShipBuildingVisualMeshProvider`。静态分析已核对 1.7 的 Mesh 读取接口，第三方实际 AssetBundle 显示仍待游戏验收。

游戏内验收可使用已有的战斗测试舰与双舱舰。检查不同朝向及奇偶尺寸占地、开放与封闭舱、船体滚转、炮塔转向、雷达启动和暂停，以及建筑拆除、地图封存和存档重载。性能验收仍需固定相机与场景，统计 500 与 1000 个设备时的提交耗时、批次和资源回收。
