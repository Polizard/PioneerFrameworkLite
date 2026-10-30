using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **船上控制台**（③ + [A8] 的玩家入口）—— 2026-10-07。
    ///
    /// 它是什么：一块**顶上向下看的本地地块图**（以本船**星下点**为心，取 `LayerRing` 环地块），
    /// 加上一栏推进读数与航行控制（起搬 / 停止 / 悬浮档位）。
    /// **地块图上点一下 = 选目的地**（不再让人填"顺行 km"这种干数字；高度与 Δv 仍留小输入框）。
    ///
    /// 三个口径（都只有一处实现，本窗口只读/只显示）：
    /// · **读数**：`SpaceGameComp.GetGroupEnduranceReport` —— 火箭网报剩余 Δv、**环境相关的网报全推续航**；
    /// · **地块几何**：`Launch.GetSurfaceState`（tile ↔ ECI 单一事实源）+ `PlanetLayer.LongLatOf/GetTileNeighbors`；
    /// · **起搬**：`SpaceGameComp.StartAnalyticTransfer` / `StopAnalyticTransfer`（大气层内也允许，D8 只管"照扣料"）。
    ///
    /// ⚠ **红字教训（2026-10-07，用户"看红字"）**：Gizmo 的 `icon` 原来写了个**不存在的贴图路径**
    /// （`Things/Building/Ship/ShipSensorCluster`）⇒ 每次画按钮都吐一行红字（实测 871 行）。
    /// **贴图路径要么来自真文件（本 mod `1.6/Textures/...`）、要么用 `TexCommand.*` 这种"已加载的引用"**；
    /// 不确定的一律 `Get(path, reportFailure: false) ?? 兜底`。
    ///
    /// ⚠ 本文件是**本线自己的新文件**：不碰 PFView / 操舵台窗口那条视觉/交互 WIP 线的布局
    /// （入口只是挂在操舵台组件上多一个 Gizmo）。
    /// </summary>
    public class Window_Transfer : Window
    {
        // ── 地图 ──
        private const float MapSize = 340f;      // 地块图边长（像素）
        private const int LayerRing = 3;         // 以星下点为心的环数（3 ⇒ 7×7 地块）
        private const double GeoRefreshSeconds = 0.4;   // 星下点反查（线性扫描）刷新节拍
        private static readonly Color MapBg = new(0.03f, 0.04f, 0.06f, 1f);
        private static readonly Color GridLine = new(0f, 0f, 0f, 0.55f);

        /// <summary>一块地格（缓存：画它只需要这些）。</summary>
        private struct TileCell
        {
            public int tileId;
            public float latDeg, lonDeg;
            public Color color;          // **底色来自地貌配色表**（保证画得出来，绝不依赖贴图成功）
            public Texture2D? tex;       // 贴图只做**低透明度叠加**（有就有细节，没有也不影响可读）
            public Rect uv;
        }

        private readonly int groupId;
        private readonly List<TileCell> patch = new();
        private PlanetLayer? patchLayer;          // 换天体（换层）⇒ 缓存必须重建（同 id ≠ 同地块）
        private int patchCenterTile = -1;
        private string? mapUnavailable;           // 地图画不出来时的**具体原因**（画在界面上 + 日志一行）
        private bool patchLogged, unavailableLogged;
        private bool hasScanned;                  // 星下点反查是"全网格线性扫描" ⇒ 只在船走过约 0.35 格时做
        private DVector3 lastScanPos;
        private int shipTile = -1;
        private double lastGeoRefresh = double.NegativeInfinity;

        // ── 本船几何（每帧现算，便宜）──
        private float shipLatDeg, shipLonDeg;
        private float shipAltKm;
        private float shipGroundSpeed;
        private float shipHeadingDeg;
        private bool hasHeading;
        private float degPerTile = 0.573f;
        private float tileKm;                    // 一格 ≈ 多少 km（写在地图角上，省得玩家猜比例尺）

        // ── 玩家输入 ──
        private int destTile = -1;
        private string altitudeKm = "30";
        private string dv = "400";
        private string? note;
        private bool seeded;

        public Window_Transfer(int groupId)
        {
            this.groupId = groupId;
            closeOnAccept = false;
            doCloseX = true;
            draggable = true;
            resizeable = false;
            absorbInputAroundWindow = false;
            forcePause = false;
            preventCameraMotion = false;
        }

        // ⚠ `Window.Margin = 18`（原版）：`DoWindowContents` 收到的是**扣掉边距的内矩形**
        // ⇒ 窗口尺寸必须把两边边距与标题也算进去，否则地图会被窗口裁掉。
        public override Vector2 InitialSize => new(MapSize + 320f + 36f, MapSize + 30f + 36f);

        // ══════════════════════════════════════════════════════════════
        //  几何刷新（节拍与投影）
        // ══════════════════════════════════════════════════════════════

        private PlanetLayer? ResolveLayer(CelestialBodyManager? mgr, SpaceEntityGroup group)
            => Launch.GetSurfaceState.GetLayerForBody(mgr, group.CelestialBodyId);

        private void RefreshGeometry(SpaceEntityGroup group,
            CelestialBodyManager? mgr, PlanetLayer? layer, double physicsTick)
        {
            var pos = group.CurrentOrbit.positionSystem.v;
            var vel = group.CurrentOrbit.velocitySystem.v;
            if (mgr?.TryGetCelestialRuntime(out _) == true)
            {
                var offset = mgr.BodyFrameOffset(group.CelestialBodyId, physicsTick);
                pos -= offset.Position;
                vel -= offset.Velocity;
            }
            double omega = Launch.GetSurfaceState.RotationOmega(mgr, group.CelestialBodyId);
            double radius = Launch.GetSurfaceState.BodyRadius(mgr, group.CelestialBodyId);

            var (latRad, lonRad) = Launch.GetSurfaceState.BodyLongLat(mgr, group.CelestialBodyId, pos, physicsTick);
            shipLatDeg = (float)(latRad * Mathf.Rad2Deg);
            shipLonDeg = (float)(lonRad * Mathf.Rad2Deg);
            shipAltKm = (float)((pos.magnitude - radius) / 1000.0);

            // 地速与航向：地面相对速度（扣掉共转）投到当地东/北基上（基用**同一份**换算转进 ECI）
            var vRel = vel - Launch.GetSurfaceState.CoRotationVelocity(mgr, group.CelestialBodyId, pos, physicsTick);
            var eastFixed = new DVector3(Math.Cos(lonRad), 0.0, Math.Sin(lonRad));
            var northFixed = new DVector3(-Math.Sin(lonRad) * Math.Sin(latRad), Math.Cos(latRad),
                Math.Cos(lonRad) * Math.Sin(latRad));
            var eastEci = mgr?.TryGetCelestialRuntime(out _) == true
                ? Launch.GetSurfaceState.FixedSurfaceState(mgr, group.CelestialBodyId, eastFixed, physicsTick).pos
                : SurfaceFrame.FixedToEci(eastFixed, omega, physicsTick);
            var northEci = mgr?.TryGetCelestialRuntime(out _) == true
                ? Launch.GetSurfaceState.FixedSurfaceState(mgr, group.CelestialBodyId, northFixed, physicsTick).pos
                : SurfaceFrame.FixedToEci(northFixed, omega, physicsTick);
            double ve = DVector3.Dot(vRel, eastEci), vn = DVector3.Dot(vRel, northEci);
            shipGroundSpeed = (float)Math.Sqrt(ve * ve + vn * vn);
            hasHeading = shipGroundSpeed > 1.0f;      // 站着不动（共转）时没有"航向"
            shipHeadingDeg = (float)(Math.Atan2(ve, vn) * Mathf.Rad2Deg);

            if (layer == null)
            {
                mapUnavailable = "本船所在地体没有地表网格（bodyId=" + group.CelestialBodyId + "）";
                LogUnavailable();
                return;
            }
            if (physicsTick - lastGeoRefresh < GeoRefreshSeconds) return;
            lastGeoRefresh = physicsTick;

            // ⚠ 星下点反查 = **全网格线性扫描**（原版 `EciToTile` 自己标了"只适合事件级调用"）：
            //   不能每帧做 ⇒ 只在**船走过了约 0.35 格**时才重算（悬停时永远不算、LEO 也就几秒一次）。
            //   地图的"滚动"不靠它：地格位置每帧按**船自己的经纬度**投影 ⇒ 照样平顺。
            if (hasScanned && (pos - lastScanPos).magnitude < 0.35 * tileKm * 1000.0) return;
            hasScanned = true;
            lastScanPos = pos;

            int t = mgr?.TryGetCelestialRuntime(out _) == true
                ? Launch.GetSurfaceState.EciToTile(pos, mgr, group.CelestialBodyId, physicsTick)
                : Launch.GetSurfaceState.EciToTile(pos, layer, omega, physicsTick);
            if (t >= 0) shipTile = t;
            if (layer != patchLayer || shipTile != patchCenterTile) BuildPatch(layer, shipTile, radius);

            // 首次开窗：把高度框填成**当前高度**（省得玩家先换算一遍）
            if (!seeded && shipAltKm > 0.01f)
            {
                seeded = true;
                altitudeKm = $"{shipAltKm:F1}";
            }
        }

        /// <summary>地图画不出来时：**界面上写原因 + 日志留一行**（"全黑"什么都问不出来，实测踩过）。</summary>
        private void LogUnavailable()
        {
            if (unavailableLogged) return;
            unavailableLogged = true;
            Log.Warning($"[控制台] 地块图不可用：{mapUnavailable}（船 φ={shipLatDeg:F2}° λ={shipLonDeg:F2}° 高度 {shipAltKm:F1} km）");
        }

        /// <summary>以星下点为心，BFS 取 `LayerRing` 环地块（含材质/经纬度缓存）。</summary>
        private void BuildPatch(PlanetLayer layer, int centerTile, double bodyRadiusMeters)
        {
            patch.Clear();
            patchLayer = layer;
            patchCenterTile = centerTile;
            if (centerTile < 0) return;

            degPerTile = layer.AverageTileSize / Mathf.Max(layer.Radius, 1f) * Mathf.Rad2Deg;
            tileKm = (float)(degPerTile * Mathf.Deg2Rad * bodyRadiusMeters / 1000.0);

            var visited = new HashSet<int> { centerTile };
            var frontier = new List<int> { centerTile };
            var all = new List<int> { centerTile };
            var nb = new List<PlanetTile>();
            for (int ring = 0; ring < LayerRing; ring++)
            {
                var next = new List<int>();
                foreach (int id in frontier)
                {
                    nb.Clear();
                    layer.GetTileNeighbors(id, nb);
                    for (int i = 0; i < nb.Count; i++)
                    {
                        int nid = nb[i].tileId;
                        if (visited.Add(nid)) { next.Add(nid); all.Add(nid); }
                    }
                }
                frontier = next;
                if (frontier.Count == 0) break;
            }

            foreach (int id in all)
            {
                Vector2 ll = layer.LongLatOf(id);
                Tile? tile = layer[id];
                BiomeDef? biome = tile?.PrimaryBiome;
                var cell = new TileCell
                {
                    tileId = id,
                    lonDeg = ll.x,
                    latDeg = ll.y,
                    color = BiomeColor(biome, tile?.elevation ?? 100f)
                };
                var mat = biome?.DrawMaterial;
                if (mat != null && mat.mainTexture is Texture2D tex2)
                {
                    cell.tex = tex2;
                    // 每格取贴图上的**不同小窗**（按 id 定死）⇒ 相邻地块不会是一模一样的方块
                    float u = Mathf.Repeat(id * 0.618034f, 1f - 0.18f);
                    float v = Mathf.Repeat(id * 0.381966f, 1f - 0.18f);
                    cell.uv = new Rect(u, v, 0.18f, 0.18f);
                }
                patch.Add(cell);
            }

            if (!patchLogged)
            {
                patchLogged = true;
                mapUnavailable = null;
                // 守住"经纬度读成 NaN"这一类层数据异常（NaN 会让每块地格什么都画不出来 ⇒ 全黑）
                int bad = 0;
                for (int i = 0; i < patch.Count; i++)
                    if (float.IsNaN(patch[i].latDeg) || float.IsNaN(patch[i].lonDeg)) bad++;
                if (bad > 0)
                {
                    patch.Clear();
                    mapUnavailable = $"地块经纬度读到 NaN（{bad}/{all.Count} 格）：层={(patchLayer?.Def?.defName)} TilesCount={(patchLayer?.TilesCount ?? -1)}";
                    LogUnavailable();
                    return;
                }
                Log.Message($"[控制台] 地块图：层={(patchLayer?.Def?.defName)}｜中心格 {patchCenterTile}｜共 {patch.Count} 格（有贴图 {CountTextured()}）｜船 φ={shipLatDeg:F2}° λ={shipLonDeg:F2}° 高度 {shipAltKm:F1} km｜每格≈{tileKm:F0} km");
            }
        }

        private int CountTextured()
        {
            int n = 0;
            for (int i = 0; i < patch.Count; i++) if (patch[i].tex != null) n++;
            return n;
        }

        /// <summary>
        /// **地貌配色表**（保证"画得出来"的唯一依据：贴图可能缺席/偏暗，颜色不会）。
        /// 按 `defName` 关键词归类；没归到的按名字哈希给一个**稳定且够亮**的颜色（绝不落到黑色）。
        /// </summary>
        private static Color BiomeColor(BiomeDef? biome, float elevation)
        {
            string n = biome != null ? biome.defName.ToLowerInvariant() : "";
            Color c;
            if (n.Length == 0) c = new Color(0.40f, 0.44f, 0.40f);
            else if (n.Contains("ocean") || n.Contains("sea") || n.Contains("water")) c = new Color(0.13f, 0.30f, 0.55f);
            else if (n.Contains("lake") || n.Contains("river")) c = new Color(0.18f, 0.38f, 0.62f);
            else if (n.Contains("ice") || n.Contains("glacier") || n.Contains("snow")) c = new Color(0.84f, 0.88f, 0.93f);
            else if (n.Contains("tundra") || n.Contains("boreal")) c = new Color(0.50f, 0.56f, 0.46f);
            else if (n.Contains("jungle") || n.Contains("tropical") || n.Contains("rainforest")) c = new Color(0.18f, 0.42f, 0.20f);
            else if (n.Contains("forest")) c = new Color(0.26f, 0.48f, 0.26f);
            else if (n.Contains("swamp") || n.Contains("marsh") || n.Contains("wetland")) c = new Color(0.30f, 0.40f, 0.29f);
            else if (n.Contains("desert") || n.Contains("sand") || n.Contains("dune")) c = new Color(0.80f, 0.72f, 0.46f);
            else if (n.Contains("shrub")) c = new Color(0.62f, 0.60f, 0.38f);
            else if (n.Contains("mountain") || n.Contains("rock") || n.Contains("stone") || n.Contains("crag")) c = new Color(0.52f, 0.50f, 0.47f);
            else if (n.Contains("moon") || n.Contains("asteroid") || n.Contains("barren") || n.Contains("regolith")) c = new Color(0.60f, 0.60f, 0.62f);
            else if (n.Contains("grass") || n.Contains("plain") || n.Contains("savanna") || n.Contains("meadow")) c = new Color(0.44f, 0.56f, 0.30f);
            else
            {
                int h = 0;
                foreach (char ch in biome!.defName) h = (h * 31 + ch) & 0x7fffffff;
                c = Color.HSVToRGB((h % 360) / 360f, 0.32f, 0.62f);
            }
            // 高程微调明暗（同批地块之间能看出地势起伏；纯表现，不改数据）
            float b = Mathf.Clamp(0.94f + (elevation - 100f) / 2400f, 0.72f, 1.15f);
            return new Color(Mathf.Clamp01(c.r * b), Mathf.Clamp01(c.g * b), Mathf.Clamp01(c.b * b), 1f);
        }

        // ══════════════════════════════════════════════════════════════
        //  绘制
        // ══════════════════════════════════════════════════════════════

        public override void DoWindowContents(Rect inRect)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var group = spaceGame?.GetGroupById(groupId);

            Text.Font = GameFont.Small;
            if (group == null)
            {
                Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 24f), "编队不存在（已销毁？）");
                return;
            }

            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            PlanetLayer? layer = ResolveLayer(mgr, group);
            RefreshGeometry(group, mgr, layer, physicsTick);

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 30f), "船上控制台 · 航行与推进");
            Text.Font = GameFont.Small;

            Rect mapRect = new Rect(inRect.x, inRect.y + 30f, MapSize, MapSize);

            DrawMap(mapRect, spaceGame!, group, mgr, layer, physicsTick);
            DrawSide(new Rect(mapRect.xMax + 10f, inRect.y, inRect.width - MapSize - 10f, inRect.height),
                spaceGame!, group, mgr, layer, physicsTick);
        }

        private void DrawMap(Rect mapRect, SpaceGameComp spaceGame, SpaceEntityGroup group,
            CelestialBodyManager? mgr, PlanetLayer? layer, double physicsTick)
        {
            Widgets.DrawBoxSolid(mapRect, MapBg);
            if (layer == null || patch.Count == 0)
            {
                Widgets.Label(new Rect(mapRect.x + 8f, mapRect.y + 8f, mapRect.width - 16f, 60f),
                    "地块图暂时画不出来：" + (mapUnavailable ?? "星下点还在反查（等半秒）")
                    + "\n读数与搬运仍可用（原因也写进了日志）。");
                return;
            }

            // 局部等距圆柱投影：窗口中心 = 本船星下点（地图随船"滚"）
            float pxPerDeg = MapSize / (2f * LayerRing + 1f) / Mathf.Max(degPerTile, 1e-4f);
            float tilePx = pxPerDeg * degPerTile;
            float cosLat = Mathf.Cos(shipLatDeg * Mathf.Deg2Rad);

            GUI.BeginGroup(mapRect);
            try
            {
                for (int i = 0; i < patch.Count; i++)
                {
                    TileCell c = patch[i];
                    float dLon = Mathf.DeltaAngle(shipLonDeg, c.lonDeg) * cosLat;
                    float dLat = c.latDeg - shipLatDeg;
                    if (float.IsNaN(dLon) || float.IsNaN(dLat)) continue;      // 守住 NaN（NaN 矩形=什么都不画 ⇒ 全黑）
                    float cx = MapSize * 0.5f + dLon * pxPerDeg;
                    float cy = MapSize * 0.5f - dLat * pxPerDeg;
                    Rect r = new Rect(cx - tilePx * 0.5f, cy - tilePx * 0.5f, tilePx, tilePx);
                    if (r.xMax < 0f || r.yMax < 0f || r.x > MapSize || r.y > MapSize) continue;

                    // ① **底色（配色表）先画** —— 保证地块一定看得见；② 贴图只做低透明度叠加（有就有细节）
                    Widgets.DrawBoxSolid(r, c.color);
                    if (c.tex != null)
                    {
                        GUI.color = new Color(1f, 1f, 1f, 0.45f);
                        Widgets.DrawTexturePart(r, c.uv, c.tex);
                        GUI.color = Color.white;
                    }
                    // 1px 缝 → 地块之间看得出格线
                    Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 1f), GridLine);
                    Widgets.DrawBoxSolid(new Rect(r.x, r.y, 1f, r.height), GridLine);

                    if (c.tileId == shipTile)
                    {
                        Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 2f), new Color(1f, 1f, 1f, 0.45f));
                        Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 2f, r.width, 2f), new Color(1f, 1f, 1f, 0.45f));
                    }
                    if (c.tileId == destTile)
                    {
                        Widgets.DrawBoxSolid(r, new Color(0.35f, 1f, 0.55f, 0.18f));
                        GUI.DrawTexture(new Rect(cx - 12f, cy - 12f, 24f, 24f), TexCommand.Attack);
                    }
                    // **点地块 = 选目的地**
                    if (Widgets.ButtonInvisible(r))
                    {
                        destTile = c.tileId;
                        note = "目的地已选：地块 " + c.tileId + "（按「起搬」发车）。";
                    }
                }

                // 航线（把解析搬运的路径按同一份纯函数采样，再投回经纬度）
                if (destTile >= 0)
                {
                    var from = group.CurrentOrbit.positionSystem.v;
                    var to = DestinationEci(layer, mgr, group, physicsTick);
                    Vector2 prev = default;
                    bool has = false;
                    for (int k = 0; k <= 24; k++)
                    {
                        double u = k / 24.0;
                        var p = AnalyticTransferCore.PositionAt(from, to, u);
                        if (mgr?.TryGetCelestialRuntime(out _) == true) p -= mgr.BodyFrameOffset(group.CelestialBodyId, physicsTick).Position;
                        var (la, lo) = Launch.GetSurfaceState.BodyLongLat(mgr, group.CelestialBodyId, p, physicsTick);
                        float dLon2 = Mathf.DeltaAngle(shipLonDeg, (float)(lo * Mathf.Rad2Deg)) * cosLat;
                        float dLat2 = (float)(la * Mathf.Rad2Deg) - shipLatDeg;
                        var pt = new Vector2(MapSize * 0.5f + dLon2 * pxPerDeg, MapSize * 0.5f - dLat2 * pxPerDeg);
                        if (has) Widgets.DrawLine(prev, pt, new Color(0.4f, 1f, 0.6f, 0.7f), 1.4f);
                        prev = pt;
                        has = true;
                    }
                }

                // 本船：位置点 + 航向线（北在图上、东在右）
                var ctr = new Vector2(MapSize * 0.5f, MapSize * 0.5f);
                Widgets.DrawBoxSolid(new Rect(ctr.x - 3f, ctr.y - 3f, 6f, 6f), Color.white);
                if (hasHeading)
                {
                    float rad = (90f - shipHeadingDeg) * Mathf.Deg2Rad;   // 屏幕：+x 东、+y 南
                    var dir = new Vector2(Mathf.Cos(rad), -Mathf.Sin(rad));
                    Widgets.DrawLine(ctr, ctr + dir * 26f, new Color(1f, 0.95f, 0.45f, 0.95f), 2f);
                }
            }
            finally
            {
                GUI.EndGroup();
            }

            // 比例尺（写在地图上，不用玩家猜）
            Widgets.Label(new Rect(mapRect.x + 4f, mapRect.yMax - 20f, mapRect.width - 8f, 20f),
                $"每格 ≈ {tileKm:F0} km｜本船星下点居中（{2 * LayerRing + 1}×{2 * LayerRing + 1} 格）");
        }

        private void DrawSide(Rect r, SpaceGameComp spaceGame, SpaceEntityGroup group,
            CelestialBodyManager? mgr, PlanetLayer? layer, double physicsTick)
        {
            float x = r.x, w = r.width, y = r.y + 30f;
            const float RowH = 22f;

            Widgets.Label(new Rect(x, y, w, RowH), $"星下点：地块 {shipTile}（φ {shipLatDeg:F1}° λ {shipLonDeg:F1}°）");
            y += RowH;
            Widgets.Label(new Rect(x, y, w, RowH), $"高度 {shipAltKm:F1} km｜地速 {shipGroundSpeed:F0} m/s｜{(hasHeading ? ("航向 " + $"{shipHeadingDeg:F0}" + "°") : "原地（共转）")}");
            y += RowH + 4f;

            // ── 推进读数（口径唯一出处 = GetGroupEnduranceReport）──
            Widgets.Label(new Rect(x, y, w, RowH), group.debugInfinitePropellant
                ? "推进读数（每工质网）· **调试：无限燃料**（料不减少）"
                : "推进读数（每工质网）：");
            y += RowH;
            var report = spaceGame.GetGroupEnduranceReport(group);
            if (report.Count == 0) { Widgets.Label(new Rect(x, y, w, RowH), "（没有工质网）"); y += RowH; }
            for (int i = 0; i < report.Count; i++)
            {
                var net = report[i];
                string nm = (net.propellant != null ? net.propellant.label : "?");
                string v = net.environmentDependent
                    ? $"全推续航 {net.enduranceSeconds:F0} s（ṁ {net.massFlow:F1} kg/s）"
                    : $"料 {net.propellantMass:F0} kg";
                Widgets.Label(new Rect(x, y, w, RowH), nm + "：" + v);
                y += RowH;
            }
            y += 4f;

            // ── 目的地（地块图上点选）──
            Widgets.Label(new Rect(x, y, w, RowH), destTile >= 0
                ? $"目的地：地块 {destTile}｜面距 {SurfaceDistanceKm(layer, destTile, mgr, group):F0} km"
                : "目的地：在地块图上点一下");
            y += RowH;
            Widgets.Label(new Rect(x, y + 3f, 54f, RowH), "高度 km");
            altitudeKm = Widgets.TextField(new Rect(x + 56f, y, 64f, RowH), altitudeKm);
            Widgets.Label(new Rect(x + 126f, y + 3f, 34f, RowH), "Δv");
            dv = Widgets.TextField(new Rect(x + 152f, y, 64f, RowH), dv);
            y += RowH + 2f;

            bool busy = group.transferActive;
            if (Widgets.ButtonText(new Rect(x, y, 96f, 26f), busy ? "改航（重起）" : "起搬"))
                StartTransfer(spaceGame, group, mgr, layer, physicsTick);
            if (busy)
            {
                if (Widgets.ButtonText(new Rect(x + 102f, y, 74f, 26f), "停止"))
                {
                    spaceGame.StopAnalyticTransfer(group, "控制台下令");
                    note = "已停止搬运（停在当前位置）。";
                }
            }
            y += 30f;

            // ── 悬浮档位 ──
            // ⚠ 先做**只读预检**（`CanHover`，与生产门同一份判据）⇒ **把"为什么按不动"写在界面上**，
            //   不再给"不在大气里/没有网/没引擎"这种三选一（玩家只能看日志猜；2026-10-07 实测被误读成"不在大气里"）。
            Widgets.Label(new Rect(x, y + 4f, 40f, RowH), "悬浮：");
            bool canPhys = spaceGame.CanHover(group, analytic: false, out string? whyPhys);
            bool canAna = spaceGame.CanHover(group, analytic: true, out string? whyAna);
            GUI.color = group.hoverActive && !group.hoverAnalytic ? Color.green : (canPhys ? Color.white : Color.gray);
            if (Widgets.ButtonText(new Rect(x + 42f, y, 72f, 26f), "物理档")) ToggleHover(spaceGame, group, analytic: false);
            GUI.color = group.hoverActive && group.hoverAnalytic ? Color.green : (canAna ? Color.white : Color.gray);
            if (Widgets.ButtonText(new Rect(x + 118f, y, 72f, 26f), "解析档")) ToggleHover(spaceGame, group, analytic: true);
            GUI.color = Color.white;
            if (Widgets.ButtonText(new Rect(x + 194f, y, 56f, 26f), "解除"))
            {
                if (spaceGame.SetHover(group, false)) note = "悬浮已解除。";
            }
            y += 28f;
            // 悬浮中 ⇒ 明说"船被按在定点上"（2026-10-07 实测：玩家看着像"被锚死了、走不出去"）
            if (group.hoverActive)
            {
                Widgets.Label(new Rect(x, y, w, RowH * 2f), "（悬浮中：船被按在定点上 ⇒ 要飞就「起搬」"
                    + "（到点自动恢复悬浮）或「解除」；用操舵台接管也会退悬浮）");
                y += RowH;
            }
            if (!canAna) { Widgets.Label(new Rect(x, y, w, RowH * 2f), "解析档不可用：" + whyAna); y += RowH; }
            if (whyPhys != null)
            {
                Widgets.Label(new Rect(x, y, w, RowH * 2f), "物理档：" + whyPhys);
                y += RowH;
            }
            y += 4f;

            // ── 搬运进度 / 结果 ──
            if (busy)
            {
                double u = AnalyticTransferCore.Progress(physicsTick - group.transferStartTick, group.transferDuration);
                Widgets.Label(new Rect(x, y, w, RowH), $"搬运中：Δv {group.transferDvTotal:F0} m/s｜时长 {group.transferDuration:F0} s｜进度 {u:P0}");
                y += RowH;
            }
            else if (group.transferArrivalErrorMeters > 0.0)
            {
                Widgets.Label(new Rect(x, y, w, RowH), $"上次到达落位误差：{group.transferArrivalErrorMeters:F1} m");
                y += RowH;
            }

            if (!string.IsNullOrEmpty(note))
                Widgets.Label(new Rect(x, y, w, 40f), note);
        }

        // ══════════════════════════════════════════════════════════════
        //  动作
        // ══════════════════════════════════════════════════════════════

        /// <summary>目的地（地块中心抬到给定高度）的 ECI 状态——起搬与实际画航线**同一份**。</summary>
        private DVector3 DestinationEci(PlanetLayer? layer, CelestialBodyManager? mgr, SpaceEntityGroup group, double physicsTick, float altKmOverride = -1f)
        {
            if (layer == null || destTile < 0) return group.CurrentOrbit.positionSystem.v;
            double omega = Launch.GetSurfaceState.RotationOmega(mgr, group.CelestialBodyId);
            double radius = Launch.GetSurfaceState.BodyRadius(mgr, group.CelestialBodyId);
            float altKm = altKmOverride > 0f ? altKmOverride : shipAltKm;
            if (mgr?.TryGetCelestialRuntime(out _) == true)
            {
                var local = Launch.GetSurfaceState.TileToEci(destTile, mgr, group.CelestialBodyId, physicsTick);
                return mgr.BodyFrameOffset(group.CelestialBodyId, physicsTick).Position + local + local.normalized * (altKm * 1000.0);
            }
            var p = Launch.GetSurfaceState.TileToEci(layer, destTile, radius, omega, physicsTick);
            return p + p.normalized * (altKm * 1000.0);
        }

        private float SurfaceDistanceKm(PlanetLayer? layer, int tile, CelestialBodyManager? mgr, SpaceEntityGroup group)
        {
            if (layer == null || tile < 0) return 0f;
            Vector3 a = layer.GetTileCenter(shipTile >= 0 ? shipTile : tile);
            Vector3 b = layer.GetTileCenter(tile);
            double radius = Launch.GetSurfaceState.BodyRadius(mgr, group.CelestialBodyId);
            return (float)(Vector3.Angle(a, b) * Mathf.Deg2Rad * radius / 1000.0);
        }

        private void ToggleHover(SpaceGameComp spaceGame, SpaceEntityGroup group, bool analytic)
        {
            if (group.hoverActive && group.hoverAnalytic == analytic)
            {
                spaceGame.SetHover(group, false);
                note = "悬浮已解除。";
                return;
            }
            bool ok = group.hoverActive
                ? spaceGame.SwitchHoverTier(group, analytic)
                : spaceGame.SetHover(group, true, analytic);
            if (ok)
            {
                note = analytic ? "悬浮（解析档）。" : "悬浮（物理档）。";
                return;
            }
            // 被拒 ⇒ **报具体原因**（与生产同一份预检，不再三选一让玩家猜）
            spaceGame.CanHover(group, analytic, out string? why);
            note = (analytic ? "解析档被拒：" : "物理档被拒：") + (why ?? "详情见日志");
        }

        private void StartTransfer(SpaceGameComp spaceGame, SpaceEntityGroup group,
            CelestialBodyManager? mgr, PlanetLayer? layer, double physicsTick)
        {
            if (destTile < 0) { note = "先在地块图上点一个目的地。"; return; }
            if (!float.TryParse(altitudeKm, out float altKm) || !float.TryParse(dv, out float dvF))
            { note = "高度/Δv 要能填成小数。"; return; }
            if (dvF <= 0f) { note = "Δv 要 > 0。"; return; }

            var targetPos = DestinationEci(layer, mgr, group, physicsTick, altKm);
            var offset = mgr?.TryGetCelestialRuntime(out _) == true ? mgr.BodyFrameOffset(group.CelestialBodyId, physicsTick) : (Position: DVector3.zero, Velocity: DVector3.zero);
            var targetVel = offset.Velocity + Launch.GetSurfaceState.CoRotationVelocity(mgr, group.CelestialBodyId, targetPos - offset.Position, physicsTick);
            bool wasHovering = group.hoverActive;      // 起搬会**临时退出悬浮**，并在**到达点自动恢复原档位**
            note = spaceGame.StartAnalyticTransfer(group, targetPos, targetVel, dvF, physicsTick)
                ? (wasHovering
                    ? "已起搬（临时退出悬浮 —— **到点自动恢复悬浮**）。"
                    : "已起搬（看日志 [搬运] 行与上面的进度）。")
                : "起搬被拒 —— 具体原因看日志（没有引擎 / 端点在地表以下 / 处于交战动作）。";
        }
    }

    /// <summary>
    /// **本线 UI 的 Gizmo 宿主**：挂在**操舵台**上（2026-10-07 用户点名"UI 要在控制台上、不要挂在罐子上"）。
    ///
    /// ⚠ **玩家判定看"船的派系"**（不是宿主部件自己的 `Thing.Faction`）：调试场景的建筑是
    /// `GenSpawn.Spawn` 直生的、**无派系**（`Thing.Faction == null`）—— 拿部件派系当闸会把入口
    /// 在全部调试场景里整条藏掉（实测踩过）。非飞船地图 / 无主 / 敌方船 ⇒ 一律不给这个按钮。
    ///
    /// ⚠ **图标必须是真的**：这里用本 mod 自己的贴图，并留 `TexCommand` 兜底；
    /// 写一个不存在的路径 ⇒ 每次画按钮吐一行红字（实测 871 行，用户"看红字"那次）。
    /// </summary>
    public static class PropulsionGizmo
    {
        private static Texture2D? icon;

        /// <summary>由宿主组件调用；**只在"玩家自己的船"上**返回 Gizmo，否则返回 null（= 没有这个按钮）。</summary>
        public static Gizmo? TryMake(ThingComp host)
        {
            int groupId = ResolvePlayerGroupId(host);
            if (groupId < 0) return null;
            if (icon == null)
                icon = ContentFinder<Texture2D>.Get("UI/Icons/SatelliteIcon", false) ?? TexCommand.DesirePower;
            return new Command_Action
            {
                defaultLabel = "船上控制台",
                defaultDesc = "顶上向下看的本地地块图（点地块 = 选目的地）+ 推进读数 + 起搬/停止 + 悬浮档位。",
                icon = icon,
                action = () => Find.WindowStack.Add(new Window_Transfer(groupId))
            };
        }

        /// <summary>宿主所在地图的"船主"编队；**只有它是玩家自己的**才返回 groupId，否则 -1（含非飞船地图 / 无主）。</summary>
        private static int ResolvePlayerGroupId(ThingComp host)
        {
            var mapComp = host.parent?.Map?.GetComponent<SpaceMapComp>();
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (mapComp == null || spaceGame == null || mapComp.ownerEntityId < 0) return -1;

            var entity = spaceGame.GetEntityById(mapComp.ownerEntityId);
            if (entity == null) return -1;

            var group = spaceGame.GetGroupById(entity.GroupId);
            var player = Faction.OfPlayer;
            if (group == null || player == null || group.FactionId != player.loadID) return -1;
            return group.GroupId;
        }
    }
}
