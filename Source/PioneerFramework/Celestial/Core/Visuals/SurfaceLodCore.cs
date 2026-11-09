// ─────────────────────────────────────────────────────────────────────────────
// 纯核（无 Verse / Unity / RimWorld —— 本文件被 PioneerFramework.Pure.Tests 通配链接，定义 PURE_MATH）。
//
// 从已归档的 Source/已弃置/PFViewTiles-近档分格-20261010.cs 的 PFViewTerrainTiers（约 :534-641）
// 迁移「近档分格该不该铺 tile」的纯数学判定；归档文件只读、不修改。Mathf → MathF，公式逐条对照，
// 只额外加退化输入（0 / 负数 / NaN / ±∞）的安全哨兵 —— 理由是这些函数跑在渲染热路径上：
// 一个 NaN 会顺着「格像素数 / 估算格数」传进 LOD 分支，把整颗星球的分格静默关掉，或反过来让
// 估算格数变成 Int32.MinValue 后整帧刷错误日志（比「少铺一层瓦」难查得多）。
//
// 归档注释里记的那次事故（别删，这是本类存在的理由）：2026-10-09 第一版把可见帽半角写成 asin(R/d)，
// 那是「相机处看球盘的视半径」，不是「球心处看仍可见那片」的角半径；贴地时两者 6.2° vs 83°，
// 于是面朝判据整体错档。正确关系是 cos = R/d（切点条件 dot(格法线, 相机方向) ≥ R/d），
// 所以 FacingCapCosine 返回 clamp(R/d)，FacingCapFraction 再把它换成「可见帽占整球面积比」。
// 归档时那批用例随「近档分格」整件退役；本次只搬数学，不搬 Quaternion / 切平面基等 Unity 依赖部分
// （自转换系仍留在 PFView 侧，见 Source/PioneerFramework/PFView/ 的烘焙路径）。
//
// 命名口径：*View 后缀 = 视图系单位（米）。distanceToCenterView 是相机到**球心**的距离，
// distanceToSurfaceView 是相机到**地面**的距离（≈ d − R，调用方自己减）；两者别混用 ——
// 归档实现里 TileScreenPixels 收的正是「到地面」的距离。
// ─────────────────────────────────────────────────────────────────────────────
using System;

namespace PioneerFramework.Celestial;

/// <summary>
/// 天体地表「近档分格」的纯判定（可见帽 / 格屏幕像素 / 可见格数估算 / 是否该铺 tile）。
/// </summary>
public static class SurfaceLodCore
{
    private const float DegreesToRadians = MathF.PI / 180f;

    // 退化距离哨兵：小于等于 1 mm 视为「贴脸/在球内」，按归档实现的一样处理。
    private const float MinUsableDistance = 1e-3f;

    /// <summary>
    /// 面朝判据：dot(格法线, 相机方向) ≥ 本值 才算在地平线内。返回 clamp(R/d) ∈ [-1, 1]。
    /// 退化（d ≤ 1 mm / R ≤ 0 / NaN）返回 1f = 只剩正下方那一格（保守哨兵，不给 NaN）。
    /// d = +∞ 是合法极限（R/∞ = 0 ⇒ 半球可见），照公式返回 0f。
    /// </summary>
    public static float FacingCapCosine(float bodyRadiusView, float distanceToCenterView)
    {
        if (float.IsNaN(bodyRadiusView) || float.IsNaN(distanceToCenterView)) return 1f;
        if (distanceToCenterView <= MinUsableDistance || bodyRadiusView <= 0f) return 1f;
        float cosine = bodyRadiusView / distanceToCenterView;
        if (float.IsNaN(cosine)) return 1f;   // (+∞)/(+∞) 之流
        if (cosine > 1f) return 1f;
        if (cosine < -1f) return -1f;
        return cosine;
    }

    /// <summary>
    /// 面朝帽占整球面积比 = (1 − cos)/2（远看 → 0.5 = 半球 ✅，与归档 :550 同式）。
    /// 非有限输入返回 0f（「没有可见面积」），避免把 NaN/∞ 喂给下游比例。
    /// </summary>
    public static float FacingCapFraction(float cosCap)
    {
        if (float.IsNaN(cosCap) || float.IsInfinity(cosCap)) return 0f;
        return (1f - cosCap) * 0.5f;
    }

