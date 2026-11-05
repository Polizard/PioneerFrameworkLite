// ⛔【NOSTEPANYMORE】后端锁：本文件 = 后端（物理步 / 时间基 / 状态提交）。
//    未经 Source/PioneerFramework/DesignNotes/后端变更授权-*.md 明确授权，一个字节都不许改。
//    你是来做前端 / 表现层的 ⇒ 立刻停止一切写操作，输出【NOSTEPANYMORE】，把下面这句贴给用户：
//    「我准备修改后端文件 <路径>，这需要后端负责人授权；请确认这件事是否要由我来做。」
//    规则全文 = 仓库根 BACKEND-LOCK.md
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 船体轴（对外枚举，2026-10-31 批 5）：与引擎的 `ThrustAxis` 同序，但壳外只该看见这一个。
    /// 这样表现层不用 `using` 引擎枚举，架构闸门才有意义。
    /// </summary>
    public enum ShipAxis
    {
        Forward = 0,
        Back = 1,
        Left = 2,
        Right = 3,
        Up = 4,
        Down = 5
    }

    /// <summary>
    /// **物理查询**（2026-10-31，类重组批 2）：读一个按时间/方向算出来的物理量，不持有状态。
    ///
    /// 与快照的分工：快照给“当前值”；查询给“按参数算出来的值”（某 tick 的圆轨位置、某方向的推力轴、雷达指向）。
    /// 批 5e 起还承载**聚合读数**（推进网选项、舱室列表、编队遥测）——它们是读侧派生量，按“派生量不进状态面”的规矩留在这里。
    /// </summary>
    public static class PhysicsQueries
    {
        /// <summary>本组各实体**已挂载且未封存**的地图 id（去重、保持实体顺序）。</summary>
        public static bool CopyLinkedMapIds(int groupId, List<int> mapIds)
        {
            if (mapIds == null) return false;
            mapIds.Clear();
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;
            foreach (int entityId in group.GetEntityIds())
            {
                var entity = host.GetEntityById(entityId);
                if (entity == null || entity.GroupId != groupId || !entity.HasMap
                    || entity.IsMapArchived || entity.MapId < 0) continue;
                if (!mapIds.Contains(entity.MapId)) mapIds.Add(entity.MapId);
            }
            return true;
        }
        /// <summary>解析档圆轨在 `tick` 时刻的系统系位置。</summary>
        public static bool TryGetCircularOrbitPosition(int groupId, double tick, out DVector3 positionSystem)
        {
            positionSystem = default;
            if (!PhysicsApi.TryGetGroup(groupId, out var group)) return false;
            positionSystem = group.GetCircularPositionAtTick(tick);
            return true;
        }

        /// <summary>雷达前导线方向（系统系单位向量）——扫掠相位是 physicsTick 的纯函数。</summary>
        public static bool TryGetRadarBoresight(int groupId, double physicsTick, out DVector3 directionSystem)
        {
            directionSystem = default;
            if (!PhysicsApi.TryGetGroup(groupId, out var group)) return false;
            directionSystem = group.RadarBoresight(physicsTick);
            return true;
        }

        /// <summary>
        /// 编队在 `tick` 时刻的**系统系位置**（渲染插值口径）。绝对世界坐标 = 本值 + 顶层行星位置，
        /// 但那一步属于**表现层**（绝对帧是 display-only，见 frame-crossing 闸门），所以不在引擎里做。
        /// </summary>
        public static bool TryGetGroupSystemPosition(int groupId, double tick, out DVector3 positionSystem)
        {
            positionSystem = default;
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;
            positionSystem = host.RenderedGroupPosition(group, tick);
            return true;
        }

        /// <summary>船体某轴的推力方向（系统系单位向量）——对外版本，只暴露 <see cref="ShipAxis" />。</summary>
        public static bool TryGetThrustAxis(int groupId, ShipAxis axis, out DVector3 directionSystem)
            => TryGetThrustAxis(groupId, (ThrustAxis)(int)axis, out directionSystem);

        /// <summary>主推进轴方向（系统系单位向量）。</summary>
        public static bool TryGetMainThrustAxis(int groupId, out DVector3 directionSystem)
        {
            directionSystem = default;
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;
            directionSystem = host.GetMainThrustAxisSystem(group);
            return true;
        }

        /// <summary>船体某轴的推力方向（系统系单位向量）。</summary>
        public static bool TryGetThrustAxis(int groupId, ThrustAxis axis, out DVector3 directionSystem)
        {
            directionSystem = default;
            if (!PhysicsApi.TryGetGroup(groupId, out var group)) return false;
            directionSystem = group.GetThrustAxisSystem(axis);
            return true;
        }

        // ── 批 5e（2026-11-01）：PFView 取数改 id 前的查询面 ──

        /// <summary>
        /// **组的主图**（批 5e）：与 `CanOperateGroup` 的操舵台准入同一口径——`group.GetLinkedMapComp()?.map`。
        /// 无图 / 未关联 / 图未加载 ⇒ false。
        /// </summary>
        public static bool TryGetLinkedMap(int groupId, out Map map)
        {
            map = null!;
            if (!PhysicsApi.TryGetGroup(groupId, out var group)) return false;
            var mapComp = group.GetLinkedMapComp();
            if (mapComp?.map == null) return false;
            map = mapComp.map;
            return true;
        }

        /// <summary>
        /// **推进网络选项**（批 5e）：写进 buffer（先 Clear）。口径照搬旧网络选择器：
        /// 只收 `ṁ &gt; 0` 的网、同工质去重（首见者胜）、`AvailableKg` = 该网工质现存料。
        /// 组存在但没有推进源 ⇒ true + 空表；组不存在 / 没有引擎 ⇒ false + 空表。
        /// </summary>
        public static bool CopyNetworkOptions(int groupId, List<NetworkOptionSnapshot> buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            buffer.Clear();
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;

            var source = host.GetGroupPropulsionSource(group);
            if (source == null) return true;
            var seen = new HashSet<string>();
            var nets = source.Networks;
            for (int i = 0; i < nets.Count; i++)
            {
                var net = nets[i];
                if (net.propellant == null || net.MassFlow <= 0f) continue;
                string key = net.propellant.defName;
                if (!seen.Add(key)) continue;
                buffer.Add(new NetworkOptionSnapshot
                {
                    DefName = key,
                    Label = net.propellant.label,
                    IsRcs = net.HasRcsEngines,
                    Ve = net.Ve,
                    MassFlow = net.MassFlow,
                    AvailableKg = source.GetPropellantMass(net.propellant),
                });
            }
            return true;
        }

        /// <summary>
        /// **舱室件列表**（批 5e）：写进 buffer（先 Clear），顺序 = 组内实体顺序 × 件顺序。
        /// 只收未毁的舱室件（`IsRoomModulePart`）；组不存在 / 没有引擎 ⇒ false + 空表。
        /// </summary>
        public static bool CopyRoomParts(int groupId, List<RoomPartSnapshot> buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            buffer.Clear();
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;

            foreach (int entityId in group.GetEntityIds())
            {
                var entity = host.GetEntityById(entityId);
                if (entity == null || entity.GroupId != groupId) continue;
                var parts = entity.Parts;
                for (int i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    if (part == null || !part.IsRoomModulePart) continue;
                    buffer.Add(new RoomPartSnapshot
                    {
                        PartId = part.partId,
                        EntityId = entity.EntityId,
                        RoomLabel = part.RoomTypeDef?.label,
                    });
                }
            }
            return true;
        }

        /// <summary>
        /// **编队遥测**（批 5e）：部件健康（最小 HP / 已毁数）、图装载（可进入 / 归档未加载 / 受损建筑）、
        /// Δv 与续航、成员与网络明细。数据部分与旧 `PFViewFleetTelemetry` 逐项一致（武器仍由调用方经
        /// `CombatManager` 自己取）。组不存在 / 没有引擎 ⇒ false（`snapshot = null`）。
        /// **分配**：每次 new 一份，UI 刷新频率调用，不在物理步内。
        /// </summary>
        public static bool TryCollectTelemetry(int groupId, out TelemetrySnapshot snapshot)
        {
            snapshot = null!;
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;

            var result = new TelemetrySnapshot { GroupId = groupId };
            var maps = new HashSet<int>();   // 只用于建筑计数去重；循环后不再读（未装载图不进集合不影响其它读数）
            foreach (int entityId in group.GetEntityIds())
            {
                var entity = host.GetEntityById(entityId);
                if (entity == null || entity.GroupId != groupId) continue;
                int destroyed = 0;
                var parts = entity.Parts;
                for (int i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    if (part == null) continue;
                    if (part.isDestroyed) { destroyed++; continue; }
                    if (!part.isIndestructible && float.IsFinite(part.hp))
                        result.MinimumPartHp = Math.Min(result.MinimumPartHp ?? part.hp, part.hp);
                }
                result.DestroyedParts += destroyed;
                result.AddMember(new TelemetryMemberSnapshot
                {
                    EntityId = entity.EntityId,
                    PartCount = parts.Count,
                    DestroyedPartCount = destroyed,
                });
                if (entity.IsMapArchived) { result.UnloadedMaps = true; continue; }
                var mapIds = entity.GetMapIds();
                for (int i = 0; i < mapIds.Count; i++)
                {
                    int mapId = mapIds[i];
                    var mapComp = host.GetMapComponent<SpaceMapComp>(mapId);
                    if (mapComp?.map == null) { result.UnloadedMaps = true; continue; }
                    // 2026-11-02 批 6（交接 §6.9 观察项裁定）：`HasMap` 是**实体级**事实，必须在图去重之前按实体复核。
                    // 组级口径 = **任一**实体为 true（旧实现也是 any，去重只是漏检）；旧写法把复核塞在 `maps.Add`
                    // 之后，共享同一张图的后续实体会被跳过，首个实体 `HasMap=false` 时整组误报"无图"。
                    // 建筑计数仍按图去重（同一张图的受损建筑只数一次）。
                    if (entity.HasMap && entity.MapId == mapId) result.HasMap = true;
                    if (!maps.Add(mapId)) continue;
                    var buildings = mapComp.map.listerThings?.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
                    if (buildings == null) continue;
                    for (int b = 0; b < buildings.Count; b++)
                    {
                        var building = buildings[b];
                        if (!building.Destroyed && building.def.useHitPoints && building.HitPoints < building.MaxHitPoints)
                            result.DamagedBuildings++;
                    }
                }
            }
            result.SortMembers();

            var source = host.GetGroupPropulsionSource(group);
            result.HasPropulsion = source != null && source.Networks.Count > 0;
            var (dv, _, mass, capacity) = host.GetGroupDeltaV(group);
            result.ReferenceDeltaV = dv;
            result.PropellantMass = mass;
            result.PropellantCapacity = capacity;
            if (source != null)
            {
                var report = host.GetGroupEnduranceReport(group);
                for (int i = 0; i < report.Count; i++)
                {
                    var network = report[i];
                    double endurance = network.enduranceSeconds;
                    result.AddNetwork(new TelemetryNetworkSnapshot
                    {
                        Name = network.propellant.LabelCap,
                        MassKg = source.GetPropellantMass(network.propellant),
                        EnvironmentDependent = network.environmentDependent,
                        EnduranceSeconds = endurance,
                    });
                    if (network.environmentDependent)
                    {
                        result.EnvironmentDependent = true;
                        result.ShortestEndurance = Math.Min(result.ShortestEndurance, endurance);
                    }
                }
            }
            snapshot = result;
            return true;
        }
        /// <summary>
        /// **组在 `tick` 是否已出生**（渲染准入）：出生过渡前的帧不画。
        /// 口径 = 宿主 `GroupIsVisibleAt`（`physicsBirthSourceId &lt; 0 && tick &gt;= physicsBirthTick`）。
        /// </summary>
        public static bool GroupVisibleAt(int groupId, double tick)
        {
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            return group != null && host.GroupIsVisibleAt(group, tick);
        }

        /// <summary>
        /// **开火/物理基准位置**（系统系）：圆轨走解析式，其余走已提交 `positionSystem`，**不做渲染插值**
        /// （渲染插值用 <see cref="TryGetGroupSystemPosition" />）。与 `CombatManager` 的开火基准同口径。
        /// </summary>
        public static bool TryGetGroupExactPosition(int groupId, double physicsTick, out DVector3 positionSystem)
        {
            positionSystem = default;
            if (!PhysicsApi.TryGetGroup(groupId, out var group)) return false;
            positionSystem = group.IsCircularOrbit
                ? group.GetCircularPositionAtTick(physicsTick)
                : group.CurrentOrbit.positionSystem.v;
            return true;
        }

        /// <summary>
        /// **地图浮雕数据**（PFView 有图编队的表示）：图 + 逐格分类 + 版本号。
        /// 取图口径与旧 `SpaceMapGenerator.GetEntityMap` 一致（`FindPocketMap`，**不依赖** `mapComponents` 缓存——
        /// 那张表不随存档恢复，读档后靠这里兜底）。无图 / 无 `SpaceMapComp` / 尚无建造痕迹 ⇒ false。
        /// </summary>
        public static bool TryGetMapRelief(int mapId, out Map map, out byte[] classes, out int version)
        {
            map = null!;
            classes = Array.Empty<byte>();
            version = 0;
            if (mapId < 0) return false;
            var found = SpaceMapGenerator.FindPocketMap(mapId);
            var mapComp = found?.GetComponent<SpaceMapComp>();
            if (mapComp == null) return false;
            var data = mapComp.GetReliefClasses();
            if (data == null || !mapComp.ReliefHasContent) return false;
            map = found!;
            classes = data;
            version = mapComp.ReliefVersion;
            return true;
        }

        // ── 批 5e §4.3b：机动队列 / 规划器 / 交会窗口 ──

        /// <summary>
        /// **机动队列条目**（写进 buffer，先 Clear）：含已取消条目，索引 = 队列下标（取消动作用它）。
        /// 分量口径与旧窗口一致；`UseNetworkDefNames` null = 全启用。
        /// </summary>
        public static bool CopyPlannedManeuvers(int groupId, List<PlannedManeuverSnapshot> buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            buffer.Clear();
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;
            var nodes = group.plannedManeuvers;
            for (int i = 0; i < nodes.Count; i++)
            {
                var pm = nodes[i];
                string[]? useDefs = null;
                string networkLabel;
                if (pm.useNetworks == null)
                {
                    networkLabel = "全启用";
                }
                else
                {
                    var defs = new List<string>(pm.useNetworks.Count);
                    var labels = new List<string>(pm.useNetworks.Count);
                    foreach (var def in pm.useNetworks)
                    {
                        if (def == null) { labels.Add("?"); continue; }
                        defs.Add(def.defName);
                        labels.Add(def.label);
                    }
                    useDefs = defs.ToArray();
                    networkLabel = string.Join("+", labels);
                }
                buffer.Add(new PlannedManeuverSnapshot
                {
                    Index = i,
                    Active = pm.active,
                    ExecuteTick = pm.executeTick,
                    RadialDv = pm.radialDV,
                    TangentialDv = pm.tangentialDV,
                    NormalDv = pm.normalDV,
                    UseNetworkDefNames = useDefs,
                    NetworkLabel = networkLabel,
                    IntentDescription = pm.intent?.DescribeSelf(),
                });
            }
            return true;
        }

        /// <summary>
        /// **一次机动的燃烧估算**（批 5e §4.3b）：现算、不缓存。
        /// 口径与旧窗口相同：干质量按实体求和、`networkDefNames` null = 全启用、空数组 = 一条都不启用。
        /// 组不存在 / 没有引擎 ⇒ false。
        /// </summary>
        public static bool TryBuildBurnQuote(int groupId, float dvMps, IReadOnlyList<string>? networkDefNames,
            out BurnQuote quote)
        {
            quote = null!;
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;

            float dryMass = 0f;
            foreach (int entityId in group.GetEntityIds())
            {
                var entity = host.GetEntityById(entityId);
                if (entity != null) dryMass += entity.TotalMass;
            }

            HashSet<PropellantDef>? enabled = null;
            if (networkDefNames != null)
            {
                enabled = new HashSet<PropellantDef>();
                for (int i = 0; i < networkDefNames.Count; i++)
                {
                    var def = DefDatabase<PropellantDef>.GetNamedSilentFail(networkDefNames[i]);
                    if (def != null) enabled.Add(def);
                }
            }

            var source = host.GetGroupPropulsionSource(group);
            var estimate = source != null
                ? BurnCalculator.Estimate(dvMps, dryMass, source, source.Networks, enabled)
                : default;
            var result = new BurnQuote
            {
                HasNetworks = estimate.Networks != null && estimate.Networks.Count > 0,
                Feasible = estimate.Feasible,
                RequiredPropellant = estimate.RequiredPropellant,
                BurnSeconds = estimate.BurnSeconds,
                MaxDeltaV = estimate.MaxDeltaV,
            };
            if (estimate.Networks != null)
                for (int i = 0; i < estimate.Networks.Count; i++)
                {
                    var net = estimate.Networks[i];
                    result.AddNetwork(new BurnNetworkQuote
                    {
                        PropellantLabel = net.Propellant?.label,
                        IsReserve = net.IsReserve,
                        Feasible = net.Feasible,
                        AvailableKg = net.Available,
                        RequiredKg = net.RequiredPropellant,
                    });
                }
            quote = result;
            return true;
        }

        /// <summary>组在 `tick` 的规划轨道视图（`OrbitAdapter` 同一条构造路径）。</summary>
        public static bool TryToOrbitView(int groupId, double tick, out OrbitView view)
        {
            view = default;
            if (!PhysicsApi.TryGetGroup(groupId, out var group)) return false;
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            return OrbitAdapter.TryToOrbitView(group, mgr, tick, out view);
        }

        /// <summary>
        /// **交会相对状态**（批 5e §4.3b）：与目标窗口同口径——两船各走 `OrbitAdapter`，相位差用
        /// `RendezvousLadder.PhaseDifferenceDeg`。任一船取不到轨道 ⇒ false。
        /// </summary>
        public static bool TryGetRendezvousProbe(int selfGroupId, int otherGroupId,
            out double distanceKm, out double relVelMps, out double phaseDeg)
        {
            distanceKm = 0.0;
            relVelMps = 0.0;
            phaseDeg = 0.0;
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var self = host.GetGroupById(selfGroupId);
            var other = host.GetGroupById(otherGroupId);
            if (self == null || other == null) return false;
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return false;
            double tick = TimeScaleManager.GetPhysicsTickSafe();
            if (!OrbitAdapter.TryToOrbitView(self, mgr, tick, out var ovSelf)
                || !OrbitAdapter.TryToOrbitView(other, mgr, tick, out var ovTgt)) return false;
            var (rSelf, vSelf) = ovSelf.RightHandedStateVectorsAtUT(tick);
            var (rTgt, vTgt) = ovTgt.RightHandedStateVectorsAtUT(tick);
            distanceKm = (rTgt - rSelf).magnitude / 1000.0;
            relVelMps = (vTgt - vSelf).magnitude;
            phaseDeg = RendezvousLadder.PhaseDifferenceDeg(ovSelf, ovTgt, tick);
            return true;
        }

        /// <summary>
        /// **选点着陆报价**（批 5e §4.3b）：校验链与结算 `TryLandAtTile` 共用宿主同一条实现；
        /// 这里只把窗口要的读数摊平（Δv / 所需工质 / 现有 Δv 余量 / 天体名 / 落点描述 / 拒绝原因）。
        /// </summary>
        public static bool TryQuoteLanding(int groupId, int targetBodyId, PlanetTile target,
            out double deltaV, out float requiredPropellant, out float remainingDv,
            out string bodyName, out string spot, out string? reason)
        {
            deltaV = 0.0;
            requiredPropellant = 0f;
            remainingDv = 0f;
            bodyName = "";
            spot = "";
            reason = null;
            if (!PhysicsApi.TryGetHost(out var host)) { reason = "no-engine"; return false; }
            var group = host.GetGroupById(groupId);
            if (group == null) { reason = "group-not-found"; return false; }
            bool ok = host.TryQuoteLanding(group, targetBodyId, target, out var quote, out var estimate,
                out bodyName, out spot, out reason);
            deltaV = quote.deltaV;
            requiredPropellant = estimate.RequiredPropellant;
            remainingDv = host.GetGroupDeltaV(group).remainingDv;
            return ok;
        }

        // ── 批 5e §4.3b：搬运窗口 ──

        /// <summary>**逐网续航读数**（搬运窗口用；比整份遥测轻，只走推进报告）。写进 buffer（先 Clear）。</summary>
        public static bool CopyEnduranceNetworks(int groupId, List<TelemetryNetworkSnapshot> buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            buffer.Clear();
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;
            var source = host.GetGroupPropulsionSource(group);
            if (source == null) return true;
            var report = host.GetGroupEnduranceReport(group);
            for (int i = 0; i < report.Count; i++)
            {
                var network = report[i];
                buffer.Add(new TelemetryNetworkSnapshot
                {
                    Name = network.propellant != null ? network.propellant.label : "?",
                    MassKg = network.propellantMass,
                    EnvironmentDependent = network.environmentDependent,
                    EnduranceSeconds = network.enduranceSeconds,
                    MassFlow = network.massFlow,
                });
            }
            return true;
        }

        /// <summary>**悬浮预检**（批 5e §4.3b）：与生产门同一份判据，`why` 是具体原因。</summary>
        public static bool CanHover(int groupId, bool analytic, out string? why)
        {
            why = null;
            if (!PhysicsApi.TryGetHost(out var host)) { why = "no-engine"; return false; }
            var group = host.GetGroupById(groupId);
            if (group == null) { why = "group-not-found"; return false; }
            return host.CanHover(group, analytic, out why);
        }

        /// <summary>
        /// **地图的船主编队**（批 5e §4.3b：Gizmo 入口用）：只有"玩家自己的船"才返回 groupId，否则 -1。
        /// 取图口径 = `GetMapComponent` 优先、`FindPocketMap` 兜底（与 `TryGetMapRelief` 同理）。
        /// </summary>
        public static int ResolvePlayerGroupIdForMap(int mapId)
        {
            if (mapId < 0 || !PhysicsApi.TryGetHost(out var host)) return -1;
            var mapComp = host.GetMapComponent<SpaceMapComp>(mapId);
            if (mapComp == null)
                mapComp = SpaceMapGenerator.FindPocketMap(mapId)?.GetComponent<SpaceMapComp>();
            if (mapComp == null || mapComp.ownerEntityId < 0) return -1;
            var entity = host.GetEntityById(mapComp.ownerEntityId);
            if (entity == null) return -1;
            var group = host.GetGroupById(entity.GroupId);
            var player = Faction.OfPlayer;
            if (group == null || player == null || group.FactionId != player.loadID) return -1;
            return group.GroupId;
        }
    }
}
