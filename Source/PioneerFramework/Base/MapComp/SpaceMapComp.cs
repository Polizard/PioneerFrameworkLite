using System.Collections.Generic;
using UnityEngine;
using Verse;
using PioneerFramework.GroundAssembly;

namespace PioneerFramework
{
    /// <summary>
    /// 推进器轴向（船体局部系）。局部系约定：Map +X → 船 +X，Map +Z → 船 +Z，船 +Y = 世界 Y。
    /// XZ 四向随建筑 Rot4 映射；Up/Down 固定。
    /// </summary>
    public enum ThrustAxis
    {
        Forward,  // +Z（船头）
        Back,     // -Z（船尾，主推进默认）
        Left,     // -X
        Right,    // +X
        Up,       // +Y
        Down      // -Y
    }

    /// <summary>ThrustAxis 辅助：轴向 → 船体局部系单位向量；Rot4 朝向映射。</summary>
    public static class ThrustAxisHelper
    {
        private static readonly ThrustAxis[] XzAxes =
            { ThrustAxis.Forward, ThrustAxis.Back, ThrustAxis.Left, ThrustAxis.Right };

        public static DVector3 ShipVector(this ThrustAxis axis)
        {
            switch (axis)
            {
                case ThrustAxis.Forward: return DVector3.forward;  // +Z
                case ThrustAxis.Back:    return DVector3.back;     // -Z
                case ThrustAxis.Left:    return DVector3.left;     // -X
                case ThrustAxis.Right:   return DVector3.right;    // +X
                case ThrustAxis.Up:      return DVector3.up;       // +Y
                case ThrustAxis.Down:    return DVector3.down;     // -Y
                default:                 return DVector3.forward;
            }
        }

        /// <summary>
        /// XZ 四向随建筑 Rot4 旋转映射；Up/Down 固定。
        /// RimWorld 地图坐标：Rot4.North 朝 -Z（屏幕上）、East +X、South +Z、West -X；
        /// 船体局部系 = 地图 XZ（Map +X=船+X, Map +Z=船+Z）。
        /// 朝向往东 = 船头(-Z)转成船右(+X)：绕 +Y 旋转 -90°·AsInt。
        /// </summary>
        public static ThrustAxis RotateByRot4(this ThrustAxis axis, Rot4 rot)
        {
            if (axis == ThrustAxis.Up || axis == ThrustAxis.Down) return axis;
            DVector3 v = axis.ShipVector();
            double rad = -rot.AsInt * 90f * Mathf.Deg2Rad;
            DVector3 r = DVector3.Rotate(v, DVector3.up, rad);
            foreach (var a in XzAxes)
            {
                if (DVector3.Dot(r, a.ShipVector()) > 0.999)
                    return a;
            }
            return axis;
        }
    }

    /// <summary>
    /// 推进源抽象：一组工质网络 + 工质池的提供者。
    /// 两个实现：<see cref="SpaceMapComp"/>（Map 建筑引擎/储罐聚合）与 <see cref="SpaceEntity"/>
    /// （无 Map 实体自带推进数据，见 PropulsionData）。burn / 机动估算统一走此接口，
    /// 调用方不再区分「Map 建筑」还是「实体字段」。
    /// </summary>
    public interface IPropulsionSource
    {
        /// <summary>全部工质网络（引擎聚合：ve/ṁ/推力）。</summary>
        IReadOnlyList<PropellantNetwork> Networks { get; }

        /// <summary>某工质当前存量（kg）。未记录返回 0。
        /// ⚠ **质量一律 `double`**（2026-09-27 用户裁定）：工质质量的消耗是**逐步累加**（每步 `-= 一点`），
        /// 而 float 在 72 t 处只有 **7.8 g** 分辨率 ⇒ 一步的舍入差（2 ULP = 15.6 g）会被 `accel = F/m`
        /// 放大成 3.4e-6 m/s 的 Δv 差、末端 7e-5 m 的位置差（逐步指纹实测钉死）。
        /// 速率类（ve/ṁ/推力）保持 float：它们是**瞬时量**、不做累加，且当前取值都可精确表示。</summary>
        double GetPropellantMass(PropellantDef def);

