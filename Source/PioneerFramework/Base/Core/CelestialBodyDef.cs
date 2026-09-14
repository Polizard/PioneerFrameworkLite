using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public enum CelestialBodyType
    {
        Star,
        Planet,
        Moon
    }

    /// <summary>
    /// 天体定义。通过 XML 注册，由 DefDatabase 管理。
    /// 物理量使用 double 以兼容高精度计算。
    /// </summary>
    public class CelestialBodyDef : Def
    {
        // ── 基础属性 ──
        /// <summary>数值 ID，运行时快速查找用</summary>
        public int bodyId;
        /// <summary>天体类型</summary>
        public CelestialBodyType bodyType;
        /// <summary>半径（米）</summary>
        public double radius;
        /// <summary>引力参数 GM（m³/s²）</summary>
        public double gravitationalParameter;
        /// <summary>是否有固体表面（能否着陆）</summary>
        public bool hasSurface;
        /// <summary>大气层厚度（米），0 = 无大气</summary>
        public double atmosphereThickness;
        /// <summary>自转周期（游戏 tick），默认 60000 = 1 RimWorld 天。火星约 61500，木星约 21400</summary>
        public double dayLengthTicks = 60000.0;
        /// <summary>对应的 PlanetLayerDef 名称。不填则默认使用 defName 匹配</summary>
        public string planetLayerDefName;

        // ── 轨道参数（Star 类型不需要）──
        /// <summary>轨道半径（米）</summary>
        public double orbitRadius;
        /// <summary>轨道周期（游戏 tick，1 tick = 1 秒）</summary>
        public double orbitPeriod;
        /// <summary>
        /// 轨道平面法线向量（XML 反序列化用 Vector3，运行时通过 OrbitNormalD 获取 DVector3）。
        /// </summary>
        public Vector3 orbitNormal;
        /// <summary>
        /// 轨道平面法线（DVector3 懒加载）。
        /// </summary>
        public DVector3 OrbitNormalD => (DVector3)orbitNormal;
        /// <summary>初始相位（弧度，t=0 时的角度）</summary>
        public double initialPhase;

        // ── 运行时缓存（PERF-3）：轨道平面正交基，避免 N 体积分热路径每次重算 normalized + 双重 cross ──
        // volatile：见 EnsureOrbitFrame 的写序说明（读写可能发生在不同线程）。
        [Verse.Unsaved]
        private volatile bool orbitFrameCached;
        [Verse.Unsaved]
        private DVector3 orbitFrameRefX;
        [Verse.Unsaved]
        private DVector3 orbitFrameRefY;

        /// <summary>懒加载计算并缓存轨道平面正交基（规范化法线 + refX/refY），供圆轨解析位置/速度热路径复用。
        /// **写序**：先把两个基向量写好，最后才置标志（标志 volatile）——反过来的话，并发首用的一方
        /// 可能看到"标志已置、基向量还是零"，于是拿到 (0,0,0) 的 refX 把位置算成 0 或错方向
        /// （本缓存会被 Parallel.For 里的群组引力场查到，见 `BodySampleCache` 的并发说明）。
        /// 两线程同时首算无害：各自算出的值逐位相同，谁最后写都一样。</summary>
        public void EnsureOrbitFrame()
        {
            if (orbitFrameCached) return;

            DVector3 normal = OrbitNormalD;
            if (normal.sqrMagnitude > 0.0)
                normal = normal.normalized;

            DVector3 up = System.Math.Abs(DVector3.Dot(normal, DVector3.up)) > 0.99
                ? DVector3.forward : DVector3.up;
            DVector3 refX = DVector3.Cross(normal, up).normalized;
            DVector3 refY = DVector3.Cross(normal, refX).normalized;

            orbitFrameRefX = refX;
            orbitFrameRefY = refY;
            orbitFrameCached = true;
        }

        /// <summary>获取缓存的轨道平面正交基（refX/refY）。</summary>
        public void GetOrbitFrame(out DVector3 refX, out DVector3 refY)
        {
            EnsureOrbitFrame();
            refX = orbitFrameRefX;
            refY = orbitFrameRefY;
        }

        // ── XML 交叉引用 ──
        /// <summary>父天体的 defName（XML 引用），空表示直接绕恒星</summary>
        public string parentBodyDefName;

        // ── 运行时解析（不由 XML 直接填充）──
        /// <summary>父天体的 bodyId，-1 表示绕恒星</summary>
        public int ParentBodyId
        {
            get; private set;
        }

        // ── 运行时预加载 ──
        /// <summary>此天体的卫星列表（运行时由 CelestialBodyManager 填充）。</summary>
        [Verse.Unsaved]
        public List<CelestialBodyDef> moonBodies = new List<CelestialBodyDef>();

        /// <summary>
        /// RimWorld Def 系统的引用解析。
        /// 将 parentBodyDefName 解析为实际的 ParentBodyId。
        /// </summary>
        public override void ResolveReferences()
        {
            base.ResolveReferences();
            ParentBodyId = -1;
            if (!string.IsNullOrEmpty(parentBodyDefName))
            {
                var parent = DefDatabase<CelestialBodyDef>.GetNamedSilentFail(parentBodyDefName);
                if (parent != null)
                    ParentBodyId = parent.bodyId;
                else
                    Log.Error(string.Format("[CelestialBodyDef] {0}: parentBodyDefName '{1}' not found.", defName, parentBodyDefName));
            }
        }

        /// <summary>
        /// 是否拥有轨道（恒星返回 false）。
        /// </summary>
        public bool HasOrbit => bodyType != CelestialBodyType.Star;

        /// <summary>
        /// 是否为引力源（有 GM 的天体都算）。
        /// </summary>
        public bool IsGravitySource => gravitationalParameter > 0.0;
    }
}
