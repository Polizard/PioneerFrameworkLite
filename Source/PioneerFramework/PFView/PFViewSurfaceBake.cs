using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 天体地表的**烘焙**（2026-10-10 用户裁定："我们只烘焙有 tile 覆盖的区域，其他的部分用基底填充，
    /// 为之后的细化留出余地"）。
    ///
    /// ## 烤哪块：数据区球冠，不是整球
    /// 原版地表层只生成一块**球冠**（`PlanetCoverage × 180°` = 球冠半角；本局 30% ⇒ **54°**），帽外没有任何地形数据
    /// ⇒ 烤整球等于把八成的像素花在没有数据的球面上：3072 宽的等距圆柱上，一格（25 km）只有 **2.4 texel**，
    /// 山体/河流那种"一格一块"的细节画上去就是 1–2 px 的糊点。
    /// 现在只烤数据区：**补丁图** = 正方形里**内切的圆盘**，覆盖"球冠 + 2° 余量"。
    /// 3072² ⇒ 盘半径 1536 px；54° 球冠在半径 5000 km 的地球上弧长 4,712 km ⇒ **≈3.2 km/texel**
    /// ⇒ **一格 25 km ≈ 8 texel**（整球那版是 2.4，同样是 3072 量级的像素预算）。
    /// 帽外（以及没有补丁的天体）= PFView 的**基底球**（背景 biome 贴图平铺）—— 这正是原版对"未生成区"的同一口径
    /// （`UngeneratedPlanetParts` + `backgroundBiome`），所以边界是原版自己的语言，不是新发明的。
    ///
    /// ## 怎么投：方位等距（AEQD）
    /// 球冠中心 `n` + 切平面正交基 `(t1,t2)`（`TileTangentFrame`，与"格内贴图 UV"同一份口径）：
    /// 方向 d ⇒ θ = acos(d·n)、φ = atan2(d·t2, d·t1) ⇒ 图坐标 = 0.5 + 0.5·(θ/θmax)·(cosφ, sinφ)。
    /// 选它而不选等距圆柱/正切平面的三条理由：
    /// ① **texel 密度沿半径均匀**（正切平面在 54° 处已是 2.9× 拉伸、方角 5× 以上）；
    /// ② **没有极点奇点**（本局球冠中心在纬度 ~36°、顶到北极 ⇒ 等距圆柱裁切会把极区糊成一条）；
    /// ③ **跨经度缝的 ±1 双份拷贝那套特例整个消失**（圆盘没有 u 缝）——旧代码里最绕的一段直接删掉。
    /// ⚠ 补丁网格（`MainTabWindow_PFView.Terrain.cs` 的 `BuildSurfacePatchMesh`）用的是**同一个** `PatchDir`
    /// ⇒ 网格与这张图**互为逆映射**，错位在构造上不可能。
    ///
    /// ## 其余照旧（不动的地方）
    /// 逐格多边形 + 每格一张 biome 贴图（格内切平面归一化 UV，逐字与 `BuildSphereMesh` 的 UV 口径对齐）·
    /// 正交相机 + `CommandBuffer`（GUI 相位里 GL/`DrawMeshNow` 全是 no-op）· clear color = **背景 biome 平均色**
    /// （盘内没画到的地方 = 那个 biome 的底色 ⇒ 结构上不开天窗）· 结果落**旁挂 PNG**（跟存档走）·
    /// **手动按钮是唯一入口，不自动烘**。
    ///
    /// ## 自检（两条，都进日志；这是"日志说成功、图其实是空的"那类事故的防线）
    /// ① **几何**：所有格心必须落在补丁盘内（越界数 ≠ 0 ⇒ `Log.Error`）——映射/球冠算错的唯一硬证据；
    /// ② **像素**：盘内"非底色"占比 ≈ 0 ⇒ `Log.Error`。
    ///
    /// ## 落盘
    /// `<世界种子>_<bodyId>_patch.png`（与 `MapArchiveManager` 同一套落盘纪律：不塞进存档 XML，文件名带世界种子
    /// ⇒ 换世界不串档）。⚠ 文件名带 `_patch` 是**故意的**：上一版（整球等距圆柱）的旧图仍叫 `<seed>_<bodyId>.png`，
    /// 两者像素布局完全不同 —— 改了名就天然互不干扰（另有"必须正方形"的兜底闸，见 `TryLoadSaved`）。
    /// </summary>
    internal static class PFViewSurfaceBake
    {
        /// <summary>补丁图边长（正方形；圆盘内切其中）。</summary>
        public const int PatchSize = 3072;

        /// <summary>数据区外再留的余量（度）——保证边缘那圈格完整落进图里，也给网格弦高留位置。</summary>
        public const float PatchMarginDeg = 2f;

        /// <summary>球冠半角上限（度）：正常 30% 覆盖 = 54°；真到半球以上时方位等距会把边缘压扁，但不会退化/除零。</summary>
        private const float MaxHalfAngleDeg = 178f;

        // ── 细节（山/河/路）的"我们的样子"：全是单点常量，用户一句话就能改 ──
        /// <summary>山地起伏明暗的最大幅度（乘性；按"典型起伏"自动标定 ⇒ 山脉密的星和疏的星看起来一样明显）。
        /// ⚠ 这是"好不好看"的头号旋钮；**2026-10-10 用户第二次看过之后**："把山区的噪点减弱到一点点" ⇒ 0.22 → **0.08**
        /// （嫌吵往下调、嫌平往上调：0.05 几乎看不见 / 0.22 是上一版）。</summary>
        private const float ReliefStrength = 0.08f;
        /// <summary>河：水色 = **地图上海洋的那一色**（= 背景 biome 贴图的平均色，也就是整球的底色）——
        /// 用户 2026-10-10 口径："河流的实心的颜色和星球海洋底色一致"（这样河口与海、帽外基底是同一种水）。
        /// 没背景 biome 贴图（算不出平均色）时用它兜底。</summary>
        private static readonly Color32 RiverFallbackWaterColor = new(59, 71, 90, 255);
        /// <summary>河：岸色 = 水色 × 它（只压暗一点点，读作"水边"；不想要描边就把 `RiverBankBorderPx` 给 0）。</summary>
        private const float RiverBankDarken = 0.72f;
        /// <summary>河：岸比芯每侧宽出多少 px（细描边；给 0 就是一条纯色线）。</summary>
        private const float RiverBankBorderPx = 0.6f;
        /// <summary>河/路的**宽度倍率**。
        /// ⚠ 2026-10-10 用户两次口述的区间：先"太细 ⇒ 一节一节"（其实主因是半透明 + 逐段画，见 §当年 19:29 批），
        /// 修好后"**改细**" ⇒ 从 1.7/1.4 收到 **0.8/0.6**（数值上是"比 19:11 首版还细一档"）。</summary>
        private const float RiverWidthScale = 0.8f;
        private const float RoadWidthScale = 0.6f;
        /// <summary>河/路的不透明度：**恒 1**（用户口径："我希望是纯色的"）。
        /// ⚠ 半透明会同时坏两件事：山体明暗透出来（带子花花的）+ 相邻段重叠处二次混色（结子）。</summary>
        private const float RiverCoreAlpha = 1f;
        private const float RiverBankAlpha = 1f;
        /// <summary>路的不透明度：同上恒 1。</summary>
        private const float RoadAlpha = 1f;

        private const string FolderName = "PF_SurfaceBake";

        /// <summary>一张补丁图 + 它覆盖的球冠（PFView 建补丁网格要用**同一份** center/thetaMax）。</summary>
        private struct Patch
        {
            public Texture2D tex;
            public string path;
            public Vector3 center;          // 球冠中心方向（天体局部系、单位向量）
            public float thetaMaxRad;       // 补丁盘覆盖的半角（含余量）
        }

        private static readonly Dictionary<int, Patch> baked = new();
        private static readonly HashSet<int> failed = new();
        private static readonly HashSet<int> loadTried = new();

        /// <summary>缓存所属的世界种子（地形是世界种子的函数 ⇒ 换种子必须整个丢掉）。</summary>
        private static int cachedSeed = int.MinValue;
        private static Game? cachedGame;
        private static System.Guid cachedCelestialSession;

        /// <summary>兼容的总修订计数；消费者按 GetRevision 读取所属天体的修订。</summary>
        public static int Version { get; private set; }
        private static int resetRevision;
        private static readonly Dictionary<int, int> ownerRevisions = new();

        internal static int GetRevision(int handle)
        {
            EnsureContext();
            return ownerRevisions.TryGetValue(handle, out int revision) ? revision : resetRevision;
        }

        private static void Touch(int handle)
        {
            Version++;
            ownerRevisions[handle] = Version;
        }

        // Both native bake and sidecar decode publish through this existing texture owner.
        private static void StorePatch(int handle, Patch patch)
        {
            EnsureContext();
            if (baked.TryGetValue(handle, out var old) && old.tex != null) UnityEngine.Object.Destroy(old.tex);
            baked[handle] = patch;
            failed.Remove(handle);
            loadTried.Add(handle);
            Touch(handle);
        }

        /// <summary>取补丁图 + 它的球冠。没有（没烤过、也没旁挂文件）⇒ false。</summary>
        public static bool TryGetPatch(int bodyId, out Texture2D? tex, out Vector3 center, out float thetaMaxRad)
        {
            EnsureContext();
            if (TryGetCurrentPatch(bodyId, out var p) && p.tex != null)
            {
                tex = p.tex; center = p.center; thetaMaxRad = p.thetaMaxRad;
                return true;
            }
            tex = null; center = Vector3.up; thetaMaxRad = 0f;
            return false;
        }

        public static bool HasFailed(int bodyId) { EnsureContext(); return failed.Contains(bodyId); }

        /// <summary>
        /// 把**能从旁挂文件重新读回来**的补丁图从显存里放掉（2026-10-10 用户："这个显存占用是不是太巨大了"）。
        /// PFView 收起时调一次（`CleanupResources`）：不看的时候不该扣着 12 MiB/天体。
        /// ⚠ 只放**有旁挂文件**的：落盘失败过的那种（只有内存副本、盘上没有）留着 —— 否则就真丢了。
        /// ⚠ 这不是"清理口吞持久数据"（那条教训是"卸了却读不回来"）：`loadTried` 一并清掉 ⇒ 下次 `EnsureReady` 会重新加载
        ///   （代价 = 下次开 PFView 时一次 PNG 解码，几十~几百 ms）。
        /// </summary>
        public static void UnloadReloadable()
        {
            EnsureContext();
            var world = Find.World;
            if (world == null || baked.Count == 0) return;

            var ids = new List<int>(baked.Keys);
            for (int i = 0; i < ids.Count; i++)
            {
                int id = ids[i];
                if (!File.Exists(baked[id].path)) continue;
                if (baked[id].tex != null) UnityEngine.Object.Destroy(baked[id].tex);
                baked.Remove(id);
                loadTried.Remove(id);
                Touch(id);
            }
        }

        /// <summary>
        /// 上下文检查（**每次取用前调**）：换世界种子 ⇒ 全部作废（地形是种子的函数）；
        /// 回主菜单（无局）⇒ 立即释放（每体 3072² ≈ 38 MB，不能扣着）。
        /// ⚠ 这里**不**由 PFView 的 `CleanupResources` 触发 —— 补丁图是"持久数据"（另有旁挂文件），
        /// 挂在窗口资源上会"每次收起 PFView 就把烘焙成果销毁"（2026-10-10 实测踩到）。
        /// </summary>
        private static void EnsureContext()
        {
            if (Current.Game == null || Find.World == null)
            {
                if (baked.Count > 0 || failed.Count > 0 || loadTried.Count > 0 || ownerRevisions.Count > 0 || baseTexCache.Count > 0)
                {
                    Clear();
                    cachedSeed = int.MinValue;
                }
                cachedGame = null;
                cachedCelestialSession = System.Guid.Empty;
                return;
            }

            int seed = Find.World.info.Seed;
            System.Guid session = Current.Game.GetComponent<CelestialBodyManager>()?.TryGetCelestialRuntime(out var runtime) == true ? runtime.WorldSessionId : System.Guid.Empty;
            if (seed != cachedSeed || !ReferenceEquals(cachedGame, Current.Game) || cachedCelestialSession != session)
            {
                Clear();
                cachedSeed = seed;
                cachedGame = Current.Game;
                cachedCelestialSession = session;
            }
        }

        public static void Clear()
        {
            foreach (var kv in baked)
                if (kv.Value.tex != null) UnityEngine.Object.Destroy(kv.Value.tex);
            baked.Clear();
            failed.Clear();
            loadTried.Clear();
            baseTexCache.Clear();     // 底色解析结果也跟"世界/存档上下文"走（层 id 复用别串档）
            baseTexLabel.Clear();
            baseTexLayers.Clear();
            Version++;
            resetRevision = Version;
            ownerRevisions.Clear();
        }
        internal static void ForgetMemory(int handle) => ForgetMemory(handle, null);

        internal static void InvalidateVisualMemory(int handle, int? layerId)
        {
            EnsureContext();
            // Preserve the sole copy only while the existing generated surface and disk identity remain valid.
            if (layerId.HasValue && Find.WorldGrid != null && Find.WorldGrid.PlanetLayers.TryGetValue(layerId.Value, out var layer) &&
                MatchesCelestialOwner(handle, layer) && TryGetCurrentPatch(handle, out var patch) && !File.Exists(patch.path)) return;
            ForgetMemory(handle, layerId);
        }

        internal static void ForgetMemory(int handle, int? layerId)
        {
            EnsureContext();
            if (baked.TryGetValue(handle, out var patch) && patch.tex != null) UnityEngine.Object.Destroy(patch.tex);
            baked.Remove(handle); failed.Remove(handle); loadTried.Remove(handle);
            if (layerId.HasValue)
            {
                baseTexCache.Remove(layerId.Value); baseTexLabel.Remove(layerId.Value); baseTexLayers.Remove(layerId.Value);
            }
            Touch(handle);
        }
        private static bool TryGetCurrentPatch(int bodyId, out Patch patch)
        {
            if (!baked.TryGetValue(bodyId, out patch)) return false;
            bool removed = false;
            var manager = Current.Game?.GetComponent<CelestialBodyManager>();
            if (manager?.TryGetCelestialRuntime(out var runtime) == true && runtime.Catalog.TryResolveHandle(bodyId, out var id))
            {
                var body = runtime.Query(id);
                removed = !body.Success || body.Value.Lifecycle == Celestial.CelestialLifecycle.Removed;
            }
            if (!removed && string.Equals(patch.path, PathFor(cachedSeed, bodyId), StringComparison.Ordinal)) return true;
            if (patch.tex != null) UnityEngine.Object.Destroy(patch.tex);
            baked.Remove(bodyId); loadTried.Remove(bodyId); failed.Remove(bodyId); Touch(bodyId);
            patch = default;
            return false;
        }

        /// <summary>
        /// 取用前调一次：保证（1）上下文正确（换种子/回主菜单即作废）、（2）旁挂文件被尝试加载过
        /// （每体只 stat 一次盘）。返回该体当前有没有补丁图。
        /// ⚠ `layer` 必需：旁挂 PNG 里没有球冠信息，**球冠是从 layer 的格心反推的**（同一份数据 ⇒ 与烘焙时逐位一致）。
        /// </summary>
        public static bool EnsureReady(int bodyId, PlanetLayer? layer)
        {
            EnsureContext();
            if (!MatchesCelestialOwner(bodyId, layer)) return false;
            if (TryGetCurrentPatch(bodyId, out _)) return true;
            if (loadTried.Contains(bodyId)) return false;
            loadTried.Add(bodyId);

            var world = Find.World;
            if (world != null)
            {
                // ⚠ 这行原来把 `message` 丢了（`out _`）⇒ "从旁挂文件加载" / "不是正方形被忽略" 这些结果外面看不见，
                //   于是"改了没生效"和"没烤过"分不清（2026-10-11 就为这个困惑过一轮）。现在打出来。
                if (TryLoadSaved(world.info.Seed, bodyId, layer, out string? loaded) && loaded != null)
                    Log.Message("[PFView·烘焙] body=" + bodyId + " " + loaded);
                else if (loaded != null)
                    Log.Warning("[PFView·烘焙] body=" + bodyId + " 旁挂图未采用：" + loaded);
            }
            return baked.ContainsKey(bodyId);
        }

        /// <summary>
        /// 旁挂文件名。⚠ 带版本号 `_patch2`：**v1 是线性 RT 烤的（比基底球暗一档）**，内容口径变了就换名
        /// ⇒ 旧图不会被当成新图读进来（省得出现"改了没生效"的假象）。读档时另有一道"必须正方形"的兜底闸。
        /// </summary>
        private static string PathFor(int seed, int bodyId)
        {
            string directory = Path.Combine(GenFilePaths.SaveDataFolderPath, FolderName);
            var manager = Current.Game?.GetComponent<CelestialBodyManager>();
            if (manager?.TryGetCelestialRuntime(out var runtime) == true && runtime.Catalog.TryResolveHandle(bodyId, out var id) && runtime.Catalog.TryGetInstance(id, out var instance))
            {
                string configuration = Celestial.CelestialBakeCacheIdentity.ConfigurationHash(instance.GenerationOrigin, instance.CompStateRecords.Values);
                return Path.Combine(directory, Celestial.CelestialBakeCacheIdentity.DiskKey(seed, id, instance.Description.DescriptorRevision, configuration) + "_patch3.png");
            }
            return Path.Combine(directory, seed + "_" + bodyId + "_patch2.png");
        }
        private static bool MatchesCelestialOwner(int handle, PlanetLayer? layer)
        {
            var manager = Current.Game?.GetComponent<CelestialBodyManager>();
            bool active = manager?.TryGetCelestialRuntime(out _) == true;
            if (layer is not Celestial.CelestialSurfaceLayer surface) return !active;
            if (!active || !manager!.TryGetCelestialRuntime(out var runtime) || !runtime.Catalog.TryResolveHandle(handle, out var id) || id != surface.Header.OwnerId ||
                surface.Header.State != Celestial.CelestialSurfaceState.Ready || Find.WorldGrid == null ||
                !Find.WorldGrid.PlanetLayers.TryGetValue(layer.LayerID, out var registered) || !ReferenceEquals(registered, layer)) return false;
            var body = runtime.Query(id);
            return body.Success && body.Value.Lifecycle != Celestial.CelestialLifecycle.Removed && body.Value.Description.RadiusMeters == surface.Header.PhysicalRadiusMeters;
        }

        /// <summary>先看旁挂文件在不在；在就读进来（转场/读档后由 `EnsureReady` 调一次）。</summary>
        public static bool TryLoadSaved(int seed, int bodyId, PlanetLayer? layer, out string? message)
        {
            EnsureContext();
            message = null;
            if (!MatchesCelestialOwner(bodyId, layer)) { message = "SurfaceIdentityMismatch"; return false; }
            if (TryGetCurrentPatch(bodyId, out _)) return true;

            string path = PathFor(seed, bodyId);
            if (!File.Exists(path)) return false;

            try
            {
                var bytes = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);   // mipmap：与烘焙路径同口径（细线远景不闪）
                if (!tex.LoadImage(bytes))
                {
                    UnityEngine.Object.Destroy(tex);
                    message = "旁挂文件读不出图";
                    return false;
                }
                // ⚠ 兜底闸：补丁图**必须正方形**。旧版（整球等距圆柱）是 2:1，像素布局与补丁完全不同，
                //   拿它当补丁画出来是"整球图被塞进圆盘"（看着像地形错位，实际是拿错了图）⇒ 直接从盘上拒绝。
                if (tex.width != tex.height)
                {
                    UnityEngine.Object.Destroy(tex);
                    message = $"旁挂文件不是正方形（{tex.width}×{tex.height}）⇒ 那是旧版整球图，忽略（重烤一次即得补丁）";
                    return false;
                }
                if (layer == null || !ComputeCapFromLayer(layer, out Vector3 center, out float halfAngleDeg))
                {
                    UnityEngine.Object.Destroy(tex);
                    message = "缺 layer / 格心退化 ⇒ 反推不出球冠，无法把旁挂图摆到球上";
                    return false;
                }

                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Trilinear;
                tex.Apply(true);                     // LoadImage 之后重建 mip 链（否则三线性过滤只有基级）
                tex.name = "PF_Patch_" + bodyId;
                try { tex.Compress(true); }          // 与烘焙路径同口径：压成平台格式（48 → 12 MiB）
                catch (Exception ex) { Log.Warning("[PFView·烘焙] 旁挂图压缩失败（只是显存偏大）：" + ex.Message); }
                StorePatch(bodyId, new Patch
                {
                    tex = tex,
                    path = path,
                    center = center,
                    thetaMaxRad = (Mathf.Min(halfAngleDeg, MaxHalfAngleDeg) + PatchMarginDeg) * Mathf.Deg2Rad,
                });
                message = $"已从旁挂文件加载（{bytes.Length / 1024f:F0} KB）";
                return true;
            }
            catch (Exception ex)
            {
                message = "旁挂文件加载失败：" + ex.Message;
                return false;
            }
        }

        // ════════════════════════════════════════════════════
        //  纯核心（无 Unity 运行时对象 ⇒ headless 可测）
        // ════════════════════════════════════════════════════

        /// <summary>
        /// 球冠：**中心方向 + 半角**，从格心方向反推（不读 `PlanetCoverage` 那类配置 ⇒ 对每颗天体的层都成立，
        /// 月球那种"另一套 subdivisions/半径"的层一样适用）。
        /// 中心 = 全部格心方向的平均（等角 Voronoi 下 ≈ 球冠轴）；半角 = 最远格心与中心的夹角。
        /// 退化（没有格 / 方向恰好对消）⇒ false，调用方据此报"烤不了"。
        /// </summary>
        internal static bool ComputeCap(IList<Vector3> centers, out Vector3 center, out float halfAngleDeg)
        {
            center = Vector3.up;
            halfAngleDeg = 0f;

            Vector3 sum = Vector3.zero;
            int n = 0;
            for (int i = 0; i < centers.Count; i++)
            {
                Vector3 c = centers[i];
                if (c.sqrMagnitude < 1e-6f) continue;
                sum += c.normalized;
                n++;
            }
            if (n == 0 || sum.sqrMagnitude < 1e-6f) return false;
            center = sum.normalized;

            float maxDeg = 0f;
            for (int i = 0; i < centers.Count; i++)
            {
                Vector3 c = centers[i];
                if (c.sqrMagnitude < 1e-6f) continue;
                float dot = Mathf.Clamp(Vector3.Dot(center, c.normalized), -1f, 1f);
                float deg = Mathf.Acos(dot) * Mathf.Rad2Deg;
                if (deg > maxDeg) maxDeg = deg;
            }
            halfAngleDeg = Mathf.Min(maxDeg, MaxHalfAngleDeg);
            return true;
        }

        /// <summary>
        /// 方向 → 补丁图坐标（[0,1]²，圆盘内切；盘心 = (0.5,0.5)、盘边 = 半径 0.5）。
        /// **不夹**半径：θ &gt; θmax 的方向映射到盘外（正交相机自然裁掉），越界这件事由自检去数、不在这里抹掉。
        /// ⚠ 纯算术（不用 `Quaternion` 那族 ECall —— headless 一调即抛）。
        /// </summary>
        internal static Vector2 PatchUv(Vector3 dir, Vector3 center, Vector3 t1, Vector3 t2, float thetaMaxRad)
        {
            Vector3 d = dir.normalized;
            float theta = Mathf.Acos(Mathf.Clamp(Vector3.Dot(d, center), -1f, 1f));
            float r = thetaMaxRad > 1e-6f ? theta / thetaMaxRad : 0f;
            float phi = Mathf.Atan2(Vector3.Dot(d, t2), Vector3.Dot(d, t1));
            return new Vector2(0.5f + 0.5f * r * Mathf.Cos(phi), 0.5f + 0.5f * r * Mathf.Sin(phi));
        }

        /// <summary>
        /// 补丁图坐标 → 方向（`PatchUv` 的逆）。**补丁网格的唯一出处** ⇒ 网格与烘焙图严丝合缝。
        /// </summary>
        internal static Vector3 PatchDir(Vector2 uv, Vector3 center, Vector3 t1, Vector3 t2, float thetaMaxRad)
        {
            float dx = (uv.x - 0.5f) * 2f;
            float dy = (uv.y - 0.5f) * 2f;
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            float phi = Mathf.Atan2(dy, dx);
            float theta = r * thetaMaxRad;
            float st = Mathf.Sin(theta), ct = Mathf.Cos(theta);
            Vector3 dir = center * ct + (t1 * Mathf.Cos(phi) + t2 * Mathf.Sin(phi)) * st;
            return dir.sqrMagnitude > 1e-12f ? dir.normalized : center;
        }

        /// <summary>
        /// 补丁网格的**径向外扩**（相对半径）= 只保证"大于网格自身的弦高"（弦高 = 1 − cos(环步长/2)，取 4 倍安全系数），
        /// 兜底 **2e-4（地球 = 1 km）**。
        ///
        /// ⚠⚠ 2026-10-11 用户实测："**低飞 Group 视角朝向地表时烘焙渲染会消失**" —— 病根就在这个数：
        /// 上一批为压过深度分辨率把它抬到 **6e-3（地球 30 km）**，结果**低飞（< 30 km）的相机跑到补丁下面去了**
        /// ⇒ 朝下看到的是**基底球**（背景 biome 平铺）而不是烘焙地形。
        /// ⇒ 正确分工：**补丁只离地一点点**（这个函数，低飞永远不会钻到它下面），
        /// **深度余量由"基底球下沉"来买**（`BaseSinkFactor`，见下）—— 两层的相对间距照样 ≫ δz，但**绝对高度**不受影响。
        /// </summary>
        internal static float PatchEpsilon(float thetaMaxRad, int rings)
        {
            float step = thetaMaxRad / Mathf.Max(1, rings);
            return Mathf.Max(2e-4f, 4f * (1f - Mathf.Cos(step * 0.5f)));
        }

        /// <summary>
        /// **基底球的下沉量**（相对半径，0.8% ⇒ 地球 40 km、月球 12 km）。
        /// 只在"该天体有烘焙补丁"时下沉（没补丁时基底球就是地表，不能沉 ✗）：给"补丁 ↔ 基底球"买出 ≫ δz 的深度间距
        /// （地球最贴近取景那档 δz ≈ 14 km ⇒ 40 km 有 ~3 倍余量；月球 δz ≈ 1.3 km ⇒ 12 km 有 ~9 倍），
        /// 代价是"补丁之外那圈（未生成区）"的表面看起来低一点点（0.8% 半径，肉眼不可辨），以及补丁边缘有个台阶。
        /// **永远不要用"抬高补丁"来买深度余量** —— 那会把低飞的相机关在补丁底下（2026-10-11 踩过）。
        /// </summary>
        public const float BaseSinkFactor = 0.008f;

        /// <summary>
        /// **显示色 → 渲染色**：把"想在屏幕上看到的颜色"换成该着色器要的**线性值**。
        /// 为什么需要它（2026-10-11 一连串"基底球看起来浅"的根）：Linear 工程里 `Material.color` / `Camera.backgroundColor`
        /// 都被当作**线性值**用；把显示色（0..1 的 sRGB 语义）直接填进去 ⇒ 出来会明显偏亮。
        /// Gamma 工程则本来就是直通，不做转换。日志里打过 `activeColorSpace` 便于核对。
        /// </summary>
        internal static Color ToRenderColor(Color displayColor)
        {
            if (QualitySettings.activeColorSpace != ColorSpace.Linear)
                return displayColor;
            return new Color(Mathf.GammaToLinearSpace(displayColor.r), Mathf.GammaToLinearSpace(displayColor.g),
                Mathf.GammaToLinearSpace(displayColor.b), displayColor.a);
        }

        // ── 底色口径（2026-10-11 量出来的那条）──
        // 离线量盘上的补丁图：**盘缘那圈（clear color）是 (90,86,78) 灰褐，而烘焙出来的海洋是 (72,72,104) 蓝**
        //   ⇒ 不是亮度问题（亮度比 0.98 ✓，sRGB 口径已对），是**颜色来源根本不同**：
        //   烘焙的海洋格用的是"水 biome 的贴图"（真颜色图 ✓），而背景/基底球用的是 `layer.Def.backgroundBiome`
        //   那张贴图 —— **它是给世界着色器做调制的细节图，不是颜色图**（大气线当年踩过同一个坑：
        //   "原版海洋贴图是给着色器调制的细节图，直接画=黑"）。
        // ⇒ 口径改成"**数据区里水格最多的那个 biome**的贴图"（= 烘焙海洋的同一来源 ⇒ 盘内/盘缘/基底球三处同色）✓
        private static readonly Dictionary<int, Texture2D?> baseTexCache = new();
        private static readonly Dictionary<int, string> baseTexLabel = new();
        private static readonly Dictionary<int, PlanetLayer> baseTexLayers = new();

        /// <summary>
        /// 解析"该天体的底色贴图"：优先取**水格最多的 biome 的贴图**（= 烘焙海洋的同一来源），
        /// 没有水格（月球那种）⇒ 退回 `layer.Def.backgroundBiome` 的贴图（老口径）。
        /// 结果按层缓存（地形是存档内的定值）。`label` 进日志，方便跟盘上的图交叉核对。
        /// </summary>
        internal static Texture2D? ResolveBaseTexture(PlanetLayer layer, out string label)
        {
            EnsureContext();
            int key = layer.LayerID;
            if (baseTexLayers.TryGetValue(key, out var cachedLayer) && ReferenceEquals(cachedLayer, layer) && baseTexCache.TryGetValue(key, out var cached))
            {
                label = baseTexLabel[key];
                return cached;
            }

            Texture2D? tex = null;
            label = "（没取到）";
            try
            {
                var counts = new Dictionary<BiomeDef, int>();
                int waterTiles = 0;
                for (int t = 0; t < layer.TilesCount; t++)
                {
                    var tile = layer[t];
                    if (tile == null || !tile.WaterCovered) continue;
                    counts.TryGetValue(tile.PrimaryBiome, out int c);
                    counts[tile.PrimaryBiome] = c + 1;
                    waterTiles++;
                }

                BiomeDef? best = null;
                int bestN = 0;
                foreach (var kv in counts)
                    if (kv.Value > bestN) { bestN = kv.Value; best = kv.Key; }

                // 水格太少（< 1% 或 < 16 格）就不拿它当"底色"—— 免得一两个小湖定调整颗星球
                if (best != null && bestN >= 16 && bestN * 100 >= layer.TilesCount)
                {
                    tex = best.DrawMaterial?.mainTexture as Texture2D;
                    label = $"水格最多 biome={best.defName}（贴图 {(tex != null ? tex.name : "?")}，{bestN}/{waterTiles} 格）";
                }
                if (tex == null)
                {
                    var bg = layer.Def?.backgroundBiome;
                    tex = bg?.DrawMaterial?.mainTexture as Texture2D;
                    label = $"背景 biome（贴图 {(tex != null ? tex.name : "?")}）";
                    if (bg != null && waterTiles > 0)
                        label += $"〔该层有 {waterTiles} 个水格，但没有够格的水 biome ⇒ 退回背景口径〕";
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[PFView·底色] 解析失败 ⇒ 退回背景 biome：" + ex.Message);
                tex = layer.Def?.backgroundBiome?.DrawMaterial?.mainTexture as Texture2D;
            }

            baseTexCache[key] = tex;
            baseTexLabel[key] = label;
            baseTexLayers[key] = layer;
            return tex;
        }

        private static bool ComputeCapFromLayer(PlanetLayer layer, out Vector3 center, out float halfAngleDeg)
        {
            int tiles = layer.TilesCount;
            var centers = new Vector3[tiles];
            for (int t = 0; t < tiles; t++) centers[t] = layer.GetTileCenter(t);
            return ComputeCap(centers, out center, out halfAngleDeg);
        }

        // ════════════════════════════════════════════════════
        //  烘焙
        // ════════════════════════════════════════════════════

        /// <summary>
        /// 手动烘焙入口。成功 ⇒ 进缓存 + 落盘；失败 ⇒ 登记（不刷屏）并给出原因。
        /// </summary>
        public static bool TryBake(CelestialBodyInfo body, PlanetLayer? layer, int seed, out string? message)
        {
            EnsureContext();
            message = null;
            if (body != null && !MatchesCelestialOwner(body.bodyId, layer)) { message = "SurfaceIdentityMismatch"; return false; }
            if (body == null || layer == null || layer.TilesCount <= 0)
            {
                message = "该天体没有地表层（或层还没生成）";
                failed.Add(body != null ? body.bodyId : -1);
                return false;
            }

            var manager = Current.Game?.GetComponent<CelestialBodyManager>();
            if (manager?.TryGetCelestialRuntime(out var runtime) == true && runtime.Catalog.TryResolveHandle(body.bodyId, out var owner))
            {
                var instance = runtime.Query(owner);
                if (!instance.Success || instance.Value.Lifecycle != Celestial.CelestialLifecycle.Ready) { message = "BodyNotReady"; return false; }
                var visuals = runtime.GetCapabilities<Celestial.ICelestialVisualCapability>(owner).Select(capability => capability.GetVisualDescription()).ToArray();
                var atmospheres = runtime.GetCapabilities<Celestial.ICelestialAtmosphereCapability>(owner);
                var environment = runtime.Creation.GetProperties(owner).OfType<Celestial.ExistingEnvironmentResolvedProperties>().SingleOrDefault();
                body = Celestial.CelestialRenderProjection.Create(instance.Value.Description, visuals, true,
                    atmospheres.Count == 1 ? atmospheres[0].GetProfile() : default, environment?.Profile.DayLengthTicks ?? 0);
            }

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            RenderTexture? rt = null;
            Mesh? mesh = null;
            try
            {
                int tiles = layer.TilesCount;
                var centers = new Vector3[tiles];
                for (int t = 0; t < tiles; t++) centers[t] = layer.GetTileCenter(t);

                // 0) 球冠（中心 + 半角）——这一版"烤哪块"的唯一出处
                if (!ComputeCap(centers, out Vector3 capCenter, out float capHalfDeg))
                {
                    message = "格心方向退化了（反推不出球冠 ⇒ 烤不了）";
                    failed.Add(body.bodyId);
                    return false;
                }
                float thetaMaxRad = (Mathf.Min(capHalfDeg, MaxHalfAngleDeg) + PatchMarginDeg) * Mathf.Deg2Rad;
                TileTangentFrame(capCenter, out Vector3 patchT1, out Vector3 patchT2);

                // 找"离球冠中心最近的那一格"：日志里的中心经纬度用它报（走 layer 自己的 LongLatOf ⇒ 与世界视图同一口径）
                int nearestTile = 0;
                float nearestDot = -2f;
                for (int t = 0; t < tiles; t++)
                {
                    if (centers[t].sqrMagnitude < 1e-6f) continue;
                    float dot = Vector3.Dot(capCenter, centers[t].normalized);
                    if (dot > nearestDot) { nearestDot = dot; nearestTile = t; }
                }
                Vector2 centerLatLon = layer.LongLatOf(nearestTile);

                // 1) RT + **临时正交相机**（正方形 ⇒ 视野 [-1,1]²；旧版是 2:1，所以那套 u*4-2 的缩放一并作废）。
                //    ⚠ 第一次实现用即时模式（`GL.LoadProjectionMatrix` + `Graphics.DrawMeshNow`）——那是**空操作**：
                //    这套 API 只在渲染回调里有效，而烘焙按钮跑在 GUI 阶段 ⇒ 一次都没画上，回读到的是没清过的 RT
                //    （整张纯白，落盘 PNG 86 KB 纯白 —— 从盘上那张图当场定案的）。
                //    临时相机 + 普通 `Graphics.DrawMesh` + `cam.Render()`：与 PFView 自己那条渲染路同一套机制，GUI 相位可用。
                // ⚠ **RT 用 sRGB 编码**（2026-10-10 用户："基底球和上面的烘焙贴图反射率不一致，应该一致"）：
                //    默认的线性 RT 写进去的是**线性值**，回读进 sRGB 贴图后会被 Standard 再解码一次
                //    ⇒ 补丁比基底球**暗一档**（双重 gamma）。走 sRGB 后：着色器把 sRGB 贴图解成线性 → 写回 sRGB RT 编码 →
                //    回读进 sRGB 贴图 → Standard 再解码 ⇒ **一来一回恒等** ⇒ 补丁像素 = 原贴图本色
                //    = 与基底球（直接采样同一批 biome 贴图）**同一个反射率口径** ✓
                rt = RenderTexture.GetTemporary(PatchSize, PatchSize, 0, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.sRGB);
                var camGo = new GameObject("PF_PatchCam");
                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = 1f;                       // 半高 = 1 ⇒ 半宽 = 1（正方形 RT）
                cam.transform.position = new Vector3(0f, 0f, -10f);
                cam.transform.rotation = Quaternion.identity;    // Unity 相机沿自身 +Z 看 ⇒ z=0 的平面在正前方
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 100f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                // 2026-10-13（P2-c）：**改用绘制队列**——1.7 的 UnityStubs 里
                // `CommandBuffer.DrawMesh`（CommandBuffer.cs:16）与 `Camera.AddCommandBuffer`（Camera.cs:163）
                // 都是**记日志的空实现**（实测：日志两条 Stub + 我们的自检数字"盘内非底色占比 ≈ 0"，
                // 而清屏色那几项自检全过 ⇒ `cam.Render()` 有效，是命令缓冲根本没画）。
                // 当年用命令缓冲要解决的"同帧串到别的相机"（见下方历史注释）改由**独立图层**解决：
                // 烘焙网格排 `BakeLayer`、烘焙相机只收该层（引擎的 `CopyPendingCommands` 按层掩码过滤；
                // 队列在帧界清空，不漏帧）。
                const int BakeLayer = 29;   // 烘焙专用层（PFView 场景层 = 30，互不串画；原版无 29/30 占用）
                if (Find.Camera != null)
                    Find.Camera.cullingMask &= ~(1 << BakeLayer);   // 地图相机同样摘掉该层（位 29 专属烘焙）
                cam.cullingMask = 1 << BakeLayer;
                cam.targetTexture = rt;

                // 底色贴图 = 水格最多的 biome（= 烘焙海洋的同一来源）；没有水格才退回背景 biome（见 ResolveBaseTexture 的注释）
                var bgTex = ResolveBaseTexture(layer, out string baseTexLabel);
                Log.Message("[PFView·烘焙] 底色口径：" + baseTexLabel);

                // ── 底 = **清屏色**，不再用四边形 ⚠⚠（2026-10-10 的教训）
                // 原来用一张"远超视口的四边形"盖底，实测**盖不满**：诊断图（只含底图那一遍）显示右半边是清屏色，
                // 楔形边界是"过画面中心、斜率 ±45°"的两条直线 —— 那是四边形自身几何的边，跟视口/贴图都无关。
                // 与其继续跟它纠缠，不如**取消这块几何**：清屏色本身就是底 ⇒ 不存在"没盖到"这回事。
                // 代价：补丁盘内未覆盖区是**平均色**而不是平铺贴图；世界 biome 贴图本身近乎纯色（Ocean 读数 (59,71,90)，
                // 波动 ±15%）⇒ 这个差别在这个尺度上看不出来，而且盘外本来就是基底球在画（同一条口径）。
                // 清屏色（盘缘那圈）优先取 Def 里的 `baseSphereColor`（与基底球同一口径 ⇒ 盘缘与基底球必然同色）；
                // 没设才退回"水格最多 biome 贴图的平均色"。
                Color bgCol = new Color(0.02f, 0.03f, 0.05f);
                bool flatBase = body.baseSphereColor.a > 0f;
                if (flatBase) bgCol = ToRenderColor(body.baseSphereColor);
                else if (bgTex != null) bgCol = AverageColor(bgTex, bgCol);
                Log.Message($"[PFView·烘焙] 渲染色空间={QualitySettings.activeColorSpace}｜盘缘色={(flatBase ? "Def.baseSphereColor" : "水格最多 biome 贴图平均色")}（{$"({bgCol.r * 255f:F0},{bgCol.g * 255f:F0},{bgCol.b * 255f:F0})"}）");
                if (bgTex == null)
                    Log.Warning("[PFView·烘焙] 取不到底色贴图 ⇒ 补丁盘内只能用兜底色");
                cam.backgroundColor = bgCol;

                // 2) 逐格多边形摊到**补丁平面**（每 biome 一个 submesh；映射 = `PatchUv`）
                mesh = BuildBakeMesh(layer, centers, capCenter, patchT1, patchT2, thetaMaxRad, out var biomeMats);
                for (int b = 0; b < biomeMats.Length; b++)
                    Graphics.DrawMesh(mesh, Matrix4x4.identity, biomeMats[b], BakeLayer, null, b, null);

                cam.Render();
                cam.targetTexture = null;
                UnityEngine.Object.Destroy(camGo);

                // 3) 回读 → Texture2D（回读要 RT 处于 active）
                //    ⚠ **开 mipmap**（2026-10-10 细节上线后加）：细线（河/路 1–3 px）在不带 mip 的贴图上
                //    缩到远景会闪（摩尔纹）；mip 链 + 三线性过滤把它压掉，代价是 +33% 显存（38 → 51 MB）。
                RenderTexture.active = rt;
                var tex = new Texture2D(PatchSize, PatchSize, TextureFormat.RGBA32, true);
                tex.ReadPixels(new Rect(0f, 0f, PatchSize, PatchSize), 0, 0);
                tex.Apply(true);
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Trilinear;
                tex.name = "PF_Patch_" + body.bodyId;

                RenderTexture.active = null;
                RenderTexture.ReleaseTemporary(rt);
                rt = null;

                // 3a) **回读一次，三处共用**（2026-10-14，M1#1）：原先自检/细节/口径核对各调一次
                //     `GetPixels32()` ⇒ 每次一份 3072²×4 B = 36 MB 的新数组与整张拷贝（共 3 份、108 MB）。
                //     现在只读一份：自检读它 → 细节就地改它 → `SetPixels32` 上传一次 → 口径核对读它
                //     （上传前后像素值不变 ⇒ 三者数值口径与原来逐位相同）。
                //     ⚠ 不做跨次缓存（静态留 36 MB 换"一次烘焙省一份分配"不值，烘焙有盘缓存 ⇒ 本就稀发）。
                Color32[] px = tex.GetPixels32();

                // 3b) **自检 1：几何** —— 所有格心必须落在补丁盘内。
                // 这条是"网格/映射/球冠算错"的唯一硬证据：那种错从画面上看不出来（图还是那张图，只是错位/被裁）。
                int outside = 0;
                for (int t = 0; t < tiles; t++)
                {
                    if (centers[t].sqrMagnitude < 1e-6f) continue;
                    Vector2 puv = PatchUv(centers[t], capCenter, patchT1, patchT2, thetaMaxRad);
                    float dx = (puv.x - 0.5f) * 2f, dy = (puv.y - 0.5f) * 2f;
                    if (dx * dx + dy * dy > 1.0001f) outside++;
                }
                if (outside > 0)
                    Log.Error($"[PFView·烘焙] {outside} 个格心落在补丁盘外 ⇒ 球冠/映射有问题（这张图不可用）");

                // 3c) **自检 2：像素** —— 盘内"非底色"像素占比（采样步长 4 ⇒ 省 16 倍时间）。
                // 存在的理由：前两版都出现过"日志说成功、图其实什么都没画上"（一次纯白、一次品红带），
                // 从外面看全是"烘焙完成" ⇒ 现在把占比直接打进日志，接近 0 就当场报错，不再让它悄悄过关。
                float diskRatio = 0f;
                if (bgTex != null)
                {
                    Color32 bg = bgCol;
                    const int step = 4;
                    int hit = 0, total = 0;
                    int c0 = PatchSize / 2;
                    int rPix = PatchSize / 2;
                    int r2Max = rPix * rPix;
                    for (int y = 0; y < PatchSize; y += step)
                    {
                        int row = y * PatchSize;
                        int dy = y - c0;
                        for (int x = 0; x < PatchSize; x += step)
                        {
                            int dx = x - c0;
                            if (dx * dx + dy * dy > r2Max) continue;      // 只数盘内
                            var c = px[row + x];
                            total++;
                            if (Math.Abs(c.r - bg.r) + Math.Abs(c.g - bg.g) + Math.Abs(c.b - bg.b) > 12) hit++;
                        }
                    }
                    diskRatio = total > 0 ? (float)hit / total : 0f;
                    if (diskRatio < 0.005f)
                        Log.Error("[PFView·烘焙] 盘内非底色占比 ≈ 0 ⇒ 地形那一步什么都没画上（这张补丁图是无效的）");
                }

                // 3d) **细节点缀：山 / 河 / 路**（用户 2026-10-10："先整山、河、路，用我们自己的方式而不是 RW 原版的方式整"）。
                //     放在两条自检**之后**是刻意的：自检数的是"地形那一步画上了没有"，细节把像素改花会让它失真。
                //     水色 = **地图上海洋的那一色**（用户 19:5x 口径："河流的实心的颜色和星球海洋底色一致"）：
                //     就用本函数的 clear color（= 背景 biome 贴图的平均色）—— 海洋格画的是同一张贴图 ⇒ 与海同色 ✓。
                Color32 waterColor = bgTex != null
                    ? new Color32((byte)(bgCol.r * 255f), (byte)(bgCol.g * 255f), (byte)(bgCol.b * 255f), 255)
                    : RiverFallbackWaterColor;
                string detailReadout = CompositeDetails(tex, px, layer, centers, capCenter, patchT1, patchT2, thetaMaxRad, waterColor);

                // 3e) **海洋口径自检**（2026-10-11 用户："基底球的反射率还是和烘焙的海洋不一致"）：
                //     基底球直接采样原始 biome 贴图（sRGB）；补丁的海洋格也应当是原贴图的同一编码 ⇒ 两者比值 ≈ 1。
                //     差一倍以上 = 编码口径不一致（线性/sRGB 双重转换那类），一眼可判。
                string waterCheck = OceanColorCheck(px, layer, centers, capCenter, patchT1, patchT2, thetaMaxRad);

                // 4) 落盘（旁挂文件）
                int pngBytes = 0;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(PathFor(seed, body.bodyId)));
                    var png = tex.EncodeToPNG();
                    pngBytes = png != null ? png.Length : 0;
                    if (png != null) File.WriteAllBytes(PathFor(seed, body.bodyId), png);
                }
                catch (Exception ex)
                {
                    Log.Warning("[PFView·烘焙] 落盘失败（不影响本次使用）：" + ex.Message);
                }

                // 4b) **压成平台压缩格式**（2026-10-10 用户："这个显存占用是不是太巨大了"）：
                //     RGBA32 未压缩 3072²+mip = **48 MiB**，压缩后 **12 MiB**（Unity 按"有没有 alpha 通道"选 DXT1/DXT5；
                //     我们是 RGBA32 ⇒ 走 DXT5。想要 6 MiB 的 DXT1 得把贴图换成 RGB24 —— 那要换掉 GetPixels32 的载体，
                //     本机离线验不了，见交接件"观感/开销旋钮"）。
                //     ⚠ 必须在 `EncodeToPNG` **之后**压：`Compress` 之后 CPU 侧像素就取不回来了（落盘的仍是未压缩原图 ✓）；
                //     也必须在细节画完之后压 ✓（顺序：回读 → 自检 → 细节 → 落盘 → 压缩）。
                try
                {
                    tex.Compress(true);
                }
                catch (Exception ex)
                {
                    Log.Warning("[PFView·烘焙] 贴图压缩失败（不影响使用，只是显存偏大）：" + ex.Message);
                }

                StorePatch(body.bodyId, new Patch { tex = tex, center = capCenter, thetaMaxRad = thetaMaxRad, path = PathFor(seed, body.bodyId) });

                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * (1000.0 / System.Diagnostics.Stopwatch.Frequency);
                float diameterKm = (float)(body.radius * 2.0 * thetaMaxRad / 1000.0);
                float kmPerTexel = (float)(body.radius * thetaMaxRad / (PatchSize * 0.5) / 1000.0);
                message = $"{body.defName}：补丁 {PatchSize}² · 球冠半角 {capHalfDeg:F1}°（+{PatchMarginDeg:F0}° 余量；中心 φ{centerLatLon.y:F1}° λ{centerLatLon.x:F1}°）· {tiles} 格（越界 {outside}）· 直径 {diameterKm:F0} km · {kmPerTexel:F2} km/texel（一格 25 km ≈ {25f / kmPerTexel:F1} texel）· 用时 {ms:F0} ms · PNG {pngBytes / 1024f:F0} KB · 盘内覆被 {diskRatio:P1} · {detailReadout} · {waterCheck}";
                Log.Message("[PFView·烘焙] " + message);
                return true;
            }
            catch (Exception ex)
            {
                message = "烘焙失败：" + ex.Message;
                failed.Add(body.bodyId);
                Log.Error("[PFView·烘焙] " + message + "\n" + ex);
                return false;
            }
            finally
            {
                RenderTexture.active = null;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (mesh != null) UnityEngine.Object.Destroy(mesh);
            }
        }

        // ════════════════════════════════════════════════════
        //  细节合成（山 / 河 / 路）—— 纯 CPU，画进回读出来的像素
        //  ⚠ 位置在两条自检**之后**：自检数的是"地形那一步画上了没有"，细节把像素改花会让它失真。
        // ════════════════════════════════════════════════════

        /// <summary>一格在补丁图上多少像素（一格角径 ÷ 补丁半角 × 盘半径）—— 宽度/大小一律按它换算 ⇒ 换天体也同观感。</summary>
        private static float TilePixelSize(PlanetLayer layer, float thetaMaxRad)
        {
            float tileRad = layer.AverageTileSize / Mathf.Max(layer.Radius, 1f);
            return thetaMaxRad > 1e-6f ? tileRad / thetaMaxRad * (PatchSize * 0.5f) : 0f;
        }

        private static Vector2 PatchPixel(Vector3 dir, Vector3 capCenter, Vector3 t1, Vector3 t2, float thetaMaxRad, int size)
        {
            Vector2 uv = PatchUv(dir, capCenter, t1, t2, thetaMaxRad);
            return new Vector2(uv.x * size, uv.y * size);
        }

        /// <summary>
        /// 把山 / 河 / 路画进补丁像素，返回一句读数（进日志）。
        /// 顺序：山（地形起伏，乘性）→ 河（岸 → 芯）→ 路（按层，数据里的层色直接用）。
        /// </summary>
        private static string CompositeDetails(Texture2D tex, Color32[] px, PlanetLayer layer, Vector3[] centers,
            Vector3 capCenter, Vector3 patchT1, Vector3 patchT2, float thetaMaxRad, Color32 waterColor)
        {
            int size = PatchSize;
            float tilePx = TilePixelSize(layer, thetaMaxRad);
            if (tilePx < 0.25f) return "细节：跳过（一格不足 0.25 px）";

            // `px` 由调用方回读一次并传入（M1#1）；细节就地改它，末尾统一 `SetPixels32` 上传。

            // ── ① 山：高度场（**半分辨率**）+ 无方向高通 ──
            //    半分辨率是划算的：起伏本来就是低频量 ⇒ 28 MB 而不是 113 MB，最后双线性采样回去。
            //    形态：每个有起伏的格撒一个穹顶（半径按 hilliness 档位、权重按档位 × 高程）⇒ 山脉连成片；
            //    然后 低通（箱式模糊两次）取 `h − 低通(h)` ⇒ 隆起处亮、凹陷处暗（**不表态光从哪来**）。
            //    ⚠ 低通半径（`blurR`）越大 ⇒ 高通抓的是越大尺度的差异 ⇒ 越"整片起伏"、越不"噪点"；
            //      用户第二次看过之后要求"噪点减弱到一点点" ⇒ 半径系数 0.8 → 1.1 + `ReliefStrength` 0.22 → 0.08。
            int fieldSize = Mathf.Max(64, size / 2);
            float fieldPerPixel = (float)fieldSize / size;
            var stamps = new List<PFViewSurfaceDetails.HillStamp>();
            for (int t = 0; t < layer.TilesCount; t++)
            {
                Tile tile = layer[t];
                if (tile == null || centers[t].sqrMagnitude < 1e-6f) continue;
                float radiusTiles, weight;
                switch (tile.hilliness)
                {
                    case Hilliness.SmallHills: radiusTiles = 0.55f; weight = 0.45f; break;
                    case Hilliness.LargeHills: radiusTiles = 0.75f; weight = 0.70f; break;
                    case Hilliness.Mountainous: radiusTiles = 0.95f; weight = 1.00f; break;
                    case Hilliness.Impassable: radiusTiles = 1.15f; weight = 1.25f; break;
                    default: continue;                          // Flat：没有起伏，不撒
                }
                Vector2 c = PatchPixel(centers[t], capCenter, patchT1, patchT2, thetaMaxRad, fieldSize);
                float r = radiusTiles * tilePx * fieldPerPixel;
                stamps.Add(new PFViewSurfaceDetails.HillStamp
                {
                    // 落点抖动 ±35% 半径：不让穹顶严格对齐格心（否则会看出格子的规则网格）
                    x = c.x + (PFViewSurfaceDetails.Hash01(t, 11) - 0.5f) * r * 0.7f,
                    y = c.y + (PFViewSurfaceDetails.Hash01(t, 23) - 0.5f) * r * 0.7f,
                    radius = r,
                    weight = weight * (1f + 0.5f * Mathf.Clamp01(tile.elevation / 5000f)),
                });
            }

            int hills = stamps.Count;
            if (hills > 0)
            {
                var field = new float[fieldSize * fieldSize];
                var low = new float[fieldSize * fieldSize];
                var tmp = new float[fieldSize * fieldSize];
                PFViewSurfaceDetails.AccumulateHills(field, fieldSize, stamps);
                int blurR = Mathf.Max(2, Mathf.RoundToInt(tilePx * fieldPerPixel * 1.1f));   // 1.1：比一格略大 ⇒ 成"整片起伏"而不是噪点
                PFViewSurfaceDetails.BoxBlur(field, low, tmp, fieldSize, blurR, 2);
                float scale = PFViewSurfaceDetails.MeanAbsRelief(field, low, fieldSize, 0.05f);
                PFViewSurfaceDetails.ApplyRelief(px, size, field, low, fieldSize, scale, ReliefStrength);
            }

            // ── ② 河（岸 → 芯）──
            int rivers = DrawRivers(px, size, layer, centers, capCenter, patchT1, patchT2, thetaMaxRad, tilePx, waterColor);
            // ── ③ 路（按 RoadWorldLayerDef 逐层）──
            int roads = DrawRoads(px, size, layer, centers, capCenter, patchT1, patchT2, thetaMaxRad, tilePx);

            tex.SetPixels32(px);
            tex.Apply();
            return $"山 {hills} 座 · 河 {rivers} 段 · 路 {roads} 段（一格 {tilePx:F1} px · 起伏幅度 {ReliefStrength:P0}）";
        }

        /// <summary>
        /// 画河：每段 = "岸 + 芯"两层，宽度按 `riverDef.widthOnWorld`（原版单位：**格边比例**；River 0.4 / HugeRiver 0.8）
        /// 换算成像素，再**夹进可见区间**（0.10–0.30 格）—— 数据里的细流在 ~8 px/格 的图上不足 1 px，不夹就等于没画。
        ///
        /// ⚠ 两条是 2026-10-10 用户实测报回来之后定死的（症状："一节一节的不连续很难看"）：
        /// ① **两遍画**：先把**所有**岸铺完，再画所有芯。逐段"岸→芯→岸→芯"会让后一段的暗岸压在前一段的亮芯上
        ///    ⇒ 每个格心一个深色结 = 一节一节；
        /// ② **不透明**（`RiverCoreAlpha/BankAlpha = 1`）：半透明会把山体明暗透出来（带子花花的不纯色），
        ///    且相邻段重叠处二次混色 ⇒ 颜色一段深一段浅。不透明还有个好处：重叠是幂等的（同色盖同色 = 无变化）。
        /// </summary>
        private static int DrawRivers(Color32[] px, int size, PlanetLayer layer, Vector3[] centers,
            Vector3 capCenter, Vector3 t1, Vector3 t2, float thetaMaxRad, float tilePx, Color32 waterColor)
        {
            // 水色与海同色（用户口径）；岸 = 水色压暗一点点，只用来把河口/岸边读出来（`RiverBankBorderPx = 0` ⇒ 一条纯线）
            var bankColor = new Color32(
                (byte)(waterColor.r * RiverBankDarken),
                (byte)(waterColor.g * RiverBankDarken),
                (byte)(waterColor.b * RiverBankDarken), 255);
            var cores = new List<PFViewSurfaceDetails.LineStamp>();
            var banks = new List<PFViewSurfaceDetails.LineStamp>();
            for (int i = 0; i < layer.TilesCount; i++)
            {
                if (!(layer[i] is SurfaceTile tile) || tile.potentialRivers == null) continue;
                Vector3 c0 = centers[i];
                if (c0.sqrMagnitude < 1e-6f) continue;
                for (int k = 0; k < tile.potentialRivers.Count; k++)
                {
                    var link = tile.potentialRivers[k];
                    int nid = link.neighbor.tileId;
                    if (link.river == null) continue;
                    if (nid < 0 || nid >= centers.Length || centers[nid].sqrMagnitude < 1e-6f) continue;

                    // 一条链在两边各存一份 ⇒ 只画一次；但**不假设数据一定对称**：
                    // 对面没存回这条链时，本侧就是唯一的出处，再跳过就留缝（缝 = 一节一节）。
                    RiverDef? back = RiverLinkBack(layer, nid, i);
                    if (nid < i && back != null) continue;

                    Vector2 a = PatchPixel(c0, capCenter, t1, t2, thetaMaxRad, size);
                    Vector2 b = PatchPixel(centers[nid], capCenter, t1, t2, thetaMaxRad, size);
                    // 两侧 RiverDef 可能不是同一个（河道降级）⇒ 取较宽的那个：宽度不在格心处跳变
                    float half = Mathf.Clamp(RiverHalfWidthTiles(back, link.river) * RiverWidthScale, 0.12f, 0.40f) * tilePx;
                    cores.Add(new PFViewSurfaceDetails.LineStamp { ax = a.x, ay = a.y, bx = b.x, by = b.y, halfWidth = half });
                    banks.Add(new PFViewSurfaceDetails.LineStamp { ax = a.x, ay = a.y, bx = b.x, by = b.y, halfWidth = half + RiverBankBorderPx });
                }
            }
            for (int i = 0; i < banks.Count; i++)
                PFViewSurfaceDetails.DrawSegment(px, size, banks[i], bankColor, RiverBankAlpha);
            for (int i = 0; i < cores.Count; i++)
                PFViewSurfaceDetails.DrawSegment(px, size, cores[i], waterColor, RiverCoreAlpha);
            return cores.Count;
        }

        /// <summary>
        /// 画路：按 `RoadWorldLayerDef.order` 逐层叠（原版同序 —— 先画的宽层当描边、后画的窄层当路面），
        /// **层色直接用数据里的**（`RoadWorldLayerDef.color`）；只有宽度换算与透明度是我们的口径。
        /// 水上不画路（原版的同一条门：`!WaterCovered`）。
        /// ⚠ 同样是"按层两遍"（不是逐段两层）：见 `DrawRivers` 里那两条 —— 逐段画会把邻段的描边压上来成结子。
        /// </summary>
        private static int DrawRoads(Color32[] px, int size, PlanetLayer layer, Vector3[] centers,
            Vector3 capCenter, Vector3 t1, Vector3 t2, float thetaMaxRad, float tilePx)
        {
            var layers = new List<RoadWorldLayerDef>(DefDatabase<RoadWorldLayerDef>.AllDefsListForReading);
            if (layers.Count == 0) return 0;
            layers.Sort((x, y) => x.order.CompareTo(y.order));

            var byLayer = new List<PFViewSurfaceDetails.LineStamp>[layers.Count];
            for (int L = 0; L < layers.Count; L++) byLayer[L] = new List<PFViewSurfaceDetails.LineStamp>();

            for (int i = 0; i < layer.TilesCount; i++)
            {
                if (!(layer[i] is SurfaceTile tile) || tile.potentialRoads == null || tile.WaterCovered) continue;
                Vector3 c0 = centers[i];
                if (c0.sqrMagnitude < 1e-6f) continue;
                for (int k = 0; k < tile.potentialRoads.Count; k++)
                {
                    var link = tile.potentialRoads[k];
                    int nid = link.neighbor.tileId;
                    if (link.road == null) continue;
                    if (nid < 0 || nid >= centers.Length || centers[nid].sqrMagnitude < 1e-6f) continue;

                    RoadDef? back = RoadLinkBack(layer, nid, i);           // 同上：数据不对称时本侧也要画
                    if (nid < i && back != null) continue;

                    Vector2 a = PatchPixel(c0, capCenter, t1, t2, thetaMaxRad, size);
                    Vector2 b = PatchPixel(centers[nid], capCenter, t1, t2, thetaMaxRad, size);
                    for (int L = 0; L < layers.Count; L++)
                    {
                        float w = Mathf.Max(link.road.GetLayerWidth(layers[L]), back != null ? back.GetLayerWidth(layers[L]) : 0f);
                        if (w <= 0f) continue;
                        float half = Mathf.Clamp(w * 0.4f * RoadWidthScale, 0.06f, 0.28f) * tilePx;
                        byLayer[L].Add(new PFViewSurfaceDetails.LineStamp
                        { ax = a.x, ay = a.y, bx = b.x, by = b.y, halfWidth = half });
                    }
                }
            }

            int drawn = 0;
            for (int L = 0; L < layers.Count; L++)
            {
                Color col = layers[L].color;
                var c32 = new Color32((byte)(col.r * 255f), (byte)(col.g * 255f), (byte)(col.b * 255f), 255);
                for (int i = 0; i < byLayer[L].Count; i++)
                    PFViewSurfaceDetails.DrawSegment(px, size, byLayer[L][i], c32, RoadAlpha);
                drawn += byLayer[L].Count;
            }
            return drawn;
        }

        /// <summary>河：一条链两侧存的 RiverDef 取较宽的那个（河道会降级 ⇒ 两侧 def 可能不同）。null 当 0 处理。</summary>
        private static float RiverHalfWidthTiles(RiverDef? a, RiverDef? b)
        {
            float wa = a != null ? a.widthOnWorld : 0f;
            float wb = b != null ? b.widthOnWorld : 0f;
            return Mathf.Max(wa, wb) * 0.5f;
        }

        /// <summary>到对面那格找同一条链（河/路两侧各存一份）；**没找到 ⇒ null = 数据不对称**，
        /// 调用方据此决定"本侧必须自己画"（否则会留缝）。</summary>
        private static RiverDef? RiverLinkBack(PlanetLayer layer, int neighbourTile, int fromTile)
        {
            if (layer[neighbourTile] is SurfaceTile nt && nt.potentialRivers != null)
                for (int k = 0; k < nt.potentialRivers.Count; k++)
                    if (nt.potentialRivers[k].neighbor.tileId == fromTile)
                        return nt.potentialRivers[k].river;
            return null;
        }

        private static RoadDef? RoadLinkBack(PlanetLayer layer, int neighbourTile, int fromTile)
        {
            if (layer[neighbourTile] is SurfaceTile nt && nt.potentialRoads != null)
                for (int k = 0; k < nt.potentialRoads.Count; k++)
                    if (nt.potentialRoads[k].neighbor.tileId == fromTile)
                        return nt.potentialRoads[k].road;
            return null;
        }

        /// <summary>
        /// **口径自检（同口径比：存盘像素 vs 存盘像素）**：
        /// · ① 一个海洋格中心的像素（= 数据区里真实画出来的海洋）；
        /// · ② 盘缘余量带上的一个像素（uv (0.995,0.5)，= 清屏色，也就是**基底球同款**的底色）；
        /// · ③ 图角（uv (0.03,0.03)，圆盘之外 ⇒ **纯**清屏色）。
        /// 报 ①/③（海洋 vs 底色，也就是用户看的那条）与 ②/③（盘缘 vs 纯底色；两者本该一模一样 ⇒ 不相等说明余量带里有东西）。
        /// ⚠ 2026-10-11 教训：上一版拿 **`bgCol`（线性值）** 去比**像素字节**，两把尺子 ⇒ 报了个 1.00 的假绿（真正的差异在编码上）。
        /// </summary>
        private static string OceanColorCheck(Color32[] px, PlanetLayer layer, Vector3[] centers,
            Vector3 capCenter, Vector3 t1, Vector3 t2, float thetaMaxRad)
        {
            // `px` 由调用方回读一次并传入（M1#1）；本核对读的时候，细节那一步已把改动上传过，
            // 但"上传不改缓冲里的值" ⇒ 这份 `px` 与原来"这里再读一次"的数值逐位相同。
            Color32 Sample(float u, float v)
            {
                int x = Mathf.Clamp((int)(u * PatchSize), 0, PatchSize - 1);
                int y = Mathf.Clamp((int)(v * PatchSize), 0, PatchSize - 1);
                return px[y * PatchSize + x];
            }
            static float Lum(Color32 c) => (c.r + c.g + c.b) / 3f;

            Color32 corner = Sample(0.03f, 0.03f);          // 纯清屏色（圆盘之外）
            Color32 ring = Sample(0.995f, 0.5f);            // 盘缘余量带
            float ringRatio = Lum(corner) > 1f ? Lum(ring) / Lum(corner) : 0f;

            // 找一个海洋格（水格 = elevation <= 0；用与基底同源的那一档 biome 判据更稳：这里直接按"水格"找）
            for (int t = 0; t < layer.TilesCount; t++)
            {
                if (centers[t].sqrMagnitude < 1e-6f) continue;
                var tile = layer[t];
                if (tile == null || !tile.WaterCovered) continue;

                Vector2 uv = PatchUv(centers[t], capCenter, t1, t2, thetaMaxRad);
                Color32 c = Sample(uv.x, uv.y);
                float ratio = Lum(corner) > 1f ? Lum(c) / Lum(corner) : 0f;
                return $"口径自检：海洋格{t}像素({c.r},{c.g},{c.b}) ÷ 纯底色({corner.r},{corner.g},{corner.b}) ⇒ {ratio:F2}{(ratio > 1.3f || ratio < 0.77f ? "  ⚠" : "  ✓")}｜盘缘({ring.r},{ring.g},{ring.b}) ÷ 纯底色 ⇒ {ringRatio:F2}{(ringRatio > 1.15f || ringRatio < 0.87f ? "  ⚠ 盘缘带里有东西（本该=纯底色）" : "  ✓")}";
            }
            return $"口径自检：没有水格可采 ⇒ 只报盘缘 ÷ 纯底色 = {ringRatio:F2}";
        }

        /// <summary>
        /// 取一张贴图的**平均色**（GPU 逐级降采样到 1×1 再回读 4 字节）—— 用作烘焙的清屏色 = "盘内没画到的地方的底"。
        /// 为什么不用 CPU 采样：游戏内贴图默认 `isReadable = false` ⇒ `GetPixels` 会抛。
        /// 失败（没有可用的 shader/render texture）⇒ 返回兜底色，绝不抛。
        /// </summary>
        private static Color AverageColor(Texture2D tex, Color fallback)
        {
            RenderTexture? src = null, cur = null;
            try
            {
                int w = 64, h = 64;
                src = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(tex, src);
                cur = src;
                while (w > 1 || h > 1)
                {
                    int nw = Mathf.Max(1, w / 2), nh = Mathf.Max(1, h / 2);
                    var next = RenderTexture.GetTemporary(nw, nh, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(cur, next);
                    if (cur != src) RenderTexture.ReleaseTemporary(cur);
                    cur = next;
                    w = nw; h = nh;
                }

                RenderTexture.active = cur;
                var one = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                one.ReadPixels(new Rect(0f, 0f, 1f, 1f), 0, 0);
                one.Apply();
                RenderTexture.active = null;
                var c = one.GetPixel(0, 0);
                UnityEngine.Object.Destroy(one);
                return new Color(c.r, c.g, c.b, 1f);
            }
            catch (Exception ex)
            {
                Log.Warning("[PFView·烘焙] 平均色取得失败 ⇒ 用兜底色：" + ex.Message);
                return fallback;
            }
            finally
            {
                RenderTexture.active = null;
                if (cur != null && cur != src) RenderTexture.ReleaseTemporary(cur);
                if (src != null) RenderTexture.ReleaseTemporary(src);
            }
        }

        /// <summary>
        /// 建"补丁平面"网格：格顶点的位置 = (u*2-1, v*2-1, 0)，其中 (u,v) = `PatchUv(格顶点方向)`
        /// （正交相机 `orthographicSize=1` + 正方形 RT ⇒ 视野 **x,y ∈ [-1,1]**）。
        /// UV 仍用格内切平面归一化坐标（与世界视图同口径：一格一张完整 biome 贴图）。
        /// 圆盘没有经度缝 ⇒ 旧版"跨缝格发两份拷贝"那套特例**整块删除**。
        /// </summary>
        private static Mesh BuildBakeMesh(PlanetLayer layer, Vector3[] centers,
            Vector3 capCenter, Vector3 patchT1, Vector3 patchT2, float thetaMaxRad, out Material[] biomeMats)
        {
            var verts = layer.UnsafeVerts;
            var offsets = layer.UnsafeTileIDToVerts_offsets;
            int tiles = layer.TilesCount;

            var bucketOf = new Dictionary<BiomeDef, int>();
            var bucketBiomes = new List<BiomeDef>();
            var bVerts = new List<List<Vector3>>();
            var bUvs = new List<List<Vector2>>();
            var bTris = new List<List<int>>();

            for (int t = 0; t < tiles; t++)
            {
                Vector3 center = centers[t];
                if (center.sqrMagnitude < 1e-6f) continue;
                int start = offsets[t];
                int end = t + 1 < offsets.Length ? offsets[t + 1] : verts.Length;
                int polyVerts = end - start;
                if (polyVerts < 3) continue;

                var biome = layer[t].PrimaryBiome;
                if (!bucketOf.TryGetValue(biome, out int bucket))
                {
                    bucket = bucketBiomes.Count;
                    bucketOf[biome] = bucket;
                    bucketBiomes.Add(biome);
                    bVerts.Add(new List<Vector3>());
                    bUvs.Add(new List<Vector2>());
                    bTris.Add(new List<int>());
                }
                var bv = bVerts[bucket];
                var bu = bUvs[bucket];
                var bt = bTris[bucket];

                TileTangentFrame(center, out Vector3 t1, out Vector3 t2);
                float maxAbs = 1e-4f;
                for (int k = start; k < end; k++)
                {
                    Vector3 rel = verts[k] - center;
                    maxAbs = Mathf.Max(maxAbs, Mathf.Abs(Vector3.Dot(rel, t1)));
                    maxAbs = Mathf.Max(maxAbs, Mathf.Abs(Vector3.Dot(rel, t2)));
                }

                var uv = new Vector2[polyVerts];
                var pos = new Vector3[polyVerts];
                for (int k = 0; k < polyVerts; k++)
                {
                    Vector3 v = verts[k + start];
                    Vector2 puv = PatchUv(v, capCenter, patchT1, patchT2, thetaMaxRad);
                    pos[k] = new Vector3(puv.x * 2f - 1f, puv.y * 2f - 1f, 0f);
                    Vector3 rel = v - center;
                    uv[k] = new Vector2(
                        Vector3.Dot(rel, t1) / (2f * maxAbs) + 0.5f,
                        Vector3.Dot(rel, t2) / (2f * maxAbs) + 0.5f);
                }

                AddPolygon(bv, bu, bt, pos, uv);
            }

            var mesh = new Mesh { name = "PF_SurfacePatchBakeMesh" };
            var allVerts = new List<Vector3>();
            var allUvs = new List<Vector2>();
            var subTris = new int[bucketBiomes.Count][];
            for (int b = 0; b < bucketBiomes.Count; b++)
            {
                int baseIndex = allVerts.Count;
                allVerts.AddRange(bVerts[b]);
                allUvs.AddRange(bUvs[b]);
                var src = bTris[b];
                var dst = new int[src.Count];
                for (int i = 0; i < src.Count; i++) dst[i] = src[i] + baseIndex;
                subTris[b] = dst;
            }
            if (allVerts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(allVerts);
            mesh.SetUVs(0, allUvs);
            mesh.subMeshCount = bucketBiomes.Count;
            for (int b = 0; b < bucketBiomes.Count; b++) mesh.SetTriangles(subTris[b], b);
            mesh.RecalculateBounds();

            biomeMats = new Material[bucketBiomes.Count];
            for (int b = 0; b < bucketBiomes.Count; b++)
            {
                var tex = bucketBiomes[b]?.DrawMaterial != null
                    ? bucketBiomes[b].DrawMaterial.mainTexture as Texture2D
                    : null;
                biomeMats[b] = MaterialPool.MatFrom(tex != null ? tex : BaseContent.WhiteTex,
                    ShaderDatabase.WorldOverlayCutout, Color.white);
            }
            return mesh;
        }

        /// <summary>
        /// 格内切平面基（UV 口径的**唯一出处**，与 `GetTangentsToPlanet` 同构）：t1/t2 与格法线正交、彼此正交。
        /// 现在有**两个**用途，都建在同一份口径上：① 烘焙里"格内归一化 UV"（一格一张 biome 贴图）；
        /// ② 补丁的球冠中心正交基（`TryBake` 与 PFView 的补丁网格各算一次，同一函数 ⇒ 逐位一致）。
        ///
        /// ⚠ **纯算术实现，不用 `Quaternion.LookRotation`**：那是 ECall，headless（xunit）一调即抛
        /// `SecurityException: ECall 方法必须打包到系统模块中` —— 新加的用例当场抓到过（仓库里同族的坑还有
        /// `Quaternion.Euler` / `AngleAxis`，`ViewFrame` 2026-09-23 也为同一原因把它们换掉了）。
        /// 算式与 LookRotation(n, up) 等价（Gram–Schmidt + 右手定则，Unity 里 right = Cross(up, forward)）。
        /// </summary>
        internal static void TileTangentFrame(Vector3 center, out Vector3 t1, out Vector3 t2)
        {
            Vector3 n = center.sqrMagnitude > 1e-9f ? center.normalized : Vector3.up;
            Vector3 up = Mathf.Abs(n.y) > 0.999f ? Vector3.right : Vector3.up;

            Vector3 p1 = up - n * Vector3.Dot(up, n);          // up 去掉法线分量 ⇒ 落在切平面
            if (p1.sqrMagnitude < 1e-12f) p1 = Vector3.forward - n * Vector3.Dot(Vector3.forward, n);
            t1 = p1.normalized;
            t2 = Vector3.Cross(t1, n).normalized;              // 右手定则补齐（= LookRotation 的 +X）
        }

        private static void AddPolygon(List<Vector3> bv, List<Vector2> bu, List<int> bt,
            Vector3[] pos, Vector2[] uv)
        {
            int baseIndex = bv.Count;
            for (int k = 0; k < pos.Length; k++)
            {
                bv.Add(pos[k]);
                bu.Add(uv[k]);
            }
            for (int k = 1; k + 1 < pos.Length; k++)
            {
                bt.Add(baseIndex);
                bt.Add(baseIndex + k);
                bt.Add(baseIndex + k + 1);
                // 反面（同一片反向绕）：正交渲染的绕序不赌；同色 + cutout 不混合 ⇒ 无副作用
                bt.Add(baseIndex);
                bt.Add(baseIndex + k + 1);
                bt.Add(baseIndex + k);
            }
        }
    }
}
