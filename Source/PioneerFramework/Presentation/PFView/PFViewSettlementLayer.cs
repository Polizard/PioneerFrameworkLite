using UnityEngine;

namespace PioneerFramework
{
    /// <summary>
    /// **PFView 定居点层（D-2）的纯核心**（2026-10-12 用户裁定：「**做：实时画一层（含夜灯）**」）。
    ///
    /// ## 为什么是"实时层"而不是烘进贴图
    /// **动态数据不进静态图**：定居点会出生 / 灭亡 / 易主，而地表贴图（烘焙补丁）是**事件级**静态产物
    /// （改一次要重烤）。⇒ 定居点每帧从 `Find.WorldObjects.Settlements` 现取现画——几十个 ⇒ 每帧成本可忽略。
    /// 这不是"按距离切档"（那条路已被用户裁定统一烘焙），而是**数据性质**决定的第二层。
    ///
    /// 本文件只有**纯算术**（尺寸换算 / 昼夜判据 / 光点 alpha 轮廓）；渲染那一半在
    /// `MainTabWindow_PFView.Settlements.cs`。
    /// </summary>
    internal static class PFViewSettlementLayer
    {
        /// <summary>标记直径 = **几格**（视觉旋钮）。1 格 ≈ `PlanetLayer.AverageTileSize` 世界单位（1 世界单位 = 50 km）。
        /// 2026-10-12 两轮口径：先「弥漫到周围一圈格子那么大」（3 格）⇒ 再看实测「**覆盖范围直径放大 4 倍**」
        /// ⇒ **12 格**（本格 + 周围两圈；地球档 ≈ 600 km 直径）。</summary>
        public const float MarkerTiles = 12.0f;

        /// <summary>噪点的细胞数（每边）：**粗**才禁得住远景缩小——8×8 在整球视角下每格只有 ~3 px ⇒ 糊成一个点
        /// （用户实测"还是只有一个圆点"）。5×5 ⇒ 每格 ~8 px，结构看得见。</summary>
        public const int SpeckCells = 5;

        /// <summary>
        /// 标记直径换算成**米**：`AverageTileSize`（世界单位）× `SpaceConstants.MetersPerUnit`（50 km/单位）× 格数。
        /// ⚠ 与地表网格**同一比例尺**（补丁的 km/texel、地块图那套都用同一个 `MetersPerUnit`）；
        /// 米→视图单位的换算留给调用方（`MetersToView` 在窗口里只有一处）。
        /// </summary>
        public static float MarkerSizeMeters(float averageTileSizeWorldUnits, float markerTiles = MarkerTiles)
        {
            float tileMeters = Mathf.Max(0.01f, averageTileSizeWorldUnits) * (float)SpaceConstants.MetersPerUnit;
            return tileMeters * Mathf.Max(0.01f, markerTiles);
        }

        /// <summary>
        /// **贴地（切平面）平片相对球面的最大下沉**：平片角点在离中轴 `s/√2` 处、球面在该处的下沉
        /// `(s/√2)²/(2R) = s²/(4R)`。⇒ 抬升量必须 **> 本值**，否则平片会被地表咬进去
        /// （用户 2026-10-12 两轮口径的先半圆后"贴地但不被遮挡"就是这条）。
        /// </summary>
        public static float TangentSag(float quadSize, float radius)
        {
            float r = Mathf.Max(1f, radius);
            return quadSize * quadSize / (4f * r);
        }

        /// <summary>
        /// **贴地抬升量** = 最大下沉 × 2（余量）+ 半径的 5e-4（**深度余量**：不与球面共面打架）。
        /// 地球档（R=5e5 视图单位 / 12 格 = 6e4）：下沉 ≈ 1.8e3 单位、抬升 ≈ 3.9e3 单位（半径的 0.8%，
        /// 球面尺度上看不见）。⚠ 这是"贴地但不被遮挡"的唯一手法：切平面 + 抬升，**不是** billboard。
        /// </summary>
        public static float TangentLift(float quadSize, float radius)
        {
            float r = Mathf.Max(1f, radius);
            return TangentSag(quadSize, r) * 2f + r * 5e-4f;
        }

        /// <summary>噪点贴图的随机种子（确定性：同种子 ⇒ 同一张图；改这个数就换一片"城区纹理"）。</summary>
        public const int SpeckSeed = 20261012;

