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
        /// <summary>
        /// 基准高度（h=0）处的大气密度 ρ₀ (kg/m³)。**0 = 未定/无大气**（[补2]，2026-10-06：
        /// 数值不按地球取值、延后裁定）—— 与 <see cref="atmosphereThickness"/> = 0 一样
        /// ⇒ 密度模型**恒惰性**（"缺席即零"，见 <see cref="AtmosphereModel"/>）。
        /// </summary>
        public double atmosphereRho0;
        /// <summary>
        /// 大气标尺高度 H (m)：ρ(h) = ρ₀·exp(−h/H)。**0 = 未定**（同 ρ₀ ⇒ 惰性）。
        /// </summary>
        public double atmosphereScaleHeight;
        /// <summary>
        /// **恢复阈值**（[A6]，2026-10-06）：多少**个标尺高度**之外认为"气动可忽略"（ρ/ρ₀ ≤ e^−k）。
        /// 同一常数、同一语义供两处：① 引力掩码的**单向闩锁恢复**判据（取反）；
        /// ② **解析圆轨准入**（`EnterCircularOrbit` 进入前查）。保守取 9.5（计划稿 §2.5："保守取 9-10 个标尺高度"）。
        /// 默认值写在字段上 ⇒ 不配 XML 也有意义。
        /// </summary>
        public double atmosphereRestoreScaleHeights = 9.5;
        /// <summary>
        /// 大气**视觉色**（2026-10-10 渲染线加，用户口径："摄像机越深入天空盒越蔚蓝"）。
        /// 语义 = "从这颗天体的大气内部抬头看到的颜色"：天空盒按它上色、外面那圈半透明壳也用它。
        /// 不填 ⇒ 天蓝（默认值就在字段上）。想给别的行星配色（火星土黄之类）就往它的 Def 里加一行
        /// `<atmosphereColor>(0.62, 0.48, 0.35)</atmosphereColor>`。
        /// ⚠ **只影响表现**：物理（阻力/包线/掩码/准入）一律读 ρ₀ / H / 厚度，不读这个颜色。
        /// </summary>
        public Color atmosphereColor = new Color(0.35f, 0.55f, 0.85f, 1f);
        /// <summary>
        /// **基底球渲染色**（2026-10-11 用户口径："在天体定义里加一个基底球渲染颜色，然后把这颗行星的设置为烘焙海洋格子的颜色"）。
        /// 语义 = **显示色**（"我想在屏幕上看到什么颜色"，按 0..1 填，例如烘焙海洋格的字节 (72,72,104) ⇒ (0.282,0.282,0.408)）——
        /// 代码会按工程的颜色空间把它转成**渲染用的线性值**再用（`PFViewSurfaceBake.ToRenderColor`），
        /// 因为 `Material.color` 在 Linear 工程里是**线性值**：把显示色直接填进去会明显偏亮（"基底球看起来浅"）。
        /// **默认 `Color.clear`（a=0）= 未设置** ⇒ 退回"数据区里水格最多的 biome 的贴图"那条口径。
        /// 只影响表现：物理/几何一律不读它。
        /// </summary>
        public Color baseSphereColor = new Color(0f, 0f, 0f, 0f);
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

        // ── 运行时缓存（[A1]，2026-10-06）：大气密度剖面（64 点表），首次访问时建 ──
        [Verse.Unsaved]
        private volatile bool atmosphereProfileCached;
        [Verse.Unsaved]
        private AtmosphereProfile atmosphereProfileCache;

        /// <summary>
        /// **大气密度剖面（[A1]，计划稿 §2.1）**：由 <see cref="atmosphereRho0"/> / <see cref="atmosphereScaleHeight"/> /
        /// <see cref="atmosphereThickness"/> 建一次 64 点表并缓存；**任一参数 ≤ 0 ⇒ 惰性剖面**（`Density` 恒 0）。
        ///
        /// **写序**：同 <see cref="EnsureOrbitFrame"/> —— 先把值写好、最后才置标志（volatile）；
        /// 两线程同时首建无害（各自建出的表逐位相同，谁最后写都一样）。
        /// ⚠ 建表是**分配**：调用方应在**进并行物理区前预暖**（[A2] 的调用纪律）。
        /// </summary>
        public AtmosphereProfile Atmosphere
        {
            get { EnsureAtmosphereProfile(); return atmosphereProfileCache; }
        }

        /// <summary>预热/取剖面：幂等；惰性参数下也置标志（省得每次重试建表）。</summary>
        public void EnsureAtmosphereProfile()
        {
            if (atmosphereProfileCached) return;
            atmosphereProfileCache = AtmosphereModel.Build(
                atmosphereRho0, atmosphereScaleHeight, atmosphereThickness);
            atmosphereProfileCached = true;
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
