using System;
using System.Collections.Generic;
using System.Text;
using PioneerFramework.Launch;   // GetSurfaceState（layer↔天体、tile↔固连位置、星下点反查）
using RimWorld;                  // PlanetTile
using RimWorld.Planet;           // PlanetLayer（⚠ 与 PlanetTile 不同命名空间 —— 全库先例见 GetSurfaceState.cs:2）
using UnityEngine;               // Vector3（tile 中心点积）
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **选 tile 主动降落 + 撞击事件的机器判据**（S3，2026-09-23）——把原先"只有部署、人读日志"的两个
    /// 场景接进统一判定框架（<see cref="PflTestKit"/>）。
    ///
    /// ## 判据取自哪（别在这里另立标准）
    /// · **Δv 量级区间**来自 `LandingQuote` 类注释里登记的物理口径（近地圆轨 ≈5.5 km/s、绕月 ≈1.17 km/s），
    ///   区间留 ±10% —— 判"是不是那个量级"，不判小数位（精确值由纯核 `LandingQuoteTests` 钉）；
    /// · **距离门**直接与纯核 `LandingQuote.MaxSurfaceDistance` 对同一个 R 比 ⇒ 这一条查的是**接线**
    ///   （`TryQuoteLanding` 有没有把天体半径传对），不是重算一遍纯核；
    /// · **着陆/坠毁分界**由 `SpaceConstants.IsSafeLandingSpeed`（唯一权威）负责，本文件只断言**事件真的发生了**。
    ///
    /// ## 覆盖到哪 / 没覆盖到哪
    /// · ✅ 报价（地月星下点）· 距离门拒（对跖点）· 跨层错配拒 + 同层对照可落 · tile 越界拒 ·
    ///   缺表面拒（找不到无地表天体 ⇒ **UNCHECKED**，不折算成通过）· 执行三件事（消息 + 扣料 + 销毁）；
    /// · ✅ 撞击事件（异步：两颗探针落地后断言"着陆/坠毁各一次且经纬度不同"）；
    /// · ⚠ **未覆盖**：UI 的选点交互（鼠标拾取/窗口）——那要人在屏幕前点，归 S3 的手动清单。
    ///
    /// ## 「拒绝的原因对不对」怎么判（**不匹配中文文案**）
    /// 每个拒绝用例都配一个**对照组**：同一颗船、同一个 tile 索引，只换那一个"应当决定结果"的变量
    /// （层 / tile 索引 / 天体）⇒ 被拒的那次如果换个变量就能落，那它就是**因为那个变量**被拒的。
    /// 这比断言"reason 里含『越界』"结实：文案会改，变量关系不会；文案仍打进判据明细供人读。
    /// </summary>
    public static class LandingAcceptanceRunner
    {
        private const int EARTH_BODY_ID = 1;
        private const int MOON_BODY_ID = 2;
        private const string Category = "00 验收";

        /// <summary>撞击事件的等待上限（真实 tick；正常速度下两颗几十 tick 内就落地）。</summary>
        private const int ImpactTimeoutTicks = 900;

        /// <summary>撞击事件用例名 —— **只能有一份字面量**（`StageImpact` 开它、`Tick` 认它、`Abort` 收它；
        /// 名字串了就是"判据写进别人用例"的那种静默串味）。</summary>
        private const string ImpactCaseName = "撞击事件（着陆/坠毁分界）";

        // ── 异步段状态（撞击事件）──
        private static bool impactPending;
        private static int impactStartTick;
        private static int impactLogMark;
        private static readonly List<int> impactGroups = new List<int>();

        // ══════════════════════════════════════════════════════════════════════════
        //  同步段：报价 / 各种拒绝 / 执行
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>同步跑完报价与执行判据（**几毫秒、当下出结论**）。</summary>
        public static void RunSync()
        {
            if (Current.Game == null)
            {
                PflTestKit.Skip("降落报价/执行（地月）", "没有游戏（要在一盘存档里跑）");
                return;
            }

            PflTestKit.SnapshotGroups();
            if (!DebugShipSpawner.TrySpawnLandingProbeShips(out var sg, out var mgr,
                    out double tick, out var eShip, out var mShip)
                || sg == null || mgr == null || eShip == null || mShip == null)
            {
                PflTestKit.Skip("降落报价/执行（地月）",
                    "探针前置不满足（无 SpaceGameComp / 天体 Def 或工质 Def 缺失）⇒ 场景没搭起来");
                return;
            }

            // 量级区间 = LandingQuote 注释里的登记口径 ±10%
            QuoteCase(sg, mgr, eShip, EARTH_BODY_ID, tick, "近地圆轨", 5000.0, 6100.0, DebugShipSpawner.LastEarthProbeTile);
            QuoteCase(sg, mgr, mShip, MOON_BODY_ID, tick, "绕月圆轨", 1050.0, 1300.0, DebugShipSpawner.LastMoonProbeTile);
            GateCase(sg, mgr, eShip, EARTH_BODY_ID, tick, "地球");
            GateCase(sg, mgr, mShip, MOON_BODY_ID, tick, "月球");
            WrongLayerCase(sg, mgr, eShip, EARTH_BODY_ID, tick, DebugShipSpawner.LastEarthProbeTile);
            OutOfRangeCase(sg, mgr, eShip, EARTH_BODY_ID, tick, DebugShipSpawner.LastEarthProbeTile);
            NoSurfaceCase(sg, mgr, eShip, EARTH_BODY_ID, tick, DebugShipSpawner.LastEarthProbeTile);
            SubSatelliteCase(sg, mgr, mShip, MOON_BODY_ID, tick, "月球");   // 反查那条链（网格诊断也在这里打）
            SubSatelliteCase(sg, mgr, eShip, EARTH_BODY_ID, tick, "地球");
            ExecuteCase(sg, mgr, eShip, EARTH_BODY_ID, tick, DebugShipSpawner.LastEarthProbeTile);   // 会销毁编队 ⇒ 放最后

            int cleared = PflTestKit.CleanupSince();
            if (cleared > 0) PflTestKit.Note(string.Format("本段清场销毁了 {0} 个探针编队", cleared));
        }

        private static void QuoteCase(SpaceGameComp sg, CelestialBodyManager mgr, SpaceEntityGroup g,
            int bodyId, double tick, string tag, double lo, double hi, int shipTile)
        {
            PflTestKit.Begin("降落报价·" + tag + "（船正对的 tile）", Category);
            var layer = GetSurfaceState.GetLayerForBody(mgr, bodyId);
            var body = mgr.GetBodyById(bodyId);
            if (layer == null || body == null)
            {
                PflTestKit.Unchecked("前置", string.Format("天体 {0} 缺地表网格或 Def", bodyId));
                return;
            }

            int tile = shipTile;   // 摆场时把船摆在它正上方 ⇒ 这个 tile 由构造保证"船正对"
            bool ok = sg.TryQuoteLanding(g, bodyId, new PlanetTile(tile, layer),
                out var q, out var est, out var bodyName, out var spot, out var reason);

            PflTestKit.Check(PflTestKit.Kind.Value, ok, "船正对的 tile 必须可落（探针工质充裕）",
                string.Format("tile={0}｜{1}", tile, reason));
            if (!ok) { PflTestKit.End(); return; }

            PflTestKit.Check(PflTestKit.Kind.Value, q.deltaV >= lo && q.deltaV <= hi,
                string.Format("Δv 落进登记区间 [{0:F0}, {1:F0}] m/s（量级判据）", lo, hi),
                string.Format("Δv={0:F1} m/s｜{1}｜工质 {2:F1} kg｜正对 tile={3}",
                    q.deltaV, spot, est.RequiredPropellant, tile));
            PflTestKit.Check(PflTestKit.Kind.Value, q.surfaceDistance <= 1000.0,
                "地表距离 ≈ 0（船就摆在这个 tile 正上方；1 km 的宽容是给『径向偏移不影响角度』留的）",
                string.Format("{0:F2} m", q.surfaceDistance));
            PflTestKit.Check(PflTestKit.Kind.Invariant,
                Math.Abs(q.maxDistance - LandingQuote.MaxSurfaceDistance(body.radius)) < 1e-6,
                "距离门与纯核同值（查接线有没有传错天体半径）",
                string.Format("报价门 {0:F0} km／纯核 {1:F0} km（R={2:F0} km）",
                    q.maxDistance / 1000.0, LandingQuote.MaxSurfaceDistance(body.radius) / 1000.0,
                    body.radius / 1000.0));
            PflTestKit.End();
        }

        /// <summary>对跖点（离星下点最远的 tile）必须被**距离门**拒掉 —— 且拒的原因确实是距离。</summary>
        private static void GateCase(SpaceGameComp sg, CelestialBodyManager mgr, SpaceEntityGroup g,
            int bodyId, double tick, string tag)
        {
            PflTestKit.Begin("降落距离门·" + tag + "（对跖点必拒）", Category);
            var layer = GetSurfaceState.GetLayerForBody(mgr, bodyId);
            var body = mgr.GetBodyById(bodyId);
            if (layer == null || body == null)
            {
                PflTestKit.Unchecked("前置", string.Format("天体 {0} 缺地表网格或 Def", bodyId));
                return;
            }

            int tile = AntipodeTile(layer, mgr, bodyId, BodyRelativePos(sg, g, bodyId, tick), tick, out float bestDot);
            bool ok = sg.TryQuoteLanding(g, bodyId, new PlanetTile(tile, layer),
                out var q, out _, out _, out _, out var reason);

            // 读数（**不**在这里判）：真对跖点应有 dot ≈ −1。网格偏心时它会明显偏大 ——
            // 那属于"网格↔方向"这一环的问题，由「星下点反查」用例统一判定，不在这里重复计红。
            PflTestKit.Note(string.Format("对跖点方向点积 {0:F4}（应 ≈ −1）｜tile={1}", bestDot, tile));
            PflTestKit.Check(PflTestKit.Kind.Contract, !ok,
                "离星下点最远的 tile 必须被拒（距离门）", ok ? "**却通过了**" : reason);
            if (!ok)
            {
                // 拒的**原因必须是距离门**（不是"没工质/天体没地表"这类别的拒绝 ⇒ 否则这条判据会假绿）
                PflTestKit.Check(PflTestKit.Kind.Value, q.surfaceDistance > q.maxDistance,
                    "拒因 = 地表距离超过门（不是别的分支）",
                    string.Format("地表 {0:F0} km > 门 {1:F0} km（tile={2}）",
                        q.surfaceDistance / 1000.0, q.maxDistance / 1000.0, tile));
            }
            PflTestKit.End();
        }

        /// <summary>跨层错配：tile 索引在**另一个天体**的 layer 上"看起来合法" ⇒ 必须被拒；
        /// 同一个 tile 用**正确的** layer 必须可落（对照组 —— 否则"被拒"可能只是因为这个 tile 本来就落不了）。</summary>
        private static void WrongLayerCase(SpaceGameComp sg, CelestialBodyManager mgr, SpaceEntityGroup g,
            int bodyId, double tick, int shipTile)
        {
            PflTestKit.Begin("降落拒绝·跨层错配（静默落到别处的那条路）", Category);
            var earthLayer = GetSurfaceState.GetLayerForBody(mgr, bodyId);
            var moonLayer = GetSurfaceState.GetLayerForBody(mgr, MOON_BODY_ID);
            if (earthLayer == null || moonLayer == null)
            {
                PflTestKit.Unchecked("前置", "地球或月球缺地表网格");
                return;
            }

            int tile = shipTile;
            bool mismatched = sg.TryQuoteLanding(g, bodyId, new PlanetTile(tile, moonLayer),
                out _, out _, out _, out _, out var reason);
            bool control = sg.TryQuoteLanding(g, bodyId, new PlanetTile(tile, earthLayer),
                out _, out _, out _, out _, out _);

            PflTestKit.Check(PflTestKit.Kind.Contract, !mismatched,
                "月球层上的 tile 在地球上必须被拒（否则落点会静默落到月球）", reason);
            PflTestKit.Check(PflTestKit.Kind.Value, control,
                "**对照**：同一个 tile 配正确的层必须可落",
                string.Format("tile={0}｜错配被拒={1}｜同层可落={2}", tile, !mismatched, control));
            PflTestKit.End();
        }

        /// <summary>tile 索引越界与"未选点"两条都必须被拒。</summary>
        private static void OutOfRangeCase(SpaceGameComp sg, CelestialBodyManager mgr, SpaceEntityGroup g,
            int bodyId, double tick, int shipTile)
        {
            PflTestKit.Begin("降落拒绝·tile 越界 / 未选点", Category);
            var layer = GetSurfaceState.GetLayerForBody(mgr, bodyId);
            if (layer == null)
            {
                PflTestKit.Unchecked("前置", "天体缺地表网格");
                return;
            }

            int valid = shipTile;
            bool over = sg.TryQuoteLanding(g, bodyId, new PlanetTile(layer.TilesCount, layer),
                out _, out _, out _, out _, out var overReason);
            bool under = sg.TryQuoteLanding(g, bodyId, new PlanetTile(-1, layer),
                out _, out _, out _, out _, out var underReason);
            bool control = sg.TryQuoteLanding(g, bodyId, new PlanetTile(valid, layer),
                out _, out _, out _, out _, out _);

            PflTestKit.Check(PflTestKit.Kind.Contract, !over,
                string.Format("tile = TilesCount（{0}）必须被拒", layer.TilesCount), overReason);
            PflTestKit.Check(PflTestKit.Kind.Contract, !under, "tile = -1（未选点）必须被拒", underReason);
            PflTestKit.Check(PflTestKit.Kind.Value, control, "**对照**：合法 tile 仍可落",
                string.Format("对照 tile={0}", valid));
            PflTestKit.End();
        }

        /// <summary>无固体表面的天体必须被拒（本存档若真的没有这种天体 ⇒ **UNCHECKED**，不折算成通过）。
        /// ⚠ 用 `GetAllBodies`（天体全集），**不是** `GetPlanets()`：首跑用的是后者，它恒为空
        /// （过滤器要求 `ParentBodyId == -1`，而地球的父天体是太阳）⇒ 用例假报"本存档 0 个天体都有地表"。
        /// 枚举面选错同样是仪器错 —— 太阳（`hasSurface=false`）本来就在那儿。</summary>
        private static void NoSurfaceCase(SpaceGameComp sg, CelestialBodyManager mgr, SpaceEntityGroup g,
            int bodyId, double tick, int shipTile)
        {
            PflTestKit.Begin("降落拒绝·目标天体无固体表面", Category);
            int target = -1;
            string targetName = "?";
            int withSurface = 0;
            var bodies = mgr.GetAllBodies();
            if (bodies != null)
            {
                for (int i = 0; i < bodies.Count; i++)
                {
                    if (bodies[i] == null) continue;
                    if (!bodies[i].hasSurface) { target = bodies[i].bodyId; targetName = bodies[i].label; break; }
                    withSurface++;
                }
            }
            if (target < 0)
            {
                PflTestKit.Unchecked("缺表面天体",
                    string.Format("本存档枚举到 {0} 个天体、其中 {1} 个有地表、**0 个无地表** ⇒ 这条分支没有被走到过（不是通过）",
                        bodies != null ? bodies.Count : 0, withSurface));
                return;
            }

            int tile = shipTile;
            bool ok = sg.TryQuoteLanding(g, target, new PlanetTile(tile), out _, out _, out var bodyName,
                out _, out var reason);

            PflTestKit.Check(PflTestKit.Kind.Contract, !ok,
                string.Format("无地表的 `{0}`（bodyId={1}）必须被拒", targetName, target), reason);
            PflTestKit.Check(PflTestKit.Kind.Coverage, !string.IsNullOrEmpty(bodyName),
                "拒绝信息里带得出天体名（不是空串）", bodyName);
            PflTestKit.End();
        }

        /// <summary>
        /// **星下点反查的自洽性**（`EciToTile` ↔ 报价距离口径）。
        ///
        /// 为什么单列一条：这两者**数学上必然一致** —— 报价的距离是
        /// `acos(dot(û_ship, M⁻¹·û_tile))` = `acos(dot(M·û_ship, û_tile))`，而 `EciToTile` 取的是
        /// `argmax dot(c_i, M·û_ship)` ⇒ 它选出的 tile 就是**离星下点最近的那个** ⇒ 距离必然 ≈ 0。
        /// 所以这条一红，就说明"网格顶点 ↔ 方向"这一环有问题（`GetTileCenter` 的假设），
        /// **与降落功能本身无关** —— 2026-09-24 首跑就是这个现象（读到的距离 34°，见网格诊断读数）。
        /// </summary>
        private static void SubSatelliteCase(SpaceGameComp sg, CelestialBodyManager mgr, SpaceEntityGroup g,
            int bodyId, double tick, string tag)
        {
            PflTestKit.Begin("星下点反查·" + tag + "（EciToTile 与报价口径必须一致）", Category);
            var layer = GetSurfaceState.GetLayerForBody(mgr, bodyId);
            if (layer == null)
            {
                PflTestKit.Unchecked("前置", "天体缺地表网格");
                return;
            }

            DVector3 rel = BodyRelativePos(sg, g, bodyId, tick);
            int tile = GetSurfaceState.EciToTile(rel, mgr, bodyId, tick);
            string diag = MeshDiag(layer);
            if (tile < 0)
            {
                PflTestKit.Unchecked("反查返回 -1", "位置退化（|p| ≈ 0）｜" + diag);
                return;
            }

            bool ok = sg.TryQuoteLanding(g, bodyId, new PlanetTile(tile, layer),
                out var q, out _, out var bodyName, out var spot, out var reason);
            PflTestKit.Check(PflTestKit.Kind.Value, ok,
                "反查出来的 tile 应当**可落**（它理应是离星下点最近的格）",
                string.Format("tile={0}｜{1}｜{2}", tile, reason, diag));
            if (ok)
            {
                // 半个 tile 的角距在星体尺度上只有几 km；给 200 km 是"即使很宽松也必须 ≈0"的量级门。
                PflTestKit.Check(PflTestKit.Kind.Invariant, q.surfaceDistance <= 200_000.0,
                    "星下点反查的地表距离必须 ≈ 0（数学上必然 ⇒ 不成立即『网格↔方向』这一环有问题）",
                    string.Format("地表 {0:F0} km｜tile={1}｜{2}｜{3}", q.surfaceDistance / 1000.0, tile, spot, diag));
            }
            PflTestKit.End();
        }

        /// <summary>**地表网格诊断读数**（无条件打印）：网格是否"以原点为心的球"。
        /// `|Σ û| / n` 接近 0 = 方向遍布全球（球心在原点 ✓）；接近 1 = 所有 tile 方向挤在一起（网格偏心 ✗）。</summary>
        private static string MeshDiag(PlanetLayer layer)
        {
            int n = layer.TilesCount;
            if (n <= 0) return "网格为空";
            double minR = double.MaxValue, maxR = 0.0, sumR = 0.0;
            Vector3 sumDir = Vector3.zero;
            Vector3 u0 = layer.GetTileCenter(0).normalized;
            float minDot = float.MaxValue, maxDot = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                Vector3 c = layer.GetTileCenter(i);
                float r = c.magnitude;
                if (r < minR) minR = r;
                if (r > maxR) maxR = r;
                sumR += r;
                Vector3 u = c.normalized;
                sumDir += u;
                float d = Vector3.Dot(u, u0);
                if (d < minDot) minDot = d;
                if (d > maxDot) maxDot = d;
            }
            // 判性质用的补充读数（2026-09-24）：帽状分布 vs "网格里大量 tile 退化成同一个方向" 是两件事，
            // 用"有多少 tile 挤在 û₀ 附近" + "游戏自己的纬度覆盖多宽"就能分开。
            int near0 = 0;
            float latMin = 90f, latMax = -90f;
            for (int i = 0; i < n; i++)
            {
                Vector3 c = layer.GetTileCenter(i);
                if (Vector3.Dot(c.normalized, u0) > 0.9998f) near0++;      // 与 û₀ 夹角 < ~1°
                float lat = layer.LongLatOf(i).y;                          // 游戏自己的纬度（Asin(c.y/Radius)）
                if (lat < latMin) latMin = lat;
                if (lat > latMax) latMax = lat;
            }
            Vector3 axis = sumDir.normalized;
            return string.Format("网格诊断：n={0}｜|c| {1:F3}..{2:F3}（均 {3:F3}／layer.Radius={4:F3}）" +
                "｜|Σû|/n={5:F4}（≈0=球心在原点）｜dot(û,û₀) {6:F3}..{7:F3}｜近 û₀ 的 tile≈{8}" +
                "｜游戏纬度覆盖 {9:F1}°..{10:F1}°｜帽轴 û=( {11:F3}, {12:F3}, {13:F3} )",
                n, minR, maxR, sumR / n, layer.Radius, sumDir.magnitude / n, minDot, maxDot, near0,
                latMin, latMax, axis.x, axis.y, axis.z);
        }

        /// <summary>执行三件事：**消息 + 扣料 + 销毁**（用户 2026-09-22 的原话就这三件）。</summary>
        private static void ExecuteCase(SpaceGameComp sg, CelestialBodyManager mgr, SpaceEntityGroup g,
            int bodyId, double tick, int shipTile)
        {
            PflTestKit.Begin("降落执行（消息 + 扣料 + 销毁）", Category);
            var layer = GetSurfaceState.GetLayerForBody(mgr, bodyId);
            if (layer == null)
            {
                PflTestKit.Unchecked("前置", "天体缺地表网格");
                return;
            }

            int tile = shipTile;
            var target = new PlanetTile(tile, layer);
            int groupId = g.GroupId;
            int tickMark = Find.TickManager?.TicksGame ?? 0;

            bool quoted = sg.TryQuoteLanding(g, bodyId, target, out var q, out var est,
                out var bodyName, out var spot, out var quoteReason);
            if (!quoted)
            {
                PflTestKit.Check(PflTestKit.Kind.Value, false, "执行前必须能报出价", quoteReason);
                PflTestKit.End();
                return;
            }

            bool ok = sg.TryLandAtTile(g, bodyId, target, out var landReason);
            bool gone = sg.GetGroupById(groupId) == null;

            PflTestKit.Check(PflTestKit.Kind.Value, ok, "降落指令被接受", landReason);
            PflTestKit.Check(PflTestKit.Kind.Invariant, gone,
                "① 编队已销毁（落点不再留在场上）",
                string.Format("G{0} {1}", groupId, gone ? "已销毁" : "**仍在场上**"));
            PflTestKit.Check(PflTestKit.Kind.Value, sg.lastLandingConsumedKg > 0.0,
                "② 工质真的扣了（> 0 kg）",
                string.Format("实扣 {0:F1} kg／报价预测 {1:F1} kg", sg.lastLandingConsumedKg, est.RequiredPropellant));
            PflTestKit.Check(PflTestKit.Kind.Invariant,
                sg.lastLandingConsumedKg <= est.RequiredPropellant * 1.0001,
                "② 实扣不许超过火箭方程的需求（截断只能更少）",
                string.Format("实扣 {0:F1} ≤ 需 {1:F1}", sg.lastLandingConsumedKg, est.RequiredPropellant));
            PflTestKit.Check(PflTestKit.Kind.Invariant,
                Math.Abs(sg.lastLandingQuotedDeltaV - q.deltaV) <= Math.Max(1e-6, q.deltaV * 1e-6),
                "② 读数记录的价 = 本次报价（同一趟）",
                string.Format("记 {0:F3} ／ 报 {1:F3} m/s", sg.lastLandingQuotedDeltaV, q.deltaV));

            // ③ 消息（玩家可见那条）：落在**归档**里（`Messages.Message(..., historical: true)` 的收口），
            //    不是"我们打了日志所以玩家看到了"。
            string? archived = FindArchivedMessage(tickMark, "着陆于" + bodyName);
            PflTestKit.Check(PflTestKit.Kind.Value, archived != null,
                "③ 玩家可见消息出现（归档里能查到）",
                archived ?? string.Format("归档里没有『着陆于{0}』（tick ≥ {1}）", bodyName, tickMark));
            if (archived != null)
                PflTestKit.Note(string.Format("消息原文：{0}｜报价 {1:F1} m/s（{2}）", archived, q.deltaV, spot));
            PflTestKit.End();
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  异步段：撞击事件（着陆 / 坠毁分界）
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>摆两颗探针（20 m/s ⇒ 着陆、300 m/s ⇒ 坠毁）并登记一个带超时的异步用例。</summary>
        public static void StageImpact()
        {
            if (Current.Game == null || impactPending)
            {
                PflTestKit.Skip(ImpactCaseName, impactPending ? "已在等待中" : "没有游戏");
                return;
            }

            DebugShipSpawner.SpawnImpactEventTest();
            impactGroups.Clear();
            impactGroups.AddRange(DebugShipSpawner.LastImpactProbeGroups);
            if (impactGroups.Count != 2)
            {
                PflTestKit.Begin(ImpactCaseName, Category);
                PflTestKit.Unchecked("探针未摆出",
                    string.Format("只拿到 {0} 颗（天体 Def 缺失？）", impactGroups.Count));
                impactGroups.Clear();
                return;
            }

            impactStartTick = Find.TickManager?.TicksGame ?? 0;
            impactLogMark = LogMessageCount();
            impactPending = true;
            // 超时由本类自己判（消息更具体）；给框架一个更宽的后备，防"本类的 Tick 没被驱动"时卡住登记表。
            PflTestKit.Begin(ImpactCaseName, Category, ImpactTimeoutTicks + 120);
            PflTestKit.Check(PflTestKit.Kind.Coverage, true, "两颗探针已摆出（赤道 20 m/s / 北纬30° 300 m/s）",
                string.Format("G{0}（预期着陆）、G{1}（预期坠毁）", impactGroups[0], impactGroups[1]));
        }

        /// <summary>由 `SpaceGameComp.GameComponentTick` 每真实 tick 调用（空闲时第一行就返回）。</summary>
        public static void Tick()
        {
            if (!impactPending) return;

            // ⚠ **登记表只有一个槽位**：现在开着的用例如果已经不是我们这条（有人在我们等落地的时候又
            //   `Begin` 了别的用例），写进去就是把判据串到别人身上、还把它提前结算 ⇒ 这里必须放弃并说清。
            if (!PflTestKit.IsCurrent(ImpactCaseName))
            {
                impactPending = false;
                Log.Warning("[降落/撞击判据] 撞击事件用例已被顶掉（本轮不再结算它；异步段要最后登记）");
                return;
            }

            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null)
            {
                impactPending = false;
                PflTestKit.UncoveredCase(ImpactCaseName, "SpaceGameComp 消失");
                return;
            }

            bool aGone = sg.GetGroupById(impactGroups[0]) == null;
            bool bGone = sg.GetGroupById(impactGroups[1]) == null;
            int now = Find.TickManager?.TicksGame ?? 0;
            bool timedOut = now - impactStartTick > ImpactTimeoutTicks;
            if (!((aGone && bGone) || timedOut)) return;

            impactPending = false;
            if (!(aGone && bGone))
            {
                // 超时：先清掉还活着的探针（**只动自己摆的两颗**，不用快照清场 —— 玩家可能同时在自己摆船）
                for (int i = 0; i < impactGroups.Count; i++)
                    if (sg.GetGroupById(impactGroups[i]) != null) sg.DestroyGroup(impactGroups[i]);
                PflTestKit.UncoveredCase(ImpactCaseName,
                    string.Format("{0} tick 内没等到撞击（G{1} 在场上={2}、G{3} 在场上={4}）",
                        ImpactTimeoutTicks, impactGroups[0], !aGone, impactGroups[1], !bGone));
                return;
            }

            string? landLog = FindLogLine(impactLogMark, "] Group " + impactGroups[0] + " LANDED");
            string? crashLog = FindLogLine(impactLogMark, "] Group " + impactGroups[1] + " CRASHED");
            string? landMsg = FindArchivedMessage(impactStartTick, "着陆于");
            string? crashMsg = FindArchivedMessage(impactStartTick, "坠毁于");

            PflTestKit.Check(PflTestKit.Kind.Invariant, aGone && bGone,
                "两颗都被撞击事件收走（不是留在场上）",
                string.Format("G{0} {1}｜G{2} {3}", impactGroups[0], aGone ? "已销毁" : "仍在",
                    impactGroups[1], bGone ? "已销毁" : "仍在"));
            PflTestKit.Check(PflTestKit.Kind.Value, landLog != null,
                "20 m/s 那颗走**着陆**分支（接地速度 ≤ 100）", landLog ?? "日志里没有 LANDED 行");
            PflTestKit.Check(PflTestKit.Kind.Value, crashLog != null,
                "300 m/s 那颗走**坠毁**分支（接地速度 > 100）", crashLog ?? "日志里没有 CRASHED 行");
            PflTestKit.Check(PflTestKit.Kind.Value, landMsg != null && crashMsg != null,
                "两条玩家可见消息都在（着陆 + 坠毁）",
                string.Format("着陆『{0}』｜坠毁『{1}』", landMsg ?? "无", crashMsg ?? "无"));
            // ⚠ 这里的比对对象必须是**落点经纬度**那一段，不是整行 —— 整行必然不同（组 id 就不同），
            //   那样的判据恒真、名不副实（自查抓到的第一版就是这个错）。
            string landSpot = SpotOf(landLog);
            string crashSpot = SpotOf(crashLog);
            PflTestKit.Check(PflTestKit.Kind.Value,
                landSpot != "?" && crashSpot != "?" && !string.Equals(landSpot, crashSpot, StringComparison.Ordinal),
                "两个落点的经纬度不同（防『经纬度恒为 0,0』或两句互换）",
                string.Format("着陆落点『{0}』／坠毁落点『{1}』", landSpot, crashSpot));
            PflTestKit.End();
        }

        /// <summary>中止（供调试页签的"中止"用）。</summary>
        public static void Abort()
        {
            if (!impactPending) return;
            impactPending = false;
            PflTestKit.UncoveredCase(ImpactCaseName, "被中止");
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  工具
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 探针船的**天体相对**位置（= `EciToTile` / `AntipodeTile` 要的口径）。
        ///
        /// ⚠ **不许用 `group.localPosition`**（2026-09-24 首跑实测抓到）：那是**每物理步**算出来的缓存，
        /// 而本 runner 在**摆船的那一拍**就检查 ⇒ 它还是零向量 ⇒ `EciToTile(0) = -1` ⇒ 一整套用例
        /// 红成"尚未选中落点"（不是产品问题，是我的取数时机错了）。
        /// 改用 `CurrentOrbit.position`（摆好即有）+ `GetBodyWorldOffset` —— 与 `TryQuoteLanding`
        /// **同一个约定**（天体侧对根行星返零）⇒ 这不是第二份换系，是同一个换算。
        /// </summary>
        private static DVector3 BodyRelativePos(SpaceGameComp sg, SpaceEntityGroup g, int bodyId, double tick)
        {
            int tickI = (int)Math.Round(tick);
            return g.CurrentOrbit.position - sg.GetBodyWorldOffset(bodyId, tickI);
        }

        /// <summary>离星下点**最远**的 tile（与 `EciToTile` 同法：只把 argmax 换成 argmin）。
        /// `bestDot` 出参 = 那个 tile 与星下点方向的点积（真对跖点应 ≈ **−1**）——调用方**必须**拿它做健全性检查：
        /// 第一版没有它，于是"星下点位置是零向量"时 argmin 退化成 tile 0，而 tile 0 照样"离得远"、
        /// 照样被距离门拒 ⇒ 那条判据会**假绿**（首跑实测：它确实绿了，是假绿）。</summary>
        private static int AntipodeTile(PlanetLayer layer, CelestialBodyManager mgr, int bodyId,
            DVector3 relPosLocal, double tick, out float bestDot)
        {
            double omega = GetSurfaceState.RotationOmega(mgr, bodyId);
            DVector3 pFixed = SurfaceFrame.EciToFixed(relPosLocal, omega, tick);
            Vector3 dir = ((Vector3)pFixed).normalized;

            int best = 0;
            bestDot = float.MaxValue;
            for (int i = 0; i < layer.TilesCount; i++)
            {
                // ⚠ 必须**归一化**再比：网格顶点不是单位向量（实测地球 |c|≈79、月球 ≈1.19 —— 两个图层尺度不同），
                //   拿原始点积比就是在比"半径"（我第一版就是这么错的，`dot<-0.9` 那条判据量纲是错的）。
                float d = Vector3.Dot(layer.GetTileCenter(i).normalized, dir);
                if (d < bestDot) { bestDot = d; best = i; }
            }
            return best;
        }

        /// <summary>
        /// 从 `[Collision] … at ({落点}) …` 里抠出**落点经纬度**那一段（ASCII 锚点：`" at ("` … `)`）。
        /// 抠不出 ⇒ `"?"`（⇒ 判据会红，而不是"比不出就通过"）。
        /// `internal` = 给集成测试钉住（这件仪器在游戏外没法验证，但它自己是**纯字符串**函数 ⇒ 可以）。
        /// </summary>
        internal static string SpotOf(string? logLine)
        {
            if (logLine == null) return "?";
            int a = logLine.IndexOf(" at (", StringComparison.Ordinal);
            if (a < 0) return "?";
            int b = logLine.IndexOf(')', a + 5);
            if (b <= a) return "?";
            return logLine.Substring(a + 5, b - a - 5);
        }

        private static int LogMessageCount()
        {
            int n = 0;
            foreach (var _ in Log.Messages) n++;
            return n;
        }

        private static string? FindLogLine(int fromIndex, string needle)
        {
            int i = 0;
            foreach (var m in Log.Messages)
            {
                if (i++ < fromIndex) continue;
                if (m.text != null && m.text.Contains(needle)) return m.text;
            }
            return null;
        }

        /// <summary>在**玩家可见的归档**里找一条消息（`Messages.Message(..., historical:true)` 的收口）。</summary>
        private static string? FindArchivedMessage(int sinceTick, string needle)
        {
            var archive = Find.Archive;
            if (archive == null) return null;
            var list = archive.ArchivablesListForReading;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is Message m && m.text != null && m.text.Contains(needle)
                    && ((IArchivable)m).CreatedTicksGame >= sinceTick)
                    return m.text;
            }
            return null;
        }
    }
}
