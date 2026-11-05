using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using RimWorld.Planet;
using PioneerFramework.Launch;   // GetSurfaceState（天体→PlanetLayer）

namespace PioneerFramework
{
    /// <summary>
    /// **PFView 定居点层（D-2，2026-10-12 用户裁定「做：实时画一层（含夜灯）」）** —— 渲染那一半。
    ///
    /// 口径：
    /// · **贴地**：与烘焙补丁 / 基底球**共用同一套 TRS**（`renderPos + selfRot × 固连方向 × renderR`）
    ///   ——补丁怎么转它就怎么转 ⇒ 定居点落在**自己那块地**上（⛔ `GetTileCenter` 返回的是"layer 半径"长向量 ⇒ 必须归一化，
    ///   这是 2026-10-11 自检假红 494,998 km 的同一个坑）；
    /// · **昼夜**：夜面（背阳）点暖色城市灯，昼面给中性小点（判据 = `PFViewSettlementLayer.IsNightSide`）；
    /// · **不烘**：每帧现取 `Find.WorldObjects.Settlements`（动态数据不进静态图）；
    /// · **朝向**：复用太阳圆片那套 `QuadFacingAxis`（quad 正面 = −Z ⇒ +Z 背对相机；2026-10-11 踩过背面剔除）；
    /// · **遮挡**：材质走 `ShaderDatabase.Transparent`（不写深度）⇒ 行星（不透明、写深度）自然挡住背面的点、
    ///   球面也把"扎进球里"的那半片切掉 ⇒ 不需要额外剔除逻辑。
    ///
    /// 纯算数（尺寸 / 昼夜 / 轮廓）在 `PFViewSettlementLayer`；本文件只管把矩阵与绘制发出去。
    /// </summary>
    public partial class MainTabWindow_PFView
    {
        // ── 视觉旋钮 ──
        /// <summary>昼面标记色（中性、偏暗——白天城市从太空看本来就很淡）。</summary>
        private static readonly Color SettlementDayColor = new(0.88f, 0.88f, 0.92f, 0.70f);
        /// <summary>夜面城市灯光色（定色暖黄）——**科技差走"面积/密度/亮度"三轴**（用户 2026-10-12 口径），
        /// 不靠色温（先前那版琥珀↔冷白的插值已按口径收掉）。</summary>
        private static readonly Color SettlementNightColor = new(1.00f, 0.80f, 0.45f);

        /// <summary>工具条上的 A/B 勾选（默认开）。</summary>
        private bool debugSettlements = true;

        private static Mesh? settlementMesh;
        private static Material? settlementMat;
        private static readonly Texture2D?[] settlementTexByTech = new Texture2D?[8];   // 索引 = (int)TechLevel
        private static readonly MaterialPropertyBlock settlementBlock = new();

        /// <summary>每颗天体的读数限频状态（**必须按天体分开**：地球 92 座 / 月球 0 座交替绘制，
        /// 共用一份"上次数量"会每帧判成"数量变了" ⇒ 实测 859 行 + 每条 10 行栈 / 45 秒，见 2026-10-17 日志）。</summary>
        private struct SettlementLogState { public double tick; public int count; }

        private readonly Dictionary<(System.Guid, int), SettlementLogState> settlementLogByBody = new();

