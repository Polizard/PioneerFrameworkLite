// ⚠ **已归档（2026-10-10）** —— 本文件**不参与编译**（位于工程目录之外，主工程按目录通配 ⇒ 天然排除）。
// 原因：用户 2026-10-10 裁定 —— "不论远近均使用烘焙贴图，取消档位区分"。
//       于是"近档：可见分格补丁"（原 `Source/PioneerFramework/PFView/MainTabWindow_PFView.Terrain.cs`）
//       整块退役；天体表面统一由**烘焙图**（`PFViewSurfaceBake`）承担，PFView 侧只剩"基底球 + 材质"。
// 替代品：烘焙（`PFView/PFViewSurfaceBake.cs`，调试页签手动按钮 + 旁挂 PNG）+ 基底球材质
//       （现仍在 `MainTabWindow_PFView.Terrain.cs` 里，只保留基底球那一半）。
// 取回代价：把本文件的内容按下面的分节搬回去 ——
//   ① `MainTabWindow_PFView` 那一半（近档机件 + `DestroyTerrainPatch`）并入
//      `Source/PioneerFramework/PFView/MainTabWindow_PFView.Terrain.cs`；
//   ② `PFViewTerrainTiers` 类单独成文件（并把它从 `已弃置` 里移回）；
//   ③ `PFViewSurfaceBake` 里的 `TileTangentFrame` 抄回 tiers 类（本次归档时它被搬进了烘焙器，
//      因为烘焙也要同一份"格内切平面 UV 口径"）；
//   ④ `MainTabWindow_PFView.Scene.cs` 里恢复那句 `TryDrawTerrainPatch(...)` 调用、
//      `Render.cs` 的 `CleanupResources` 里恢复 `DestroyTerrainPatch()`；
//   ⑤ 测试放回 `Source/PioneerFramework.Tests/PFViewTerrainTiersTests.cs`（见本文件末注释）。
//
// 当时它已经跑通过：日志出现过 `铺分格：格=3102 顶点=18612 材质=5`（用户也确认"看到渲染 biome"）。
// 退役不是因为它坏，而是因为**档位本身被取消**（烘焙图足够、机械更少）。

