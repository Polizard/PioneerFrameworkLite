# 已退役：PFView 恒星专用视角

2026-10-26，用户明确要求弃用现有恒星视角，按普通行星处理。此处保留原始实现与对应历史测试、着色器，供追溯使用；不参与生产程序集、当前测试工程或 1.7 运行时资源加载。

退役范围包括背景太阳圆盘、恒星专用入场/缩放策略、离屏体积等离子体、独立 HDR 辉光和旧太阳画序。

当前太阳使用 `CelestialBodyManager.GetBodyPositionAbs` 的真实星历，进入普通 Body 相机、球网格和不透明深度流程。恒星仅使用该球材质的自发光系数。现有绝对星历包含地球绕太阳和月球绕地球运动，不改生产轨道积分与存档数据。

仍在使用的共享四边形几何已提取到 `PFViewQuadGeometry`，其 UV/绕序回归保留在 `PFViewQuadGeometryTests`。新验收为 `PFViewPhysicalBodiesTests`，覆盖普通天体路径与真实星历，不再执行本目录中约束旧太阳模式的测试。

后续澄清：用户要求弃用特殊取景，原视觉应保留。当前 PF_StellarVolume 复用本目录中的等离子体数学场，附着实际天体球体，保留普通天体相机。此目录的旧相机、背景圆盘与离屏管线继续归档；当前实现见 MainTabWindow_PFView.StellarVisuals.cs 与 PFViewStellar.xml。
