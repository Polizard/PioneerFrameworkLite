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
        /// AABB 半边长（旋转保守放大 2 倍）。所有 AABB 消费方必须用此属性而非 BoundsSize*0.5：
        /// 忽略 Rotation 的轴对齐近似下，半边长取整个 BoundsSize 才能覆盖任意旋转的最坏投影。
        /// </summary>
        public DVector3 AabbHalfSize => BoundsSize;

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
            }
            statsDirty = false;
        }

        /// <summary>实体级 stats 失效。所有"部件集 / 部件组件可能变化"的路径必须走这里。</summary>
        public void InvalidateStats()
        {
            statsDirty = true;
        }

        // ── 无 Map 实体的自带推进数据（IPropulsionSource）──
        // 导弹/载荷等纯物理实体没有内部地图，引擎参数与工质直接挂在实体上。
        // 有 Map 的实体忽略此数据：burn/机动估算统一走 SpaceMapComp（见 GetPropulsionSource）。
        public List<PropulsionData> propulsion = new List<PropulsionData>();
        public bool HasPropulsionData => propulsion.Count > 0;

        // 运行时缓存（并行物理线程只读；propulsion 变更时重建）
        private List<PropellantNetwork> cachedPropulsionNetworks = new List<PropellantNetwork>();
        private bool propulsionDirty = true;
        private float totalPropulsionMass;
        private float totalPropulsionCapacity;

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
                        net.RegisterEngine(d.ve, d.massFlow);
                        list.Add(net);
                    }
                    cachedPropulsionNetworks = list;
                    propulsionDirty = false;
                }
                return cachedPropulsionNetworks;
            }
        }

        /// <summary>某工质自带存量（kg）。</summary>
        public float GetPropellantMass(PropellantDef def)
        {
            if (def == null) return 0f;
            for (int i = 0; i < propulsion.Count; i++)
            {
                var d = propulsion[i];
                if (d.propellant == def) return d.mass;
            }
            return 0f;
        }

        /// <summary>消耗自带工质（kg），返回实际扣除量（受存量截断）。</summary>
        public float TryConsumePropellant(PropellantDef def, float kg)
        {
            if (def == null || kg <= 0f) return 0f;
            for (int i = 0; i < propulsion.Count; i++)
            {
                var d = propulsion[i];
                if (d.propellant != def) continue;
                float actual = Mathf.Min(kg, d.mass);
                if (actual <= 0f) return 0f;
                d.mass -= actual;
                totalPropulsionMass = Mathf.Max(0f, totalPropulsionMass - actual);
                return actual;
            }
            return 0f;
        }

        /// <summary>自带工质总质量（kg）。</summary>
        public float TotalPropellantMass => totalPropulsionMass;

        /// <summary>自带燃料总容量（kg，MaxDeltaV 用）。</summary>
        public float TotalPropellantCapacity => totalPropulsionCapacity;

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
                if (propulsion[i].propellant == d.propellant)
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

        public void SetRotation(Quaternion rot) => rotation = rot;

        // ── 部件管理 ──

        /// <summary>
        /// 从飞船设计数据初始化部件列表。会从 Parts 重新计算 BoundsSize。
        /// </summary>
        public void SetParts(List<SpacePart> newParts, bool recalculateBounds = true)
        {
            parts = newParts ?? new List<SpacePart>();
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
                DVector3 half = part.localBoundsSize * 0.5;
                double px = part.localPosition.x, py = part.localPosition.y, pz = part.localPosition.z;
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

        /// <summary>燃料存量（kg）。</summary>
        public float mass;

        /// <summary>燃料容量（kg，MaxDeltaV / 补给上限用）。</summary>
        public float capacity;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref propellant, "propellant");
            Scribe_Values.Look(ref ve, "ve", 0f);
            Scribe_Values.Look(ref massFlow, "massFlow", 0f);
            Scribe_Values.Look(ref mass, "mass", 0f);
            Scribe_Values.Look(ref capacity, "capacity", 0f);
        }
    }
}