        /// <summary>
        /// 把**这一颗天体**上的定居点画到它的地表（实时；不烘）。由 `DrawCelestialBodiesCore` 在对该天体画完
        /// 基底球 + 补丁之后、大气壳之前调用（⇒ 大气壳会自然罩在它上面）。
        /// </summary>
        private void DrawSettlementOverlay(CelestialBodyInfo body, PlanetLayer? layer,
            Vector3 renderPos, float renderR, Quaternion selfRot, Vector3 toSun)
        {
            if (!debugSettlements || layer == null || layer.TilesCount <= 0) return;

            var mesh = GetSettlementMesh();
            var mat = GetSettlementMaterial();
            if (mesh == null || mat == null) return;

            float size = PFViewSettlementLayer.MarkerSizeMeters(layer.AverageTileSize) * MetersToView;
            if (size <= 1e-4f) return;

            int shown = 0, night = 0;
            float techMin = 2f, techMax = -1f;

            var settlements = Find.WorldObjects.Settlements;
            for (int i = 0; i < settlements.Count; i++)
            {
                var s = settlements[i];
                if (s == null || s.Destroyed) continue;
                PlanetTile t = s.Tile;
                if (!t.Valid) continue;
                if (!ReferenceEquals(t.Layer, layer)) continue;      // 只画本层（地球层 / 月球层各画各的）

                // ⚠ 必须归一化：`GetTileCenter` 是"layer 半径"长的向量（地球层 ≈100、月球层 ≈30）
                Vector3 dirFixed = layer.GetTileCenter(t.tileId).normalized;
                Vector3 dirView = selfRot * dirFixed;                 // 固连 → 视图系（与补丁同一个旋转）
                // **贴地**（切平面，用户 2026-10-12：「它们应该贴在地表，但不被地表遮挡」）：
                // 法线 = 外法线 dirView ⇒ 平片躺在当地的水平面上（不再是"始终朝向相机"的 billboard）。
                // ⚠ 抬升量 = 球面在平片角点处的下沉 × 2 + 深度余量（见 `TangentLift`）⇒ 贴地但不被地表咬进去。
                float lift = PFViewSettlementLayer.TangentLift(size, renderR);
                Vector3 pos = renderPos + dirView * (renderR + lift);

                // 派系科技水平 ⇒ **三轴**（用户 2026-10-12 口径）：面积/密度烘进该档的贴图、亮度进颜色 alpha
                TechLevel techLevel = s.Faction?.def?.techLevel ?? TechLevel.Undefined;
                float tech = PFViewSettlementLayer.TechIntensity(techLevel);
                if (tech < techMin) techMin = tech;
                if (tech > techMax) techMax = tech;

                Texture2D? techTex = GetSettlementTexture(techLevel);
                if (techTex == null) continue;

                bool isNight = PFViewSettlementLayer.IsNightSide(dirView, toSun);
                Color color = isNight
                    ? SettlementNightColor with { a = PFViewSettlementLayer.LightAlphaFor(tech) }
                    : SettlementDayColor;

                // 切平面姿态：局部 +Z = 外法线（`DiscQuadGeometry` 正反两面都发 ⇒ 从太空中外侧也看得见）
                Quaternion rot = Quaternion.LookRotation(dirView,
                    Mathf.Abs(dirView.y) < 0.9f ? Vector3.up : Vector3.forward);
                Matrix4x4 mtx = Matrix4x4.TRS(pos, rot, Vector3.one * size);
                settlementBlock.SetColor("_Color", color);
                settlementBlock.SetTexture("_MainTex", techTex);      // 贴图逐点不同（按科技档）⇒ 走 MPB
                Graphics.DrawMesh(mesh, mtx, mat, SceneLayer, null, 0, settlementBlock);

                shown++;
                if (isNight) night++;
            }

            LogSettlementReadout(body, shown, night, size, techMin, techMax);
        }

        /// <summary>读数（限频：**该天体**数量变化即打，或每 300 物理秒一行）——"画了几座、几座亮着、多大、科技亮度区间"。</summary>
        private void LogSettlementReadout(CelestialBodyInfo body, int shown, int night, float sizeViewUnits,
            float techMin, float techMax)
        {
            double tick = TimeScaleManager.GetPhysicsTickSafe();
            var key = (celestialRenderRuntime?.WorldSessionId ?? System.Guid.Empty, body.bodyId);
            if (settlementLogByBody.TryGetValue(key, out var st)
                && shown == st.count && tick - st.tick < 300.0) return;
            settlementLogByBody[key] = new SettlementLogState { tick = tick, count = shown };
            Log.Message($"[PFView·定居点] body={body.defName} 定居点 {shown} 座（夜面 {night} 座亮灯）｜标记 {sizeViewUnits:F0} 视图单位（{sizeViewUnits / MetersToView / 1000f:F0} km）｜科技强度 {(techMax < 0f ? 0f : techMin):F2}~{(techMax < 0f ? 0f : techMax):F2}（覆盖/密度/亮度三轴）");
        }