        /// <summary>消耗工质（kg），返回实际扣除量（受存量截断，用于判定耗尽）。**double**：见上。</summary>
        double TryConsumePropellant(PropellantDef def, double kg);

        /// <summary>工质总质量（kg）。**double**：累加量，见上。</summary>
        double TotalPropellantMass { get; }

        /// <summary>燃料总容量（kg，MaxDeltaV 用）。**double**：同 <see cref="TotalPropellantMass"/>。</summary>
        double TotalPropellantCapacity { get; }

        /// <summary>等效排气速度 ve = Σ(ve·ṁ)/Σṁ（m/s，全网络并行烧时的加权平均）。</summary>
        float EffectiveVe { get; }
    }

    /// <summary>一次方向感知推进的方向切片：参与出力的网络 + 其锥内推力/流率（工质消耗按此分配）。</summary>
    public struct ThrustSlice
    {
        public PropellantNetwork network;
        public float thrust;
        public float massFlow;
    }

    /// <summary>
    /// 工质网络：一种工质 + 使用它的所有推进器引擎的聚合。
    /// 记录「以排气速度 ve 喷射质量流 ṁ」：推力 F = ve·ṁ。
    /// 一条船可同时携带多个工质网络（核热网络/化学网络/离子网络…），
    /// 机动时各网络并行出力、各自消耗自己的工质。
    /// per-axis 聚合（六方向）：burn/手动平移按引擎朝向过滤出力；无朝向的调用（无 Map 全矢量）只走总聚合。
    /// </summary>
    public class PropellantNetwork
    {
        public PropellantDef propellant;

        private float sumVeMf;   // Σ(ve·ṁ)
        private float sumMf;     // Σṁ
        private readonly float[] sumVeMfByAxis = new float[6];  // Σ(ve·ṁ) per ThrustAxis（主引擎）
        private readonly float[] sumMfByAxis = new float[6];    // Σṁ per ThrustAxis（主引擎）
        private float sumVeMfRcs; // Σ(ve·ṁ) RCS 引擎（全矢量）
        private float sumMfRcs;   // Σṁ RCS 引擎（全矢量）

        /// <summary>加权排气速度 ve = Σ(ve·ṁ)/Σṁ（m/s）。</summary>
        public float Ve => sumMf > 0f ? sumVeMf / sumMf : 0f;

        /// <summary>总质量流率 ṁ（kg/s）。</summary>
        public float MassFlow => sumMf;

        /// <summary>总推力 F = ve·ṁ（N）。</summary>
        public float Thrust => Ve * sumMf;

        /// <summary>某轴向上的推力（N，Σ(ve·ṁ)）。</summary>
        public float ThrustByAxis(int axisIndex) => sumVeMfByAxis[axisIndex];

        /// <summary>某轴向上的质量流率（kg/s）。</summary>
        public float FlowByAxis(int axisIndex) => sumMfByAxis[axisIndex];

        /// <summary>RCS 总推力（N，全矢量）。</summary>
        public float RcsThrust => sumVeMfRcs;

        /// <summary>RCS 总质量流率（kg/s）。</summary>
        public float RcsFlow => sumMfRcs;

        /// <summary>该网络是否含 RCS 引擎（no-map 实体每 PropulsionData 一条网络，可用此判定类属；D15）。</summary>
        public bool HasRcsEngines => sumMfRcs > 0f;

        /// <summary>该网络工质的当前可用存量（kg，动态查推进源工质池）。</summary>
        public float AvailableMass(IPropulsionSource src)
        {
            // 边界 cast：这是**阈值读**（判"还有没有料"），不参与累加 ⇒ float 足够
            return src != null ? (float)src.GetPropellantMass(propellant) : 0f;
        }

        /// <summary>无朝向注册（无 Map 全矢量源）：只入总聚合，不入 per-axis。</summary>
        public void RegisterEngine(float ve, float mf)
        {
            sumVeMf += ve * mf;
            sumMf += mf;
        }

        public void RegisterEngine(float ve, float mf, int axisIndex)
        {
            sumVeMf += ve * mf;
            sumMf += mf;
            sumVeMfByAxis[axisIndex] += ve * mf;
            sumMfByAxis[axisIndex] += mf;
        }

