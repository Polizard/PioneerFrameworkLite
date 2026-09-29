using PioneerFramework.Launch;   // GetSurfaceState（layer↔天体、自转角速度的唯一出处）
using UnityEngine;
using Verse;                     // Current.Game

namespace PioneerFramework
{
    /// <summary>
    /// **渲染的视图参考系**（2026-09-23）—— 系统系（= 根行星中心，米）→ 叠加层视图系（Unity 单位）
    /// 的**唯一换系口**。
    ///
    /// ## 为什么必须有它（用户 2026-09-23 报告的现象）
    /// "航天器和当前行星系其他天体的渲染都是按照行星在中心走的，以至于我切换视角到月球以后只有中心天体改变。"
    /// 物理帧恒为"根行星（地球）中心"（`group.CurrentOrbit.position` 就在这个系），而叠加层原来把**渲染系**也
    /// 写死成地球系 + 地球自转（`360/60000`）+ 地球 layer 半径（`Find.WorldGrid.Surface.Radius`），
    /// 而"玩家在看哪颗天体"（`PlanetLayer.Selected`）**没有任何渲染代码读** ⇒ 切到月球时只有 RimWorld
    /// 画的那颗球换了，我们的船 / 轨道线 / 标记原地不动。
    ///
    /// ## 唯一的视图变换
    /// ```
    /// p_view = Ry(+ω_view · t) · (p_系统系 − viewedBodyOffset(t)) · UnitsPerMeter
    /// ```
    /// · `viewedBody` = `PlanetLayer.Selected` 反查出来的天体（**只看被切换的 layer**，取不到 ⇒ 根行星）；
    /// · `viewedBodyOffset` = 该天体在系统系中的位置 = **绝对 − 绝对**（见下）；
    /// · `ω_view` = 该天体的自转角速度（`GetSurfaceState.RotationOmega`，**不再写死 60000**）。
    ///
    /// ## ⚠ 纪律（违反即事故；`CelestialBodyManager.cs:450-470` 有两次事故的点名）
    /// · **绝对系**（`GetBodyPositionAbs`，递归到恒星）只准用于"绝对 vs 绝对"的差或跨系展示 ——
    ///   绝不准拿去减**系统系**坐标（2026-09-10 星座锚点就是这么差出 12 万 km 的）；
    /// · 换系**只允许有一处**（物理侧是 `OrbitAdapter`，渲染侧就是本类的 `ToViewUnity`）
    ///   ⇒ 调用点不许自己再写一遍 `origin + Rot(p)`；
    /// · 时刻**保留 double**（`physicsTick`）：int 化在 1/60× 流速下会让天体位置每 60 真实 tick 跳 1 秒
    ///   （PFView `:1003-1008` 的实测结论）；
        /// · 旋转方向 `Ry(+ωt)` 与 `SurfaceFrame.FixedToEci = Ry(−ωt)` **互逆**（`GetSurfaceState.cs:14`
        ///   的冻结约定）⇒ 钉在地表 tile 上的东西才不会从地面飞走。**该旋转直接调 `SurfaceFrame.EciToFixed`**
        ///   （不是各写一份再指望符号凑对；`ViewFrameTests` 用"固连点往返"逐 tick 钉住）。
    /// </summary>
    internal static class ViewFrame
    {
        /// <summary>当前被查看的天体（`PlanetLayer.Selected` → 反查；取不到退回根行星）。</summary>
        public static int ViewedBodyId
        {
            get
            {
                var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
                if (mgr == null) return 1;
                var layer = CelestialBodyLayerManager.CurrentCelestialBodyLayer;
                if (layer != null)
                {
                    int id = GetSurfaceState.GetBodyForLayer(mgr, layer);
                    if (id >= 0) return id;
                }
                return RootPlanetId(mgr);
            }
        }

        /// <summary>
        /// 系统系的锚点（**单行星系统下 == `CelestialBodyManager.GetReferenceFrameId`**）。
        ///
        /// ⚠ 2026-09-26（D8）：**凡手里有体的 bodyId，就不要再调这个方法**——用
        /// `mgr.GetReferenceFrameId(bodyId)`，那才是**与物理同一条规则**
        /// （权威口径见 `CelestialBodyManager` 的坐标系约定：系统系 = 根行星/**恒星**为中心、根钉住；
        /// 物理侧 8 处建网格/判域都用它）。本方法只留给"**手上没有被查询的体**"的场合（如 `ViewedBodyId` 的兜底）。
        /// 原先它取"第一个行星 + 硬编码兜底 1"：单一权威被写成两套规则，正是"12 万公里"那一类事故的形状
        /// （`GetPlanets()` 曾长期返回空，而那个 `1` 把症状盖住了）。
        /// </summary>
        public static int RootPlanetId(CelestialBodyManager? mgr)
        {
            var planets = mgr?.GetPlanets();
            return planets != null && planets.Count > 0 ? planets[0].bodyId : 1;
        }