        /// <summary>标记网格使用带 UV 的双面四边形，保持径向贴图和两面可见。</summary>
        private static Mesh? GetSettlementMesh()
        {
            if (settlementMesh != null) return settlementMesh;
            try
            {
                PFViewQuadGeometry.Create(out var verts, out var uvs, out var tris);
                var m = new Mesh { name = "PF_SettlementQuad" };
                m.vertices = verts;
                m.uv = uvs;
                m.triangles = tris;
                m.RecalculateNormals();
                settlementMesh = m;
                return m;
            }
            catch (System.Exception ex)
            {
                Log.Warning("[PFView·定居点] 标记网格创建失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 标记材质：原版透明 shader（不写深度；行星自然遮挡背面的点）。
        /// ⚠ 贴图**逐点不同**（按科技档选）⇒ 材质里只放白图，真贴图逐次走 `MaterialPropertyBlock` 的 `_MainTex`
        /// （MPB 能覆盖贴图，与 `_Color` 同一路）。
        /// </summary>
        private static Material? GetSettlementMaterial()
        {
            if (settlementMat != null) return settlementMat;
            settlementMat = new Material(ShaderDatabase.Transparent)
            {
                mainTexture = BaseContent.WhiteTex,
                color = Color.white,
            };
            return settlementMat;
        }

        /// <summary>
        /// **弥散噪点贴图（按科技档各一张）**：64×64，**每一档首次用到时生成一次**、之后一直贴着用
        /// （用户 2026-10-12 口径：「**预先搞一个噪点贴图贴上去，而不是吃 CPU 生成噪点**」⇒ 每帧成本 = 0；
        /// 8 档 × 4096 texel 的一次性哈希 = 可忽略）。
        ///
        /// **派系科技差 = 三轴**（用户 2026-10-12 口径）：**覆盖面积**（`CoverageFor`：灯光铺开的半径）
        /// · **密度**（`DensityFor`：8×8 里亮格的比例）烘进本图；**亮度**走调用方的颜色 alpha。
        /// 开 mipmap（远看碎光被平均值化，不闪）。
        /// </summary>
        private static Texture2D? GetSettlementTexture(TechLevel tech)
        {
            int idx = (int)tech;
            if (idx < 0 || idx >= settlementTexByTech.Length) idx = 0;      // Undefined = 0
            if (settlementTexByTech[idx] != null) return settlementTexByTech[idx];

            try
            {
                float intensity = PFViewSettlementLayer.TechIntensity(tech);
                float coverage = PFViewSettlementLayer.CoverageFor(intensity);
                float density = PFViewSettlementLayer.DensityFor(intensity);

                const int N = 64;
                var tex = new Texture2D(N, N, TextureFormat.RGBA32, true) { name = "PF_SettlementSpeck_" + tech };
                var px = new Color32[N * N];
                const float half = (N - 1) * 0.5f;
                for (int y = 0; y < N; y++)
                {
                    for (int x = 0; x < N; x++)
                    {
                        float dx = (x - half) / half;
                        float dy = (y - half) / half;
                        byte a = (byte)(255f * PFViewSettlementLayer.SpeckleAlphaAt(
                            dx, dy, PFViewSettlementLayer.SpeckSeed + idx * 101, coverage, density));
                        px[y * N + x] = new Color32(255, 255, 255, a);   // 白图 ⇒ 颜色全靠 MPB 的 _Color
                    }
                }
                tex.SetPixels32(px);
                tex.Apply(true);                       // true = 同时生成 mipmap
                tex.wrapMode = TextureWrapMode.Clamp;
                settlementTexByTech[idx] = tex;
                return tex;
            }
            catch (System.Exception ex)
            {
                Log.Warning("[PFView·定居点] 噪点贴图创建失败（" + tech + "）：" + ex.Message);
                return null;
            }
        }
    }
}
