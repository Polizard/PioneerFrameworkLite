using UnityEngine;

namespace PioneerFramework
{
    /// <summary>
    /// **PFView 场景的画序（层次）口径** —— 2026-10-11「修法甲：分档」的第一刀。
    ///
    /// ## 为什么层次要用**画序**而不是深度
    /// PFView 是一台透视相机同时装"米级飞船零件"和"半径 50 万视图单位的行星"（7 个数量级）：
    /// 深度量化步长 `δz = d²/(near·2²⁴)`，而 `near = clamp(camDistance×0.35, 0.01, 5000)` 是**随相机距离自适应**的
    /// ⇒ 看船那一档 near 只有 10.5–105 ⇒ 远处的 δz 涨到 0.5–1420 单位。
    /// **两个同心球面（大气壳 1.01R 与行星面 R）在"球缘"处必然相切** ⇒ 那一圈上两者的深度差 → 0 ⇒
    /// 无论 δz 多小，都有一条"谁在前由量化噪声决定"的带 ⇒ **逐帧翻面 = 用户看到的"壳一闪一闪"**。
    /// ⇒ 确定性只能来自**画序**：**先用画序定死层次，再让"后画的不透明层"覆盖"先画的半透明层"**。
    ///
    /// ## 本场景的分层（值与用途）
    /// ```
    /// 星空 1000（RenderQueue.Background）
    ///   太阳盘 1200          ← 原 1900；提前是为了让壳能排在它之后（太阳该被大气蒙一层雾）
    ///   大气壳 1950          ← **排在行星之前**（这是修球缘闪的关键：壳先画、行星随后**覆盖**它的盘面部分，
    ///                          于是"壳 vs 行星"根本不进深度比较 ⇒ 没有可翻的边）
    /// 行星（基底球 + 烘焙补丁）／飞船零件／浮雕 2000（Standard 的默认 Geometry 队列，不透明、写深度）
    /// 云层 2850（Alpha 混合，保留深度测试；仅画面向相机的一侧）
    /// ```
    /// ⚠ **已知代价（写在明处）**：壳排在行星之前 ⇒ 行星（不透明、后画）会把壳在**盘面内**的那部分盖掉 ⇒
    /// **盘面那层 18% 蓝雾没有了**（天空里与球缘那圈照旧，球缘的"两倍浓"也在）。原因：Unity 的 `ZTest`/`Offset`
    /// 都是 **pass 状态**（运行时改不了），而本仓库**没有编译自定义 shader 的能力**（需 Unity 编辑器）⇒
    /// "两者都要"（盘面雾 + 不闪）要等以后有一支带深度偏置的大气 shader 才能做到。
    /// 若想退回"要盘面雾、忍球缘闪"：把 <see cref="AtmosphereShell"/> 改回 2900 即可（一个数）。
    ///
    /// ⚠ 另一条相关但**独立**的现象：壳与**飞船**之间不存在这个问题（飞船比壳近几个数量级，深度差巨大）；
    /// "别的航天器闪"已查明**不是深度**（零件在屏幕上小于 0.05 px 时才会受 δz 影响 ⇒ 看不见），另案处理。
    /// </summary>
    internal static class PFViewLayers
    {
        /// <summary>星空盒：`RenderQueue.Background`（排在一切之前，含大气壳与太阳）。</summary>
        public const int Starfield = 1000;

        /// <summary>太阳圆片：星空之上、大气壳之下（⇒ 会被壳蒙一层雾）。</summary>
        public const int SunDisc = 1200;

        /// <summary>
        /// 大气壳：**排在行星之前**（正是这一条消掉"球缘闪"：行星随后不透明覆盖它的盘面部分）。
        /// 改回 2900 = 退回"盘面有 18% 蓝雾、但球缘一圈闪"。
        /// </summary>
        public const int AtmosphereShell = 1950;

        /// <summary>行星（基底球 + 烘焙补丁）与飞船零件/浮雕所在的不透明队列（Standard 默认 2000；此处只作读数）。</summary>
        public const int PlanetAndShips = 2000;

        /// <summary>云层在不透明场景之后混合，仍由深度测试保留近处飞船的遮挡。</summary>
        public const int Clouds = 2850;

        /// <summary>
        /// 分层次序自检（**画序必须严格递增**）—— 用例钉的就是这条：层次一旦写乱，
        /// "谁挡谁"就重新变成深度竞争，球缘闪会回来。
        /// </summary>
        public static bool IsLayeredInOrder()
        {
            return Starfield < SunDisc && SunDisc < AtmosphereShell && AtmosphereShell < PlanetAndShips
                && PlanetAndShips < Clouds;
        }
    }
}
