using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **世界图标网格合批**（2026-10-16，"渲染线优化审查"第一、二批：轨道点 / 点阵画笔 / σ 圈 / 编队图标 / PFView 零件）。
    ///
    /// ## 为什么是"拼 mesh"而不是"实例化"
    /// 1.7 的 `Graphics` 只有 `DrawMesh` 族、**没有 instanced 版本**（核过 UnityStubs 表面）⇒ 合批 =
    /// 自己把每个实例按各自矩阵**变换到视图空间**后拼进同一个 mesh，再用**单位矩阵**一次性提交。
    ///
    /// ## 三条 1.7 实测语义（都读过 UnityStubs 源码）
    /// ① `Graphics.DrawMesh` = `new MeshDrawCommand(mesh.EngineMesh, …)` → `MeshDrawSubmission.Enqueue`：
    ///    **MPB 在入队时被拷贝**（改 MPB 安全）、**网格是引用**（改网格 = 改已入队那一笔）
    ///    ⇒ **按批给网格**：槽位随批序递增（同帧内绝不重复用同一个 mesh）+ 帧代双缓冲（帧号驱动换代）。
    /// ② `Mesh.RebuildFromCompat` **只采用"数组长度 == 顶点数"的顶点属性**（法线/切线/颜色/UV 各自判长度）
    ///    ⇒ 合批网格必须**逐实例把源网格的属性也拼进去**：`MaterialPool` 的图层材质是拿 UV 采 `_MainTex` 的
    ///    （2026-10-16 实测事故：只拼位置 ⇒ 全批 UV=(0,0) ⇒ 图标贴图采样到透明纹素 ⇒ **图标整批看不见**；
    ///    轨道点用的是白贴图所以看不出问题——"看起来没事"不等于没事）。
    /// ③ `Mesh.vertices/uv/normals/colors32` 每次读都**分配一份拷贝** ⇒ 源网格几何按网格缓存，只读一次。
    ///
    /// ## 用法（一次 `Flush` = 一批 = 一种颜色/材质/层；**每批必须以 `Reset` 开始**）
    /// <code>
    /// WorldQuadBatch.Reset();
    /// WorldQuadBatch.AddQuad(matrix);          // 或 AddMesh(任意单 submesh 网格, matrix)
    /// …
    /// WorldQuadBatch.Flush(material, propertyBlock, layer);
    /// </code>
    /// · 批间不可交叠（一次只有一批是"开着"的）；各列表与网格**全部复用**（每帧零分配，P52 同口径）；
    /// · 批内绘制顺序 = 加入顺序（与原来"逐个提交"的先后一致）；跨批仍按各自调用顺序；
    /// · 槽位按**引擎帧号**归零（`UnityRuntime.FrameCount`）⇒ 任何调用点都不需要自己记帧。
    /// · 顶点属性（UV/法线/颜色）由**本批第一个实例**的源网格锚定：源没有则该批不带（引擎会忽略长度不匹配的数组）；
    ///   后续实例若缺该属性则补零——**同一批尽量只用同一种源网格**（现在都是这么用的）。
    /// </summary>
    internal static class WorldQuadBatch
    {
        /// <summary>单帧批次数上限（安全阀）：实际 ~10；超出则循环复用槽位（理论碰撞窗口 = 同一帧 64 批以上）。</summary>
        private const int MaxSlotsPerFrame = 64;

        private static readonly List<Vector3> verts = new(4096);
        private static readonly List<int> tris = new(6144);
        private static readonly List<Vector2> uvs = new(4096);
        private static readonly List<Vector3> normals = new(4096);
        private static readonly List<Color32> colors = new(4096);
        private static readonly List<Mesh?> pool = new(16);   // 索引 = 槽位 × 2 + 帧代

        /// <summary>源网格几何缓存（各属性数组都是"读一次"的拷贝）。</summary>
        private static readonly Dictionary<Mesh, MeshGeometry> geometryCache = new();

        private sealed class MeshGeometry
        {
            internal Vector3[] verts = [];
            internal int[] tris = [];
            internal Vector2[]? uv;          // uv0（源没有则 null）
            internal Vector3[]? normals;     // 源没有则 null
            internal Color32[]? colors;      // 源没有则 null
        }

        private static int slotThisFrame;
        private static int generation;
        private static int lastFrame = int.MinValue;

        private static bool batchHasUv;
        private static bool batchHasNormals;
        private static bool batchHasColors;

        /// <summary>当前批里并入的**网格实例数**（读数用；空批 = 0）。</summary>
        internal static int ItemCount { get; private set; }

        /// <summary>清空当前批的缓冲（开始新的一批）。**帧号变化时**在此归零槽位并换代。</summary>
        internal static void Reset()
        {
            EnsureFrame();
            verts.Clear();
            tris.Clear();
            uvs.Clear();
            normals.Clear();
            colors.Clear();
            batchHasUv = batchHasNormals = batchHasColors = false;
            ItemCount = 0;
        }

        /// <summary>把一个四边形（`MeshPool.plane10` 的几何，按给定矩阵变换后）加入当前批。</summary>
        internal static void AddQuad(Matrix4x4 matrix) => AddMesh(MeshPool.plane10, matrix);

        /// <summary>
        /// 把一个源网格实例（按给定矩阵变换后）加入当前批。
        /// ⚠ 源网格须为**单 submesh**（本批只写 submesh 0）；几何从 <see cref="geometryCache"/> 取（不重复拷贝）。
        /// </summary>
        internal static void AddMesh(Mesh? source, Matrix4x4 matrix)
        {
            if (source == null) return;
            if (!geometryCache.TryGetValue(source, out MeshGeometry? g))
            {
                g = new MeshGeometry
                {
                    verts = source.vertices,
                    tris = source.triangles,
                    uv = source.uv,
                    normals = source.normals,
                    colors = source.colors32,
                };
                geometryCache[source] = g;
            }

            if (ItemCount == 0)   // 本批属性由第一个实例锚定
            {
                batchHasUv = g.uv is { Length: > 0 };
                batchHasNormals = g.normals is { Length: > 0 };
                batchHasColors = g.colors is { Length: > 0 };
            }

            int baseIndex = verts.Count;
            int n = g.verts.Length;
            for (int i = 0; i < n; i++)
                verts.Add(matrix.MultiplyPoint3x4(g.verts[i]));
            for (int i = 0; i < g.tris.Length; i++)
                tris.Add(g.tris[i] + baseIndex);

            if (batchHasUv) AppendOrZero(uvs, g.uv, n);
            if (batchHasNormals) AppendOrZero(normals, g.normals, n);
            if (batchHasColors) AppendOrZero(colors, g.colors, n);

            ItemCount++;
        }

        /// <summary>提交当前批（空批直接返回，不产生 draw）。</summary>
        internal static void Flush(Material material, MaterialPropertyBlock? properties, int layer)
        {
            if (verts.Count == 0) return;

            Mesh mesh = AcquireMesh();
            mesh.Clear();
            if (verts.Count > 65000 && mesh.indexFormat != IndexFormat.UInt32)
                mesh.indexFormat = IndexFormat.UInt32;

            mesh.SetVertices(verts);
            // 顶点属性：长度必须与顶点数一致引擎才采用（语义②）；正常路径一定一致，这里再守一道。
            if (batchHasUv && uvs.Count == verts.Count) mesh.SetUVs(0, uvs, 0, uvs.Count);
            if (batchHasNormals && normals.Count == verts.Count) mesh.SetNormals(normals);
            if (batchHasColors && colors.Count == verts.Count) mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();      // 视锥剔除要用（与其它自建 mesh 同口径）
            Graphics.DrawMesh(mesh, Matrix4x4.identity, material, layer, null, 0, properties);
        }

        private static void AppendOrZero<T>(List<T> dst, T[]? src, int count)
        {
            int have = src?.Length ?? 0;
            for (int i = 0; i < count; i++)
                dst.Add(i < have ? src![i] : default!);
        }

        private static void EnsureFrame()
        {
            var runtime = Waylith.UnityCompat.UnityRuntime.Current;
            if (runtime == null) return;                     // 无引擎（headless）：不换帧，槽位靠安全阀封顶
            int frame = runtime.FrameCount;
            if (frame == lastFrame) return;

            lastFrame = frame;
            slotThisFrame = 0;
            generation ^= 1;
        }

        private static Mesh AcquireMesh()
        {
            int poolIndex = (slotThisFrame % MaxSlotsPerFrame) * 2 + generation;
            slotThisFrame++;

            while (pool.Count <= poolIndex) pool.Add(null);
            Mesh? mesh = pool[poolIndex];
            if (mesh == null)
            {
                mesh = new Mesh { name = "PF_WorldQuadBatch" };
                pool[poolIndex] = mesh;
            }
            return mesh;
        }
    }
}
