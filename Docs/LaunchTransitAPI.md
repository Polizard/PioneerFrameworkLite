# 独立飞船起飞过场接口

入口位于 `PioneerFramework.Launch.LaunchTransitService`。调用方需要准备好已有的 `SpacecraftMapParent` 和目标 `OrbitState`。

```csharp
bool started = LaunchTransitService.TryStart(
    groundMap: groundMap,
    spacecraft: spacecraft,
    bodyId: centralBodyId,
    factionId: ownerFactionId,
    targetOrbit: targetOrbit,
    durationTicks: 180,
    transit: out LaunchTransitWorldObject transit,
    reason: out string reason);
```

`targetOrbit.positionSystem` 的单位为米，`velocitySystem` 的单位为米/秒，采用框架已有的系统系。`rotation` 应提供有效姿态，例如 `Quaternion.identity`。`durationTicks` 使用游戏 tick；暂停会暂停过场。起点从地面地图的世界地块换算，中间路径随中心天体的位置移动。目标状态在到达时直接使用，不自动传播到其他轨道时刻。过场使用视觉路径，不消耗燃料。

启动时按当前物理速度检查预计到达位置。过场中若切换物理时间倍率，结束时还会重新校验；目标若已进入天体内部，过场会失败，图标停在天体表面外，调用方可以取消并选择新的目标。

调用成功后立即登记临时 `PF_LaunchTransit` WorldObject。期间飞船地图保持地面状态且没有 group。到达终点后创建并注册 entity 和 group，绑定原飞船地图，标记地图为 `InSpace`，随后删除临时对象。

可以通过 `LaunchTransitService.TryFindByMapId(mapId, out transit)` 查找尚未移除的过场，通过 `transit.Status`、`transit.FailureReason`、`transit.ProgressAt(tick)` 查询状态。完成后可以通过飞船地图的 `SpaceMapComp.ownerEntityId` 查询最终实体，再由实体的 `GroupId` 查询 group。保存并读取游戏时，过场继续使用保存的起点、目标和 tick 数据。

`LaunchTransitService.TryCancel(transit, out reason)` 支持取消进行中或失败的过场。失败对象会保留错误信息，并阻止同一地图再次启动；调用方可以取消后重新发起。

调用方负责生成飞船地图、准备推进数据及处理地面火箭和发射台状态。现有 `LaunchService`、`RocketLaunchManager`、`CompAssemblyPad` 调用点尚未接入此接口。

游戏内验收需要覆盖：世界图标从地块升起并到达目标；暂停与速度切换；过场途中存档读档；完成后原地图只绑定一个实体和 group；移除地图导致失败以及取消后重试。
