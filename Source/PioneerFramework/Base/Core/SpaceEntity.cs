using System.Collections.Generic;
using UnityEngine;
using Verse;
using PioneerFramework.Components;

namespace PioneerFramework
{
    /// <summary>
    /// 空间实体（载具：船/空间站 = 单 part + 可选内部地图；无 Map part 如导弹为纯物理实体）。
    /// Lite 裁剪：装配系统（Ship3D）已删除；武器缓存与 Morton 部件索引已删除（§5.4 C1）。
    /// 实现 IPropulsionSource：无 Map 实体自带推进数据（PropulsionData），有 Map 时 burn 走 SpaceMapComp。
    /// </summary>
    public class SpaceEntity : IExposable, IPropulsionSource
    {
        private int entityId;
        public int EntityId => entityId;

        private int groupId;
        public int GroupId
        {
            get => groupId;
            set => groupId = value;
        }

        private int factionId;
        public int FactionId
        {
            get => factionId;
            set => factionId = value;
        }

        public bool CanInteract = false;
        public bool IsSelectable = true;
        public bool HasMap = false;
        public bool IsMapArchived = false;  // 地图已被封存到磁盘
        public bool IsMovable = true;
        public bool IsAffectedByGravity = true;
        public bool IsCollidable = true;

        public DVector3 BoundsSize = new DVector3(5000.0, 3000.0, 5000.0);

        /// <summary>
        /// AABB 半边长 = BoundsSize × 0.5（真实尺寸）。
        /// 空间站 Part 尺寸 = mapWidth×50m×mapHeight → AABB 面积与 Map 1:1、高 50m，
        /// 3D 物理 ↔ Map 直接映射（贯穿弹道入口点落在 Map 边缘）。
        /// 注意：不做旋转保守 2 倍放大——轴对齐 AABB 忽略旋转时角部有覆盖缺口，
        /// 以面积精确对齐换取 1:1 映射（空间站本体旋转不参与近战碰撞判定）。
        /// </summary>
        public DVector3 AabbHalfSize => BoundsSize * 0.5;

        /// <summary>
        /// 旋转后的世界轴对齐包围盒（相对 entity 原点，OBB→AABB 投影并集）。
        /// 世界系用途（船-船碰撞 / 半径 / 战斗格子占用）用它——旋转后 part 会超出未旋转盒。
        /// 注意：射弹命中检测在船体系用 AabbHalfSize（part 局部正盒），不要改成这个。
        /// 每 tick 懒缓存：同一 tick 内被碰撞检测多次调用（球快筛/扫掠盒/分离量）只算一次。
        /// </summary>
        public void GetAabbSystem(out DVector3 min, out DVector3 max)
        {
            int tick = Find.TickManager?.TicksGame ?? -1;
            if (tick == cachedAabbTick)
            {
                min = cachedAabbMin;
                max = cachedAabbMax;
                return;
            }
            if (Parts.Count == 0)
            {
                DVector3 h = BoundsSize * 0.5;
                min = -h;
                max = h;
            }
            else
            {
                // 旋转矩阵分量（Unity Quaternion → 3x3，行优先）
                Quaternion q = Rotation;
                double x = q.x, y = q.y, z = q.z, w = q.w;
                double r00 = 1 - 2 * (y * y + z * z), r01 = 2 * (x * y - z * w), r02 = 2 * (x * z + y * w);
                double r10 = 2 * (x * y + z * w), r11 = 1 - 2 * (x * x + z * z), r12 = 2 * (y * z - x * w);
                double r20 = 2 * (x * z - y * w), r21 = 2 * (y * z + x * w), r22 = 1 - 2 * (x * x + y * y);

                min = new DVector3(double.MaxValue, double.MaxValue, double.MaxValue);
                max = new DVector3(double.MinValue, double.MinValue, double.MinValue);
                foreach (var part in Parts)
                {
                    DVector3 c = DVector3.Rotate(part.positionShip, Rotation);   // 旋转后中心
                    DVector3 h = part.boundsSizeShip * 0.5;
                    // OBB→AABB 投影：旋转后轴对齐半尺寸
                    DVector3 rh = new DVector3(
                        System.Math.Abs(r00) * h.x + System.Math.Abs(r01) * h.y + System.Math.Abs(r02) * h.z,
                        System.Math.Abs(r10) * h.x + System.Math.Abs(r11) * h.y + System.Math.Abs(r12) * h.z,
                        System.Math.Abs(r20) * h.x + System.Math.Abs(r21) * h.y + System.Math.Abs(r22) * h.z);
                    DVector3 pMin = c - rh;
                    DVector3 pMax = c + rh;
                    min = DVector3.Min(min, pMin);
                    max = DVector3.Max(max, pMax);
                }
            }
            cachedAabbTick = tick;
            cachedAabbMin = min;
            cachedAabbMax = max;
        }