        /// <summary>RCS 引擎注册：只入总聚合与 RCS 聚合，不入 per-axis（主引擎六向判定不包含 RCS）。</summary>
        public void RegisterEngineRcs(float ve, float mf)
        {
            sumVeMf += ve * mf;
            sumMf += mf;
            sumVeMfRcs += ve * mf;
            sumMfRcs += mf;
        }

        public void UnregisterEngine(float ve, float mf)
        {
            sumVeMf = Mathf.Max(0f, sumVeMf - ve * mf);
            sumMf = Mathf.Max(0f, sumMf - mf);
        }

        public void UnregisterEngine(float ve, float mf, int axisIndex)
        {
            sumVeMf = Mathf.Max(0f, sumVeMf - ve * mf);
            sumMf = Mathf.Max(0f, sumMf - mf);
            sumVeMfByAxis[axisIndex] = Mathf.Max(0f, sumVeMfByAxis[axisIndex] - ve * mf);
            sumMfByAxis[axisIndex] = Mathf.Max(0f, sumMfByAxis[axisIndex] - mf);
        }

        public void UnregisterEngineRcs(float ve, float mf)
        {
            sumVeMf = Mathf.Max(0f, sumVeMf - ve * mf);
            sumMf = Mathf.Max(0f, sumMf - mf);
            sumVeMfRcs = Mathf.Max(0f, sumVeMfRcs - ve * mf);
            sumMfRcs = Mathf.Max(0f, sumMfRcs - mf);
        }

        public bool Empty => sumMf <= 0f;
    }

    public class SpaceMapComp : MapComponent, IPropulsionSource
    {
        // 可扩展的持久化数据，例如空间站名称、内部建筑列表等
        private string stationName = "Unnamed Station";

        /// <summary>Map → Entity：此地图所属的 SpaceEntity ID。</summary>
        public int ownerEntityId = -1;

        /// <summary>Map → Part：此地图对应的 SpacePart.partId（在 ownerEntity 的 Parts 列表中）。</summary>

        // ── PFL 火箭虚拟推进持久化（LA-18）──
        // RocketDesignSpec 与虚拟推进参数不依赖地图建筑，因此需要显式落盘；
        // 读档后在 FinalizeInit 中重新注册到本 MapComponent 的推进聚合。
        private RocketDesignSpec rocketSpec;
        private bool hasVirtualPropulsion;
        private string virtualPropellantDefName;
        private float virtualVe;
        private float virtualMassFlow;
        private float virtualFuelMass;
        private float virtualFuelCapacity;
        private bool virtualPropulsionRegistered;

        /// <summary>当前火箭设计规格（入轨后随地图保存）。</summary>
        public RocketDesignSpec RocketSpec => rocketSpec;

        // ── 工质聚合向量（dV 体系）──
        // 键 = 工质 Def，值 = 该工质在当前地图（=船）内的总质量（kg）。
        // 多储罐同种工质自然累加；一条船可同时携带多种工质（液氢/化学/氙……）。
        // 该向量是运行时聚合缓存，不落盘：由各储罐建筑在 Spawn 时 +、
        // 在加注/消耗/摧毁时 ±，读档/取档后由建筑重建，避免重复记账。
        private Dictionary<PropellantDef, double> propellantMass = new Dictionary<PropellantDef, double>();

        /// <summary>工质总质量缓存（**double**，供并行物理线程只读，避免遍历字典）。
        /// ⚠ double 的理由见 <see cref="IPropulsionSource.GetPropellantMass"/>：累加量在 72 t 处
        /// float 只有 7.8 g 分辨率 ⇒ 一步的舍入差会被 `a=F/m` 放大成可测的 Δv/位置差。</summary>
        private double totalPropellantMass;

        /// <summary>储罐总容量缓存（kg）：Σ 全部储罐容量，由储罐 Spawn/DeSpawn 同步。**double**（同 <see cref="totalPropellantMass"/>）。</summary>
        private double totalPropellantCapacity;

        // ── 工质网络（储罐 + 引擎）──
        // 键 = 工质，值 = 使用该工质的引擎聚合（ve/ṁ/推力）。
        // 引擎 Spawn/DeSpawn 时注册/注销；并行物理线程只读网络引用与 float。
        private readonly Dictionary<PropellantDef, PropellantNetwork> networks = new Dictionary<PropellantDef, PropellantNetwork>();
        private List<PropellantNetwork> networkList = new List<PropellantNetwork>();

