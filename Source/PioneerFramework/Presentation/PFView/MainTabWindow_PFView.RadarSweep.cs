using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 雷达盘面的**扫描动画**（2026-10-18，按用户逐条裁定实现）。
    ///
    /// 需求（用户原话）：给 PFView 的雷达图加入"雷达线周期性扫描"这一最常见的雷达特效。
    /// 裁定落地：
    ///   · **只做 2D 雷达盘面**（独立「雷达」仪器窗与主窗共用同一份 `DrawRadarPanel` ⇒ 两处自动同步）；
    ///   · **完全联动真实雷达**：扫掠模式 ⇒ 前导线 = `RadarBoresight` 在盘面基底上的投影，
    ///     转速即 `RadarCap.scanRateDegPerSec`；跟踪 / 手选方位 ⇒ 线停在真实指向上；
    ///   · **视觉角速度有上限**（2026-10-19 用户要求）：显示层最多 `SweepMaxDegPerRealSecond` = 1 圈/**真实**秒；
    ///     Normal 档本来就 ≈1 圈/秒（原速），Fast/更高倍速不再变成 8 圈/秒的糊线。物理波束不受影响；
    ///   · **亮线 + 60° 渐隐尾迹扇区**；接触点被扫过时闪一下并留短余辉，
    ///     余辉长度按**扫描线走过的角度**（40°）衰减，不按真实秒 —— 这样 Normal（≈1 圈/秒）与
    ///     Combat（≈60 秒/圈）下都是"扫过 ⇒ 亮一下 ⇒ 落回"，不会一个亮半圈、一个看不见；
    ///   · **随游戏暂停一起停**：角度取 `TimeScaleManager.GetRenderTick()`（暂停 ⇒ 冻结），
    ///     呼吸/待机扫描取"只在未暂停时前进"的视觉钟；
    ///   · 没有主动雷达 / 未开机 ⇒ **慢速压暗的待机扫描**（约 8 秒一圈、固定顺时针；
    ///     2026-10-18 用户二次裁定：任何编队都要看得见特效），真雷达一开机立刻切换为真实速率/方位。
    ///
    /// ⚠ 仪表基准（2026-10-18 用户裁定③）：盘面显示的是**当前选中的编队**（由调用方传入），
    ///   与"操作编队"解耦 —— 操作编队只决定命令来源；没选中编队才画空态。
    ///
    /// ⚠ 时间轴（2026-10-18 核对，别再算错）：`scanRateDegPerSec` 的单位是**模拟秒**；1 tick = 1 模拟秒，
    ///   档位决定"每真实秒多少模拟秒"（Combat 1/60 · Normal 1 · Fast 8）。同一门 6°/s 的雷达：
    ///   Combat ≈ 60 真实秒/圈、Normal ≈ 1 真实秒/圈。这是"真联动"的**预期行为**，不是卡顿。
    ///
    /// ⚠ 数据准入不变：本文件只读"选中编队自己的雷达状态"与认知层接触位置（盘面本来就这么画），
    ///   不读敌方真值、不写任何模拟状态 —— 纯表现层，可随时整段删掉而不影响模拟。
    ///
    /// ⚠ 2D/3D 一致性：3D 波束锥（`SpaceGroupVisualManager.DrawBeamCone`）与本文件吃**同一个**
    ///   `RadarBoresight` + `GetRenderTick` ⇒ 低倍速下盘面线与场景里的锥同向同速。
    ///   **高速档例外**：盘面被 `SweepMaxDegPerRealSecond` 限速后可能落后于 3D 锥（用户裁定要上限，
    ///   接受这份"显示滞后"；要改上限只动那一个常量，180 / 90 分别是 2 秒 / 4 秒一圈）。
    ///
    /// ⚠ 暂停（2026-10-19 修）：`GetRenderTick()` 在暂停时会丢掉子 tick 相位 ⇒ 同一帧算出的角度会**回跳**
    ///   最多一个 tick。所以显示相位用 `NextDisplayTick`（只允许前进、暂停时 dt=0 冻结），
    ///   旋向更新也要连续 3 帧反向才翻转 —— 否则单帧回跳会把尾迹翻到前导线前方。
    /// </summary>
    public partial class MainTabWindow_PFView
    {
        // ── 视觉常量（要调观感只动这一节）──
        private const float SweepTrailDeg = 60f;          // 尾迹扇区张角
        private const float SweepTrailPeakAlpha = 0.62f;  // 尾迹最亮处（越靠前导线越亮，尾端到 0）
        private const float SweepLineWidth = 1.25f;
        private const float SweepGlowWidth = 3.5f;
        private const float SweepGlowAlpha = 0.16f;
        private const float SweepStandbyWidth = 1f;
        private const float SweepStandbySeconds = 8f;      // 待机扫描一圈（真实秒；暂停冻结）
        private const float SweepStandbyTrailAlpha = 0.26f; // 待机尾迹比真实扫掠暗一档

        // ── 显示限速（2026-10-19 用户要求：高倍速时间流逝下雷达线不能失控）──
        private const float SweepMaxDegPerRealSecond = 360f; // 视觉角速度上限（真实秒）= 1 圈/秒：Normal 档原速，Fast/更高倍速不再更快
        private const float SweepDisplayResyncSeconds = 0.5f; // 超过这么久没画（面板关过）⇒ 直接对齐真实相位，不做慢速追赶
        private const int SweepDirFlipFrames = 3;             // 视在旋向要连续这么多帧反向才翻转（防暂停/恢复的单帧跳变）

        private const float SweepBreathSeconds = 2.4f;    // 跟踪/手选模式下"呼吸"周期（真实秒）
        private const float SweepBreathMin = 0.60f;
        private const float SweepBreathMax = 0.95f;
        private const float BlipFlashSweepDeg = 40f;      // 余辉按"扫描线走过的角度"衰减（≈一圈的 1/9；与流速档无关）
        private const float BlipFlashMinStepDeg = 0.02f;  // 单帧跨角小于它 = 线没动（暂停/投影卡住/同帧二次绘制），不产生"扫过"事件
        private const float BlipFlashPeak = 0.85f;
        private const float SweepFlashToleranceDeg = 0.75f;
        private const float SweepProjectionEpsilon = 0.05f; // 投影模长下限（≈3°）：更小视为"波束几乎垂直于盘面"
        private const float SweepMaxFlashArcDeg = 45f;     // 单帧跨角超过它 = 丢帧/切组，本帧不做"扫过"判定
        private const int SweepTexSize = 128;

        // 尾迹扇区贴图（程序化生成一次；与 `pfvDiscTex` 同一条"不用外部贴图资源"纪律）。
        // 两张 = 尾迹在 +角侧 / -角侧：视在旋向决定用哪张，避免每帧做镜像矩阵。
        private static Texture2D? pfvSweepTexBehindPos;
        private static Texture2D? pfvSweepTexBehindNeg;

        // ── 盘面扫描状态（主窗与独立仪器窗共用同一 owner 实例 ⇒ 每帧可能画两次，推进点用帧号防重）──
        private int radarSweepGroupId = -1;
        private float radarSweepAngleDeg = -90f;   // 前导线角（屏幕 atan2：+x=0、+y 向下为正 ⇒ 正角 = 顺时针）
        private float radarSweepFromDeg = -90f;    // 上一帧角（判定"本帧扫过哪些接触点"的区间起点）
        private float radarSweepDir;               // 视在旋向 +1/-1；0 = 尚未测出
        private bool radarSweeping;                // 本帧在扫掠（= 开机 + 扫掠模式）
        private float radarSweepAlpha = 1f;        // 前导线亮度系数（跟踪/手选模式的呼吸）
        private float radarSweepTravelDeg;         // 扫描线累计走过的绝对角度（余辉的"时间轴"，只增不减）

        // 接触点余辉：key = 真接触的 targetGroupId / 假接触的 -1-decoyId → 被扫到时扫描线已走过的角度
        private readonly Dictionary<int, float> radarBlipSweptAt = new();
        private readonly List<int> radarBlipPruneBuf = new();

        // 视觉钟（真实秒，暂停时不前进）：余辉与呼吸都挂它 ⇒ 用户裁定的"随游戏暂停一起停"
        private float radarVisualClock;
        private int radarVisualClockFrame = int.MinValue;
        private float radarVisualClockDelta;      // 本帧前进的真实秒（0 = 暂停 / 同帧二次绘制）
        private double radarDisplayTick;          // 视觉用的波束时刻（被速率上限夹过；只增不减）
        private bool radarDisplayTickPrimed;
        private float radarLastClock;             // 上一帧的视觉钟（判"面板是不是刚被关过"）
        private int radarSweepDirFlipFrames;      // 连续反向帧数（旋向防抖）

        /// <summary>
        /// 画一次扫描线（含尾迹）。**必须在接触点循环之前调用**：接触点余辉靠本方法写下的
        /// <see cref="radarSweepFromDeg"/> / <see cref="radarSweepAngleDeg"/> 判定"这一帧扫过了谁"。
        /// </summary>
        /// <param name="radial">盘面右向基底（径外向，系统系单位向量）。</param>
        /// <param name="pro">盘面上向基底（顺行，系统系单位向量）。</param>
        private void DrawRadarSweep(Vector2 c, float radius, int groupId, in SpacecraftSnapshot snap, DVector3 radial, DVector3 pro)
        {
            AdvanceRadarVisualClock();
            PruneBlipFlashes();

            // 换编队：盘面中心变了 ⇒ 角度/旋向/余辉全部重置（-90° = 顺行/正上）
            if (groupId != radarSweepGroupId)
            {
                radarSweepGroupId = groupId;
                radarSweepAngleDeg = -90f;
                radarSweepFromDeg = -90f;
                radarSweepDir = 0f;
                radarSweeping = false;
                radarSweepTravelDeg = 0f;
                radarSweepDirFlipFrames = 0;
                radarDisplayTickPrimed = false;
                radarBlipSweptAt.Clear();
            }

            bool hasBeam = snap.IsActive && snap.Radar.BeamRangeMeters > 0f;   // 装了主动雷达（几何有效）
            bool radiating = hasBeam && snap.ActivePingEnabled;                // 且开机（在辐射）
            bool scanning = radiating && snap.RadarScanning;

            if (!radiating)
            {
                // **待机扫描**（2026-10-18 用户二次裁定）：没有雷达 / 未开机时也画一条**慢速压暗**的扫描，
                // 只作视觉锚点（"没有真雷达也要看得见特效"）。用真实秒驱动 ⇒ 暂停即冻结；固定顺时针、
                // 从"顺行/正上"起，约 8 秒一圈。它**不代表**真实雷达状态（真雷达一律优先）。
                radarSweeping = false;
                radarDisplayTickPrimed = false;   // 下次真雷达开机时重新对齐真实相位
                radarSweepFromDeg = radarSweepAngleDeg;
                radarSweepAngleDeg = Mathf.Repeat(-90f + radarVisualClock * (360f / SweepStandbySeconds), 360f);
                DrawSweepTrail(c, radius, radarSweepAngleDeg, 1f, SweepStandbyTrailAlpha);
                DrawStandbyLine(c, radius, radarSweepAngleDeg);
                return;
            }

            if (scanning)
            {
                radarSweeping = true;
                radarSweepFromDeg = radarSweepAngleDeg;
                // 视觉相位：跟随真实波束，但**限速**（Fast 档 8 圈/秒 ⇒ 最多 1 圈/秒；Normal 档正好原速）
                radarSweepAngleDeg = ProjectBoresight(groupId, NextDisplayTick(snap.Radar.ScanRateDegPerSec), radial, pro, radarSweepAngleDeg);
                float step = Mathf.DeltaAngle(radarSweepFromDeg, radarSweepAngleDeg);
                float absStep = Mathf.Abs(step);
                if (absStep <= SweepMaxFlashArcDeg)
                {
                    if (absStep > 1e-3f) UpdateSweepDirection(step);
                    radarSweepTravelDeg += absStep;   // 丢帧/切组不计（否则一帧清空余辉）
                }
                radarSweepAlpha = 1f;
                DrawSweepTrail(c, radius, radarSweepAngleDeg, radarSweepDir, SweepTrailPeakAlpha);
            }
            else
            {
                // 跟踪/手选：线指真实方位、不转，但要"活着" —— 轻微呼吸（真实时间，暂停即停）
                radarSweeping = false;
                radarDisplayTickPrimed = false;   // 回到扫掠模式时重新对齐真实相位
                radarSweepFromDeg = radarSweepAngleDeg;
                radarSweepAngleDeg = ProjectBoresight(groupId, RadarTickNow(), radial, pro, radarSweepAngleDeg);
                float phase = radarVisualClock * (Mathf.PI * 2f / SweepBreathSeconds);
                radarSweepAlpha = Mathf.Lerp(SweepBreathMin, SweepBreathMax, 0.5f + 0.5f * Mathf.Sin(phase));
            }

            DrawSweepLine(c, radius, radarSweepAngleDeg, radarSweepAlpha);
        }

        /// <summary>
        /// 视觉用的波束时刻：跟随真实时刻，但**每真实秒最多走 `SweepMaxDegPerRealSecond`**。
        /// Normal 档 6°/s × 60 = 360°/真实秒 ⇒ 正好等于上限、保持原速；Fast 档（×8）被夹到 1 圈/秒。
        /// · 暂停：`radarVisualClockDelta = 0` ⇒ 不前进（线冻结，也**不会**因渲染时钟回落而倒退）；
        /// · 面板关过 / 首次：直接对齐真实相位（不做慢速追赶）；
        /// · 落后超过一整圈：折掉整圈 —— 波束相位是周期函数，所以角度不变、只是不无限累积。
        /// </summary>
        private double NextDisplayTick(float scanRateDegPerSec)
        {
            double nowTick = RadarTickNow();
            double rateDeg = scanRateDegPerSec > 0f ? scanRateDegPerSec : 6.0;
            double periodTicks = 360.0 / rateDeg;

            if (!radarDisplayTickPrimed || radarVisualClock - radarLastClock > SweepDisplayResyncSeconds)
            {
                radarDisplayTick = nowTick;
                radarDisplayTickPrimed = true;
            }
            else if (radarVisualClockDelta > 0f && nowTick > radarDisplayTick)
            {
                double maxStep = (SweepMaxDegPerRealSecond / rateDeg) * radarVisualClockDelta;
                radarDisplayTick += System.Math.Min(nowTick - radarDisplayTick, maxStep);
            }
            radarLastClock = radarVisualClock;

            double lag = nowTick - radarDisplayTick;
            if (lag >= periodTicks)
                radarDisplayTick += System.Math.Floor(lag / periodTicks) * periodTicks;
            return radarDisplayTick;
        }

        /// <summary>
        /// 波束方向 → 盘面角（右 = 径外向、上 = 顺行；屏幕 atan2：+x=0、+y 向下为正）。
        /// 投影模长过小（波束几乎垂直于盘面）时保留上一有效角 —— 真投影在零点附近本来就是"线翻面"。
        /// </summary>
        private static float ProjectBoresight(int groupId, double tick, DVector3 radial, DVector3 pro, float fallbackDeg)
        {
            if (!PhysicsQueries.TryGetRadarBoresight(groupId, tick, out var b)) return fallbackDeg;
            float right = (float)DVector3.Dot(b, radial);
            float up = (float)DVector3.Dot(b, pro);
            float mag = Mathf.Sqrt(right * right + up * up);
            return mag > SweepProjectionEpsilon ? Mathf.Atan2(-up, right) * Mathf.Rad2Deg : fallbackDeg;
        }

        /// <summary>
        /// 视在旋向（决定尾迹画在前导线哪一侧）。**必须防单帧跳变**：暂停/恢复时渲染时钟会回跳/跳前，
        /// 单帧大跨角会把旋向翻反 ⇒ 暂停时尾迹跑到线前方（2026-10-19 报障）。
        /// 规则：连续 `SweepDirFlipFrames` 帧反向才翻转（真反向由投影几何产生，会持续多帧）。
        /// </summary>
        private void UpdateSweepDirection(float stepDeg)
        {
            if (radarSweepDir == 0f)
            {
                radarSweepDir = Mathf.Sign(stepDeg);
                radarSweepDirFlipFrames = 0;
                return;
            }
            if (stepDeg * radarSweepDir >= 0f)
            {
                radarSweepDirFlipFrames = 0;
                return;
            }
            if (++radarSweepDirFlipFrames >= SweepDirFlipFrames)
            {
                radarSweepDir = -radarSweepDir;
                radarSweepDirFlipFrames = 0;
            }
        }

        /// <summary>真实扫描的前导线（+ 外发光 + 线头方块）。</summary>
        private static void DrawSweepLine(Vector2 c, float radius, float angleDeg, float alpha)
        {
            float rad = angleDeg * Mathf.Deg2Rad;
            Vector2 tip = c + new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * radius;

            float glow = SweepGlowAlpha * alpha;
            if (glow > 0.01f)
                Widgets.DrawLine(c, tip, PFViewPalette.Gray(0xFF, glow), SweepGlowWidth);

            Color line = alpha >= 0.999f ? PFViewPalette.SweepLine : PFViewPalette.Gray(0xFF, alpha);
            Widgets.DrawLine(c, tip, line, SweepLineWidth);
            Widgets.DrawBoxSolid(new Rect(tip.x - 1.5f, tip.y - 1.5f, 3f, 3f), line);
        }

        /// <summary>待机扫描的前导线：压暗、不发光（明度介于网格与接触点之间，提示"这不是真雷达"）。</summary>
        private static void DrawStandbyLine(Vector2 c, float radius, float angleDeg)
        {
            float rad = angleDeg * Mathf.Deg2Rad;
            Vector2 tip = c + new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * radius;
            Widgets.DrawLine(c, tip, PFViewPalette.SweepStandby, SweepStandbyWidth);
            Widgets.DrawBoxSolid(new Rect(tip.x - 1f, tip.y - 1f, 2f, 2f), PFViewPalette.SweepStandby);
        }

        /// <summary>尾迹扇区（旋转贴图；真实扫掠与待机扫描共用，亮度由 alpha 区分）。</summary>
        private static void DrawSweepTrail(Vector2 c, float radius, float angleDeg, float dir, float alpha)
        {
            if (radius < 6f || alpha <= 0.01f) return;
            // dir < 0（视在逆时针）⇒ 尾迹在 +角侧；否则（含首帧未测出）按顺时针处理，尾迹在 -角侧。
            Texture2D? tex = EnsureSweepTexture(dir < 0f);
            if (tex == null) return;

            Matrix4x4 savedMatrix = GUI.matrix;
            Color savedColor = GUI.color;
            try
            {
                // 必须用 Verse.UI.RotateAroundPivot，不能直接调 GUIUtility.RotateAroundPivot：
                // RimWorld 把 UI 缩放放在 GUI.matrix 里（UI.ApplyUIScale），而 Unity 原版的 pivot 是
                // 屏幕坐标；RimWorld 的封装做了 center * Prefs.UIScale（见 Verse.UI 反编译）。
                // 直接用原版会在 UI 缩放 != 1 时出现『拖尾绕错误中心公转 + 自转』（2026-10-18 实测报障）。
                UI.RotateAroundPivot(angleDeg, c);
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.DrawTexture(new Rect(c.x - radius, c.y - radius, radius * 2f, radius * 2f), tex);
            }
            finally
            {
                GUI.color = savedColor;
                GUI.matrix = savedMatrix;
            }
        }

        /// <summary>
        /// 登记"本帧扫描线扫过了这个接触点"（只扫掠模式；丢帧/切组的大跨角不判定，避免一片乱闪）。
        /// </summary>
        private void RegisterBlipSweep(int key, float blipAngleDeg)
        {
            if (!radarSweeping) return;
            float step = Mathf.DeltaAngle(radarSweepFromDeg, radarSweepAngleDeg);
            // 线没动 ⇒ 没有新的"扫过"事件（暂停/投影退化/同一帧主窗+独立窗各画一次都会命中这里）。
            // 2026-10-18 修：原来没有这条守卫，停在线上/被二次绘制的接触点会被**每帧重新点亮** = 一直闪。
            if (Mathf.Abs(step) < BlipFlashMinStepDeg) return;
            if (Mathf.Abs(step) > SweepMaxFlashArcDeg) return;

            float d = Mathf.DeltaAngle(radarSweepFromDeg, blipAngleDeg);
            bool crossed = step >= 0f
                ? d >= -SweepFlashToleranceDeg && d <= step + SweepFlashToleranceDeg
                : d <= SweepFlashToleranceDeg && d >= step - SweepFlashToleranceDeg;
            if (crossed) radarBlipSweptAt[key] = radarSweepTravelDeg;
        }

        /// <summary>
        /// 接触点余辉系数（0 = 没余辉，1 = 刚被扫到）；二次衰减 = 前段亮、后段迅速压暗。
        /// 衰减轴是**扫描线走过的角度**（不是真实秒）：Normal 档一圈≈1 秒、Combat 档≈60 秒，
        /// 固定时长会让前者亮半圈、后者看不见；按角度算则任何流速下都是"扫过 ⇒ 亮一下 ⇒ 很快落回"。
        /// </summary>
        private float BlipFlashOf(int key)
        {
            if (!radarSweeping) return 0f;   // 待机/跟踪/手选：没有"扫过"事件（也不该留残辉）
            if (!radarBlipSweptAt.TryGetValue(key, out float sweptTravel)) return 0f;
            float age = radarSweepTravelDeg - sweptTravel;
            if (age < 0f || age >= BlipFlashSweepDeg) return 0f;
            float f = 1f - age / BlipFlashSweepDeg;
            return f * f;
        }

        /// <summary>清掉早就熄灭的余辉（接触点通常个位数，超过阈值才扫一遍）。</summary>
        private void PruneBlipFlashes()
        {
            if (radarBlipSweptAt.Count < 32) return;
            radarBlipPruneBuf.Clear();
            foreach (KeyValuePair<int, float> kv in radarBlipSweptAt)
                if (radarSweepTravelDeg - kv.Value >= BlipFlashSweepDeg) radarBlipPruneBuf.Add(kv.Key);
            for (int i = 0; i < radarBlipPruneBuf.Count; i++) radarBlipSweptAt.Remove(radarBlipPruneBuf[i]);
        }

        /// <summary>
        /// 视觉钟：**只在游戏未暂停时前进**的真实秒（帧号防重：同一帧主窗 + 独立仪器窗各画一次也只走一次）。
        /// 余辉与呼吸挂它 ⇒ 暂停时跟着冻住，与用户裁定"随游戏暂停一起停"一致。
        /// </summary>
        private void AdvanceRadarVisualClock()
        {
            radarVisualClockDelta = 0f;
            if (radarVisualClockFrame == RealTime.frameCount) return;
            radarVisualClockFrame = RealTime.frameCount;
            if (Find.TickManager != null && Find.TickManager.CurTimeSpeed == TimeSpeed.Paused) return;
            float dt = RealTime.deltaTime;
            if (dt > 0f && dt < 0.5f)
            {
                radarVisualClock += dt;
                radarVisualClockDelta = dt;
            }
        }

        /// <summary>
        /// 本帧的物理时刻（**帧级连续时钟**）：与 3D 波束锥、图标/轨道同一条
        /// `TimeScaleManager.GetRenderTick()`（暂停 ⇒ 冻结；子 tick 相位 ⇒ 扫描线不按 tick 一格一格跳）。
        /// </summary>
        private static double RadarTickNow()
        {
            var tsm = Current.Game?.GetComponent<TimeScaleManager>();
            return tsm != null ? tsm.GetRenderTick() : TimeScaleManager.GetPhysicsTickSafe();
        }

        /// <summary>
        /// 烘一张"扇区"贴图（只做一次）。角度按**屏幕口径**烘：+x = 0、+y（屏幕向下）为正；
        /// `SetPixels` 的行 0 是贴图底行、`GUI.DrawTexture` 显示时在屏幕下方 ⇒ sy 要按行号反算。
        /// </summary>
        private static Texture2D? EnsureSweepTexture(bool trailOnPositiveSide)
        {
            if (trailOnPositiveSide ? pfvSweepTexBehindPos != null : pfvSweepTexBehindNeg != null)
                return trailOnPositiveSide ? pfvSweepTexBehindPos : pfvSweepTexBehindNeg;

            int size = SweepTexSize;
            float r = size * 0.5f;
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                float sy = r - (y + 0.5f);   // 屏幕 y 偏移（向下为正）：行 0 在屏幕下方 ⇒ 反算
                for (int x = 0; x < size; x++)
                {
                    float sx = x + 0.5f - r;
                    float dist = Mathf.Sqrt(sx * sx + sy * sy);
                    float a = 0f;
                    if (dist <= r - 1f)
                    {
                        float ang = Mathf.Atan2(sy, sx) * Mathf.Rad2Deg;
                        float behind = trailOnPositiveSide ? ang : -ang;   // 尾迹落在前导线"后方"
                        if (behind >= 0f && behind <= SweepTrailDeg)
                        {
                            float t = behind / SweepTrailDeg;
                            float rim = Mathf.Clamp01((r - 1f - dist) / 1.5f);   // 外沿 1.5px 羽化
                            a = Mathf.Pow(1f - t, 1.4f) * rim;
                        }
                    }
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            tex.SetPixels(px);
            tex.Apply();
            if (trailOnPositiveSide) pfvSweepTexBehindPos = tex;
            else pfvSweepTexBehindNeg = tex;
            return tex;
        }
    }
}
