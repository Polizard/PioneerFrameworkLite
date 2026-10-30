using System.Collections.Generic;
using PioneerFramework.ShipSkin;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 里"有 Map 的编队"的表示：按**地图上实在的建造情况**挤出的三层浮雕
    /// （用户 2026-10-02 裁定：结构梁 / 舱室 / 装甲 三色三档；在建的不画）。
    ///
    /// 数据链：`SpaceMapComp.GetReliefClasses()`（逐格分类，事件置脏 + 按需重建）
    ///        → `MapReliefPlan.Build`（同类矩形合并，超限降采样）
    ///        → 每档一个 `Mesh`（本文件，按 `ReliefVersion` 缓存）。
    ///
    /// 尺度：**1 格 = 1 米**（与 `MapCoordMapper` / 物理 AABB 同一口径）。网格顶点以**格**为单位，
    /// 绘制矩阵乘 `MetersToView` ⇒ 落地到视图单位；这样 `_OutlineWidth` 也是"格比例"而不是绝对米。
    /// 地图中心 = 实体局部原点（`MapCoordMapper` 约定）⇒ 浮雕直接画在 `entity.Position` 处。
    ///
    /// 竖直口径（用户 2026-10-02："上下对称"、取"墙体上下预设的高度"）：
    ///   地图平面（船体局部 y = 0）是**中面**，三档都向**上下各挤一半**；
    ///   预设半厚 = **船体厚度 10 m 的一半 = ±5 m** —— 即炮塔设计记录 §2 的那条线
    ///   （`炮塔实现记录-20260819.md`：AABB 高度 50 m = 中间 **10 m 船体** + 上下各 20 m 缓冲；
    ///   贯穿入口高度规则 `|hitY| ≤ 5m` 且入射角小 ⇒ 落点贴地 y=0 = **直射**，其余才从缓冲空间斜降）。
    ///   ⇒ 浮雕画的是"船体"这一层，命中判定里 ±25 m 的缓冲**不进画面**（它是余量，不是墙）。
    ///
    /// 回退（"宁可画旧的盒体，也不让船消失"）：无图 / 图已封存 / 房间数据未追平 / 整图无建造痕迹
    /// ⇒ 返回 false，调用方走原来的 part 盒体路径。
    /// </summary>
    public partial class MainTabWindow_PFView
    {
        /// <summary>船体半厚（米）= 10 m 船体厚度的一半。出处：炮塔设计记录 §2
        /// （"AABB 高度 50m = 内部 10m（船体）+ 上下各 20m 缓冲"），也是贯穿入口高度规则里
        /// `|hitY| ≤ 5m → 贴地直射` 的那条判据线。别再拿 AABB 的 ±25 m 当墙。</summary>
        private const float HullHalfThickness = 5f;

        // ── 视觉旋钮：三档**半高**（米，上下对称挤出）—— 这是**网格几何**口径，留在本文件 ──
        // 索引 = 分类（0 不用）：1 结构梁 / 2 舱室 / 3 装甲。
        // 结构梁 = 薄片（±1，用户 2026-10-02）；装甲 = 满船体（±5）；舱室居中。
        // ⚠ 三档**颜色**与描边宽是"画"的口径 ⇒ 已移到 `ShipModelRenderer.ReliefColors` / `ReliefOutlineWidth`（D-1）。
        private static readonly float[] ReliefHalfHeights = [0f, 1f, 4.5f, HullHalfThickness];

        private class ReliefMeshSet
        {
            public int Version;
            public Mesh?[] Meshes = new Mesh?[MapReliefPlan.Armor + 1];   // 索引 = 分类（0 空位）
        }

        private readonly Dictionary<int, ReliefMeshSet> reliefMeshes = new();
        private readonly HashSet<int> reliefLoggedMaps = new();   // 首次构建时打一行分类普查（游戏内验收用）

        /// <summary>蒙皮在"有 Map 浮雕"路径上的首次命中读数（只打一次；确认材质真的被这条路径用了）。</summary>
        private static bool loggedReliefSkin;

        /// <summary>
        /// 画本实体的地图浮雕。返回 true = 已画（调用方跳过 part 盒体）；false = 画不了（调用方回退）。
        /// </summary>
        private bool TryDrawMapRelief(SpaceEntity entity, Vector3 eVp, Quaternion gRot, float scale)
        {
            if (!debugMapRelief || entity == null || entity.MapId < 0 || entity.IsMapArchived)
                return false;

            Map? map = SpaceMapGenerator.GetEntityMap(entity);
            if (map == null)
                return false;

            var comp = map.GetComponent<SpaceMapComp>();
            if (comp == null)
                return false;

            byte[]? classes = comp.GetReliefClasses();
            if (classes == null || !comp.ReliefHasContent)
                return false;

            ReliefMeshSet set = GetOrBuildReliefMeshes(map, comp, classes);
            if (set == null)
                return false;

            // 2026-10-20：三平面工业装甲 + 局部阵营涂装；关闭时保留旧三色。
            Material? skin = ShipSkinSettings.Enabled ? ShipSkinAssets.Material : null;
            Material mat = skin ?? (ToonMat ?? renderMat);
            if (mat == null)
                return false;
            Color? tint = skin == null ? (Color?)null : ShipSkinPalette.PartTint(entity.FactionId);
            if (skin != null && !loggedReliefSkin)
            {
                loggedReliefSkin = true;
                Log.Message("[PFView·蒙皮] 首次用于有 Map 浮雕：贴图=" + (ShipSkinAssets.TextureLoaded ? "OK" : "缺失→白图")
                    + "｜" + ShipSkinAssets.StatusText);
            }

            // 画（矩阵：网格以"格"为单位 ⇒ 缩放 = 米→视图、debugScaleParts 同倍；三档颜色；描边宽）
            // 全在 `ShipModelRenderer.DrawRelief`（D-1：与场景无关的绘制段）——本函数只管**取数据**
            // （地图/分类/网格缓存）与**回退判定**。
            ShipModelRenderer.DrawRelief(set.Meshes, eVp, gRot, scale, mat, matBlock,
                skin == null && ToonMat != null, MetersToView, SceneLayer, tint);
            return true;
        }

        /// <summary>按 `ReliefVersion` 取（或重建）本图的网格集。</summary>
        private ReliefMeshSet GetOrBuildReliefMeshes(Map map, SpaceMapComp comp, byte[] classes)
        {
            if (reliefMeshes.TryGetValue(map.uniqueID, out ReliefMeshSet cached)
                && cached.Version == comp.ReliefVersion)
                return cached;

            List<MapReliefPlan.Box> boxes = MapReliefPlan.Build(classes, map.Size.x, map.Size.z);

            // 网格坐标以"格"为单位、地图中心 = 实体原点（MapCoordMapper 约定）。
            Vector3 center = new Vector3(map.Center.x, 0f, map.Center.z);
            var meshes = new Mesh?[MapReliefPlan.Armor + 1];
            int[] counts = new int[MapReliefPlan.Armor + 1];
            for (int i = 0; i < boxes.Count; i++)
                counts[boxes[i].cls]++;

            for (int c = 1; c < meshes.Length; c++)
            {
                if (counts[c] == 0) continue;
                var subset = new List<MapReliefPlan.Box>(counts[c]);
                for (int i = 0; i < boxes.Count; i++)
                    if (boxes[i].cls == c) subset.Add(boxes[i]);
                meshes[c] = BuildReliefMesh(subset, center, ReliefHalfHeights[c]);
            }

            if (cached != null)
                DestroyReliefMeshSet(cached);
            PruneReliefMeshes(map.uniqueID);

            var set = new ReliefMeshSet { Version = comp.ReliefVersion, Meshes = meshes };
            reliefMeshes[map.uniqueID] = set;

            if (reliefLoggedMaps.Add(map.uniqueID))
            {
                int n1 = 0, n2 = 0, n3 = 0;
                for (int i = 0; i < classes.Length; i++)
                {
                    byte cl = classes[i];
                    if (cl == MapReliefPlan.Beam) n1++;
                    else if (cl == MapReliefPlan.Cabin) n2++;
                    else if (cl == MapReliefPlan.Armor) n3++;
                }
                Log.Message($"[PFView] 地图浮雕 Map={map.uniqueID} {map.Size.x}x{map.Size.z}：结构梁 {n1} 格 / 舱室 {n2} 格 / 装甲 {n3} 格，盒体 {boxes.Count}（梁{counts[1]}/舱{counts[2]}/装{counts[3]}）");
            }
            return set;
        }

        /// <summary>缓存瘦身：地图已不在内存（销毁/卸载）的条目连网格一起销毁。刚加过新条目时顺手清一次。</summary>
        private void PruneReliefMeshes(int keepMapId)
        {
            if (reliefMeshes.Count <= 4)
                return;

            reliefDeadBuf.Clear();
            foreach (var kv in reliefMeshes)
            {
                if (kv.Key == keepMapId) continue;
                if (SpaceMapGenerator.FindPocketMap(kv.Key) == null)
                    reliefDeadBuf.Add(kv.Key);
            }
            for (int i = 0; i < reliefDeadBuf.Count; i++)
            {
                DestroyReliefMeshSet(reliefMeshes[reliefDeadBuf[i]]);
                reliefMeshes.Remove(reliefDeadBuf[i]);
                reliefLoggedMaps.Remove(reliefDeadBuf[i]);
            }
            reliefDeadBuf.Clear();
        }

        private readonly List<int> reliefDeadBuf = new();

        private static void DestroyReliefMeshSet(ReliefMeshSet set)
        {
            if (set?.Meshes == null) return;
            for (int i = 0; i < set.Meshes.Length; i++)
                if (set.Meshes[i] != null)
                    Object.Destroy(set.Meshes[i]);
        }

        /// <summary>窗口关闭时把全部浮雕网格销毁（运行时 Mesh 不销毁会跟着会话泄漏）。</summary>
        private void DestroyAllReliefMeshes()
        {
            foreach (var kv in reliefMeshes)
                DestroyReliefMeshSet(kv.Value);
            reliefMeshes.Clear();
            reliefLoggedMaps.Clear();
        }

        // ── 盒体清单 → 网格 ──

        // 六面四角顺序（索引进 8 角表）与三角形模式：**逐字照抄** BuildCubeMesh 那个已验证的立方体网格
        // ——面朝外的绕序不能凭感觉推（推反了面就被背面剔除掉，画面上是"缺面"）。
        private static readonly int[][] ReliefFaceCorners =
        [[0, 1, 2, 3],   // -Z
            [4, 5, 6, 7],   // +Z
            [0, 1, 5, 4],   // -Y
            [2, 3, 7, 6],   // +Y
            [1, 2, 6, 5],   // +X
            [3, 0, 4, 7],   // -X
        ];

        private static readonly Vector3[] ReliefFaceNormals =
        [new(0f, 0f, -1f), new(0f, 0f, 1f),
            new(0f, -1f, 0f), new(0f, 1f, 0f),
            new(1f, 0f, 0f), new(-1f, 0f, 0f),
        ];

        /// <summary>
        /// 盒体清单 → 一个 Mesh（24 顶点/盒：每个面独立四角 ⇒ 法线硬边，方块看起来是方块而不是揉过的球）。
        /// 单位 = **格**（调用方用矩阵缩放到视图单位）。竖直方向**上下对称**：y: −half → +half
        /// （地图平面 = 船体局部 y=0 = 中面；±half 见 <see cref="ReliefHalfHeights"/>）。
        /// </summary>
        private static Mesh BuildReliefMesh(List<MapReliefPlan.Box> boxes, Vector3 center, float halfHeightCells)
        {
            int n = boxes.Count;
            var verts = new Vector3[n * 24];
            var norms = new Vector3[n * 24];
            var tris = new int[n * 36];
            var corners = new Vector3[8];
            int vi = 0, ti = 0;

            for (int i = 0; i < n; i++)
            {
                MapReliefPlan.Box b = boxes[i];
                float x0 = b.x0 - center.x - 0.5f;
                float x1 = b.x1 - center.x + 0.5f;
                float z0 = b.z0 - center.z - 0.5f;
                float z1 = b.z1 - center.z + 0.5f;
                float y0 = -halfHeightCells;
                float y1 = halfHeightCells;

                corners[0] = new Vector3(x0, y0, z0);
                corners[1] = new Vector3(x1, y0, z0);
                corners[2] = new Vector3(x1, y1, z0);
                corners[3] = new Vector3(x0, y1, z0);
                corners[4] = new Vector3(x0, y0, z1);
                corners[5] = new Vector3(x1, y0, z1);
                corners[6] = new Vector3(x1, y1, z1);
                corners[7] = new Vector3(x0, y1, z1);

                for (int f = 0; f < 6; f++)
                {
                    int[] quad = ReliefFaceCorners[f];
                    Vector3 nrm = ReliefFaceNormals[f];
                    int bv = vi;
                    for (int q = 0; q < 4; q++)
                    {
                        verts[vi] = corners[quad[q]];
                        norms[vi] = nrm;
                        vi++;
                    }
                    if (f == 0)
                    {
                        // -Z 面：0,2,1 / 0,3,2（见 BuildCubeMesh）
                        tris[ti++] = bv; tris[ti++] = bv + 2; tris[ti++] = bv + 1;
                        tris[ti++] = bv; tris[ti++] = bv + 3; tris[ti++] = bv + 2;
                    }
                    else
                    {
                        // 其余五面：0,1,2 / 0,2,3
                        tris[ti++] = bv; tris[ti++] = bv + 1; tris[ti++] = bv + 2;
                        tris[ti++] = bv; tris[ti++] = bv + 2; tris[ti++] = bv + 3;
                    }
                }
            }

            var mesh = new Mesh { name = "PFViewRelief" };
            if (verts.Length > 65000)
                mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.normals = norms;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
