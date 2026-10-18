# 人工表面扩展样例

这是独立程序集 `PFL.ArtificialSurfaceSample`，未自动装入主 Mod。样例采用 3×2 个四边形地块，用于验证公开契约及通用服务。

使用项目固定 SDK 构建本工程。需要游戏内验证时，将生成的样例 DLL 和本目录 Defs 作为依赖 PFL 的独立测试 Mod 加载；保留本 Mod 的主 DLL。该样例没有研究、建造成本或其他新玩法。

入口为 `RectangularBackend`，通过 `MegastructureRegistry.RegisterBackendProvider` 注册。XML 提供定义与模块组合；`RectangularLayer` 实现原生表面和保存头契约；`RectangularGeometry` 只负责几何及四邻接。原生保存头先于网格恢复。

| 职责 | 实际消费路径 | 自动证据 |
| --- | --- | --- |
| G1 生命周期 | RectangularBackend → StructureBackendService / manager.Create | RingWorldManagedLifecycleTests：独立原生矩形层导入、解除、再导入 |
| G2 表面与地址 | RectangularGeometry → PlanetLayerGridBridge / SurfaceAddress | ArtificialSurfaceGridTests、ArtificialSurfaceReuseTests |
| G3 周期运动 | descriptor.Motion → SurfaceMotionBridge / SurfaceSpatialMath | ArtificialSurfaceGeometryTests、相机跟随复用测试 |
| G4 查询寻路 | SurfacePathSearch / SurfaceQueryService | 矩形 0→5 最短路径与距离筛选 |
| G5 地图站点 | manager.MapSites → SettlementMapSiteProvider | 同一真实 Map 对象复用、父对象变化保护 |
| G6 环境 | SurfaceEnvironmentBridge / ArtificialCycle | 初始温度、降雨与昼夜数值 |
| G7 表现 | RectangularDrawLayer → SurfaceMeshParts | 四边形 fan、主体拾取、装饰无映射 |
| G8 视图 | RectangularView → SurfaceViewController / SurfaceCameraFollow | 公共视图约束编译、绝对时刻跟随与 Reset |
| G9 对象定位 | SurfaceWorldObjectBridge.CellPoint | 非零 Origin 仅加一次 |
| G10 动作权限 | manager.Actions / Query / Execute | 验证动作、过期修订和越权地图拒绝 |

完整地图生成、商队动画、真实相机/材质绘制、保存退出重载与实机性能仍需在 RimWorld 中验收。头部序列化及宿主测试不覆盖这些完整游戏流程。