        /// <summary>
        /// **确定性哈希**（[0,1)）：噪点贴图与用例共用（同输入 ⇒ 同输出；**不碰 `UnityEngine.Random`**，
        /// 免得污染别人的随机流/不可复现）。
        /// </summary>
        public static float Hash01(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFFu) / 16777216f;
            }
        }

        /// <summary>
        /// **弥散噪点**的 alpha（贴图与用例共用这一份）——**派系科技差就长在这三个参数上**（用户 2026-10-12 口径）：
        /// · **覆盖面积** = `coverage`（灯光铺开的半径占贴图的比；低科技只在中心一小片，高科技铺满）；
        /// · **密度** = `density`（`SpeckCells²` 个细胞里**亮格**的比例；低科技零零几盏，高科技成片）；
        /// · **亮度** = 调用方按 `LightAlphaFor(强度)` 乘进颜色 alpha（贴图本身只给形状）。
        /// 每颗灯点在格内**位置/半径/亮度都抖动**（哈希）且**边缘锐**（`SpeckAlpha`）⇒ 是"一片一粒粒的城区灯"。
        /// ⚠ 细胞取 **5×5（粗）**：远景整球视角下每格 ~8 px，结构看得见；8×8 只有 ~3 px ⇒ 糊成一个点（实测踩过）。
        /// ⚠ 贴图**按科技档预烘、每档只生成一次**（不是每帧算噪点）——每帧成本为零。
        /// </summary>
        public static float SpeckleAlphaAt(float dx, float dy, int seed, float coverage, float density)
        {
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            float cover = CoverageFade(r, coverage);
            if (cover <= 0f) return 0f;

            // 细胞坐标（[0, SpeckCells)）
            float gx = (dx + 1f) * 0.5f * SpeckCells;
            float gy = (dy + 1f) * 0.5f * SpeckCells;
            int cx = Mathf.FloorToInt(gx);
            int cy = Mathf.FloorToInt(gy);
            if (cx < 0 || cy < 0 || cx >= SpeckCells || cy >= SpeckCells) return 0f;

            if (Hash01(cx, cy, seed) >= Mathf.Clamp01(density)) return 0f;   // 这格不亮

            // 格内一颗灯：位置/半径/亮度都抖动
            float px = cx + 0.25f + 0.50f * Hash01(cx, cy, seed + 131);
            float py = cy + 0.25f + 0.50f * Hash01(cx, cy, seed + 977);
            float radius = 0.16f + 0.22f * Hash01(cx, cy, seed + 313);
            float jitter = 0.55f + 0.45f * Hash01(cx, cy, seed + 555);

            float ddx = gx - px, ddy = gy - py;
            float d = Mathf.Sqrt(ddx * ddx + ddy * ddy) / radius;            // 1 = 点半径
            return Mathf.Clamp01(cover * SpeckAlpha(d) * jitter);
        }

        /// <summary>**覆盖面积轴**：科技强度 ⇒ 灯光铺开的半径比（动物 0.35 → Archotech 1.0）。</summary>
        public static float CoverageFor(float techIntensity) => 0.35f + 0.65f * Mathf.Clamp01(techIntensity);

        /// <summary>**密度轴**：科技强度 ⇒ 亮格比例（动物 0.25 → Archotech 0.95）。</summary>
        public static float DensityFor(float techIntensity) => 0.25f + 0.70f * Mathf.Clamp01(techIntensity);

        /// <summary>
        /// **派系科技水平 → 灯光强度**（用户 2026-10-12：「根据派系科技水平改变光照」）。
        /// 单调不减：动物/新石器基本没灯；中世纪零星；工业起明显；太空/超凡最亮；Archotech = 1。
        /// `Undefined`（无派系/未定义）给中间值——宁可"像座城"，别让人以为漏画了。
        /// </summary>
        public static float TechIntensity(RimWorld.TechLevel tech)
        
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   => tech switch
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   {
            RimWorld.TechLevel.Animal => 0.10f,
            RimWorld.TechLevel.Neolithic => 0.20f,
            RimWorld.TechLevel.Medieval => 0.35f,
            RimWorld.TechLevel.Industrial => 0.60f,
            RimWorld.TechLevel.Spacer => 0.80f,
            RimWorld.TechLevel.Ultra => 0.92f,
            RimWorld.TechLevel.Archotech => 1.00f,
            _ => 0.50f,
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   };

        /// <summary>灯光 alpha：`0.25 + 0.7×强度`（动物 ≈0.32 ⇒ 几乎看不见；Archotech = 0.95）。</summary>
        public static float LightAlphaFor(float techIntensity) => 0.25f + 0.70f * Mathf.Clamp01(techIntensity);

        /// <summary>
        /// **昼夜判据**：地表外法线与"指向太阳"的夹角 ⇒ 背阳 = 夜面（点灯）。
        /// 两个向量必须**同一个系**（调用方用视图系：法线 = `selfRot × 固连方向`、太阳 = `sunDirectionView`，同源）。
        /// </summary>
        public static bool IsNightSide(Vector3 surfaceNormalView, Vector3 toSunView)
            => Vector3.Dot(surfaceNormalView, toSunView) < 0f;

        /// <summary>
        /// **覆盖轮廓**（贴图生成与用例共用这一份）：半径 `r` 处、覆盖半径 `coverage` 的亮度权重。
        /// **平台 + 快收**：`r/coverage ≤ 0.75` 一律 1（"整块都亮"——用户要的是**弥漫**，不是中间一坨），
        /// 0.75→1 快速收到 0（边界清楚 = 锐）。
        /// </summary>
        public static float CoverageFade(float r, float coverage)
        {
            float t = Mathf.Clamp01(1f - r / Mathf.Max(1e-3f, coverage));
            return Mathf.Clamp01(t / 0.25f);
        }

        /// <summary>`coverage = 1` 的退化情形（贴图整幅为覆盖区），保留给用例与将来复用。</summary>
        public static float GlowAlphaAt(float dx, float dy)
            => CoverageFade(Mathf.Sqrt(dx * dx + dy * dy), 1f);

        /// <summary>
        /// **单颗灯点的锐度轮廓**：`d` = 归一化距离（1 = 点半径）。内部实（=1）、边缘一条**窄带**收到 0
        /// （带宽 = 半径的 12%）⇒ "锐度比较高的"（用户 2026-10-12 口径）：是**一粒粒灯**，不是一团糊。
        /// </summary>
        public static float SpeckAlpha(float d, float radius = 1f)
        {
            if (d >= radius) return 0f;
            float band = radius * 0.12f + 1e-4f;
            return Mathf.Clamp01((radius - d) / band);
        }
    }
}
