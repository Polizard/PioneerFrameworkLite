using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 工质网络：一种工质 + 使用它的所有推进器引擎的聚合。
    /// 记录「以排气速度 ve 喷射质量流 ṁ」：推力 F = ve·ṁ。
    /// 一条船可同时携带多个工质网络（核热网络/化学网络/离子网络…），
    /// 机动时各网络并行出力、各自消耗自己的工质。
    /// </summary>
    public class PropellantNetwork
    {
        public PropellantDef propellant;

        private float sumVeMf;   // Σ(ve·ṁ)
        private float sumMf;     // Σṁ

        /// <summary>加权排气速度 ve = Σ(ve·ṁ)/Σṁ（m/s）。</summary>
        public float Ve => sumMf > 0f ? sumVeMf / sumMf : 0f;

        /// <summary>总质量流率 ṁ（kg/s）。</summary>
        public float MassFlow => sumMf;

        /// <summary>总推力 F = ve·ṁ（N）。</summary>
        public float Thrust => Ve * sumMf;

        /// <summary>该网络工质的当前可用存量（kg，动态查船级工质池）。</summary>
        public float AvailableMass(SpaceMapComp mapComp)
        {
            return mapComp != null ? mapComp.GetPropellantMass(propellant) : 0f;
        }

        public void RegisterEngine(float ve, float mf)
        {
            sumVeMf += ve * mf;
            sumMf += mf;
        }

        public void UnregisterEngine(float ve, float mf)
        {
            sumVeMf = Mathf.Max(0f, sumVeMf - ve * mf);
            sumMf = Mathf.Max(0f, sumMf - mf);
        }

        public bool Empty => sumMf <= 0f;
    }

    public class SpaceMapComp : MapComponent
    {
        // 可扩展的持久化数据，例如空间站名称、内部建筑列表等
        private string stationName = "Unnamed Station";

        /// <summary>Map → Entity：此地图所属的 SpaceEntity ID。</summary>
        public int ownerEntityId = -1;

        /// <summary>Map → Part：此地图对应的 SpacePart.partId（在 ownerEntity 的 Parts 列表中）。</summary>
        public int linkedPartId = -1;

        // ── 工质聚合向量（dV 体系）──
        // 键 = 工质 Def，值 = 该工质在当前地图（=船）内的总质量（kg）。
        // 多储罐同种工质自然累加；一条船可同时携带多种工质（液氢/化学/氙……）。
        // 该向量是运行时聚合缓存，不落盘：由各储罐建筑在 Spawn 时 +、
        // 在加注/消耗/摧毁时 ±，读档/取档后由建筑重建，避免重复记账。
        private Dictionary<PropellantDef, float> propellantMass = new Dictionary<PropellantDef, float>();

        /// <summary>工质总质量缓存（float，供并行物理线程只读，避免遍历字典）。</summary>
        private float totalPropellantMass;

        /// <summary>储罐总容量缓存（kg）：Σ 全部储罐容量，由储罐 Spawn/DeSpawn 同步。</summary>
        private float totalPropellantCapacity;

        // ── 工质网络（储罐 + 引擎）──
        // 键 = 工质，值 = 使用该工质的引擎聚合（ve/ṁ/推力）。
        // 引擎 Spawn/DeSpawn 时注册/注销；并行物理线程只读网络引用与 float。
        private readonly Dictionary<PropellantDef, PropellantNetwork> networks = new Dictionary<PropellantDef, PropellantNetwork>();
        private List<PropellantNetwork> networkList = new List<PropellantNetwork>();

        // ── 推进器建筑列表（burn 状态传播 / 喷焰表现）──
        private readonly List<CompThruster> thrusters = new List<CompThruster>();

        public string StationName
        {
            get => stationName;
            set => stationName = value;
        }

        public SpaceMapComp(Map map) : base(map) { }

        /// <summary>某工质的当前总质量（kg）。未记录返回 0。</summary>
        public float GetPropellantMass(PropellantDef def)
        {
            if (def == null) return 0f;
            return propellantMass.TryGetValue(def, out var m) ? m : 0f;
        }

        /// <summary>
        /// 增减某工质的总质量（kg）。delta 可为负（消耗/摧毁）；结果不落负。
        /// 向量 = Σ 全部储罐存量，由储罐 Comp 在变化点调用。
        /// </summary>
        public void AddPropellantMass(PropellantDef def, float delta)
        {
            if (def == null || delta == 0f) return;
            float next = GetPropellantMass(def) + delta;
            if (next <= 0.0001f)
                propellantMass.Remove(def);
            else
                propellantMass[def] = next;
            // 增量维护总量（delta 即本次净变化，键新增/移除均适用）
            totalPropellantMass = Mathf.Max(0f, totalPropellantMass + delta);
        }

        /// <summary>
        /// 消耗工质（推进/轨道保持用）。从船级工质池扣减，返回实际扣除量
        /// （受当前存量截断，用于判定"工质是否耗尽"）。
        /// </summary>
        public float TryConsumePropellant(PropellantDef def, float kg)
        {
            if (def == null || kg <= 0f) return 0f;
            float have = GetPropellantMass(def);
            float actual = Mathf.Min(kg, have);
            if (actual <= 0f) return 0f;
            AddPropellantMass(def, -actual);
            return actual;
        }

        /// <summary>全部工质 → 质量 的快照（调试 / dV 计算消费方）。</summary>
        public IEnumerable<KeyValuePair<PropellantDef, float>> PropellantSnapshot
        {
            get
            {
                foreach (var kv in propellantMass)
                    yield return kv;
            }
        }

        /// <summary>工质总质量（kg）。</summary>
        public float TotalPropellantMass => totalPropellantMass;

        /// <summary>储罐总容量（kg，满箱工质总量，MaxDeltaV 用）。</summary>
        public float TotalPropellantCapacity => totalPropellantCapacity;

        /// <summary>储罐总容量增减（由 CompPropellantTank Spawn/DeSpawn 同步）。</summary>
        public void AddPropellantCapacity(float delta)
        {
            totalPropellantCapacity = Mathf.Max(0f, totalPropellantCapacity + delta);
        }

        // ── 工质网络（引擎注册/聚合）──

        /// <summary>全部工质网络（并行物理线程只读快照）。</summary>
        public IReadOnlyList<PropellantNetwork> Networks => networkList;

        /// <summary>总推力（N）= Σ(ve·ṁ)。</summary>
        public float TotalThrust
        {
            get
            {
                float f = 0f;
                for (int i = 0; i < networkList.Count; i++)
                    f += networkList[i].Thrust;
                return f;
            }
        }

        /// <summary>总质量流率（kg/s）= Σṁ。</summary>
        public float TotalMassFlow
        {
            get
            {
                float mf = 0f;
                for (int i = 0; i < networkList.Count; i++)
                    mf += networkList[i].MassFlow;
                return mf;
            }
        }

        /// <summary>等效排气速度 ve = 总推力/总流率（全网络并行烧时的加权平均）。</summary>
        public float EffectiveVe
        {
            get
            {
                float f = TotalThrust;
                float mf = TotalMassFlow;
                return mf > 0f ? f / mf : 0f;
            }
        }

        public void RegisterEngine(PropellantDef def, float ve, float massFlow)
        {
            if (def == null || massFlow <= 0f) return;
            if (!networks.TryGetValue(def, out var net))
            {
                net = new PropellantNetwork { propellant = def };
                networks[def] = net;
            }
            net.RegisterEngine(ve, massFlow);
            RebuildNetworkList();
        }

        public void UnregisterEngine(PropellantDef def, float ve, float massFlow)
        {
            if (def == null || massFlow <= 0f) return;
            if (networks.TryGetValue(def, out var net))
            {
                net.UnregisterEngine(ve, massFlow);
                if (net.Empty)
                    networks.Remove(def);
                RebuildNetworkList();
            }
        }

        private void RebuildNetworkList()
        {
            networkList = new List<PropellantNetwork>(networks.Values);
        }

        // ── 推进器状态（burn 表现层）──

        public void RegisterThruster(CompThruster t)
        {
            if (t != null && !thrusters.Contains(t))
                thrusters.Add(t);
        }

        public void UnregisterThruster(CompThruster t)
        {
            thrusters.Remove(t);
        }

        /// <summary>设置地图内全部推进器的燃烧状态（空间层 burn 开始/结束时调用，表现层读）。</summary>
        public void SetThrustersBurning(bool burning)
        {
            for (int i = 0; i < thrusters.Count; i++)
            {
                if (thrusters[i] != null)
                    thrusters[i].IsBurning = burning;
            }
        }

        /// <summary>储罐差值同步（由 SpaceGameComp 每 tick 驱动；pocket map 隐藏时 Thing.CompTick 不可靠）。</summary>
        public void TickPropellantTankSync()
        {
            CompPropellantTank.TickDifferenceSync(map);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref stationName, "stationName", "Unnamed Station");
            Scribe_Values.Look(ref ownerEntityId, "ownerEntityId", -1);
            Scribe_Values.Look(ref linkedPartId, "linkedPartId", -1);
            // propellantMass / networks / 各聚合缓存均为运行时聚合缓存，不序列化
            // （由储罐与引擎建筑在 Spawn/DeSpawn 时重建）。
        }
    }
}
