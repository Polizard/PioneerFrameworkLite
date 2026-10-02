using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 布局与仪表（V-1，2026-10-02 用户裁定①）：
    ///   顶部工具条 / 左侧文字堆（编队列表 + 接触列表 + 选中组读数）/ 下边中央姿态球（KSP 式）/
    ///   右侧雷达（以选中编队为中心，**只显示、不做功能**）。
    ///
    /// 视觉纪律（裁定③）：全套灰度走 <see cref="PFViewPalette"/>——层级靠明度与线宽、状态靠符号，
    /// 不用色相。面板/角标装饰复用 <c>UiKit.SpacecraftUIDecor</c>（同一份程序化画笔）。
    ///
    /// 数据准入（红线）：列表与雷达**只读认知层**（`PlayerCanSee` / `SensorManager.AppendVisible`），
    /// 不得读真值；dev 真值对照仍走 `DebugShowGroundTruth` 开关。
    /// </summary>
    public partial class MainTabWindow_PFView : MainTabWindow
    {
        // ── V-1 布局常量 ──
        private const float ToolbarH = 30f;
        private const float LeftColW = 252f;
        private const float RadarW = 260f;
        private const float NavballD = 222f;        // 球直径（V-1.1：用户 2026-10-02 要求放大到 1.5×）
        private const float NavballPanelW = 522f;   // 面板宽 = 左文字 150 + 球 222 + 右文字 150
        private const float PanelPad = 8f;

        // 本帧分区（ComputeLayout 算好，绘制与输入共用同一几何）
        private Rect rToolbar;
        private Rect rLeftCol;
        private Rect rRadar;
        private Rect rNavball;
        private Rect rStatus;

        // 列表缓冲（追加式，免每帧分配；[M3] 硬规则：不用 IEnumerable）
        private readonly List<Contact> contactBuf = new List<Contact>();
        private readonly List<SpaceEntityGroup> groupListBuf = new List<SpaceEntityGroup>();

        // 姿态球圆盘贴图（程序化生成一次；RimWorld 纪律：不用外部贴图资源）
        private static Texture2D? pfvDiscTex;

        // 中键/Shift+右键平移拖拽中
        private bool isDraggingPan;

        /// <summary>算出本帧五分区并登记占位矩形（挡 3D 输入）。</summary>
        private void ComputeLayout(Rect inRect)
        {
            rToolbar = new Rect(inRect.x, inRect.y, inRect.width, ToolbarH);

            float top = inRect.y + ToolbarH + PanelPad;
            rLeftCol = new Rect(inRect.x + PanelPad, top, LeftColW, inRect.yMax - top - PanelPad);
            rRadar = new Rect(inRect.xMax - RadarW - PanelPad, top, RadarW, RadarW);
            // 姿态球面板：球居中，左右各留一条文字区（读数 + 图例）
            rNavball = new Rect(inRect.center.x - NavballPanelW * 0.5f, inRect.yMax - NavballD - PanelPad, NavballPanelW, NavballD);
            // 状态行：左缘起，宽度钳到姿态球面板左缘之前（球放大后不再重叠）
            float statusW = Mathf.Max(200f, rNavball.x - inRect.x - 16f);
            rStatus = new Rect(inRect.x + PanelPad, inRect.yMax - 22f, statusW, 20f);

            uiBlockRects.Add(rToolbar);
            uiBlockRects.Add(rLeftCol);
            uiBlockRects.Add(rRadar);
            uiBlockRects.Add(rNavball);
            uiBlockRects.Add(rStatus);
        }

        // ════════════════════════════════════════════════════
        //  顶部工具条
        // ════════════════════════════════════════════════════

        private void DrawToolbar()
        {
            var r = rToolbar;
            Widgets.DrawBoxSolid(r, PFViewPalette.BarBg);
            UiKit.SpacecraftUIDecor.DrawDivider(r, r.yMax - 1f, r.width, PFViewPalette.Divider);

            const float bh = 24f;
            float by = r.y + (r.height - bh) * 0.5f;
            float x = r.x + 6f;

            if (Widgets.ButtonText(new Rect(x, by, 96f, bh), "选择编队")) ShowGroupMenu();
            x += 100f;
            if (Widgets.ButtonText(new Rect(x, by, 96f, bh), "切换星系")) ShowSystemMenu();
            x += 100f;
            if (Widgets.ButtonText(new Rect(x, by, 132f, bh), "部署交火测试对")) DeployCombatTestPair();
            x += 140f;

            Widgets.CheckboxLabeled(new Rect(x, by, 108f, bh), "放大部件", ref debugScaleParts);
            x += 110f;
            Widgets.CheckboxLabeled(new Rect(x, by, 132f, bh), "太阳标记", ref debugShowSunMarker);
            x += 134f;
            Widgets.CheckboxLabeled(new Rect(x, by, 132f, bh), "地图浮雕", ref debugMapRelief);

            string anchor = selectedGroupId >= 0
                ? $"锚定 编队 {selectedGroupId}"
                : "未选中编队（点左栏列表或 3D 里左键拾取）";
            UiKit.SpacecraftUIDecor.DrawColoredLabel(
                new Rect(r.xMax - 430f, r.y + 4f, 420f, r.height - 8f),
                anchor, PFViewPalette.TextSecondary, GameFont.Tiny, TextAnchor.MiddleRight);
        }

        // ════════════════════════════════════════════════════
        //  左侧文字堆：编队 / 接触 / 选中组读数
        // ════════════════════════════════════════════════════

        private void DrawLeftColumn()
        {
            var r = rLeftCol;
            Widgets.DrawBoxSolid(r, PFViewPalette.PanelBg);
            UiKit.SpacecraftUIDecor.DrawCornerLBrackets(r, PFViewPalette.Border);

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null) return;

            var sensor = Current.Game?.GetComponent<SensorManager>();
            var player = Faction.OfPlayer;
            int playerFid = player?.loadID ?? 0;

            double tick = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 origin = GetRenderOrigin(tick);

            float x = r.x + 8f;
            float w = r.width - 16f;
            float y = r.y + 8f;

            // ── 编队 ──
            groupListBuf.Clear();
            var sysIds = GetCurrentSystemBodyIds(mgr);
            foreach (var g in spaceGame.GetAllGroups())
            {
                if (g == null || !g.IsActive) continue;
                if (!sysIds.Contains(g.CelestialBodyId)) continue;
                if (sensor != null && !sensor.PlayerCanSee(g)) continue;
                groupListBuf.Add(g);
            }

            MiniLabel(new Rect(x, y, w, 16f), $"编队 ({groupListBuf.Count})", PFViewPalette.TextSecondary);
            y += 18f;

            const int maxGroupRows = 10;
            int groupShown = Mathf.Min(groupListBuf.Count, maxGroupRows);
            for (int i = 0; i < groupShown; i++)
            {
                var g = groupListBuf[i];
                bool own = g.FactionId == playerFid;
                float distKm = (float)((GetGroupPosAbs(g, tick) - origin).magnitude / 1000.0);
                string label = $"{((own) ? "●" : "○")} G{g.GroupId}  {distKm:F0} km  {EntityCountOf(g, spaceGame)}实体";
                if (ListRow(new Rect(x, y, w, 18f), g.GroupId == selectedGroupId, label,
                        own ? PFViewPalette.TextPrimary : PFViewPalette.TextSecondary, out bool dbl))
                {
                    selectedGroupId = g.GroupId;
                    if (dbl) InitCameraForCurrentBody();
                }
                y += 18f;
            }
            if (groupListBuf.Count > groupShown)
            {
                MiniLabel(new Rect(x, y, w, 14f), $"... 其余 {groupListBuf.Count - groupShown} 个（用工具条「选择编队」）", PFViewPalette.TextMuted);
                y += 16f;
            }
            y += 6f;

            // ── 接触（只列情报类：匿踪接触 + 假接触；公开/我方已在上面的编队表）──
            contactBuf.Clear();
            sensor?.AppendVisible(playerFid, contactBuf);
            int intelCount = 0;
            for (int i = 0; i < contactBuf.Count; i++)
                if (IsIntelContact(contactBuf[i], spaceGame)) intelCount++;

            MiniLabel(new Rect(x, y, w, 16f), $"接触 ({intelCount})", PFViewPalette.TextSecondary);
            y += 18f;

            var selGroup = selectedGroupId >= 0 ? spaceGame.GetGroupById(selectedGroupId) : null;
            DVector3 refRel = selGroup != null ? GetGroupRelPos(selGroup) : DVector3.zero;

            const int maxContactRows = 8;
            int contactShown = 0;
            for (int i = 0; i < contactBuf.Count && contactShown < maxContactRows; i++)
            {
                var c = contactBuf[i];
                if (!IsIntelContact(c, spaceGame)) continue;
                contactShown++;

                string glyph;
                string tag;
                Color col;
                if (c.IsForged)
                {
                    // 假接触：识破与否是**观测者粒度**（`isDecoy` 只表示"已识破"）
                    glyph = c.isDecoy ? "✕" : "◌";
                    col = c.isDecoy ? PFViewPalette.StateDead : PFViewPalette.StateWarn;
                    tag = c.isDecoy ? $"d{c.decoyId} 已识破" : $"d{c.decoyId} 假";
                }
                else
                {
                    var phase = sensor != null ? sensor.ContactPhaseOf(playerFid, c.targetGroupId) : TrackPhase.Live;
                    glyph = phase == TrackPhase.Shadow ? "◐" : "●";
                    col = phase == TrackPhase.Shadow ? PFViewPalette.StateWarn : PFViewPalette.StateOk;
                    tag = $"G{c.targetGroupId}{(phase == TrackPhase.Shadow ? " 影子" : "")}";
                }

                string distStr = "—";
                if (selGroup != null)
                {
                    bool sameBody = c.IsForged || ContactBodyMatches(c, selGroup, spaceGame);
                    if (sameBody)
                        distStr = $"{(float)((c.position - refRel).magnitude / 1000.0):F0} km";
                }
                string sigmaStr = c.positionSigma > 0.5 ? $"  σ {c.positionSigma / 1000.0:F1} km" : "";
                string label = $"{glyph} {tag}  {distStr}{sigmaStr}";

                if (ListRow(new Rect(x, y, w, 18f), false, label, col, out _))
                {
                    // 真接触：选中它（可继续用"设为目标"）；假接触没有实体，仅提示
                    if (!c.IsForged) { selectedGroupId = c.targetGroupId; }
                }
                y += 18f;
            }
            if (intelCount > contactShown)
            {
                MiniLabel(new Rect(x, y, w, 14f), $"... 其余 {intelCount - contactShown} 条", PFViewPalette.TextMuted);
            }

            // ── 选中组读数（原 DrawInfoPanel 内容搬进左栏底部）──
            if (selectedGroupId >= 0)
                DrawInfoPanel(rLeftCol, LeftColW - 8f);
        }

        /// <summary>情报类接触判定：假接触，或"目标组是匿踪组"（公开/我方不进接触表）。</summary>
        private static bool IsIntelContact(in Contact c, SpaceGameComp sg)
        {
            if (c.IsForged) return true;
            var g = sg.GetGroupById(c.targetGroupId);
            return g != null && g.IsStealth;
        }

        /// <summary>接触与选中编队在同一中心天体（相距才有意义——两边都是"系统系"坐标）。</summary>
        private static bool ContactBodyMatches(in Contact c, SpaceEntityGroup sel, SpaceGameComp sg)
        {
            var g = sg.GetGroupById(c.targetGroupId);
            return g != null && g.CelestialBodyId == sel.CelestialBodyId;
        }

        private static int EntityCountOf(SpaceEntityGroup g, SpaceGameComp sg)
        {
            var ids = g.GetEntityIds();
            return ids != null ? ids.Count : 0;
        }

        // ════════════════════════════════════════════════════
        //  右侧雷达（只显示：以选中编队为中心，不做交互）
        // ════════════════════════════════════════════════════

        private void DrawRadarPanel()
        {
            var r = rRadar;
            Widgets.DrawBoxSolid(r, PFViewPalette.PanelBg);
            UiKit.SpacecraftUIDecor.DrawCornerLBrackets(r, PFViewPalette.Border);

            MiniLabel(new Rect(r.x + 8f, r.y + 4f, r.width - 16f, 16f),
                "雷达 · 相对选中编队（只显示）", PFViewPalette.TextSecondary);

            Vector2 c = new Vector2(r.center.x, r.center.y + 8f);
            float radius = Mathf.Min(r.width, r.height) * 0.5f - 28f;
            if (radius < 12f) return;

            // 环 + 十字；"上 = 顺行方向"
            for (int i = 1; i <= 3; i++)
                DrawScreenCircle(c, radius * i / 3f, PFViewPalette.Grid);
            Widgets.DrawLine(new Vector2(c.x, c.y - radius), new Vector2(c.x, c.y + radius), PFViewPalette.Grid, 1f);
            Widgets.DrawLine(new Vector2(c.x - radius, c.y), new Vector2(c.x + radius, c.y), PFViewPalette.Grid, 1f);
            MiniLabel(new Rect(c.x - 24f, c.y - radius - 14f, 48f, 12f), "顺行", PFViewPalette.TextMuted, TextAnchor.MiddleCenter);
            MiniLabel(new Rect(c.x + radius + 2f, c.y - 8f, 40f, 12f), "径外向", PFViewPalette.TextMuted, TextAnchor.MiddleLeft);

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var sensor = Current.Game?.GetComponent<SensorManager>();
            var g = (spaceGame != null && selectedGroupId >= 0) ? spaceGame.GetGroupById(selectedGroupId) : null;
            if (g == null || sensor == null || spaceGame == null)
            {
                MiniLabel(new Rect(r.x + 8f, r.yMax - 22f, r.width - 16f, 16f), "未选中编队", PFViewPalette.TextMuted, TextAnchor.MiddleCenter);
                return;
            }

            var player = Faction.OfPlayer;
            int playerFid = player?.loadID ?? 0;

            // 选中编队的轨道系基：R = 径向外、N = 轨道法向、T = 顺行（= N × R）
            DVector3 center = GetGroupRelPos(g);
            DVector3 radial = center.sqrMagnitude > 1.0 ? center.normalized : new DVector3(1, 0, 0);
            DVector3 nrm = g.circularOrbitNormal.sqrMagnitude > 0.001
                ? g.circularOrbitNormal.normalized
                : DVector3.Cross(radial, g.CurrentOrbit.velocitySystem.v).normalized;
            DVector3 pro = DVector3.Cross(nrm, radial).normalized;

            contactBuf.Clear();
            sensor.AppendVisible(playerFid, contactBuf);

            // 自适应量程（含全环标注）
            float maxR = 1000f;
            for (int i = 0; i < contactBuf.Count; i++)
            {
                var cc = contactBuf[i];
                if (cc.targetGroupId == selectedGroupId) continue;
                DVector3 rel = cc.position - center;
                float up = (float)DVector3.Dot(rel, pro);
                float right = (float)DVector3.Dot(rel, radial);
                maxR = Mathf.Max(maxR, Mathf.Sqrt(up * up + right * right));
            }
            float scale = radius * 0.92f / maxR;

            // 接触 blip（灰度：形状/明度表态，不靠色相）
            for (int i = 0; i < contactBuf.Count; i++)
            {
                var cc = contactBuf[i];
                if (cc.targetGroupId == selectedGroupId) continue;

                DVector3 rel = cc.position - center;
                float up = (float)DVector3.Dot(rel, pro);
                float right = (float)DVector3.Dot(rel, radial);
                Vector2 p = c + new Vector2(right, -up) * scale;
                if ((p - c).magnitude > radius * 1.02f) continue;

                Color col;
                float sz;
                bool stealth = false;
                if (cc.IsForged)
                {
                    col = cc.isDecoy ? PFViewPalette.StateDead : PFViewPalette.StateWarn;
                    sz = 3.5f;
                }
                else
                {
                    var phase = sensor.ContactPhaseOf(playerFid, cc.targetGroupId);
                    var gg = spaceGame.GetGroupById(cc.targetGroupId);
                    stealth = gg != null && gg.IsStealth;
                    col = phase == TrackPhase.Live
                        ? (stealth ? PFViewPalette.StateOk : PFViewPalette.StateWarn)
                        : PFViewPalette.StateDead;
                    sz = stealth ? 4f : 2.5f;
                }

                if (stealth || cc.IsForged)
                    Widgets.DrawBoxSolid(new Rect(p.x - sz * 0.5f, p.y - sz * 0.5f, sz, sz), col);
                else
                    Widgets.DrawBoxSolid(new Rect(p.x - 1f, p.y - 1f, 2f, 2f), col);
            }

            // 中心 = 选中编队
            Widgets.DrawLine(new Vector2(c.x - 5f, c.y), new Vector2(c.x + 5f, c.y), PFViewPalette.Accent, 1f);
            Widgets.DrawLine(new Vector2(c.x, c.y - 5f), new Vector2(c.x, c.y + 5f), PFViewPalette.Accent, 1f);

            MiniLabel(new Rect(r.x + 8f, r.yMax - 18f, r.width - 16f, 14f),
                $"满环 {maxR / 1000.0:F0} km", PFViewPalette.TextMuted);
        }

        private static void DrawScreenCircle(Vector2 c, float r, Color col)
        {
            const int seg = 48;
            Vector2 prev = c + new Vector2(r, 0f);
            for (int i = 1; i <= seg; i++)
            {
                float a = Mathf.PI * 2f * i / seg;
                Vector2 p = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                Widgets.DrawLine(prev, p, col, 1f);
                prev = p;
            }
        }

        // ════════════════════════════════════════════════════
        //  下边中央姿态球（KSP 式）
        // ════════════════════════════════════════════════════

        private void DrawNavballPanel()
        {
            var r = rNavball;
            Widgets.DrawBoxSolid(r, PFViewPalette.BarBg);
            UiKit.SpacecraftUIDecor.DrawCornerLBrackets(r, PFViewPalette.Border);

            float radius = Mathf.Min(r.width, r.height) * 0.5f - 12f;
            Vector2 c = r.center;

            EnsureDiscTexture();
            if (pfvDiscTex == null) return;   // 贴图生成失败：不画球（防御，正常不会发生）
            GUI.color = PFViewPalette.PanelBg;
            GUI.DrawTexture(new Rect(c.x - radius, c.y - radius, radius * 2f, radius * 2f), pfvDiscTex);
            GUI.color = Color.white;

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var g = (spaceGame != null && selectedGroupId >= 0) ? spaceGame.GetGroupById(selectedGroupId) : null;
            if (g == null || spaceGame == null)
            {
                MiniLabel(new Rect(r.x, c.y - 8f, r.width, 16f), "未选中编队", PFViewPalette.TextMuted, TextAnchor.MiddleCenter);
                return;
            }

            // 球面固定网格 = 船体系；屏幕 +Y 上 = 船体 +Y，+Z 朝向观察者
            DrawNavballGrid(c, radius);

            Quaternion qi = Quaternion.Inverse(g.CurrentOrbit.rotation);

            Vector3 vel = (Vector3)g.CurrentOrbit.velocitySystem.v;
            Vector3 pos = (Vector3)g.CurrentOrbit.positionSystem.v;
            Vector3 nrm = (Vector3)g.circularOrbitNormal;

            DrawNavballMarker(c, radius, qi, vel, "P", PFViewPalette.Accent);
            DrawNavballMarker(c, radius, qi, -vel, "A", PFViewPalette.TextSecondary);
            DrawNavballMarker(c, radius, qi, pos, "R", PFViewPalette.TextSecondary);
            DrawNavballMarker(c, radius, qi, -pos, "r", PFViewPalette.TextSecondary);
            if (nrm.sqrMagnitude > 0.001f)
            {
                DrawNavballMarker(c, radius, qi, nrm, "N", PFViewPalette.TextSecondary);
                DrawNavballMarker(c, radius, qi, -nrm, "S", PFViewPalette.TextSecondary);
            }

            DVector3 thrust = spaceGame.GetMainThrustAxisSystem(g);
            if (thrust.sqrMagnitude > 1e-12)
                DrawNavballMarker(c, radius, qi, (Vector3)thrust.normalized, "T", PFViewPalette.Accent);

            // 左右文字区（左：姿态/主推轴；右：速度 + 记号图例）——球放大 1.5× 后放两侧
            DrawNavballFlanks(r, c, radius, g, spaceGame, vel);
        }

        /// <summary>姿态球两侧文字读数（V-1.1）：左 = 姿态/角速度/主推轴；右 = 速度 + 记号图例。</summary>
        private void DrawNavballFlanks(Rect r, Vector2 c, float radius, SpaceEntityGroup g, SpaceGameComp spaceGame, Vector3 vel)
        {
            float sideW = (r.width - radius * 2f) * 0.5f;
            float lx = r.x + 8f;
            float rx = c.x + radius + 8f;
            const float lh = 16f;

            // 左：姿态 + 主推轴（与 3D 黄棒同一份数据）
            float y = r.y + 10f;
            MiniLabel(new Rect(lx, y, sideW - 16f, lh), "姿态", PFViewPalette.TextSecondary);
            y += lh;
            Vector3 euler = g.CurrentOrbit.rotation.eulerAngles;
            MiniLabel(new Rect(lx, y, sideW - 16f, lh), string.Format("yaw {0:F0}° pitch {1:F0}° roll {2:F0}°", euler.y, euler.x, euler.z), PFViewPalette.TextPrimary);
            y += lh;
            MiniLabel(new Rect(lx, y, sideW - 16f, lh), string.Format("角速度 {0:F1}°/s", g.angularVelocity.magnitude * Mathf.Rad2Deg), PFViewPalette.TextPrimary);
            y += lh + 6f;

            MiniLabel(new Rect(lx, y, sideW - 16f, lh), "主推轴（黄棒）", PFViewPalette.TextSecondary);
            y += lh;
            DVector3 thrustAxis = spaceGame.GetMainThrustAxisSystem(g);
            if (thrustAxis.sqrMagnitude > 1e-12)
            {
                MiniLabel(new Rect(lx, y, sideW - 16f, lh), DirectionLabel(thrustAxis), PFViewPalette.TextPrimary);
                y += lh;
                if (vel.sqrMagnitude > 1e-6f)
                {
                    float ang = Mathf.Acos(Mathf.Clamp(Vector3.Dot((Vector3)thrustAxis.normalized, vel.normalized), -1f, 1f)) * Mathf.Rad2Deg;
                    MiniLabel(new Rect(lx, y, sideW - 16f, lh), string.Format("与速度夹角 {0:F0}°", ang), PFViewPalette.TextMuted);
                }
            }
            else
            {
                MiniLabel(new Rect(lx, y, sideW - 16f, lh), "无（无引擎/全向）", PFViewPalette.TextMuted);
            }

            // 右：本组速度 + 记号图例
            y = r.y + 10f;
            MiniLabel(new Rect(rx, y, sideW - 16f, lh), "本组速度", PFViewPalette.TextSecondary);
            y += lh;
            MiniLabel(new Rect(rx, y, sideW - 16f, lh), string.Format("{0:F0} m/s", vel.magnitude), PFViewPalette.TextPrimary);
            y += lh + 6f;
            MiniLabel(new Rect(rx, y, sideW - 16f, lh), "记号图例", PFViewPalette.TextSecondary);
            y += lh;
            MiniLabel(new Rect(rx, y, sideW - 16f, lh), "P 顺行 / A 逆行", PFViewPalette.TextMuted); y += lh;
            MiniLabel(new Rect(rx, y, sideW - 16f, lh), "R 径外 / r 径内", PFViewPalette.TextMuted); y += lh;
            MiniLabel(new Rect(rx, y, sideW - 16f, lh), "N 法向 / S 反法向", PFViewPalette.TextMuted); y += lh;
            MiniLabel(new Rect(rx, y, sideW - 16f, lh), "T 主推轴", PFViewPalette.TextMuted);
        }

        /// <summary>船体系固定网格（子午线 + 纬线 + 外圈），只画正面半球段。</summary>
        private static void DrawNavballGrid(Vector2 c, float radius)
        {
            const int seg = 20;
            Color col = PFViewPalette.Grid;

            // 子午线（含 Y 轴的大圆）
            for (int k = 0; k < 4; k++)
            {
                float phi = Mathf.PI * k / 4f;
                bool has = false;
                Vector2 prev = Vector2.zero;
                for (int i = 0; i <= seg; i++)
                {
                    float th = Mathf.PI * i / seg;
                    Vector3 d = new Vector3(Mathf.Sin(th) * Mathf.Cos(phi), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(phi));
                    if (d.z <= 0.03f) { has = false; continue; }
                    Vector2 p = c + new Vector2(d.x, -d.y) * radius;
                    if (has) Widgets.DrawLine(prev, p, col, 1f);
                    prev = p; has = true;
                }
            }

            // 纬线（3 条）
            for (int k = 1; k <= 3; k++)
            {
                float lat = -Mathf.PI * 0.5f + Mathf.PI * k / 4f;
                float y = Mathf.Sin(lat), rr = Mathf.Cos(lat);
                bool has = false;
                Vector2 prev = Vector2.zero;
                for (int i = 0; i <= seg * 2; i++)
                {
                    float ph = Mathf.PI * 2f * i / (seg * 2);
                    Vector3 d = new Vector3(rr * Mathf.Cos(ph), y, rr * Mathf.Sin(ph));
                    if (d.z <= 0.03f) { has = false; continue; }
                    Vector2 p = c + new Vector2(d.x, -d.y) * radius;
                    if (has) Widgets.DrawLine(prev, p, col, 1f);
                    prev = p; has = true;
                }
            }

            // 外圈
            DrawScreenCircle(c, radius, PFViewPalette.Divider);
        }

        /// <summary>把世界方向投到球上（体轴系 + 小方点 + 字母标签；背面镜像贴边）。</summary>
        private static void DrawNavballMarker(Vector2 c, float radius, Quaternion qi, Vector3 worldDir, string label, Color col)
        {
            if (worldDir.sqrMagnitude < 1e-9f) return;

            Vector3 d = qi * worldDir.normalized;
            bool front = d.z > 0f;
            Vector2 off = new Vector2(d.x, -d.y);
            float m = off.magnitude;
            if (m < 1e-4f) off = Vector2.zero;
            else if (m > 0.96f) off = off / m * 0.96f;
            if (!front) off = -off;

            Vector2 p = c + off * radius;
            Widgets.DrawBoxSolid(new Rect(p.x - 2.5f, p.y - 2.5f, 5f, 5f), col);
            UiKit.SpacecraftUIDecor.DrawColoredLabel(new Rect(p.x + 4f, p.y - 8f, 22f, 14f), label,
                front ? col : PFViewPalette.TextMuted, GameFont.Tiny, TextAnchor.MiddleLeft);
        }

        private static void EnsureDiscTexture()
        {
            if (pfvDiscTex != null) return;
            const int size = 96;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float r = size * 0.5f;
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - r, dy = y + 0.5f - r;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(r - dist);   // 1px 羽化
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }
            tex.SetPixels(px);
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            pfvDiscTex = tex;
        }

        // ════════════════════════════════════════════════════
        //  状态行 + 小工具
        // ════════════════════════════════════════════════════

        private void DrawStatusLine()
        {
            double tick = TimeScaleManager.GetPhysicsTickSafe();
            string s = $"相机 {camDistance * ViewToMeters / 1000f:F0} km｜t {tick:F0} s｜滚轮缩放 · 右键旋转 · 中键平移 · R 复位";
            MiniLabel(rStatus, s, PFViewPalette.TextMuted);
        }

        /// <summary>列表行：单击选中（返回 true），双击聚焦（doubleClicked）。</summary>
        private static bool ListRow(Rect rr, bool selected, string text, Color color, out bool doubleClicked)
        {
            doubleClicked = false;
            bool over = Mouse.IsOver(rr);
            if (selected) Widgets.DrawBoxSolid(rr, PFViewPalette.PanelBgRaised);
            else if (over) Widgets.DrawBoxSolid(rr, PFViewPalette.PanelBgHover);

            UiKit.SpacecraftUIDecor.DrawColoredLabel(new Rect(rr.x + 4f, rr.y, rr.width - 8f, rr.height),
                text, color, GameFont.Tiny, TextAnchor.MiddleLeft);

            if (!over) return false;
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (e.clickCount >= 2) doubleClicked = true;
                e.Use();
                return true;
            }
            return false;
        }

        private static void MiniLabel(Rect r, string text, Color color, TextAnchor anchor = TextAnchor.MiddleLeft)
        {
            UiKit.SpacecraftUIDecor.DrawColoredLabel(r, text, color, GameFont.Tiny, anchor);
        }
    }
}