        private int cachedAabbTick = -1;        // GetAabbSystem 每 tick 懒缓存（旋转/部件未变时同 tick 复用）
        private DVector3 cachedAabbMin, cachedAabbMax;

        private int cachedRotInvTick = -1;      // RotationInverse 每 tick 懒缓存（同款模式）
        private DQuaternion cachedRotInv;

        /// <summary>
        /// 姿态逆四元数（per-tick 懒缓存，与 GetAabbSystem 同款；SetRotation 失效）。
        /// 命中几何对「每射弹/每光束 × 每候选实体」各算一次 Inverse(FromQuaternion(Rotation))，
        /// 而姿态在步内不变（物理区只算，主线程统一施加）——缓存把每弹每实体的开销压成每实体每 tick。
        /// </summary>
        public DQuaternion RotationInverse()
        {
            int tick = Find.TickManager?.TicksGame ?? -1;
            if (tick == cachedRotInvTick)
            {
                if (PerfProbe.Enabled)
                {
                    PerfProbe.RotInvHit(); // 命中 = 省下的一次旋转逆运算
                    if (PerfProbe.ShadowChecks && PerfProbe.ShouldShadowRotInv())
                    {
                        // 影子校验：缓存值 vs 实时重算（姿态若在同 tick 内被改而未失效，此处必然抓到）
                        var fresh = DQuaternion.Inverse(DQuaternion.FromQuaternion(rotation));
                        PerfProbe.RotInvShadow(fresh.Equals(cachedRotInv),
                            $"entity={entityId} tick={tick} cached=({cachedRotInv.x:F6},{cachedRotInv.y:F6},{cachedRotInv.z:F6},{cachedRotInv.w:F6}) fresh=({fresh.x:F6},{fresh.y:F6},{fresh.z:F6},{fresh.w:F6})");
                    }
                }
                return cachedRotInv;
            }
            cachedRotInv = DQuaternion.Inverse(DQuaternion.FromQuaternion(rotation));
            cachedRotInvTick = tick;
            if (PerfProbe.Enabled) PerfProbe.RotInvMiss();
            return cachedRotInv;
        }

        private DVector3 positionMeters;
        private DVector3 velocityMeters;
        private Quaternion rotation;

        /// <summary>
        /// 当前剩余工质全部燃烧可获得的总 ΔV（显示值，Tsiolkovsky 理想公式）。
        /// 由「推进器等效排气速度 ve + 干质量 + 当前工质总量」推导，不落盘、不被消耗——
        /// 真实推进是推进器每 tick 消耗工质，质量变化后此值自然下降。
        /// 无推进器 / 无工质 / 无任何推进源返回 0。
        /// </summary>
        public float RemainingDeltaV
        {
            get
            {
                var src = GetPropulsionSource();
                if (src == null || src.EffectiveVe <= 0f) return 0f;
                double dry = TotalMass;                     // 干质量：部件结构质量
                double wet = dry + src.TotalPropellantMass; // 当前质量 = 干 + 剩余工质
                if (wet <= dry) return 0f;
                return (float)(src.EffectiveVe * System.Math.Log(wet / dry));
            }
        }

