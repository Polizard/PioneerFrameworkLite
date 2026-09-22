using System;
using System.Collections.Generic;
using System.Text;

namespace PioneerFramework
{
    /// <summary>
    /// 一条机动的**意图（目标条件）**——MJ 的"节点语义"在我们这边的载体（《MechJeb参考大修计划》§2.5 / B1.2）。
    ///
    /// ## 为什么要有它
    /// 现在队列里的 `PlannedManeuver` 携带的是"**规划那一刻算出的 Δv 向量**"（冻结指令）。
    /// MJ 携带的是"**从当前位置看，还差多少 Δv 才能到达计划的后继轨道**"（目标条件），**每 tick 从活轨道重解**。
    /// 计划稿 §2.5 把"上一轮提交 dv2 台架实测失败"的第一嫌疑就归到这条差别上：
    /// 冻结的向量在等它的那几百秒里已经过期了，照烧就会偏。
    ///
    /// ⇒ 本类型存的是**能重新算一遍的全部输入**：操作名 + 参数值（**显示单位**）+ 时间选择器状态 + 目标。
    /// 于是"活重解" = `Apply` 找回操作 + `MakeNodes` 重算一遍 ✓。
    ///
    /// ## 两个刻意的设计
    /// ① **参数走 `Operation.GetParams()` 的描述层**（与规划器界面**同一条通道**）⇒ 界面改的参数天然可回放，
    ///    不另开一条"参数序列化"的路（否则就是第二份权威）。
    /// ② **可序列化**：全部字段都是 `string`/`double[]`/`int` ⇒ 队列存档时由 `PlannedManeuver.ExposeData`
    ///    逐字段 `Scribe`（本类型**不认识 Verse**，保持纯层）。
    ///
    /// ## 与冻结向量的关系（**两者并存**，不是替换）
    /// 计划稿 B1.2 明确要"保留现有冻结向量语义给玩家手动节点"⇒ `PlannedManeuver` 里两者都在：
    /// 冻结向量照旧执行，意图是**可选的附加信息**；等执行层接上重解（P2 下一步）时再由它接管。
    /// </summary>
    public sealed class PlanIntent
    {
        /// <summary>操作名（`Operation.GetName()`）——按名字找回操作，**不存类型**（避免存档绑死类名）。</summary>
        public string operationName = "";

        /// <summary>参数值，**显示单位**（km / 度 / 比值），按 `GetParams()` 的顺序。</summary>
        public double[] paramValues = new double[0];

        /// <summary>参数标签（**捕获时记下**）：让队列界面不必再去找回操作就能显示人话，
        /// 也让"旧计划"在操作改名后依然读得懂。</summary>
        public string[] paramLabels = new string[0];

        /// <summary>时间选择器状态：当前项下标 / 提前量（秒）/ 到达高度（米）。没有时间选择器时下标为 -1。</summary>
        public int timeRefIndex = -1;
        public double leadTime;
        public double circularizeAltitude;

        /// <summary>目标群组 id（-1 = 本操作不需要目标）。</summary>
        public int targetGroupId = -1;

        /// <summary>
        /// **绝对点火时刻**（tick）。活重解时用它把"哪一刻点火"钉死——相对口径（`X_FROM_NOW`）
        /// 每重解一次就会往后漂一次，所以意图必须记绝对时刻，不能用"从现在起 N 秒"。
        /// </summary>
        public double absoluteTick = -1.0;

        /// <summary>从一条已配置好的操作里**捕获**意图（规划器界面就是这么做的）。</summary>
        public static PlanIntent Capture(Operation op, int targetGroupId = -1, double absoluteTick = -1.0)
        {
            IReadOnlyList<PlanParam> ps = op.GetParams();
            var values = new double[ps.Count];
            var labels = new string[ps.Count];
            for (int i = 0; i < ps.Count; i++)
            {
                values[i] = ps[i].Get();
                labels[i] = ps[i].Label;
            }

            TimeSelector? ts = op.GetTimeSelector();

            return new PlanIntent
            {
                operationName = op.GetName(),
                paramValues = values,
                paramLabels = labels,
                timeRefIndex = ts?._currentTimeRef ?? -1,
                leadTime = ts?.LeadTime.Val ?? 0.0,
                circularizeAltitude = ts?.CircularizeAltitude.Val ?? 0.0,
                targetGroupId = targetGroupId,
                absoluteTick = absoluteTick,
            };
        }

        /// <summary>**按名字找回操作并把参数写回去**。参数个数与当前代码不一致时明确拒绝（存档里的旧计划）。</summary>
        public Operation? Apply(out string reason)
        {
            reason = "";

            foreach (Operation candidate in Operation.GetAvailableOperations())
            {
                if (candidate.GetName() != operationName) continue;

                IReadOnlyList<PlanParam> ps = candidate.GetParams();
                if (ps.Count != paramValues.Length)
                {
                    reason = $"计划里的「{operationName}」有 {paramValues.Length} 个参数，当前代码是 {ps.Count} 个" +
                             "（计划是旧版本存的？）";
                    return null;
                }

                for (int i = 0; i < ps.Count; i++)
                    ps[i].Set(paramValues[i]);

                TimeSelector? ts = candidate.GetTimeSelector();
                if (ts != null)
                {
                    if (timeRefIndex >= 0 && timeRefIndex < ts.TimeRefNames.Length)
                        ts._currentTimeRef = timeRefIndex;
                    ts.LeadTime.Val = leadTime;
                    ts.CircularizeAltitude.Val = circularizeAltitude;
                }

                return candidate;
            }

            reason = $"找不到名为「{operationName}」的操作（这个操作被删了？）";
            return null;
        }

        /// <summary>
        /// **活重解**：按当前（活）轨道状态把这条意图重新算成节点 —— 这就是 MJ 节点语义的核心动作。
        /// 返回 `null` 时 <paramref name="reason" /> 给出可读原因（找不到操作 / 参数过期 / 算出不来）。
        /// </summary>
        public List<PlanNode>? ReSolve(OrbitView ov, double tick, ITargetView target, out string reason)
        {
            Operation? op = Apply(out reason);
            if (op == null) return null;

            // 把"哪一刻点火"钉成绝对时刻（见 `absoluteTick` 的注释）：
            // 记了绝对时刻、且本操作允许 `X_FROM_NOW` 时，用它覆盖相对口径。
            TimeSelector? ts = op.GetTimeSelector();
            if (ts != null && absoluteTick >= 0.0 && ts.Allows(TimeReference.X_FROM_NOW))
            {
                ts.TrySetTimeRef(TimeReference.X_FROM_NOW);
                ts.LeadTime.Val = absoluteTick - tick;
            }

            List<PlanNode>? nodes = op.MakeNodes(ov, tick, target);
            if (nodes == null)
                reason = op.GetErrorMessage() ?? "重解失败（无理由）";
            return nodes;
        }

        /// <summary>给人看的一句话（队列界面用）——**不需要找回操作**（标签在捕获时就记下了）。</summary>
        public string DescribeSelf()
        {
            var sb = new StringBuilder(operationName);
            for (int i = 0; i < paramValues.Length; i++)
            {
                string label = i < paramLabels.Length ? paramLabels[i] : "参数";
                sb.Append(i == 0 ? "（" : "，").Append(label).Append('=')
                  .Append(paramValues[i].ToString("0.###"));
            }
            if (paramValues.Length > 0) sb.Append('）');
            if (absoluteTick >= 0.0) sb.Append("｜点火 tick=").Append(absoluteTick.ToString("F0"));
            return sb.ToString();
        }
    }
}
