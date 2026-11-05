using System;
using System.Collections.Generic;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// **设计规则**（2026-10-29）：设计规格里与"目录查表"无关的纯判据。
    ///
    /// 两件事在这里，且**只在**这里：
    /// · **件数口径**：一行设计的实际件数 = 轴向站数(`quantity`) × 并联台数(`parallelCount`)。
    ///   所有"数件"的地方（推进聚合、推力/TWR、工时、活动级检查、窗口读数）都走 <see cref="PartCountOf"/>，
    ///   免得又出现"加 6 台并联只算 6 次工时/只算 1 台推力"那种口径分叉；
    /// · **引擎必须在它那一级的末端**（2026-10-29 用户令）：堆叠顺序 = 自上而下（下标 0 = 机头），
    ///   一级的引擎必须落在该级件的**末尾**（喷口朝下）。否则拒绝设计通过。
    ///
    /// 为什么这条不是"排版洁癖"：<see cref="DesignPartAssembler"/> 按 spec 顺序沿 −Z 堆，
    /// 一堆零件中间夹着引擎 ⇒ 引擎前面还有结构，喷口推的不是本级而是自己的罐/控制核心，
    /// 抛级时也会把"引擎上面的东西"一起带走 ⇒ 不可能飞。所以建之前就拦。
    ///
    /// 口径边界：只认 `category == "thruster"`（主引擎）。`rcs` 是姿态推力器，不要求在尾部。
    /// </summary>
    public static class RocketDesignRules
    {
        /// <summary>
        /// **级间空隙**（m，2026-10-30 用户方案）：装配时在**每一处级边界**留出这段空当，
        /// 抛级时两组从一开始就不接触。
        ///
        /// 为什么这比"分离后给碰撞豁免"好：窄相要 `depth > 0` 才算接触、宽相是纯 AABB 无 margin
        /// ⇒ 只要空隙 > 0，分离瞬间**一对接触都不会产生** —— 既没有那 4–5 ms 的贴面求解尖峰，
        /// 也不会有"船本来在慢滚 ⇒ 界面横向相对速度 ⇒ 接触冲量把二级踢转"的磕碰。
        /// 代价只有视觉上一条缝（真实多级火箭本来就有级间段，喷管正好悬在这里）与总长 +0.3 m/边界。
        /// 取值：> 0 就够（0.15–0.5 都能用）；0.3 是"看得像接缝、又不显断头"的折中，改这一行即可调。
        /// </summary>
        public const float StageGapMeters = 0.3f;

        /// <summary>这一行的并联台数（夹到 1…7；老档/坏档的 0/负数按 1 读）。</summary>
        public static int ParallelOf(DesignPartSpec? entry)
            => entry == null ? 1 : ParallelLayout.Clamp(entry.parallelCount);

        /// <summary>这一行的**轴向站数**（沿堆叠方向摆几次；至少 1）。</summary>
        public static int StationCountOf(DesignPartSpec? entry)
            => entry == null ? 0 : Math.Max(1, entry.quantity);

        /// <summary>这一行的**实际件数** = 站数 × 并联台数。</summary>
        public static int PartCountOf(DesignPartSpec? entry)
        {
            if (entry == null) return 0;
            return StationCountOf(entry) * ParallelOf(entry);
        }

        /// <summary>主引擎件（RCS 不算：它是姿态推力器，不要求在尾部）。</summary>
        public static bool IsEngine(DesignPartSpec? entry)
            => entry != null && entry.category == "thruster";

        /// <summary>整份设计的实际件数（Σ 站数 × 并联）。</summary>
        public static int TotalPartCount(RocketDesignSpec? spec)
        {
            if (spec?.parts == null) return 0;
            int n = 0;
            for (int i = 0; i < spec.parts.Count; i++) n += PartCountOf(spec.parts[i]);
            return n;
        }

        /// <summary>
        /// **引擎必须在它那一级的末端**（"否则拒绝设计通过"）。
        /// 判据：按 spec 顺序（自上而下）扫每一级；本级出现主引擎之后，本级不许再出现非引擎件。
        /// 返回空串 = 通过；否则返回可直接展示的拒绝原因（带级号/行号/件名，并给可执行的修法）。
        /// </summary>
        /// <param name="offenders">非 null 时填入违规行的下标（窗口用来标红）。</param>
        public static string EnginePlacementError(RocketDesignSpec? spec, List<int>? offenders = null)
        {
            offenders?.Clear();
            if (spec?.parts == null) return string.Empty;

            List<int> stages = StagesOf(spec);
            int firstStage = -1;
            int firstRow = -1;
            string firstDef = "";

            for (int s = 0; s < stages.Count; s++)
            {
                int stage = stages[s];
                bool seenEngine = false;
                for (int i = 0; i < spec.parts.Count; i++)
                {
                    DesignPartSpec? e = spec.parts[i];
                    if (e == null || e.stageId != stage) continue;
                    if (e.useExplicitPose) continue;   // 显式位姿行（径向安装）不按堆叠顺序判（2026-11-03）
                    if (IsEngine(e)) { seenEngine = true; continue; }
                    if (!seenEngine) continue;

                    offenders?.Add(i);
                    if (firstRow < 0) { firstStage = stage; firstRow = i; firstDef = e.defName ?? ""; }
                }
            }

            if (firstRow < 0) return string.Empty;
            return $"引擎必须在它那一级的末端（喷口朝下）：级 {firstStage} 的第 {firstRow + 1} 行「{firstDef}」"
                + "排在本级引擎之后。把这一级的引擎挪到该级最后一行（设计清单用 ↑/↓ 调顺序）。";
        }

        /// <summary>设计里出现过的级号（升序；空设计返回空表）。</summary>
        public static List<int> StagesOf(RocketDesignSpec? spec)
        {
            var stages = new List<int>();
            if (spec?.parts == null) return stages;
            for (int i = 0; i < spec.parts.Count; i++)
            {
                DesignPartSpec? e = spec.parts[i];
                if (e == null || stages.Contains(e.stageId)) continue;
                stages.Add(e.stageId);
            }
            stages.Sort();
            return stages;
        }

        /// <summary>
        /// **活动级集合**（C-full，2026-11-03）：最大级 + 标了并联点火的下级（可传递）。
        /// 设计窗口的 TWR/发射门用它；运行时同口径在 `StagePartition.ActiveStages`（按 part）。
        /// </summary>
        public static HashSet<int> ActiveStages(RocketDesignSpec? spec)
        {
            var active = new HashSet<int>();
            if (spec?.parts == null) return active;

            int max = 0;
            for (int i = 0; i < spec.parts.Count; i++)
            {
                var e = spec.parts[i];
                if (e != null && e.stageId > max) max = e.stageId;
            }
            active.Add(max);
            for (int s = max - 1; s >= 0; s--)
            {
                if (!active.Contains(s + 1)) continue;
                if (spec.parallelIgnitionStageIds != null && spec.parallelIgnitionStageIds.Contains(s))
                    active.Add(s);
            }
            return active;
        }

        /// <summary>
        /// **同一级的零件必须在堆叠里连成一段**（2026-10-30，为抛级服务）。
        ///
        /// 为什么这是硬条件（不是排版偏好）：抛级按 `stageId` 切组（`StagePartition.Split`），
        /// 一旦某级的行被别的级隔开 ⇒ 切出来的两半在 z 向**互相穿插**（真交叠），
        /// 分离后只能靠碰撞求解器硬推；那种几何下"刚体分裂"的物理解算也没有意义。
        ///
        /// 判据：按堆叠顺序扫，某级**闭合后再次出现** ⇒ 该次出现的行记为违规。
        /// 返回空串 = 通过；否则返回可直接展示的拒绝原因（带行号/件名 + 修法）。
        /// </summary>
        public static string StageContiguityError(RocketDesignSpec? spec, List<int>? offenders = null)
        {
            offenders?.Clear();
            if (spec?.parts == null) return string.Empty;

            var finished = new HashSet<int>();
            int prevStage = int.MinValue;
            int firstStage = -1, firstRow = -1;
            string firstDef = "";

            for (int i = 0; i < spec.parts.Count; i++)
            {
                DesignPartSpec? e = spec.parts[i];
                if (e == null) continue;
                if (e.useExplicitPose) continue;   // 显式位姿行（径向安装）不受“同级连成段”约束（2026-11-03）

                if (e.stageId != prevStage)
                {
                    if (prevStage != int.MinValue) finished.Add(prevStage);
                    if (finished.Contains(e.stageId))
                    {
                        offenders?.Add(i);
                        if (firstRow < 0) { firstStage = e.stageId; firstRow = i; firstDef = e.defName ?? ""; }
                    }
                    prevStage = e.stageId;
                }
            }

            if (firstRow < 0) return string.Empty;
            return $"级 {firstStage} 的零件在堆叠里被别的级隔开了：第 {firstRow + 1} 行「{firstDef}」又回到该级。"
                + "抛级按级号整段切走，级行必须连成一段（否则两半几何会互相穿插）。"
                + "用 ↑/↓ 把同级的零件挪到一起。";
        }
    }
}