using System;
using System.Collections.Generic;
using PioneerFramework.Launch;   // GetSurfaceState（自转角速度、天体→PlanetLayer 的唯一出处）
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 的天体地表：**"泰南那套分格"在 PFView 比例尺下的近档**（2026-10-09，用户：
    /// "把PFview的天体改为和'世界'一样的渲染，只是尺寸变大…我们也贴视野内的Tile" /
    /// "先不考虑最近档，姑且就分两档就行了"）。
    ///
    /// ## 两档口径（本文件做近档）
    /// · **近档（本文件）**：天体近到"格子在屏幕上还看得见"时，画**原版世界网格的可见补丁** ——
    ///   逐格多边形 + 该格 biome 的贴图（就是世界视图里那套 ✅），整体按 PFView 的比例尺放大；
    /// · **远档（未做）**：退回现在的纯色球 —— 下一批接"手动烘焙贴图"（用户口径：调试按钮手动烘、
    ///   结果跟存档走、不自动烘）。
    ///
    /// ## 为什么"近档"必须只画**可见补丁**（性能账，读码核过）
    /// 原版网格是**一格一层、没有层级**（`PlanetLayer.InitializeLayer` 一次建好，格数 ≈ 10·3^(subdivisions+1)）：
    /// 月球层 subdivisions=5 ⇒ 几千格（随便画）；地球 Surface subdivisions=10 ⇒ **十万～百万格**
    /// （照抄整球 = 百万级顶点 ✗）。而 PFView 的相机很近时**视野里只有几十格**（30 km 高度一屏还没一格大）
    /// ⇒ 只画可见帽里的那些，成本与距离成反比、天然有界 ✅。
    /// 档位判据也因此是"**可见格数预算**"而不是拍死的距离：估算可见帽占整球的比例 × 总格数，超预算就退回球。
    ///
    /// ## 坐标系（这块必须写清楚）
    /// · 层的顶点是**世界系**（行星固定系）视图单位（地球 radius=100、月球 30）；PFView 是**米制绝对系**
    ///   （1 单位 = 10 m）⇒ 网格顶点乘 `MetersPerUnit × MetersToView` = **5000** 落到 PFView 单位；
    /// · 世界系 → PFView 的绝对系 = **自转**：用 `SurfaceFrame.FixedToEci`（唯一权威）在基向量上的像反推相位
    ///   （与 `ViewFrame.ToViewRotation` 同一手法，不另写符号），放进绘制矩阵的旋转里 —— 网格保持**静态**
    ///   （只在相机/距离显著变化时重建），自转每帧从矩阵走 ✅；
    /// · 可见帽判据同样在**网格系**里算（相机向量先过自转的逆）。
    ///
    /// ## 纪律
    /// · 运行时 `Mesh`/`Material` **必须显式销毁**（浮雕那条教训，`CleanupResources` 是唯一清理口）；
    /// · 顶点数可能超 65535 ⇒ 显式 `indexFormat = UInt32`（否则一上地球就坏）；
    /// · 重建**去抖**（0.25 s + 相机夹角 2° + 距离变化 10%），不在每帧路径重建；
    /// · 层的网格可能还没生成（world-gen 时机）⇒ 取不到就退回球，不许崩。
    /// </summary>
    public partial class MainTabWindow_PFView
    {
        // ════════════════════════════════════════════════════
        //  近档：可见分格补丁
        // ════════════════════════════════════════════════════

        /// <summary>两次重建之间的最短间隔（秒）——去抖，别让相机一转就每帧重建。</summary>
        private const float TerrainRebuildMinInterval = 0.25f;

        /// <summary>可见帽中心移动超过这个角度才重建。</summary>
        private const float TerrainRebuildAngleDeg = 2f;

        /// <summary>相机到天体中心距离变化超过这个比例才重建。</summary>
        private const float TerrainRebuildDistRatio = 0.10f;

        /// <summary>分格层相对基底球的**外扩比例**：同心面靠深度分不开（见 `UpdateCamera` 的近裁剪面说明），
        /// 往外让开 0.6%（行星半径量级 ⇒ 3×10³ 单位）就够，肉眼看不出来。</summary>
        internal const float TerrainPatchRadiusBias = 1.006f;

        /// <summary>世界视图单位 → PFView 单位（1 世界单位 = 50 km；1 PFView 单位 = 10 m）。</summary>
        private static float PlanetTileScale => (float)(SpaceConstants.MetersPerUnit * MetersToView);

        private sealed class TerrainPatch
        {
            public Mesh? mesh;
            public Material[]? materials;    // 一个 submesh 一个 biome 材质
            public int bodyId;
            public Vector3 capDir;           // 网格系（天体固定系）里的相机方向
            public float distance;           // PFView 单位
            public float builtAt;
            public int tileCount;            // 诊断读数：实际建进去几格
            public int vertCount;
            public int drawCount;
        }

        private TerrainPatch? terrainPatch;

        /// <summary>诊断读数节流：每 5 秒最多一行（读数必须无条件打，但不能刷屏）。</summary>
        private float terrainLogNextAt;

        // ── 地表数据的**覆盖球冠**（原版 `WorldGrid.CalculateViewCenterAndAngle`：`surfaceViewAngle = PlanetCoverage × 180`）：
        //    一个世界只生成一块球冠的地表数据（奥德赛默认覆盖率 0.5 ⇒ 约半壁），**帽外没有 tile 数据可言**。
        //    这里把"帽心 + 帽半径"量一次缓存下来（帽半径 = 全量扫一遍取最大夹角，约 1 ms，按体缓存）。──
        private struct TerrainCoverage
        {
            public Vector3 center;       // 网格系（层固定系）里的帽心方向
            public float radiusRad;      // 帽半径（最大夹角）
        }

        private int coverageBodyId = -1;
        private TerrainCoverage coverage;

        // 格心数组缓存（2026-10-10，用户点名要）：`GetTileCenter` 是 Burst 调用，全量扫 119,904 格实测 16.8 ms（Debug），
        // 而重建节流下每秒最多扫 4 次 ⇒ 必须缓存。只缓存**当前天体**一份（Earth ≈ 1.4 MB）。
        private int centersBodyId = -1;
        private Vector3[]? tileCenters;

        private Vector3[] GetTileCenters(CelestialBodyDef body, PlanetLayer layer)
        {
            if (centersBodyId == body.bodyId && tileCenters != null && tileCenters.Length == layer.TilesCount)
                return tileCenters;

            var arr = new Vector3[layer.TilesCount];
            for (int t = 0; t < arr.Length; t++) arr[t] = layer.GetTileCenter(t);
            tileCenters = arr;
            centersBodyId = body.bodyId;
            return arr;
        }

        private TerrainCoverage GetCoverage(CelestialBodyDef body, PlanetLayer layer)
        {
            if (coverageBodyId == body.bodyId) return coverage;

            Vector3 center = layer.ViewCenter.sqrMagnitude > 1e-6f ? layer.ViewCenter.normalized : Vector3.back;
            var centersForCoverage = GetTileCenters(body, layer);
            float maxAngle = 0f;
            for (int t = 0; t < centersForCoverage.Length; t++)
            {
                Vector3 c = centersForCoverage[t];
                if (c.sqrMagnitude < 1e-6f) continue;
                maxAngle = Mathf.Max(maxAngle, Vector3.Angle(c.normalized, center));
            }
            coverage = new TerrainCoverage { center = center, radiusRad = maxAngle * Mathf.Deg2Rad };
            coverageBodyId = body.bodyId;
            return coverage;
        }
        private const float TerrainLogInterval = 5f;

        /// <summary>
        /// 近档尝试：那天体近到值得铺格、层也在，就画**可见补丁**并返回 true（调用方跳过纯色球）。
        /// 任何一条不满足都返回 false ⇒ 调用方照旧画球（远档/兜底）。
        /// </summary>
        private bool TryDrawTerrainPatch(CelestialBodyDef body, Vector3 bodyViewPos, float bodyRadiusView, Vector3 camPos)
        {
            // 护栏：这条路读的是**原版的** NativeArray（层没生成/已被释放都会抛）⇒ 新路失败必须退回纯色球，
            // 不能把 PFView 整套拖死。只报一次，避免每帧刷屏。
            try
            {
                return TryDrawTerrainPatchInner(body, bodyViewPos, bodyRadiusView, camPos);
            }
            catch (Exception ex)
            {
                if (!terrainPatchFailedLogged)
                {
                    terrainPatchFailedLogged = true;
                    Log.Warning("[PFView·地表] 分格补丁构建失败 ⇒ 退回纯色球（只报一次）：" + ex);
                }
                DestroyTerrainPatch();
                return false;
            }
        }

        private bool terrainPatchFailedLogged;

        private bool TryDrawTerrainPatchInner(CelestialBodyDef body, Vector3 bodyViewPos, float bodyRadiusViewOffset, Vector3 camPos)
        {
            if (body == null || renderCamera == null) return false;

            // 调用方传进来的是"外扩后的半径"（= 真实半径 × 1.006，见 Scene.cs）：反推真实半径与绘制缩放。
            float bodyRadiusView = bodyRadiusViewOffset / TerrainPatchRadiusBias;
            float drawScale = PlanetTileScale * (bodyRadiusViewOffset / Mathf.Max(bodyRadiusView, 1e-3f));

            Vector3 camLocal = camPos - bodyViewPos;
            float dist = camLocal.magnitude;
            if (dist < 1e-3f || bodyRadiusView < 1f) return false;

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var layer = GetSurfaceState.GetLayerForBody(mgr, body.bodyId);
            if (layer == null || layer.TilesCount <= 0 || layer.UnsafeVerts.Length == 0) return false;   // 层没生成 ⇒ 兜底

            // 自转（世界系 → PFView 绝对系）——和 ViewFrame.ToViewRotation 同手法：从唯一权威取相位
            Quaternion spin = FixedToEciRotation(body.bodyId);
            Vector3 camDirInMeshFrame = (Quaternion.Inverse(spin) * camLocal).normalized;

            // ── 档位判据：屏幕地面足迹里的格子数 + 格子像素数 ⇒ 超预算/太小就退回球（这就是"远档"的入口）──
            float d = Vector3.Distance(bodyViewPos, camPos);
            float cosCap = PFViewTerrainTiers.FacingCapCosine(bodyRadiusView, d);
            float tileSizeView = layer.AverageTileSize * PlanetTileScale;
            float tilePx = PFViewTerrainTiers.TileScreenPixels(tileSizeView, Mathf.Max(d - bodyRadiusView, 1f),
                renderCamera.pixelHeight, renderCamera.fieldOfView);
            int estimatedTiles = PFViewTerrainTiers.EstimatedVisibleTiles(
                layer.TilesCount, bodyRadiusView, d, renderCamera.fieldOfView, renderCamera.aspect);
            bool useTiles = PFViewTerrainTiers.ShouldUseTiles(estimatedTiles, tilePx);

            float now = Time.realtimeSinceStartup;
            // 诊断读数（每 5 s 一行，无条件打）：档位判据的每个输入 + 实际建了多少 —— 下次"看不见格子"时靠它定案
            bool wantLog = now >= terrainLogNextAt;
            if (wantLog) terrainLogNextAt = now + TerrainLogInterval;

            // 覆盖球冠：帽外根本没有 tile 数据 ⇒ 直接了断（不扫、不报"空"，如实说清原因）
            var cov = GetCoverage(body, layer);
            float camToCoverageDeg = Vector3.Angle(camDirInMeshFrame, cov.center);
            float coverageReachDeg = cov.radiusRad * Mathf.Rad2Deg + Mathf.Acos(cosCap) * Mathf.Rad2Deg;
            if (camToCoverageDeg > coverageReachDeg + 1f)
            {
                if (wantLog)
                    Log.Message(string.Format(
                        "[PFView·地表] body={0} 覆盖率={1:P0} ⇒ **在覆盖帽外**（帽心距 {2:F0}°，帽半径 {3:F0}°）⇒ 退回纯色球（世界只生成了这一块的地形数据）",
                        body.defName, Find.World?.PlanetCoverage ?? 0f, camToCoverageDeg, cov.radiusRad * Mathf.Rad2Deg));
                return false;
            }
            long scanT0 = wantLog ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

            if (!useTiles)
            {
                if (wantLog)
                    Log.Message(string.Format(
                        "[PFView·地表] body={0} 层格={1} 均格={2:F3}(世界单位) 距={3:F0} est={4} px={5:F1} ⇒ **不铺分格**（{6}）",
                        body.defName, layer.TilesCount, layer.AverageTileSize, d, estimatedTiles, tilePx,
                        tilePx < PFViewTerrainTiers.TileMinScreenPixels ? "格太小" : "可见格数超预算"));
                return false;
            }
            bool needRebuild = terrainPatch == null
                || terrainPatch.bodyId != body.bodyId
                || terrainPatch.mesh == null
                || (now - terrainPatch.builtAt >= TerrainRebuildMinInterval
                    && (Vector3.Angle(terrainPatch.capDir, camDirInMeshFrame) >= TerrainRebuildAngleDeg
                        || Mathf.Abs(d - terrainPatch.distance) / Mathf.Max(terrainPatch.distance, 1e-3f) >= TerrainRebuildDistRatio));

            TerrainBuildDiag diag = default;
            if (needRebuild)
            {
                DestroyTerrainPatch();
                terrainPatch = BuildTerrainPatch(body, layer, camDirInMeshFrame, cosCap, d, now,
                    bodyViewPos, spin, tileSizeView * 0.5f, drawScale, out diag);
            }

            var patch = terrainPatch;
            if (patch?.mesh == null || patch.materials == null)
            {
                // 第三条静默路：裁剪把全部格都剔掉了（或建网格返回空）⇒ 退回球。也记一笔，否则"什么都看不到"无从判断。
                if (wantLog)
                {
                    var mgr2 = Current.Game?.GetComponent<CelestialBodyManager>();
                    double omega2 = GetSurfaceState.RotationOmega(mgr2, body.bodyId);
                    double tick2 = TimeScaleManager.GetPhysicsTickSafe();
                    Log.Message(string.Format(
                        "[PFView·地表] body={0} 层格={1} 距={2:F0} 均格={3:F3} est={4} px={5:F1} ⇒ **建出来是空的**｜" +
                        "扫描={6} 面朝过={7} 视锥过={8} maxDot={9:F4} cosCap={10:F4}｜相机方向(网格系)=({11:F3},{12:F3},{13:F3})",
                        body.defName, layer.TilesCount, d, layer.AverageTileSize, estimatedTiles, tilePx,
                        diag.scanCount, diag.facingPass, diag.frustumPass, diag.maxDot, cosCap,
                        camDirInMeshFrame.x, camDirInMeshFrame.y, camDirInMeshFrame.z));
                    // 视锥判定的决定性读数：最大点积那格（就是"正对相机"的那格）在相机空间里被判成什么
                    if (diag.firstTileId >= 0)
                    {
                        Vector3 c = layer.GetTileCenter(diag.firstTileId);
                        Vector3 tilePos = bodyViewPos + spin * (c * (PlanetTileScale * TerrainPatchRadiusBias));
                        Vector3 cs = renderCamera.transform.InverseTransformPoint(tilePos);
                        float halfFov = renderCamera.fieldOfView * 0.5f * Mathf.Deg2Rad;
                        float tanV = Mathf.Tan(halfFov);
                        float tanH = tanV * renderCamera.aspect;
                        float margin = (tileSizeView * 0.5f) / Mathf.Max(Mathf.Cos(halfFov), 0.001f);
                        Log.Message(string.Format(
                            "[PFView·地表·视锥] tile={0} 相机空间=({1:F0},{2:F0},{3:F0}) 限=({4:F0}横,{5:F0}纵) near={6:F2} far={7:F0}｜" +
                            "相机位={8:F0} 相机前={9:F3},{10:F3},{11:F3}｜我的 camPos={12:F0}｜中心在视锥内={13}",
                            diag.firstTileId, cs.x, cs.y, cs.z, tanH * Mathf.Abs(cs.z) + margin, tanV * Mathf.Abs(cs.z) + margin,
                            renderCamera.nearClipPlane, renderCamera.farClipPlane,
                            renderCamera.transform.position.magnitude,
                            renderCamera.transform.forward.x, renderCamera.transform.forward.y, renderCamera.transform.forward.z,
                            camPos.magnitude,
                            SphereInFrustum(bodyViewPos, bodyRadiusView * 0.5f)));
                    }

                    // 权威换算自检：拿第一格比对"mod 的 tile→ECI"与"我的 网格×自转"
                    if (diag.firstTileId >= 0)
                    {
                        DVector3 authoritative = GetSurfaceState.TileToEci(layer, diag.firstTileId, body.radius, omega2, tick2);
                        Vector3 mineUnity = spin * (GetTileCenters(body, layer)[diag.firstTileId] * drawScale);
                        DVector3 mineMeters = (DVector3)mineUnity / MetersToView;
                        Log.Message(string.Format(
                            "[PFView·地表·自检] tile={0}｜权威 ECI={1:F0}km｜我的={2:F0}km｜差={3:F1}km（>1 km 就是自转/换系错了）",
                            diag.firstTileId,
                            authoritative.magnitude / 1000.0, mineMeters.magnitude / 1000.0,
                            (authoritative - mineMeters).magnitude / 1000.0));
                    }
                }
                return false;
            }

            Matrix4x4 mtx = Matrix4x4.TRS(bodyViewPos, spin, Vector3.one * drawScale);
            for (int i = 0; i < patch.materials.Length; i++)
                Graphics.DrawMesh(patch.mesh, mtx, patch.materials[i], SceneLayer, null, i);

            if (wantLog)
            {
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - scanT0) * (1000.0 / System.Diagnostics.Stopwatch.Frequency);
                Log.Message(string.Format(
                    "[PFView·地表] body={0} 层格={1} 均格={2:F3} 距={3:F0} est={4} px={5:F1} ⇒ **铺分格**：格={6} 顶点={7} 材质={8}｜" +
                    "（本帧含建网格 {9:F1} ms，重建节流={10:F2}s）",
                    body.defName, layer.TilesCount, layer.AverageTileSize, d, estimatedTiles, tilePx,
                    patch.tileCount, patch.vertCount, patch.drawCount, ms, TerrainRebuildMinInterval));
            }
            return true;
        }

        /// <summary>
        /// 世界固定系 → PFView 绝对系的旋转 = `SurfaceFrame.FixedToEci`（= Ry(−ωt)）。
        ///
        /// ⚠ 2026-10-10 修：上一版拿 `atan2(z, x)` 当相位、直接喂半角 ⇒ **转成了 Ry(+ωt)**（差一个符号），
        /// 误差 = 2ωt 随时间长大 —— 表现就是"有时铺出一片错位的格、有时一格都不剩"，而**离线数学测试看不出来**
        /// （那条路当时没被"四元数是否等于函数"覆盖）。现在不碰任何角度约定：
        /// 把 `FixedToEci` 作用在三个基向量上取出**矩阵的三列**，纯算术还原四元数（`RotationFromColumns`）。
        /// 判据：`PFViewTerrainTiersTests.Spin_Quaternion_AgreesWithFixedToEci`（四元数作用 = 函数作用，逐向量比）。
        /// </summary>
        private static Quaternion FixedToEciRotation(int bodyId)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            double omega = GetSurfaceState.RotationOmega(mgr, bodyId);
            double tick = TimeScaleManager.GetPhysicsTickSafe();
            return PFViewTerrainTiers.SurfaceFrameRotation(omega, tick);
        }

        /// <summary>
        /// 建可见补丁：逐格多边形 → 按 biome 分桶（一个 biome 一个 submesh）→ 一张 Mesh。
        /// 顶点是**网格系 PFView 单位**（层顶点 × 5000），自转由绘制矩阵给；UV 是格内切平面归一化坐标
        /// （= 世界视图里"一格一张 biome 贴图"的同一口径）。
        /// </summary>
        /// <summary>建网格的诊断读数（空结果时唯一能定案的东西：面朝过几个、视锥又剔了几个、最大点积多少）。</summary>
        private struct TerrainBuildDiag
        {
            public int facingPass;      // 过面朝判据的格数
            public int frustumPass;     // 过视锥判据的格数
            public float maxDot;        // 全量扫描里"格法线 · 相机方向"的最大值（判据是否根本没够着）
            public int firstTileId;
            public int scanCount;
        }

        private TerrainPatch? BuildTerrainPatch(CelestialBodyDef body, PlanetLayer layer, Vector3 camDirInMeshFrame,
            float cosCap, float distance, float now, Vector3 bodyViewPos, Quaternion spin, float tileRadiusView,
            float drawScale, out TerrainBuildDiag diag)
        {
            var verts = layer.UnsafeVerts;
            var offsets = layer.UnsafeTileIDToVerts_offsets;
            var centers = GetTileCenters(body, layer);          // 缓存：全量扫描不再逐格调 Burst
            int tiles = Mathf.Min(layer.TilesCount, centers.Length);

            int builtTiles = 0;
            diag = new TerrainBuildDiag { maxDot = float.NegativeInfinity, firstTileId = -1 };
            var bucketOf = new Dictionary<BiomeDef, int>();
            var bucketBiomes = new List<BiomeDef>();
            var bucketVerts = new List<List<Vector3>>();
            var bucketNormals = new List<List<Vector3>>();
            var bucketUvs = new List<List<Vector2>>();
            var bucketTris = new List<List<int>>();

            for (int t = 0; t < tiles; t++)
            {
                Vector3 center = centers[t];        // 缓存数组：不再逐格调 Burst 取格心
                if (center.sqrMagnitude < 1e-6f) continue;
                Vector3 normal = center.normalized;
                diag.scanCount++;
                float dot = Vector3.Dot(normal, camDirInMeshFrame);
                if (dot > diag.maxDot) { diag.maxDot = dot; diag.firstTileId = t; }
                if (dot < cosCap) continue;                                     // ① 便宜的面朝裁剪（地平线外/背面，全量扫）
                diag.facingPass++;

                // ② 精确的视锥裁剪（幸存者才做；球体判定，用 PFView 自己的视锥助手）
                Vector3 tileViewPos = bodyViewPos + spin * (center * drawScale);   // 与绘制同一缩放（含外扩）
                if (!SphereInFrustum(tileViewPos, tileRadiusView)) continue;
                diag.frustumPass++;

                int start = offsets[t];
                int end = t + 1 < offsets.Length ? offsets[t + 1] : verts.Length;
                int polyVerts = end - start;
                if (polyVerts < 3) continue;

                builtTiles++;
                var biome = layer[t].PrimaryBiome;
                int bucket;
                if (!bucketOf.TryGetValue(biome, out bucket))
                {
                    bucket = bucketBiomes.Count;
                    bucketOf[biome] = bucket;
                    bucketBiomes.Add(biome);
                    bucketVerts.Add(new List<Vector3>());
                    bucketNormals.Add(new List<Vector3>());
                    bucketUvs.Add(new List<Vector2>());
                    bucketTris.Add(new List<int>());
                }

                var bv = bucketVerts[bucket];
                var bn = bucketNormals[bucket];
                var bu = bucketUvs[bucket];
                var bt = bucketTris[bucket];

                // 格内切平面基（UV 用）——与烘焙同一条口径（唯一出处见 `PFViewTerrainTiers.TileTangentFrame`）
                PFViewTerrainTiers.TileTangentFrame(center, out Vector3 t1, out Vector3 t2);

                // 先算一遍切平面坐标，用自身范围归一化到 0..1（不猜格尺寸，环上/极点都稳）
                int baseIndex = bv.Count;
                float maxAbs = 1e-4f;
                for (int k = start; k < end; k++)
                {
                    Vector3 rel = verts[k] - center;
                    maxAbs = Mathf.Max(maxAbs, Mathf.Abs(Vector3.Dot(rel, t1)));
                    maxAbs = Mathf.Max(maxAbs, Mathf.Abs(Vector3.Dot(rel, t2)));
                }
                for (int k = start; k < end; k++)
                {
                    Vector3 v = verts[k];
                    bv.Add(v);
                    bn.Add(normal);
                    Vector3 rel = v - center;
                    bu.Add(new Vector2(
                        Vector3.Dot(rel, t1) / (2f * maxAbs) + 0.5f,
                        Vector3.Dot(rel, t2) / (2f * maxAbs) + 0.5f));
                }
                // 扇形三角化（凸多边形；六边格照原版）。⚠ 绕序必须**朝外**：Standard 材质背面剔除，
                // 反了就是"日志说建好了、画面上一片空"（静默失败，2026-10-10 踩过）。用几何法线与径向比一次定序。
                Vector3 faceNormal = Vector3.Cross(verts[start + 1] - verts[start], verts[start + 2] - verts[start]);
                bool flip = Vector3.Dot(faceNormal, normal) < 0f;
                for (int k = 1; k + 1 < polyVerts; k++)
                {
                    if (!flip)
                    {
                        bt.Add(baseIndex);
                        bt.Add(baseIndex + k);
                        bt.Add(baseIndex + k + 1);
                    }
                    else
                    {
                        bt.Add(baseIndex);
                        bt.Add(baseIndex + k + 1);
                        bt.Add(baseIndex + k);
                    }
                }
            }

            if (bucketBiomes.Count == 0) return null;

            // ── 拼一张 Mesh：顶点缓冲统一，逐 submesh 给三角形 ──
            int totalVerts = 0, totalTris = 0;
            for (int b = 0; b < bucketBiomes.Count; b++)
            {
                totalVerts += bucketVerts[b].Count;
                totalTris += bucketTris[b].Count;
            }
            if (totalVerts == 0) return null;

            var allVerts = new List<Vector3>(totalVerts);
            var allNormals = new List<Vector3>(totalVerts);
            var allUvs = new List<Vector2>(totalVerts);
            var subTris = new int[bucketBiomes.Count][];
            var baseOffsets = new int[bucketBiomes.Count];
            for (int b = 0; b < bucketBiomes.Count; b++)
            {
                baseOffsets[b] = allVerts.Count;
                allVerts.AddRange(bucketVerts[b]);
                allNormals.AddRange(bucketNormals[b]);
                allUvs.AddRange(bucketUvs[b]);
                var src = bucketTris[b];
                var dst = new int[src.Count];
                for (int i = 0; i < src.Count; i++) dst[i] = src[i] + baseOffsets[b];
                subTris[b] = dst;
            }

            var mesh = new Mesh { name = "PF_BodyTerrain_" + body.bodyId };
            if (totalVerts > 65000) mesh.indexFormat = IndexFormat.UInt32;   // ⚠ 超 65535 必须显式给，否则直接坏
            mesh.SetVertices(allVerts);
            mesh.SetNormals(allNormals);
            mesh.SetUVs(0, allUvs);
            mesh.subMeshCount = bucketBiomes.Count;
            for (int b = 0; b < bucketBiomes.Count; b++) mesh.SetTriangles(subTris[b], b);
            mesh.RecalculateBounds();

            // 每 biome 一份材质。⚠ 这里**故意用 `renderMat`（Standard）而不是 `ToonMat`**：
            // 地形靠 `_MainTex` 出效果，而 `PF_Toon` 是否采样主贴图**没验证过** —— 不采样的话整片会是一片纯色、
            // 看起来和原来的纯色球一模一样（正是"看不见格子"的一种静默失败）。等 toon 验证过再切回去。
            Material baseMat = renderMat;
            var materials = new Material[bucketBiomes.Count];
            for (int b = 0; b < bucketBiomes.Count; b++)
            {
                var biome = bucketBiomes[b];
                var tex = biome?.DrawMaterial != null ? biome.DrawMaterial.mainTexture : null;
                var m = new Material(baseMat) { color = Color.white };
                if (tex != null) m.mainTexture = tex;
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                // 小自发光：与基底球同一理由（方向光会把夜面压成纯黑 ⇒ 行星夜侧的格子会"消失"）。
                if (tex != null && m.HasProperty("_EmissionColor"))
                {
                    m.EnableKeyword("_EMISSION");
                    m.SetTexture("_EmissionMap", tex);
                    m.SetColor("_EmissionColor", new Color(0.45f, 0.45f, 0.45f));
                }
                materials[b] = m;
            }

            return new TerrainPatch
            {
                mesh = mesh,
                materials = materials,
                bodyId = body.bodyId,
                capDir = camDirInMeshFrame,
                distance = distance,
                builtAt = now,
                tileCount = builtTiles,
                vertCount = totalVerts,
                drawCount = bucketBiomes.Count,
            };
        }

    /// PFView 天体地表的**纯判定**（无窗口状态、无 Unity 对象 ⇒ headless 可钉）。
    /// 从 `MainTabWindow_PFView` 里抽出来**不是为了好看**：那个类的静态构造会 `InitStaticResources()`（建网格/材质），
    /// headless 测试一碰它的静态成员就会炸 —— 抽成独立类，测试才碰得到。
    ///
    /// ⚠ 2026-10-09 修正（被测试当场抓出来的几何错误）：可见帽半角是 **`acos(R/d)`**（球心处看"还能看见的那片"的角半径），
    /// 我第一版写成了 `asin(R/d)` —— 那是**相机处看球盘的视半径**，两者差得远（贴地时 6.2° vs 83°）。
    /// 于是面朝判据就该是 `cos = R/d`（切点条件 `dot(n, 相机方向) ≥ R/d`），而"屏幕上到底有多少格"要用
    /// **屏幕地面足迹面积 ÷ 球面积**（不是可见帽比例 ✗：远看可见帽=半球，但屏幕只摊到一小片）。
    /// </summary>
    internal static class PFViewTerrainTiers
    {
        /// <summary>可见格预算：估算超过它就不铺 tile（⇒ 远档）。地球十万级、月球几千级，取 6000 兼顾。</summary>
        public const int MaxVisibleTiles = 20000;

        /// <summary>一格在屏幕上小于这个像素数就不值当铺（网格粒度已经看不出来了）。</summary>
        public const float TileMinScreenPixels = 2.5f;

        /// <summary>面朝判据：`dot(格法线, 相机方向) ≥ 本值` 才算在地平线内。返回 clamp(R/d)。</summary>
        public static float FacingCapCosine(float bodyRadius, float distance)
        {
            if (distance <= 1e-3f || bodyRadius <= 0f) return 1f;    // 退化：只留正下方（哨兵，不给 NaN）
            return Mathf.Clamp(bodyRadius / distance, -1f, 1f);
        }

        /// <summary>面朝帽占整球面积比 = (1 − cos)/2（远看 → 0.5 = 半球 ✅）。</summary>
        public static float FacingCapFraction(float cosCap) => (1f - cosCap) * 0.5f;

        /// <summary>一格在屏幕上占多少像素（distance 传**到地面**的距离 ≈ d − R）。</summary>
        public static float TileScreenPixels(float tileSizeView, float distance, float pixelHeight, float fovDeg)
        {
            if (distance <= 1e-3f || tileSizeView <= 0f) return 0f;
            float perPixel = 2f * Mathf.Tan(fovDeg * 0.5f * Mathf.Deg2Rad) / Mathf.Max(pixelHeight, 1f);   // 每像素覆盖的角正切
            return tileSizeView / distance / perPixel;
        }

        /// <summary>
        /// 屏幕上大约有多少格：**地面足迹面积 ÷ 每格面积**（每格面积 = 球面积 / 总格数）。
        /// 足迹 = 高度 × 两个方向的 FOV 张角（远/近都够用；超过整球面积就夹住 = 整球）。
        /// </summary>
        public static int EstimatedVisibleTiles(int tilesCount, float bodyRadius, float distance, float fovYDeg, float aspect)
        {
            if (tilesCount <= 0 || bodyRadius <= 0f) return 0;
            float altitude = Mathf.Max(distance - bodyRadius, 1f);
            float halfTanY = Mathf.Tan(fovYDeg * 0.5f * Mathf.Deg2Rad);
            float halfTanX = halfTanY * Mathf.Max(aspect, 0.01f);
            float footprint = (2f * altitude * halfTanX) * (2f * altitude * halfTanY);
            float sphereArea = 4f * Mathf.PI * bodyRadius * bodyRadius;
            float fraction = Mathf.Clamp01(footprint / sphereArea);
            return Mathf.Clamp(Mathf.CeilToInt(tilesCount * fraction), 0, tilesCount);
        }

        /// <summary>
        /// 世界固定系 → PFView 绝对系的旋转四元数 = `SurfaceFrame.FixedToEci`（Ry(−ωt)）。
        /// **生产路径就是调它**（`FixedToEciRotation` 转发），测试也直接钉它 ⇒ 不会出现"测试测的是复刻、生产是另一份"。
        /// </summary>
        public static Quaternion SurfaceFrameRotation(double omega, double tick)
        {
            return RotationFromColumns(
                (Vector3)SurfaceFrame.FixedToEci(new DVector3(1.0, 0.0, 0.0), omega, tick),
                (Vector3)SurfaceFrame.FixedToEci(new DVector3(0.0, 1.0, 0.0), omega, tick),
                (Vector3)SurfaceFrame.FixedToEci(new DVector3(0.0, 0.0, 1.0), omega, tick));
        }

        /// <summary>
        /// 由旋转矩阵的**三列**还原四元数（Shepperd/trace 法，纯算术 ⇒ headless 可钉）。
        /// 之所以要它：`SurfaceFrame.FixedToEci` 只给矢量、不给四元数；自己拿 `atan2` 反推相位
        /// 极易把符号写反（2026-10-10 就这么错过一次）⇒ 改成"列进四元数出"，不再有角度约定的空间。
        /// </summary>
        public static Quaternion RotationFromColumns(Vector3 colX, Vector3 colY, Vector3 colZ)
        {
            float m00 = colX.x, m10 = colX.y, m20 = colX.z;
            float m01 = colY.x, m11 = colY.y, m21 = colY.z;
            float m02 = colZ.x, m12 = colZ.y, m22 = colZ.z;
            float trace = m00 + m11 + m22;

            if (trace > 0f)
            {
                float s = Mathf.Sqrt(trace + 1f) * 2f;   // s = 4w
                return new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
            }
            if (m00 > m11 && m00 > m22)
            {
                float s = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f;   // s = 4x
                return new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            if (m11 > m22)
            {
                float s = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f;   // s = 4y
                return new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            {
                float s = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f;   // s = 4z
                return new Quaternion((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
            }
        }

        /// <summary>
        /// 格内切平面基（UV 口径的**唯一出处**，与 `GetTangentsToPlanet` 同构）：t1/t2 与格法线正交、彼此正交。
        /// 分格网格与烘焙网格**都用它**算格内 UV ⇒ 两条路的"一格一张贴图"永远一致。
        /// </summary>
        public static void TileTangentFrame(Vector3 center, out Vector3 t1, out Vector3 t2)
        {
            Vector3 n = center.sqrMagnitude > 1e-9f ? center.normalized : Vector3.up;
            Vector3 up = Mathf.Abs(n.y) > 0.999f ? Vector3.right : Vector3.up;
            Quaternion basis = Quaternion.LookRotation(n, up);
            t1 = basis * Vector3.up;
            t2 = basis * Vector3.right;
        }

        /// <summary>档位判据：格子太小（看不出粒度）或可见格数超预算（那是远档）⇒ 不用分格。</summary>
        public static bool ShouldUseTiles(int estimatedVisibleTiles, float tileScreenPixels)
        {
            if (estimatedVisibleTiles <= 0) return false;
            if (tileScreenPixels < TileMinScreenPixels) return false;
            return estimatedVisibleTiles <= MaxVisibleTiles;
        }
    }
}

/* 归档时随件退役的用例（`PFViewTerrainTiersTests`）：
   · VisibleCapCosine / FacingCapCosine 系列（可见帽判据）
   · EstimatedVisibleTiles / TileScreenPixels / ShouldUseTiles（档位判据）
   · SurfaceFrameRotation / RotationFromColumns（自转换系：四元数必须等于 FixedToEci —— 这条判据抓到过真符号错）
   现役只剩 `TileTangentFrame` 的用例（在 PFViewSurfaceBakeTests.cs），因为烘焙仍在用这套 UV 口径。
*/
