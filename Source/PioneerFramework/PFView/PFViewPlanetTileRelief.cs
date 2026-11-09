using System;
using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 实时逐格表面的**几何起伏**（2026-11-09，目标 A：参考模组式近景）。
    ///
    /// 此前逐格面是平的：只有 biome 贴图与 hilliness 明暗。这里按该天体**冻结的**地形场
    /// （`CelestialMapGeneration.TryGetTerrain` ⇒ 与 worldgen / 地图生态同一份 `Sample`）给每个顶点做径向位移，
    /// 并用法线做真正的地形光照 ⇒ 山脊、坑缘、干河床都变成可读的几何。
    ///
    /// 关键不变量：
    /// · **位移按"顶点方向"采样，不按格心** —— 相邻格的公共顶点得到同一个位移 ⇒ 不会裂缝；
    /// · 法线用"每格面法线按公共顶点累加平均"：两遍还是同一次构建内完成，平滑且没有接缝；
    /// · 幅度做了可视化夸张（`ReliefExaggeration`），真实 3000 m 在 2500 km 半径上只有 0.1%、肉眼不可见；
    /// · 没有冻结地形场的层（原版地球 VanillaSurface）直接跳过 ⇒ 保持原有平面贴图路径；
    /// · 不写存档、不改地块，纯表现。
    /// </summary>
    public partial class MainTabWindow_PFView
    {
        /// <summary>径向位移的可视化夸张倍率（真实比例太细；8× 让 3000 m ≈ 1% 半径）。</summary>
        private const double ReliefExaggeration = 32.0;

        /// <summary>位移因子夹取（相对半径）：±3%，防止极端地形把球面戳穿/凹陷。 </summary>
        private const double ReliefClamp = 0.08;

        /// <summary>法线用的切向采样步长（弧度；约 0.7°，小于一格）。</summary>
        private const double ReliefDeltaRad = 0.012;

        /// <summary>起伏预计算每帧推进的格数（避免 65k 格星球一次卡满一帧）。</summary>
        private const int ReliefTileSlice = 1024;

        private static (int X, int Y, int Z) ReliefKey(Vector3 dir)
        {
            // 1e-5 量化：相邻格公共顶点的浮点末位差异不会裂开，同时远小于格距（~0.07 rad）。
            const double scale = 1e5;
            return ((int)Math.Round(dir.x * scale), (int)Math.Round(dir.y * scale), (int)Math.Round(dir.z * scale));
        }

        /// <summary>起伏预计算阶段：逐片积累顶点位移因子与平均法线。无地形场 ⇒ 直接跳到建块（保持平面）。</summary>
        private static void PrepareTileReliefSlice(TileSurfaceBuild build, PlanetLayer layer)
        {
            if (build.reliefFactors == null)
            {
                build.reliefRadiusMeters = layer is Celestial.CelestialSurfaceLayer surface
                    ? surface.Header.PhysicalRadiusMeters : 0.0;
                if (build.reliefRadiusMeters <= 0.0 || build.included == null || build.included.Count == 0)
                { build.phase = 4; return; }
                if (!Celestial.CelestialMapGeneration.TryGetTerrain(layer, out var terrain) || terrain == null)
                { build.phase = 4; return; }   // 原版地表（VanillaSurface）没有冻结地形场 ⇒ 保持平面贴图路径
                build.terrain = terrain;
                build.reliefFactors = new Dictionary<(int X, int Y, int Z), float>(build.included!.Count);
                build.reliefNormals = new Dictionary<(int X, int Y, int Z), Vector3>(build.included.Count);
                build.tileWet = new bool[build.tilesCount];
                build.reliefCursor = 0;
                Log.Message("[PFView·起伏] body=" + build.layer!.LayerID + " 启用几何起伏：物理半径=" +
                    (build.reliefRadiusMeters / 1000.0).ToString("F0") + " km｜夸张=" + ReliefExaggeration +
                    "×｜夹取=±" + (ReliefClamp * 100.0).ToString("F0") + "%｜格=" + build.included.Count);
            }

            var included = build.included!;
            int end = Math.Min(included.Count, build.reliefCursor + ReliefTileSlice);
            for (int i = build.reliefCursor; i < end; i++) AccumulateTileRelief(build, layer, included[i]);
            build.reliefCursor = end;
            build.reliefTiles = end;
            if (end >= included.Count)
            {
                float minFactor = float.MaxValue, maxFactor = float.MinValue;
                foreach (float factor in build.reliefFactors!.Values)
                { if (factor < minFactor) minFactor = factor; if (factor > maxFactor) maxFactor = factor; }
                Log.Message("[PFView·起伏] 预计算完成：顶点缓存=" + build.reliefFactors.Count + "｜位移因子范围=" +
                    minFactor.ToString("F4") + " ~ " + maxFactor.ToString("F4") + "（1=未位移）");
                build.phase = 4;
            }
        }

        /// <summary>把一个格的顶点位移与面法线累加进缓存（相邻格共用顶点 ⇒ 法线自然平均）。</summary>
        private static void AccumulateTileRelief(TileSurfaceBuild build, PlanetLayer layer, int tile)
        {
            var verts = layer.UnsafeVerts;
            var offsets = layer.UnsafeTileIDToVerts_offsets;
            int start = offsets[tile];
            int end = tile + 1 < offsets.Length ? offsets[tile + 1] : verts.Length;
            int count = end - start;
            if (count < 3) return;
            Vector3 centerDir = build.centers![tile].sqrMagnitude > 1e-9f ? build.centers[tile].normalized : Vector3.up;

            // 渲染用湿格：格心湿度超过阈值 ⇒ 深色河道格（读几何场，不改 tile 数据）。
            if (build.tileWet != null && build.terrain != null && tile < build.tileWet.Length)
            {
                double wetLat = Math.Asin(Math.Clamp(centerDir.y, -1f, 1f)) * 180.0 / Math.PI;
                double wetLon = Math.Atan2(centerDir.x, -centerDir.z) * 180.0 / Math.PI;
                build.tileWet[tile] = Celestial.CelestialPlanetTerrain.Sample(build.terrain, wetLon, wetLat, build.terrain.FieldSeed).Moisture >= 0.85;
            }
            var dirs = new Vector3[count];
            var points = new Vector3[count];
            for (int k = 0; k < count; k++)
            {
                Vector3 v = verts[start + k];
                Vector3 dir = v.sqrMagnitude > 1e-9f ? v.normalized : centerDir;
                dirs[k] = dir;
                points[k] = dir * ReliefFactorAt(build, dir);
            }

            Vector3 face = Vector3.Cross(points[1] - points[0], points[2] - points[0]);
            if (face.sqrMagnitude < 1e-14f) return;
            face.Normalize();
            if (Vector3.Dot(face, centerDir) < 0f) face = -face;

            var normals = build.reliefNormals!;
            for (int k = 0; k < count; k++)
            {
                var key = ReliefKey(dirs[k]);
                normals[key] = normals.TryGetValue(key, out var sum) ? sum + face : face;
            }
        }

        /// <summary>顶点方向的径向位移因子（1 = 未位移）。带缓存：共享顶点只采样一次。</summary>
        private static float ReliefFactorAt(TileSurfaceBuild build, Vector3 dir)
        {
            if (build.reliefFactors == null || build.terrain == null || build.reliefRadiusMeters <= 0.0) return 1f;
            var key = ReliefKey(dir);
            if (build.reliefFactors.TryGetValue(key, out float cached)) return cached;
            double lat = Math.Asin(Math.Clamp(dir.y, -1f, 1f)) * 180.0 / Math.PI;
            double lon = Math.Atan2(dir.x, -dir.z) * 180.0 / Math.PI;
            double elevation = Celestial.CelestialPlanetTerrain.Sample(build.terrain, lon, lat, build.terrain.FieldSeed).ElevationMeters;
            double factor = 1.0 + Math.Clamp(elevation * ReliefExaggeration / build.reliefRadiusMeters, -ReliefClamp, ReliefClamp);
            float result = (float)factor;
            build.reliefFactors[key] = result;
            return result;
        }

        /// <summary>顶点法线（无起伏/无缓存时退回径向）。</summary>
        private static Vector3 ReliefNormalAt(TileSurfaceBuild build, Vector3 dir)
        {
            if (build.reliefNormals == null) return dir;
            if (build.reliefNormals.TryGetValue(ReliefKey(dir), out var normal) && normal.sqrMagnitude > 1e-12f)
                return normal.normalized;
            return dir;
        }
    }
}