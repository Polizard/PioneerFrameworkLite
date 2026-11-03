using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// **按级号切组（纯函数）**（2026-10-27 用户裁定：不需要分离器部件）。
    /// `stageId = 0` 是上面级；**抛级 = 把当前最大的 stageId 整级切走**。
    /// 父组保留 `0..max−1`；没有多于一级（`max == 0`）时不可分离。
    /// </summary>
    public static class StagePartition
    {
        public static int MaxStage(IReadOnlyList<SpacePart> parts)
        {
            int max = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p != null && p.stageId > max) max = p.stageId;
            }
            return max;
        }

        /// <summary>把 `parts` 分成"保留"和"抛掉"两半；`max == 0` 时两半无意义（调用方先判）。</summary>
        public static void Split(IReadOnlyList<SpacePart> parts, int maxStage,
            out List<SpacePart> kept, out List<SpacePart> jettisoned)
        {
            kept = new List<SpacePart>(parts.Count);
            jettisoned = new List<SpacePart>();
            if (maxStage <= 0)   // 单级船：没有可抛的（调用方通常已先判，这里也兜住）
            {
                for (int i = 0; i < parts.Count; i++)
                    if (parts[i] != null) kept.Add(parts[i]);
                return;
            }
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null) continue;
                if (p.stageId == maxStage) jettisoned.Add(p);
                else kept.Add(p);
            }
        }
    }
}