        /// <summary>
        /// 天体在**系统系**中的中心位置（米）= 绝对 − 绝对（两者同系 ⇒ 差是相对位置，纪律允许）。
        /// 根行星恒为零向量。
        /// </summary>
        public static DVector3 BodySystemPosition(int bodyId, double tick)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return DVector3.zero;
            // ⚠ 2026-09-26（D8）：锚点按**被查询的体**解析，与物理同规则（`GetReferenceFrameId`）。
            //   当前天体集（地球/月球）下与旧的"第一个行星"逐值相同；但对恒星/多行星系统旧写法是错的。
            int root = mgr.GetReferenceFrameId(bodyId);
            if (bodyId == root) return DVector3.zero;
            return mgr.GetBodyPositionAbs(bodyId, tick) - mgr.GetBodyPositionAbs(root, tick);
        }

        /// <summary>被查看天体在视图里的半径（Unity 单位）—— 球体遮挡判定用（月亮 30 / 地球 100）。</summary>
        public static float ViewedBodyRadiusUnity
        {
            get
            {
                var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
                var body = mgr?.GetBodyById(ViewedBodyId);
                if (body == null || body.radius <= 0.0) return (float)SpaceConstants.PlanetRadiusUnity;
                return (float)(body.radius * SpaceConstants.UnitsPerMeter);
            }
        }

        /// <summary>**渲染侧唯一的换系口**：系统系米制坐标 → 视图系 Unity 坐标（含被查看天体的自转）。</summary>
        public static Vector3 ToViewUnity(DVector3 systemFrameMeters, double tick)
            => ToViewUnityFromUnity((Vector3)(systemFrameMeters * SpaceConstants.UnitsPerMeter), tick);

        /// <summary>
        /// 同上，但入参**已经是 Unity 单位**的系统系坐标 —— 轨道采样点与规划器预览（`*Unity` 系列）走这条。
        /// `ToViewUnity(米)` 内部就是调它 ⇒ 变换算式仍然**只有一份**。
        ///
        /// ⚠ 2026-09-23 改：旋转**不再用 `Quaternion.AngleAxis`**，改调 `SurfaceFrame.EciToFixed`（两者算式逐项相同，
        /// 见下）。两个理由，都不是"为了好测"：
        /// 1. **消除两份权威**：原来"视图旋转"（Unity 四元数）与"固连↔ECI 冻结约定"（`SurfaceFrame`，纯双精度）
        ///    是**两份独立实现**，上文那句"互逆"只能靠巧合成立 —— 而本类的存在理由正是"换系只准有一处"。
        ///    改调之后"互逆"是**结构性事实**（`EciToFixed` 就是 `FixedToEci` 的逆），地表物体不会飞走这件事
        ///    不再依赖两个文件各自的符号写对。
        /// 2. **去 ECall**：`Quaternion.AngleAxis` 在 Unity 引用程序集里是 **ECall**（引擎外部调用），
        ///    headless 一调即 `SecurityException`（与 `BoardingMathTests.cs:44` 登记的 `Quaternion.Euler` 同族）
        ///    ⇒ 换系口此前**零测试覆盖**；换成纯数学后 `ViewFrameTests` 18 条可 headless 跑。
        ///    纯双精度还顺手免掉"`% 2π` 取模 + `float` 角度"那段精度折返。
        /// </summary>
        public static Vector3 ToViewUnityFromUnity(Vector3 systemFrameUnity, double tick)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            int viewed = ViewedBodyId;
            Vector3 rel = systemFrameUnity - (Vector3)(BodySystemPosition(viewed, tick) * SpaceConstants.UnitsPerMeter);

            double omega = GetSurfaceState.RotationOmega(mgr, viewed);
            // Ry(+ω_view·t)：与 SurfaceFrame.FixedToEci = Ry(−ωt) 互逆（同一份算式，见上）
            return (Vector3)SurfaceFrame.EciToFixed((DVector3)rel, omega, tick);
        }
        /// <summary>
        /// **物理半径（米）→ Unity 球体的缩放值**（给 `GameObject.CreatePrimitive(PrimitiveType.Sphere)` 这类网格用）。
        ///
        /// ⚠ 2026-09-23 修：Unity 的内置球网格**直径是 1（半径 0.5）**，所以 `localScale = 半径` 画出来只有
        /// **一半**大小。用户实测到的现象正是它：切到月球视图时"绕地球的天体轨道半径不对，或者地球的半径不对"
        /// —— 其实是**地球被画成了半径 50 而不是 100**（LEO 船在 110 Unity 单位处，看着就成了 2.2 个地球半径）。
        ///
        /// 这里按**网格自己的 bounds**归一化（不写死 2）⇒ 换成别的球网格也不会再错一次。
        /// </summary>
        public static float RadiusToSphereScale(float radiusMeters, Mesh? mesh)
        {
            float diameter = mesh != null && mesh.bounds.size.x > 0f ? mesh.bounds.size.x : 1f;
            return (float)(radiusMeters * SpaceConstants.UnitsPerMeter * 2f / diameter);
        }
    }
}