        // ── 推进器建筑列表（burn 状态传播 / 喷焰表现）──
        private readonly List<CompThruster> thrusters = new List<CompThruster>();

        // ── 六方向推力聚合（per-axis 缓存，引擎注册/注销时维护；并行物理线程只读）──
        private readonly float[] totalThrustByAxis = new float[6];

        // ── RCS 全矢量聚合（Σ RCS 引擎，免对准）──
        private float rcsTotalThrust;

        // ── 主推进方向 ──
        /// <summary>主推进方向（ThrustAxis 索引）。-1 = 自动（取推力最大轴向）。序列化。</summary>
        public int mainThrustAxisIndex = -1;

        // ── 动作组（手动指令过滤）──
        /// <summary>已武装动作组掩码（bit0..8 = 动作组 1..9）。只过滤手动指令；自动 burn 不受影响。序列化。</summary>
        public int armedGroupsMask;

        public string StationName
        {
            get => stationName;
            set => stationName = value;
        }

        public SpaceMapComp(Map map) : base(map) { }

        /// <summary>某工质的当前总质量（kg）。未记录返回 0。**double**：见 <see cref="IPropulsionSource.GetPropellantMass"/>。</summary>
        public double GetPropellantMass(PropellantDef def)
        {
            if (def == null) return 0.0;
            return propellantMass.TryGetValue(def, out var m) ? m : 0.0;
        }

        /// <summary>
        /// 增减某工质的总质量（kg）。delta 可为负（消耗/摧毁）；结果不落负。
        /// 向量 = Σ 全部储罐存量，由储罐 Comp 在变化点调用。
        /// </summary>
        public void AddPropellantMass(PropellantDef def, double delta)
        {
            if (def == null || delta == 0.0) return;
            double next = GetPropellantMass(def) + delta;
            if (next <= 0.0001)
                propellantMass.Remove(def);
            else
                propellantMass[def] = next;
            // 增量维护总量（delta 即本次净变化，键新增/移除均适用）
            totalPropellantMass = System.Math.Max(0.0, totalPropellantMass + delta);
        }

        /// <summary>
        /// 消耗工质（推进/轨道保持用）。从船级工质池扣减，返回实际扣除量
        /// （受当前存量截断，用于判定"工质是否耗尽"）。**double**：见 <see cref="IPropulsionSource.TryConsumePropellant"/>。
        /// </summary>
        public double TryConsumePropellant(PropellantDef def, double kg)
        {
            if (def == null || kg <= 0.0) return 0.0;
            double have = GetPropellantMass(def);
            double actual = System.Math.Min(kg, have);
            if (actual <= 0.0) return 0.0;
            AddPropellantMass(def, -actual);
            return actual;
        }

        /// <summary>全部工质 → 质量 的快照（调试 / dV 计算消费方）。</summary>
        public IEnumerable<KeyValuePair<PropellantDef, double>> PropellantSnapshot
        {
            get
            {
                foreach (var kv in propellantMass)
                    yield return kv;
            }
        }

        /// <summary>工质总质量（kg）。**double**：见 <see cref="IPropulsionSource.TotalPropellantMass"/>。</summary>
        public double TotalPropellantMass => totalPropellantMass;

        /// <summary>储罐总容量（kg，满箱工质总量，MaxDeltaV 用）。**double**（同上）。</summary>
        public double TotalPropellantCapacity => totalPropellantCapacity;

