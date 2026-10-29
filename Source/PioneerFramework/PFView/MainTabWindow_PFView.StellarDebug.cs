using System.Globalization;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **P0 恒星性能探针**（2026-10-28；用户已批准先测量、默认行为不变）。
    ///
    /// 目的：把"拉近恒星为什么掉帧"落成可读数字，而不是再靠指令数/感觉猜：
    /// · 两个 pass 可以分别关掉（surface / halo）⇒ 归因到具体 pass；
    /// · 渲染纹理可降到 0.75/0.5 ⇒ 判"是否 GPU 填充率主导"；
    /// · 步数预算可 64/32/16/8 ⇒ 判"步数是否真是成本主项"；
    /// · 距离一键跳到 2.7R/2.35R/2.1R/1.65R/1.2R/1e7/5e7 ⇒ 同一机位可重复比较。
    ///
    /// 开关状态放在 `PFViewStellarProbe`（无 Unity 依赖，headless 可测）；本文件只负责
    /// 帧时采样、读数和"把相机挪到指定距离"。
    ///
    /// 纪律：
    /// · **所有开关默认值 = 现状**（surface/halo 开、步数 0=64、RT 缩放 1.0）；
    /// · 探针只在 PFView 调试窗打开时记录（`PFViewStellarProbe.Active`），关窗零开销；
    /// · 不控制相机取景策略：距离跳转只是把现有 `FocusBody` + `camDistance` 设到目标值，
    ///   不改相机/轨道/夹取规则（P0 仍走普通天体相机，测试钉死）。
    /// </summary>
    public partial class MainTabWindow_PFView
    {
        /// <summary>最近 240 帧的帧时统计（只在探针激活时写入）。</summary>
        private static readonly PFViewFrameStatsCore stellarFrameStats = new(240);

        private static float nextStellarProbeLogTime;

        /// <summary>调试窗打开时调用：清空上一段样本，避免不同距离档互相污染。</summary>
        internal void DebugStellarResetStats()
        {
            stellarFrameStats.Reset();
            nextStellarProbeLogTime = Time.realtimeSinceStartup + 2f;
        }

        /// <summary>把所有 P0 开关恢复成现状默认（测量结束后必须按一下，防忘记）。</summary>
        internal static void DebugStellarResetDefaults() => PFViewStellarProbe.ResetDefaults();

        /// <summary>RenderScene 每帧调用；探针未激活时只是读一个 static bool。</summary>
        private void RecordStellarPerfFrame()
        {
            if (!PFViewStellarProbe.Active) return;
            stellarFrameStats.Add(Time.unscaledDeltaTime * 1000f);
            // 按**真实时间**限频（不是帧数）：掉帧时 300 帧可能要 30 s，日志会来不及记录每个样本。
            if (Time.realtimeSinceStartup < nextStellarProbeLogTime) return;
            nextStellarProbeLogTime = Time.realtimeSinceStartup + 2f;
            Log.Message("[PFView·恒星性能探针] " + DebugStellarStatsText());
        }

        /// <summary>当前读数（调试窗每帧显示 + 限频日志共用同一口径）。</summary>
        internal string DebugStellarStatsText()
        {
            string rt = renderTexture != null ? $"{renderTexture.width}×{renderTexture.height}" : "-";
            int steps = PFViewStellarProbe.ResolveStepBudget();
            return $"帧时 中位 {stellarFrameStats.Median():F1} / p99 {stellarFrameStats.P99():F1} / 均值 {stellarFrameStats.Average():F1} ms（{stellarFrameStats.Count} 帧）"
                + $"｜距离 {DebugStellarDistanceText()}｜恒星屏占≈{DebugStellarCoverageText()}"
                + $"｜RT {rt}×{PFViewStellarProbe.RenderScale:F2}｜surface={(PFViewStellarProbe.SurfacePass ? "开" : "关")}"
                + $"｜halo={(PFViewStellarProbe.HaloPass ? "开" : "关")}｜steps={steps}";
        }

        /// <summary>焦点恒星的相机距离（R 为单位）；不是恒星焦点时给 "-"。</summary>
        private string DebugStellarDistanceText()
        {
            var star = Current.Game?.GetComponent<CelestialBodyManager>()?.GetStar();
            float radius = FocusBodyRadiusView();
            if (star == null || focusKind != PFViewFocusKind.Body || focusBodyId != star.bodyId || radius <= 0f)
                return "-";
            return (camDisplayDistance / radius).ToString("F2", CultureInfo.InvariantCulture) + "R";
        }

        /// <summary>
        /// 恒星盘面在屏幕上的**粗略**覆盖比例（只作测量参照，不是渲染判据）：
        /// 角半径 θ=asin(R/d)，屏幕半轴 a=tanθ/tanH、b=tanθ/tanV；椭圆面积 πab ÷ 视口面积 4（归一化坐标）。
        /// </summary>
        private string DebugStellarCoverageText()
        {
            var star = Current.Game?.GetComponent<CelestialBodyManager>()?.GetStar();
            float radius = FocusBodyRadiusView();
            if (star == null || focusKind != PFViewFocusKind.Body || focusBodyId != star.bodyId || radius <= 0f)
                return "-";
            float d = Mathf.Max(camDisplayDistance, radius);
            float theta = Mathf.Asin(Mathf.Clamp(radius / d, 0f, 1f));
            float halfV = renderCamera != null ? renderCamera.fieldOfView * 0.5f * Mathf.Deg2Rad : 22.5f * Mathf.Deg2Rad;
            float aspect = renderCamera != null ? renderCamera.aspect : 16f / 9f;
            float tanV = Mathf.Max(Mathf.Tan(halfV), 0.01f);
            float tanH = Mathf.Max(tanV * aspect, 0.01f);
            float a = Mathf.Min(1f, Mathf.Tan(theta) / tanH);
            float b = Mathf.Min(1f, Mathf.Tan(theta) / tanV);
            float fraction = Mathf.Clamp01(Mathf.PI * a * b / 4f);
            return (fraction * 100f).ToString("F0", CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>跳到"距恒星中心 = factor × 恒星半径"的机位（只用于 P0 测量）。</summary>
        internal void DebugStellarJumpToBodyRadii(float factor)
        {
            DebugStellarJumpToDistance(Mathf.Max(FocusBodyRadiusView() * factor, 1f));
        }

        /// <summary>跳到指定视图距离（view units；只用于 P0 测量，1e7/5e7 远景档用）。</summary>
        internal void DebugStellarJumpToViewDistance(float distanceView)
        {
            DebugStellarJumpToDistance(distanceView);
        }

        private void DebugStellarJumpToDistance(float distanceView)
        {
            var star = Current.Game?.GetComponent<CelestialBodyManager>()?.GetStar();
            if (star == null) return;
            FocusBody(star.bodyId);                 // 普通天体相机（不引入恒星专用取景）
            cameraTransitioning = false;            // 测量要立即到位，不做 0.2–0.45 s 过渡
            camFocusOffset = Vector3.zero;
            camDistance = Mathf.Clamp(distanceView, FocusBodyRadiusView() * 1.2f, PFViewCameraCore.MaxBodyDistanceView);
            camDisplayDistance = camDistance;
            orbitDrag.Reset();
            DebugStellarResetStats();
        }
    }
}
