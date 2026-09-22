using System;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.Launch
{
    /// <summary>
    /// 天地映射单一事实源（设计 §3）：地面发射台（tile）→ ECI 惯性状态。
    /// 冻结约定（2026-09-05 以 RimWorld 源码逐环节锚定）：
    ///   自转轴 = +y（北极），赤道面 = xz；
    ///   RW 经度 0 = 世界 −z（LongLatOf: λ=Atan2(x,−z)），东（λ 增）= −z→+x；
    ///   RW 正午太阳 = (0,0,−1)（CurSunPositionInWorldSpace），固连系中太阳星下点经度随时间减少（西移）；
    ///   固连系地表位置 = Find.WorldGrid.GetTileCenter(tile)（渲染网格顶点中心，球心原点无 root 旋转），
    ///   即 p_fix = R(sinλcosφ, sinφ, −cosλcosφ)；
    ///   行星在 ECI 中自转 p_eci(t) = Ry(−ωt)·p_fix（惯性系太阳固定 + 固连系太阳西移 ⇒ 唯一解），
    ///   等价表面点 λ_eci(t) = λ0 + ωt；速度 v = ω·(p×up)（真实自转切向，与 prograde up×radial 反向！）；
    ///   渲染端 ApplyVisualRotation = AngleAxis(+ωt) = Ry(+ωt) 与 Ry(−ωt) 互逆 → 世界视图中钉在发射 tile；
    ///   自转相位时间基准 = physicsTick（与轨道物理同源）。
/// 公开转换 API（tile ↔ ECI 单一事实源，支持任意 PlanetLayer 天体）：
///   GetLayerForBody（bodyId → PlanetLayer）/ TileToFixedPosition / FixedToEci / EciToFixed /
///   TileToEci / TileToWorldEci / EciToTile（星下点反查）/ EciToLongLat / SurfaceVelocity。
///   所有 tile 转换均接受 PlanetLayer 参数（地表层快捷重载默认 Find.WorldGrid.Surface）。
/// ⚠ 坐标系语义：本类所有 ECI 结果都以【天体中心为原点】（local ECI，与 SpaceGameComp 积分系一致，
///   group.CurrentOrbit 直接兼容，发射出生状态无需额外换算）。若需行星系/世界参考系坐标
///   （与其它天体比较、渲染绝对位置、跨天体轨道计算），使用 TileToWorldEci，或手动加
///   CelestialBodyManager.GetBodyWorldPosition(bodyId, physicsTick)。
    /// 出生状态 = 地表点 + 离地高度 alt，速度继承自转初速 v_surface（赤道 ≈ 524 m/s）。
    /// 注意（设计缺口）：高纬度发射会入倾斜圆轨（控制律无面外项）。
    /// </summary>
    public static class GetSurfaceState
    {
        public const double DefaultDayLengthTicks = 60000.0;

        /// <summary>自转角速度 ω (rad/s)：2π/dayLengthTicks（1 tick = 1 s）。</summary>
        public static double RotationOmega(CelestialBodyManager mgr, int bodyId)
        {
            var body = mgr?.GetBodyById(bodyId);
            double dayTicks = body != null && body.dayLengthTicks > 0.0 ? body.dayLengthTicks : DefaultDayLengthTicks;
            return 2.0 * Math.PI / dayTicks;
        }

        /// <summary>地心半径 (m)。</summary>
        public static double BodyRadius(CelestialBodyManager mgr, int bodyId)
        {
            var body = mgr?.GetBodyById(bodyId);
            return body != null && body.radius > 0.0 ? body.radius : SpaceConstants.PlanetRadiusMeters;
        }

        /// <summary>
        /// 经纬度 → 惯性状态（行星中心原点，ECI）。随自转时变，点火时刻现算。
        /// 公式与 GetTileCenter 固连真值同构（t=0 时相等），供纯数学/标记同步使用。
        /// </summary>
        /// <param name="radius">地心半径 (m)。</param>
        /// <param name="latRad">纬度 φ (rad)。</param>
        /// <param name="lonRad0">固连经度 λ₀ (rad，RimWorld LongLatOf 约定，表面上不动)。</param>
        /// <param name="omega">自转角速度 ω (rad/s)。</param>
        /// <param name="physicsTick">当前物理时间 (s)。</param>
        public static (DVector3 pos, DVector3 vel) SurfaceState(double radius, double latRad, double lonRad0, double omega, double physicsTick)
            => SurfaceFrame.SurfaceState(radius, latRad, lonRad0, omega, physicsTick);

        /// <summary>表面上方出生点：p + radial·alt，速度继承地表（尚未被推力改变）。</summary>
        public static (DVector3 pos, DVector3 vel) SurfaceStateAbove((DVector3 pos, DVector3 vel) surface, double altMeters)
            => SurfaceFrame.SurfaceStateAbove(surface, altMeters);

        /// <summary>
        /// 发射台 → 惯性出生状态（点火时刻现算，发射台随自转在动）。
        /// tile 取 groundMap.Tile（其 Layer 即该天体地表网格层——地球 Surface / 月球 Moon 均自动正确），
        /// 固连位置 = layer.GetTileCenter（渲染网格顶点中心）归一化 × 米制物理半径 → ECI = Ry(−ωt)·p_fixed。
        /// 返回【相对天体中心的 local ECI】：直接可放 group.CurrentOrbit（SpaceGameComp 积分即 local 系），
        /// 无需加天体世界位置。
        /// </summary>
        public static (DVector3 pos, DVector3 vel) GroundStateAtLaunch(Map groundMap, double altMeters, int bodyId)
        {
            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            double radius = BodyRadius(mgr, bodyId);
            double omega = RotationOmega(mgr, bodyId);

            PlanetLayer layer = groundMap != null ? groundMap.Tile.Layer : Find.WorldGrid.Surface;
            int tileId = groundMap != null ? groundMap.Tile : 0;
            var pEci = TileToEci(layer, tileId, radius, omega, physicsTick);
            var vEci = SurfaceVelocity(pEci, omega);

            return SurfaceStateAbove((pEci, vEci), altMeters);
        }

        // ═══ tile ↔ 物理层（ECI）坐标转换（天地映射公开 API，单一事实源）═══
        // 支持任意 PlanetLayer 天体：tile 的中心取 tile.Layer.GetTileCenter（渲染网格顶点中心），
        // 归一化 × 该天体的米制物理半径（CelestialBodyDef.radius，非渲染单位）→ 固连系米制位置。
        // 约定：所有 layer 均为球心原点、极轴 +y 的球面网格，经度 0 = −z（与 RW LongLatOf 一致）。

        /// <summary>
        /// bodyId → PlanetLayer（该天体的地表网格层）。
        /// 匹配规则与 CelestialBodyLayerManager 一致：planetLayerDefName 优先，否则用 body.defName。
        /// 无表面 / 未创建 layer 返回 null。
        /// </summary>
        public static PlanetLayer GetLayerForBody(CelestialBodyManager mgr, int bodyId)
        {
            var body = mgr?.GetBodyById(bodyId);
            if (body == null || !body.hasSurface) return null;
            string defName = !string.IsNullOrEmpty(body.planetLayerDefName) ? body.planetLayerDefName : body.defName;
            var grid = Find.WorldGrid;
            if (grid == null) return null;
            foreach (var kv in grid.PlanetLayers)
            {
                if (kv.Value?.Def?.defName == defName) return kv.Value;
            }
            return null;
        }

        /// <summary>tile → 固连系位置（米）：layer.GetTileCenter（渲染网格顶点中心）归一化 × 物理半径。任意 PlanetLayer。</summary>
        public static DVector3 TileToFixedPosition(PlanetLayer layer, int tileId, double bodyRadiusMeters)
        {
            Vector3 tc = layer != null ? layer.GetTileCenter(tileId) : Find.WorldGrid.GetTileCenter(tileId);
            return new DVector3(tc.x, tc.y, tc.z).normalized * bodyRadiusMeters;
        }

        /// <summary>tile → 固连系位置（米，地表层快捷：Find.WorldGrid.Surface）。</summary>
        public static DVector3 TileToFixedPosition(int tile, double bodyRadiusMeters)
            => TileToFixedPosition(Find.WorldGrid.Surface, tile, bodyRadiusMeters);

        // 【2026-09-18】本文件里的**纯数学**已搬到 `Base/Orbital/SurfaceFrame.cs`
        // （**实现只有一份**，纯层可复用、可单测；那段"冻结约定"的文档也随之停到实现现场）。
        // 这里只**转发**，以保持 `GetSurfaceState` 的公开 API 与全部调用方不变。

        /// <summary>固连系 → ECI：Ry(−ωt)。见 <see cref="SurfaceFrame.FixedToEci" />。</summary>
        public static DVector3 FixedToEci(DVector3 pFixed, double omega, double physicsTick)
            => SurfaceFrame.FixedToEci(pFixed, omega, physicsTick);

        /// <summary>ECI → 固连系：Ry(+ωt)。见 <see cref="SurfaceFrame.EciToFixed" />。</summary>
        public static DVector3 EciToFixed(DVector3 pEci, double omega, double physicsTick)
            => SurfaceFrame.EciToFixed(pEci, omega, physicsTick);

        /// <summary>tile → ECI 位置（米，行星中心原点，随自转时变）。任意 PlanetLayer。</summary>
        public static DVector3 TileToEci(PlanetLayer layer, int tileId, double bodyRadiusMeters, double omega, double physicsTick)
            => FixedToEci(TileToFixedPosition(layer, tileId, bodyRadiusMeters), omega, physicsTick);

        /// <summary>tile → ECI（地表层快捷）。</summary>
        public static DVector3 TileToEci(int tile, double bodyRadiusMeters, double omega, double physicsTick)
            => TileToEci(Find.WorldGrid.Surface, tile, bodyRadiusMeters, omega, physicsTick);

        /// <summary>
        /// tile → ECI（天体级入口）：tile id 按 bodyId 对应的 PlanetLayer 解释，物理量从 CelestialBodyManager 取。
        /// 返回相对该天体中心的 local ECI。
        /// </summary>
        public static DVector3 TileToEci(int tile, CelestialBodyManager mgr, int bodyId, double physicsTick)
        {
            var layer = GetLayerForBody(mgr, bodyId) ?? Find.WorldGrid.Surface;
            return TileToEci(layer, tile, BodyRadius(mgr, bodyId), RotationOmega(mgr, bodyId), physicsTick);
        }

        /// <summary>
        /// tile → 世界参考系 ECI（行星系/世界坐标）：local ECI + 天体世界位置（GetBodyWorldPosition）。
        /// 用于跨天体比较 / 渲染绝对位置 / 与其它天体轨道叠加。天体级入口。
        /// </summary>
        public static DVector3 TileToWorldEci(int tile, CelestialBodyManager mgr, int bodyId, double physicsTick)
            => TileToEci(tile, mgr, bodyId, physicsTick) + (mgr?.GetBodyWorldPosition(bodyId, physicsTick) ?? DVector3.zero);

        /// <summary>
        /// ECI → tile（星下点反查）：Ry(+ωt) 回固连系后取该 layer 中方向最近的 tile。
        /// 线性扫描全网格（~15k tile），只适合事件级调用（落点选点/标记/测试），不要每物理步调用。
        /// </summary>
        public static int EciToTile(DVector3 pEci, PlanetLayer layer, double omega, double physicsTick)
        {
            if (layer == null) return -1;
            DVector3 pFixed = EciToFixed(pEci, omega, physicsTick);
            if (pFixed.sqrMagnitude < 1e-9) return -1;
            Vector3 dir = ((Vector3)pFixed).normalized;
            int best = -1;
            float bestDot = float.MinValue;
            for (int i = 0; i < layer.TilesCount; i++)
            {
                float d = Vector3.Dot(layer.GetTileCenter(i), dir);   // center 半径恒定，免归一化
                if (d > bestDot) { bestDot = d; best = i; }
            }
            return best;
        }

        /// <summary>ECI → tile（地表层快捷）。</summary>
        public static int EciToTile(DVector3 pEci, double omega, double physicsTick)
            => EciToTile(pEci, Find.WorldGrid.Surface, omega, physicsTick);

        /// <summary>ECI → tile（天体级入口）：按 bodyId 对应的 PlanetLayer 反查。</summary>
        public static int EciToTile(DVector3 pEci, CelestialBodyManager mgr, int bodyId, double physicsTick)
        {
            var layer = GetLayerForBody(mgr, bodyId);
            if (layer == null) return -1;
            return EciToTile(pEci, layer, RotationOmega(mgr, bodyId), physicsTick);
        }

        /// <summary>
        /// ECI → 固连经纬度 (rad)。与 RW LongLatOf 同约定：λ = Atan2(x, −z)，φ = Asin(y/r)。
        /// 经纬度→ECI 走 SurfaceState；此为反向。
        /// </summary>
        public static (double latRad, double lonRad) EciToLongLat(DVector3 pEci, double omega, double physicsTick)
            => SurfaceFrame.EciToLongLat(pEci, omega, physicsTick);

        /// <summary>表面点自转速度（ECI，米/秒）：v = ω·(p×up) = ω·(−p_z, 0, p_x)（真实自转切向，与 ω×p 反向）。</summary>
        public static DVector3 SurfaceVelocity(DVector3 pEci, double omega)
            => SurfaceFrame.SurfaceVelocity(pEci, omega);
    }
}
