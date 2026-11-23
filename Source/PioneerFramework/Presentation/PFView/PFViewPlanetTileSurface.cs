using System;
using System.Collections.Generic;
using System.Diagnostics;
using PioneerFramework.Celestial;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 的**实时逐格行星表面**（2026-11-08，A 方案第一批）：设计参考「天体运行」的逐格球面，
    /// 但保留我们自己的口径 —— 一格一张 biome 贴图，数据区球冠来自烘焙器同一套 ComputeCap。
    ///
    /// 解决什么：现在没有烘焙补丁的天体近看只有一张平铺底图。本层把原版世界地图那套分格实时铺到球上，
    /// 于是不需要手动烘焙也有真实地表，且地块/世界变化后自动跟着变。
    ///
    /// 与既有两层的分工（不要改成三层同时画）：
    /// - 有烘焙补丁 => 画补丁（带河/路/山体细节），本层不画；
    /// - 无烘焙补丁 => 画本层；基底球下沉 BaseSinkFactor（深度余量由基底球下沉买，绝不抬高表面）。
    ///
    /// 构建策略（对应参考模组的 4 ms/帧分帧状态机）：收集格心 -> 反推球冠 + 分桶 -> 建块，每帧最多 4 ms。
    /// 网格顶点是单位方向 * 0.5（与球体网格同一空间），绘制矩阵 = TRS(中心, 自转, R*2) => 自转零成本。
    ///
    /// 纪律：网格/材质显式销毁；任一步失败只报一次并退回基底球；不读存档、不改地块。
    /// </summary>
    public partial class MainTabWindow_PFView
    {
        /// <summary>每帧构建预算（毫秒）。参考模组对齐 4 ms。</summary>
        private const int TileSurfaceBudgetMs = 4;

        /// <summary>收集格心的切片大小（GetTileCenter 是 Burst 调用，一次全扫要十几毫秒）。</summary>
        private const int TileSurfaceCenterSlice = 8192;

        /// <summary>每块目标格数（块数 * 每块 submesh 数 = 每帧 DrawMesh 次数）。</summary>
        private const int TileSurfaceChunkTargetTiles = 1200;

        /// <summary>空间分桶分辨率（每轴桶数）：8^3 = 512 桶。</summary>
        private const int TileSurfaceBinResolution = 8;

        /// <summary>一格在屏幕上小于这个像素就不画本层（远景逐格只是噪声）。</summary>
        private const float TileSurfaceMinPixels = 1.5f;

        /// <summary>层格数上限（资源保护）：超过它不铺，退回基底球。</summary>
        private const int TileSurfaceMaxTiles = 400000;

        private sealed class TileSurfaceChunk
        {
            public Mesh? mesh;
            public Material?[] materials = Array.Empty<Material?>();
            public Vector3 boundsCenter;    // 固连系中的网格包围球，包含起伏后的顶点
            public float boundsRadius;
            public int tiles;
            public int verts;
        }

        private sealed class TileSurfaceBuild
        {
            public PlanetLayer? layer;
            public int tilesCount;
            public int phase;               // 0=收集格心 1=球冠分桶 2=打包成块 3=建网格 4=完成 5=失败
            public Vector3[]? centers;
            public int cursor;
            public Vector3 capCenter;
            public float thetaMaxRad;
            public List<int>[]? bins;
            public int[]? binOrder;
            public List<int[]>? chunkPlans;
            public int chunkCursor;
            public readonly List<TileSurfaceChunk> chunks = new();
            public readonly Dictionary<(BiomeDef Biome, int Grade, bool Wet), Material> materials = new();
            public int includedTiles;
            public List<int>? included;      // 球冠内的格 id（起伏预计算按它推进）
            public CelestialPlanetTerrainConfiguration? terrain;
            public double reliefRadiusMeters;
            public Dictionary<(int X, int Y, int Z), float>? reliefFactors;
            public Dictionary<(int X, int Y, int Z), Vector3>? reliefNormals;
            public bool[]? tileWet;   // 渲染用：该格中心地形湿度 > 阈值 ⇒ 深色河道格（不改地块数据）
            public int reliefCursor;
            public int reliefTiles;
            public Celestial.SphereTileIndexCore? pickIndex;
            public int builtTiles;
            public int builtVerts;
            public long buildTicks;         // 累计构建耗时（Stopwatch tick）
            public bool failed;
            public bool loggedReady;

            public bool Ready => phase == 5;
            public bool Failed => failed || phase == 6;
            public void Fail() { failed = true; phase = 6; }
        }

        private readonly Dictionary<int, TileSurfaceBuild> tileSurfaceBuilds = new();

        /// <summary>地形 shader 只在第一次建材质时报一次（缺了会退回 PFLit：几何仍在、浮雕明暗没有）。</summary>
        private static bool loggedTerrainShader;

        /// <summary>该天体的逐格表面是否已启用几何起伏（决定基底球要不要多沉）。</summary>
        private bool TileSurfaceReliefActive(int bodyId) =>
            tileSurfaceBuilds.TryGetValue(bodyId, out var build) && build.reliefFactors != null && !build.Failed;

        /// <summary>构建调度器（与参考模组同一套 4 ms/帧口径；纯核在 Celestial/Core/Visual）。</summary>
        private readonly Celestial.FrameBudgetScheduler tileSurfaceScheduler = new(TileSurfaceBudgetMs);

        /// <summary>该天体是否有可铺的实时表面（会顺手创建构建状态；不保证已建好）。</summary>
        private bool HasPlanetTileSurface(int bodyId, PlanetLayer? layer, Vector3 position, float radius, float distance)
        {
            if (layer == null || renderCamera == null) return false;
            int tilesCount = layer.TilesCount;
            if (tilesCount <= 0 || tilesCount > TileSurfaceMaxTiles) return false;
            if (PFViewSurfaceBake.IsSidecarPending(bodyId)) return false;
            // Apply the draw-distance cutoff before enqueuing any mesh work.
            float tileSizeView = layer.AverageTileSize * (float)(SpaceConstants.MetersPerUnit * MetersToView);
            float pixels = Celestial.SurfaceLodCore.TileScreenPixels(tileSizeView, Mathf.Max(distance - radius, 1f),
                renderCamera.pixelHeight, renderCamera.fieldOfView);
            if (pixels < TileSurfaceMinPixels || !SphereInFrustum(position, radius * (1f + (float)ReliefClamp))) return false;

            bool hasVerts;
            try { hasVerts = layer.UnsafeVerts != null && layer.UnsafeVerts.Length > 0; }
            catch (Exception ex)
            {
                // 世界层还没生成/已释放时读原生数组会抛：只报一次，本帧退回基底球。
                Log.WarningOnce("[PFView·地表] body=" + bodyId + " 读层顶点失败（层未生成？）=> 本帧退回基底球：" + ex.Message, bodyId * 53 + 11);
                return false;
            }
            if (!hasVerts) return false;
            var build = EnsureTileSurfaceBuild(bodyId, layer, tilesCount);
            return build != null && !build.Failed;
        }

        private TileSurfaceBuild? EnsureTileSurfaceBuild(int bodyId, PlanetLayer layer, int tilesCount)
        {
            if (tileSurfaceBuilds.TryGetValue(bodyId, out var existing))
            {
                if (ReferenceEquals(existing.layer, layer) && existing.tilesCount == tilesCount) return existing;
                DestroyTileSurface(existing);
                tileSurfaceBuilds.Remove(bodyId);
            }
            if (tilesCount > TileSurfaceMaxTiles)
            {
                Log.WarningOnce("[PFView·地表] body=" + bodyId + " 层格 " + tilesCount + " 超过实时表面上限 " + TileSurfaceMaxTiles + "，退回基底球（资源保护）", bodyId * 31 + 17);
                return null;
            }
            var build = new TileSurfaceBuild { layer = layer, tilesCount = tilesCount };
            tileSurfaceBuilds[bodyId] = build;
            tileSurfaceScheduler.Enqueue("tile-surface:" + bodyId, () => StepTileSurface(bodyId));
            return build;
        }

        /// <summary>每帧推进构建（调度器按 4 ms 预算跑，挂在 RenderScene 最前面）。</summary>
        private void PumpTileSurfaceBuilds()
        {
            if (tileSurfaceBuilds.Count == 0) return;
            tileSurfaceScheduler.Pump(out _, out _);
        }

        /// <summary>单个天体的一个构建步（调度器回调）。返回 true = 该步完成/失败，移出队列。</summary>
        private bool StepTileSurface(int bodyId)
        {
            if (!tileSurfaceBuilds.TryGetValue(bodyId, out var build)) return true;
            if (build.Ready || build.Failed) return true;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                PumpOneTileSurface(build);
            }
            catch (Exception ex)
            {
                build.Fail();
                DestroyTileSurface(build);
                Log.Warning("[PFView·地表] body=" + bodyId + " 实时逐格表面构建失败 => 退回基底球（只报一次）：" + ex.Message);
                return true;
            }
            build.buildTicks += Stopwatch.GetTimestamp() - t0;
            if (build.Ready && !build.loggedReady)
            {
                build.loggedReady = true;
                double totalMs = build.buildTicks * 1000.0 / Stopwatch.Frequency;
                Log.Message("[PFView·地表] body=" + bodyId + " 实时逐格表面就绪：格=" + build.includedTiles +
                    " 块=" + build.chunks.Count + " 顶点=" + build.builtVerts + " 材质=" + build.materials.Count +
                    " 分帧累计=" + totalMs.ToString("F1") + " ms");
            }
            return build.Ready || build.Failed;
        }

        private void PumpOneTileSurface(TileSurfaceBuild build)
        {
            var layer = build.layer!;
            switch (build.phase)
            {
                case 0: CollectTileSurfaceCenters(build, layer); break;
                case 1: PlanTileSurfaceCoverage(build, layer); break;
                case 2: PlanTileSurfaceChunks(build); break;
                case 3: PrepareTileReliefSlice(build, layer); break;
                case 4: BuildTileSurfaceChunks(build, layer); break;
            }
        }

        private static void CollectTileSurfaceCenters(TileSurfaceBuild build, PlanetLayer layer)
        {
            if (build.centers == null) build.centers = new Vector3[build.tilesCount];
            int end = Mathf.Min(build.tilesCount, build.cursor + TileSurfaceCenterSlice);
            for (int t = build.cursor; t < end; t++) build.centers[t] = layer.GetTileCenter(t);
            build.cursor = end;
            if (end >= build.tilesCount)
            {
                // 球冠反推失败 = 格心方向合矢量对消 ⇒ **整球都生成了数据**（我们 Parameterized 的层就是全球）。
                // 这时不该"退回基底球"，而该按整球铺（thetaMax=180°）。原版地表层是"只生成一块球冠"，
                // 所以同一函数在那里会给出正常球冠 ⇒ 两种世界一条代码都成立。
                bool hasCap = PFViewSurfaceBake.ComputeCap(build.centers, out Vector3 capCenter, out float capHalfDeg);
                build.capCenter = hasCap ? capCenter : Vector3.up;
                build.thetaMaxRad = hasCap ? (Mathf.Min(capHalfDeg, 178f) + 2f) * Mathf.Deg2Rad : Mathf.PI;
                int binCount = TileSurfaceBinResolution * TileSurfaceBinResolution * TileSurfaceBinResolution;
                build.bins = new List<int>[binCount];
                for (int i = 0; i < binCount; i++) build.bins[i] = new List<int>();
                build.cursor = 0;
                build.included = new List<int>(build.tilesCount);
                build.phase = 1;
            }
        }

        /// <summary>把球冠内的格按方向分桶；球冠与烘焙器同一出处 => 两层永远同区域。</summary>
        private static void PlanTileSurfaceCoverage(TileSurfaceBuild build, PlanetLayer layer)
        {
            float cosMax = Mathf.Cos(build.thetaMaxRad);
            bool wholeSphere = build.thetaMaxRad >= Mathf.PI;
            int end = Mathf.Min(build.tilesCount, build.cursor + TileSurfaceCenterSlice);
            for (int t = build.cursor; t < end; t++)
            {
                Vector3 c = build.centers![t];
                if (c.sqrMagnitude < 1e-6f) continue;
                Vector3 dir = c.normalized;
                // 全球直接纳入有效格心，避免浮点误差把对跖格的点积算成小于 -1。
                if (!wholeSphere && Vector3.Dot(dir, build.capCenter) < cosMax) continue;
                int res = TileSurfaceBinResolution;
                int bx = Mathf.Clamp((int)((dir.x * 0.5f + 0.5f) * res), 0, res - 1);
                int by = Mathf.Clamp((int)((dir.y * 0.5f + 0.5f) * res), 0, res - 1);
                int bz = Mathf.Clamp((int)((dir.z * 0.5f + 0.5f) * res), 0, res - 1);
                build.bins![(bz * res + by) * res + bx].Add(t);
                build.included!.Add(t);
                build.includedTiles++;
            }
            build.cursor = end;
            if (end < build.tilesCount) return;

            var order = new List<int>();
            for (int i = 0; i < build.bins!.Length; i++) if (build.bins[i].Count > 0) order.Add(i);
            order.Sort();
            build.binOrder = order.ToArray();
            build.cursor = 0;
            build.phase = 2;
        }

        /// <summary>按桶顺序把格 id 打包成块（~1200 格/块）。桶按 key 排序 => 块划分与运行顺序无关。</summary>
        private static void PlanTileSurfaceChunks(TileSurfaceBuild build)
        {
            var plans = new List<int[]>();
            var current = new List<int>(TileSurfaceChunkTargetTiles);
            foreach (int bin in build.binOrder!)
            {
                foreach (int tile in build.bins![bin])
                {
                    current.Add(tile);
                    if (current.Count >= TileSurfaceChunkTargetTiles)
                    {
                        plans.Add(current.ToArray());
                        current = new List<int>(TileSurfaceChunkTargetTiles);
                    }
                }
            }
            if (current.Count > 0) plans.Add(current.ToArray());
            build.chunkPlans = plans;
            build.chunkCursor = 0;
            build.phase = 3;   // 起伏预计算（只在有冻结地形场时）
        }

        private static void BuildTileSurfaceChunks(TileSurfaceBuild build, PlanetLayer layer)
        {
            var plans = build.chunkPlans!;
            if (build.chunkCursor < plans.Count)
            {
                var chunk = BuildTileSurfaceChunk(build, layer, plans[build.chunkCursor]);
                if (chunk != null) build.chunks.Add(chunk);
                build.chunkCursor++;
            }
            if (build.chunkCursor == plans.Count) build.phase = 5;
        }

        /// <summary>建一块：逐格多边形 + 一格一张 biome 贴图（UV = 格内切平面归一化，与世界地图同口径）。</summary>
        private static TileSurfaceChunk? BuildTileSurfaceChunk(TileSurfaceBuild build, PlanetLayer layer, int[] tileIds)
        {
            var verts = layer.UnsafeVerts;
            var offsets = layer.UnsafeTileIDToVerts_offsets;
            var bucketOf = new Dictionary<(BiomeDef Biome, int Grade, bool Wet), int>();
            var bucketBiomes = new List<(BiomeDef Biome, int Grade, bool Wet)>();
            var bVerts = new List<List<Vector3>>();
            var bNormals = new List<List<Vector3>>();
            var bUvs = new List<List<Vector2>>();
            var bTris = new List<List<int>>();
            int built = 0;

            foreach (int t in tileIds)
            {
                int start = offsets[t];
                int end = t + 1 < offsets.Length ? offsets[t + 1] : verts.Length;
                int polyVerts = end - start;
                if (polyVerts < 3) continue;
                Vector3 centerRaw = build.centers![t];
                if (centerRaw.sqrMagnitude < 1e-6f) continue;
                Vector3 centerDir = centerRaw.normalized;

                // 层格 biome 缺失（未生成/半成品层）⇒ 跳过该格；Dictionary 不接受 null 键，别让它抛。
                BiomeDef? biome = layer[t].PrimaryBiome;
                if (biome == null) continue;
                // 2026-11-08 可见性：同一 biome 再按 hilliness 分 5 档做明暗 tint ⇒
                // 近景能读出山脊/丘陵的亮带（逐格面本身是平的，只能靠色阶表达起伏）。
                int grade = Mathf.Clamp((int)layer[t].hilliness, 0, 4);
                // 2026-11-09：按地形场湿度给"河道格"单独一档暗色 —— 旧实例的 tile 数据没有 riverbed biome，
                // 这一档让河道在渲染上仍然成深色线（纯表现，不改地块数据）。
                bool wet = build.tileWet != null && build.tileWet[t];
                var bucketKey = (biome, grade, wet);
                if (!bucketOf.TryGetValue(bucketKey, out int bucket))
                {
                    bucket = bucketBiomes.Count;
                    bucketOf[bucketKey] = bucket;
                    bucketBiomes.Add(bucketKey);
                    bVerts.Add(new List<Vector3>());
                    bNormals.Add(new List<Vector3>());
                    bUvs.Add(new List<Vector2>());
                    bTris.Add(new List<int>());
                }

                PFViewSurfaceBake.TileTangentFrame(centerDir, out Vector3 t1, out Vector3 t2);
                float maxAbs = 1e-4f;
                for (int k = start; k < end; k++)
                {
                    Vector3 rel = verts[k] - centerRaw;
                    maxAbs = Mathf.Max(maxAbs, Mathf.Abs(Vector3.Dot(rel, t1)));
                    maxAbs = Mathf.Max(maxAbs, Mathf.Abs(Vector3.Dot(rel, t2)));
                }

                var bv = bVerts[bucket];
                var bn = bNormals[bucket];
                var bu = bUvs[bucket];
                var bt = bTris[bucket];
                int baseIndex = bv.Count;
                for (int k = start; k < end; k++)
                {
                    Vector3 v = verts[k];
                    Vector3 dir = v.sqrMagnitude > 1e-9f ? v.normalized : centerDir;
                    bv.Add(dir * 0.5f * ReliefFactorAt(build, dir));
                    bn.Add(ReliefNormalAt(build, dir));
                    Vector3 rel = v - centerRaw;
                    bu.Add(new Vector2(
                        Vector3.Dot(rel, t1) / (2f * maxAbs) + 0.5f,
                        Vector3.Dot(rel, t2) / (2f * maxAbs) + 0.5f));
                }
                // 绕序必须朝外：与烘焙器同一条判据（几何法线 vs 径向），反了就是静默的空白。
                Vector3 p0 = verts[start];
                Vector3 p1 = verts[start + 1];
                Vector3 p2 = verts[start + 2];
                bool flip = Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), centerDir) < 0f;
                for (int k = 1; k + 1 < polyVerts; k++)
                {
                    if (!flip)
                    {
                        bt.Add(baseIndex); bt.Add(baseIndex + k); bt.Add(baseIndex + k + 1);
                    }
                    else
                    {
                        bt.Add(baseIndex); bt.Add(baseIndex + k + 1); bt.Add(baseIndex + k);
                    }
                }
                built++;
            }

            if (built == 0) return null;

            int totalVerts = 0;
            for (int b = 0; b < bucketBiomes.Count; b++) totalVerts += bVerts[b].Count;
            if (totalVerts == 0) return null;

            var allVerts = new List<Vector3>(totalVerts);
            var allNormals = new List<Vector3>(totalVerts);
            var allUvs = new List<Vector2>(totalVerts);
            var subTris = new int[bucketBiomes.Count][];
            for (int b = 0; b < bucketBiomes.Count; b++)
            {
                int baseIndex = allVerts.Count;
                allVerts.AddRange(bVerts[b]);
                allNormals.AddRange(bNormals[b]);
                allUvs.AddRange(bUvs[b]);
                var src = bTris[b];
                var dst = new int[src.Count];
                for (int i = 0; i < src.Count; i++) dst[i] = src[i] + baseIndex;
                subTris[b] = dst;
            }

            var mesh = new Mesh { name = "PF_TileSurface" };
            if (totalVerts > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(allVerts);
            mesh.SetNormals(allNormals);
            mesh.SetUVs(0, allUvs);
            mesh.subMeshCount = bucketBiomes.Count;
            for (int b = 0; b < bucketBiomes.Count; b++) mesh.SetTriangles(subTris[b], b);
            mesh.RecalculateBounds();

            var materials = new Material?[bucketBiomes.Count];
            for (int b = 0; b < bucketBiomes.Count; b++)
                materials[b] = GetTileSurfaceMaterial(build, bucketBiomes[b].Biome, bucketBiomes[b].Grade, bucketBiomes[b].Wet);

            Bounds bounds = mesh.bounds;
            build.builtTiles += built;
            build.builtVerts += totalVerts;
            return new TileSurfaceChunk
            {
                mesh = mesh,
                materials = materials,
                boundsCenter = bounds.center,
                boundsRadius = bounds.extents.magnitude,
                tiles = built,
                verts = totalVerts,
            };
        }

        /// <summary>每天体每 (biome, hilliness 档) 一份材质：贴图同源，只改明暗 tint 表达起伏。</summary>
        private static Material? GetTileSurfaceMaterial(TileSurfaceBuild build, BiomeDef? biome, int grade, bool wet)
        {
            if (biome == null) return null;
            var key = (biome, grade, wet);
            if (build.materials.TryGetValue(key, out var cached)) return cached;
            Texture2D? tex = biome.DrawMaterial?.mainTexture as Texture2D;
            if (tex == null) return renderMat;
            // 目标 A：地形走独立 shader（PFLit + 法线浮雕项）；取不到就退回 PFLit 克隆（至少几何仍在）。
            Shader? terrainShader = Shader.Find("PF_TerrainLit");
            if (!loggedTerrainShader)
            {
                loggedTerrainShader = true;
                Log.Message(terrainShader != null
                    ? "[PFView·地形] PF_TerrainLit 就绪（地形法线浮雕启用）"
                    : "[PFView·地形] 找不到 PF_TerrainLit ⇒ 退回 PFLit（只有几何、没有法线浮雕明暗）");
            }
            Material mat = terrainShader != null ? new Material(terrainShader) : NewSurfaceMaterial(tex);
            mat.mainTexture = tex;
            // 0=Flat .. 4=Impassable：0.86 → 1.18 的乘性灰阶（贴图之外再加一档起伏提示）。
            float tint = 0.86f + 0.08f * Mathf.Clamp(grade, 0, 4);
            if (wet) tint *= 0.45f;   // 河道格：压暗到 ~0.4，读成深色水线/干河床
            mat.color = new Color(tint, tint, tint, 1f);
            if (mat.HasProperty("_Emission"))
                mat.SetVector("_Emission", new Vector4(SurfaceNightEmission, SurfaceNightEmission, SurfaceNightEmission, 1f));
            if (mat.HasProperty("_Terrain")) mat.SetVector("_Terrain", new Vector4(1f, 0f, 0f, 0f));
            build.materials[key] = mat;
            return mat;
        }

        /// <summary>
        /// 画该天体的实时逐格表面。返回 false = 本帧没有可画内容（调用方照旧画基底球/补丁）。
        /// 与绘制同帧推进构建：没建好就返回 false（此时基底球已下沉，台阶不可见）。
        /// </summary>
        private bool DrawPlanetTileSurface(int bodyId, Vector3 renderPos, float renderR, Quaternion selfRot,
            Vector3 toSun, Color sunColor)
        {
            if (!tileSurfaceBuilds.TryGetValue(bodyId, out var build) || !build.Ready) return false;

            float meshScale = renderR * 2f;
            Matrix4x4 mtx = Matrix4x4.TRS(renderPos, selfRot, Vector3.one * meshScale);
            int drawnChunks = 0;
            for (int i = 0; i < build.chunks.Count; i++)
            {
                var chunk = build.chunks[i];
                if (chunk.mesh == null) continue;
                // 分块可能跨过半球，起伏也可能越过名义地平线。遮挡交给深度测试，CPU 只剔除视锥外的分块。
                Vector3 chunkCenterView = mtx.MultiplyPoint3x4(chunk.boundsCenter);
                if (!SphereInFrustum(chunkCenterView, chunk.boundsRadius * meshScale)) continue;
                for (int m = 0; m < chunk.materials.Length; m++)
                {
                    var mat = chunk.materials[m];
                    if (mat == null) continue;
                    // 材质是独立实例：不逐帧刷新就收不到太阳（否则昼夜线会冻结在创建时刻）。
                    ApplySunUniforms(mat, toSun, sunColor);
                    Graphics.DrawMesh(chunk.mesh, mtx, mat, SceneLayer, null, m);
                }
                drawnChunks++;
            }
            return drawnChunks > 0;
        }


        private void DestroyTileSurface(TileSurfaceBuild build)
        {
            foreach (var chunk in build.chunks)
            {
                if (chunk.mesh != null) UnityEngine.Object.Destroy(chunk.mesh);
                chunk.mesh = null;
            }
            build.chunks.Clear();
            foreach (var kv in build.materials)
            {
                if (kv.Value != null && kv.Value != renderMat) UnityEngine.Object.Destroy(kv.Value);
            }
            build.materials.Clear();
        }

        /// <summary>全部实时表面资源（收窗/切世界/切天体时调用）。</summary>
        private void DestroyAllTileSurfaces()
        {
            foreach (var kv in tileSurfaceBuilds) DestroyTileSurface(kv.Value);
            tileSurfaceBuilds.Clear();
        }

        private void DestroyTileSurfaceForBody(int bodyId)
        {
            if (!tileSurfaceBuilds.TryGetValue(bodyId, out var build)) return;
            DestroyTileSurface(build);
            tileSurfaceBuilds.Remove(bodyId);
        }
    }
}