        /// <summary>储罐总容量增减（由 CompPropellantTank Spawn/DeSpawn 同步）。</summary>
        public void AddPropellantCapacity(double delta)
        {
            totalPropellantCapacity = System.Math.Max(0.0, totalPropellantCapacity + delta);
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

        /// <summary>轴向总量：某方向（船体局部系）当前全部引擎总推力（N，跨网络）。</summary>
        public float GetAxisTotalThrust(int axisIndex)
        {
            return axisIndex >= 0 && axisIndex < 6 ? totalThrustByAxis[axisIndex] : 0f;
        }

        /// <summary>主推进方向：显式设置（操舵台 UI）或自动 = 推力最大轴向。无引擎时回退 Back。</summary>
        public ThrustAxis MainThrustAxis
        {
            get
            {
                if (mainThrustAxisIndex >= 0 && mainThrustAxisIndex < 6)
                    return (ThrustAxis)mainThrustAxisIndex;
                int best = (int)ThrustAxis.Back;
                float bestT = 0f;
                for (int i = 0; i < 6; i++)
                {
                    if (totalThrustByAxis[i] > bestT)
                    {
                        bestT = totalThrustByAxis[i];
                        best = i;
                    }
                }
                return (ThrustAxis)best;
            }
        }

        /// <summary>无朝向引擎注册（虚拟推进/全矢量引擎）：按 RCS 口径入全矢量聚合（免对准）。</summary>
        public void RegisterEngine(PropellantDef def, float ve, float massFlow)
        {
            RegisterEngine(def, ve, massFlow, ThrustAxis.Back, isRcs: true);
        }

        /// <summary>是否装有方向性（per-axis）引擎：false = 纯全矢量船（仅虚拟推进/RCS），burn 免对准。</summary>
        public bool HasDirectionalEngines
        {
            get
            {
                for (int i = 0; i < 6; i++)
                    if (totalThrustByAxis[i] > 0f) return true;
                return false;
            }
        }

        public void RegisterEngine(PropellantDef def, float ve, float massFlow, ThrustAxis axis, bool isRcs = false)
        {
            if (def == null || massFlow <= 0f) return;
            if (!networks.TryGetValue(def, out var net))
            {
                net = new PropellantNetwork { propellant = def };
                networks[def] = net;
            }
            if (isRcs)
            {
                net.RegisterEngineRcs(ve, massFlow);
                rcsTotalThrust += ve * massFlow;
            }
            else
            {
                int axisIndex = (int)axis;
                net.RegisterEngine(ve, massFlow, axisIndex);
                totalThrustByAxis[axisIndex] += ve * massFlow;
            }
            RebuildNetworkList();
        }

        public void UnregisterEngine(PropellantDef def, float ve, float massFlow, ThrustAxis axis, bool isRcs = false)
        {
            if (def == null || massFlow <= 0f) return;
            if (networks.TryGetValue(def, out var net))
            {
                if (isRcs)
                {
                    net.UnregisterEngineRcs(ve, massFlow);
                    rcsTotalThrust = Mathf.Max(0f, rcsTotalThrust - ve * massFlow);
                }
                else
                {
                    int axisIndex = (int)axis;
                    net.UnregisterEngine(ve, massFlow, axisIndex);
                    totalThrustByAxis[axisIndex] = Mathf.Max(0f, totalThrustByAxis[axisIndex] - ve * massFlow);
                }
                if (net.Empty)
                    networks.Remove(def);
                RebuildNetworkList();
            }
        }

        /// <summary>RCS 总推力（N，全矢量）。</summary>
        public float RcsTotalThrust => rcsTotalThrust;

        /// <summary>是否装有矢量 RCS（微调/手动平移免对准的前提）。</summary>
        public bool HasRcs => rcsTotalThrust > 0f;

        /// <summary>
        /// RCS 全矢量推力：任意方向 = Σ RCS 引擎（免对准/免锥）。
        /// 返回按工质网络的切片（工质消耗按切片流率分配）。
        /// </summary>
        public void GetRcsThrustAlong(HashSet<PropellantDef> enabled, List<ThrustSlice> slices,
            out float totalThrust, out float totalMassFlow)
        {
            totalThrust = 0f;
            totalMassFlow = 0f;
            if (slices != null) slices.Clear();
            for (int n = 0; n < networkList.Count; n++)
            {
                var net = networkList[n];
                if (net.RcsFlow <= 0f) continue;
                if (enabled != null && !enabled.Contains(net.propellant)) continue;
                if (net.AvailableMass(this) <= 0f) continue; // 工质已尽熄火
                totalThrust += net.RcsThrust;
                totalMassFlow += net.RcsFlow;
                if (slices != null)
                    slices.Add(new ThrustSlice { network = net, thrust = net.RcsThrust, massFlow = net.RcsFlow });
            }
        }

        /// <summary>
        /// 方向感知推力：目标世界方向 dir 落在哪个船体轴向的矢量锥内（含主推进方向辅助），
        /// 取锥内引擎的聚合推力/流率（跨网络，按网络切片，工质消耗按切片流率分配）。
        /// 调用方（burn/手动平移）传入 group 当前姿态 rotation 做局部→世界换算。
        /// </summary>
        public void GetThrustAlong(DVector3 dirSystem, Quaternion rotation, float coneCos,
            HashSet<PropellantDef> enabled, List<ThrustSlice> slices, out float totalThrust, out float totalMassFlow)
        {
            totalThrust = 0f;
            totalMassFlow = 0f;
            if (slices != null) slices.Clear();
            if (dirSystem.sqrMagnitude < 1e-12) return;

            DVector3 dirN = dirSystem.normalized;
            for (int n = 0; n < networkList.Count; n++)
            {
                var net = networkList[n];
                if (net.MassFlow <= 0f) continue;
                if (enabled != null && !enabled.Contains(net.propellant)) continue;
                if (net.AvailableMass(this) <= 0f) continue; // 工质已尽熄火

                float t = 0f, mf = 0f;
                for (int a = 0; a < 6; a++)
                {
                    if (net.FlowByAxis(a) <= 0f) continue;
                    DVector3 worldAxis = DVector3.Rotate(((ThrustAxis)a).ShipVector(), rotation);
                    if (DVector3.Dot(worldAxis, dirN) >= coneCos)
                    {
                        t += net.ThrustByAxis(a);
                        mf += net.FlowByAxis(a);
                    }
                }
                if (t > 0f)
                {
                    totalThrust += t;
                    totalMassFlow += mf;
                    if (slices != null)
                        slices.Add(new ThrustSlice { network = net, thrust = t, massFlow = mf });
                }
            }
        }

        /// <summary>
        /// 手动指令用的方向感知推力：**逐引擎**过滤——
        /// RCS 引擎**全矢量**（任意方向出力，免对准），主引擎只在矢量锥内出力；
        /// 都受「已武装（动作组）」过滤。返回按工质网络的切片（工质消耗按切片流率分配）。
        /// </summary>
        public void GetManualThrustAlong(DVector3 dirSystem, Quaternion rotation, float coneCos,
            HashSet<PropellantDef> enabled, int armedGroupsMask, List<ThrustSlice> slices,
            out float totalThrust, out float totalMassFlow)
        {
            totalThrust = 0f;
            totalMassFlow = 0f;
            if (slices != null) slices.Clear();
            if (dirSystem.sqrMagnitude < 1e-12) return;
            DVector3 dirN = dirSystem.normalized;

            var acc = new Dictionary<PropellantDef, (float t, float mf)>();
            for (int i = 0; i < thrusters.Count; i++)
            {
                var t = thrusters[i];
                if (t == null || t.parent == null || t.parent.Destroyed || t.PropellantDef == null) continue;
                if (!IsThrusterArmed(t, armedGroupsMask)) continue;
                if (enabled != null && !enabled.Contains(t.PropellantDef)) continue;
                // RCS 全矢量（免方向判定）；主引擎需落在目标方向矢量锥内
                if (!t.IsVectoredRcs)
                {
                    DVector3 wAxis = DVector3.Rotate(t.ShipAxis.ShipVector(), rotation);
                    if (DVector3.Dot(wAxis, dirN) < coneCos) continue;
                }
                float ve = t.Ve, mf = t.MassFlowRate;
                if (ve <= 0f || mf <= 0f) continue;
                if (acc.TryGetValue(t.PropellantDef, out var e))
                    acc[t.PropellantDef] = (e.t + ve * mf, e.mf + mf);
                else
                    acc[t.PropellantDef] = (ve * mf, mf);
            }

            foreach (var kv in acc)
            {
                var net = networks.TryGetValue(kv.Key, out var n) ? n : null;
                if (net == null) continue;
                totalThrust += kv.Value.t;
                totalMassFlow += kv.Value.mf;
                if (slices != null)
                    slices.Add(new ThrustSlice { network = net, thrust = kv.Value.t, massFlow = kv.Value.mf });
            }
        }

        /// <summary>引擎是否武装：未分组（mask=0）恒武装；分组引擎只在其任一组已武装时出力。</summary>
        public bool IsThrusterArmed(CompThruster t, int armedGroupsMask)
        {
            if (t == null) return false;
            int mask = t.ActionGroupMask;
            if (mask == 0) return true; // 未分组 → 常开
            return (armedGroupsMask & mask) != 0;
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

        /// <summary>
        /// 设置并注册 PFL 火箭的虚拟推进数据（LA-18）。
        /// 该数据没有对应地图建筑，必须由本组件负责持久化与重建。
        /// </summary>
        public void SetVirtualRocketPropulsion(RocketDesignSpec spec, PropellantDef def,
            float ve, float massFlow, float fuelMass, float capacity)
        {
            // 发射失败后可能复用同一张舱内地图重试；已有虚拟推进时不要重复注册，
            // 避免引擎/容量叠加。只更新设计引用，保留当前实际工质剩余量。
            if (hasVirtualPropulsion && virtualPropulsionRegistered)
            {
                rocketSpec = spec;
                return;
            }

            rocketSpec = spec;
            hasVirtualPropulsion = true;
            virtualPropellantDefName = def?.defName;
            virtualVe = ve;
            virtualMassFlow = massFlow;
            virtualFuelMass = fuelMass;
            virtualFuelCapacity = capacity;
            virtualPropulsionRegistered = false;
            RegisterVirtualPropulsion();
        }

        /// <summary>把保存的虚拟推进参数重新注册到本 MapComponent（新建/读档共用）。</summary>
        private void RegisterVirtualPropulsion()
        {
            if (!hasVirtualPropulsion || virtualPropulsionRegistered)
                return;

            PropellantDef def = DefDatabase<PropellantDef>.GetNamedSilentFail(virtualPropellantDefName);
            if (def == null || virtualMassFlow <= 0f)
                return;

            RegisterEngine(def, virtualVe, virtualMassFlow);
            AddPropellantCapacity(virtualFuelCapacity);
            AddPropellantMass(def, virtualFuelMass);
            virtualPropulsionRegistered = true;
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();

            // mapComponents 是 SpaceGameComp 的运行时缓存，不随存档恢复；
            // 读档后必须把自己重新登记，否则群组/实体无法找到本地图的推进源。
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame != null)
                spaceGame.RegisterMapComponent(map.uniqueID, this);

            if (hasVirtualPropulsion)
                RegisterVirtualPropulsion();
        }

        public override void ExposeData()
        {
            base.ExposeData();

            // 保存前把虚拟燃料同步为当前工质向量中的真实剩余量。
            if (Scribe.mode == LoadSaveMode.Saving && hasVirtualPropulsion
                && !string.IsNullOrEmpty(virtualPropellantDefName))
            {
                PropellantDef currentDef = DefDatabase<PropellantDef>.GetNamedSilentFail(virtualPropellantDefName);
                if (currentDef != null)
                    virtualFuelMass = (float)GetPropellantMass(currentDef);   // 边界 cast：镜像读数
            }

            Scribe_Values.Look(ref stationName, "stationName", "Unnamed Station");
            Scribe_Values.Look(ref ownerEntityId, "ownerEntityId", -1);
            Scribe_Deep.Look(ref rocketSpec, "rocketSpec");
            Scribe_Values.Look(ref hasVirtualPropulsion, "hasVirtualPropulsion", false);
            Scribe_Values.Look(ref virtualPropellantDefName, "virtualPropellantDefName");
            Scribe_Values.Look(ref virtualVe, "virtualVe", 0f);
            Scribe_Values.Look(ref virtualMassFlow, "virtualMassFlow", 0f);
            Scribe_Values.Look(ref virtualFuelMass, "virtualFuelMass", 0f);
            Scribe_Values.Look(ref virtualFuelCapacity, "virtualFuelCapacity", 0f);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
                virtualPropulsionRegistered = false;

            // 操舵与推进朝向（六方向/主推进/动作组）
            Scribe_Values.Look(ref mainThrustAxisIndex, "mainThrustAxisIndex", -1);
            Scribe_Values.Look(ref armedGroupsMask, "armedGroupsMask", 0);

            // propellantMass / networks / 各聚合缓存均为运行时聚合缓存，不序列化；
            // 普通地图建筑在 Spawn/DeSpawn 时重建，虚拟推进在 FinalizeInit 重建。
        }
    }
}
