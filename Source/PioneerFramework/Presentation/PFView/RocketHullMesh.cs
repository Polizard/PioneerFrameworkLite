using System.Collections.Generic;
using UnityEngine;

namespace PioneerFramework
{
    /// <summary>**一个实体的外壳网格**（渲染专用）：健康/残骸/整流罩三批 + "已被壳包住"的 partId 集合。</summary>
    internal sealed class RocketHullGeometry
    {
        public Mesh? healthy;
        public Mesh? damaged;
        public Mesh? fairing;
        public readonly HashSet<int> coveredPartIds = new();
        public int version;

        public void Destroy()
        {
            if (healthy != null) Object.Destroy(healthy);
            if (damaged != null) Object.Destroy(damaged);
            if (fairing != null) Object.Destroy(fairing);
            healthy = damaged = fairing = null;
            coveredPartIds.Clear();
        }
    }

    /// <summary>
    /// **火箭外壳 → Unity 网格（渲染专用，2026-10-30）**：把 <see cref="RocketHullCore"/> 的分段转成
    /// 3 个网格（健康 / 残骸 / 整流罩），并给出"被壳包住、不要再画零件盒"的 partId 集合。
    ///
    /// 缓存：按 `SpaceEntity.partSetVersion`（损毁→`MarkPartDestroyed`、分离/重定→`OnPartsChanged`
    /// 都会 bump ⇒ 自动重建）。网格顶点在**米制船体系**（机头 +Z、沿 −Z 堆），由绘制侧用
    /// `metersToView` 缩放，与零件盒同一套口径。
    ///
    /// 纪律：本文件只服务 PFView；不改物理/存档/战斗。`Enabled=false` 即逐字回到零件盒。
    /// </summary>
    internal static class RocketHullMesh
    {
        internal static bool Enabled = true;

        private const int MaxCachedEntities = 4096;
        private static readonly Dictionary<int, RocketHullGeometry> cache = new();

        internal static bool TryGet(int entityId, int partSetVersion, IReadOnlyList<PartSnapshot> parts,
            out RocketHullGeometry geometry)
        {
            geometry = null!;
            if (!Enabled || parts == null || parts.Count == 0) return false;

            int version = partSetVersion;
            if (cache.TryGetValue(entityId, out var cached) && cached.version == version)
            {
                geometry = cached;
                return true;
            }

            if (cache.Count > MaxCachedEntities) ClearAll();
            if (cached != null) { cached.Destroy(); cache.Remove(entityId); }

            var built = Build(parts, version);
            if (built == null) return false;
            cache[entityId] = built;
            geometry = built;
            return true;
        }

        internal static void ClearAll()
        {
            foreach (var g in cache.Values) g.Destroy();
            cache.Clear();
        }

        private static RocketHullGeometry? Build(IReadOnlyList<PartSnapshot> parts, int version)
        {
            int activeStage = 0;
            bool hasControlCore = false;

            var input = new List<RocketHullCore.Part>(parts.Count);
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                bool engine = p.IsEngine;
                bool payload = p.IsPayloadBay;
                if (p.HasControlCore) hasControlCore = true;
                if (!p.IsDestroyed && p.StageId > activeStage) activeStage = p.StageId;

                var b = p.BoundsSizeShip;
                input.Add(new RocketHullCore.Part
                {
                    stageId = p.StageId,
                    centerZ = p.PositionShip.z,
                    lengthZ = b.z,
                    halfX = b.x * 0.5,
                    halfY = b.y * 0.5,
                    destroyed = p.IsDestroyed,
                    isEngine = engine,
                    isPayload = payload,
                });
            }
            if (input.Count == 0) return null;

            // 渲染侧推断"碎片"：**没有控制核心**（火箭的设计门保证正式火箭必有核心；被抛级没有）。
            bool jettisoned = !hasControlCore;

            var res = RocketHullCore.Build(input, activeStage, jettisoned);
            if (!res.HasGeometry) return null;

            var geo = new RocketHullGeometry { version = version };
            geo.healthy = BuildMesh(res, fairingOnly: false, damaged: false, withCaps: true);
            geo.damaged = BuildMesh(res, fairingOnly: false, damaged: true, withCaps: false);
            geo.fairing = BuildMesh(res, fairingOnly: true, damaged: false, withCaps: false);