        /// <summary>满箱工质全部燃烧可获得的总 ΔV（显示值；储罐全满时的上限）。</summary>
        public float MaxDeltaV
        {
            get
            {
                var src = GetPropulsionSource();
                if (src == null || src.EffectiveVe <= 0f) return 0f;
                double dry = TotalMass;
                double wet = dry + src.TotalPropellantCapacity;
                if (wet <= dry) return 0f;
                return (float)(src.EffectiveVe * System.Math.Log(wet / dry));
            }
        }

        /// <summary>
        /// 实体推进源：优先 Map 建筑（SpaceMapComp）；无 Map 时回退自带推进数据（PropulsionData）。
        /// 与 group 级 burn 的解析规则（GetGroupPropulsionSource）保持一致。
        /// </summary>
        private IPropulsionSource GetPropulsionSource()
        {
            var mc = GetMapComp();
            if (mc != null) return mc;
            return HasPropulsionData ? this : null;
        }

        /// <summary>实体 → 所属地图的 SpaceMapComp（无 Map 时返回 null）。</summary>
        private SpaceMapComp GetMapComp()
        {
            if (MapId < 0) return null;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var mc = sg?.GetMapComponent<SpaceMapComp>(MapId);
            if (mc != null)
                return mc;

            // mapComponents 不随存档恢复；兜底直接从地图取 SpaceMapComp。
            Map map = SpaceMapGenerator.FindPocketMap(MapId);
            return map?.GetComponent<SpaceMapComp>();
        }

        public int MapId = -1;

        // ── 部件系统（单 part 化：载具至多一个 part，导弹等无 Map part 也走此列表）──
        private List<SpacePart> parts = new List<SpacePart>();
        public IReadOnlyList<SpacePart> Parts => parts;

        // ── 实体级 stats 缓存（kg 等，Parts / 组件变更时置脏）──
        private ShipStats shipStats;
        private bool statsDirty = true;

        /// <summary>实体聚合属性：Σ 未摧毁部件的 ShipStats，双级缓存（部件级 cachedStats + 实体级聚合）。</summary>
        public ShipStats AggregatedStats
        {
            get
            {
                if (statsDirty)
                    RecalculateShipStats();
                return shipStats;
            }
        }

        /// <summary>实体总质量（kg）：Σ 未摧毁部件质量，缓存防重复计算。</summary>
        public float TotalMass => AggregatedStats.mass;

