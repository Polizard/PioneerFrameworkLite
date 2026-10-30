using System;
using PioneerFramework.Launch;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **发射站点**（2026-10-27）：从"地图/天体"解析出自动发射需要的全部天体量。
    /// 解决旧代码写死 `EarthBodyId = 1` 的问题（月球/其它天体的殖民地此前会被按地球参数发射）。
    ///
    /// 数据来源：`map.Tile.Layer` → `GetSurfaceState.GetBodyForLayer` → `CelestialBodyDef`
    /// （半径/GM/大气）+ `OrbitAdapter.SoiRadiusApprox`（Hill 近似）+ 系统成员位置（最近邻距离）。
    /// 只在**注册发射时**调一次，不进每步循环。
    /// </summary>
    public readonly struct LaunchSite
    {
        public readonly int BodyId;
        public readonly double Radius;
        public readonly double Mu;
        public readonly double Omega;
        public readonly double AtmosphereThickness;
        /// <summary>SOI 近似半径（Hill，m）；NaN = 无法近似。</summary>
        public readonly double SoiApprox;
        /// <summary>最近邻天体中心距（m）；∞ = 无邻居。</summary>
        public readonly double NearestBodyDistance;

        private LaunchSite(int bodyId, double radius, double mu, double omega,
            double atmosphereThickness, double soiApprox, double nearestBodyDistance)
        {
            BodyId = bodyId;
            Radius = radius;
            Mu = mu;
            Omega = omega;
            AtmosphereThickness = atmosphereThickness;
            SoiApprox = soiApprox;
            NearestBodyDistance = nearestBodyDistance;
        }

        public LaunchTargetInputs TargetInputs
            => new(Radius, Mu, AtmosphereThickness, SoiApprox, NearestBodyDistance);

        /// <summary>
        /// **体心系 → 世界系**（2026-10-27）：走唯一换算口 `FrameConvert`，偏移取
        /// `GetCentralBodyOffset/Velocity`（行星 → 零；卫星 → 绕父天体的偏移）。
        /// `CurrentOrbit.positionSystem` 是世界系，而地表状态/轨道要素都是体心系。
        /// </summary>
        public (DVector3 pos, DVector3 vel) ToWorld(DVector3 bodyCenteredPos, DVector3 bodyCenteredVel, double tick)
            => BodyToSystem(BodyId, bodyCenteredPos, bodyCenteredVel, tick);

        /// <summary>静态版（站点解析前也能用）：体心系 → 世界系。</summary>
        public static (DVector3 pos, DVector3 vel) BodyToSystem(int bodyId,
            DVector3 bodyCenteredPos, DVector3 bodyCenteredVel, double tick)
        {
            var temp = new SpaceEntityGroup(0, 0) { CelestialBodyId = bodyId };
            SystemVec3 off = temp.GetCentralBodyOffset(tick);
            SystemVec3 velOff = temp.GetCentralBodyVelocity((int)System.Math.Round(tick));
            return (FrameConvert.BodyToSystem(new BodyVec3(bodyCenteredPos), off),
                    FrameConvert.BodyToSystemVelocity(new BodyVec3(bodyCenteredVel), velOff));
        }

        /// <summary>按地面地图解析（地图所在 PlanetLayer → bodyId）。</summary>
        public static bool TryResolve(Map? map, out LaunchSite site, out string reason)
        {
            site = default;
            reason = "";
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (mgr == null || sg == null) { reason = "CelestialBodyManager / SpaceGameComp 缺失"; return false; }
            if (map == null) { reason = "没有地面地图"; return false; }

            PlanetLayer? layer = map.Tile.Layer;
            int bodyId = GetSurfaceState.GetBodyForLayer(mgr, layer);
            if (bodyId < 0) { reason = $"地图 {map.uniqueID} 所在层不是天体地表"; return false; }
            return TryResolve(mgr, sg, bodyId, out site, out reason);
        }

        /// <summary>按天体 id 解析（跑批/无地图场景用）。</summary>
        public static bool TryResolve(int bodyId, out LaunchSite site, out string reason)
        {
            site = default;
            reason = "";
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (mgr == null || sg == null) { reason = "CelestialBodyManager / SpaceGameComp 缺失"; return false; }
            return TryResolve(mgr, sg, bodyId, out site, out reason);
        }

        private static bool TryResolve(CelestialBodyManager mgr, SpaceGameComp sg, int bodyId,
            out LaunchSite site, out string reason)
        {
            site = default;
            reason = "";
            var def = mgr.GetBodyById(bodyId);
            if (def == null) { reason = $"天体 {bodyId} 不存在"; return false; }
            if (!def.hasSurface) { reason = $"天体 {def.defName} 没有固体表面"; return false; }

            double radius = GetSurfaceState.BodyRadius(mgr, bodyId);
            double mu = sg.GetBodyGM(bodyId);
            if (!(radius > 0.0) || !(mu > 0.0))
            {
                reason = $"天体 {def.defName} 半径/GM 无效（R={radius:E2}, μ={mu:E2}）";
                return false;
            }

            double omega = GetSurfaceState.RotationOmega(mgr, bodyId);
            double soi = OrbitAdapter.SoiRadiusApprox(def, mgr);
            double nearest = NearestNeighborDistance(mgr, def);
            site = new LaunchSite(bodyId, radius, mu, omega, def.atmosphereThickness, soi, nearest);
            return true;
        }

        /// <summary>最近邻天体中心距（**只算父/子天体**，用定义里的 `orbitRadius`；不读绝对位置 ⇒ 不跨帧）。
        /// 同系统的兄弟天体不参与：它们属于别的 SOI，真正的"抢走"来自父/子。无 ⇒ +∞。</summary>
        // 合并（2026-10-31）：`GetBodyById` 现在返回 `CelestialBodyInfo`（远端把"运行时事实"从
        // `CelestialBodyDef`（纯 XML Def）里拆了出来）。字段名一致（bodyId/ParentBodyId/orbitRadius），
        // 只是类型换了 ⇒ 这里只改签名。
        private static double NearestNeighborDistance(CelestialBodyManager mgr, CelestialBodyInfo def)
        {
            var members = mgr.GetSystemMembers(def.bodyId);
            if (members == null || members.Count == 0) return double.PositiveInfinity;

            double best = double.PositiveInfinity;
            for (int i = 0; i < members.Count; i++)
            {
                var other = members[i];
                if (other == null || other.bodyId == def.bodyId) continue;
                double d = -1.0;
                if (def.ParentBodyId == other.bodyId) d = def.orbitRadius;          // 父天体距离
                else if (other.ParentBodyId == def.bodyId) d = other.orbitRadius;   // 子天体距离
                if (d > 0.0 && d < best) best = d;
            }
            return best;
        }

        public string Describe()
            => $"body={BodyId} R={Radius / 1000.0:F0}km μ={Mu:E2} ω={Omega:E3} "
             + $"atmo={(AtmosphereThickness > 0.0 ? (AtmosphereThickness / 1000.0).ToString("F0") + "km" : "无")} "
             + $"SOI={(double.IsNaN(SoiApprox) ? "?" : (SoiApprox / 1000.0).ToString("F0") + "km")} "
             + $"邻居={(double.IsInfinity(NearestBodyDistance) ? "无" : (NearestBodyDistance / 1000.0).ToString("F0") + "km")}";
    }
}
