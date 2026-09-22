using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **选 tile 主动降落的窗口**（2026-09-22 用户裁定："接近天体时选一个 tile、消耗一定 Δv、瞬间完成降落"）。
    ///
    /// 交互三步：① 选**目标天体**（默认 = 本编队当前中心天体，若无地表则退到第一个有地表的天体）
    /// ② 在**星球视图上点选落点** ③ 看报价（经纬度 / 地表距离 / Δv / 判定）后按「降落」结算。
    ///
    /// ## 为什么不用自写"球面拾取"
    /// 原版 `Find.WorldSelector.SelectedTile` 就是玩家点选的那个 tile（悬停方块高亮也是原版
    /// `WorldDrawLayer_MouseTile` 免费给的）⇒ 本窗口只**读**它，不自己算屏幕射线与球面求交。
    ///
    /// ## ⚠ 落点必须与目标天体**同层**
    /// tile 索引在另一层上"看起来合法"（越界检查会放行），落点却会落在**另一个天体**上。
    /// 该错配由 `SpaceGameComp.TryQuoteLanding` 硬挡（比 LayerDef），本窗口负责在切天体时
    /// 顺手把地表视图也切过去，并在被拒时把原因显示出来。
    /// </summary>
    public class Window_LandingTarget : Window
    {
        private const float RowH = 26f;
        private const float Gap = 6f;
        private static readonly Color Dim = new Color(0.62f, 0.68f, 0.74f, 1f);
        private static readonly Color OkColor = new Color(0.35f, 1f, 0.55f, 1f);
        private static readonly Color BadColor = new Color(1f, 0.45f, 0.35f, 1f);

        private readonly int groupId;
        private int targetBodyId;

        public Window_LandingTarget(SpaceGameComp sg, SpaceEntityGroup group)
        {
            groupId = group.GroupId;

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var own = mgr?.GetBodyById(group.CelestialBodyId);
            targetBodyId = own != null && own.hasSurface ? group.CelestialBodyId : FirstLandableBody(mgr);

            forcePause = true;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            draggable = true;
        }

        public override Vector2 InitialSize => new Vector2(620f, 340f);

        private static int FirstLandableBody(CelestialBodyManager? mgr)
        {
            if (mgr != null)
            {
                foreach (var b in mgr.GetAllBodies())
                    if (b.hasSurface) return b.bodyId;
            }
            return 1;   // 地球（XML bodyId）
        }

        public override void DoWindowContents(Rect inRect)
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var group = sg?.GetGroupById(groupId);
            if (sg == null || mgr == null || group == null || !group.IsActive) { Close(); return; }

            Text.Font = GameFont.Small;
            float x = 0f;
            float w = inRect.width;
            float y = 0f;

            Widgets.Label(new Rect(x, y, w, RowH),
                string.Format("主动降落 · Group {0}（付出 Δv，换一次保证安全的瞬间降落）", group.GroupId));
            y += RowH + Gap;

            // ── 目标天体（Lite 只有地球/月球；只有 hasSurface 的才列）──
            Widgets.Label(new Rect(x, y, 72f, RowH), "目标天体");
            float bx = x + 74f;
            foreach (var b in mgr.GetAllBodies())
            {
                if (!b.hasSurface) continue;
                string label = (b.label ?? b.defName) + (b.bodyId == targetBodyId ? " ●" : "");
                if (Widgets.ButtonText(new Rect(bx, y, 96f, RowH), label))
                {
                    targetBodyId = b.bodyId;
                    SwitchSurfaceView(mgr, b.bodyId);   // 顺手切地表视图 ⇒ 点选才落在对的 layer 上
                }
                bx += 102f;
            }
            y += RowH + Gap;

            // ── 当前选中落点 + 报价（每帧现算；被拒时把原因原样显示）──
            PlanetTile planetTile = Find.WorldSelector.SelectedTile;
            if (sg.TryQuoteLanding(group, targetBodyId, planetTile, out var q, out var est,
                    out var bodyName, out var spot, out var reason))
            {
                Widgets.Label(new Rect(x, y, w, RowH),
                    string.Format("落点：{0} {1}（tile {2}）", bodyName, spot, planetTile.tileId));
                y += RowH;
                Widgets.Label(new Rect(x, y, w, RowH), string.Format(
                    "地表距离 {0:F0} km / 上界 {1:F0} km　｜　Δv 报价 {2:F0} m/s（工质 {3:F0} kg）　｜　剩余 Δv {4:F0} m/s",
                    q.surfaceDistance / 1000.0, q.maxDistance / 1000.0, q.deltaV, est.RequiredPropellant,
                    sg.GetGroupDeltaV(group).remainingDv));
                y += RowH + Gap;

                GUI.color = OkColor;
                Widgets.Label(new Rect(x, y, w, RowH), "判定：可落");
                GUI.color = Color.white;
                y += RowH + Gap;

                if (Widgets.ButtonText(new Rect(x, y, w, 32f),
                        string.Format("降落（付出 Δv {0:F0} m/s）", q.deltaV)))
                {
                    if (sg.TryLandAtTile(group, targetBodyId, planetTile, out var execReason))
                        Close();
                    else
                        Messages.Message(execReason ?? "降落被拒", MessageTypeDefOf.RejectInput);
                }
                y += 32f + Gap;
            }
            else
            {
                GUI.color = BadColor;
                Widgets.Label(new Rect(x, y, w, RowH), string.Format("判定：{0}", reason));
                GUI.color = Color.white;
                y += RowH + Gap;
                y += 32f;   // 给"降落"按钮留位（被拒时不画按钮，保持高度稳定）
            }

            GUI.color = Dim;
            Widgets.Label(new Rect(x, y, w, RowH * 2f),
                "在星球视图上点选落点（原版悬停高亮）。落点必须与目标天体同层；\n" +
                "若点的是另一层地表，上面会给出提示 —— 先按右下角把视图切到目标天体。");
            GUI.color = Color.white;

            if (Widgets.ButtonText(new Rect(x, inRect.height - RowH, w, RowH), "把视图切到目标天体"))
                SwitchSurfaceView(mgr, targetBodyId);
        }

        /// <summary>把星球视图切到该天体的地表层（与「查看天体」同一个做法，含强制重绘）。</summary>
        private static void SwitchSurfaceView(CelestialBodyManager mgr, int bodyId)
        {
            var layer = Launch.GetSurfaceState.GetLayerForBody(mgr, bodyId);
            if (layer == null || layer.IsSelected) return;
            PlanetLayer.Selected = layer;
            Find.World.renderer.SetDirty<WorldDrawLayer_Terrain>(layer);
        }
    }
}
