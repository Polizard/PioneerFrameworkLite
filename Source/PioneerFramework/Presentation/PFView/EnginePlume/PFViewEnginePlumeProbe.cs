// ⛔【NOSTEPANYMORE】表现层文件（PFView 引擎喷焰开关）。不写任何后端字段。
//    若下一步要改 Base/** 等后端文件，按 BACKEND-LOCK.md 停机请示，不得顺手改。
// ─────────────────────────────────────────────────────────────────────────────
namespace PioneerFramework
{
    /// <summary>
    /// **PFView 引擎喷焰探针**（2026-11-08）：开关与上一帧读数。
    /// 与 PFViewStellarProbe 同型——静态状态单独放一个类，调试窗与绘制段都能读写，
    /// 不触发 MainTabWindow_PFView 的静态构造器（headless 测试只读开关时不该建 GPU 资源）。
    /// </summary>
    internal static class PFViewEnginePlumeProbe
    {
        internal const bool DefaultEnabled = true;

        /// <summary>总开关（调试窗可关；关 = 逐帧回到当前画面）。</summary>
        internal static bool Enabled = DefaultEnabled;

        /// <summary>只画喷口点、不画尾焰：P0 用来核对锚点/方向（不点火也能看）。</summary>
        internal static bool NozzleOnly;

        /// <summary>强制显示全部喷口（忽略点火判据）：锚点检查用，不是玩家功能。</summary>
        internal static bool ForceAllEngines;

        /// <summary>只看当前选中的编队：排查时减少画面干扰。</summary>
        internal static bool DrawOnlySelectedGroup;

        /// <summary>长度/亮度总倍率（0.3–2.0）。</summary>
        internal static float IntensityScale = 1f;

        /// <summary>片数上限（与 PFViewEnginePlumeBatch 的容量一致）。</summary>
        internal const int MaxQuads = 1024;

        // 上一帧读数（调试窗显示 + 强制/仅喷口点调试日志）。
        internal static int LastEngineCount;
        internal static int LastQuadCount;
        internal static int LastSkippedEngines;
        internal static int LastCollectedGroups;
        internal static int LastWholeReliefEntities;
        internal static int LastMapThrusters;

        // 调试日志：每进程只打一次"钩子已运行"；强制/仅喷口点模式每 180 帧一行读数。
        internal static bool LoggedFirstFrame;
        internal static bool LoggedFirstMarker;
        internal static int NextLogFrame;

        internal static void ResetDefaults()
        {
            Enabled = DefaultEnabled;
            NozzleOnly = false;
            ForceAllEngines = false;
            DrawOnlySelectedGroup = false;
            IntensityScale = 1f;
        }
    }
}