            // 被壳包住的件：除"露出来的喷管"（活动级 + 非碎片）以外的全部
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                bool exposed = !jettisoned && p.StageId == activeStage && p.IsEngine;
                if (!exposed) geo.coveredPartIds.Add(p.PartId);
            }

            return geo;
        }

        private static Mesh? BuildMesh(RocketHullCore.Result res, bool fairingOnly, bool damaged, bool withCaps)
        {
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            int bands = 0;

            for (int i = 0; i < res.bands.Count; i++)
            {
                var b = res.bands[i];
                if (b.isFairing != fairingOnly || b.destroyed != damaged) continue;
                AppendBand(verts, norms, uvs, tris, b.zTop, b.zBottom, (float)b.rTop, (float)b.rBottom);
                bands++;
            }
            if (withCaps && !fairingOnly)
            {
                for (int i = 0; i < res.caps.Count; i++)
                    AppendCap(verts, norms, uvs, tris, res.caps[i].z, (float)res.caps[i].radius);
            }
            if (verts.Count == 0) return null;
            if (bands == 0 && !(withCaps && res.caps.Count > 0)) return null;

            // 预旋转系（轴 = Y）→ 交付系（轴 = Z）：与 `PartShapeMeshes.BuildCylinder` 逐字同一条旋转，
            // 这样三角形绕序/法线朝向沿用已装箱子的口径（不用在这里重新推 winding）。
            for (int i = 0; i < verts.Count; i++)
            {
                Vector3 p = verts[i];
                verts[i] = new Vector3(p.x, -p.z, p.y);
                Vector3 n = norms[i];
                norms[i] = new Vector3(n.x, -n.z, n.y);
            }

            var mesh = new Mesh { name = "PF_RocketHull" };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>一段棱柱/棱台（预旋转系：轴 = Y，y = 体轴 z，x/z = 径向）。绕序抄 `BuildCylinder`。</summary>
        private static void AppendBand(List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris,
            double zTop, double zBottom, float rTop, float rBottom)
        {
            int sides = RocketHullCore.Sides;
            int start = verts.Count;
            float yTop = (float)zTop;
            float yBottom = (float)zBottom;

            for (int i = 0; i <= sides; i++)
            {
                float u = (float)i / sides;
                float a = u * Mathf.PI * 2f;
                float ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                var n = new Vector3(ca, 0f, sa);
                verts.Add(new Vector3(ca * rBottom, yBottom, sa * rBottom)); norms.Add(n); uvs.Add(new Vector2(u, 0f));
                verts.Add(new Vector3(ca * rTop, yTop, sa * rTop)); norms.Add(n); uvs.Add(new Vector2(u, 1f));
            }
            for (int i = 0; i < sides; i++)
            {
                int b = start + i * 2;
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                tris.Add(b + 2); tris.Add(b + 1); tris.Add(b + 3);
            }
        }

        /// <summary>底封口（法线 −Y，预旋转系；绕序抄 `PartShapeMeshes.AddCap` 的 ny &lt; 0 分支）。</summary>
        private static void AppendCap(List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris,
            double z, float radius)
        {
            int sides = RocketHullCore.Sides;
            float y = (float)z;
            int center = verts.Count;
            verts.Add(new Vector3(0f, y, 0f)); norms.Add(new Vector3(0f, -1f, 0f)); uvs.Add(new Vector2(0.5f, 0.5f));
            int ring = verts.Count;
            for (int i = 0; i <= sides; i++)
            {
                float a = (float)i / sides * Mathf.PI * 2f;
                float ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                verts.Add(new Vector3(ca * radius, y, sa * radius));
                norms.Add(new Vector3(0f, -1f, 0f));
                uvs.Add(new Vector2(ca * radius + 0.5f, sa * radius + 0.5f));
            }
            for (int i = 0; i < sides; i++)
            {
                tris.Add(center); tris.Add(ring + i + 1); tris.Add(ring + i);
            }
        }
    }
}
