using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 行星际航线演示（2026-11-05）：Rimata → 赭砂。
    ///
    /// 只铺**日心转移段**：从 Rimata 轨道位置（已推出 Rimata SOI）出发，用 Gooding Lambert
    /// 求日心转移速度，船以恒星帧自由飞行；抵达时若进赭砂 SOI，`CheckGroupSoi` 会自动换系。
    /// 星历用本地纯解析传播（与 `GetBodyPositionParentCore` 同公式），不查 runtime 的已提交时间线
    /// ——否则 `now+TOF` 会抛 `CelestialMotionUnavailable: TimeUnavailable`。
    /// </summary>
    internal static class InterplanetaryRouteDemo
    {
        public static void SpawnRimataToZheSha()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null) return;

            int rimata = FindBodyId(mgr, "Earth");     // Rimata 的 Def 仍是 Earth
            int zheSha = FindBodyId(mgr, "ZheSha");    // 赭砂
            var rimataDef = mgr.GetBodyById(rimata);
            var zheShaDef = mgr.GetBodyById(zheSha);
            if (rimataDef == null || zheShaDef == null || !rimataDef.HasOrbit || !zheShaDef.HasOrbit ||
                rimataDef.orbitPeriod <= 0 || zheShaDef.orbitPeriod <= 0)
            {
                Log.Error($"[行星际演示] 天体解析失败：Rimata(id={rimata}) 赭砂(id={zheSha})");
                return;
            }

            double now = TimeScaleManager.GetPhysicsTickSafe();
            // 注意：GetReferenceFrameId(行星) 返回的是**行星自身**（帧根 = 自身），不是父恒星；
            // 日心转移必须用主恒星的 GM。
            var star = mgr.GetStar();
            if (star == null)
            {
                Log.Error("[行星际演示] 主恒星解析失败");
                return;
            }
            int starId = star.bodyId;
            double muStar = star.gravitationalParameter;

            (DVector3 r1, DVector3 v1) = Circular(rimataDef, now);
            double a1 = r1.magnitude;
            double a2 = Circular(zheShaDef, now).pos.magnitude;
            Log.Message($"[行星际演示·诊断] Rimata id={rimata} def={rimataDef.defName} type={rimataDef.bodyType} " +
                $"R={rimataDef.orbitRadius:E4} T={rimataDef.orbitPeriod:F0} ph={rimataDef.initialPhase:F4}" +
                $"｜赭砂 id={zheSha} def={zheShaDef.defName} type={zheShaDef.bodyType} " +
                $"R={zheShaDef.orbitRadius:E4} T={zheShaDef.orbitPeriod:F0} ph={zheShaDef.initialPhase:F4}" +
                $"｜a1={a1:E4} a2={a2:E4} now={now:F0}");
            if (a1 <= 0 || a2 <= 0)
            {
                Log.Error("[行星际演示] 位置非法");
                return;
            }

            // 霍曼半周期作为 TOF；起点推到 Rimata SOI 外 15%，避免一铺出来就被判"进 SOI"。
            double tof = Math.PI * Math.Sqrt(Math.Pow(0.5 * (a1 + a2), 3) / muStar);
            double soiRimata = OrbitAdapter.SoiRadiusApprox(rimataDef, mgr);
            if (!(soiRimata > 0)) soiRimata = 0.01 * a1;
            DVector3 r1Out = r1 * (1.0 + 1.15 * soiRimata / a1);

            (DVector3 r2, _) = Circular(zheShaDef, now + tof);
            (DVector3 vi, DVector3 vf) = Gooding.Solve(muStar, r1Out, r2, tof);
            double dv1 = (vi - v1).magnitude;

            var group = DebugShipSpawner.CreateNoMapShipGroup(spaceGame, starId,
                r1Out, vi, out int _, overThrust: true, circularOrbit: false);
            if (group == null)
            {
                Log.Error("[行星际演示] 铺船失败");
                return;
            }
            group.CelestialBodyId = starId;

            Log.Message($"[行星际演示] Rimata → 赭砂｜TOF={tof:F0}s（{tof / 60000.0:F2} 游戏日）" +
                $"｜日心转移 v={vi.magnitude:F0} m/s，Δv≈{dv1:F0} m/s，到达 v={vf.magnitude:F0} m/s" +
                $"｜Rimata SOI≈{soiRimata / 1000.0:F0} km，起点推到 1.15×SOI 外" +
                $"｜G{group.GroupId} 已铺在恒星帧 r={r1Out.magnitude / 1e9:F3}e9 m" +
                $"｜出发 tick={now:F0}，预计到达 tick={now + tof:F0}。" +
                "开时间加速观察「进赭砂 SOI → 自动换系」。");
        }

        /// <summary>快速 SOI 入射：铺在赭砂 SOI 外 1.1R，相对赭砂 10 km/s 向内，秒级进 SOI 触发换系。</summary>
        public static void SpawnZheShaSoiEntry()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null) return;

            int zheSha = FindBodyId(mgr, "ZheSha");
            var def = mgr.GetBodyById(zheSha);
            var star = mgr.GetStar();
            if (def == null || !def.HasOrbit || star == null)
            {
                Log.Error($"[SOI入射演示] 天体解析失败：赭砂(id={zheSha})");
                return;
            }

            double now = TimeScaleManager.GetPhysicsTickSafe();
            (DVector3 pos, DVector3 vel) = Circular(def, now);
            double soi = OrbitAdapter.SoiRadiusApprox(def, mgr);
            if (!(soi > 0))
            {
                Log.Error("[SOI入射演示] 赭砂 SOI 半径无效");
                return;
            }

            DVector3 dir = pos.normalized;                 // 太阳 → 赭砂 径向
            DVector3 shipPos = pos + dir * (1.1 * soi);    // SOI 外 0.1R
            DVector3 shipVel = vel - dir * 10000.0;        // 相对赭砂 10 km/s 向内
            double eta = 0.1 * soi / 10000.0;

            var group = DebugShipSpawner.CreateNoMapShipGroup(spaceGame, star.bodyId,
                shipPos, shipVel, out int _, overThrust: true, circularOrbit: false);
            if (group == null)
            {
                Log.Error("[SOI入射演示] 铺船失败");
                return;
            }
            group.CelestialBodyId = star.bodyId;

            Log.Message($"[SOI入射演示] 赭砂 SOI≈{soi / 1000.0:F0} km｜G{group.GroupId} 铺在 1.1×SOI" +
                $"（距赭砂 {1.1 * soi / 1000.0:F0} km）｜相对速度 10 km/s 向内" +
                $"｜预计 {eta:F0} 物理秒后进 SOI（加速后观察 [换系] 日志）");
        }

        /// <summary>
        /// 关键点探针：在 Rimata / 赭砂 周围撒 7 个组，逐个打印
        /// 帧根、|posBody|、源表成员、盒外 gap（验证“盒内恒星=0 / 盒外近旁带”）。
        /// A Rimata 低轨 · B Rimata 盒外近旁 · C Rimata 盒外远处 · D 日心中点 ·
        /// E 赭砂盒外近旁 · F 赭砂低轨 · G 赭砂表面 10 km（应触发撞击结算）。
        /// </summary>
        public static void SpawnKeyPointProbes()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null) return;

            int rimataId = FindBodyId(mgr, "Earth");
            int zheShaId = FindBodyId(mgr, "ZheSha");
            var rimata = mgr.GetBodyById(rimataId);
            var zheSha = mgr.GetBodyById(zheShaId);
            var star = mgr.GetStar();
            if (rimata == null || zheSha == null || star == null) { Log.Error("[关键点] 天体缺失"); return; }

            double now = TimeScaleManager.GetPhysicsTickSafe();
            (DVector3 rE, DVector3 vE) = Circular(rimata, now);
            (DVector3 rZ, DVector3 vZ) = Circular(zheSha, now);
            double soiE = OrbitAdapter.SoiRadiusApprox(rimata, mgr);
            double soiZ = OrbitAdapter.SoiRadiusApprox(zheSha, mgr);
            double rELeo = rimata.radius + 500_000.0;
            double rZLeo = zheSha.radius + 200_000.0;
            var sources = new List<GravitySource>();

            void Place(string tag, int bodyId, DVector3 pos, DVector3 vel, bool circularOrbit)
            {
                var g = DebugShipSpawner.CreateNoMapShipGroup(spaceGame, bodyId, pos, vel, out int _,
                    overThrust: true, circularOrbit: circularOrbit);
                if (g == null) { Log.Error($"[关键点] {tag} 铺船失败"); return; }
                sources.Clear();
                mgr.CollectSystemSources(bodyId, now, sources);
                int frameRoot = mgr.GetReferenceFrameId(bodyId);
                double posBody = g.GetBodyPosition(now).v.magnitude;

                string gapText = "";
                if (mgr.GetBodyById(bodyId)?.bodyType == CelestialBodyType.Star)
                {
                    DVector3 relE = mgr.GetBodyPositionParent(rimataId, now);
                    DVector3 dE = pos - relE;
                    double gapE = Math.Max(Math.Abs(dE.x), Math.Max(Math.Abs(dE.y), Math.Abs(dE.z))) - soiE;
                    DVector3 relZ = mgr.GetBodyPositionParent(zheShaId, now);
                    DVector3 dZ = pos - relZ;
                    double gapZ = Math.Max(Math.Abs(dZ.x), Math.Max(Math.Abs(dZ.y), Math.Abs(dZ.z))) - soiZ;
                    gapText = $" gapE={gapE / 1000.0:F0}km(带 {soiE * 0.5 / 1000.0:F0}km) gapZ={gapZ / 1000.0:F0}km";
                }
                Log.Message($"[关键点] {tag} G{g.GroupId} body={bodyId} frame={frameRoot}｜|posBody|={posBody:E3} m" +
                    $"｜|posFrame|={g.CurrentOrbit.positionSystem.v.magnitude:E3} m｜源=[{string.Join(",", sources.Select(s => s.bodyId))}]" +
                    gapText);
            }

            Place("A Rimata低轨", rimataId, new DVector3(rELeo, 0, 0),
                new DVector3(0, 0, -Math.Sqrt(rimata.gravitationalParameter / rELeo)), circularOrbit: true);
            Place("B Rimata近旁", star.bodyId, rE * (1.0 + 1.2 * soiE / rE.magnitude), vE, circularOrbit: true);
            Place("C Rimata远处", star.bodyId, rE * (1.0 + 3.0 * soiE / rE.magnitude), vE, circularOrbit: true);
            Place("D 日心中点", star.bodyId, (rE + rZ) * 0.5, vE, circularOrbit: true);
            Place("E 赭砂近旁", star.bodyId, rZ * (1.0 + 1.2 * soiZ / rZ.magnitude), vZ, circularOrbit: true);
            Place("F 赭砂低轨", zheShaId, new DVector3(rZLeo, 0, 0),
                new DVector3(0, 0, -Math.Sqrt(zheSha.gravitationalParameter / rZLeo)), circularOrbit: true);
            Place("G 赭砂表面10km", zheShaId, new DVector3(zheSha.radius + 10_000.0, 0, 0), DVector3.zero, circularOrbit: false);
        }

        /// <summary>纯解析圆轨（与 CelestialBodyManager.GetBodyPositionParentCore 同公式）。</summary>
        private static (DVector3 pos, DVector3 vel) Circular(CelestialBodyInfo body, double tick)
        {
            double w = 2.0 * Math.PI / body.orbitPeriod;
            double phase = body.initialPhase + w * tick;
            body.GetOrbitFrame(out var refX, out var refY);
            double c = Math.Cos(phase), s = Math.Sin(phase);
            return (body.orbitRadius * (c * refX + s * refY),
                    (body.orbitRadius * w) * (-s * refX + c * refY));
        }

        private static int FindBodyId(CelestialBodyManager mgr, string defName)
        {
            foreach (var body in mgr.GetAllBodies())
                if (string.Equals(body.defName, defName, StringComparison.Ordinal)) return body.bodyId;
            return -1;
        }
    }
}
