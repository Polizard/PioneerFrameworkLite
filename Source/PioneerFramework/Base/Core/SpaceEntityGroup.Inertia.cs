using System.Collections.Generic;
using System.Globalization;
using PioneerFramework.Components;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **群级刚体惯量（批 0 底座，2026-10-17）**——事件驱动的惰性缓存，**绝不 tick**。
    ///
    /// ## 为什么是"惰性 + 指纹"
    /// 惯量只有在读者（操舵窗、未来的物理档/碰撞/控制律）来拉时才需要；部件的质量/位置/形状只在
    /// **部件事变**（摧毁/装卸/分级/建造完成）与**群实体集变化**时改变。指纹 =
    /// `entitySetVersion ⊕ Σ 各实体 partSetVersion`（与 `GroupPropulsionAggregator` 的
    /// `entitySetVersion ⊕ Σ PropulsionVersion` 同款模式）⇒ **静止时重算增量 = 0**
    /// （判据：`InertiaRecomputeCount` 不增长）。
    ///
    /// ## 口径
    /// · 部件质量 = `part.Mass`（组件聚合；含 1 kg 地板）；**损毁件仍计入**（B5 / D2 乙：残骸有质量；
    ///   其燃料已在损毁时漏光，不再计 tank.massKg）。
    /// · 部件质心（群体系）= `entity.Position + part.positionShip`（单 part 化：实体系 ≡ 群体系）。
    /// · 部件自惯量：`PartInertiaComponent`（形状/自定义）；无组件 ⇒ **默认长方体**（boundsSizeShip）。
    /// · 合成 = `InertiaTensor.Compose`（完整式：自惯量 + 平行轴；绕质心主轴 + 绕原点两套都出）。
    /// · **枢轴选择尚未裁定**（质心 vs 群原点）：本缓存两套都存，批 1/2 再定消费者 ⇒ 改口零返工。
    /// </summary>
    public partial class SpaceEntityGroup
    {
        private InertiaTensor.InertiaResult cachedInertia;
        private int inertiaStamp = int.MinValue;
        private List<InertiaTensor.PartContribution>? inertiaScratch;

        /// <summary>上次算出惯量时的指纹（诊断读数；<see cref="int.MinValue"/> = 从未算过）。</summary>
        public int InertiaStamp => inertiaStamp;

        /// <summary>进程内惯量重算总次数（**探针**：静止时应不增长——批 0 的直接判据）。</summary>
        public static int InertiaRecomputeCount { get; private set; }

        /// <summary>群级刚体惯量（惰性、缓存、事件失效）。无 Game（headless）时返回空结果。</summary>
        public InertiaTensor.InertiaResult InertiaResult => EnsureInertia();

        /// <summary>操舵窗/日志用的一行读数（**只读缓存**；不触发无谓重算）。</summary>
        public string InertiaDebugString()
        {
            InertiaDebugLines(out string line1, out string line2, out string line3);
            string s = line1;
            if (!string.IsNullOrEmpty(line2)) s += "｜" + line2;
            if (!string.IsNullOrEmpty(line3)) s += "｜" + line3;
            return s;
        }

        /// <summary>读数拆两行（旧调用点；与三行版同一出处）。</summary>
        public void InertiaDebugLines(out string line1, out string line2)
        {
            InertiaDebugLines(out line1, out line2, out _);
        }

        /// <summary>
        /// 读数拆三行（操舵窗 / PFView 左栏窄栏用；与 <see cref="InertiaDebugString"/> 同一出处）。
        /// ⚠ 数值必须走 <see cref="Sci"/>：`F3E1` 这类"F+E 混写"**不是合法格式串**，.NET 会原样吐出
        /// 字面量（2026-10-17 实测读数是 `I₁=F3E1`——游戏内验收当场抓到的假读数）。
        /// </summary>
        public void InertiaDebugLines(out string line1, out string line2, out string line3)
        {
            var r = EnsureInertia();
            if (!r.HasData)
            {
                line1 = "惯量: 无数据（无 Game / 无部件）";
                line2 = string.Empty;
                line3 = string.Empty;
                return;
            }
            line1 = $"I₁={Sci(r.p1)} I₂={Sci(r.p2)} I₃={Sci(r.p3)} kg·m²";
            line2 = $"质心=({r.comX:F2},{r.comY:F2},{r.comZ:F2}) m｜{InertiaTypeLabel(r)}｜重算#{InertiaRecomputeCount}";
            line3 = $"原点 I=({Sci(r.oIxx)},{Sci(r.oIyy)},{Sci(r.oIzz)})";
        }

        /// <summary>紧凑科学计数（3 位有效：`5.833E+5`）。</summary>
        private static string Sci(double v) => v.ToString("0.###E+0", CultureInfo.InvariantCulture);

        /// <summary>
        /// 绕**本体系**某方向（不必单位）的魔法限速（rad/s，批 1）：def 覆盖优先；
        /// 否则 `clamp(τ_ref / (nᵀ·I_com·n), 0.2, 30)°/s`。
        /// 滚转 = (0,0,1)、偏航 = (0,1,0)、俯仰 = (1,0,0)（与 `SteeringController` 的键位轴一致）。
        /// </summary>
        public float MaxAngularRateAboutBodyAxis(double x, double y, double z)
        {
            if (maxAngularRate > 0f) return maxAngularRate;
            var r = InertiaResult;
            if (!r.HasData) return DefaultMaxAngularRate;
            double moment = r.MomentAboutBodyAxis(x, y, z);
            return moment > 0.0 ? (float)InertiaTensor.RateForMoment(moment) : DefaultMaxAngularRate;
        }

        /// <summary>绕**世界系**某方向的魔法限速（rad/s）：方向转进本体系后再按轴查
        /// （`AlignShipAxis` 的最短旋转轴就是世界系向量）。</summary>
        public float MaxAngularRateAboutWorldAxis(DVector3 worldAxis)
        {
            if (maxAngularRate > 0f) return maxAngularRate;
            var body = DVector3.Rotate(worldAxis, DQuaternion.Inverse(CurrentOrbit.rotation));
            return MaxAngularRateAboutBodyAxis(body.x, body.y, body.z);
        }

        /// <summary>分类的中文标签（含"近简并带"后缀；与分类器同一出处，禁止第二份判定）。</summary>
        private static string InertiaTypeLabel(in InertiaTensor.InertiaResult r)
        {
            switch (r.type)
            {
                case InertiaTensor.InertiaType.Isotropic: return "等惯";
                case InertiaTensor.InertiaType.Axisymmetric: return "轴对称";
                default:
                    return InertiaTensor.IsNearDegenerate(r.p1, r.p2, r.p3, out _, out _)
                        ? "一般(近简并带)" : "一般";
            }
        }

        private InertiaTensor.InertiaResult EnsureInertia()
        {
            var comp = (preparedPhysicsHost ?? Current.Game?.GetComponent<SpaceGameComp>());
            if (comp == null) return default;

            int stamp = CalcInertiaStamp(comp);
            if (stamp == inertiaStamp) return cachedInertia;

            inertiaScratch ??= new List<InertiaTensor.PartContribution>(16);
            inertiaScratch.Clear();

            foreach (int eid in entityIds)
            {
                var e = comp.GetEntityById(eid);
                if (e == null) continue;
                var parts = e.Parts;
                for (int i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    if (part == null) continue;
                    // B5 / D2 乙：损毁件仍在、仍有质量/惯量（残骸参与物理）；其燃料已漏光。
                    double m = part.Mass;
                    // B4a：tank part 的燃料存量按件位置进入 COM/惯量（结构质量仍由 part.Mass 给）
                    var tank = part.GetComponent<PropellantTankComponentData>();
                    if (tank != null && !part.isDestroyed) m += tank.massKg;
                    if (!(m > 0.0)) continue;

                    var inertiaComp = part.GetComponent<PartInertiaComponent>();
                    var shape = part.shape;
                    double sx = part.boundsSizeShip.x;
                    double sy = part.boundsSizeShip.y;
                    double sz = part.boundsSizeShip.z;
                    bool useCustom = false;
                    double cxx = 0.0, cyy = 0.0, czz = 0.0, cxy = 0.0, cxz = 0.0, cyz = 0.0;
                    if (inertiaComp != null)
                    {
                        shape = inertiaComp.shape;
                        useCustom = inertiaComp.useCustomTensor;
                        cxx = inertiaComp.cIxx; cyy = inertiaComp.cIyy; czz = inertiaComp.cIzz;
                        cxy = inertiaComp.cIxy; cxz = inertiaComp.cIxz; cyz = inertiaComp.cIyz;
                        if (inertiaComp.dims.x > 0.0) sx = inertiaComp.dims.x;
                        if (inertiaComp.dims.y > 0.0) sy = inertiaComp.dims.y;
                        if (inertiaComp.dims.z > 0.0) sz = inertiaComp.dims.z;
                    }

                    InertiaTensor.PartSelfInertia(shape, m, sx, sy, sz, useCustom,
                        cxx, cyy, czz, cxy, cxz, cyz,
                        out double ixx, out double iyy, out double izz,
                        out double ixy, out double ixz, out double iyz);

                    inertiaScratch.Add(new InertiaTensor.PartContribution
                    {
                        mass = m,
                        px = e.Position.x + part.positionShip.x,
                        py = e.Position.y + part.positionShip.y,
                        pz = e.Position.z + part.positionShip.z,
                        cxx = ixx,
                        cyy = iyy,
                        czz = izz,
                        cxy = ixy,
                        cxz = ixz,
                        cyz = iyz,
                        rotation = part.rotationShip,
                    });
                }
            }

            var result = InertiaTensor.Compose(inertiaScratch);
            cachedInertia = result;
            inertiaStamp = stamp;
            if (result.HasData)
                InertiaRecomputeCount++;   // 探针：仅"真的重算且出数据"才 +1
            return result;
        }

        /// <summary>指纹：群实体集版本 ⊕ Σ 各实体 (partSetVersion, tankMassVersion)。
        /// 罐内工质质量不进功能 stats，但进 COM/惯量 ⇒ 单独用 tankMassVersion 触发重算，
        /// 避免燃烧每步把 `InvalidateStats` 的 stats/群信号也一起打脏。</summary>
        private int CalcInertiaStamp(SpaceGameComp comp)
        {
            int stamp = entitySetVersion * 397;
            foreach (int eid in entityIds)
            {
                var e = comp.GetEntityById(eid);
                if (e != null)
                    stamp ^= e.partSetVersion * 31 ^ e.tankMassVersion * 17;
            }
            return stamp;
        }
    }
}