        private void RecalculateShipStats()
        {
            shipStats = default(ShipStats);
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.isDestroyed) continue;
                var ps = p.Stats;
                shipStats.mass += ps.mass;
                shipStats.thrust += ps.thrust;
                shipStats.powerGen += ps.powerGen;
                shipStats.powerDraw += ps.powerDraw;
                shipStats.heatProduction += ps.heatProduction;
                shipStats.heatDissipation += ps.heatDissipation;
                shipStats.propellant += ps.propellant;
                shipStats.powerStorage += ps.powerStorage;
                shipStats.shieldCapacity += ps.shieldCapacity;
                shipStats.sensorRange += ps.sensorRange; // 聚合语义待定（max vs sum），先求和
                shipStats.cargoCapacity += ps.cargoCapacity;
                // [N2] 抗压制 / 抗欺骗：与其余字段同为求和。**新增 ShipStats 字段必须在此加一行**，
                // 否则组件贡献进不了实体级聚合——本方法是唯一的"必须同步点"。
                // [V3]（2026-10-02 裁定 ②）记忆时长同走此路：部件组件 → 本行 → 群级 `SensorTech.memorySeconds`。
                shipStats.memorySeconds += ps.memorySeconds;
                shipStats.jamStrength += ps.jamStrength;   // [E3] 干扰效力（同上：新增槽必须在此加一行）
                shipStats.ecmResist += ps.ecmResist;
                shipStats.eccmResist += ps.eccmResist;
            }
            statsDirty = false;
        }

        /// <summary>实体级 stats 失效。所有"部件集 / 部件组件可能变化"的路径必须走这里。</summary>
        public void InvalidateStats()
        {
            statsDirty = true;
            // 部件集变化 ⇒ 本船所属群组的派生信号（[V8]，吃 AggregatedStats 的热/电/尺寸项）也失效。
            // 本方法是"部件集可能变化"的**唯一既有收口**（AddComponent / RemoveComponent / 部件销毁都汇聚到这里），
            // 所以同步点放这里最省。⚠ 若将来新增改部件的路径绕开本方法，群组信号会静默过期
            // —— 与 AVM:824 的"派生点"是同一类陷阱，新加部件变更入口时请对照此处。
            Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(GroupId)?.InvalidateSignal();
        }

        // ── 无 Map 实体的自带推进数据（IPropulsionSource）──
        // 导弹/载荷等纯物理实体没有内部地图，引擎参数与工质直接挂在实体上。
        // 有 Map 的实体忽略此数据：burn/机动估算统一走 SpaceMapComp（见 GetPropulsionSource）。
        public List<PropulsionData> propulsion = new List<PropulsionData>();
        public bool HasPropulsionData => propulsion.Count > 0;

        // ── 无 Map 主推方向型（D15，可选）──
        // 有 Map 实体忽略此字段（主推轴 = mapComp.MainThrustAxis）。
        // null = 全向（穿梭机/EVA/无人机等默认，任意方向推力、无姿态对准）；
        // 有值 = 主推方向型（导弹/火箭：只沿该 body 局部轴锥角内出力，需魔动量轮对准）。
        public DVector3? mainAxisShip;
        public bool HasMainAxis => mainAxisShip.HasValue && mainAxisShip.Value.sqrMagnitude > 1e-12;

        // 运行时缓存（并行物理线程只读；propulsion 变更时重建）
        private List<PropellantNetwork> cachedPropulsionNetworks = new List<PropellantNetwork>();
        private bool propulsionDirty = true;
        private double totalPropulsionMass;      // ⚠ double：见 PropulsionData.mass 的说明（累加量不许用 float）
        private double totalPropulsionCapacity;

        /// <summary>推进数据版本：InvalidatePropulsion 自增（不序列化）。
        /// 群级推进源（GroupPropulsionAggregator）缓存以它判陈旧——与 Networks 缓存同一失效入口。</summary>
        public int PropulsionVersion { get; private set; }

        /// <summary>全部自带工质网络（由 propulsion 数据懒构建；ve/ṁ 变更需 InvalidatePropulsion）。</summary>
        public IReadOnlyList<PropellantNetwork> Networks
        {
            get
            {
                if (propulsionDirty)
                {
                    var list = new List<PropellantNetwork>(propulsion.Count);
                    for (int i = 0; i < propulsion.Count; i++)
                    {
                        var d = propulsion[i];
                        if (d == null || d.massFlow <= 0f) continue;
                        var net = new PropellantNetwork { propellant = d.propellant };
                        if (d.isVectoredRcs)
                            net.RegisterEngineRcs(d.ve, d.massFlow); // RCS 副网：入总聚合 + RCS 聚合（D15/D13 同构）
                        else
                            net.RegisterEngine(d.ve, d.massFlow);     // 主引擎：总聚合（无 Map 由实体主推轴约束）
                        list.Add(net);
                    }
                    cachedPropulsionNetworks = list;
                    propulsionDirty = false;
                }
                return cachedPropulsionNetworks;
            }
        }

        /// <summary>某工质自带存量（kg）。**double**：见 <see cref="PropulsionData.mass"/>。</summary>
        public double GetPropellantMass(PropellantDef def)
        {
            if (def == null) return 0.0;
            for (int i = 0; i < propulsion.Count; i++)
            {
                var d = propulsion[i];
                if (d.propellant == def) return d.mass;
            }
            return 0.0;
        }

        /// <summary>消耗自带工质（kg），返回实际扣除量（受存量截断）。**double**：见 <see cref="PropulsionData.mass"/>。</summary>
        public double TryConsumePropellant(PropellantDef def, double kg)
        {
            if (def == null || kg <= 0.0) return 0.0;
            // ⚠ 原来这里是"**扣第一个匹配的条目就 return**" ✗ —— 同一工质有多个条目（多罐同种料）时
            //   只烧得到第一罐，余量被**静默丢弃**（调用方会把剩余传给下一个**实体**，永不回头）⇒
            //   燃耗记不满、群质量掉不到位 ⇒ 燃烧按偏大的质量算 `a` ⇒ **多给 Δv**（2026-09-19 实测：
            //   `Σ(a·dt)=808` 而工质只够 749.6 ⇒ 白送 58 m/s）。
            // ⇒ 跨**全部**同工质条目扣足本次请求的量。
            double consumed = 0.0;
            for (int i = 0; i < propulsion.Count && consumed < kg; i++)
            {
                var d = propulsion[i];
                if (d.propellant != def) continue;
                double take = System.Math.Min(kg - consumed, d.mass);
                if (take <= 0.0) continue;
                d.mass -= take;
                consumed += take;
            }
            if (consumed > 0.0)
                totalPropulsionMass = System.Math.Max(0.0, totalPropulsionMass - consumed);
            return consumed;
        }

        /// <summary>自带工质总质量（kg）。**double**：它是"每步扣一点"的累加量，float 会在 72 t 处只剩 7.8 g 分辨率。</summary>
        public double TotalPropellantMass => totalPropulsionMass;

        /// <summary>自带燃料总容量（kg，MaxDeltaV 用）。**double**（同 <see cref="TotalPropellantMass"/>）。</summary>
        public double TotalPropellantCapacity => totalPropulsionCapacity;

        /// <summary>等效排气速度 ve = Σ(ve·ṁ)/Σṁ（m/s，全网络并行烧的加权平均）。</summary>
        public float EffectiveVe
        {
            get
            {
                float f = 0f, mf = 0f;
                var nets = Networks;
                for (int i = 0; i < nets.Count; i++)
                {
                    f += nets[i].Thrust;
                    mf += nets[i].MassFlow;
                }
                return mf > 0f ? f / mf : 0f;
            }
        }

        /// <summary>添加/合并一条自带推进配置（同工质合并 ve/ṁ，累加存量与容量）。</summary>
        public void AddPropulsion(PropulsionData d)
        {
            if (d == null || d.propellant == null) return;
            for (int i = 0; i < propulsion.Count; i++)
            {
                if (propulsion[i].propellant == d.propellant && propulsion[i].isVectoredRcs == d.isVectoredRcs)
                {
                    var existing = propulsion[i];
                    existing.ve = d.ve;
                    existing.massFlow = d.massFlow;
                    existing.mass += d.mass;
                    existing.capacity += d.capacity;
                    InvalidatePropulsion();
                    return;
                }
            }
            propulsion.Add(d);
            InvalidatePropulsion();
        }

        /// <summary>自带推进数据变更入口：重建网络缓存与总量。</summary>
        public void InvalidatePropulsion()
        {
            propulsionDirty = true;
            PropulsionVersion++;
            totalPropulsionMass = 0f;
            totalPropulsionCapacity = 0f;
            if (propulsion == null) return; // 旧存档无此节点
            for (int i = 0; i < propulsion.Count; i++)
            {
                totalPropulsionMass += propulsion[i].mass;
                totalPropulsionCapacity += propulsion[i].capacity;
            }
        }

        // 物理状态
        public DVector3 Position
        {
            get => positionMeters;
            set => positionMeters = value;
        }

        public DVector3 Velocity
        {
            get => velocityMeters;
            set => velocityMeters = value;
        }

        public Quaternion Rotation
        {
            get => rotation;
            set => rotation = value;
        }

        // 辅助：Unity 世界坐标（用于渲染）
        public Vector3 PositionUnity => (Vector3)(positionMeters * SpaceConstants.UnitsPerMeter);
        public Vector3 VelocityUnity => (Vector3)(velocityMeters * SpaceConstants.UnitsPerMeter);

        public SpaceEntity() { }
        public SpaceEntity(int entityId, int factionId = 0)
        {
            this.entityId = entityId;
            FactionId = factionId;
        }

        public void SetPositionVelocity(DVector3 posMeters, DVector3 velMeters)
        {
            positionMeters = posMeters;
            velocityMeters = velMeters;
        }

        public void SetRotation(Quaternion rot)
        {
            rotation = rot;
            cachedAabbTick = -1; // 旋转参与 GetAabbSystem → 失效缓存
            cachedRotInvTick = -1; // 同上：姿态逆四元数随之失效
        }

        // ── 部件管理 ──

        /// <summary>
        /// 从飞船设计数据初始化部件列表。会从 Parts 重新计算 BoundsSize。
        /// </summary>
        public void SetParts(List<SpacePart> newParts, bool recalculateBounds = true)
        {
            parts = newParts ?? new List<SpacePart>();
            cachedAabbTick = -1; // 部件参与 GetAabbSystem → 失效缓存
            InvalidateStats();
            if (recalculateBounds && parts.Count > 0)
                RecalculateBoundsFromParts();
        }

        /// <summary>
        /// 根据所有 Part 的 AABB 重新计算 Entity 的 BoundsSize。
        /// 取全部 Part 在三个轴向上的最大跨度。
        /// </summary>
        public void RecalculateBoundsFromParts()
        {
            if (parts.Count == 0) return;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (var part in parts)
            {
                if (part.isDestroyed) continue;
                DVector3 half = part.boundsSizeShip * 0.5;
                double px = part.positionShip.x, py = part.positionShip.y, pz = part.positionShip.z;
                if (px - half.x < minX) minX = px - half.x;
                if (px + half.x > maxX) maxX = px + half.x;
                if (py - half.y < minY) minY = py - half.y;
                if (py + half.y > maxY) maxY = py + half.y;
                if (pz - half.z < minZ) minZ = pz - half.z;
                if (pz + half.z > maxZ) maxZ = pz + half.z;
            }

            if (maxX > minX && maxY > minY && maxZ > minZ)
            {
                BoundsSize = new DVector3(maxX - minX, maxY - minY, maxZ - minZ);
            }
        }

        /// <summary>
        /// 标记部件被摧毁（惰性标记，不立即重建）。
        /// 舱室部件被毁时联动销毁其内部地图（先杀光内部 pawns）。
        /// </summary>
        public void MarkPartDestroyed(int partId)
        {
            foreach (var part in parts)
            {
                if (part.partId == partId)
                {
                    part.isDestroyed = true;
                    InvalidateStats();
                    RecalculateBoundsFromParts();
                    // 舱室联动：销毁内部地图（含 pawns 清除）
                    if (part.linkedMapId >= 0)
                        SpaceMapGenerator.DestroyRoomMap(part.linkedMapId);
                    return;
                }
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref entityId, "entityId");
            Scribe_Values.Look(ref groupId, "groupId");
            Scribe_Values.Look(ref factionId, "factionId");
            Scribe_Values.Look(ref CanInteract, "canInteract", false);
            Scribe_Values.Look(ref IsSelectable, "isSelectable", true);
            Scribe_Values.Look(ref HasMap, "hasMap", false);
            Scribe_Values.Look(ref IsMovable, "isMovable", true);
            Scribe_Values.Look(ref IsAffectedByGravity, "isAffectedByGravity", true);
            DVector3.Scribe("positionMeters", ref positionMeters);
            DVector3.Scribe("velocityMeters", ref velocityMeters);
            Scribe_Values.Look(ref rotation, "rotation");
            Scribe_Values.Look(ref MapId, "mapId", -1);
            Scribe_Values.Look(ref IsMapArchived, "isMapArchived", false);

            // 无 Map 实体自带推进数据序列化
            Scribe_Collections.Look(ref propulsion, "propulsion", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                InvalidatePropulsion();

            // D15 主推轴（null = 全向；旧存档无此节点 → 保持全向，向后兼容）
            DVector3.ScribeNullable("mainAxisShip", ref mainAxisShip);

            // 部件列表序列化
            Scribe_Collections.Look(ref parts, "parts", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (parts == null) parts = new List<SpacePart>();
                if (propulsion == null) propulsion = new List<PropulsionData>();
                InvalidateStats();
                foreach (var part in parts)
                {
                    part.ResolveReferences();
                    part.PostLoad();
                }
                if (parts.Count > 0)
                    RecalculateBoundsFromParts();
            }
        }
    }

    /// <summary>
    /// 无 Map 实体的单工质推进配置（工质种类 + 引擎参数 + 燃料存量/容量）。
    /// burn 通过 SpaceEntity（IPropulsionSource）读取；有 Map 实体忽略此数据（走 SpaceMapComp）。
    /// </summary>
    public class PropulsionData : IExposable
    {
        /// <summary>工质种类。</summary>
        public PropellantDef propellant;

        /// <summary>排气速度 ve（m/s）。</summary>
        public float ve;

        /// <summary>质量流率 ṁ（kg/s），推力 F = ve·ṁ。</summary>
        public float massFlow;

        /// <summary>燃料存量（kg）。
        /// ⚠ **必须 double**（2026-09-27 用户裁定）：原 float 在 72 t 处 1 ULP = **0.0078 kg**，
        /// 而消耗是 `mass -= take` 的**累加** ⇒ 一步的舍入差就是 **2 ULP = 0.0156 kg**，
        /// 再被 `accel = F/m` 放大成 **3.4e-6 m/s** 的 Δv 差、末端 **7e-5 m** 的位置差
        /// （由逐步指纹的输入列实测钉死：`burnDbgMass Δ=0.015625 kg`）。
        /// 这是**精度缺陷**、不是竞态 —— 工质质量是物理量，不该只有 7.8 g 分辨率。</summary>
        public double mass;

        /// <summary>燃料容量（kg，MaxDeltaV / 补给上限用）。见 <see cref="mass"/>（同批改 double）。</summary>
        public double capacity;

        /// <summary>
        /// 是否弱 RCS 副网（D15/D13 同构，仅无 Map 实体使用）：
        /// true = 全矢量小推力修正（主推未对准时仍可出力、主网尽时兜底），不参与主推轴向约束；
        /// false = 主引擎（无 Map 主推方向型沿 entity.mainAxisShip 锥内出力；全向型无约束）。
        /// 约束：同一实体内主网与 RCS 网须用不同 PropellantDef（同工质无法区分消耗）。
        /// </summary>
        public bool isVectoredRcs;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref propellant, "propellant");
            Scribe_Values.Look(ref ve, "ve", 0f);
            Scribe_Values.Look(ref massFlow, "massFlow", 0f);
            // ⚠ 这两个的默认值写 `0.0`（它们已是 double）：留 `0f` 能隐式转换、编译过，
            //    但读起来像"字段还是 float" ⇒ 下一个人会误判（同批改 double，见 `mass` 的说明）。
            //    **旧档读不出来是有意的**（开发期不考虑旧档兼容：不为旧表示写迁移）。
            Scribe_Values.Look(ref mass, "mass", 0.0);
            Scribe_Values.Look(ref capacity, "capacity", 0.0);
            Scribe_Values.Look(ref isVectoredRcs, "isVectoredRcs", false);
        }
    }
}