    /// <summary>
    /// 一格在屏幕上占多少像素（distanceToSurfaceView 传**到地面**的距离 ≈ d − R；与归档 :553 同式）。
    /// 退化输入返回 0f：d ≤ 1 mm / tile ≤ 0 / fov 不在开区间 (0,180) / 非有限值。
    /// fov 必须夹在 (0,180)：0 会让 tan 归零除零出 ∞，≥180 会把 tan 翻负 ⇒ 负像素数（归档未防，本轮补上）。
    /// </summary>
    public static float TileScreenPixels(float tileSizeView, float distanceToSurfaceView, float pixelHeight, float fovDegrees)
    {
        if (float.IsNaN(tileSizeView) || float.IsNaN(distanceToSurfaceView) || float.IsNaN(pixelHeight)) return 0f;
        if (distanceToSurfaceView <= MinUsableDistance || tileSizeView <= 0f) return 0f;
        if (!(fovDegrees > 0f) || fovDegrees >= 180f) return 0f;   // NaN / 0 / 负 / ≥180 一并挡掉

        float perPixel = 2f * MathF.Tan(fovDegrees * 0.5f * DegreesToRadians) / MathF.Max(pixelHeight, 1f);
        if (!(perPixel > 0f) || float.IsInfinity(perPixel)) return 0f;

        float pixels = tileSizeView / distanceToSurfaceView / perPixel;
        return float.IsFinite(pixels) ? pixels : 0f;
    }

    /// <summary>
    /// 屏幕上大约有多少格：**地面足迹面积 ÷ 每格面积**（每格面积 = 球面积 / 总格数；与归档 :564-574 同式）。
    /// 足迹 = 高度 × 两方向 FOV 张角（远近都够用；超过整球面积就夹到整球 = tilesCount）。
    /// 退化返回 0（不抛、不出 NaN）：格数 ≤ 0 / R ≤ 0 / d ≤ 0 / fov 不在 (0,180) / NaN。
    /// ⚠ 与归档的唯一有意偏差：归档把 d ≤ R（相机已在球内）也按「高度夹到 1 m」算出正数，
    /// 这里直接判 0 —— 相机进球体是非法态，宁可不铺 tile 也不按 1 m 高度估一整屏。
    /// </summary>
    public static int EstimatedVisibleTiles(int tilesCount, float bodyRadiusView, float distanceToCenterView, float fovYDeg, float aspect)
    {
        if (tilesCount <= 0 || bodyRadiusView <= 0f || float.IsNaN(bodyRadiusView)) return 0;
        if (!(distanceToCenterView > 0f) || float.IsNaN(distanceToCenterView) || float.IsNaN(aspect)) return 0;
        if (!(fovYDeg > 0f) || fovYDeg >= 180f) return 0;

        float altitude = MathF.Max(distanceToCenterView - bodyRadiusView, 1f);
        float halfTanY = MathF.Tan(fovYDeg * 0.5f * DegreesToRadians);
        if (!(halfTanY > 0f) || float.IsInfinity(halfTanY)) return 0;

        float halfTanX = halfTanY * MathF.Max(aspect, 0.01f);
        float footprint = (2f * altitude * halfTanX) * (2f * altitude * halfTanY);
        float sphereArea = 4f * MathF.PI * bodyRadiusView * bodyRadiusView;
        if (!(sphereArea > 0f) || float.IsInfinity(sphereArea)) return 0;

        float fraction = footprint / sphereArea;
        if (!(fraction > 0f)) return 0;   // 含 NaN 兜底（∞/∞、0/0）
        if (fraction > 1f) fraction = 1f; // 足迹盖过整球 ⇒ 整球

        int estimated = (int)Math.Ceiling(tilesCount * (double)fraction);
        if (estimated < 0) return 0;
        return estimated > tilesCount ? tilesCount : estimated;
    }

    /// <summary>
    /// 档位判据（与归档 :635-640 同式）：格子太小（看不出粒度）或可见格数超预算（那是远档）⇒ 不用分格。
    /// estimatedVisibleTiles ≤ 0 ⇒ false；pixel 为 NaN/∞ ⇒ false（∞ 也当「算错了」处理：
    /// 分格关闭比按 ∞ 铺满屏安全）；默认阈值 20000 格 / 2.5 px 与归档常量一致。
    /// </summary>
    public static bool ShouldUseTiles(int estimatedVisibleTiles, float tileScreenPixels, int maxVisibleTiles = 20000, float minTileScreenPixels = 2.5f)
    {
        if (estimatedVisibleTiles <= 0) return false;
        if (float.IsNaN(tileScreenPixels) || float.IsInfinity(tileScreenPixels)) return false;
        if (tileScreenPixels < minTileScreenPixels) return false;
        return estimatedVisibleTiles <= maxVisibleTiles;
    }
}
