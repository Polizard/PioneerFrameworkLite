using System;
using System.Collections.Generic;
using PioneerFramework.Celestial;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>业务消费者的实例数据副本。保持既有单位和成员名，构造后只能读取；不属于 DefDatabase。</summary>
    public sealed record CelestialBodyInfo
    {
        public CelestialInstanceId InstanceId { get; init; }
        public long DescriptorRevision { get; init; }
        public string defName { get; init; } = "";
        public string label { get; init; } = "";
        public TaggedString LabelCap => label.CapitalizeFirst();
        public int bodyId { get; init; }
        public CelestialBodyType bodyType { get; init; }
        public double radius { get; init; }
        public double gravitationalParameter { get; init; }
        public bool hasSurface { get; init; }
        /// <summary>物理大气顶高度 (m)（= `air.topAltitude`）：天空 α、高度阈值、大气顶/进入判定用它。</summary>
        public double atmosphereThickness { get; init; }
        /// <summary>视觉大气壳厚度 (m)（= 视觉壳高度 × 2 = `atmosphereVisual.shellHeightMeters × 2`）：画壳/壳半径/壳网格/壳偏移用它。0 = 没有视觉壳。</summary>
        public double atmosphereVisualThickness { get; init; }
        public double atmosphereRho0 { get; init; }
        public double atmosphereScaleHeight { get; init; }
        public double atmosphereRestoreScaleHeights { get; init; } = 9.5;
        public Color atmosphereColor { get; init; } = new(0.35f, 0.55f, 0.85f, 1f);
        public Color baseSphereColor { get; init; } = Color.clear;
        public double dayLengthTicks { get; init; } = 60000;
        public string? planetLayerDefName { get; init; }
        public double orbitRadius { get; init; }
        public double orbitEccentricity { get; init; }
        public double OrbitPeriapsis => orbitRadius * (1 - orbitEccentricity);
        public double orbitPeriod { get; init; }
        public Vector3 orbitNormal { get; init; }
        public double initialPhase { get; init; }
        public string? parentBodyDefName { get; init; }
        public int ParentBodyId { get; init; } = -1;
        public int ReferenceFrameBodyId { get; init; } = -1;
        public IReadOnlyList<CelestialBodyInfo> moonBodies { get; init; } = Array.Empty<CelestialBodyInfo>();
        public bool HasOrbit => orbitRadius > 0;
        public bool IsGravitySource => gravitationalParameter > 0;
        public DVector3 OrbitNormalD => (DVector3)orbitNormal;
        private volatile bool orbitFrameCached;
        private DVector3 refX;
        private DVector3 refY;
        private volatile bool atmosphereCached;
        private AtmosphereProfile atmosphere;
        // 临时离线数值夹具以及轨道线输入仍需要正交基；生产运动一律来自协议快照。
        public void EnsureOrbitFrame()
        {
            if (orbitFrameCached) return;
            var normal = OrbitNormalD;
            if (normal.sqrMagnitude > 0) normal = normal.normalized;
            var up = Math.Abs(DVector3.Dot(normal, DVector3.up)) > 0.99 ? DVector3.forward : DVector3.up;
            refX = DVector3.Cross(normal, up).normalized;
            refY = DVector3.Cross(normal, refX).normalized;
            orbitFrameCached = true;
        }
        public void GetOrbitFrame(out DVector3 x, out DVector3 y) { EnsureOrbitFrame(); x = refX; y = refY; }
        public void EnsureAtmosphereProfile()
        {
            if (atmosphereCached) return;
            atmosphere = AtmosphereModel.Build(atmosphereRho0, atmosphereScaleHeight, atmosphereThickness);
            atmosphereCached = true;
        }
        public AtmosphereProfile Atmosphere { get { EnsureAtmosphereProfile(); return atmosphere; } }
    }
}
